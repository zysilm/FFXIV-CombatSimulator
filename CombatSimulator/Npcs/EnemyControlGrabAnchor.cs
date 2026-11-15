// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace CombatSimulator.Npcs;

/// <summary>
/// A point on a character's surface where a grab holds, picked by clicking the model (docs/ENEMY_CONTROL_GRAB.md §3).
/// Stored in the frame of the bone that owns that patch of skin, so it follows the bone through any pose and scales
/// with the skeleton on another body. <see cref="Offset"/> is in model units: the actor's draw scale at pick time is
/// divided out (<see cref="PickScale"/> keeps it for the log), and multiplied back in by whatever actor it is resolved
/// on. Plain floats rather than a Vector3 so the saved configuration names no runtime type.
/// </summary>
[Serializable]
public sealed class EnemyControlGrabAnchor
{
    public string Bone { get; set; } = string.Empty;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
    public float NormalX { get; set; }
    public float NormalY { get; set; }
    public float NormalZ { get; set; }
    public float PickScale { get; set; } = 1f;

    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Offset
    {
        get => new(OffsetX, OffsetY, OffsetZ);
        set { OffsetX = value.X; OffsetY = value.Y; OffsetZ = value.Z; }
    }

    /// <summary>Outward surface normal at the point, in the bone's frame (unit length).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Normal
    {
        get => new(NormalX, NormalY, NormalZ);
        set { NormalX = value.X; NormalY = value.Y; NormalZ = value.Z; }
    }

    public EnemyControlGrabAnchor Clone() => (EnemyControlGrabAnchor)MemberwiseClone();

    /// <summary>Identity for de-duplication: the bone and the point to a tenth of a millimetre.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string Signature => string.Create(CultureInfo.InvariantCulture,
        $"{Bone.Trim()}@{OffsetX:F4},{OffsetY:F4},{OffsetZ:F4}");
}

/// <summary>Where a character's model space sits in the world: the render skeleton's position and rotation, and the
/// actor's draw scale (applied in model space, before the rotation — the same transform the rendered-geometry
/// snapshot multiplies its vertices through).</summary>
public readonly record struct GrabRootTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>A ray–mesh hit: the triangle (index of its first corner in the index list), the distance along the ray,
/// and the barycentric weights of its three corners.</summary>
public readonly record struct GrabMeshHit(int Triangle, float Distance, Vector3 Point, Vector3 Normal, Vector3 Barycentric);

/// <summary>The pure geometry of anchored grabs: no game memory, so the offline tests compile it as is.</summary>
public static class EnemyControlGrabAnchorMath
{
    public static Vector3 ModelToWorld(in GrabRootTransform root, Vector3 model)
        => root.Position + Vector3.Transform(model * root.Scale, root.Rotation);

    public static Vector3 WorldToModel(in GrabRootTransform root, Vector3 world)
        => Vector3.Transform(world - root.Position, Quaternion.Inverse(root.Rotation)) / root.Scale;

    // A normal goes through the inverse transpose: R S^-1 one way, S R^-1 the other.
    public static Vector3 NormalModelToWorld(in GrabRootTransform root, Vector3 normal)
        => SafeNormalize(Vector3.Transform(normal / root.Scale, root.Rotation));

    public static Vector3 NormalWorldToModel(in GrabRootTransform root, Vector3 normal)
        => SafeNormalize(Vector3.Transform(normal, Quaternion.Inverse(root.Rotation)) * root.Scale);

    /// <summary>A world point and normal into the frame of a bone whose model-space pose is given.</summary>
    public static (Vector3 Offset, Vector3 Normal) ToBoneLocal(in GrabRootTransform root, Vector3 boneModelPosition,
        Quaternion boneModelRotation, Vector3 worldPoint, Vector3 worldNormal)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(boneModelRotation));
        var model = WorldToModel(root, worldPoint);
        var offset = Vector3.Transform(model - boneModelPosition, inverse);
        var normal = SafeNormalize(Vector3.Transform(NormalWorldToModel(root, worldNormal), inverse));
        return (offset, normal);
    }

    /// <summary>The inverse of <see cref="ToBoneLocal"/>, on whichever pose and actor the bone is on now.</summary>
    public static (Vector3 Point, Vector3 Normal) ToWorld(in GrabRootTransform root, Vector3 boneModelPosition,
        Quaternion boneModelRotation, Vector3 offset, Vector3 normal)
    {
        var rotation = Quaternion.Normalize(boneModelRotation);
        var model = boneModelPosition + Vector3.Transform(offset, rotation);
        return (ModelToWorld(root, model), NormalModelToWorld(root, Vector3.Transform(normal, rotation)));
    }

    /// <summary>Möller–Trumbore, both faces. False when the ray misses or the hit is behind its origin.</summary>
    public static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance, out float u, out float v)
    {
        distance = u = v = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        var inv = 1f / det;
        var s = origin - a;
        u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        v = Vector3.Dot(direction, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(e2, q) * inv;
        return distance > 0f;
    }

    /// <summary>The nearest triangle the ray crosses. The normal is the face's, turned to face the ray's origin.</summary>
    public static bool TryPick(Vector3 origin, Vector3 direction, IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> triangles, out GrabMeshHit hit)
    {
        hit = default;
        if (direction.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(direction);
        var best = float.MaxValue;
        var found = false;
        for (var t = 0; t + 2 < triangles.Count; t += 3)
        {
            int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
            if ((uint)i0 >= (uint)positions.Count || (uint)i1 >= (uint)positions.Count || (uint)i2 >= (uint)positions.Count)
                continue;
            var a = positions[i0];
            var b = positions[i1];
            var c = positions[i2];
            if (!RayTriangle(origin, direction, a, b, c, out var d, out var u, out var v) || d >= best) continue;
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-20f) continue;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, direction) > 0f) normal = -normal;
            best = d;
            hit = new GrabMeshHit(t, d, origin + direction * d, normal, new Vector3(1f - u - v, u, v));
            found = true;
        }
        return found;
    }

    /// <summary>The bone that owns a point inside a triangle: each corner's skin weights, weighted by how close the
    /// point is to that corner, summed per bone; the largest wins (ties to the lower index, so it is stable).</summary>
    public static int OwningBone(Vector3 barycentric, ReadOnlySpan<(int Bone, float Weight)> corner0,
        ReadOnlySpan<(int Bone, float Weight)> corner1, ReadOnlySpan<(int Bone, float Weight)> corner2)
    {
        var totals = new Dictionary<int, float>();
        void Add(ReadOnlySpan<(int Bone, float Weight)> corner, float share)
        {
            foreach (var (bone, weight) in corner)
            {
                if (bone < 0 || !(weight > 0f)) continue;
                totals[bone] = totals.GetValueOrDefault(bone) + weight * share;
            }
        }
        Add(corner0, barycentric.X);
        Add(corner1, barycentric.Y);
        Add(corner2, barycentric.Z);
        var bestBone = -1;
        var bestWeight = 0f;
        foreach (var (bone, weight) in totals)
            if (weight > bestWeight || (weight == bestWeight && bestBone >= 0 && bone < bestBone))
            {
                bestBone = bone;
                bestWeight = weight;
            }
        return bestBone;
    }

    /// <summary>
    /// Moves a point along its normal onto a surface point found near it (the re-snap at grab start): only the
    /// normal component of the gap is taken, so the point keeps its place on the body and only its depth changes.
    /// Returns the signed move; zero (and the point unchanged) when the move would exceed <paramref name="maxMove"/>.
    /// </summary>
    public static float SnapAlongNormal(ref Vector3 point, Vector3 normal, Vector3 surfacePoint, float maxMove)
    {
        if (normal.LengthSquared() < 1e-12f) return 0f;
        normal = Vector3.Normalize(normal);
        var move = Vector3.Dot(surfacePoint - point, normal);
        if (!float.IsFinite(move) || MathF.Abs(move) > maxMove) return 0f;
        point += normal * move;
        return move;
    }

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared() > 1e-20f ? Vector3.Normalize(v) : Vector3.Zero;
}

/// <summary>The grab-pair list rules shared by the configuration, the profiles and the grab itself: one copy and
/// one de-duplication, so a pair with anchors is never told apart by bone names alone.</summary>
public static class EnemyControlGrabPairList
{
    /// <summary>Two pairs are the same hold when their bones and both anchors match.</summary>
    public static string Key(string npcBone, string playerBone, EnemyControlGrabAnchor? npcAnchor, EnemyControlGrabAnchor? playerAnchor)
        => $"{npcBone.Trim()}\u001f{playerBone.Trim()}\u001f{npcAnchor?.Signature}\u001f{playerAnchor?.Signature}";

    public static string Key(EnemyControlGrabPairConfig pair)
        => Key(pair.NpcBone, pair.PlayerBone, pair.NpcAnchor, pair.PlayerAnchor);

    public static EnemyControlGrabPairConfig Clone(EnemyControlGrabPairConfig pair) => new()
    {
        NpcBone = pair.NpcBone,
        PlayerBone = pair.PlayerBone,
        NpcAnchor = pair.NpcAnchor?.Clone(),
        PlayerAnchor = pair.PlayerAnchor?.Clone(),
    };

    /// <summary>Drops empty and repeated pairs and trims bone names, in place; with <paramref name="ensureOne"/> an
    /// emptied list gets the default pair back. True when anything changed.</summary>
    public static bool Deduplicate(List<EnemyControlGrabPairConfig> pairs, bool ensureOne)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair == null || string.IsNullOrWhiteSpace(pair.NpcBone) || string.IsNullOrWhiteSpace(pair.PlayerBone))
            {
                pairs.RemoveAt(i);
                i--;
                changed = true;
                continue;
            }

            if (!seen.Add(Key(pair)))
            {
                pairs.RemoveAt(i);
                i--;
                changed = true;
                continue;
            }

            var npcBone = pair.NpcBone.Trim();
            var playerBone = pair.PlayerBone.Trim();
            if (npcBone != pair.NpcBone || playerBone != pair.PlayerBone)
            {
                pair.NpcBone = npcBone;
                pair.PlayerBone = playerBone;
                changed = true;
            }
        }

        if (ensureOne && pairs.Count == 0)
        {
            pairs.Add(new EnemyControlGrabPairConfig());
            changed = true;
        }
        return changed;
    }

    /// <summary>Deep copies of <paramref name="source"/>, de-duplicated; never empty.</summary>
    public static List<EnemyControlGrabPairConfig> CopyForUse(IEnumerable<EnemyControlGrabPairConfig> source)
    {
        var copy = new List<EnemyControlGrabPairConfig>();
        foreach (var pair in source)
            if (pair != null) copy.Add(Clone(pair));
        Deduplicate(copy, ensureOne: true);
        return copy;
    }
}
