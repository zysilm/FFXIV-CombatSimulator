// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.CollapseProfiles;
using BepuPhysics;
using BepuPhysics.Constraints;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // --- Death-collapse spike (experimental proof-of-concept) ---
    // Active-ragdoll spike: instead of letting the corpse go instantly limp, snapshot the
    // current ("death-instant") relative orientation of every joint and pin it with an
    // AngularServo ("muscle tone"), then fade each joint's strength toward zero so gravity
    // drives a controlled collapse. The per-tier fade order/curve decides the collapse style.
    // NOTE: this reuses the AngularServo *mechanism* only and is tuned from scratch — it
    // inherits nothing from (and proves nothing about) the poor soft-body bones.
    // Knee buckle on death (Ragdoll page > Guided Collapse): the knee power-loss pattern, entered
    // through the stance-conditioning pre-pass when the death pose needs it.
    private void ArmConfiguredGuidedCollapse()
    {
        if (settings.GuidedCollapse.Enabled)
            RequestEntryConditionedKneePowerLossOnReady();
    }

    public bool DirectedCollapseSpikeActive => directedCollapseActive;

    private void RequestDirectedKneelPitchOnReadyCore(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        if (IsSimulationReady)
        {
            BeginDirectedKneelPitchSpike(duration, footForce, pelvisForce, drop, forward, pitchDegrees);
            return;
        }

        pendingDirectedDuration = duration;
        pendingDirectedFootForce = footForce;
        pendingDirectedPelvisForce = pelvisForce;
        pendingDirectedDrop = drop;
        pendingDirectedForward = forward;
        pendingDirectedPitchDegrees = pitchDegrees;
        pendingDirectedProfileId = string.Empty;
        pendingDirectedCollapseSpike = true;
        log.Info("RagdollController: directed kneel-pitch armed for next death");
    }

    private void RequestProfileDirectedCollapseOnReadyCore(string profileId)
    {
        if (IsSimulationReady)
        {
            BeginProfileDirectedCollapseSpike(profileId);
            return;
        }

        pendingDirectedProfileId = profileId;
        pendingDirectedCollapseSpike = true;
        log.Info($"RagdollController: directed collapse profile armed for next death - profile={profileId}");
    }

    private bool BeginProfileDirectedCollapseSpikeCore(string profileId)
    {
        var profile = collapseProfileBook.FindById(profileId);
        if (profile == null)
        {
            log.Warning($"RagdollController: directed collapse profile not found - profile={profileId}");
            return false;
        }

        return BeginProfileDirectedCollapseSpike(profile);
    }

    private bool BeginProfileDirectedCollapseSpike(CollapseProfile profile)
    {
        if (simulation == null || !isActive || profile.Phases.Count == 0) return false;

        StopDirectedCollapseSpike();

        directedProfile = profile;
        directedPhaseIndex = 0;
        directedPhaseElapsed = 0f;
        directedElapsed = 0f;
        directedDuration = 0f;
        foreach (var phase in profile.Phases)
            directedDuration += MathF.Max(0.01f, phase.Duration);

        directedForward = ResolveForwardDirection();
        directedRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        var pelvis = FindBodyHandle(Bones.Pelvis);
        if (pelvis.HasValue)
        {
            var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
            directedPelvisStart = pelvisBody.Pose.Position;
            directedPelvisStartRot = pelvisBody.Pose.Orientation;
        }

        var chest = FindBodyHandle(Bones.Chest) ?? FindBodyHandle(Bones.MidSpine);
        if (chest.HasValue)
            directedChestStartRot = simulation.Bodies.GetBodyReference(chest.Value).Pose.Orientation;

        var leftFoot = FindBodyHandle(Bones.Foot.Left);
        if (leftFoot.HasValue)
            directedLeftFootTarget = simulation.Bodies.GetBodyReference(leftFoot.Value).Pose.Position;
        var rightFoot = FindBodyHandle(Bones.Foot.Right);
        if (rightFoot.HasValue)
            directedRightFootTarget = simulation.Bodies.GetBodyReference(rightFoot.Value).Pose.Position;

        WakeRagdollBodiesForBiomechanicalSettle();
        EnsureProfileConstraints(profile);

        directedCollapseActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: directed collapse profile begun - profile={profile.Id} phases={profile.Phases.Count} duration={directedDuration:F2}");
        return true;
    }

    private bool BeginDirectedKneelPitchSpikeCore(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        if (simulation == null || !isActive) return false;

        var leftFoot = FindBodyHandle(Bones.Foot.Left);
        var rightFoot = FindBodyHandle(Bones.Foot.Right);
        var pelvis = FindBodyHandle(Bones.Pelvis);
        var chest = FindBodyHandle(Bones.Chest) ?? FindBodyHandle(Bones.MidSpine);
        if (!leftFoot.HasValue || !rightFoot.HasValue || !pelvis.HasValue || !chest.HasValue)
        {
            log.Warning("RagdollController: directed kneel-pitch failed - missing foot/pelvis/chest body");
            return false;
        }

        StopDirectedCollapseSpike();

        directedProfile = null;
        directedPhaseIndex = 0;
        directedPhaseElapsed = 0f;
        directedDuration = Math.Clamp(duration, 0.25f, 4f);
        directedFootForce = Math.Clamp(footForce, 50f, 10000f);
        directedPelvisForce = Math.Clamp(pelvisForce, 50f, 10000f);
        directedDrop = Math.Clamp(drop, 0.05f, 1.5f);
        directedForwardDistance = Math.Clamp(forward, 0f, 2f);
        directedPitchRadians = Math.Clamp(pitchDegrees, -90f, 90f) * (MathF.PI / 180f);
        directedElapsed = 0f;

        directedForward = ResolveForwardDirection();
        directedRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        var leftBody = simulation.Bodies.GetBodyReference(leftFoot.Value);
        var rightBody = simulation.Bodies.GetBodyReference(rightFoot.Value);
        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var chestBody = simulation.Bodies.GetBodyReference(chest.Value);

        directedLeftFootTarget = leftBody.Pose.Position;
        directedRightFootTarget = rightBody.Pose.Position;
        directedPelvisStart = pelvisBody.Pose.Position;
        directedPelvisStartRot = pelvisBody.Pose.Orientation;
        directedChestStartRot = chestBody.Pose.Orientation;

        WakeRagdollBodiesForBiomechanicalSettle();

        directedLeftFootConstraint = simulation.Solver.Add(leftFoot.Value, DirectedLinearServo(directedLeftFootTarget, directedFootForce, 90f));
        directedRightFootConstraint = simulation.Solver.Add(rightFoot.Value, DirectedLinearServo(directedRightFootTarget, directedFootForce, 90f));
        directedPelvisLinearConstraint = simulation.Solver.Add(pelvis.Value, DirectedLinearServo(directedPelvisStart, directedPelvisForce, 45f));
        directedPelvisAngularConstraint = simulation.Solver.Add(pelvis.Value, DirectedAngularServo(directedPelvisStartRot, directedPelvisForce * 0.25f, 20f));
        directedChestAngularConstraint = simulation.Solver.Add(chest.Value, DirectedAngularServo(directedChestStartRot, directedPelvisForce * 0.35f, 25f));

        directedCollapseActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: directed kneel-pitch begun - duration={directedDuration:F2} footForce={directedFootForce:F0} pelvisForce={directedPelvisForce:F0} drop={directedDrop:F2} forward={directedForwardDistance:F2} pitch={pitchDegrees:F0}");
        return true;
    }

    private void StopDirectedCollapseSpikeCore()
    {
        if (simulation != null)
        {
            RemoveDirectedConstraint(ref directedLeftFootConstraint);
            RemoveDirectedConstraint(ref directedRightFootConstraint);
            RemoveDirectedConstraint(ref directedPelvisLinearConstraint);
            RemoveDirectedConstraint(ref directedPelvisAngularConstraint);
            RemoveDirectedConstraint(ref directedChestAngularConstraint);
        }
        else
        {
            directedLeftFootConstraint = null;
            directedRightFootConstraint = null;
            directedPelvisLinearConstraint = null;
            directedPelvisAngularConstraint = null;
            directedChestAngularConstraint = null;
        }

        if (directedCollapseActive)
        {
            directedCollapseActive = false;
            directedProfile = null;
            directedPhaseIndex = 0;
            directedPhaseElapsed = 0f;
            BeginBiomechanicalSettle();
            log.Info("RagdollController: directed kneel-pitch stopped");
        }
    }

    private void UpdateDirectedCollapseSpike(float dt)
    {
        if (!directedCollapseActive || simulation == null) return;
        if (directedProfile != null)
        {
            UpdateProfileDirectedCollapseSpike(dt);
            return;
        }

        directedElapsed += dt;

        var t = Math.Clamp(directedElapsed / directedDuration, 0f, 1f);
        var plantGain = 1f - SmoothStep(Math.Clamp((t - 0.70f) / 0.25f, 0f, 1f));
        var driveGain = 1f - SmoothStep(Math.Clamp((t - 0.78f) / 0.20f, 0f, 1f));
        var buckle = SmoothStep(Math.Clamp(t / 0.62f, 0f, 1f));
        var pitch = SmoothStep(Math.Clamp((t - 0.32f) / 0.46f, 0f, 1f));

        var pelvisTarget = directedPelvisStart
            + directedForward * (directedForwardDistance * buckle)
            - Vector3.UnitY * (directedDrop * buckle);

        var pelvisTargetRot = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(directedRight, directedPitchRadians * 0.35f * pitch) * directedPelvisStartRot);
        var chestTargetRot = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(directedRight, directedPitchRadians * pitch) * directedChestStartRot);

        ApplyDirectedLinear(directedLeftFootConstraint, directedLeftFootTarget, directedFootForce * plantGain, 90f);
        ApplyDirectedLinear(directedRightFootConstraint, directedRightFootTarget, directedFootForce * plantGain, 90f);
        ApplyDirectedLinear(directedPelvisLinearConstraint, pelvisTarget, directedPelvisForce * driveGain, 45f);
        ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot, directedPelvisForce * 0.25f * driveGain, 20f);
        ApplyDirectedAngular(directedChestAngularConstraint, chestTargetRot, directedPelvisForce * 0.35f * driveGain, 25f);

        if (directedElapsed >= directedDuration)
            StopDirectedCollapseSpike();
    }

    private void UpdateProfileDirectedCollapseSpike(float dt)
    {
        if (directedProfile == null || simulation == null) return;
        if (directedPhaseIndex >= directedProfile.Phases.Count)
        {
            StopDirectedCollapseSpike();
            return;
        }

        directedElapsed += dt;
        directedPhaseElapsed += dt;

        var phase = directedProfile.Phases[directedPhaseIndex];
        var phaseDuration = MathF.Max(0.01f, phase.Duration);
        var phaseT = Math.Clamp(directedPhaseElapsed / phaseDuration, 0f, 1f);

        foreach (var controller in phase.Controllers)
            ApplyProfileController(controller, phaseT);

        if (directedPhaseElapsed >= phaseDuration)
        {
            directedPhaseIndex++;
            directedPhaseElapsed = 0f;
            if (directedPhaseIndex >= directedProfile.Phases.Count)
                StopDirectedCollapseSpike();
        }
    }

    private void EnsureProfileConstraints(CollapseProfile profile)
    {
        foreach (var phase in profile.Phases)
        foreach (var controller in phase.Controllers)
        {
            switch (controller.Type)
            {
                case "footPlant":
                    EnsureFootPlantConstraints(controller);
                    break;
                case "pelvisDrop":
                    EnsurePelvisLinearConstraint(controller);
                    EnsurePelvisAngularConstraint(controller);
                    break;
                case "torsoPitch":
                    EnsureChestAngularConstraint(controller);
                    EnsurePelvisAngularConstraint(controller);
                    break;
            }
        }
    }

    private void ApplyProfileController(CollapseControllerProfile controller, float phaseT)
    {
        var localT = ControllerLocalT(controller, phaseT);
        var eased = SmoothStep(localT);
        var strength = Lerp(controller.StrengthFrom, controller.StrengthTo, eased);

        switch (controller.Type)
        {
            case "footPlant":
                ApplyDirectedLinear(directedLeftFootConstraint, directedLeftFootTarget,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 90f);
                ApplyDirectedLinear(directedRightFootConstraint, directedRightFootTarget,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 90f);
                break;

            case "pelvisDrop":
            {
                var direction = ResolveProfileDirection(controller.Direction);
                var drop = Lerp(controller.DropFrom, controller.Drop, eased);
                var distance = Lerp(controller.DistanceFrom, controller.Distance, eased);
                var target = directedPelvisStart
                    + direction * distance
                    - Vector3.UnitY * drop;
                ApplyDirectedLinear(directedPelvisLinearConstraint, target,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 45f);

                var pelvisPitch = Lerp(controller.PitchDegreesFrom, controller.PitchDegrees, eased)
                    * controller.PelvisPitchScale * (MathF.PI / 180f);
                var pelvisTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pelvisPitch) * directedPelvisStartRot);
                ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot,
                    controller.Force * 0.25f * strength, 20f);
                break;
            }

            case "torsoPitch":
            {
                var pitchDegrees = Lerp(controller.PitchDegreesFrom, controller.PitchDegrees, eased);
                var pitch = pitchDegrees * (MathF.PI / 180f);
                var chestTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pitch) * directedChestStartRot);
                ApplyDirectedAngular(directedChestAngularConstraint, chestTargetRot,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 25f);

                var pelvisPitch = pitchDegrees * controller.PelvisPitchScale * (MathF.PI / 180f);
                var pelvisTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pelvisPitch) * directedPelvisStartRot);
                ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot,
                    controller.Force * 0.45f * strength, 20f);
                break;
            }

            case "releaseToPassive":
                StopDirectedCollapseSpike();
                break;
        }
    }

    private static OneBodyLinearServo DirectedLinearServo(Vector3 target, float force, float freq)
        => DirectedLinearServo(target, force, freq, Vector3.Zero);

    private static OneBodyLinearServo DirectedLinearServo(Vector3 target, float force, float freq, Vector3 localOffset) => new()
    {
        LocalOffset = localOffset,
        Target = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private static OneBodyAngularServo DirectedAngularServo(Quaternion target, float force, float freq) => new()
    {
        TargetOrientation = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private static AngularServo DirectedRelativeAngularServo(Quaternion target, float force, float freq) => new()
    {
        TargetRelativeRotationLocalA = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private void EnsureFootPlantConstraints(CollapseControllerProfile controller)
    {
        if (simulation == null) return;

        foreach (var bone in controller.Bones)
        {
            var handle = FindBodyHandle(bone);
            if (!handle.HasValue) continue;

            if (bone.EndsWith(HumanoidSkeleton.SideSuffixes[0], StringComparison.Ordinal)) // the left side
            {
                if (!directedLeftFootConstraint.HasValue)
                {
                    directedLeftFootTarget = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
                    directedLeftFootConstraint = simulation.Solver.Add(handle.Value,
                        DirectedLinearServo(directedLeftFootTarget, 0f, controller.Frequency > 0 ? controller.Frequency : 90f));
                }
            }
            else if (bone.EndsWith(HumanoidSkeleton.SideSuffixes[1], StringComparison.Ordinal)) // the right side
            {
                if (!directedRightFootConstraint.HasValue)
                {
                    directedRightFootTarget = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
                    directedRightFootConstraint = simulation.Solver.Add(handle.Value,
                        DirectedLinearServo(directedRightFootTarget, 0f, controller.Frequency > 0 ? controller.Frequency : 90f));
                }
            }
        }
    }

    private void EnsurePelvisLinearConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedPelvisLinearConstraint.HasValue) return;
        var handle = FindBodyHandle(string.IsNullOrWhiteSpace(controller.Bone) ? Bones.Pelvis : controller.Bone);
        if (!handle.HasValue) return;
        directedPelvisStart = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
        directedPelvisLinearConstraint = simulation.Solver.Add(handle.Value,
            DirectedLinearServo(directedPelvisStart, 0f, controller.Frequency > 0 ? controller.Frequency : 45f));
    }

    private void EnsurePelvisAngularConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedPelvisAngularConstraint.HasValue) return;
        var handle = FindBodyHandle(Bones.Pelvis);
        if (!handle.HasValue) return;
        directedPelvisStartRot = simulation.Bodies.GetBodyReference(handle.Value).Pose.Orientation;
        directedPelvisAngularConstraint = simulation.Solver.Add(handle.Value,
            DirectedAngularServo(directedPelvisStartRot, 0f, 20f));
    }

    private void EnsureChestAngularConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedChestAngularConstraint.HasValue) return;
        var handle = FindBodyHandle(string.IsNullOrWhiteSpace(controller.Bone) ? Bones.Chest : controller.Bone)
            ?? FindBodyHandle(Bones.MidSpine);
        if (!handle.HasValue) return;
        directedChestStartRot = simulation.Bodies.GetBodyReference(handle.Value).Pose.Orientation;
        directedChestAngularConstraint = simulation.Solver.Add(handle.Value,
            DirectedAngularServo(directedChestStartRot, 0f, controller.Frequency > 0 ? controller.Frequency : 25f));
    }

    private Vector3 ResolveProfileDirection(string direction)
    {
        return direction.Equals("backward", StringComparison.OrdinalIgnoreCase) ? -directedForward :
               direction.Equals("left", StringComparison.OrdinalIgnoreCase) ? -directedRight :
               direction.Equals("right", StringComparison.OrdinalIgnoreCase) ? directedRight :
               direction.Equals("sideways", StringComparison.OrdinalIgnoreCase) ? (random.Next(RandomSite.DirectedCollapseSideways, 2) == 0 ? -directedRight : directedRight) :
               directedForward;
    }

    private static float ControllerLocalT(CollapseControllerProfile controller, float phaseT)
    {
        var start = Math.Clamp(controller.StartTime, 0f, 1f);
        var end = Math.Clamp(controller.EndTime, 0f, 1f);
        if (end <= start) end = start + 0.0001f;
        return Math.Clamp((phaseT - start) / (end - start), 0f, 1f);
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private void ApplyDirectedLinear(ConstraintHandle? handle, Vector3 target, float force, float freq)
        => ApplyDirectedLinear(handle, target, force, freq, Vector3.Zero);

    private void ApplyDirectedLinear(ConstraintHandle? handle, Vector3 target, float force, float freq, Vector3 localOffset)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedLinearServo(target, force, freq, localOffset)); } catch { }
    }

    private void ApplyDirectedAngular(ConstraintHandle? handle, Quaternion target, float force, float freq)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedAngularServo(target, force, freq)); } catch { }
    }

    private void ApplyDirectedRelativeAngular(ConstraintHandle? handle, Quaternion target, float force, float freq)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedRelativeAngularServo(target, force, freq)); } catch { }
    }

    private void RemoveDirectedConstraint(ref ConstraintHandle? handle)
    {
        if (!handle.HasValue || simulation == null) { handle = null; return; }
        try { simulation.Solver.Remove(handle.Value); } catch { }
        handle = null;
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // The character's facing (the profile's forward axis), flattened onto the ground plane.
    private Vector3 ResolveForwardDirection()
        => FlatNormalize(Vector3.Transform(HumanoidSkeleton.Forward, skelWorldRot), HumanoidSkeleton.Forward);

    private static Vector3 FlatNormalize(Vector3 v, Vector3 fallback)
    {
        v.Y = 0f;
        return v.LengthSquared() > 0.0001f ? Vector3.Normalize(v) : fallback;
    }

    private bool TryGetSupportPoint(bool left, out Vector3 point)
    {
        point = Vector3.Zero;
        if (simulation == null) return false;

        var names = new[]
        {
            Bones.Toes.For(left),
            Bones.Foot.For(left),
            Bones.Calf.For(left),
        };

        var found = false;
        var bestY = float.PositiveInfinity;
        foreach (var name in names)
        {
            var handle = FindBodyHandle(name);
            if (!handle.HasValue) continue;
            var pos = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
            if (!found || pos.Y < bestY)
            {
                found = true;
                bestY = pos.Y;
                point = pos;
            }
        }
        return found;
    }

    private void ApplyCoreAxisVelocity(string boneName, Vector3 axis, float targetSpeed, float maxDelta)
    {
        if (simulation == null || axis.LengthSquared() < 0.0001f) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        var n = Vector3.Normalize(axis);
        var current = Vector3.Dot(body.Velocity.Linear, n);
        var delta = Math.Clamp(targetSpeed - current, -maxDelta, maxDelta);
        body.Velocity.Linear += n * delta;
        body.Awake = true;
    }

    // --- Collapse entry conditioning ---
    // Common pre-pass for narrow upright death poses. When the stance is too closed or the knees
    // are locked nearly straight, downstream collapse profiles inherit an unstable support base
    // and tend to fold the knees inward. This pre-pass briefly opens the support geometry and
    // starts a small pelvis drop before handing off to the requested collapse pattern.
    private bool pendingEntryConditionedKneePowerLoss;
    private bool entryConditioningActive;
    private float entryConditioningElapsed;
    private Func<bool>? entryConditioningContinuation;
    private Vector3 entryForward;
    private Vector3 entryRight;
    private Vector3 entryLeftKneeStart;
    private Vector3 entryRightKneeStart;
    private float entryStanceWidth;
    private float entryLeftKneeAngle;
    private float entryRightKneeAngle;
    private bool entryWasNeeded;

    private void RequestEntryConditionedKneePowerLossOnReadyCore()
    {
        var knee = EffectiveKneePowerLossSettings();
        if (!knee.EntryConditioningEnabled)
        {
            RequestKneePowerLossForwardOnReady();
            return;
        }

        if (IsSimulationReady)
        {
            if (!BeginCollapseEntryConditioning(BeginKneePowerLossForwardPattern))
                BeginKneePowerLossForwardPattern();
            return;
        }

        pendingEntryConditionedKneePowerLoss = true;
        log.Info("RagdollController: entry-conditioned knee power-loss armed for next death");
    }

    private bool BeginCollapseEntryConditioning(Func<bool> continuation)
    {
        if (simulation == null || !isActive) return false;

        if (!TryMeasureEntryPose(out entryStanceWidth, out entryLeftKneeAngle, out entryRightKneeAngle,
                out entryLeftKneeStart, out entryRightKneeStart))
        {
            log.Warning("RagdollController: entry conditioning skipped - missing leg pose data");
            return false;
        }

        entryForward = ResolveForwardDirection();
        entryRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        var knee = EffectiveKneePowerLossSettings();
        var straightestKnee = MathF.Min(entryLeftKneeAngle, entryRightKneeAngle);
        entryWasNeeded = entryStanceWidth < Math.Clamp(knee.EntryStanceThreshold, 0.05f, 1.0f) ||
                         straightestKnee < Math.Clamp(knee.EntryReadyKneeAngle, 1f, 60f);
        if (!entryWasNeeded)
        {
            log.Info($"RagdollController: entry conditioning skipped - stance={entryStanceWidth:F2} kneeAngles=({entryLeftKneeAngle:F1},{entryRightKneeAngle:F1})");
            return false;
        }

        StopDirectedCollapseSpike();
        StopKneePowerLossPattern();
        StopCollapseEntryConditioning();

        entryConditioningContinuation = continuation;
        entryConditioningElapsed = 0f;
        entryConditioningActive = true;
        WakeRagdollBodiesForBiomechanicalSettle();
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: entry conditioning begun - stance={entryStanceWidth:F2} kneeAngles=({entryLeftKneeAngle:F1},{entryRightKneeAngle:F1})");
        return true;
    }

    private void StopCollapseEntryConditioning()
    {
        if (!entryConditioningActive) return;
        entryConditioningActive = false;
        entryConditioningContinuation = null;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: entry conditioning stopped");
    }

    private void UpdateCollapseEntryConditioning(float dt)
    {
        if (!entryConditioningActive || simulation == null) return;

        entryConditioningElapsed += dt;
        var knee = EffectiveKneePowerLossSettings();
        var minDuration = Math.Clamp(knee.EntryMinDuration, 0.05f, 1.0f);
        var maxDuration = Math.Clamp(MathF.Max(knee.EntryMaxDuration, minDuration), minDuration, 1.5f);
        var t = SmoothStep(Math.Clamp(entryConditioningElapsed / maxDuration, 0f, 1f));
        var stepScale = Math.Clamp(dt * 60f, 0.35f, 2.0f);

        var targetStance = Math.Clamp(knee.EntryTargetStanceStart, 0.05f, 1.2f)
            + (Math.Clamp(knee.EntryTargetStanceEnd, 0.05f, 1.2f) - Math.Clamp(knee.EntryTargetStanceStart, 0.05f, 1.2f)) * t;
        var pelvisDown = Math.Clamp(knee.EntryPelvisDownStart, 0f, 2f)
            + (Math.Clamp(knee.EntryPelvisDownEnd, 0f, 2f) - Math.Clamp(knee.EntryPelvisDownStart, 0f, 2f)) * t;

        ApplyEntryKneeSeparation(targetStance, 0.10f * stepScale);
        ApplyCoreAxisVelocity(Bones.Pelvis, -Vector3.UnitY, pelvisDown, 0.075f * stepScale);
        ApplyCoreAxisVelocity(Bones.Pelvis, entryForward, 0.20f + 0.18f * t, 0.055f * stepScale);
        ApplyCoreAxisVelocity(Bones.MidSpine, entryForward, 0.10f + 0.10f * t, 0.040f * stepScale);

        var ready = TryMeasureEntryPose(out var stance, out var leftAngle, out var rightAngle, out _, out _) &&
                    stance >= Math.Clamp(knee.EntryReadyStance, 0.05f, 1.2f) &&
                    MathF.Min(leftAngle, rightAngle) >= Math.Clamp(knee.EntryReadyKneeAngle, 1f, 60f);
        if (entryConditioningElapsed < minDuration || (!ready && entryConditioningElapsed < maxDuration))
            return;

        var continuation = entryConditioningContinuation;
        entryConditioningActive = false;
        entryConditioningContinuation = null;
        log.Info($"RagdollController: entry conditioning complete - stance={stance:F2} kneeAngles=({leftAngle:F1},{rightAngle:F1}) ready={ready}");
        _ = continuation?.Invoke();
    }

    private bool TryMeasureEntryPose(out float stanceWidth, out float leftKneeAngle, out float rightKneeAngle,
        out Vector3 leftKnee, out Vector3 rightKnee)
    {
        stanceWidth = 0f;
        leftKneeAngle = 0f;
        rightKneeAngle = 0f;
        leftKnee = Vector3.Zero;
        rightKnee = Vector3.Zero;

        if (!TryGetSupportPoint(true, out var leftSupport) || !TryGetSupportPoint(false, out var rightSupport))
            return false;
        var rightAxis = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        stanceWidth = MathF.Abs(Vector3.Dot(rightSupport - leftSupport, rightAxis));

        leftKneeAngle = MeasureLegBendAngle(Bones.Thigh.Left, Bones.Shin.Left, Bones.Foot.Left, out leftKnee);
        rightKneeAngle = MeasureLegBendAngle(Bones.Thigh.Right, Bones.Shin.Right, Bones.Foot.Right, out rightKnee);
        return leftKneeAngle >= 0f && rightKneeAngle >= 0f;
    }

    private float MeasureLegBendAngle(string hipBone, string kneeBone, string ankleBone, out Vector3 knee)
    {
        knee = Vector3.Zero;
        if (!TryGetBoneOriginPosition(hipBone, out var hip) ||
            !TryGetBoneOriginPosition(kneeBone, out knee) ||
            !TryGetBoneOriginPosition(ankleBone, out var ankle))
            return -1f;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        return MathF.Acos(Math.Clamp(Vector3.Dot(thigh, shin), -1f, 1f)) * 180f / MathF.PI;
    }

    private void ApplyEntryKneeSeparation(float targetWidth, float maxDelta)
    {
        if (simulation == null) return;
        var leftHandle = FindBodyHandle(Bones.Shin.Left);
        var rightHandle = FindBodyHandle(Bones.Shin.Right);
        if (!leftHandle.HasValue || !rightHandle.HasValue) return;

        var lateral = FlatNormalize(entryRight, Vector3.UnitX);
        var left = simulation.Bodies.GetBodyReference(leftHandle.Value);
        var right = simulation.Bodies.GetBodyReference(rightHandle.Value);
        var currentWidth = MathF.Abs(Vector3.Dot(right.Pose.Position - left.Pose.Position, lateral));
        var error = MathF.Max(0f, targetWidth - currentWidth);
        if (error <= 0.001f) return;

        var leftSign = Vector3.Dot(left.Pose.Position - right.Pose.Position, lateral) >= 0f ? 1f : -1f;
        var leftDir = lateral * leftSign;
        var rightDir = -leftDir;
        var speed = Math.Clamp(error * 3.2f, 0f, 0.55f);
        ApplyBodyVelocityAlong(left, leftDir, speed, maxDelta);
        ApplyBodyVelocityAlong(right, rightDir, speed, maxDelta);
    }

    private static void ApplyBodyVelocityAlong(BodyReference body, Vector3 axis, float targetSpeed, float maxDelta)
    {
        if (axis.LengthSquared() < 0.0001f) return;
        var n = Vector3.Normalize(axis);
        var current = Vector3.Dot(body.Velocity.Linear, n);
        var delta = Math.Clamp(targetSpeed - current, -maxDelta, maxDelta);
        body.Velocity.Linear += n * delta;
        body.Awake = true;
    }
}
