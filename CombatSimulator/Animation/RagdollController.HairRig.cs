using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using HkaPose = FFXIVClientStructs.Havok.Animation.Rig.hkaPose;

namespace CombatSimulator.Animation;

// BEPU rigid-body hair rig — an alternative to the legacy pendulum HairPhysicsSimulator.
//
// Rationale: the garment "tube" rig (BallSocket + relaxing SwingLimit + damping AngularMotor +
// fading pose-guide servo) already produces convincing hanging/settling cloth. Hair is the same
// problem with simpler topology (open chains, no ring seams), so we reuse the exact same rig
// primitives (TryCreate*/AddExternalRigJoint/Relax*/ApplyExternalRigPoseGuidance) instead of the
// kinematic pendulum, which never had real collision or inertia.
//
// The rig is built from the hair partial-skeleton bone tree, so it is name-/style-agnostic: any
// hairstyle (including mod hair) whose bones live in a hair partial gets a rig automatically.
//
// Anchoring: each hair partial's root frame (j_kao) is represented by a KINEMATIC body that tracks
// the head ragdoll body every substep (position + head-derived velocity). The dynamic strand bodies
// hang from it through joints, so the hair inherits the head's whip. Strand bodies collide with the
// corpse capsules and the ground (collideWithRagdoll) but not with each other (one shared
// self-collide group), matching how the garment tube avoids intra-rig jitter.
public unsafe partial class RagdollController
{
    private sealed class HairRigSegment
    {
        public int BoneIndex;                   // index within the hair partial's ModelPose
        public BodyHandle Body;
        public float SegmentHalfLength;         // half the bone->child distance (box local-Y half extent)
        public Quaternion CapsuleToBoneOffset;  // body.Orientation * offset = boneWorldRot
    }

    private sealed class HairRigChain
    {
        public int PartialSkeletonIndex;
        public ExternalRigHandle Rig = null!;
        public BodyHandle AnchorBody;           // kinematic, tracks j_kao
        public readonly List<HairRigSegment> Segments = new();
        // Anchor kinematic-follow bookkeeping (head-derived velocity for whip).
        public Vector3 AnchorPrevPos;
        public Quaternion AnchorPrevRot = Quaternion.Identity;
        public bool AnchorHasPrev;
    }

    private readonly List<HairRigChain> hairRigChains = new();
    private bool hairRigActive;
    private float hairRigElapsed;
    private int hairKaoRagdollBodyIndex = -1;

    private const int MaxHairRigBodies = 256;

    /// <summary>True when the BEPU hair rig is built and driving hair bones this activation.</summary>
    public bool HairRigActive => hairRigActive;

    // --- Build (called from InitializePhysics, at the physics handoff instant) ---

    private void BuildHairRig(in SkeletonAccess skel)
    {
        hairRigChains.Clear();
        hairRigActive = false;
        hairRigElapsed = 0f;
        hairKaoRagdollBodyIndex = -1;

        if (simulation == null || bufferPool == null) return;

        // The head must be a ragdoll body so the anchor can track it.
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            if (ragdollBones[i].Name == "j_kao") { hairKaoRagdollBodyIndex = i; break; }
        }
        if (hairKaoRagdollBodyIndex < 0)
        {
            log.Info("HairRig: no j_kao ragdoll body — hair rig unavailable, falling back to pendulum.");
            return;
        }

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;

        var totalBodies = 0;
        for (int ps = 1; ps < skeleton->PartialSkeletonCount && totalBodies < MaxHairRigBodies; ps++)
        {
            var partial = &skeleton->PartialSkeletons[ps];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;
            if (!IsHairPartial(pose)) continue;

            if (TryBuildHairChain(ps, pose, ref totalBodies, out var chain) && chain != null)
                hairRigChains.Add(chain);
        }

        hairRigActive = hairRigChains.Count > 0;
        if (hairRigActive)
        {
            var segCount = 0;
            foreach (var c in hairRigChains) segCount += c.Segments.Count;
            log.Info($"HairRig: built {hairRigChains.Count} chain(s), {segCount} strand bodies.");
        }
        else
        {
            log.Info("HairRig: no hair partials with simulatable bones — falling back to pendulum.");
        }
    }

    private bool TryBuildHairChain(int partialIndex, HkaPose* pose, ref int totalBodies, out HairRigChain? chain)
    {
        chain = null;

        var bones = pose->Skeleton->Bones;
        var parents = pose->Skeleton->ParentIndices;
        var boneCount = Math.Min(pose->ModelPose.Length, bones.Length);
        var parentCount = parents.Length;
        if (boneCount < 2) return false;

        // Mark hair bones: a bone is hair if its name matches a hair prefix, or its parent is hair.
        // Havok skeletons are topologically ordered (parent before child), so a single forward pass
        // resolves the transitive closure. Bone 0 (j_kao) is never hair (it is the anchor frame).
        var isHair = new bool[boneCount];
        for (int b = 1; b < boneCount && b < parentCount; b++)
        {
            var parent = parents[b];
            var nameHair = IsHairBoneName(bones[b].Name.String);
            isHair[b] = nameHair || (parent >= 0 && parent < boneCount && isHair[parent]);
        }

        // World-space bone origin + rotation for every bone (from the frozen death pose at handoff).
        var worldPos = new Vector3[boneCount];
        var worldRot = new Quaternion[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            ref var mt = ref pose->ModelPose.Data[b];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            worldPos[b] = ModelToWorld(modelPos);
            worldRot[b] = ModelRotToWorld(modelRot);
        }

        // First hair child of each bone, used to orient a bone's strand segment toward its child.
        var firstHairChild = new int[boneCount];
        Array.Fill(firstHairChild, -1);
        for (int b = 1; b < boneCount && b < parentCount; b++)
        {
            if (!isHair[b]) continue;
            var parent = parents[b];
            if (parent >= 0 && parent < boneCount && firstHairChild[parent] < 0)
                firstHairChild[parent] = b;
        }

        var thickness = MathF.Max(0.002f, config.RagdollHairRigThickness);
        var mass = MathF.Max(0.002f, config.RagdollHairRigSegmentMass);

        // --- Kinematic anchor at j_kao's frame (rig body index 0) ---
        var anchorShape = simulation!.Shapes.Add(new Box(thickness * 2f, thickness * 2f, thickness * 2f));
        var anchorHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
            new RigidPose(worldPos[0], Quaternion.Normalize(worldRot[0])),
            default(BodyVelocity),
            new CollidableDescription(anchorShape, 0.02f),
            new BodyActivityDescription(0.01f)));

        var newChain = new HairRigChain
        {
            PartialSkeletonIndex = partialIndex,
            AnchorBody = anchorHandle,
            AnchorPrevPos = worldPos[0],
            AnchorPrevRot = Quaternion.Normalize(worldRot[0]),
            AnchorHasPrev = true,
        };

        var selfCollideGroup = nextExternalRigSelfCollideGroup++;
        var rig = new ExternalRigHandle { Generation = externalBodyGeneration };
        newChain.Rig = rig;

        var anchorWrapper = new ExternalBodyHandle
        {
            Body = anchorHandle,
            Shapes = new[] { anchorShape },
            Generation = externalBodyGeneration,
        };
        rig.Bodies.Add(anchorWrapper);                          // rig body index 0
        externalBodies.Add(anchorWrapper);
        externalRigNoRagdollContactBodyHandles.Add(anchorHandle.Value); // anchor collides with nothing
        externalRigSelfCollideGroupByBody[anchorHandle.Value] = selfCollideGroup;

        // partial-bone-index -> rig-body-index (0 = anchor for j_kao and any non-hair frame).
        var rigBodyIndexByBone = new Dictionary<int, int>();

        // --- Dynamic strand bodies, one per hair bone ---
        for (int b = 1; b < boneCount && b < parentCount; b++)
        {
            if (!isHair[b]) continue;
            if (totalBodies >= MaxHairRigBodies) break;

            // Segment direction/length: bone -> first hair child, or an extrapolated stub for leaves.
            Vector3 dir;
            float len;
            var child = firstHairChild[b];
            if (child >= 0)
            {
                var d = worldPos[child] - worldPos[b];
                len = d.Length();
                dir = len > 1e-5f ? d / len : Vector3.Zero;
            }
            else
            {
                // Leaf: continue the direction coming from the parent, at a fraction of that length.
                var parent = parents[b];
                var d = parent >= 0 ? worldPos[b] - worldPos[parent] : Vector3.Zero;
                var pl = d.Length();
                dir = pl > 1e-5f ? d / pl : Vector3.UnitY;
                len = Math.Clamp(pl * 0.7f, 0.01f, 0.06f);
            }
            if (len < 0.006f) len = 0.006f;
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.UnitY;

            var halfLen = len * 0.5f;
            var bodyOri = Quaternion.Normalize(RotationBetween(Vector3.UnitY, dir));
            var center = worldPos[b] + dir * halfLen;

            var parts = new[]
            {
                new ExternalShapePart(new Vector3(thickness, halfLen, thickness), Vector3.Zero, Quaternion.Identity),
            };
            var shapeIndex = CreateExternalBodyShape(parts, out var shapes, out var inertia, mass);
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(center, bodyOri),
                default(BodyVelocity),
                inertia,
                new CollidableDescription(shapeIndex, 0.012f),
                new BodyActivityDescription(0.01f)));

            var wrapper = new ExternalBodyHandle
            {
                Body = bodyHandle,
                Shapes = shapes.ToArray(),
                Generation = externalBodyGeneration,
            };
            rig.Bodies.Add(wrapper);
            externalBodies.Add(wrapper);
            externalDynamicBodyHandles.Add(bodyHandle.Value);
            externalRigDynamicBodyHandles.Add(bodyHandle.Value);
            externalRigSelfCollideGroupByBody[bodyHandle.Value] = selfCollideGroup;
            // Strands do NOT collide with the corpse (they spawn overlapping the head/body capsules,
            // so contact resolution would explode and fling the ragdoll). They keep ground contact
            // (dynamic-vs-static is always allowed) and are driven by the head anchor through joints.
            // The filter also skips their contact with kinematic bodies (the anchor / NPC proxies).
            externalRigNoRagdollContactBodyHandles.Add(bodyHandle.Value);

            rigBodyIndexByBone[b] = rig.Bodies.Count - 1;
            newChain.Segments.Add(new HairRigSegment
            {
                BoneIndex = b,
                Body = bodyHandle,
                SegmentHalfLength = halfLen,
                // body.Orientation * offset = boneWorldRot  =>  offset = inv(bodyOri) * worldRot[b]
                CapsuleToBoneOffset = Quaternion.Normalize(Quaternion.Inverse(bodyOri) * worldRot[b]),
            });
            totalBodies++;
        }

        if (newChain.Segments.Count == 0)
        {
            RemoveExternalRig(rig);
            return false;
        }

        // --- Joints: each strand to its parent strand, or to the anchor if the parent is j_kao/non-hair ---
        var swing = Math.Clamp(config.RagdollHairRigSwingLimit, 0.05f, MathF.PI - 0.05f);
        var initialFactor = config.RagdollHairRigInitialSwingFactor;
        var poseForce = MathF.Max(0f, config.RagdollHairRigPoseGuideForce);
        foreach (var seg in newChain.Segments)
        {
            var b = seg.BoneIndex;
            var parent = parents[b];
            var parentRigIndex = (parent >= 1 && rigBodyIndexByBone.TryGetValue(parent, out var pri)) ? pri : 0;
            var childRigIndex = rigBodyIndexByBone[b];

            // Ball socket sits at this bone's origin (the joint between parent segment and this one).
            AddExternalRigJoint(rig, new ExternalRigJointSpec(
                parentIndex: parentRigIndex,
                childIndex: childRigIndex,
                anchorWorld: worldPos[b],
                swingLimit: swing,
                initialSwingFactor: initialFactor,
                poseGuideMaxForce: poseForce,
                poseGuideFrequency: 5f));
        }

        externalRigs.Add(rig);
        prevAllAsleep = false;
        chain = newChain;
        return true;
    }

    // --- Per-substep: drive each anchor to follow the head ragdoll body ---

    private void UpdateHairKinematicRoots()
    {
        if (!hairRigActive || simulation == null) return;
        if (hairKaoRagdollBodyIndex < 0 || hairKaoRagdollBodyIndex >= ragdollBones.Count) return;

        var rb = ragdollBones[hairKaoRagdollBodyIndex];
        var head = simulation.Bodies.GetBodyReference(rb.BodyHandle);
        var headOri = head.Pose.Orientation;
        var y = Vector3.Transform(Vector3.UnitY, headOri);
        var kaoPos = head.Pose.Position - rb.SegmentHalfLength * y;
        var kaoRot = Quaternion.Normalize(headOri * rb.CapsuleToBoneOffset);

        if (float.IsNaN(kaoPos.X) || float.IsNaN(kaoRot.W)) return;

        foreach (var chain in hairRigChains)
        {
            var anchor = simulation.Bodies.GetBodyReference(chain.AnchorBody);
            MoveKinematicBody(anchor, kaoPos, kaoRot,
                ref chain.AnchorPrevPos, ref chain.AnchorPrevRot, ref chain.AnchorHasPrev, FixedTimestep);

            // MoveKinematicBody derives the anchor's velocity from its pose delta. A jittering/settling
            // head produces spiky deltas that, unclamped, get injected into the strands as violent whip.
            // Cap the head-derived velocity so hair swings with the head without amplifying its jitter.
            var lin = anchor.Velocity.Linear;
            var linSpeed = lin.Length();
            if (linSpeed > AnchorMaxLinearVelocity)
                anchor.Velocity.Linear = lin * (AnchorMaxLinearVelocity / linSpeed);
            var ang = anchor.Velocity.Angular;
            var angSpeed = ang.Length();
            if (angSpeed > AnchorMaxAngularVelocity)
                anchor.Velocity.Angular = ang * (AnchorMaxAngularVelocity / angSpeed);
        }
    }

    private const float AnchorMaxLinearVelocity = 6f;
    private const float AnchorMaxAngularVelocity = 12f;

    // --- Per-substep safety: cap strand velocities ---
    // Hair strands are not covered by the corpse ClampVelocities pass, and they collide with the
    // corpse — so a diverged (exploding) strand could kick the ragdoll despite its tiny mass
    // (huge velocity × small mass is still a large impulse). Cap them hard as insurance.
    private const float HairMaxLinearVelocity = 30f;
    private const float HairMaxAngularVelocity = 50f;

    private void ClampHairRigVelocities()
    {
        if (!hairRigActive || simulation == null) return;
        foreach (var chain in hairRigChains)
        {
            foreach (var seg in chain.Segments)
            {
                var body = simulation.Bodies.GetBodyReference(seg.Body);
                if (!body.Awake) continue;
                var lin = body.Velocity.Linear;
                var linSpeed = lin.Length();
                if (linSpeed > HairMaxLinearVelocity)
                    body.Velocity.Linear = lin * (HairMaxLinearVelocity / linSpeed);
                var ang = body.Velocity.Angular;
                var angSpeed = ang.Length();
                if (angSpeed > HairMaxAngularVelocity)
                    body.Velocity.Angular = ang * (HairMaxAngularVelocity / angSpeed);
            }
        }
    }

    // --- Per-frame: read strand body poses back into the hair partial ModelPose ---

    private void ReadbackHairRig(in SkeletonAccess skel)
    {
        if (!hairRigActive || simulation == null) return;

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;

        foreach (var chain in hairRigChains)
        {
            if (chain.PartialSkeletonIndex < 0 || chain.PartialSkeletonIndex >= skeleton->PartialSkeletonCount)
                continue;
            var partial = &skeleton->PartialSkeletons[chain.PartialSkeletonIndex];
            var pose = partial->GetHavokPose(0);
            if (pose == null) continue;
            var boneCount = pose->ModelPose.Length;

            foreach (var seg in chain.Segments)
            {
                if (seg.BoneIndex < 0 || seg.BoneIndex >= boneCount) continue;
                var body = simulation.Bodies.GetBodyReference(seg.Body);
                var p = body.Pose.Position;
                var o = body.Pose.Orientation;

                // NaN guard — a diverged strand shouldn't scribble garbage into the skeleton.
                if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z) || float.IsNaN(o.W))
                    continue;

                var yAxis = Vector3.Transform(Vector3.UnitY, o);
                var boneWorldPos = p - seg.SegmentHalfLength * yAxis;   // bone origin at parent-end
                var boneWorldRot = Quaternion.Normalize(o * seg.CapsuleToBoneOffset);

                var modelPos = WorldToModel(boneWorldPos);
                var modelRot = WorldRotToModel(boneWorldRot);

                ref var mt = ref pose->ModelPose.Data[seg.BoneIndex];
                mt.Translation.X = modelPos.X;
                mt.Translation.Y = modelPos.Y;
                mt.Translation.Z = modelPos.Z;
                mt.Rotation.X = modelRot.X;
                mt.Rotation.Y = modelRot.Y;
                mt.Rotation.Z = modelRot.Z;
                mt.Rotation.W = modelRot.W;
            }
        }
    }

    // --- Per-frame: relax the swing ROM to full + fade the pose-guide servo over the settle window ---

    private void TickHairRigSettle(float dt)
    {
        if (!hairRigActive) return;

        var settle = MathF.Max(0.05f, config.RagdollHairRigSettleSeconds);
        var prev = hairRigElapsed;
        hairRigElapsed += dt;
        if (prev >= settle) return; // already fully relaxed — nothing to re-apply

        var factor = Math.Clamp(hairRigElapsed / settle, 0f, 1f);
        foreach (var chain in hairRigChains)
        {
            RelaxExternalRigSwingLimits(chain.Rig, factor);
            ApplyExternalRigPoseGuidance(chain.Rig, 1f - factor);
        }
    }

    private void RemoveHairRig()
    {
        if (hairRigChains.Count > 0)
        {
            foreach (var chain in hairRigChains)
                RemoveExternalRig(chain.Rig);
        }
        hairRigChains.Clear();
        hairRigActive = false;
        hairRigElapsed = 0f;
        hairKaoRagdollBodyIndex = -1;
    }

    // --- Hair-bone classification (shared spirit with HairPhysicsSimulator.IsHairPartialSkeleton) ---

    private static bool IsHairBoneName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("j_kami_")) return true;              // classic hair bones
        // Modern hair bones are "j_ex_h<NNNN>_<pos>" (e.g. j_ex_h0193_back_l, j_ex_h0193_side_r).
        // Require a digit right after "j_ex_h" so we catch hair but not other j_ex_ accessory
        // partials such as helmets ("j_ex_met_*") or tops ("j_ex_top_*").
        if (name.StartsWith("j_ex_h") && name.Length > 6 && char.IsDigit(name[6])) return true;
        if (name.StartsWith("j_ex_h") && name.Contains("_ke_")) return true; // older "_ke_" variant
        return false;
    }

    private static bool IsHairPartial(HkaPose* pose)
    {
        if (pose->Skeleton == null) return false;
        var bones = pose->Skeleton->Bones;
        if (bones.Length < 2) return false;
        if (bones[0].Name.String != "j_kao") return false;
        for (int i = 1; i < bones.Length; i++)
            if (IsHairBoneName(bones[i].Name.String)) return true;
        return false;
    }

    /// <summary>Shortest-arc quaternion rotating unit vector <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            var perp = Vector3.Cross(from, Vector3.UnitX);
            if (perp.LengthSquared() < 0.001f)
                perp = Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(perp), MathF.PI);
        }
        var axis = Vector3.Cross(from, to);
        var axisLen = axis.Length();
        if (axisLen < 0.0001f) return Quaternion.Identity;
        axis /= axisLen;
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }
}
