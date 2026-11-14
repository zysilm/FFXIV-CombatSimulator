// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // --- Follow-position transform, deferred out of the render hook ---
    //
    // The follow feature moves the corpse's DrawObject (or the NPC phantom's GameObject) to
    // the ragdoll root so the culling sphere tracks a thrown body. Doing that write INSIDE
    // the render hook — where the physics step runs — mutates the render scene graph while
    // the game is iterating it, and the corruption detonates later as the recurring native
    // crash in the skeleton-list walk (next=0xFFFF; two dumps today alone). The render pass
    // only records the desired position here; the write happens on the framework tick,
    // between walks instead of during one.
    private Vector3? pendingFollowPos;
    private bool pendingFollowIsLocalPlayer;

    private unsafe void ApplyDeferredFollowTransform(Dalamud.Plugin.Services.IFramework _)
    {
        SampleAnimatedHairTerrain();
        if (!pendingFollowPos.HasValue)
            return;
        var pos = pendingFollowPos.Value;
        pendingFollowPos = null;

        if (targetCharacterAddress == nint.Zero || !actors.GameRunning)
            return;

        try
        {
            if (pendingFollowIsLocalPlayer)
            {
                actors.SetDrawPosition(targetCharacterAddress, pos);
            }
            else if (colliders.HasMovementBlock && TargetObjectAlive())
            {
                actors.SetApproachPosition(targetCharacterAddress, pos);
            }
        }
        catch { }
    }

    /// <summary>Arms the ragdoll the way the options say: their delay, and their choice of when physics starts.</summary>
    public void Activate(nint characterAddress)
    {
        Activate(characterAddress, settings.RagdollActivationDelay, RagdollTrigger.ClampMode(settings.RagdollTriggerMode));
    }

    /// <summary>Arms the ragdoll to start physics a fixed time from now.</summary>
    public void Activate(nint characterAddress, float delayOverride)
    {
        Activate(characterAddress, delayOverride, RagdollTriggerMode.FixedDelay);
    }

    /// <param name="delay">Seconds to wait in <see cref="RagdollTriggerMode.FixedDelay"/>; the fallback in the other modes.</param>
    /// <param name="stagger">Extra seconds after the moment is reached, to spread out ragdolls that start together.</param>
    public void Activate(nint characterAddress, float delay, RagdollTriggerMode mode, float stagger = 0f)
    {
        if (isActive) Deactivate();

        targetCharacterAddress = characterAddress;
        activationDelay = delay;
        trigger = new RagdollTrigger(mode, delay);
        triggerPoseKnown = false;
        triggerStagger = MathF.Max(0f, stagger);
        triggeredAt = -1f;
        elapsed = 0f;
        physicsStarted = false;
        animationFrozen = false;
#if DEV_EXPERIMENTAL
        followWasActive = false;
#endif
        isActive = true;
        lastFrameTimestamp = 0;
        physicsAccumulator = 0f;
        traversalMassMultiplier = 1f;
        prevAllAsleep = false;
        npcColliderMovedNearCorpse = false;
        biomechanicalSettleRemaining = 0f;
        hasPrevPhysicsState = false;
        // Save original position so we can restore if FollowPosition is toggled off
        savedCharacterPosition = actors.GetPosition(characterAddress, ActorReadSite.Activation);
        targetEntityId = actors.GetEntityId(characterAddress);
        // The local player is always object-table index 0 (same convention the render pass uses).
        // Captured once here rather than re-read per frame, because it decides how the rig is BUILT
        // — soft tissue coverage and solver budget — and those are settled at InitializePhysics.
        targetIsLocalPlayer = actors.GetObjectIndex(characterAddress) == 0;

        // Fit the collision hulls now, on a worker, so they are ready when physics starts.
        PrewarmStructuralMeshFits();
        ArmConfiguredGuidedCollapse();
        RecBeginActivation(delay, mode, stagger);

        log.Info($"RagdollController: Activated for 0x{characterAddress:X} (start: {RagdollTrigger.Labels[(int)mode]}, delay={activationDelay:F1}s)");
    }

    private void DeactivateCore()
    {
        if (!isActive) return;
        isActive = false;

        // Restore animation speed before clearing the address. Gated on animationFrozen
        // (not physicsStarted) so a freeze that happened during a FAILED InitializePhysics
        // is still undone — otherwise the corpse would be stuck at OverallSpeed=0 forever,
        // and a later re-Activate would save that 0 as the "original" speed (sticky freeze).
        // Skip the character write during game shutdown — the actor is already freed then and the
        // write would crash (this runs from Dispose on game close). LocalPlayer null => closing.
        if (targetCharacterAddress != nint.Zero && animationFrozen && actors.GameRunning)
        {
            try
            {
                actors.SetAnimationSpeed(targetCharacterAddress, savedOverallSpeed);
                log.Info($"RagdollController: Animation restored (speed={savedOverallSpeed:F2})");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "RagdollController: Failed to restore animation speed");
            }
        }

        // Undo any soft-tissue scale writes while the skeleton is still reachable — the
        // frozen pose never refreshes scale on its own, and a revived character must not
        // keep squashed flesh.
        RestoreSquashScalesOnDeactivate();

        // If follow moved the local player's client-side render transform, snap the DrawObject
        // back to the frozen death position so there is no one-frame pop before the game
        // re-syncs it from the (never-moved) logical position on revive. NPC phantoms are left
        // as-is (they despawn or are repositioned elsewhere), matching the prior behaviour.
#if DEV_EXPERIMENTAL
        if (followWasActive && targetCharacterAddress != nint.Zero && actors.GameRunning)
        {
            try
            {
                if (actors.GetObjectIndex(targetCharacterAddress) == 0)
                    actors.SetDrawPosition(targetCharacterAddress, savedCharacterPosition);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "RagdollController: Failed to restore DrawObject position on deactivate");
            }
        }
        followWasActive = false;
#endif

        StopDirectedCollapseSpike();
        pendingDirectedCollapseSpike = false;
        StopCollapseEntryConditioning();
        pendingEntryConditionedKneePowerLoss = false;
        StopKneePowerLossPattern();
        pendingKneePowerLossPattern = false;

        targetCharacterAddress = nint.Zero;
        targetEntityId = 0;
        physicsStarted = false;
        animationFrozen = false;
        handoffSampleValid = false;
        ragdollBoneIndices.Clear();
        activeDefByName.Clear();
        activeRagdollIsGeneric = false;
        useLocalDismemberBones = false;
        localDismemberBones.Clear();
        terrainPatchCenters.Clear();
        nHaraIndex = -1;
        kaoBodyBoneIndex = -1;
        RemoveHairRig();

        DestroySimulation();
        log.Info("RagdollController: Deactivated");
    }

    // --- Coordinate conversion using Skeleton.Transform ---
    // ModelPose is in skeleton-local space (NOT character-position-offset space).
    // The proper conversion uses the skeleton's full transform (position + rotation + scale).
    //   WorldPos  = skelPos + Rotate(modelPos * skelScale, skelRot)
    //   ModelPos  = Rotate(worldPos - skelPos, skelRotInv) / skelScale
    //   WorldRot  = skelRot * modelRot
    //   ModelRot  = skelRotInv * worldRot

    // Snapshot the current animated bone poses (model space) + skeleton transform so the next
    // call to InitializePhysics can derive handoff velocities. Called each frame while the
    // activation-delay death animation plays.
    private bool ShouldStartPhysics(float dt)
    {
        if (trigger == null)
            return elapsed >= activationDelay;
        if (triggeredAt < 0f)
        {
            RagdollAnimationSample? animation = null;
            float? middleHeight = null;
            float? motion = null;
            if (trigger.Automatic)
            {
                ReadTriggerInputs(dt, out animation, out middleHeight, out motion);
                if (rec != null) RecTriggerInputs(animation, middleHeight, motion);
            }
            if (!trigger.Update(dt, elapsed, animation, middleHeight, motion))
                return false;
            triggeredAt = elapsed;
            if (trigger.Automatic)
                log.Info($"RagdollController: physics starts {elapsed:F2}s after death.");
        }
        return elapsed >= triggeredAt + triggerStagger;
    }

    /// <summary>
    /// What the death animation is doing right now: the dominant animation, the height of the
    /// pelvis, and how fast the body is moving within itself.
    /// </summary>
    private void ReadTriggerInputs(float dt, out RagdollAnimationSample? animation, out float? middleHeight, out float? motion)
    {
        animation = null;
        middleHeight = null;
        motion = null;
        try
        {
            var skel = skeletons.TryGetSkeleton(targetCharacterAddress);
            if (skel == null) return;

            var pelvis = ResolveBoneIndex(skel, Bones.Pelvis);
            if (pelvis >= 0 && pelvis < skel.BoneCount)
                middleHeight = skel.ModelPose[pelvis].Translation.Y * GetCharacterScale(targetCharacterAddress, skel).Y;

            // How far every bone has moved since the frame before, in the skeleton's own space.
            if (skel.BoneCount > 0)
            {
                var bones = skel.BoneCount;
                var scale = GetCharacterScale(targetCharacterAddress, skel).Y;
                var known = triggerPosePositions.Length == bones && triggerPoseKnown;
                if (triggerPosePositions.Length != bones)
                {
                    triggerPosePositions = new Vector3[bones];
                    triggerPoseMoved = new float[bones];
                }
                var modelPose = skel.ModelPose;
                for (var i = 0; i < bones; i++)
                {
                    ref var bone = ref modelPose[i];
                    var at = new Vector3(bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
                    triggerPoseMoved[i] = Vector3.Distance(at, triggerPosePositions[i]) * scale;
                    triggerPosePositions[i] = at;
                }
                triggerPoseKnown = true;
                if (known)
                    motion = RagdollTrigger.BodyMotion(triggerPoseMoved, dt);
            }

            // The animation with the most weight is the one the pose mostly comes from.
            if (skel.TryGetDominantAnimation(out var dominant))
                animation = dominant;
        }
        catch
        {
            // Unreadable this frame; the trigger falls back on the clock if it stays that way.
        }
    }

    private void SampleHandoffPose(float dt)
    {
        if (dt <= 1e-5f) return;
        var skel = skeletons.TryGetSkeleton(targetCharacterAddress);
        if (skel == null) return;
        if (!skel.HasRoot) return;

        var root = skel.Root;
        handoffSkelPos = root.Position;
        handoffSkelRot = root.Rotation;
        handoffSkelScale = GetCharacterScale(targetCharacterAddress, skel);

        var pose = skel.ModelPose;
        var n = skel.BoneCount;
        if (handoffPrevPos == null || handoffPrevPos.Length < n)
        {
            handoffPrevPos = new Vector3[n];
            handoffPrevRot = new Quaternion[n];
        }
        for (int i = 0; i < n; i++)
        {
            ref var mt = ref pose[i];
            handoffPrevPos[i] = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            handoffPrevRot![i] = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
        }
        handoffPrevCount = n;
        handoffSampleDt = dt;
        handoffSampleValid = true;
    }

    // Convert the rotation delta prev→curr into an angular-velocity vector (rad/s).
    private static Vector3 AngularVelocityFromQuats(Quaternion prev, Quaternion curr, float dt)
    {
        if (dt <= 1e-5f) return Vector3.Zero;
        var dq = Quaternion.Normalize(curr * Quaternion.Conjugate(prev));
        if (dq.W < 0f) dq = new Quaternion(-dq.X, -dq.Y, -dq.Z, -dq.W);
        var s = MathF.Sqrt(MathF.Max(0f, 1f - dq.W * dq.W));
        if (s < 1e-5f) return Vector3.Zero;
        var angle = 2f * MathF.Acos(Math.Clamp(dq.W, -1f, 1f));
        var axis = new Vector3(dq.X, dq.Y, dq.Z) / s;
        return axis * (angle / dt);
    }

    private static Vector3 ClampVectorLength(Vector3 v, float maxLen)
    {
        var lenSq = v.LengthSquared();
        if (lenSq > maxLen * maxLen && lenSq > 1e-8f)
            return v * (maxLen / MathF.Sqrt(lenSq));
        return v;
    }

    private void DestroySimulation()
    {
        lastMeshLodInstall = 0;
        externalBodyGeneration++;
        impactFreezeRemaining = 0f;
        impactCooldownRemaining = 0f;
        previousDescentSpeed = 0f;
        torsoBodyIndices.Clear();
        grabSlots.Clear();
        suspendedNpcAddress = nint.Zero;
        standingActive = false;
        standingConstraints.Clear();
        wristConstraints.Clear();
        angularMotorByBone.Clear();
        swingLimitByBone.Clear();
        recoilRelaxers.Clear();
        biomechanicalSettleRemaining = 0f;
        ragdollBones.Clear();
        characterSurfaceRuntime = null;
        faceTraversalLandmarks.Clear();
        externalBodies.Clear();
        externalRigs.Clear();
        externalDynamicBodyHandles.Clear();
        externalRigDynamicBodyHandles.Clear();
        externalRigNoRagdollContactBodyHandles.Clear();
        externalRigConnectedPairs.Clear();
        softKinematicBodyHandles.Clear();
        npcCollisionKinematicBodyHandles.Clear();
        npcTraversalGhostKinematicBodyHandles.Clear();
        npcTraversalDynamicBodyHandles.Clear();
        lock (npcTraversalProxySync)
        {
            npcTraversalProxyRequests.Clear();
            npcTraversalProxies.Clear();
        }
        npcTraversalProxyActivityThisFrame = false;
        softBodyBodyHandles.Clear();
        ClearWeaponContactProxyState();
        npcCollisionStates.Clear();
        npcTraversalAnchors.Clear();
        attackStrikeTimer = 0f;
        strikePower = 0f;
        strikeColliderAddress = nint.Zero;
        strikeColliderAddresses.Clear();
        weaponPrevValid = false;
        simulation?.Dispose();
        simulation = null;
        ReleaseMeshDispatcher();
        bufferPool?.Clear();
        bufferPool = null;
    }
}
