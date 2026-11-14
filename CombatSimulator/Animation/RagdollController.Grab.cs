// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // --- Grab constraint API (for cinematic victory sequence, bone-hold test mode, and
    // Enemy Control's grab-as-attack) ---
    //
    // Multiple grabs can be live at once (e.g. a body held by both the neck and the pelvis for
    // a carry pose), so each grab gets its own slot keyed by an opaque id handed back from
    // CreateGrabConstraint. A caller that only ever holds one at a time just keeps that one id.
    private sealed class GrabSlot
    {
        public ConstraintHandle ConstraintHandle;
        public BodyHandle BodyHandle;
        // Servo/spring tuning captured at CreateGrabConstraint and reused by every
        // UpdateGrabTarget — otherwise the per-frame update would overwrite the caller's
        // configured force/speed/stiffness with hardcoded defaults after a single frame.
        public ServoSettings ServoSettings;
        public SpringSettings SpringSettings;
        public bool IsRigid;          // selects a firmer external servo, never a different body model
        public bool ServoActive;
        // Where the caught BONE sits on the body that carries it. Zero when the bone has a body of
        // its own; non-zero when it is an ear, a tail, a finger — see TryResolveGrabAttachment.
        public Vector3 LocalOffset;
        public Vector3 TargetPosition;   // the grabber's grip, live
        public Vector3 CapturePosition;  // where the caught bone lay at the moment it was caught
        public float MaxSpeed;
        public float ReelElapsed;
    }

    /// <summary>
    /// Headroom over the rig's own weight that a spring grab is given.
    ///
    /// A servo's force ceiling was a flat 1000 N. That is 1.5x the weight of a default 70 kg body — it
    /// lifts, but with so little left over that the pull feels like a winch — and at 200 kg the body
    /// weighs 1960 N, so the servo could not have held it up, let alone raised it. A ceiling that does
    /// not scale with the thing it is lifting is just a hidden weight limit.
    ///
    /// So the floor is derived from the rig's real mass: enough to carry it, plus enough again to
    /// accelerate it and to chase a walking grabber.
    /// </summary>
    private const float GrabServoWeightHeadroom = 3.5f;

    /// <summary>Force floor for a spring grab: whatever the caller asked for, or enough to actually
    /// pick this body up, whichever is greater.</summary>
    private float ResolveGrabServoForce(float requested)
    {
        if (ragdollTotalMass <= 0f) return requested;
        var needed = ragdollTotalMass * RagdollTuning.Gravity * GrabServoWeightHeadroom;
        return MathF.Max(requested, needed);
    }

    /// <summary>
    /// How long the caught bone takes to travel from where it was caught to the grabber's grip.
    ///
    /// A grab has to be instant in the sense that matters — the bone is HELD from the frame the grab
    /// lands — without the body teleporting to make that true. So the hold is established where the
    /// bone already lies, and the bone is then reeled to the grip over this long. Long enough to read
    /// as a pull rather than a cut, short enough that nobody would call it slow.
    /// </summary>
    private const float GrabReelSeconds = 0.18f;

    // Limit physical body motion in the servo, rather than delaying the grip target.
    private const float GrabMaxSpeed = 12f;
    /// <summary>Request a firmer finite-force external Grab servo. This never makes a body
    /// kinematic and never changes internal joint stiffness or inertia.</summary>
    public bool GrabRigid { get; set; }

    /// <summary>
    /// Owner-supplied test: should this external rig ride along when the ragdoll is projected onto an
    /// anchor (a grab, or the standing hold)?
    ///
    /// A garment rig is held to the corpse by nothing but contact and friction — there is no joint
    /// tying it on. So when the standing support relocates the WHOLE rig in one step, the clothes are
    /// simply left standing on the ground: the body vanishes out of them. Carrying them by the same
    /// delta keeps them exactly where they sat on the body, drape and all.
    ///
    /// (A grab needs none of this. It holds only the bone it caught and lets the joints haul the rest,
    /// so the body travels continuously and its clothes come along by the contact that was holding them
    /// on in the first place. It is a relocation that leaves them behind, not motion.)
    ///
    /// But only garments still BEING WORN may come along; one that already slid off and is puddled on
    /// the floor must stay there, even though the corpse is very likely lying right on top of it. Only
    /// the rig's owner can tell those apart, so it supplies the test. Null = carry nothing.
    /// </summary>
    public Func<ExternalRigHandle, bool>? CarryExternalRigOnAnchor { get; set; }

    /// <summary>
    /// Work out which rigs come along — BEFORE the body is moved.
    ///
    /// The test asks whether a garment is still on the corpse, so it has to be asked while the corpse
    /// is still under it. Ask afterwards and the answer is always no: the body is already in the
    /// grabber's fist and the clothes are still on the floor a metre below. That answer then locks
    /// itself in, because a garment that is never carried never closes the gap.
    /// </summary>
    private void CollectCarriedExternalRigs()
    {
        carriedRigs.Clear();
        if (CarryExternalRigOnAnchor == null || simulation == null) return;

        foreach (var rig in externalRigs)
        {
            if (rig.Removed || rig.Generation != externalBodyGeneration) continue;
            if (RecDecision("CarryExternalRigOnAnchor", CarryExternalRigOnAnchor(rig))) carriedRigs.Add(rig);
        }
    }

    // A carried garment's panels weigh a fraction of a limb, so an impulse a corpse would barely
    // register throws them clear across the room. The ragdoll's own bodies have had a per-substep
    // ceiling for exactly this reason (see ClampVelocities); external rigs never did, and being
    // teleported around a live scene is precisely when they need one.
    private const float CarriedRigMaxLinearVelocity = 6f;   // m/s
    private const float CarriedRigMaxAngularVelocity = 20f; // rad/s

    /// <summary>Move the rigs collected above by the same delta the ragdoll just took. Uniform
    /// translation, so a garment's shape and its position ON the body both survive.</summary>
    private void TranslateCarriedExternalRigs(Vector3 correction, Vector3 anchorVelocity)
    {
        if (simulation == null) return;

        foreach (var rig in carriedRigs)
        {
            if (rig.Removed || rig.Generation != externalBodyGeneration) continue;

            foreach (var handle in rig.Bodies)
            {
                if (handle.Removed || handle.Generation != externalBodyGeneration) continue;
                // Kinematic rig bodies are driven from elsewhere; moving them here would fight that.
                if (!externalRigDynamicBodyHandles.Contains(handle.Body.Value)) continue;

                var body = simulation.Bodies.GetBodyReference(handle.Body);
                body.Pose.Position += correction;
                body.Velocity.Linear = ClampVectorLength(
                    body.Velocity.Linear - anchorVelocity, CarriedRigMaxLinearVelocity);
                body.Velocity.Angular = ClampVectorLength(
                    body.Velocity.Angular, CarriedRigMaxAngularVelocity);
                body.Awake = true;
            }
        }
    }

    /// <summary>
    /// Create a OneBodyLinearServo constraint that pins a ragdoll bone to a world-space
    /// target position. The target is updated each frame via UpdateGrabTarget().
    /// Also ensures all ragdoll bodies stay awake (SleepThreshold = -1).
    /// </summary>
    private int? CreateGrabConstraintCore(string boneName, Vector3 initialTarget, nint grabbingNpcAddress = 0, float maxForce = 1000f, float maxSpeed = 50f, float springFreq = 120f)
    {
        if (simulation == null || !isActive) return null;

        if (!TryResolveGrabAttachment(boneName, out var targetBody, out var localOffset))
        {
            log.Warning($"RagdollController: nothing on the rig carries bone '{boneName}'");
            return null;
        }

        var slot = new GrabSlot { BodyHandle = targetBody, LocalOffset = localOffset };

        // External controllers have exclusive ownership. A standing/posed support left alive here
        // would apply a second world-space target to the same dynamic island.
        RemoveStandingSupport();
        RemoveWristConstraints();

        // Guided-collapse servos are a short death-entry effect. Once an external hand takes
        // control, retire them instead of making the Grab servo fight pose targets inside the rig.
        StopDirectedCollapseSpike();
        pendingDirectedCollapseSpike = false;
        StopCollapseEntryConditioning();
        pendingEntryConditionedKneePowerLoss = false;
        StopKneePowerLossPattern();
        pendingKneePowerLossPattern = false;

        // Wake every body AND keep it awake for the duration of the grab. SleepThreshold=-1
        // alone only prevents *future* sleep — a corpse that has already settled has all its
        // bodies asleep, so without an explicit wake the servo lifts the pinned bone while the
        // sleeping leg bodies stay frozen in the settled pose. The joints then get dragged into
        // violation and the knees snap into a bend. Waking them up front lets the whole rig
        // follow the lift smoothly.
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
            bodyRef.Activity.SleepThreshold = -1f;
            bodyRef.Awake = true;
        }
        // The rig is no longer resting; force a full physics step next frame.
        BeginBiomechanicalSettle();

        slot.IsRigid = GrabRigid;

        // The same finite-force tuning path is used at creation and during live retuning.
        var servoForce = ConfigureGrabTuning(slot, maxForce, maxSpeed, springFreq);

        var caught = simulation.Bodies.GetBodyReference(slot.BodyHandle);
        suspendedNpcAddress = grabbingNpcAddress;
        slot.TargetPosition = initialTarget;
        slot.CapturePosition = GrabGripPoint(caught, slot.LocalOffset);
        slot.ReelElapsed = 0f;
        slot.ConstraintHandle = simulation.Solver.Add(slot.BodyHandle,
            new OneBodyLinearServo
            {
                LocalOffset = slot.LocalOffset,
                Target = slot.CapturePosition,
                ServoSettings = slot.ServoSettings,
                SpringSettings = slot.SpringSettings,
            });
        slot.ServoActive = true;
        var slotId = nextGrabSlotId++;
        grabSlots[slotId] = slot;

        // Caught where it lies. Nothing moves on this frame — the bone is simply held from now on, and
        // the reel walks it to the grip from here. The capture is the GRIP POINT, which is the bone the
        // caller asked for, not the centre of whatever body happens to be carrying it.
        log.Info($"RagdollController: Grab #{slotId} created on '{boneName}' — caught at ({slot.CapturePosition.X:F2},{slot.CapturePosition.Y:F2},{slot.CapturePosition.Z:F2}), " +
                 $"reeling to ({initialTarget.X:F2},{initialTarget.Y:F2},{initialTarget.Z:F2}), firm={slot.IsRigid}, " +
                 $"rig weighs {ragdollTotalMass:F1}kg, servo={slot.ServoSettings.MaximumForce:F0}N " +
                 $"(base floor {servoForce:F0}N, asked for {maxForce:F0}N)" +
                 $", suspend NPC 0x{grabbingNpcAddress:X}");
        return slotId;
    }

    /// <summary>Where the grip actually is in the world: the caught bone, which for an ear or a tail is
    /// an offset away from the centre of the body carrying it.</summary>
    private static Vector3 GrabGripPoint(BodyReference body, Vector3 localOffset)
        => body.Pose.Position + Vector3.Transform(localOffset, body.Pose.Orientation);

    private Vector3 AdvanceGrabTarget(GrabSlot slot, float dt)
    {
        slot.ReelElapsed = MathF.Min(slot.ReelElapsed + dt, GrabReelSeconds);
        // Once the initial pull-in finishes, submit the current grip directly.
        // The finite-force linear servo still governs how the body follows it;
        // neither body position nor orientation is assigned here.
        if (slot.ReelElapsed >= GrabReelSeconds)
            return slot.TargetPosition;

        var reel = GrabReelSeconds <= 1e-4f ? 1f : slot.ReelElapsed / GrabReelSeconds;
        reel = reel * reel * (3f - 2f * reel);
        return Vector3.Lerp(slot.CapturePosition, slot.TargetPosition, reel);
    }

    /// <summary>Blend grab entry, then follow the live grip with finite-force position servos.</summary>
    private void DriveGrabConstraints(float dt)
    {
        if (grabSlots.Count == 0 || simulation == null) return;

        // The standing support owns the whole rig when it is up; leave the body to it.
        if (standingActive) return;

        foreach (var slot in grabSlots.Values)
        {
            var target = AdvanceGrabTarget(slot, dt);

            if (slot.ServoActive)
            {
                try
                {
                    simulation.Solver.ApplyDescription(slot.ConstraintHandle,
                        new OneBodyLinearServo
                        {
                            LocalOffset = slot.LocalOffset,
                            Target = target,
                            ServoSettings = slot.ServoSettings,
                            SpringSettings = slot.SpringSettings,
                        });
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "RagdollController: Failed to drive grab target");
                }
            }
        }
    }

    /// <summary>
    /// A compatibility bounding capsule for a ragdoll bone. Mesh bodies use actual triangle
    /// surfaces for contact; use TryGetCharacterSurface for final conform/support decisions.
    /// False if the bone has no body
    /// or the ragdoll isn't simulating yet.
    /// </summary>
    public bool TryGetBoneCapsule(string boneName, out BoneCapsule capsule)
    {
        capsule = default;
        if (simulation == null || !isActive || !physicsStarted) return false;

        foreach (var rb in ragdollBones)
        {
            if (rb.Name != boneName) continue;
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            capsule = new BoneCapsule(
                body.Pose.Position, body.Pose.Orientation, rb.CapsuleRadius, rb.CapsuleHalfLength);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The bones the ragdoll currently has bodies of their own for, in build order (root outwards).
    /// Empty while nothing is simulating.
    ///
    /// This is NOT the set a grab can take hold of — that set is every bone on the skeleton, because
    /// <see cref="TryResolveGrabAttachment"/> will carry a bone that has no body by whichever ancestor
    /// does.
    /// </summary>
    public IReadOnlyList<string> GetSimulatedBoneNames()
    {
        if (!isActive || !physicsStarted) return Array.Empty<string>();

        var names = new List<string>(ragdollBones.Count);
        foreach (var rb in ragdollBones)
            names.Add(rb.Name);
        return names;
    }

    /// <summary>
    /// Every bone a grab can take hold of on a character: the whole skeleton, ears and all. A bone with
    /// no body of its own is not out of reach — it has no body precisely because something that does is
    /// carrying it rigidly, and the grab takes that carrier at the right offset. The rig defines what
    /// grabbing means, so it is the rig that answers what can be grabbed.
    /// </summary>
    public IReadOnlyList<string> GetGrabbableBoneNames(nint characterAddress)
    {
        // Every bone on the skeleton, in skeleton order (what BoneTransformService.GetBoneNames lists).
        if (characterAddress == nint.Zero) return Array.Empty<string>();
        var skel = skeletons.TryGetSkeleton(characterAddress);
        if (skel == null) return Array.Empty<string>();
        var count = Math.Min(skel.BoneCount, skel.BoneNameCount);
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var name = skel.BoneName(i);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>True while this bone has a physics body of its very own.</summary>
    public bool IsBoneSimulated(string boneName)
    {
        foreach (var rb in ragdollBones)
            if (rb.Name == boneName) return true;
        return false;
    }

    /// <summary>
    /// Work out what a grab on this bone should actually take hold of.
    ///
    /// The rig only builds bodies for about forty bones. A skeleton has well over a hundred: ears,
    /// horns, tails, whiskers, fingers, toes. None of those carry physics — but that is precisely
    /// BECAUSE they are rigidly carried by something that does. An ear does not move independently of
    /// the head; it IS the head, at an offset.
    ///
    /// So grabbing an ear is not a special case at all. It is grabbing the head, at the point where the
    /// ear happens to sit — and the servo already had the field for that (LocalOffset) sitting unused at
    /// Vector3.Zero. Walk up the skeleton until a bone with a body turns up, hold THAT, and offset the
    /// grip to where the bone the caller actually asked for is. Pull a corpse by the ear and the head
    /// comes round by its ear, rather than being towed from the middle of the skull.
    /// </summary>
    private bool TryResolveGrabAttachmentCore(string boneName, out BodyHandle body, out Vector3 localOffset)
    {
        body = default;
        localOffset = Vector3.Zero;
        if (simulation == null || !isActive) return false;

        // A bone with a body of its own: nothing to work out.
        foreach (var rb in ragdollBones)
        {
            if (rb.Name != boneName) continue;
            body = rb.BodyHandle;
            return true;
        }

        var skel = skeletons.TryGetSkeleton(targetCharacterAddress);
        if (skel == null) return false;

        var index = ResolveBoneIndex(skel, boneName);
        if (index < 0) return false;

        var requested = skeletons.GetBoneWorldPosition(targetCharacterAddress, boneName);
        if (rec != null) RecBoneWorldPosition("TryResolveGrabAttachment", targetCharacterAddress, boneName, requested);
        if (requested == null) return false;

        // Up the parent chain until something with a body turns up.
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        var current = index;
        for (int guard = 0; guard < count; guard++)
        {
            var parent = skel.Parent(current);
            if (parent < 0 || parent >= count) break;

            var parentName = skel.BoneName(parent);
            if (!string.IsNullOrEmpty(parentName))
            {
                foreach (var rb in ragdollBones)
                {
                    if (rb.Name != parentName) continue;

                    var carrier = simulation.Bodies.GetBodyReference(rb.BodyHandle);
                    body = rb.BodyHandle;
                    // Where the requested bone sits, in the carrying body's own frame.
                    localOffset = Vector3.Transform(
                        requested.Value - carrier.Pose.Position,
                        Quaternion.Inverse(carrier.Pose.Orientation));

                    log.Info($"RagdollController: '{boneName}' has no body — carried by '{rb.Name}', " +
                             $"gripping it {localOffset.Length():F3}m off that body's centre");
                    return true;
                }
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Retune a live grab. <see cref="UpdateGrabTarget"/> re-applies the stored servo/spring settings
    /// every frame, so a caller driving the grab from sliders needs this to make them take effect
    /// without tearing the constraint down and rebuilding it.
    /// </summary>
    private void SetGrabTuningCore(int slotId, float maxForce, float maxSpeed, float springFreq)
    {
        if (!grabSlots.TryGetValue(slotId, out var slot)) return;
        ConfigureGrabTuning(slot, maxForce, maxSpeed, springFreq);
    }

    private float ConfigureGrabTuning(GrabSlot slot, float maxForce, float maxSpeed, float springFreq)
    {
        var bodySpeedLimit = ActiveClassProfile.ClassConstants.MaxLinearVelocity;
        slot.MaxSpeed = Math.Clamp(float.IsFinite(maxSpeed) ? maxSpeed : GrabMaxSpeed, 0.1f,
            MathF.Min(GrabMaxSpeed, bodySpeedLimit));
        var servoForce = ResolveGrabServoForce(float.IsFinite(maxForce) ? maxForce : 1000f);
        var resolvedFrequency = Math.Clamp(float.IsFinite(springFreq) ? springFreq : 120f, 1f, 180f);
        if (slot.IsRigid)
        {
            var firmForce = MathF.Min(
                servoForce * 2f,
                MathF.Max(servoForce, ragdollTotalMass * 120f));
            slot.ServoSettings = new ServoSettings(slot.MaxSpeed, 1f, firmForce);
            slot.SpringSettings = new SpringSettings(MathF.Max(60f, resolvedFrequency), 1.5f);
        }
        else
        {
            slot.ServoSettings = new ServoSettings(slot.MaxSpeed, 1f, servoForce);
            slot.SpringSettings = new SpringSettings(resolvedFrequency, 1f);
        }
        return servoForce;
    }

    /// <summary>
    /// Update the grab constraint's target position (call each frame with NPC hand world pos).
    /// </summary>
    private void UpdateGrabTargetCore(int slotId, Vector3 worldTarget)
    {
        if (simulation == null || !grabSlots.TryGetValue(slotId, out var slot)) return;

        slot.TargetPosition = worldTarget;

    }
    /// <summary>Remove one Grab constraint. Shared sleep state is restored after the last slot.</summary>
    private void RemoveGrabConstraintCore(int slotId)
    {
        if (simulation == null || !grabSlots.TryGetValue(slotId, out var slot)) return;
        grabSlots.Remove(slotId);

        if (slot.ServoActive)
        {
            try
            {
                simulation.Solver.Remove(slot.ConstraintHandle);
            }
            catch { }
        }
        if (grabSlots.Count > 0)
        {
            log.Info($"RagdollController: Grab #{slotId} released ({grabSlots.Count} still held)");
            return;
        }

        var normalThreshold = RagdollSleepThreshold;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            try
            {
                var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
                bodyRef.Activity.SleepThreshold = normalThreshold;
                bodyRef.Awake = true;
            }
            catch { }
        }

        suspendedNpcAddress = nint.Zero;
        BeginBiomechanicalSettle();

        log.Info("RagdollController: Grab constraint removed");
    }
}
