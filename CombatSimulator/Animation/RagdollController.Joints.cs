// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.SkeletonProfiles;
using System.Collections.Immutable;
using BepuPhysics;
using BepuPhysics.Constraints;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>
    /// Compute the shortest rotation that takes Vector3.UnitY to the given direction.
    /// BEPU capsules have their length along local Y, so this orients a capsule along a limb segment.
    /// </summary>
    private static Quaternion RotationFromYToDirection(Vector3 dir)
    {
        var dirN = Vector3.Normalize(dir);
        var dot = Vector3.Dot(Vector3.UnitY, dirN);

        // Already along Y
        if (dot > 0.9999f) return Quaternion.Identity;
        // Opposite to Y — rotate 180° around X
        if (dot < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dirN));
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    // One definition, shared with the mesh fit: what is measured in this frame in the reference
    // pose must be in the same frame when the body is built in the death pose.
    private static Quaternion CreateCapsuleRotation(Vector3 segmentDir, Quaternion boneWorldRot)
        => StructuralMeshFitter.SectionRotation(segmentDir, boneWorldRot);

    /// <summary>
    /// Build a quaternion encoding an orthonormal basis for TwistLimit.
    /// Z axis = twist measurement axis, X axis = 0° reference direction (orthogonalized).
    /// Pattern from BEPU RagdollDemo's CreateBasis().
    /// </summary>
    private static Quaternion CreateTwistBasis(Vector3 twistAxis, Vector3 referenceDir)
    {
        var z = Vector3.Normalize(twistAxis);
        var y = Vector3.Normalize(Vector3.Cross(z, referenceDir));
        var x = Vector3.Cross(y, z);

        // Pack the basis as a rotation matrix whose local X/Y/Z axes map to x/y/z in world.
        // System.Numerics uses the ROW-vector convention (Vector3.Transform(v, m) = v·M, so
        // the image of UnitX is row 1), therefore the basis axes go in the ROWS, not the
        // columns. Placing them in columns yields the transpose = the conjugate rotation,
        // which made every TwistLimit measure twist about the wrong axis.
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(m);
    }

    /// <summary>
    /// Compute a hinge axis perpendicular to the parent→child segment direction.
    /// For knees: perpendicular to the leg in the horizontal plane (lateral axis).
    /// For elbows: perpendicular to the arm segment.
    /// </summary>
    private Vector3 ComputeHingeAxis(Vector3 segmentDir)
    {
        var segN = Vector3.Normalize(segmentDir);

        // Cross with world up to get a horizontal perpendicular axis
        var hingeAxis = Vector3.Cross(segN, Vector3.UnitY);
        if (hingeAxis.LengthSquared() < 0.001f)
        {
            // Segment is near-vertical — use character's forward direction instead
            var forward = Vector3.Transform(Vector3.UnitZ, skelWorldRot);
            hingeAxis = Vector3.Cross(segN, forward);
        }
        // Both crosses can still degenerate (near-vertical segment AND a pitched skeleton
        // transform whose forward is also near-vertical, e.g. a mounted/flying death). Fall
        // back to a fixed horizontal axis rather than normalizing a zero vector into NaN,
        // which would otherwise propagate into the Hinge constraint and trip the NaN guard.
        return NormalizeOrFallback(hingeAxis, Vector3.UnitX);
    }

    internal static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-6f)
            return Vector3.Normalize(value);

        return fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        var normal = NormalizeOrFallback(planeNormal, Vector3.UnitY);
        return value - Vector3.Dot(value, normal) * normal;
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static float ResolveBodyHalfLength(RagdollBoneDef def)
    {
        if (def.ColliderShape == RagdollColliderShape.Box && def.BoxHalfExtents.Y > 0)
            return def.BoxHalfExtents.Y;

        return MathF.Max(0f, def.CapsuleHalfLength);
    }

    private static Vector3 ResolveBoxHalfExtents(RagdollBoneDef def, float bodyHalfLength)
    {
        var x = def.BoxHalfExtents.X > 0 ? def.BoxHalfExtents.X : MathF.Max(0.01f, def.CapsuleRadius);
        var y = def.BoxHalfExtents.Y > 0 ? def.BoxHalfExtents.Y : MathF.Max(0.01f, bodyHalfLength);
        var z = def.BoxHalfExtents.Z > 0 ? def.BoxHalfExtents.Z : MathF.Max(0.01f, def.CapsuleRadius);
        return new Vector3(x, y, z);
    }

    private static float ComputeConfiguredVerticalExtent(
        RagdollBoneDef def,
        Quaternion bodyWorldRot,
        float bodyHalfLength,
        float shapeScale)
    {
        var yAxis = Vector3.Transform(Vector3.UnitY, bodyWorldRot);
        if (def.ColliderShape != RagdollColliderShape.Box)
            return MathF.Abs(yAxis.Y) * bodyHalfLength + def.CapsuleRadius * shapeScale;

        var extents = ResolveBoxHalfExtents(def, ResolveBodyHalfLength(def)) * shapeScale;
        var xAxis = Vector3.Transform(Vector3.UnitX, bodyWorldRot);
        var zAxis = Vector3.Transform(Vector3.UnitZ, bodyWorldRot);
        return MathF.Abs(xAxis.Y) * extents.X +
               MathF.Abs(yAxis.Y) * extents.Y +
               MathF.Abs(zAxis.Y) * extents.Z;
    }

    private Vector3 ComputeFallbackBallTwistReference(Vector3 segmentDir)
    {
        var segN = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var refDir = Vector3.Cross(segN, Vector3.UnitY);
        if (refDir.LengthSquared() < 0.001f)
            refDir = Vector3.Cross(segN, Vector3.UnitX);

        return NormalizeOrFallback(refDir, Vector3.UnitX);
    }

    private Vector3 ComputeAnatomicalBallTwistReference(Vector3 segmentDir, Vector3 parentSegmentDir)
    {
        var segN = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);

        // Prefer the parent's segment projected into the child's twist plane. This makes
        // ball-joint twist limits use an anatomical parent/child frame instead of world up.
        var refDir = ProjectOntoPlane(parentN, segN);
        if (refDir.LengthSquared() < 0.001f)
            return ComputeFallbackBallTwistReference(segN);

        return NormalizeOrFallback(refDir, ComputeFallbackBallTwistReference(segN));
    }

    private Vector3 ComputeAnatomicalHingeAxis(Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var hingeAxis = Vector3.Cross(parentN, childN);

        // Anatomical hinge axes are undefined for near-straight limbs. Keep main's
        // world-up/character-forward fallback in that case.
        if (hingeAxis.LengthSquared() < 0.001f)
            return ComputeHingeAxis(childN);

        var fallbackAxis = ComputeHingeAxis(childN);
        hingeAxis = Vector3.Normalize(hingeAxis);
        if (Vector3.Dot(hingeAxis, fallbackAxis) < 0)
            hingeAxis = -hingeAxis;

        return hingeAxis;
    }

    // FFXIV's skeleton bone roll (the rotation around a bone's own segment axis) is an
    // animation/skinning convention, not a physics one: nothing guarantees "local X = flex
    // axis", and the convention differs per bone and per race. Deriving the knee/ankle hinge
    // axis from the LIVE (death-instant) pose is numerically degenerate for a near-straight
    // limb (Cross of two nearly-parallel vectors) — the repeated source of asymmetric L/R knee
    // bugs in this codebase. bepuphysics2's own RagdollDemo sidesteps the whole problem: it
    // never imports bone orientation from an external skeleton, it builds its own capsule
    // frames purely from segment geometry (GetCapsuleForLineSegment), so "local X = hinge axis"
    // is true by construction. We can't discard the source skeleton (FFXIV drives skinning),
    // but we can take the same axis once from the REFERENCE (bind) pose — which is not
    // perfectly straight, unlike a death pose — and cache it as a bone-local vector reapplied
    // through the bone's live rotation every frame. Bind pose is fixed per skeleton
    // (race/gender), so this is computed once at activation and is immune to death-pose noise
    // and independent of character facing.
    private readonly struct BindPoseHingeFrame
    {
        public readonly Vector3 AxisBoneLocal;
        public readonly Vector3 FlexionBoneLocal;

        public BindPoseHingeFrame(Vector3 axisBoneLocal, Vector3 flexionBoneLocal)
        {
            AxisBoneLocal = axisBoneLocal;
            FlexionBoneLocal = flexionBoneLocal;
        }
    }

    // The hinges whose frame comes from the reference pose (thigh -> shin -> calf for the upper knee,
    // shin -> calf -> ankle for the lower): profile data.
    private static ImmutableArray<HingeFrameTarget> BindPoseHingeTargets => HumanoidSkeleton.HingeFrameTargets;

    private static Dictionary<string, BindPoseHingeFrame> BuildBindPoseHingeFrames(ISkeletonPose skel)
    {
        var result = new Dictionary<string, BindPoseHingeFrame>(StringComparer.Ordinal);
        if (!TryBuildReferenceModelTransforms(skel, out var referenceModel))
            return result;

        foreach (var target in BindPoseHingeTargets)
        {
            var parentIdx = ResolveBoneIndex(skel, target.Parent);
            var boneIdx = ResolveBoneIndex(skel, target.Bone);
            var childIdx = ResolveBoneIndex(skel, target.Child);
            if (parentIdx < 0 || boneIdx < 0 || childIdx < 0 ||
                parentIdx >= referenceModel.Length || boneIdx >= referenceModel.Length ||
                childIdx >= referenceModel.Length)
                continue;

            var parentDirRaw = referenceModel[boneIdx].Translation - referenceModel[parentIdx].Translation;
            var childDirRaw = referenceModel[childIdx].Translation - referenceModel[boneIdx].Translation;
            if (parentDirRaw.LengthSquared() < 1e-8f || childDirRaw.LengthSquared() < 1e-8f)
                continue;
            var parentDir = Vector3.Normalize(parentDirRaw);
            var childDir = Vector3.Normalize(childDirRaw);

            // Fold direction: the component of the child segment perpendicular to the parent
            // segment, IN THE BIND POSE. Even a near-straight bind pose carries a small natural
            // bend (unlike a death pose, which can be exactly straight), so this is far less
            // prone to the degeneracy that plagued the live-pose Cross(parent,child) attempt.
            var flexionRaw = childDir - parentDir * Vector3.Dot(childDir, parentDir);
            if (flexionRaw.LengthSquared() < 1e-8f)
                continue; // bind pose is degenerate too (shouldn't happen for knee/ankle)
            var flexion = Vector3.Normalize(flexionRaw);

            var axisRaw = Vector3.Cross(parentDir, flexion);
            if (axisRaw.LengthSquared() < 1e-8f)
                continue;
            var axis = Vector3.Normalize(axisRaw);

            if (!Matrix4x4.Decompose(referenceModel[boneIdx], out _, out var boneBindRot, out _))
                continue;
            var inverseBoneBindRot = Quaternion.Inverse(boneBindRot);

            result[target.Bone] = new BindPoseHingeFrame(
                Vector3.Normalize(Vector3.Transform(axis, inverseBoneBindRot)),
                Vector3.Normalize(Vector3.Transform(flexion, inverseBoneBindRot)));
        }

        return result;
    }

    /// <summary>
    /// One-time diagnostic (always logs, once per activation): tests whether j_asi_b ("shin") is
    /// really a full knee-to-ankle bone in FFXIV's own bind pose, or a short proximal segment with
    /// most of the visual shin length carried by j_asi_c; whether j_asi_c rotates independently of
    /// j_asi_b in the live (death) pose; and — the deciding question — whether that independent
    /// rotation is just axial roll (harmless to a single straight capsule) or a genuine kink (c's
    /// live position departs from the straight line between b and d, i.e. the leg visibly bends
    /// AT c, someplace our single b-to-d capsule can never represent).
    /// </summary>
    private void LogLegBoneStructureDiagnostics(
        ISkeletonPose skel,
        Dictionary<string, Vector3> boneWorldPositions,
        Dictionary<string, Quaternion> boneWorldRotations)
    {
        if (!TryBuildReferenceModelTransforms(skel, out var referenceModel))
            return;

        var pose = skel.ModelPose;

        foreach (var left in new[] { true, false })
        {
            var side = left ? "l" : "r"; // log text only
            var aIdx = ResolveBoneIndex(skel, Bones.Thigh.For(left));
            var bIdx = ResolveBoneIndex(skel, Bones.Shin.For(left));
            var cIdx = ResolveBoneIndex(skel, Bones.Calf.For(left));
            var dIdx = ResolveBoneIndex(skel, Bones.Foot.For(left));
            if (aIdx < 0 || bIdx < 0 || cIdx < 0 || dIdx < 0 ||
                aIdx >= referenceModel.Length || bIdx >= referenceModel.Length ||
                cIdx >= referenceModel.Length || dIdx >= referenceModel.Length ||
                cIdx >= skel.BoneCount)
            {
                log.Info($"[Ragdoll LegDiag] side={side}: one or more of j_asi_a/b/c/d did not resolve.");
                continue;
            }

            // --- BIND pose: segment lengths ---
            var aBindPos = referenceModel[aIdx].Translation;
            var bBindPos = referenceModel[bIdx].Translation;
            var cBindPos = referenceModel[cIdx].Translation;
            var dBindPos = referenceModel[dIdx].Translation;
            var abLen = Vector3.Distance(aBindPos, bBindPos);
            var bcLen = Vector3.Distance(bBindPos, cBindPos);
            var cdLen = Vector3.Distance(cBindPos, dBindPos);
            var bdLen = Vector3.Distance(bBindPos, dBindPos);

            log.Info($"[Ragdoll LegDiag] side={side} BIND lengths: thigh(a->b)={abLen:F3}m " +
                     $"b->c={bcLen:F3}m c->d={cdLen:F3}m (b->c is {(bcLen / MathF.Max(0.0001f, bcLen + cdLen)) * 100f:F0}% of the b..d span) " +
                     $"b->d(direct)={bdLen:F3}m");

            // --- Live (death-pose) rotation drift of c relative to b ---
            if (!Matrix4x4.Decompose(referenceModel[bIdx], out _, out var bBindRot, out _) ||
                !Matrix4x4.Decompose(referenceModel[cIdx], out _, out var cBindRot, out _))
            {
                log.Info($"[Ragdoll LegDiag] side={side}: could not decompose bind rotation for b/c.");
                continue;
            }
            var bindRelative = Quaternion.Normalize(Quaternion.Inverse(bBindRot) * cBindRot);

            if (!boneWorldPositions.TryGetValue(Bones.Thigh.For(left), out var aLivePos) ||
                !boneWorldPositions.TryGetValue(Bones.Shin.For(left), out var bLivePos) ||
                !boneWorldPositions.TryGetValue(Bones.Foot.For(left), out var dLivePos) ||
                !boneWorldRotations.TryGetValue(Bones.Shin.For(left), out var bLiveWorldRot))
            {
                log.Info($"[Ragdoll LegDiag] side={side}: a/b/d not in live pose maps (unexpected).");
                continue;
            }
            ref var cMt = ref pose[cIdx];
            var cLiveModelPos = new Vector3(cMt.Translation.X, cMt.Translation.Y, cMt.Translation.Z);
            var cLivePos = ModelToWorld(cLiveModelPos);
            var cLiveModelRot = new Quaternion(cMt.Rotation.X, cMt.Rotation.Y, cMt.Rotation.Z, cMt.Rotation.W);
            var cLiveWorldRot = ModelRotToWorld(cLiveModelRot);

            var liveRelative = Quaternion.Normalize(Quaternion.Inverse(bLiveWorldRot) * cLiveWorldRot);
            var drift = Quaternion.Normalize(Quaternion.Inverse(bindRelative) * liveRelative);
            var driftAngle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(drift.W), -1f, 1f)) * (180f / MathF.PI);

            // --- The deciding measurement: does c's LIVE position sit on the straight line from
            // b to d (a roll-only difference — our single capsule is still fine), or does it
            // bulge off that line (a real kink — the leg visibly bends at c) ---
            var vecAB = Vector3.Normalize(bLivePos - aLivePos);
            var vecBC = cLivePos - bLivePos;
            var vecBD = dLivePos - bLivePos;
            var vecCD = dLivePos - cLivePos;
            var bcLenLive = vecBC.Length();
            var cdLenLive = vecCD.Length();
            var bendAtB = bcLenLive > 1e-5f
                ? MathF.Acos(Math.Clamp(Vector3.Dot(vecAB, vecBC / bcLenLive), -1f, 1f)) * (180f / MathF.PI)
                : float.NaN;
            var bendAtC = bcLenLive > 1e-5f && cdLenLive > 1e-5f
                ? MathF.Acos(Math.Clamp(Vector3.Dot(vecBC / bcLenLive, vecCD / cdLenLive), -1f, 1f)) * (180f / MathF.PI)
                : float.NaN;

            // Perpendicular distance from c to the line through b and d.
            float kinkOffset;
            if (vecBD.LengthSquared() > 1e-8f)
            {
                var bdDir = Vector3.Normalize(vecBD);
                var along = Vector3.Dot(vecBC, bdDir);
                var closest = bLivePos + bdDir * along;
                kinkOffset = Vector3.Distance(cLivePos, closest);
            }
            else
            {
                kinkOffset = float.NaN;
            }

            log.Info($"[Ragdoll LegDiag] side={side} LIVE: rotation drift of c vs rigidly-following b " +
                     $"= {driftAngle:F1}°; bend AT b (thigh vs b->c) = {bendAtB:F1}°; " +
                     $"bend AT c (b->c vs c->d) = {bendAtC:F1}°; c's offset from the straight b-d line = {kinkOffset * 100f:F1}cm " +
                     $"(b->c={bcLenLive:F3}m, c->d={cdLenLive:F3}m live)");
        }
    }

    private Vector3 ComputeProfileHingeAxis(AnatomicalRole role, Vector3 parentSegmentDir, Vector3 childSegmentDir, Quaternion childBodyRot)
    {
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);

        // Tier B — anatomy fixes the hinge axis AND its sign.
        //
        // Cross(parentSegment, foldDirection) is perpendicular to both: it is the medial-lateral axis a
        // knee actually turns about. And by construction Cross(axis, parentSegment) comes straight back
        // out as the fold direction — which is precisely the vector ComputeHingeForward hands to the
        // SwingLimit. So the axis and the limit's reference cannot disagree with each other, whatever
        // pose the body died in.
        //
        // This is the piece the previous anatomical axis was missing. It got the LINE right — a stable
        // character left-right axis, instead of Cross(parent, child), which is degenerate for a limb
        // that happens to be straight — and then deliberately took its SIGN from the legacy axis. For a
        // straight leg the legacy axis falls back to Cross(vertical shin, character forward), whose fold
        // direction points FORWARD. The axis was right and it was wired to the wrong sign, which is
        // exactly why switching it on bent every knee the wrong way. It was never the wrong idea.
        var flexion = ComputeAnatomicalFlexionDirection(role, parentN);
        if (flexion.HasValue)
        {
            var axis = Vector3.Cross(parentN, flexion.Value);
            if (axis.LengthSquared() > 0.001f)
                return Vector3.Normalize(axis);
        }

        if (role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow)
        {
            var frameAxis = ProjectOntoPlane(Vector3.Transform(Vector3.UnitX, childBodyRot), childN);
            if (frameAxis.LengthSquared() > 0.001f)
            {
                var axis = Vector3.Normalize(frameAxis);
                var anatomical = ComputeAnatomicalHingeAxis(parentSegmentDir, childN);
                if (Vector3.Dot(axis, anatomical) < 0)
                    axis = -axis;
                return axis;
            }
        }

        return ComputeAnatomicalHingeAxis(parentSegmentDir, childN);
    }

    /// <summary>
    /// Which way this joint folds — read off the body, not off the pose it died in.
    ///
    /// A knee folds the shin BACKWARD. An elbow folds the forearm FORWARD. Relative to the character
    /// those are simply facts, and they have nothing whatever to do with whether the corpse was
    /// standing, kneeling or mid-stride at the moment physics took over.
    ///
    /// The rig was reading them off the pose regardless. ComputeHingeForward decided its sign with
    ///
    ///     if (Dot(forward, childSegment) &lt; 0) forward = -forward;
    ///
    /// which points the fold wherever the limb HAPPENED to be leaning. On a straight leg the shin is
    /// perpendicular to that vector, the dot product is ~0, and the sign falls out of floating-point
    /// noise — independently for each leg, which is why one knee would bend sideways and the other
    /// forward. Whichever leg lost the coin toss had its swing limit treat normal flexion as
    /// hyperextension and locked. Activate from a kneel instead and the fold direction came out right,
    /// because the shin really was leaning back. That is the whole of "the stiffness depends on the
    /// death pose".
    ///
    /// Null when the limb happens to lie along the fold direction and the projection is degenerate, so
    /// the caller can fall back.
    /// </summary>
    private Vector3? ComputeAnatomicalFlexionDirection(AnatomicalRole role, Vector3 parentSegmentDir)
    {
        if (role != AnatomicalRole.Knee && role != AnatomicalRole.Elbow) return null;

        var forward = FlatNormalize(anatomicalForward, Vector3.UnitZ);
        var fold = role == AnatomicalRole.Knee ? -forward : forward;

        // Perpendicular to the parent segment, because that is the frame the SwingLimit measures in.
        var projected = ProjectOntoPlane(fold, NormalizeOrFallback(parentSegmentDir, Vector3.UnitY));
        if (projected.LengthSquared() < 0.001f)
            return null;

        return Vector3.Normalize(projected);
    }

    /// <summary>The direction the child limb swings toward as the joint flexes — the SwingLimit's
    /// reference on the parent body.</summary>
    private Vector3 ComputeHingeForward(AnatomicalRole role, Vector3 hingeAxis, Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);

        // Anatomy first. Which way a knee folds is a fact about the body.
        var flexion = ComputeAnatomicalFlexionDirection(role, parentN);
        if (flexion.HasValue)
            return flexion.Value;

        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var forward = Vector3.Cross(hingeAxis, parentN);
        if (forward.LengthSquared() < 0.001f)
            forward = ProjectOntoPlane(childN, hingeAxis);

        forward = NormalizeOrFallback(forward, childN);

        // The legacy sign: point the fold wherever the limb is leaning. This IS the pose dependence,
        // kept only for joints anatomy has nothing to say about.
        if (Vector3.Dot(forward, childN) < 0)
            forward = -forward;

        return forward;
    }

    private void AddAnatomicalHingeFoldStop(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld,
        SpringSettings limitSpring,
        float? foldCapOverride = null)
    {
        if (simulation == null)
            return;
        // foldCapOverride (Tier C) lets the anatomical ROM drive the max-fold cap; when it is
        // supplied we honour it even if SwingMinLimit was 0/disabled for this bone.
        if (foldCapOverride == null && (boneDef.SwingMinLimit <= 0 || boneDef.SwingMinLimit >= MathF.PI))
            return;

        // Measure the true anatomical bend angle (angle between child and parent segment dirs).
        // This is pose-independent: 0° = straight, 90° = ORZ-bent, up to 180° = folded back.
        var initBendAngle = MathF.Acos(Math.Clamp(
            Vector3.Dot(Vector3.Normalize(segDirWorld), Vector3.Normalize(parentSegDir)), -1f, 1f));

        // Set the fold stop limit to max(SwingMinLimit, initBend + buffer) so it never fires
        // at the initial pose — even from ORZ (90°) — but still protects against hyper-flexion.
        // Straight init: max(43°, 0°+15°) = 43°   — normal anatomical protection.
        // ORZ init:      max(43°, 90°+15°) = 105°  — fires only at extreme over-bending.
        const float foldBuffer = 0.26f; // ~15°
        var foldFloor = foldCapOverride ?? boneDef.SwingMinLimit;
        var foldStopMaxAngle = MathF.Max(foldFloor, initBendAngle + foldBuffer);
        foldStopMaxAngle = MathF.Min(foldStopMaxAngle, MathF.PI - 0.05f);

        // Both axes use segment directions so the constraint measures the actual anatomical
        // bend angle, not a pose-derived reference that can flip between init poses.
        var shinAxisLocalChild = Vector3.Normalize(Vector3.Transform(
            segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
        var thighAxisLocalParent = Vector3.Normalize(Vector3.Transform(
            parentSegDir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

        simulation.Solver.Add(childHandle, parentHandle,
            new SwingLimit
            {
                AxisLocalA = shinAxisLocalChild,
                AxisLocalB = thighAxisLocalParent,
                MaximumSwingAngle = foldStopMaxAngle,
                SpringSettings = limitSpring,
            });

        if (settings.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' fold stop: initBend={initBendAngle * 180f / MathF.PI:F1}° limit={foldStopMaxAngle * 180f / MathF.PI:F1}°");
    }

    /// <summary>
    /// Tier C (C3) — directional flexion / hyperextension limit for a knee or elbow about the
    /// hinge axis. Unlike the fold-stop (which measures the UNSIGNED angle between the
    /// two segments and so cannot tell forward flexion from backward hyperextension), this is a
    /// SIGNED limit about the hinge axis: flexion is allowed up to <c>rom.FlexionMax</c> while
    /// bending backward past straight is blocked beyond <c>rom.ExtensionMax</c> (hyperextension).
    /// </summary>
    private void AddAnatomicalHingeFlexionLimit(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld,
        JointRom rom,
        SpringSettings limitSpring)
    {
        if (simulation == null)
            return;

        var hingeN = NormalizeOrFallback(hingeAxisWorld, Vector3.UnitX);

        // Per-body bases: same twist axis (hinge), each referenced to its own segment so the
        // measured relative twist == true flexion.
        var basisAWorld = CreateTwistBasis(hingeN, segDirWorld);    // child  (shin / forearm)
        var basisBWorld = CreateTwistBasis(hingeN, parentSegDir);   // parent (thigh / upper arm)

        // Measure init signed flexion in the solver's convention: the angle of the child's
        // in-plane axis relative to the parent's about the hinge axis. CreateTwistBasis maps the
        // local X axis to ProjectOntoPlane(segment, hingeAxis), so we compare those directly.
        var xA = ProjectOntoPlane(segDirWorld, hingeN);
        var xB = ProjectOntoPlane(parentSegDir, hingeN);
        float initFlexion = 0f;
        if (xA.LengthSquared() > 1e-6f && xB.LengthSquared() > 1e-6f)
        {
            xA = Vector3.Normalize(xA);
            xB = Vector3.Normalize(xB);
            var s = Vector3.Dot(Vector3.Cross(xB, xA), hingeN);
            var c = Math.Clamp(Vector3.Dot(xB, xA), -1f, 1f);
            initFlexion = MathF.Atan2(s, c);
        }

        // Anatomical bounds: flexion positive, hyperextension a small negative floor.
        var minAngle = -rom.ExtensionMax; // e.g. knee -5°
        var maxAngle =  rom.FlexionMax;   // e.g. knee +140°

        // Snap-proof widening: keep the init pose strictly inside the range, sign-agnostically.
        const float margin = 0.09f; // ~5°
        var initPad = MathF.Abs(initFlexion) + margin;
        minAngle = MathF.Min(minAngle, -initPad);
        maxAngle = MathF.Max(maxAngle,  initPad);
        // Never exceed the solver's measurable twist range.
        minAngle = MathF.Max(minAngle, -(MathF.PI - 0.05f));
        maxAngle = MathF.Min(maxAngle,  MathF.PI - 0.05f);

        var basisALocal = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * basisAWorld);
        var basisBLocal = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * basisBWorld);

        simulation.Solver.Add(childHandle, parentHandle,
            new TwistLimit
            {
                LocalBasisA = basisALocal,
                LocalBasisB = basisBLocal,
                MinimumAngle = minAngle,
                MaximumAngle = maxAngle,
                SpringSettings = limitSpring,
            });

        // One-time validation log (per knee/elbow): resolved flexion min/max + init flexion.
        log.Info($"[Ragdoll ROM] '{boneDef.Name}' flexion limit: initFlexion={initFlexion * 180f / MathF.PI:F1}° " +
                 $"range=[{minAngle * 180f / MathF.PI:F1}°,{maxAngle * 180f / MathF.PI:F1}°] " +
                 $"(hyperext block>{rom.ExtensionMax * 180f / MathF.PI:F0}°, flexMax={rom.FlexionMax * 180f / MathF.PI:F0}°)");
    }

    private void AddAnatomicalHingeRestBias(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld)
    {
        if (!HasPassiveHingeRest(boneDef.AnatomicalRole, boneDef.Name) ||
            boneDef.HingeRestSpringFreq <= 0 ||
            boneDef.HingeRestMaxForce <= 0 ||
            simulation == null)
            return;

        var childBasis = CreateTwistBasis(hingeAxisWorld, segDirWorld);
        var parentBasis = CreateTwistBasis(hingeAxisWorld, parentSegDir);

        var initBendCos = Vector3.Dot(Vector3.Normalize(segDirWorld), Vector3.Normalize(parentSegDir));
        var initBendDeg = MathF.Acos(Math.Clamp(initBendCos, -1f, 1f)) * (180f / MathF.PI);
        if (settings.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' hinge rest init bend={initBendDeg:F1}° (0=straight, 90=ORZ)");

        simulation.Solver.Add(childHandle, parentHandle,
            new TwistServo
            {
                LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * childBasis),
                LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * parentBasis),
                TargetAngle = boneDef.HingeRestAngle,
                SpringSettings = new SpringSettings(boneDef.HingeRestSpringFreq, 0.8f),
                ServoSettings = new ServoSettings(10.0f, 0f, boneDef.HingeRestMaxForce),
            });

        if (settings.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' passive hinge rest: angle={boneDef.HingeRestAngle:F2} freq={boneDef.HingeRestSpringFreq:F2} force={boneDef.HingeRestMaxForce:F2}");
    }

    /// <summary>
    /// Tier C (swing) — directional anatomical limits for ball joints (hips, arm-side
    /// shoulders). Expresses per-direction tilt caps a symmetric cone cannot: each
    /// direction d with cap θ becomes one SwingLimit between the child segment axis and
    /// −d with MaximumSwingAngle = 90° + θ ("stay at least 90°−θ away from d").
    /// Near-unbounded directions (90°+θ ≈ 180°) are skipped — the widened bounding cone
    /// owns those. Axes are anatomical (character facing at activation), baked into the
    /// parent body's local frame; lateral-out is resolved from which side of the parent
    /// the joint anchor sits on, so left/right need no handedness convention.
    /// </summary>
    private void AddDirectionalSwingLimits(
        string boneName,
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        Vector3 segDirWorld,
        Vector3 anchorWorld,
        Vector3 anatForwardWorld,
        Vector3 anatLateralWorld,
        JointRom rom,
        SpringSettings coneSpring,
        SpringSettings capSpring)
    {
        if (simulation == null)
            return;

        float outSign = MathF.Sign(Vector3.Dot(anchorWorld - parentBodyRef.Pose.Position, anatLateralWorld));
        if (outSign == 0f)
            outSign = 1f;
        var lateralOut = anatLateralWorld * outSign;

        var axisChildLocal = Vector3.Normalize(Vector3.Transform(
            segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

        // Symmetric ROM (the spine chain: 15/15/15/15, neck ~45): the intersection of the
        // four directional caps IS a cone around the anatomical vertical — emit ONE
        // constraint instead of four.
        var maxCap = MathF.Max(MathF.Max(rom.FlexionMax, rom.ExtensionMax),
                               MathF.Max(rom.AbductionMax, rom.AdductionMax));
        var minCap = MathF.Min(MathF.Min(rom.FlexionMax, rom.ExtensionMax),
                               MathF.Min(rom.AbductionMax, rom.AdductionMax));
        if (maxCap - minCap < 0.12f)
        {
            // Anchor to whichever world vertical the segment points along (spine: up).
            var neutralWorld = Vector3.Dot(segDirWorld, Vector3.UnitY) >= 0f ? Vector3.UnitY : -Vector3.UnitY;
            var neutralLocalParent = Vector3.Normalize(Vector3.Transform(
                neutralWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = axisChildLocal,
                AxisLocalB = neutralLocalParent,
                MaximumSwingAngle = maxCap,
                SpringSettings = coneSpring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = axisChildLocal,
                AxisLocalParent = neutralLocalParent,
                LimitAngle = maxCap,
            });

            if (settings.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' symmetric anatomical cone: {maxCap * 180f / MathF.PI:F0}°");
            return;
        }

        AddCap(anatForwardWorld, rom.FlexionMax, "flex");
        AddCap(-anatForwardWorld, rom.ExtensionMax, "ext");
        AddCap(lateralOut, rom.AbductionMax, "abd");
        AddCap(-lateralOut, rom.AdductionMax, "add");

        // Diagonal caps: four cardinal caps alone leave the 45°-azimuth corners
        // inflated — round the box toward the real elliptical envelope.
        var diag = 1f / MathF.Sqrt(2f);
        AddCap((anatForwardWorld + lateralOut) * diag, (rom.FlexionMax + rom.AbductionMax) * 0.5f, "flex-abd");
        AddCap((anatForwardWorld - lateralOut) * diag, (rom.FlexionMax + rom.AdductionMax) * 0.5f, "flex-add");
        AddCap((-anatForwardWorld + lateralOut) * diag, (rom.ExtensionMax + rom.AbductionMax) * 0.5f, "ext-abd");
        AddCap((-anatForwardWorld - lateralOut) * diag, (rom.ExtensionMax + rom.AdductionMax) * 0.5f, "ext-add");

        void AddCap(Vector3 dir, float tilt, string label)
        {
            var max = MathF.PI / 2f + tilt;
            if (max >= MathF.PI - 0.15f)
                return; // effectively unbounded — the cone backstop covers it

            var oppositeLocalParent = Vector3.Normalize(Vector3.Transform(
                -dir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation!.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = axisChildLocal,
                AxisLocalB = oppositeLocalParent,
                MaximumSwingAngle = max,
                SpringSettings = capSpring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = axisChildLocal,
                AxisLocalParent = oppositeLocalParent,
                LimitAngle = max,
            });

            if (settings.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' directional swing '{label}': tilt cap={tilt * 180f / MathF.PI:F0}° (swing max={max * 180f / MathF.PI:F0}°)");
        }
    }

    /// <summary>
    /// Tier C (swing) — hip axial-rotation caps that survive any flexion angle.
    /// Constrain the KNEECAP-FACING axis (⊥ femur, anterior at rest) instead: internal
    /// rotation sweeps it medially and external laterally, while pure flexion rotates
    /// it inside the sagittal plane and never touches these caps.
    /// </summary>
    private void AddKneecapFacingLimits(
        string boneName,
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        Vector3 segDirWorld,
        Vector3 anchorWorld,
        Vector3 anatForwardWorld,
        Vector3 anatLateralWorld,
        SpringSettings spring)
    {
        if (simulation == null)
            return;

        // Kneecap-forward at activation: anatomical forward made perpendicular to the
        // femur axis. Anatomically anchored — a death pose that froze mid-rotation
        // consumes its own budget (and gets gently un-rotated if past it).
        var femurAxis = Vector3.Normalize(segDirWorld);
        var kneecapFwd = anatForwardWorld - femurAxis * Vector3.Dot(anatForwardWorld, femurAxis);
        if (kneecapFwd.LengthSquared() < 0.01f)
            return; // femur ~aligned with anatomical forward (unusual death pose): skip
        kneecapFwd = Vector3.Normalize(kneecapFwd);

        float outSign = MathF.Sign(Vector3.Dot(anchorWorld - parentBodyRef.Pose.Position, anatLateralWorld));
        if (outSign == 0f)
            outSign = 1f;
        var lateralOut = anatLateralWorld * outSign;

        var kneecapLocalChild = Vector3.Normalize(Vector3.Transform(
            kneecapFwd, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

        AddCap(-lateralOut, HipInternalRotationCap, "int-rot");
        AddCap(lateralOut, HipExternalRotationCap, "ext-rot");

        void AddCap(Vector3 dir, float tilt, string label)
        {
            var max = MathF.PI / 2f + tilt;
            var oppositeLocalParent = Vector3.Normalize(Vector3.Transform(
                -dir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation!.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = kneecapLocalChild,
                AxisLocalB = oppositeLocalParent,
                MaximumSwingAngle = max,
                SpringSettings = spring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = kneecapLocalChild,
                AxisLocalParent = oppositeLocalParent,
                LimitAngle = max,
            });

            if (settings.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' kneecap-facing '{label}': cap={tilt * 180f / MathF.PI:F0}°");
        }
    }

    // === Tier C (swing) debug: joint-vs-limit stress for the overlay =====================
    public readonly record struct JointLimitStress(string BoneName, Vector3 WorldPosition, float Stress);

    private struct SwingStressMonitor
    {
        public string Bone;
        public BodyHandle Child;
        public BodyHandle Parent;
        public Vector3 AxisLocalChild;
        public Vector3 AxisLocalParent;
        public float LimitAngle;
    }

    /// <summary>
    /// Per-bone worst swing-limit utilisation (current angle / limit angle; ≈1 = the joint
    /// is pinned on its range edge). Covers ball cones and the Tier-C directional limits.
    /// Overlay diagnostic for "spread-eagle" poses: pinned joints light up.
    /// </summary>
    public void CollectJointLimitStress(List<JointLimitStress> output)
    {
        output.Clear();
        if (simulation == null || !isActive || swingStressMonitors.Count == 0)
            return;

        stressWorstByBone.Clear();
        foreach (var m in swingStressMonitors)
        {
            var child = simulation.Bodies.GetBodyReference(m.Child);
            var parent = simulation.Bodies.GetBodyReference(m.Parent);
            var a = Vector3.Transform(m.AxisLocalChild, child.Pose.Orientation);
            var b = Vector3.Transform(m.AxisLocalParent, parent.Pose.Orientation);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));
            var stress = m.LimitAngle > 0.01f ? angle / m.LimitAngle : 0f;

            if (!stressWorstByBone.TryGetValue(m.Bone, out var worst) || stress > worst.Stress)
                stressWorstByBone[m.Bone] = new JointLimitStress(m.Bone, child.Pose.Position, stress);
        }

        foreach (var entry in stressWorstByBone.Values)
            output.Add(entry);
    }


    private const float JointMotorDamping = 0.01f;

    /// <summary>
    /// The joint motor's torque budget, as a fraction of the torque gravity puts on the limb it holds.
    ///
    /// That motor is a damper: it drives the relative angular velocity between a bone and its parent to
    /// zero. At rest it does nothing at all — but hand it an UNLIMITED force ceiling, which is what this
    /// rig did, and any relative rotation is annihilated the instant it appears. Gravity turns a thigh
    /// about the hip, the thigh begins to turn, the motor erases the turn. An unlimited velocity damper
    /// is not a joint. It is a weld, and it is the whole reason the corpse reads stiff.
    ///
    /// The rig had already caught itself at this: to make a severed limb actually swing, the recoil pass
    /// drops this motor's force to ZERO for a moment and puts it back afterwards. Zero and infinity were
    /// the only two settings it had. Every other motor in the file — the ones on garments and external
    /// limbs — was given a finite force from the outset.
    ///
    /// Give it a budget and it stops being a weld and becomes the thing it was always meant to be:
    /// friction. Sized against the limb's own weight, it damps the jitter a light body picks up while
    /// leaving gravity and an impact the authority to genuinely throw the limb around. Below 1 the limb
    /// always wins in the end, which is exactly what a dead limb does.
    /// </summary>
    private const float JointFrictionFraction = 0.35f;
    // The head and neck form a short two-link chain and receive continuous small impulses while
    // swarm colliders walk on the corpse.  The general limp-joint budget is intentionally too weak
    // to dissipate that input, producing the known persistent head nod.  A local finite budget keeps
    // common whole-body rotation intact (the motor damps relative velocity only) without stiffening
    // the rest of the ragdoll or tightening its joint limits.
    private const float HeadNeckJointFrictionFraction = 0.9f;

    /// <summary>
    /// How much torque a bone's joint may spend resisting rotation, taken from the weight it carries. A
    /// heavier limb on a longer lever gets a proportionally firmer joint, so a neck and a thigh both
    /// behave like joints instead of one being a hinge and the other a rag.
    /// </summary>
    private float ResolveJointFriction(in RagdollBone bone)
    {
        var lever = MathF.Max(0.04f, bone.CapsuleHalfLength);
        var gravityTorque = MathF.Max(0.01f, bone.Mass) * RagdollTuning.Gravity * lever;
        var fraction = bone.Name == Bones.Neck || bone.Name == Bones.Head
            ? HeadNeckJointFrictionFraction
            : JointFrictionFraction;
        return MathF.Max(0.05f, gravityTorque * fraction);
    }
}
