// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;
using System.Threading;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using CombatSimulator.Animation.Hair;
using HkaPose = FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using CombatSimulator.Core;

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
    private Vector3 animatedHead;
    private Quaternion animatedHeadRotation;
    private readonly object animatedHairGate = new();
    private int animatedGroundCursor;
    private HairMeshBinding? hairMesh;
    private Task<HairMeshBinding?>? hairMeshLoad;
    private nint hairMeshResource;
    private uint hairMeshAttributes;
    private uint hairMeshShapeMask;
    private nint hairMeshResourceData;
    private bool hairMeshReadyRebuild;
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

    private void BuildHairRig(in SkeletonAccess skel)
    {
        var meshBinding = hairMesh;
        hairRigChains.Clear(); hairRigActive = false; hairGroundCursor = 0;
        if (!animatedHair)
        {
            if (simulation == null) return;
            for (int i = 0; i < ragdollBones.Count; i++)
                if (ragdollBones[i].Name == "j_kao") { hairKaoRagdollBodyIndex = i; break; }
        }
        if (!TryGetHairHead(out var head, out var headRot, out var linear, out var angular)) return;
        var skeleton = skel.CharBase->Skeleton; if (skeleton == null) return;
        int totalBones = 0;
        for (int ps = 1; ps < skeleton->PartialSkeletonCount && totalBones < MaxHairRigBones; ps++)
        {
            var pose = skeleton->PartialSkeletons[ps].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || !IsHairPartial(pose)) continue;
            if (!TryHairPoseAttachment(skel, ps, pose, out var poseAttachment, out _, out var attachmentRotation)) continue;
            int count = Math.Min(2048, Math.Min(pose->ModelPose.Length, Math.Min(pose->Skeleton->Bones.Length, pose->Skeleton->ParentIndices.Length)));
            var marked = new bool[count];
            for (int i = 1; i < count; i++) marked[i] = IsHairBoneName(pose->Skeleton->Bones[i].Name.String);
            // Mod skeletons need not have sorted parent indices.
            for (int pass = 0; pass < count; pass++)
            {
                bool changed = false;
                for (int i = 1; i < count; i++)
                {
                    int p = pose->Skeleton->ParentIndices[i];
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
                    int p = pose->Skeleton->ParentIndices[b];
                    if (p < 0 || p >= count || (marked[p] && map[p] < 0)) continue;
                    ref var mt = ref pose->ModelPose.Data[b];
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
            var referenceModels = BuildHairReferenceModels(pose);
            var baseReferences = BuildHairReferenceModels(skel.Pose);
            int connected = skeleton->PartialSkeletons[ps].ConnectedBoneIndex;
            int parentBone = skeleton->PartialSkeletons[ps].ConnectedParentBoneIndex;
            if (connected >= 0 && connected < referenceModels.Length && parentBone >= 0 && parentBone < baseReferences.Length &&
                Matrix4x4.Invert(referenceModels[connected], out var invConnection))
            {
                var attachment = invConnection * baseReferences[parentBone];
                for (int i = 0; i < referenceModels.Length; i++) referenceModels[i] *= attachment;
            }
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
                string name = pose->Skeleton->Bones[bone].Name.String ?? string.Empty;
                if (meshBinding != null && bone < referenceModels.Length && meshBinding.Samples.TryGetValue(name, out var samples) &&
                    Matrix4x4.Invert(referenceModels[bone], out var invBind))
                {
                    var cloud = new Vector3[samples.Length];
                    var current = QsToMatrix(pose->ModelPose.Data[bone]) * poseAttachment;
                    for (int j = 0; j < samples.Length; j++)
                        cloud[j] = Vector3.Transform(ModelToWorld(Vector3.Transform(samples[j], invBind * current)) - head, invHead);
                    var envelope = HairMeshEnvelope.Fit(cloud, points[point], Vector3.Transform(-Vector3.UnitY, invHead));
                    tip = envelope.Tip; meshRadius = envelope.Radius;
                    log.Info($"Hair mesh guide: bone={name}, samples={samples.Length}, length={Vector3.Distance(tip, points[point]):F3}m, radius={meshRadius:F3}m");
                }
                childPoints[i] = points.Count;
                points.Add(tip); parents.Add(point); radii.Add(meshRadius);
            }
            var solver = new FlexibleHairSolver(parents.ToArray(), points.ToArray(), config.RagdollHairRigSegmentMass, head, headRot, linear, angular);
            radii.CopyTo(solver.ContactRadii);
            hairRigChains.Add(new HairRigChain
            {
                PartialSkeletonIndex = ps, PoseIdentity = (nint)pose, SkeletonIdentity = (nint)pose->Skeleton,
                Solver = solver, BoneIndices = boneIndices.ToArray(), PointIndices = pointIndices.ToArray(), ChildPoints = childPoints,
                RestRotations = restRotations.ToArray(), RenderFrames = new Quaternion[solver.Count],
                RenderPositions = new Vector3[solver.Count], Ground = new HairContactPlane[solver.Count],
            });
        }
        hairRigActive = hairRigChains.Count > 0;
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
        int contactCount = 0;
        if (!animatedHair)
            for (int i = 0; i < ragdollBones.Count && contactCount < hairContacts.Length; i++)
            {
                var rb = ragdollBones[i];
                if (rb.Name != "j_kao" && rb.Name != "j_kubi" && rb.Name != "j_kosi" &&
                    !rb.Name.StartsWith("j_sebo_", StringComparison.Ordinal) && !rb.Name.StartsWith("j_ude_", StringComparison.Ordinal)) continue;
                var body = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
                var edge = Vector3.Transform(Vector3.UnitY * rb.CapsuleHalfLength, body.Pose.Orientation);
                hairContacts[contactCount++] = new(body.Pose.Position - edge, body.Pose.Position + edge, rb.CapsuleRadius, rb.Name == "j_kao");
            }
        else contactCount = CaptureAnimatedHairContacts();
        int totalPoints = 0; foreach (var c in hairRigChains) totalPoints += c.Solver.Count;
        // Fixed cap, using the existing BEPU static terrain. No native game
        // raycasts, MDL reads or new colliders in the update path.
        for (int query = 0; query < Math.Min(16, totalPoints); query++)
        {
            int index = hairGroundCursor;
            hairGroundCursor = (hairGroundCursor + 1) % totalPoints;
            foreach (var c in hairRigChains)
            {
                if (index >= c.Solver.Count) { index -= c.Solver.Count; continue; }
                if (c.Solver.Parents[index] < 0) break;
                var ray = new HairGroundRay();
                if (!animatedHair)
                {
                    simulation!.RayCast(c.Solver.Positions[index] + Vector3.UnitY * .35f, -Vector3.UnitY, .7f, ref ray);
                    c.Ground[index] = ray.Result;
                }
                // Animated terrain sampling is supplied by the framework callback,
                // never by a native game collision query in the render callback.
                break;
            }
        }
        foreach (var c in hairRigChains)
            c.Solver.Step(FixedTimestep, head, rotation, config.RagdollHairBendCompliance,
                config.RagdollHairDamping, c.Ground, hairContacts.AsSpan(0, contactCount),
                Math.Clamp(config.RagdollHairRigThickness, .001f, .02f));
    }

    private void ReadbackHairRig(in SkeletonAccess skel)
    {
        if (!hairRigActive) return;
        var skeleton = skel.CharBase->Skeleton; if (skeleton == null) return;
        float alpha = animatedHair ? Math.Clamp(animatedHairAccumulator / FixedTimestep, 0, 1) :
            hasPrevPhysicsState ? Math.Clamp(physicsAccumulator / FixedTimestep, 0, 1) : 1;
        foreach (var c in hairRigChains)
        {
            if (c.PartialSkeletonIndex >= skeleton->PartialSkeletonCount) continue;
            var pose = skeleton->PartialSkeletons[c.PartialSkeletonIndex].GetHavokPose(0);
            if (pose == null || (nint)pose != c.PoseIdentity || (nint)pose->Skeleton != c.SkeletonIdentity || pose->ModelPose.Length == 0) continue;
            if (!TryHairPoseAttachment(skel, c.PartialSkeletonIndex, pose, out var attachment, out var inverseAttachment, out var attachmentRotation)) continue;
            ref var root = ref pose->ModelPose.Data[0];
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
                int bone = c.BoneIndices[b]; if (bone >= pose->ModelPose.Length) continue;
                int point = c.PointIndices[b], child = c.ChildPoints[b];
                var frame = c.RenderFrames[point];
                var swing = FlexibleHairSolver.FromTo(Vector3.Transform(c.Solver.Rest[child] - c.Solver.Rest[point], frame),
                    c.RenderPositions[child] - c.RenderPositions[point]);
                var worldRot = Quaternion.Normalize(swing * frame * c.RestRotations[b]);
                var modelPos = Vector3.Transform(WorldToModel(c.RenderPositions[point]), inverseAttachment);
                var modelRot = Quaternion.Normalize(Quaternion.Inverse(attachmentRotation) * WorldRotToModel(worldRot));
                if (!FlexibleHairSolver.Finite(modelPos) || !FlexibleHairSolver.Finite(modelRot)) continue;
                ref var mt = ref pose->ModelPose.Data[bone];
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
        if (!config.RagdollHairPhysics) { if (animatedHair) RemoveHairRig(); return; }
        var player = Services.ObjectTable.LocalPlayer;
        if (player == null) { if (animatedHair) RemoveHairRig(); return; }
        var access = boneService.TryGetSkeleton(player.Address);
        if (access == null) return;
        var skel = access.Value;
        bool changed = !animatedHair || animatedHairDraw != (nint)skel.CharBase || animatedHairObject != player.GameObjectId || hairMeshReadyRebuild;
        hairMeshReadyRebuild = false;
        if (!changed)
            foreach (var c in hairRigChains)
            {
                if (c.PartialSkeletonIndex >= skel.CharBase->Skeleton->PartialSkeletonCount) { changed = true; break; }
                var pose = skel.CharBase->Skeleton->PartialSkeletons[c.PartialSkeletonIndex].GetHavokPose(0);
                if ((nint)pose != c.PoseIdentity || pose == null || (nint)pose->Skeleton != c.SkeletonIdentity) { changed = true; break; }
            }
        var skeleton = skel.CharBase->Skeleton;
        skelWorldPos = new(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        skelWorldRot = new(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y, skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);
        if (!FlexibleHairSolver.Finite(skelWorldPos) || !FlexibleHairSolver.Finite(skelWorldRot)) return;
        skelWorldRot = Quaternion.Normalize(skelWorldRot); skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        skelWorldScale = GetCharacterScale(player.Address, skel);
        int headBone = boneService.ResolveBoneIndex(skel, "j_kao");
        if (headBone < 0 || headBone >= skel.Pose->ModelPose.Length) return;
        ref var head = ref skel.Pose->ModelPose.Data[headBone];
        animatedHead = ModelToWorld(new(head.Translation.X, head.Translation.Y, head.Translation.Z));
        animatedHeadRotation = ModelRotToWorld(new(head.Rotation.X, head.Rotation.Y, head.Rotation.Z, head.Rotation.W));
        long now = Stopwatch.GetTimestamp();
        float dt = animatedHairTimestamp == 0 ? FixedTimestep : Math.Clamp((float)Stopwatch.GetElapsedTime(animatedHairTimestamp, now).TotalSeconds, 0, .1f);
        animatedHairTimestamp = now;
        if (changed)
        {
            RestoreAnimatedHairPose();
            animatedHair = true; animatedHairDraw = (nint)skel.CharBase; animatedHairObject = player.GameObjectId;
            animatedHairAccumulator = 0;
            BuildHairRig(skel);
        }
        animatedHairAccumulator += dt;
        for (int step = 0; animatedHairAccumulator >= FixedTimestep && step < 4; step++)
        { StepHairRig(); animatedHairAccumulator -= FixedTimestep; }
        if (animatedHairAccumulator >= FixedTimestep) animatedHairAccumulator = 0;
        ReadbackHairRig(skel);
    }

    private void RestoreAnimatedHairPose()
    {
        if (!animatedHair) return;
        var player = Services.ObjectTable.LocalPlayer;
        if (player == null || player.GameObjectId != animatedHairObject) return;
        var access = boneService.TryGetSkeleton(player.Address);
        if (access == null || (nint)access.Value.CharBase != animatedHairDraw) return;
        var skeleton = access.Value.CharBase->Skeleton;
        foreach (var c in hairRigChains)
        {
            if (c.PartialSkeletonIndex >= skeleton->PartialSkeletonCount) continue;
            var pose = skeleton->PartialSkeletons[c.PartialSkeletonIndex].GetHavokPose(0);
            if (pose == null || (nint)pose != c.PoseIdentity || (nint)pose->Skeleton != c.SkeletonIdentity) continue;
            // LocalPose remains the game's animation input. Rebuild ModelPose
            // from it on disable/unload; never leave the last guide pose held.
            pose->ModelInSync = 0; pose->SyncModelSpace();
        }
    }

    private int CaptureAnimatedHairContacts()
    {
        var player = Services.ObjectTable.LocalPlayer; if (player == null) return 0;
        var access = boneService.TryGetSkeleton(player.Address); if (access == null) return 0;
        var skel = access.Value; int count = 0;
        foreach (var def in AllBoneDefaults)
        {
            if (def.Name != "j_kao" && def.Name != "j_kubi" && def.Name != "j_kosi" &&
                !def.Name.StartsWith("j_sebo_", StringComparison.Ordinal) && !def.Name.StartsWith("j_ude_", StringComparison.Ordinal)) continue;
            int bone = boneService.ResolveBoneIndex(skel, def.Name);
            if (bone < 0 || bone >= skel.Pose->ModelPose.Length || count == hairContacts.Length) continue;
            ref var mt = ref skel.Pose->ModelPose.Data[bone];
            var origin = ModelToWorld(new(mt.Translation.X, mt.Translation.Y, mt.Translation.Z));
            var rotation = ModelRotToWorld(new(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
            float scale = MathF.Max(skelWorldScale.X, MathF.Max(skelWorldScale.Y, skelWorldScale.Z));
            var edge = Vector3.Transform(Vector3.UnitY * def.CapsuleHalfLength * scale, rotation);
            hairContacts[count++] = new(origin, origin + edge * 2, def.CapsuleRadius * scale, def.Name == "j_kao");
        }
        return count;
    }

    private void SampleAnimatedHairTerrain()
    {
        UpdateHairMeshMetadata();
        if (!RunAnimatedHair || !animatedHair || !config.RagdollHairPhysics || !Monitor.TryEnter(animatedHairGate)) return;
        try
        {
            if (!animatedHair) return;
            int count = 0; foreach (var c in hairRigChains) count += c.Solver.Count;
            if (count == 0) return;
            // Bounded game collision reads belong to Framework, never the render
            // hook. Unsupported/missing terrain clears support instead of guessing.
            for (int query = 0; query < Math.Min(8, count); query++)
            {
                int index = animatedGroundCursor % count; animatedGroundCursor = (index + 1) % count;
                foreach (var c in hairRigChains)
                {
                    if (index >= c.Solver.Count) { index -= c.Solver.Count; continue; }
                    if (c.Solver.Parents[index] < 0) break;
                    var point = c.Solver.Positions[index];
                    c.Ground[index] = default;
                    if (BGCollisionModule.RaycastMaterialFilter(point + Vector3.UnitY * .35f, -Vector3.UnitY, out var hit, .7f))
                    {
                        var normal = hit.Normal;
                        if (FlexibleHairSolver.Finite(normal) && normal.LengthSquared() > 1e-8f)
                        {
                            normal = Vector3.Normalize(normal); if (normal.Y < 0) normal = -normal;
                            if (normal.Y > .2f) c.Ground[index] = new(true, hit.Point, normal);
                        }
                    }
                    break;
                }
            }
        }
        finally { Monitor.Exit(animatedHairGate); }
    }

    private static Matrix4x4[] BuildHairReferenceModels(HkaPose* pose)
    {
        int count = Math.Min(2048, Math.Min(pose->Skeleton->ReferencePose.Length, pose->Skeleton->ParentIndices.Length));
        var models = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            int parent = pose->Skeleton->ParentIndices[i];
            models[i] = QsToMatrix(pose->Skeleton->ReferencePose.Data[i]);
            if (parent >= 0 && parent < i) models[i] *= models[parent];
        }
        return models;
    }

    private static bool TryHairPoseAttachment(SkeletonAccess skel, int partialIndex, HkaPose* pose,
        out Matrix4x4 attachment, out Matrix4x4 inverse, out Quaternion rotation)
    {
        attachment = inverse = Matrix4x4.Identity; rotation = Quaternion.Identity;
        var partial = skel.CharBase->Skeleton->PartialSkeletons[partialIndex];
        int connected = partial.ConnectedBoneIndex, parent = partial.ConnectedParentBoneIndex;
        if (connected < 0 || connected >= pose->ModelPose.Length || parent < 0 || parent >= skel.Pose->ModelPose.Length) return false;
        if (!Matrix4x4.Invert(QsToMatrix(pose->ModelPose.Data[connected]), out var invConnection)) return false;
        attachment = invConnection * QsToMatrix(skel.Pose->ModelPose.Data[parent]);
        return Matrix4x4.Invert(attachment, out inverse) && Matrix4x4.Decompose(attachment, out _, out rotation, out _) && FlexibleHairSolver.Finite(rotation);
    }

    private void UpdateHairMeshMetadata()
    {
        if (!RunAnimatedHair || !config.RagdollHairPhysics || !Monitor.TryEnter(animatedHairGate)) return;
        try
        {
            var player = Services.ObjectTable.LocalPlayer; if (player == null) return;
            var access = boneService.TryGetSkeleton(player.Address); if (access == null) return;
            var cb = access.Value.CharBase;
            if (cb->SlotCount <= 10 || cb->Models == null || cb->Models[10] == null || cb->Models[10]->ModelResourceHandle == null) return;
            var model = cb->Models[10]; var resource = model->ModelResourceHandle;
            if (hairMeshLoad != null)
            {
                if (!hairMeshLoad.IsCompleted) return;
                var result = hairMeshLoad.GetAwaiter().GetResult(); hairMeshLoad = null;
                if (hairMeshResource == (nint)resource && hairMeshResourceData == (nint)resource->ModelData &&
                    hairMeshAttributes == model->EnabledAttributeIndexMask && hairMeshShapeMask == model->EnabledShapeKeyIndexMask)
                {
                    hairMesh = result; hairMeshReadyRebuild = true;
                    log.Info($"Hair mesh binding: visibleVertices={result?.VisibleVertices ?? 0}, weightedRegions={result?.Samples.Count ?? 0}; native vertex deformation pending, guide envelopes only.");
                }
            }
            if (hairMeshResource == (nint)resource && hairMeshResourceData == (nint)resource->ModelData &&
                hairMeshAttributes == model->EnabledAttributeIndexMask && hairMeshShapeMask == model->EnabledShapeKeyIndexMask) return;
            hairMeshResource = (nint)resource; hairMeshAttributes = model->EnabledAttributeIndexMask;
            hairMeshResourceData = (nint)resource->ModelData; hairMeshShapeMask = model->EnabledShapeKeyIndexMask;
            hairMesh = null; hairMeshReadyRebuild = true;
            // Active shape replacements require a separate index snapshot; omit
            // mesh fitting rather than reading disabled replacement geometry.
            if (model->EnabledShapeKeyIndexMask != 0) return;
            var fileName = resource->FileName;
            if (fileName.Length == 0 || fileName.Length > 4096) return;
            string path = Encoding.UTF8.GetString(fileName.AsSpan());
            if (path.StartsWith('|')) { int end = path.IndexOf('|', 1); if (end >= 0) path = path[(end + 1)..]; }
            var gameData = Services.DataManager.GameData; uint attributes = hairMeshAttributes;
            hairMeshLoad = Task.Run(() =>
            {
                try
                {
                    byte[]? data;
                    if (Path.IsPathRooted(path))
                    {
                        var info = new FileInfo(path); if (!info.Exists || info.Length > 64 * 1024 * 1024) return null;
                        data = File.ReadAllBytes(path);
                    }
                    else data = gameData.GetFile(path)?.Data;
                    return data == null || data.Length > 64 * 1024 * 1024 ? null : HairMeshBinding.Decode(data, attributes);
                }
                catch { return null; }
            });
        }
        finally { Monitor.Exit(animatedHairGate); }
    }
    private static bool IsHairBoneName(string? name) => name != null &&
        (name.StartsWith("j_kami_", StringComparison.Ordinal) ||
         (name.StartsWith("j_ex_h", StringComparison.Ordinal) && name.Length > 6 && (char.IsDigit(name[6]) || name.Contains("_ke_", StringComparison.Ordinal))));
    private static bool IsHairPartial(HkaPose* pose)
    {
        var bones = pose->Skeleton->Bones;
        if (bones.Length < 2 || bones[0].Name.String != "j_kao") return false;
        for (int i = 1; i < bones.Length; i++) if (IsHairBoneName(bones[i].Name.String)) return true;
        return false;
    }
}
