// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using AdvancedRagdoll;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Camera;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Npcs;

/// <summary>
/// Anchors on live characters: where a stored anchor is in the world right now, and the skeleton questions the grab
/// asks about its bone. Every read goes through the same transform the rendered-geometry snapshot uses (render
/// skeleton position and rotation, the actor's draw scale applied in model space), so a point resolves exactly where
/// it was picked. Framework/render thread only.
/// </summary>
internal static unsafe class GrabAnchorSkeleton
{
    public static bool TryReadRoot(nint actor, SkeletonAccess skel, out GrabRootTransform root)
    {
        root = default;
        var skeleton = skel.CharBase == null ? null : skel.CharBase->Skeleton;
        if (skeleton == null) return false;
        var t = skeleton->Transform;
        root = new GrabRootTransform(
            new Vector3(t.Position.X, t.Position.Y, t.Position.Z),
            Quaternion.Normalize(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W)),
            FfxivMeshSource.GetCharacterScale(actor, skel));
        return true;
    }

    public static bool TryBoneModel(SkeletonAccess skel, int bone, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        if (bone < 0 || bone >= skel.BoneCount || skel.Pose == null) return false;
        ref var model = ref skel.Pose->ModelPose.Data[bone];
        position = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
        rotation = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
        return float.IsFinite(position.X) && rotation.LengthSquared() > 1e-8f;
    }

    /// <summary>The anchor's point and outward normal on <paramref name="actor"/> as it is posed now.</summary>
    public static bool TryResolve(BoneTransformService boneService, nint actor, EnemyControlGrabAnchor anchor,
        out Vector3 point, out Vector3 normal)
    {
        point = normal = Vector3.Zero;
        var skelN = boneService.TryGetSkeleton(actor);
        if (skelN == null) return false;
        var skel = skelN.Value;
        var bone = boneService.ResolveBoneIndex(skel, anchor.Bone);
        if (!TryReadRoot(actor, skel, out var root) || !TryBoneModel(skel, bone, out var position, out var rotation))
            return false;
        (point, normal) = EnemyControlGrabAnchorMath.ToWorld(root, position, rotation, anchor.Offset, anchor.Normal);
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    /// <summary>The bone's origin with the draw scale applied, next to the unscaled reading the rest of the grab uses
    /// (BoneTransformService.GetBoneWorldPos), for the log.</summary>
    public static bool TryScaledBoneOrigin(BoneTransformService boneService, nint actor, string boneName,
        out Vector3 scaled, out Vector3 unscaled, out Vector3 scale)
    {
        scaled = unscaled = Vector3.Zero;
        scale = Vector3.One;
        var skelN = boneService.TryGetSkeleton(actor);
        if (skelN == null) return false;
        var skel = skelN.Value;
        var bone = boneService.ResolveBoneIndex(skel, boneName);
        if (!TryReadRoot(actor, skel, out var root) || !TryBoneModel(skel, bone, out var position, out _)) return false;
        scale = root.Scale;
        scaled = EnemyControlGrabAnchorMath.ModelToWorld(root, position);
        unscaled = root.Position + Vector3.Transform(position, root.Rotation);
        return true;
    }

    /// <summary>True when the bone, or any bone above it, is in <paramref name="cut"/> (a severed part).</summary>
    public static bool IsUnderAny(BoneTransformService boneService, nint actor, string boneName, IReadOnlyCollection<string>? cut)
    {
        if (cut == null || cut.Count == 0) return false;
        var skelN = boneService.TryGetSkeleton(actor);
        if (skelN == null) return false;
        var skel = skelN.Value;
        var set = new HashSet<string>(cut, StringComparer.Ordinal);
        var bone = boneService.ResolveBoneIndex(skel, boneName);
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        for (var guard = 0; bone >= 0 && bone < count && guard < 512; guard++)
        {
            var name = skel.HavokSkeleton->Bones[bone].Name.String;
            if (name != null && set.Contains(name)) return true;
            bone = skel.HavokSkeleton->ParentIndices[bone];
        }
        return false;
    }

    /// <summary>The bone itself if <paramref name="hasBody"/> says it has a body, else the nearest ancestor that does
    /// (the body a grab on it actually takes); the bone itself when nothing above it has one.</summary>
    public static string CarryingBone(BoneTransformService boneService, nint actor, string boneName, Func<string, bool> hasBody)
    {
        var skelN = boneService.TryGetSkeleton(actor);
        if (skelN == null) return boneName;
        var skel = skelN.Value;
        var bone = boneService.ResolveBoneIndex(skel, boneName);
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        for (var guard = 0; bone >= 0 && bone < count && guard < 512; guard++)
        {
            var name = skel.HavokSkeleton->Bones[bone].Name.String;
            if (!string.IsNullOrEmpty(name) && hasBody(name)) return name;
            bone = skel.HavokSkeleton->ParentIndices[bone];
        }
        return boneName;
    }
}

/// <summary>
/// Turns a click on a character into an anchor (docs/ENEMY_CONTROL_GRAB.md §3): one snapshot of the model as it is
/// drawn now (the same capture the mesh-volume ragdoll uses), the ray under the mouse, the nearest triangle it
/// crosses, the bone that owns that patch of skin, and the point in that bone's frame.
/// </summary>
public sealed unsafe class EnemyControlGrabAnchorPicker
{
    private readonly BoneTransformService boneService;
    private readonly IPluginLog log;
    private FfxivMeshSource? meshSource;

    public EnemyControlGrabAnchorPicker(BoneTransformService boneService, IPluginLog log)
    {
        this.boneService = boneService;
        this.log = log;
    }

    /// <summary>
    /// Picks the point of <paramref name="actor"/> under <paramref name="mouse"/>. False with a short reason (for the
    /// chat line) when there is no model to read or the click missed it.
    /// </summary>
    public bool TryPick(nint actor, Vector2 mouse, Vector2 displaySize, out EnemyControlGrabAnchor anchor, out string message)
    {
        anchor = new EnemyControlGrabAnchor();
        message = string.Empty;
        if (actor == nint.Zero) { message = "nothing to pick on"; return false; }

        var skelN = boneService.TryGetSkeleton(actor);
        if (skelN == null) { message = "the model is not drawn"; return false; }
        var skel = skelN.Value;

        if (!ScreenRay.TryGet(Core.Services.GameGui, mouse, displaySize, out var origin, out var direction, out var residual))
        { message = "could not read the camera"; return false; }

        meshSource ??= new FfxivMeshSource(boneService, new FfxivRagdollActor(null), new DalamudRagdollLog(log),
            TaskRagdollScheduler.Instance);
        var snapshot = meshSource.CaptureRenderedGeometry(actor, new FfxivSkeletonPose(actor, skel));
        var vertices = snapshot.Vertices;
        if (vertices.Length == 0 || snapshot.TriangleIndices.Length < 3)
        {
            message = "the model's mesh could not be read";
            log.Warning($"GrabAnchor: no mesh for 0x{actor:X}: {string.Join(" | ", snapshot.Diagnostics)}");
            return false;
        }

        var positions = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++) positions[i] = vertices[i].WorldPosition;
        if (!EnemyControlGrabAnchorMath.TryPick(origin, direction, positions, snapshot.TriangleIndices, out var hit))
        {
            message = "missed the model";
            log.Info($"GrabAnchor: pick missed 0x{actor:X} ({snapshot.TriangleIndices.Length / 3} triangles, ray residual {residual:F1}px)");
            return false;
        }

        var tri = snapshot.TriangleIndices;
        var bone = EnemyControlGrabAnchorMath.OwningBone(hit.Barycentric,
            Weights(vertices[tri[hit.Triangle]]), Weights(vertices[tri[hit.Triangle + 1]]), Weights(vertices[tri[hit.Triangle + 2]]));
        var boneName = bone >= 0 && bone < skel.BoneCount ? skel.HavokSkeleton->Bones[bone].Name.String : null;
        if (string.IsNullOrEmpty(boneName) ||
            !GrabAnchorSkeleton.TryReadRoot(actor, skel, out var root) ||
            !GrabAnchorSkeleton.TryBoneModel(skel, bone, out var bonePosition, out var boneRotation))
        { message = "the bone under the click could not be read"; return false; }

        var (offset, normal) = EnemyControlGrabAnchorMath.ToBoneLocal(root, bonePosition, boneRotation, hit.Point, hit.Normal);
        anchor = new EnemyControlGrabAnchor { Bone = boneName, Offset = offset, Normal = normal, PickScale = root.Scale.X };
        var (back, _) = EnemyControlGrabAnchorMath.ToWorld(root, bonePosition, boneRotation, offset, normal);
        log.Info($"GrabAnchor: picked '{boneName}' on 0x{actor:X} at ({hit.Point.X:F3},{hit.Point.Y:F3},{hit.Point.Z:F3}), " +
                 $"{offset.Length() * 1000f:F0} mm from the bone (model units), scale {root.Scale.X:F2}, hit {hit.Distance:F2} m along the ray, " +
                 $"ray residual {residual:F1}px, round trip {Vector3.Distance(back, hit.Point) * 1000f:F2} mm, " +
                 $"{tri.Length / 3} triangles");
        message = boneName;
        return true;
    }

    private static (int Bone, float Weight)[] Weights(RenderedGeometryVertex vertex)
    {
        var influences = vertex.Influences ?? Array.Empty<RenderedGeometryInfluence>();
        var result = new (int, float)[influences.Length];
        for (var i = 0; i < influences.Length; i++) result[i] = (influences[i].BoneIndex, influences[i].Weight);
        return result;
    }
}
