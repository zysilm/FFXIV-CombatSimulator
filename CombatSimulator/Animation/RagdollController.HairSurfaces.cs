// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Animation;

/// <summary>
/// Hair inputs that must be continuous in time and faithful to the visible body:
/// cached rendered body regions as contact surfaces (both animated and ragdoll hair),
/// per-step interpolation of animated inputs, and ground samples that follow the hair.
/// </summary>
public unsafe partial class RagdollController
{
    private sealed class HairSurfaceRegion
    {
        public PosedSurfaceRegion Region = null!;
        public int Bone = -1, RagdollIndex = -1;
        public Vector3 FrameOrigin, FramePreviousOrigin, StepOrigin, StepPreviousOrigin;
        public Quaternion FrameRotation, FramePreviousRotation, StepRotation, StepPreviousRotation;
        public bool FrameValid, FramePreviousValid, StepValid, StepPreviousValid;
    }

    private const float HairSurfaceReach = .1f;
    private const float HairGroundResampleDistance = .1f;
    private readonly List<HairSurfaceRegion> hairSurfaceRegions = new();
    private readonly HashSet<string> hairSurfaceNames = new(StringComparer.Ordinal);
    private FrozenMeshGeometryCache? hairSurfaceCache;
    private readonly HairContactCapsule[] animatedContactsNow = new HairContactCapsule[64];
    private readonly HairContactCapsule[] animatedContactsPrevious = new HairContactCapsule[64];
    private int animatedContactsNowCount, animatedContactsPreviousCount;
    private float animatedStepBlend = 1;

    /// <summary>The rendered surface already cached for this actor; never captures for hair.</summary>
    private FrozenMeshGeometryCache? CachedSurfaceFor(nint address)
    {
        if (address == 0) return null;
        var entity = ((GameObject*)address)->EntityId;
        if (frozenMeshCache != null && frozenMeshActor == address && frozenMeshEntity == entity) return frozenMeshCache;
        return weaponContactMeshes.TryGetValue(address, out var known) && known.Entity == entity ? known.Cache : null;
    }

    /// <summary>Bind cached regions to this skeleton (animated) or to ragdoll bodies. Rebinds when
    /// the cache is replaced or a region's coarse LOD becomes available.</summary>
    private void RefreshHairSurfaceRegions(in SkeletonAccess skel, nint address)
    {
        var cache = CachedSurfaceFor(address);
        bool rebuild = cache != hairSurfaceCache;
        if (!rebuild && cache != null)
            foreach (var region in hairSurfaceRegions)
                if (cache.TryGetCollisionGeometry(region.Region.Name, out var geometry) && geometry != region.Region.Geometry) { rebuild = true; break; }
        if (!rebuild) return;
        hairSurfaceCache = cache;
        hairSurfaceRegions.Clear(); hairSurfaceNames.Clear();
        if (cache == null) return;
        foreach (var name in cache.Names)
        {
            if (!cache.TryGetCollisionGeometry(name, out var geometry)) continue;
            var region = new HairSurfaceRegion { Region = new PosedSurfaceRegion(name, geometry) };
            if (animatedHair) region.Bone = boneService.ResolveBoneIndex(skel, name);
            else
                for (int i = 0; i < ragdollBones.Count; i++)
                    if (ragdollBones[i].Name == name) { region.RagdollIndex = i; break; }
            if (region.Bone < 0 && region.RagdollIndex < 0) continue;
            hairSurfaceRegions.Add(region); hairSurfaceNames.Add(name);
        }
        log.Info($"Hair body surfaces: {hairSurfaceRegions.Count} cached rendered regions (capsule fallback elsewhere).");
    }

    private void ClearHairSurfaceRegions()
    {
        hairSurfaceRegions.Clear(); hairSurfaceNames.Clear(); hairSurfaceCache = null;
        animatedContactsNowCount = animatedContactsPreviousCount = 0;
        animatedStepBlend = 1;
    }

    /// <summary>Animated hair, once per render frame: snapshot body inputs; steps blend toward them.</summary>
    private void CaptureAnimatedHairFrame(in SkeletonAccess skel, bool reset)
    {
        Array.Copy(animatedContactsNow, animatedContactsPrevious, animatedContactsNowCount);
        animatedContactsPreviousCount = reset ? 0 : animatedContactsNowCount;
        animatedContactsNowCount = CaptureAnimatedHairContacts(skel);
        foreach (var region in hairSurfaceRegions)
        {
            region.FramePreviousOrigin = region.FrameOrigin; region.FramePreviousRotation = region.FrameRotation;
            region.FramePreviousValid = region.FrameValid && !reset;
            region.FrameValid = false;
            if (region.Bone < 0 || region.Bone >= skel.Pose->ModelPose.Length) continue;
            ref var mt = ref skel.Pose->ModelPose.Data[region.Bone];
            region.FrameOrigin = ModelToWorld(new(mt.Translation.X, mt.Translation.Y, mt.Translation.Z));
            region.FrameRotation = ModelRotToWorld(new(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
            region.FrameValid = FlexibleHairSolver.Finite(region.FrameOrigin) && FlexibleHairSolver.Finite(region.FrameRotation);
        }
    }

    /// <summary>Per fixed step: capsules (animated) blended to the step time; region step poses.</summary>
    private int PrepareHairStepContacts()
    {
        int count = 0;
        if (animatedHair)
        {
            bool blend = animatedContactsPreviousCount == animatedContactsNowCount;
            for (int j = 0; j < animatedContactsNowCount; j++)
            {
                var now = animatedContactsNow[j];
                hairContacts[count++] = blend
                    ? now with
                    {
                        A = Vector3.Lerp(animatedContactsPrevious[j].A, now.A, animatedStepBlend),
                        B = Vector3.Lerp(animatedContactsPrevious[j].B, now.B, animatedStepBlend),
                    }
                    : now;
            }
        }
        else
            for (int i = 0; i < ragdollBones.Count && count < hairContacts.Length; i++)
            {
                var rb = ragdollBones[i];
                if (hairSurfaceNames.Contains(rb.Name) || rb.ColliderShape == RagdollColliderShape.Mesh) continue;
                if (!IsHairContactBone(rb.Name)) continue;
                var body = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
                var edge = Vector3.Transform(Vector3.UnitY * rb.CapsuleHalfLength, body.Pose.Orientation);
                hairContacts[count++] = new(body.Pose.Position - edge, body.Pose.Position + edge, rb.CapsuleRadius, rb.Name == "j_kao");
            }
        foreach (var region in hairSurfaceRegions)
        {
            region.StepPreviousOrigin = region.StepOrigin; region.StepPreviousRotation = region.StepRotation;
            region.StepPreviousValid = region.StepValid;
            region.StepValid = false;
            if (animatedHair)
            {
                if (!region.FrameValid) continue;
                region.StepOrigin = region.FramePreviousValid ? Vector3.Lerp(region.FramePreviousOrigin, region.FrameOrigin, animatedStepBlend) : region.FrameOrigin;
                region.StepRotation = region.FramePreviousValid ? Quaternion.Slerp(region.FramePreviousRotation, region.FrameRotation, animatedStepBlend) : region.FrameRotation;
                region.StepValid = true;
            }
            else if (region.RagdollIndex < ragdollBones.Count && simulation != null)
            {
                GetBonePoseFromBody(ragdollBones[region.RagdollIndex], out region.StepOrigin, out region.StepRotation, out _, out _);
                region.StepValid = FlexibleHairSolver.Finite(region.StepOrigin) && FlexibleHairSolver.Finite(region.StepRotation);
            }
        }
        return count;
    }

    private static bool IsHairContactBone(string name) =>
        name == "j_kao" || name == "j_kubi" || name == "j_kosi" ||
        name.StartsWith("j_sebo_", StringComparison.Ordinal) || name.StartsWith("j_ude_", StringComparison.Ordinal);

    /// <summary>Deepest/nearest rendered surface under each guide point and segment midpoint.</summary>
    private void UpdateHairBodySurface(HairRigChain chain, Vector3 head, Quaternion rotation)
    {
        Array.Clear(chain.BodySurface); Array.Clear(chain.SegmentSurface);
        if (hairSurfaceRegions.Count == 0) return;
        var solver = chain.Solver;
        for (int i = 0; i < solver.Count; i++)
        {
            int p = solver.Parents[i]; if (p < 0) continue;
            chain.BodySurface[i] = HairSurfacePlane(solver.Positions[i], head + Vector3.Transform(solver.Rest[i], rotation));
            chain.SegmentSurface[i] = HairSurfacePlane((solver.Positions[i] + solver.Positions[p]) * .5f,
                head + Vector3.Transform((solver.Rest[i] + solver.Rest[p]) * .5f, rotation));
        }
    }

    private HairContactPlane HairSurfacePlane(Vector3 point, Vector3 restPoint)
    {
        HairSurfaceRegion? best = null; float bestDistance = float.MaxValue;
        Vector3 bestPoint = default, bestNormal = default;
        foreach (var region in hairSurfaceRegions)
        {
            if (!region.StepValid ||
                !region.Region.TryContact(point, region.StepOrigin, region.StepRotation, HairSurfaceReach, out var hit, out var normal, out var signed) ||
                signed >= bestDistance) continue;
            best = region; bestDistance = signed; bestPoint = hit; bestNormal = normal;
        }
        if (best == null) return default;
        // Preserve authored scalp overlap rather than ejecting mod hair roots.
        if (best.Region.Name == "j_kao")
        {
            var restDepth = Vector3.Dot(restPoint - bestPoint, bestNormal);
            if (restDepth < 0f) bestPoint += bestNormal * restDepth;
        }
        var velocity = Vector3.Zero;
        if (best.StepPreviousValid)
        {
            var local = Vector3.Transform(bestPoint - best.StepOrigin, Quaternion.Conjugate(best.StepRotation));
            var before = best.StepPreviousOrigin + Vector3.Transform(local, best.StepPreviousRotation);
            velocity = (bestPoint - before) / FixedTimestep;
            float speed = velocity.Length();
            if (!float.IsFinite(speed)) velocity = Vector3.Zero;
            else if (speed > 8f) velocity *= 8f / speed;
        }
        return new HairContactPlane(true, bestPoint, bestNormal, velocity);
    }

    /// <summary>
    /// Up to <paramref name="budget"/> guide points to re-sample: first any that moved more than
    /// 10 cm since their last sample (so support follows falling/sliding hair), then round-robin.
    /// </summary>
    private int PickHairGroundPoints(Span<(int Chain, int Point)> picks, int budget, ref int cursor)
    {
        int total = 0; foreach (var c in hairRigChains) total += c.Solver.Count;
        if (total == 0) return 0;
        budget = Math.Min(budget, picks.Length);
        int count = 0;
        for (int scanned = 0; scanned < total && count < budget; scanned++)
        {
            int index = (cursor + scanned) % total;
            if (Locate(index, out var chain, out var point) &&
                (!hairRigChains[chain].GroundQueried[point] ||
                 Vector3.DistanceSquared(hairRigChains[chain].Solver.Positions[point], hairRigChains[chain].GroundQueryAt[point]) >
                 HairGroundResampleDistance * HairGroundResampleDistance))
                picks[count++] = (chain, point);
        }
        for (int step = 0; step < total && count < budget; step++)
        {
            int index = cursor; cursor = (cursor + 1) % total;
            if (Locate(index, out var chain, out var point)) picks[count++] = (chain, point);
        }
        return count;

        bool Locate(int index, out int chain, out int point)
        {
            for (chain = 0; chain < hairRigChains.Count; chain++)
            {
                var solver = hairRigChains[chain].Solver;
                if (index < solver.Count) { point = index; return solver.Parents[index] >= 0; }
                index -= solver.Count;
            }
            point = -1; return false;
        }
    }

    private static void RecordHairGround(HairRigChain chain, int point, HairContactPlane plane)
    {
        chain.Ground[point] = plane;
        chain.GroundQueryAt[point] = chain.Solver.Positions[point];
        chain.GroundQueried[point] = true;
    }
}
