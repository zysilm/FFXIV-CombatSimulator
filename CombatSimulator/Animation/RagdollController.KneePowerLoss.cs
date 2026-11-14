// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using BepuPhysics;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private static GuidedCollapseKneePowerLossSettings EffectiveKneePowerLossSettings() => KneePowerLossDefaults;

    // --- Knee power-loss forward pattern (C# pattern spike) ---
    // Biomechanics-inspired failure controller: feet provide soft support, knee flexion bias
    // nudges legs into the sagittal plane, pelvis/chest only receive weak feedback. This should
    // feel less like the body is being pulled into a pose and more like leg extensor tone failed.
    private enum KneePowerLossPhase { Buckle, TorsoLoss, Release }

    private bool pendingKneePowerLossPattern;
    private bool kneePowerLossActive;
    private KneePowerLossPhase kneePowerLossPhase;
    private float kneePowerLossElapsed;
    private float kneePowerLossPhaseElapsed;
    private ConstraintHandle? kneeLeftFootSupport;
    private ConstraintHandle? kneeRightFootSupport;
    private ConstraintHandle? kneeLeftFlexBias;
    private ConstraintHandle? kneeRightFlexBias;
    private ConstraintHandle? kneePelvisSupport;
    private ConstraintHandle? kneeChestPitch;
    private Vector3 kneeForward;
    private Vector3 kneeRight;
    private Vector3 kneeLeftFootStart;
    private Vector3 kneeRightFootStart;
    private Vector3 kneeLeftFootLocalOffset;
    private Vector3 kneeRightFootLocalOffset;
    private Vector3 kneeLeftStart;
    private Vector3 kneeRightStart;
    private Vector3 kneePelvisStart;
    private Quaternion kneeLeftKneeStartTarget;
    private Quaternion kneeRightKneeStartTarget;
    private Quaternion kneeLeftKneeFlexTarget;
    private Quaternion kneeRightKneeFlexTarget;
    private Quaternion kneeChestStartRot;

    public bool KneePowerLossPatternActive => kneePowerLossActive;

    private void RequestKneePowerLossForwardOnReadyCore()
    {
        if (IsSimulationReady)
        {
            BeginKneePowerLossForwardPattern();
            return;
        }

        pendingKneePowerLossPattern = true;
        log.Info("RagdollController: knee power-loss forward armed for next death");
    }

    private bool TryBuildKneeFootSupportProxy(BodyHandle footHandle, bool left, out Vector3 target, out Vector3 localOffset)
    {
        target = Vector3.Zero;
        localOffset = Vector3.Zero;
        if (simulation == null) return false;

        var settings = EffectiveKneePowerLossSettings();
        if (!settings.FootProxyEnabled) return false;

        var body = simulation.Bodies.GetBodyReference(footHandle);
        var forward = FlatNormalize(kneeForward, Vector3.UnitZ);
        var forwardOffset = Math.Clamp(settings.FootProxyForwardOffset, -0.05f, 0.25f);
        var downOffset = Math.Clamp(settings.FootProxyDownOffset, 0f, 0.16f);
        var clearance = Math.Clamp(settings.FootProxyGroundClearance, 0.004f, 0.08f);

        // FFXIV's foot/toe bones are not reliable sole vectors; j_asi_d often behaves like an
        // ankle/downward marker. Anchor a virtual sole point in the intended sagittal direction
        // instead, and apply the foot support servo at that local point on the foot body.
        target = body.Pose.Position + forward * forwardOffset - Vector3.UnitY * downOffset;
        target.Y = groundY + clearance;
        localOffset = Vector3.Transform(target - body.Pose.Position, Quaternion.Inverse(body.Pose.Orientation));

        log.Info(
            $"RagdollController: knee foot proxy {(left ? "L" : "R")} " +
            $"target=({target.X:F2},{target.Y:F2},{target.Z:F2}) " +
            $"local=({localOffset.X:F2},{localOffset.Y:F2},{localOffset.Z:F2})");
        return true;
    }

    private bool BeginKneePowerLossForwardPatternCore()
    {
        if (simulation == null || !isActive) return false;

        var leftFoot = FindBodyHandle(Bones.Foot.Left);
        var rightFoot = FindBodyHandle(Bones.Foot.Right);
        var pelvis = FindBodyHandle(Bones.Pelvis);
        var chest = FindBodyHandle(Bones.Chest) ?? FindBodyHandle(Bones.MidSpine);
        var leftKnee = FindBodyHandle(Bones.Shin.Left);
        var rightKnee = FindBodyHandle(Bones.Shin.Right);
        var leftThigh = FindBodyHandle(Bones.Thigh.Left);
        var rightThigh = FindBodyHandle(Bones.Thigh.Right);

        if (!leftFoot.HasValue || !rightFoot.HasValue || !pelvis.HasValue || !chest.HasValue ||
            !leftKnee.HasValue || !rightKnee.HasValue || !leftThigh.HasValue || !rightThigh.HasValue)
        {
            log.Warning("RagdollController: knee power-loss failed - missing leg/core body");
            return false;
        }

        StopDirectedCollapseSpike();
        StopKneePowerLossPattern();

        kneeForward = ResolveForwardDirection();
        kneeRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        kneePowerLossElapsed = 0f;
        kneePowerLossPhaseElapsed = 0f;
        kneePowerLossPhase = KneePowerLossPhase.Buckle;

        var leftFootBody = simulation.Bodies.GetBodyReference(leftFoot.Value);
        var rightFootBody = simulation.Bodies.GetBodyReference(rightFoot.Value);
        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var chestBody = simulation.Bodies.GetBodyReference(chest.Value);

        kneeLeftFootStart = leftFootBody.Pose.Position;
        kneeRightFootStart = rightFootBody.Pose.Position;
        kneeLeftFootLocalOffset = Vector3.Zero;
        kneeRightFootLocalOffset = Vector3.Zero;
        kneePelvisStart = pelvisBody.Pose.Position;
        kneeChestStartRot = chestBody.Pose.Orientation;

        if (TryBuildKneeFootSupportProxy(leftFoot.Value, true, out var leftProxyTarget, out var leftProxyLocal))
        {
            kneeLeftFootStart = leftProxyTarget;
            kneeLeftFootLocalOffset = leftProxyLocal;
        }
        if (TryBuildKneeFootSupportProxy(rightFoot.Value, false, out var rightProxyTarget, out var rightProxyLocal))
        {
            kneeRightFootStart = rightProxyTarget;
            kneeRightFootLocalOffset = rightProxyLocal;
        }

        var leftKneeBody = simulation.Bodies.GetBodyReference(leftKnee.Value);
        var rightKneeBody = simulation.Bodies.GetBodyReference(rightKnee.Value);
        var leftThighBody = simulation.Bodies.GetBodyReference(leftThigh.Value);
        var rightThighBody = simulation.Bodies.GetBodyReference(rightThigh.Value);
        kneeLeftStart = leftKneeBody.Pose.Position;
        kneeRightStart = rightKneeBody.Pose.Position;

        kneeLeftKneeStartTarget = Quaternion.Normalize(Quaternion.Inverse(leftKneeBody.Pose.Orientation) * leftThighBody.Pose.Orientation);
        kneeRightKneeStartTarget = Quaternion.Normalize(Quaternion.Inverse(rightKneeBody.Pose.Orientation) * rightThighBody.Pose.Orientation);
        TryBuildLegAnatomyFrame(Bones.Thigh.Left, Bones.Shin.Left, Bones.Foot.Left, Bones.Toes.Left,
            leftKneeBody.Pose.Orientation, out var leftHingeAxis, out var leftHingeForward);
        TryBuildLegAnatomyFrame(Bones.Thigh.Right, Bones.Shin.Right, Bones.Foot.Right, Bones.Toes.Right,
            rightKneeBody.Pose.Orientation, out var rightHingeAxis, out var rightHingeForward);

        var kneeSettings = EffectiveKneePowerLossSettings();
        var kneeFlexDegrees = Math.Clamp(kneeSettings.KneeFlexDegrees, 0f, 90f);
        kneeLeftKneeFlexTarget = MakeRelativeFlexTarget(leftKneeBody.Pose.Orientation, leftThighBody.Pose.Orientation,
            leftHingeAxis, leftHingeForward, kneeFlexDegrees);
        kneeRightKneeFlexTarget = MakeRelativeFlexTarget(rightKneeBody.Pose.Orientation, rightThighBody.Pose.Orientation,
            rightHingeAxis, rightHingeForward, kneeFlexDegrees);

        log.Info($"RagdollController: knee power-loss axes L axis=({leftHingeAxis.X:F2},{leftHingeAxis.Y:F2},{leftHingeAxis.Z:F2}) fwd=({leftHingeForward.X:F2},{leftHingeForward.Y:F2},{leftHingeForward.Z:F2}); " +
                 $"R axis=({rightHingeAxis.X:F2},{rightHingeAxis.Y:F2},{rightHingeAxis.Z:F2}) fwd=({rightHingeForward.X:F2},{rightHingeForward.Y:F2},{rightHingeForward.Z:F2})");
        LogLegAnatomyDiagnostics("L", Bones.Thigh.Left, Bones.Shin.Left, Bones.Foot.Left, Bones.Toes.Left, leftHingeAxis, leftHingeForward);
        LogLegAnatomyDiagnostics("R", Bones.Thigh.Right, Bones.Shin.Right, Bones.Foot.Right, Bones.Toes.Right, rightHingeAxis, rightHingeForward);

        WakeRagdollBodiesForBiomechanicalSettle();

        kneeLeftFootSupport = simulation.Solver.Add(leftFoot.Value, DirectedLinearServo(kneeLeftFootStart, 0f, 55f, kneeLeftFootLocalOffset));
        kneeRightFootSupport = simulation.Solver.Add(rightFoot.Value, DirectedLinearServo(kneeRightFootStart, 0f, 55f, kneeRightFootLocalOffset));
        kneeLeftFlexBias = simulation.Solver.Add(leftKnee.Value, leftThigh.Value, DirectedRelativeAngularServo(kneeLeftKneeStartTarget, 0f, 18f));
        kneeRightFlexBias = simulation.Solver.Add(rightKnee.Value, rightThigh.Value, DirectedRelativeAngularServo(kneeRightKneeStartTarget, 0f, 18f));
        kneePelvisSupport = simulation.Solver.Add(pelvis.Value, DirectedLinearServo(kneePelvisStart, 0f, 20f));
        kneeChestPitch = simulation.Solver.Add(chest.Value, DirectedAngularServo(kneeChestStartRot, 0f, 18f));

        kneePowerLossActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: knee power-loss forward begun");
        return true;
    }

    private void StopKneePowerLossPatternCore()
    {
        if (simulation != null)
        {
            RemoveDirectedConstraint(ref kneeLeftFootSupport);
            RemoveDirectedConstraint(ref kneeRightFootSupport);
            RemoveDirectedConstraint(ref kneeLeftFlexBias);
            RemoveDirectedConstraint(ref kneeRightFlexBias);
            RemoveDirectedConstraint(ref kneePelvisSupport);
            RemoveDirectedConstraint(ref kneeChestPitch);
        }
        else
        {
            kneeLeftFootSupport = null;
            kneeRightFootSupport = null;
            kneeLeftFlexBias = null;
            kneeRightFlexBias = null;
            kneePelvisSupport = null;
            kneeChestPitch = null;
        }

        if (kneePowerLossActive)
        {
            kneePowerLossActive = false;
            BeginBiomechanicalSettle();
            log.Info("RagdollController: knee power-loss forward stopped");
        }
    }

    private void UpdateKneePowerLossPattern(float dt)
    {
        if (!kneePowerLossActive || simulation == null) return;

        kneePowerLossElapsed += dt;
        kneePowerLossPhaseElapsed += dt;

        var pelvis = FindBodyHandle(Bones.Pelvis);
        var chest = FindBodyHandle(Bones.Chest) ?? FindBodyHandle(Bones.MidSpine);
        if (!pelvis.HasValue || !chest.HasValue) { StopKneePowerLossPattern(); return; }

        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var pelvisDrop = MathF.Max(0f, kneePelvisStart.Y - pelvisBody.Pose.Position.Y);
        var kneeSettings = EffectiveKneePowerLossSettings();
        var kneeNearGround = BoneHeight(Bones.Shin.Left) < groundY + 0.18f || BoneHeight(Bones.Shin.Right) < groundY + 0.18f;
        var minKneeAngle = MeasureCurrentKneePowerLossMinAngle();
        var forwardOffset = MeasureCurrentKneePowerLossForwardOffset();

        if (kneePowerLossPhase == KneePowerLossPhase.Buckle &&
            kneePowerLossPhaseElapsed >= Math.Clamp(kneeSettings.BuckleMinDuration, 0.05f, 1.5f) &&
            (pelvisDrop > Math.Clamp(kneeSettings.BucklePelvisDropToTorso, 0.05f, 1.5f) ||
             minKneeAngle > Math.Clamp(kneeSettings.BuckleKneeAngleToTorso, 1f, 90f) ||
             kneeNearGround ||
             kneePowerLossPhaseElapsed > Math.Clamp(kneeSettings.BuckleTimeout, 0.1f, 3f)))
        {
            log.Info($"RagdollController: knee power-loss buckle complete - drop={pelvisDrop:F2} knee={minKneeAngle:F1} fwd={forwardOffset:F2} nearGround={kneeNearGround}");
            SwitchKneePowerLossPhase(KneePowerLossPhase.TorsoLoss);
        }
        else if (kneePowerLossPhase == KneePowerLossPhase.TorsoLoss &&
                 kneePowerLossPhaseElapsed >= Math.Clamp(kneeSettings.TorsoMinDuration, 0.05f, 2f) &&
                 (forwardOffset > 0.24f || kneePowerLossPhaseElapsed > Math.Clamp(kneeSettings.TorsoTimeout, 0.1f, 3f)))
        {
            log.Info($"RagdollController: knee power-loss torso complete - drop={pelvisDrop:F2} knee={minKneeAngle:F1} fwd={forwardOffset:F2}");
            SwitchKneePowerLossPhase(KneePowerLossPhase.Release);
        }

        switch (kneePowerLossPhase)
        {
            case KneePowerLossPhase.Buckle:
                UpdateKneeBucklePhase(pelvisDrop);
                break;
            case KneePowerLossPhase.TorsoLoss:
                UpdateKneeTorsoLossPhase();
                break;
            case KneePowerLossPhase.Release:
                StopKneePowerLossPattern();
                break;
        }
    }

    private void UpdateKneeBucklePhase(float pelvisDrop)
    {
        var t = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.75f, 0f, 1f));
        var supportGain = 1f - 0.45f * t;
        var flexGain = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.55f, 0f, 1f));
        var kneeSettings = EffectiveKneePowerLossSettings();

        ApplySoftFootSupport(kneeLeftFootSupport, kneeLeftFootStart, kneeLeftFootLocalOffset, supportGain, Math.Clamp(kneeSettings.BuckleFootSupportForce, 0f, 5000f));
        ApplySoftFootSupport(kneeRightFootSupport, kneeRightFootStart, kneeRightFootLocalOffset, supportGain, Math.Clamp(kneeSettings.BuckleFootSupportForce, 0f, 5000f));

        ApplyKneeLateralStability(5.5f, 0.45f);

        var flexForce = Math.Clamp(kneeSettings.KneeBuckleFlexForce, 0f, 500f);
        ApplyDirectedRelativeAngular(kneeLeftFlexBias, Quaternion.Slerp(kneeLeftKneeStartTarget, kneeLeftKneeFlexTarget, flexGain), flexForce * flexGain, 14f);
        ApplyDirectedRelativeAngular(kneeRightFlexBias, Quaternion.Slerp(kneeRightKneeStartTarget, kneeRightKneeFlexTarget, flexGain), flexForce * flexGain, 14f);

        var needMoreDrop = pelvisDrop < 0.22f ? 1f : pelvisDrop < 0.38f ? 0.45f : 0.1f;
        var target = kneePelvisStart
            + kneeForward * (0.10f + 0.18f * t)
            - Vector3.UnitY * (0.28f + 0.18f * t);
        ApplyDirectedLinear(kneePelvisSupport, target, Math.Clamp(kneeSettings.BucklePelvisForce, 0f, 3000f) * needMoreDrop, 14f);
    }

    private void UpdateKneeTorsoLossPhase()
    {
        var t = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.65f, 0f, 1f));
        var supportGain = 1f - t;
        var kneeSettings = EffectiveKneePowerLossSettings();

        ApplySoftFootSupport(kneeLeftFootSupport, kneeLeftFootStart, kneeLeftFootLocalOffset, supportGain, Math.Clamp(kneeSettings.TorsoFootSupportForce, 0f, 5000f));
        ApplySoftFootSupport(kneeRightFootSupport, kneeRightFootStart, kneeRightFootLocalOffset, supportGain, Math.Clamp(kneeSettings.TorsoFootSupportForce, 0f, 5000f));
        ApplyKneeLateralStability(3.0f * supportGain, 0.32f);
        var flexForce = Math.Clamp(kneeSettings.KneeTorsoFlexForce, 0f, 500f);
        ApplyDirectedRelativeAngular(kneeLeftFlexBias, kneeLeftKneeFlexTarget, flexForce * supportGain, 10f);
        ApplyDirectedRelativeAngular(kneeRightFlexBias, kneeRightKneeFlexTarget, flexForce * supportGain, 10f);

        var chestPitch = Math.Clamp(kneeSettings.ChestPitchDegrees, -90f, 90f) * MathF.PI / 180f;
        var chestTarget = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(kneeRight, chestPitch * t) * kneeChestStartRot);
        ApplyDirectedAngular(kneeChestPitch, chestTarget, 260f * (1f - 0.45f * t), 18f);

        var pelvisTarget = kneePelvisStart + kneeForward * (0.25f + 0.18f * t) - Vector3.UnitY * 0.46f;
        ApplyDirectedLinear(kneePelvisSupport, pelvisTarget, Math.Clamp(kneeSettings.TorsoPelvisForce, 0f, 3000f) * supportGain, 10f);
    }

    private void ApplySoftFootSupport(ConstraintHandle? handle, Vector3 start, Vector3 localOffset, float gain, float force)
    {
        var target = start;
        var settings = EffectiveKneePowerLossSettings();
        var clearance = settings.FootProxyEnabled
            ? Math.Clamp(settings.FootProxyGroundClearance, 0.004f, 0.08f)
            : 0.02f;
        target.Y = groundY + MathF.Max(clearance, start.Y - groundY);
        ApplyDirectedLinear(handle, target, force * Math.Clamp(gain, 0f, 1f), 38f, localOffset);
    }

    private void ApplyKneeLateralStability(float gain, float maxSpeed)
    {
        if (simulation == null || gain <= 0f) return;
        var leftKnee = FindBodyHandle(Bones.Shin.Left);
        var rightKnee = FindBodyHandle(Bones.Shin.Right);
        if (!leftKnee.HasValue || !rightKnee.HasValue) return;

        var lateral = FlatNormalize(kneeRight, Vector3.UnitX);
        var leftBody = simulation.Bodies.GetBodyReference(leftKnee.Value);
        var rightBody = simulation.Bodies.GetBodyReference(rightKnee.Value);
        var mid = (leftBody.Pose.Position + rightBody.Pose.Position) * 0.5f;
        var startMid = (kneeLeftStart + kneeRightStart) * 0.5f;

        var leftOffset = Vector3.Dot(kneeLeftStart - startMid, lateral);
        var rightOffset = Vector3.Dot(kneeRightStart - startMid, lateral);
        var leftTarget = mid + lateral * leftOffset;
        var rightTarget = mid + lateral * rightOffset;

        ApplyKneeLateralVelocity(leftBody, lateral, Vector3.Dot(leftTarget - leftBody.Pose.Position, lateral), gain, maxSpeed);
        ApplyKneeLateralVelocity(rightBody, lateral, Vector3.Dot(rightTarget - rightBody.Pose.Position, lateral), gain, maxSpeed);
    }

    private static void ApplyKneeLateralVelocity(BodyReference body, Vector3 lateral, float error, float gain, float maxSpeed)
    {
        var velocity = Math.Clamp(error * gain, -maxSpeed, maxSpeed);
        var existing = Vector3.Dot(body.Velocity.Linear, lateral);
        // Only correct the inward/outward spacing error. Leave vertical drop and forward kneel motion free.
        body.Velocity.Linear += lateral * (velocity - existing * 0.35f);
    }

    private void SwitchKneePowerLossPhase(KneePowerLossPhase phase)
    {
        kneePowerLossPhase = phase;
        kneePowerLossPhaseElapsed = 0f;
        log.Info($"RagdollController: knee power-loss phase -> {phase}");
    }

    private float BoneHeight(string boneName)
    {
        if (simulation == null) return float.PositiveInfinity;
        return TryGetBoneOriginPosition(boneName, out var position)
            ? position.Y
            : float.PositiveInfinity;
    }

    private float MeasureCurrentKneePowerLossMinAngle()
    {
        var left = MeasureLegBendAngle(Bones.Thigh.Left, Bones.Shin.Left, Bones.Foot.Left, out _);
        var right = MeasureLegBendAngle(Bones.Thigh.Right, Bones.Shin.Right, Bones.Foot.Right, out _);
        if (left < 0f && right < 0f) return 0f;
        if (left < 0f) return right;
        if (right < 0f) return left;
        return MathF.Min(left, right);
    }

    private float MeasureCurrentKneePowerLossForwardOffset()
    {
        if (simulation == null) return 0f;
        var pelvis = FindBodyHandle(Bones.Pelvis);
        if (!pelvis.HasValue) return 0f;

        var pelvisPos = simulation.Bodies.GetBodyReference(pelvis.Value).Pose.Position;
        var footCenter = (kneeLeftFootStart + kneeRightFootStart) * 0.5f;
        return Vector3.Dot(pelvisPos - footCenter, kneeForward);
    }

    private void LogLegAnatomyDiagnostics(string side, string thighBone, string shinBone, string calfBone, string footBone,
        Vector3 currentHingeAxis, Vector3 currentHingeForward)
    {
        if (simulation == null) return;
        if (!TryGetBoneOriginPosition(thighBone, out var hip) ||
            !TryGetBoneOriginPosition(shinBone, out var knee) ||
            !TryGetBoneOriginPosition(calfBone, out var ankle) ||
            !TryGetBoneOriginPosition(footBone, out var foot))
            return;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        var footDir = NormalizeOrFallback(foot - ankle, kneeForward);
        var rawAxis = Vector3.Cross(thigh, shin);
        var geomAxis = rawAxis.LengthSquared() > 0.0005f
            ? Vector3.Normalize(rawAxis)
            : NormalizeOrFallback(Vector3.Cross(kneeForward, shin), currentHingeAxis);
        if (Vector3.Dot(geomAxis, currentHingeAxis) < 0)
            geomAxis = -geomAxis;

        var geomForward = NormalizeOrFallback(ProjectOntoPlane(kneeForward, geomAxis), currentHingeForward);
        var footForward = NormalizeOrFallback(ProjectOntoPlane(footDir, geomAxis), geomForward);
        if (Vector3.Dot(geomForward, currentHingeForward) < 0)
            geomForward = -geomForward;

        var thighShinAngle = MathF.Acos(Math.Clamp(Vector3.Dot(thigh, shin), -1f, 1f)) * 180f / MathF.PI;
        var frameAxisDot = Vector3.Dot(geomAxis, currentHingeAxis);
        var frameForwardDot = Vector3.Dot(geomForward, currentHingeForward);

        log.Info(
            $"RagdollController: leg anatomy {side} " +
            $"hip=({hip.X:F2},{hip.Y:F2},{hip.Z:F2}) knee=({knee.X:F2},{knee.Y:F2},{knee.Z:F2}) ankle=({ankle.X:F2},{ankle.Y:F2},{ankle.Z:F2}) foot=({foot.X:F2},{foot.Y:F2},{foot.Z:F2}) " +
            $"thigh=({thigh.X:F2},{thigh.Y:F2},{thigh.Z:F2}) shin=({shin.X:F2},{shin.Y:F2},{shin.Z:F2}) footDir=({footDir.X:F2},{footDir.Y:F2},{footDir.Z:F2}) " +
            $"geomAxis=({geomAxis.X:F2},{geomAxis.Y:F2},{geomAxis.Z:F2}) geomFwd=({geomForward.X:F2},{geomForward.Y:F2},{geomForward.Z:F2}) footFwd=({footForward.X:F2},{footForward.Y:F2},{footForward.Z:F2}) " +
            $"angle={thighShinAngle:F1} axisDot={frameAxisDot:F2} fwdDot={frameForwardDot:F2}");
    }

    private bool TryBuildLegAnatomyFrame(string thighBone, string shinBone, string calfBone, string footBone,
        Quaternion childBodyRot, out Vector3 hingeAxis, out Vector3 flexForward)
    {
        hingeAxis = Vector3.Transform(Vector3.UnitX, childBodyRot);
        flexForward = Vector3.Transform(Vector3.UnitZ, childBodyRot);

        if (!TryGetBoneOriginPosition(thighBone, out var hip) ||
            !TryGetBoneOriginPosition(shinBone, out var knee) ||
            !TryGetBoneOriginPosition(calfBone, out var ankle) ||
            !TryGetBoneOriginPosition(footBone, out var foot))
            return false;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        var footDir = NormalizeOrFallback(foot - ankle, kneeForward);

        // Tier B — anatomy-fixed knee hinge axis. The skeleton medial-lateral (character
        // RIGHT, = kneeRight) axis projected perpendicular to the shin is stable regardless
        // of how bent the leg is, so both legs resolve to near-mirror axes (~±character-
        // right) instead of the Cross(thigh,shin) result that goes degenerate (and pointed
        // FORWARD for one knee) when the leg is near-straight at death.
        var anatomicalAxis = ProjectOntoPlane(kneeRight, shin);
        if (anatomicalAxis.LengthSquared() > 0.0005f)
        {
            hingeAxis = Vector3.Normalize(anatomicalAxis);
        }
        else
        {
            var rawAxis = Vector3.Cross(thigh, shin);
            if (rawAxis.LengthSquared() > 0.0005f)
            {
                hingeAxis = Vector3.Normalize(rawAxis);
            }
            else
            {
                var footProjected = ProjectOntoPlane(footDir, shin);
                if (footProjected.LengthSquared() > 0.0005f)
                    hingeAxis = NormalizeOrFallback(Vector3.Cross(footProjected, shin), hingeAxis);
                else
                    hingeAxis = NormalizeOrFallback(Vector3.Cross(kneeForward, shin), hingeAxis);
            }
        }

        // The flexion direction is the character sagittal direction projected into the knee
        // hinge plane. Do NOT use j_asi_d as "foot forward": in FFXIV this bone origin is often
        // mostly below the ankle, so ankle->foot points downward and drives the knee into a fake
        // vertical/sideways fold.
        var sagittalForward = ProjectOntoPlane(kneeForward, hingeAxis);
        var bodyForward = ProjectOntoPlane(Vector3.Transform(Vector3.UnitZ, childBodyRot), hingeAxis);
        flexForward = NormalizeOrFallback(sagittalForward, NormalizeOrFallback(bodyForward, kneeForward));

        return true;
    }

    private bool TryGetBoneOriginPosition(string boneName, out Vector3 position)
    {
        position = Vector3.Zero;
        if (simulation == null) return false;
        RagdollBone? bone = null;
        foreach (var rb in ragdollBones)
        {
            if (rb.Name == boneName)
            {
                bone = rb;
                break;
            }
        }
        if (!bone.HasValue) return false;

        var body = simulation.Bodies.GetBodyReference(bone.Value.BodyHandle);
        var yAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        var profileOrigin = body.Pose.Position -
                            Vector3.Transform(bone.Value.ShapeCenterOffset, body.Pose.Orientation);
        position = profileOrigin - yAxis * bone.Value.SegmentHalfLength;
        return true;
    }

    private static Quaternion MakeRelativeFlexTarget(Quaternion childOrientation, Quaternion parentOrientation,
        Vector3 flexAxisWorld, Vector3 flexForwardWorld, float degrees)
    {
        if (flexAxisWorld.LengthSquared() < 0.0001f) flexAxisWorld = Vector3.UnitX;
        if (flexForwardWorld.LengthSquared() < 0.0001f) flexForwardWorld = Vector3.UnitZ;

        var axis = Vector3.Normalize(flexAxisWorld);
        var forward = Vector3.Normalize(flexForwardWorld);
        var radians = degrees * (MathF.PI / 180f);

        Quaternion bestChild = childOrientation;
        var bestDot = float.NegativeInfinity;
        for (int i = 0; i < 2; i++)
        {
            var sign = i == 0 ? 1f : -1f;
            var flexWorld = Quaternion.CreateFromAxisAngle(axis, radians * sign);
            var candidateChild = Quaternion.Normalize(flexWorld * childOrientation);
            var candidateAxis = Vector3.Transform(Vector3.UnitY, candidateChild);
            var dot = Vector3.Dot(candidateAxis, forward);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestChild = candidateChild;
            }
        }

        return Quaternion.Normalize(Quaternion.Inverse(bestChild) * parentOrientation);
    }
}
