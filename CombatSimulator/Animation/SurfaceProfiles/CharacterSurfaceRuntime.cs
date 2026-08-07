// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.SurfaceProfiles;

public readonly record struct CharacterSurfaceBoneSeed(
    string Name,
    string Role,
    float HalfLength,
    float RadiusX,
    float RadiusZ);

public readonly record struct CharacterSurfaceDebugBone(
    string Name,
    ReadOnlyMemory<Vector3> Vertices,
    ReadOnlyMemory<int> Indices);

public sealed class CharacterSurfaceRuntime
{
    private readonly Dictionary<string, CharacterSurfaceRuntimeBone> byName
        = new(StringComparer.Ordinal);
    private readonly List<CharacterSurfaceRuntimeBone> bones = new();

    public CharacterSurfaceRuntime(
        CharacterSurfaceProfile profile,
        CharacterSurfaceIdentity identity,
        IReadOnlyList<CharacterSurfaceBoneSeed> seeds)
    {
        Profile = profile;
        Identity = identity;
        foreach (var seed in seeds)
        {
            var bone = new CharacterSurfaceRuntimeBone(profile, seed);
            bones.Add(bone);
            byName[seed.Name] = bone;
        }
    }

    public CharacterSurfaceProfile Profile { get; }
    public CharacterSurfaceIdentity Identity { get; }
    public IReadOnlyList<CharacterSurfaceRuntimeBone> Bones => bones;

    public bool UpdateBonePose(string boneName, Vector3 position, Quaternion orientation)
    {
        if (!byName.TryGetValue(boneName, out var bone)) return false;
        bone.UpdatePose(position, orientation);
        return true;
    }

    public bool TryGetNearestSurface(
        string boneName,
        Vector3 worldPoint,
        CharacterSurfaceUsage usage,
        out CharacterSurfaceHit hit)
    {
        hit = default;
        return byName.TryGetValue(boneName, out var bone) &&
               bone.TryGetNearestSurface(worldPoint, usage, out hit);
    }

    public bool TryGetNearestSurface(
        Vector3 worldPoint,
        CharacterSurfaceUsage usage,
        float maxDistance,
        out CharacterSurfaceHit hit)
    {
        hit = default;
        var maxDistanceSq = maxDistance <= 0f ? float.MaxValue : maxDistance * maxDistance;
        var found = false;
        foreach (var bone in bones)
        {
            if (!bone.IsNear(worldPoint, maxDistance)) continue;
            if (!bone.TryGetNearestSurface(worldPoint, usage, out var candidate) ||
                candidate.DistanceSquared > maxDistanceSq)
                continue;
            if (!found || candidate.DistanceSquared < hit.DistanceSquared)
            {
                hit = candidate;
                found = true;
            }
        }
        return found;
    }

    public bool TryGetVerticalSupport(
        Vector3 sphereCenter,
        float sphereRadius,
        string? preferredBone,
        out float requiredCenterY,
        out string boneName)
    {
        requiredCenterY = float.MinValue;
        boneName = string.Empty;
        if (!string.IsNullOrEmpty(preferredBone) && byName.TryGetValue(preferredBone, out var preferred))
        {
            if (!preferred.TryGetVerticalSupport(sphereCenter, sphereRadius, out requiredCenterY))
                return false;
            boneName = preferred.Name;
            return true;
        }

        var found = false;
        foreach (var bone in bones)
        {
            if (!bone.TryGetVerticalSupport(sphereCenter, sphereRadius, out var candidate))
                continue;
            if (!found || candidate > requiredCenterY)
            {
                requiredCenterY = candidate;
                boneName = bone.Name;
                found = true;
            }
        }
        return found;
    }

    public IEnumerable<CharacterSurfaceDebugBone> GetDebugBones(CharacterSurfaceUsage usage)
    {
        foreach (var bone in bones)
            yield return bone.GetDebugBone(usage);
    }
}

public sealed class CharacterSurfaceRuntimeBone
{
    private const int RadialSegments = 8;
    private readonly CharacterSurfaceProfile profile;
    private readonly Vector3[] localVertices;
    private readonly int[] indices;
    private readonly Vector3[] worldPhysics;
    private readonly Vector3[] worldTraversal;
    private readonly Vector3[] worldGrab;
    private readonly Vector3[] worldGround;
    private Vector3 position;
    private Quaternion orientation = Quaternion.Identity;
    private Vector3 boundsMin;
    private Vector3 boundsMax;
    private bool poseValid;

    public CharacterSurfaceRuntimeBone(CharacterSurfaceProfile profile, CharacterSurfaceBoneSeed seed)
    {
        this.profile = profile;
        Name = seed.Name;
        Role = seed.Role;

        var rings = BuildRings(profile, seed);
        BuildMesh(rings, out localVertices, out indices);
        worldPhysics = new Vector3[localVertices.Length];
        worldTraversal = new Vector3[localVertices.Length];
        worldGrab = new Vector3[localVertices.Length];
        worldGround = new Vector3[localVertices.Length];
    }

    public string Name { get; }
    public string Role { get; }
    public ReadOnlySpan<Vector3> LocalVertices => localVertices;
    public ReadOnlySpan<int> Indices => indices;

    public void UpdatePose(Vector3 newPosition, Quaternion newOrientation)
    {
        position = newPosition;
        orientation = Quaternion.Normalize(newOrientation);
        FillWorldVertices(worldPhysics, profile.UsageScale(CharacterSurfaceUsage.Physics));
        FillWorldVertices(worldTraversal, profile.UsageScale(CharacterSurfaceUsage.Traversal));
        FillWorldVertices(worldGrab, profile.UsageScale(CharacterSurfaceUsage.Grab));
        FillWorldVertices(worldGround, profile.UsageScale(CharacterSurfaceUsage.Ground));

        boundsMin = new Vector3(float.MaxValue);
        boundsMax = new Vector3(float.MinValue);
        foreach (var vertex in worldPhysics)
        {
            boundsMin = Vector3.Min(boundsMin, vertex);
            boundsMax = Vector3.Max(boundsMax, vertex);
        }
        poseValid = true;
    }

    public bool IsNear(Vector3 point, float margin)
    {
        if (!poseValid) return false;
        margin = MathF.Max(0f, margin);
        return point.X >= boundsMin.X - margin && point.X <= boundsMax.X + margin &&
               point.Y >= boundsMin.Y - margin && point.Y <= boundsMax.Y + margin &&
               point.Z >= boundsMin.Z - margin && point.Z <= boundsMax.Z + margin;
    }

    public bool TryGetNearestSurface(
        Vector3 worldPoint,
        CharacterSurfaceUsage usage,
        out CharacterSurfaceHit hit)
    {
        hit = default;
        if (!poseValid) return false;
        var vertices = VerticesFor(usage);
        var bestDistanceSq = float.MaxValue;
        var bestPoint = Vector3.Zero;
        var bestNormal = Vector3.UnitY;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            var candidate = ClosestPointOnTriangle(worldPoint, a, b, c);
            var distanceSq = Vector3.DistanceSquared(worldPoint, candidate);
            if (distanceSq >= bestDistanceSq) continue;
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-10f) continue;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, worldPoint - candidate) < 0f)
                normal = -normal;
            bestDistanceSq = distanceSq;
            bestPoint = candidate;
            bestNormal = normal;
        }
        if (bestDistanceSq == float.MaxValue) return false;
        var localPoint = Vector3.Transform(bestPoint - position, Quaternion.Inverse(orientation));
        hit = new CharacterSurfaceHit(Name, bestPoint, bestNormal, localPoint, bestDistanceSq);
        return true;
    }

    public bool TryGetVerticalSupport(Vector3 sphereCenter, float sphereRadius, out float requiredCenterY)
    {
        requiredCenterY = float.MinValue;
        if (!poseValid || sphereRadius <= 0f ||
            sphereCenter.X < boundsMin.X - sphereRadius || sphereCenter.X > boundsMax.X + sphereRadius ||
            sphereCenter.Z < boundsMin.Z - sphereRadius || sphereCenter.Z > boundsMax.Z + sphereRadius)
            return false;

        var vertices = worldTraversal;
        var sampleRadius = sphereRadius * 0.55f;
        var diagonal = sampleRadius * 0.70710678f;
        Span<Vector2> offsets = stackalloc Vector2[9]
        {
            Vector2.Zero,
            new(sampleRadius, 0f), new(-sampleRadius, 0f),
            new(0f, sampleRadius), new(0f, -sampleRadius),
            new(diagonal, diagonal), new(-diagonal, diagonal),
            new(diagonal, -diagonal), new(-diagonal, -diagonal),
        };

        var rayTop = boundsMax.Y + sphereRadius + 0.05f;
        var rayLength = MathF.Max(0.1f, rayTop - boundsMin.Y + sphereRadius + 0.05f);
        var found = false;
        foreach (var offset in offsets)
        {
            var origin = new Vector3(sphereCenter.X + offset.X, rayTop, sphereCenter.Z + offset.Y);
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                var normalLength = normal.Length();
                if (normalLength < 1e-6f || MathF.Abs(normal.Y) / normalLength < 0.25f)
                    continue;
                if (!RayTriangle(origin, -Vector3.UnitY, a, b, c, out var distance) ||
                    distance < 0f || distance > rayLength)
                    continue;
                var horizontalDistanceSq = offset.LengthSquared();
                var sphereRise = MathF.Sqrt(MathF.Max(0f, sphereRadius * sphereRadius - horizontalDistanceSq));
                var candidate = origin.Y - distance + sphereRise;
                if (!found || candidate > requiredCenterY)
                {
                    requiredCenterY = candidate;
                    found = true;
                }
            }
        }
        return found;
    }

    public CharacterSurfaceDebugBone GetDebugBone(CharacterSurfaceUsage usage)
        => new(Name, VerticesFor(usage), indices);

    private Vector3[] VerticesFor(CharacterSurfaceUsage usage) => usage switch
    {
        CharacterSurfaceUsage.Physics => worldPhysics,
        CharacterSurfaceUsage.Traversal => worldTraversal,
        CharacterSurfaceUsage.Grab => worldGrab,
        CharacterSurfaceUsage.Ground => worldGround,
        _ => worldPhysics,
    };

    private void FillWorldVertices(Vector3[] destination, float radialScale)
    {
        radialScale = MathF.Max(0.01f, radialScale);
        for (var i = 0; i < localVertices.Length; i++)
        {
            var local = localVertices[i];
            local.X *= radialScale;
            local.Z *= radialScale;
            destination[i] = position + Vector3.Transform(local, orientation);
        }
    }

    private static List<CharacterSurfaceRing> BuildRings(
        CharacterSurfaceProfile profile,
        CharacterSurfaceBoneSeed seed)
    {
        if (profile.Bones.TryGetValue(seed.Name, out var boneOverride) && boneOverride.Rings.Count >= 2)
        {
            var explicitRings = new List<CharacterSurfaceRing>(boneOverride.Rings);
            explicitRings.Sort((a, b) => a.T.CompareTo(b.T));
            return explicitRings;
        }

        var scale = profile.ResolveRoleScale(seed.Role);
        var halfLength = MathF.Max(0.005f, seed.HalfLength * MathF.Max(0.1f, scale.Length));
        var overlap = halfLength * Math.Clamp(scale.EndOverlap, 0f, 0.25f);
        var rx = MathF.Max(0.003f, seed.RadiusX * MathF.Max(0.1f, scale.Width));
        var rz = MathF.Max(0.003f, seed.RadiusZ * MathF.Max(0.1f, scale.Depth));
        var ox = scale.OffsetX * seed.RadiusX;
        var oz = scale.OffsetZ * seed.RadiusZ;
        return new List<CharacterSurfaceRing>(3)
        {
            new(-halfLength - overlap, ox, oz, rx * scale.TaperStart, rz * scale.TaperStart),
            new(0f, ox, oz, rx * scale.TaperMiddle, rz * scale.TaperMiddle),
            new(halfLength + overlap, ox, oz, rx * scale.TaperEnd, rz * scale.TaperEnd),
        };
    }

    private static void BuildMesh(
        IReadOnlyList<CharacterSurfaceRing> rings,
        out Vector3[] vertices,
        out int[] indices)
    {
        var ringVertexCount = rings.Count * RadialSegments;
        vertices = new Vector3[ringVertexCount + 2];
        for (var ringIndex = 0; ringIndex < rings.Count; ringIndex++)
        {
            var ring = rings[ringIndex];
            for (var side = 0; side < RadialSegments; side++)
            {
                var angle = side * MathF.Tau / RadialSegments;
                vertices[ringIndex * RadialSegments + side] = new Vector3(
                    ring.OffsetX + MathF.Cos(angle) * ring.RadiusX,
                    ring.T,
                    ring.OffsetZ + MathF.Sin(angle) * ring.RadiusZ);
            }
        }
        var bottomCenter = ringVertexCount;
        var topCenter = ringVertexCount + 1;
        vertices[bottomCenter] = new Vector3(rings[0].OffsetX, rings[0].T, rings[0].OffsetZ);
        var last = rings[^1];
        vertices[topCenter] = new Vector3(last.OffsetX, last.T, last.OffsetZ);

        var triangleCount = (rings.Count - 1) * RadialSegments * 2 + RadialSegments * 2;
        indices = new int[triangleCount * 3];
        var cursor = 0;
        for (var ring = 0; ring < rings.Count - 1; ring++)
        {
            var lower = ring * RadialSegments;
            var upper = (ring + 1) * RadialSegments;
            for (var side = 0; side < RadialSegments; side++)
            {
                var next = (side + 1) % RadialSegments;
                indices[cursor++] = lower + side;
                indices[cursor++] = upper + side;
                indices[cursor++] = upper + next;
                indices[cursor++] = lower + side;
                indices[cursor++] = upper + next;
                indices[cursor++] = lower + next;
            }
        }
        for (var side = 0; side < RadialSegments; side++)
        {
            var next = (side + 1) % RadialSegments;
            indices[cursor++] = bottomCenter;
            indices[cursor++] = next;
            indices[cursor++] = side;
            var topBase = (rings.Count - 1) * RadialSegments;
            indices[cursor++] = topCenter;
            indices[cursor++] = topBase + side;
            indices[cursor++] = topBase + next;
        }
    }

    private static bool RayTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        distance = 0f;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-7f) return false;
        var inverse = 1f / determinant;
        var t = origin - a;
        var u = Vector3.Dot(t, p) * inverse;
        if (u < -0.0001f || u > 1.0001f) return false;
        var q = Vector3.Cross(t, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < -0.0001f || u + v > 1.0001f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f;
    }

    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        var bp = p - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + ac * (d2 / (d2 - d6));

        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        var denominator = 1f / (va + vb + vc);
        var v = vb * denominator;
        var w = vc * denominator;
        return a + ab * v + ac * w;
    }
}
