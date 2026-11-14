// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.SurfaceProfiles;
using HkaPose = FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using CombatSimulator.Core;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.Recording;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private sealed class HairRigChain
    {
        public int PartialSkeletonIndex;
        public nint PoseIdentity, SkeletonIdentity;
        public FlexibleHairSolver Solver = null!;
        public int[] BoneIndices = Array.Empty<int>(), PointIndices = Array.Empty<int>(), ChildPoints = Array.Empty<int>();
        public Quaternion[] RestRotations = Array.Empty<Quaternion>(), RenderFrames = Array.Empty<Quaternion>();
        public Vector3[] RenderPositions = Array.Empty<Vector3>();
        public HairContactPlane[] Ground = Array.Empty<HairContactPlane>();
        public HairContactPlane[] BodySurface = Array.Empty<HairContactPlane>();
        public HairContactPlane[] SegmentSurface = Array.Empty<HairContactPlane>();
        public Vector3[] GroundQueryAt = Array.Empty<Vector3>();
        public bool[] GroundQueried = Array.Empty<bool>();
    }
    private readonly List<HairRigChain> hairRigChains = new();
    private readonly HairContactCapsule[] hairContacts = new HairContactCapsule[64];
    private bool hairRigActive;
    private int hairKaoRagdollBodyIndex = -1, hairGroundCursor;
    public bool RunAnimatedHair { get; set; }
    private bool animatedHair;
    private nint animatedHairDraw;
    private ulong animatedHairObject;
    private long animatedHairTimestamp;
    private float animatedHairAccumulator;
    private Vector3 animatedHead, animatedFrameHead, animatedPreviousFrameHead;
    private Quaternion animatedHeadRotation, animatedFrameHeadRotation, animatedPreviousFrameHeadRotation;
    private readonly object animatedHairGate = new();
    private int animatedGroundCursor;
    private const int MaxHairRigBones = 256;
    public bool HairRigActive => hairRigActive;
    private bool HairNeedsStep
    {
        get { foreach (var c in hairRigChains) if (c.Solver.Awake) return true; return false; }
    }

    private bool TryGetHairHead(out Vector3 position, out Quaternion rotation, out Vector3 linear, out Vector3 angular)
    {
        position = linear = angular = default; rotation = Quaternion.Identity;
        if (animatedHair)
        {
            position = animatedHead; rotation = animatedHeadRotation;
            return FlexibleHairSolver.Finite(position) && FlexibleHairSolver.Finite(rotation);
        }
        if (simulation == null || hairKaoRagdollBodyIndex < 0 || hairKaoRagdollBodyIndex >= ragdollBones.Count) return false;
        var rb = ragdollBones[hairKaoRagdollBodyIndex];
        var head = simulation.Bodies.GetBodyReference(rb.BodyHandle);
        position = head.Pose.Position - Vector3.Transform(rb.ShapeCenterOffset, head.Pose.Orientation) -
            Vector3.Transform(Vector3.UnitY * rb.SegmentHalfLength, head.Pose.Orientation);
        rotation = Quaternion.Normalize(head.Pose.Orientation * rb.CapsuleToBoneOffset);
        angular = head.Velocity.Angular;
        linear = head.Velocity.Linear + Vector3.Cross(angular, position - head.Pose.Position);
        return FlexibleHairSolver.Finite(position) && FlexibleHairSolver.Finite(rotation);
    }

    private void BuildHairRig(ISkeletonPose skel)
    {
        var meshBinding = meshes.HairMesh;
        if (rec != null) RecHairMesh(meshBinding);
        hairRigChains.Clear(); hairRigActive = false; hairGroundCursor = 0;
        ClearHairSurfaceRegions();
        if (!animatedHair)
        {
            if (simulation == null) return;
            for (int i = 0; i < ragdollBones.Count; i++)
                if (ragdollBones[i].Name == Bones.Head) { hairKaoRagdollBodyIndex = i; break; }
        }
        if (!TryGetHairHead(out var head, out var headRot, out var linear, out var angular)) return;
        if (!skel.HasRoot) return;
        int totalBones = 0;
        for (int ps = 1; ps < skel.PartCount && totalBones < MaxHairRigBones; ps++)
        {
            if (!skel.PartAvailable(ps) || !IsHairPartial(skel, ps)) continue;
            if (!TryHairPoseAttachment(skel, ps, out var poseAttachment, out _, out var attachmentRotation)) continue;
            var pose = skel.PartModelPose(ps);
            int count = Math.Min(2048, Math.Min(skel.PartModelPoseLength(ps), Math.Min(skel.PartBoneNameCount(ps), skel.PartParentCount(ps))));
            var marked = new bool[count];
            for (int i = 1; i < count; i++) marked[i] = IsHairBoneName(skel.PartBoneName(ps, i));
            // Mod skeletons need not have sorted parent indices.
            for (int pass = 0; pass < count; pass++)
            {
                bool changed = false;
                for (int i = 1; i < count; i++)
                {
                    int p = skel.PartParent(ps, i);
                    if (!marked[i] && p > 0 && p < count && marked[p]) { marked[i] = true; changed = true; }
                }
                if (!changed) break;
            }
            var points = new List<Vector3>(); var parents = new List<int>();
            var boneIndices = new List<int>(); var pointIndices = new List<int>(); var restRotations = new List<Quaternion>();
            var map = new int[count]; Array.Fill(map, -1);
            var invHead = Quaternion.Inverse(headRot);
            for (int pass = 0; pass < count && totalBones < MaxHairRigBones; pass++)
            {
                bool added = false;
                for (int b = 1; b < count && totalBones < MaxHairRigBones; b++)
                {
                    if (!marked[b] || map[b] >= 0) continue;
                    int p = skel.PartParent(ps, b);
                    if (p < 0 || p >= count || (marked[p] && map[p] < 0)) continue;
                    ref var mt = ref pose[b];
                    var world = ModelToWorld(Vector3.Transform(new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z), poseAttachment));
                    var rotation = ModelRotToWorld(attachmentRotation * new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
                    if (!FlexibleHairSolver.Finite(world) || !FlexibleHairSolver.Finite(rotation)) continue;
                    map[b] = points.Count;
                    points.Add(Vector3.Transform(world - head, invHead)); parents.Add(marked[p] ? map[p] : -1);
                    boneIndices.Add(b); pointIndices.Add(map[b]); restRotations.Add(Quaternion.Normalize(invHead * rotation));
                    totalBones++; added = true;
                }
                if (!added) break;
            }
            if (boneIndices.Count == 0) continue;
            var referenceModels = BuildHairReferenceModels(skel, ps);
            var baseReferences = BuildHairReferenceModels(skel, 0);
            int connected = skel.PartConnectedBone(ps);
            int parentBone = skel.PartConnectedParentBone(ps);
            if (connected >= 0 && connected < referenceModels.Length && parentBone >= 0 && parentBone < baseReferences.Length &&
                Matrix4x4.Invert(referenceModels[connected], out var invConnection))
            {
                var attachment = invConnection * baseReferences[parentBone];
                for (int i = 0; i < referenceModels.Length; i++) referenceModels[i] *= attachment;
            }
            var skin = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            AddHairSkinMatrices(skel, 0, baseReferences, Matrix4x4.Identity, skin);
            AddHairSkinMatrices(skel, ps, referenceModels, poseAttachment, skin);
            var radii = new List<float>();
            for (int i = 0; i < points.Count; i++) radii.Add(0);
            var childPoints = new int[boneIndices.Count]; Array.Fill(childPoints, -1);
            for (int i = 0; i < boneIndices.Count; i++)
            {
                int point = pointIndices[i];
                for (int j = 0; j < boneIndices.Count; j++)
                    if (parents[pointIndices[j]] == point && Vector3.DistanceSquared(points[pointIndices[j]], points[point]) > 1e-8f)
                    { childPoints[i] = pointIndices[j]; break; }
                if (childPoints[i] >= 0) continue;
                // Virtual tips let leaf bones bend rather than stay rigid.
                int p = parents[point];
                var direction = p >= 0 ? points[point] - points[p] : Vector3.Transform(-Vector3.UnitY, restRotations[i]);
                float length = direction.Length();
                direction = length > 1e-5f ? direction / length : -Vector3.UnitY;
                var tip = points[point] + direction * Math.Clamp(length * .7f, .025f, .12f);
                float meshRadius = 0;
                int bone = boneIndices[i];
                string name = skel.PartBoneName(ps, bone) ?? string.Empty;
                if (meshBinding != null && meshBinding.Samples.TryGetValue(name, out var samples))
                {
                    var cloud = new Vector3[samples.Length];
                    int admitted = 0;
                    for (int j = 0; j < samples.Length; j++)
                        if (samples[j].TryPose(skin, out var posed))
                            cloud[admitted++] = Vector3.Transform(ModelToWorld(posed) - head, invHead);
                    if (admitted >= 4)
                    {
                        var envelope = HairMeshEnvelope.Fit(cloud.AsSpan(0, admitted), points[point], Vector3.Transform(-Vector3.UnitY, invHead));
                        tip = envelope.Tip; meshRadius = envelope.Radius;
                    }
                    log.Info($"Hair mesh guide: bone={name}, samples={admitted}/{samples.Length}, length={Vector3.Distance(tip, points[point]):F3}m, thickness={meshRadius:F3}m; full skin weights, rooted fit");
                }
                childPoints[i] = points.Count;
                points.Add(tip); parents.Add(point); radii.Add(meshRadius);
            }
            var solver = new FlexibleHairSolver(parents.ToArray(), points.ToArray(), RagdollTuning.HairSegmentMass, head, headRot, linear, angular);
            radii.CopyTo(solver.ContactRadii);
            hairRigChains.Add(new HairRigChain
            {
                PartialSkeletonIndex = ps, PoseIdentity = skel.PartPoseIdentity(ps), SkeletonIdentity = skel.PartIdentity(ps),
                Solver = solver, BoneIndices = boneIndices.ToArray(), PointIndices = pointIndices.ToArray(), ChildPoints = childPoints,
                RestRotations = restRotations.ToArray(), RenderFrames = new Quaternion[solver.Count],
                RenderPositions = new Vector3[solver.Count], Ground = new HairContactPlane[solver.Count],
                BodySurface = new HairContactPlane[solver.Count], SegmentSurface = new HairContactPlane[solver.Count],
                GroundQueryAt = new Vector3[solver.Count], GroundQueried = new bool[solver.Count],
            });
        }
        hairRigActive = hairRigChains.Count > 0;
        if (hairRigActive)
            RefreshHairSurfaceRegions(skel, animatedHair ? actors.LocalPlayer : targetCharacterAddress);
        if (hairRigActive) log.Info($"HairRig: flexible guides built, partials={hairRigChains.Count}, drivenBones={totalBones}; one-way contacts, persistent rest curvature.");
        else log.Info("HairRig: no supported hair guides; original hair retained.");
    }

    private struct HairGroundRay : IRayHitHandler
    {
        public HairContactPlane Result;
        public bool AllowTest(CollidableReference c) => c.Mobility == CollidableMobility.Static;
        public bool AllowTest(CollidableReference c, int child) => AllowTest(c);
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference c, int child)
        {
            if (!float.IsFinite(t) || t < 0 || !FlexibleHairSolver.Finite(normal) || normal.LengthSquared() < 1e-8f) return;
            var n = Vector3.Normalize(normal); if (n.Y < 0) n = -n;
            if (n.Y < .2f) return;
            maximumT = t; Result = new(true, ray.Origin + ray.Direction * t, n);
        }
    }

    private void StepHairRig()
    {
        if (!hairRigActive || !TryGetHairHead(out var head, out var rotation, out _, out _)) return;
        if (!animatedHair) RefreshHairSurfaceRegions(null, targetCharacterAddress);
        int contactCount = PrepareHairStepContacts();
        if (!animatedHair)
        {
            // Fixed cap, using the existing BEPU static terrain. No native game
            // raycasts, MDL reads or new colliders in the update path. Animated
            // terrain sampling is supplied by the framework callback instead.
            Span<(int Chain, int Point)> picks = stackalloc (int, int)[16];
            int picked = PickHairGroundPoints(picks, 16, ref hairGroundCursor);
            for (int k = 0; k < picked; k++)
            {
                var c = hairRigChains[picks[k].Chain]; int index = picks[k].Point;
                var ray = new HairGroundRay();
                simulation!.RayCast(c.Solver.Positions[index] + Vector3.UnitY * .35f, -Vector3.UnitY, .7f, ref ray);
                RecordHairGround(c, index, ray.Result);
            }
        }
        // Ragdoll hair falls with the body's gravity; animated hair with the world's.
        float gravity = animatedHair ? 9.81f : RagdollTuning.Gravity;
        foreach (var c in hairRigChains)
        {
            UpdateHairBodySurface(c, head, rotation);
            c.Solver.Step(FixedTimestep, head, rotation, RagdollTuning.HairBendCompliance,
                RagdollTuning.HairDamping, c.Ground, hairContacts.AsSpan(0, contactCount),
                RagdollTuning.HairThickness, c.BodySurface,
                RagdollTuning.HairSwingDamping, gravity, c.SegmentSurface);
        }
    }

    private void ReadbackHairRig(ISkeletonPose skel)
    {
        if (!hairRigActive) return;
        if (!skel.HasRoot) return;
        float alpha = animatedHair ? Math.Clamp(animatedHairAccumulator / FixedTimestep, 0, 1) :
            hasPrevPhysicsState ? Math.Clamp(physicsAccumulator / FixedTimestep, 0, 1) : 1;
        foreach (var c in hairRigChains)
        {
            if (c.PartialSkeletonIndex >= skel.PartCount) continue;
            var ps = c.PartialSkeletonIndex;
            var poseIdentity = skel.PartPoseIdentity(ps);
            if (poseIdentity == 0 || poseIdentity != c.PoseIdentity || skel.PartIdentity(ps) != c.SkeletonIdentity || skel.PartModelPoseLength(ps) == 0) continue;
            if (!TryHairPoseAttachment(skel, ps, out var attachment, out var inverseAttachment, out var attachmentRotation)) continue;
            var pose = skel.PartModelPose(ps);
            ref var root = ref pose[0];
            var renderedHead = ModelToWorld(Vector3.Transform(new Vector3(root.Translation.X, root.Translation.Y, root.Translation.Z), attachment));
            var renderedRotation = ModelRotToWorld(attachmentRotation * new Quaternion(root.Rotation.X, root.Rotation.Y, root.Rotation.Z, root.Rotation.W));
            if (!FlexibleHairSolver.Finite(renderedHead) || !FlexibleHairSolver.Finite(renderedRotation)) continue;
            var interpolatedHead = c.Solver.InterpolatedHead(alpha);
            var delta = renderedRotation * Quaternion.Inverse(c.Solver.InterpolatedHeadRotation(alpha));
            for (int i = 0; i < c.Solver.Count; i++)
            {
                c.RenderPositions[i] = renderedHead + Vector3.Transform(c.Solver.InterpolatedPosition(i, alpha) - interpolatedHead, delta);
                int p = c.Solver.Parents[i];
                c.RenderFrames[i] = p < 0 ? renderedRotation :
                    FlexibleHairSolver.FromTo(Vector3.Transform(c.Solver.Rest[i] - c.Solver.Rest[p], c.RenderFrames[p]),
                        c.RenderPositions[i] - c.RenderPositions[p]) * c.RenderFrames[p];
            }
            for (int b = 0; b < c.BoneIndices.Length; b++)
            {
                int bone = c.BoneIndices[b]; if (bone >= pose.Length) continue;
                int point = c.PointIndices[b], child = c.ChildPoints[b];
                var frame = c.RenderFrames[point];
                var swing = FlexibleHairSolver.FromTo(Vector3.Transform(c.Solver.Rest[child] - c.Solver.Rest[point], frame),
                    c.RenderPositions[child] - c.RenderPositions[point]);
                var worldRot = Quaternion.Normalize(swing * frame * c.RestRotations[b]);
                var modelPos = Vector3.Transform(WorldToModel(c.RenderPositions[point]), inverseAttachment);
                var modelRot = Quaternion.Normalize(Quaternion.Inverse(attachmentRotation) * WorldRotToModel(worldRot));
                if (!FlexibleHairSolver.Finite(modelPos) || !FlexibleHairSolver.Finite(modelRot)) continue;
                ref var mt = ref pose[bone];
                mt.Translation.X = modelPos.X; mt.Translation.Y = modelPos.Y; mt.Translation.Z = modelPos.Z;
                mt.Rotation.X = modelRot.X; mt.Rotation.Y = modelRot.Y; mt.Rotation.Z = modelRot.Z; mt.Rotation.W = modelRot.W;
            }
        }
    }

    private void RemoveHairRig()
    {
        lock (animatedHairGate)
        {
            RestoreAnimatedHairPose();
            hairRigChains.Clear(); hairRigActive = false; hairKaoRagdollBodyIndex = -1; hairGroundCursor = 0;
            ClearHairSurfaceRegions();
            animatedHair = false; animatedHairDraw = 0; animatedHairObject = 0;
            animatedHairTimestamp = 0; animatedHairAccumulator = 0;
        }
    }

    private void OnAnimatedHairFrame()
    {
        if (!RunAnimatedHair || !Monitor.TryEnter(animatedHairGate)) return;
        try { OnAnimatedHairFrameCore(); }
        finally { Monitor.Exit(animatedHairGate); }
    }

    private void OnAnimatedHairFrameCore()
    {
        if (!RunAnimatedHair) return;
        if (!settings.RagdollHairPhysics) { if (animatedHair) RemoveHairRig(); return; }
        var playerAddress = actors.LocalPlayer;
        if (playerAddress == 0) { if (animatedHair) RemoveHairRig(); return; }
        var skel = skeletons.TryGetSkeleton(playerAddress);
        if (skel == null) return;
        var playerObjectId = actors.GetGameObjectId(playerAddress);
        var hairMeshRebuild = meshes.TakeHairMeshRebuild();
        bool changed = !animatedHair || animatedHairDraw != skel.DrawIdentity || animatedHairObject != playerObjectId || hairMeshRebuild;
        if (!changed)
            foreach (var c in hairRigChains)
            {
                if (c.PartialSkeletonIndex >= skel.PartCount) { changed = true; break; }
                var poseIdentity = skel.PartPoseIdentity(c.PartialSkeletonIndex);
                if (poseIdentity != c.PoseIdentity || poseIdentity == 0 || skel.PartIdentity(c.PartialSkeletonIndex) != c.SkeletonIdentity) { changed = true; break; }
            }
        var skeletonRoot = skel.Root;
        skelWorldPos = skeletonRoot.Position;
        skelWorldRot = skeletonRoot.Rotation;
        if (!FlexibleHairSolver.Finite(skelWorldPos) || !FlexibleHairSolver.Finite(skelWorldRot)) return;
        skelWorldRot = Quaternion.Normalize(skelWorldRot); skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        skelWorldScale = GetCharacterScale(playerAddress, skel);
        int headBone = ResolveBoneIndex(skel, Bones.Head);
        var bodyPose = skel.ModelPose;
        if (headBone < 0 || headBone >= bodyPose.Length) return;
        ref var head = ref bodyPose[headBone];
        animatedPreviousFrameHead = animatedFrameHead; animatedPreviousFrameHeadRotation = animatedFrameHeadRotation;
        animatedFrameHead = ModelToWorld(new(head.Translation.X, head.Translation.Y, head.Translation.Z));
        animatedFrameHeadRotation = ModelRotToWorld(new(head.Rotation.X, head.Rotation.Y, head.Rotation.Z, head.Rotation.W));
        animatedHead = animatedFrameHead; animatedHeadRotation = animatedFrameHeadRotation;
        long now = clock.GetTimestamp(ClockSite.AnimatedHair);
        float dt = animatedHairTimestamp == 0 ? FixedTimestep : Math.Clamp((float)clock.GetElapsedTime(ClockSite.AnimatedHair, animatedHairTimestamp, now).TotalSeconds, 0, .1f);
        animatedHairTimestamp = now;
        if (changed)
        {
            RestoreAnimatedHairPose();
            animatedHair = true; animatedHairDraw = skel.DrawIdentity; animatedHairObject = playerObjectId;
            animatedHairAccumulator = 0;
            animatedPreviousFrameHead = animatedFrameHead; animatedPreviousFrameHeadRotation = animatedFrameHeadRotation;
            BuildHairRig(skel);
        }
        else RefreshHairSurfaceRegions(skel, playerAddress);
        // A teleport/redraw must reach the solver as one discontinuity it can carry, not be
        // spread over interpolated steps that each look like (very fast) ordinary motion.
        bool discontinuous = changed || Vector3.DistanceSquared(animatedPreviousFrameHead, animatedFrameHead) > 1f ||
            MathF.Abs(Quaternion.Dot(animatedPreviousFrameHeadRotation, animatedFrameHeadRotation)) < .7071f;
        if (discontinuous) { animatedPreviousFrameHead = animatedFrameHead; animatedPreviousFrameHeadRotation = animatedFrameHeadRotation; }
        CaptureAnimatedHairFrame(skel, discontinuous);
        animatedHairAccumulator += dt;
        for (int step = 0; animatedHairAccumulator >= FixedTimestep && step < 4; step++)
        {
            animatedHairAccumulator -= FixedTimestep;
            animatedStepBlend = HairInputTiming.StepBlend(animatedHairAccumulator, dt);
            animatedHead = Vector3.Lerp(animatedPreviousFrameHead, animatedFrameHead, animatedStepBlend);
            animatedHeadRotation = Quaternion.Slerp(animatedPreviousFrameHeadRotation, animatedFrameHeadRotation, animatedStepBlend);
            StepHairRig();
        }
        if (animatedHairAccumulator >= FixedTimestep) animatedHairAccumulator = 0;
        animatedStepBlend = 1;
        ReadbackHairRig(skel);
    }

    private void RestoreAnimatedHairPose()
    {
        if (!animatedHair) return;
        var playerAddress = actors.LocalPlayer;
        if (playerAddress == 0 || actors.GetGameObjectId(playerAddress) != animatedHairObject) return;
        var skel = skeletons.TryGetSkeleton(playerAddress);
        if (skel == null || skel.DrawIdentity != animatedHairDraw) return;
        foreach (var c in hairRigChains)
        {
            if (c.PartialSkeletonIndex >= skel.PartCount) continue;
            var poseIdentity = skel.PartPoseIdentity(c.PartialSkeletonIndex);
            if (poseIdentity == 0 || poseIdentity != c.PoseIdentity || skel.PartIdentity(c.PartialSkeletonIndex) != c.SkeletonIdentity) continue;
            // LocalPose remains the game's animation input. Rebuild ModelPose
            // from it on disable/unload; never leave the last guide pose held.
            skel.ResyncPartModelPose(c.PartialSkeletonIndex);
        }
    }

    private int CaptureAnimatedHairContacts(ISkeletonPose skel)
    {
        int count = 0;
        foreach (var def in AllBoneDefaults)
        {
            // Regions with a cached rendered surface use it instead of an approximate capsule.
            if (!IsHairContactBone(def.Name) || hairSurfaceNames.Contains(def.Name)) continue;
            int bone = ResolveBoneIndex(skel, def.Name);
            if (bone < 0 || bone >= skel.ModelPose.Length || count == animatedContactsNow.Length) continue;
            ref var mt = ref skel.ModelPose[bone];
            var origin = ModelToWorld(new(mt.Translation.X, mt.Translation.Y, mt.Translation.Z));
            var rotation = ModelRotToWorld(new(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
            float scale = MathF.Max(skelWorldScale.X, MathF.Max(skelWorldScale.Y, skelWorldScale.Z));
            var edge = Vector3.Transform(Vector3.UnitY * def.CapsuleHalfLength * scale, rotation);
            animatedContactsNow[count++] = new(origin, origin + edge * 2, def.CapsuleRadius * scale, def.Name == Bones.Head);
        }
        return count;
    }

    private void SampleAnimatedHairTerrain()
    {
        UpdateHairMeshMetadata();
        if (!RunAnimatedHair || !animatedHair || !settings.RagdollHairPhysics || !Monitor.TryEnter(animatedHairGate)) return;
        try
        {
            if (!animatedHair) return;
            // Bounded game collision reads belong to Framework, never the render
            // hook. Unsupported/missing terrain clears support instead of guessing.
            Span<(int Chain, int Point)> picks = stackalloc (int, int)[8];
            int picked = PickHairGroundPoints(picks, 8, ref animatedGroundCursor);
            for (int k = 0; k < picked; k++)
            {
                var c = hairRigChains[picks[k].Chain]; int index = picks[k].Point;
                var point = c.Solver.Positions[index];
                HairContactPlane plane = default;
                if (world.Raycast(RaycastSite.AnimatedHair, point + Vector3.UnitY * .35f, -Vector3.UnitY, .7f, out var hit))
                {
                    var normal = hit.Normal;
                    if (FlexibleHairSolver.Finite(normal) && normal.LengthSquared() > 1e-8f)
                    {
                        normal = Vector3.Normalize(normal); if (normal.Y < 0) normal = -normal;
                        if (normal.Y > .2f) plane = new(true, hit.Point, normal);
                    }
                }
                RecordHairGround(c, index, plane);
            }
        }
        finally { Monitor.Exit(animatedHairGate); }
    }

    private static Matrix4x4[] BuildHairReferenceModels(ISkeletonPose skel, int part)
    {
        var reference = skel.PartReferencePose(part);
        int count = Math.Min(2048, Math.Min(skel.PartReferenceCount(part), skel.PartParentCount(part)));
        var models = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            int parent = skel.PartParent(part, i);
            models[i] = QsToMatrix(reference[i]);
            if (parent >= 0 && parent < i) models[i] *= models[parent];
        }
        return models;
    }

    private static void AddHairSkinMatrices(ISkeletonPose skel, int part, Matrix4x4[] references,
        Matrix4x4 attachment, Dictionary<string, Matrix4x4> skin)
    {
        var pose = skel.PartModelPose(part);
        int count = Math.Min(references.Length, Math.Min(skel.PartModelPoseLength(part), skel.PartBoneNameCount(part)));
        for (int i = 0; i < count; i++)
        {
            string? name = skel.PartBoneName(part, i);
            if (string.IsNullOrEmpty(name) || !Matrix4x4.Invert(references[i], out var inverse)) continue;
            skin[name] = inverse * QsToMatrix(pose[i]) * attachment;
        }
    }

    private static bool TryHairPoseAttachment(ISkeletonPose skel, int partialIndex,
        out Matrix4x4 attachment, out Matrix4x4 inverse, out Quaternion rotation)
    {
        attachment = inverse = Matrix4x4.Identity; rotation = Quaternion.Identity;
        int connected = skel.PartConnectedBone(partialIndex), parent = skel.PartConnectedParentBone(partialIndex);
        if (connected < 0 || connected >= skel.PartModelPoseLength(partialIndex) || parent < 0 || parent >= skel.BoneCount) return false;
        if (!Matrix4x4.Invert(QsToMatrix(skel.PartModelPose(partialIndex)[connected]), out var invConnection)) return false;
        attachment = invConnection * QsToMatrix(skel.ModelPose[parent]);
        return Matrix4x4.Invert(attachment, out inverse) && Matrix4x4.Decompose(attachment, out _, out rotation, out _) && FlexibleHairSolver.Finite(rotation);
    }

    // Framework tick: the mesh source follows the local player's hair model (stage A2b moved the reading there).
    private void UpdateHairMeshMetadata()
    {
        if (!RunAnimatedHair || !settings.RagdollHairPhysics || !Monitor.TryEnter(animatedHairGate)) return;
        try { meshes.PollHairMesh(); }
        finally { Monitor.Exit(animatedHairGate); }
    }
    private static bool IsHairBoneName(string? name) => HumanoidSkeleton.HairBones.IsHairBone(name);
    private static bool IsHairPartial(ISkeletonPose skel, int part)
    {
        var bones = skel.PartBoneNameCount(part);
        if (bones < 2 || skel.PartBoneName(part, 0) != Bones.Head) return false;
        for (int i = 1; i < bones; i++) if (IsHairBoneName(skel.PartBoneName(part, i))) return true;
        return false;
    }
}
