// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.Recording;
using BepuPhysics;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private void OnRenderFrameCore()
    {
        try
        {
            // The settings for this frame (and for everything until the next frame starts).
            settingsSource.Refresh(settings);
        }
        catch (Exception ex)
        {
            // Nothing may escape this handler: the render hook calls every controller through one multicast
            // delegate, and an exception would skip the controllers after this one.
            log.Error(ex, "RagdollController: the settings could not be read; this frame is skipped");
            return;
        }
        try { UpdateFrozenMeshCache(); }
        catch (Exception ex) { log.Warning(ex, "Frozen collision cache capture failed; retrying when the model is ready."); }
        if (!isActive)
        {
            try { OnAnimatedHairFrame(); }
            catch (Exception ex) { RemoveHairRig(); log.Error(ex, "Animated hair update failed"); }
            return;
        }

        try
        {
            if (!settings.RagdollHairPhysics && hairRigActive) RemoveHairRig();
            var dt = ComputeFrameDelta();
            elapsed += dt;
            if (rec != null) RecRenderFrame(dt);

            if (attackStrikeTimer > 0f) attackStrikeTimer -= dt;
            UpdateWeaponStrike(dt);

            // Wait for activation delay (death animation plays first)
            if (!physicsStarted)
            {
                if (rec != null) RecPreFrame();
                if (!RecDecision("ShouldStartPhysics", ShouldStartPhysics(dt)))
                {
                    OnAnimatedHairFrame();
                    // Keep sampling the animated pose so the last frame before handoff carries
                    // its velocity into the physics bodies (no freeze on activation).
                    SampleHandoffPose(dt);
                    return;
                }
                lock (animatedHairGate)
                {
                    animatedHair = false;
                    hairRigChains.Clear(); hairRigActive = false;
                }
                if (!InitializePhysics()) { Deactivate(); return; }
                physicsStarted = true;
                frameCount = 0;
                BeginBiomechanicalSettle();
                physicsAccumulator = 0f; // start clean — don't carry the activation-delay wait

                if (pendingDirectedCollapseSpike)
                {
                    pendingDirectedCollapseSpike = false;
                    if (!string.IsNullOrWhiteSpace(pendingDirectedProfileId))
                        BeginProfileDirectedCollapseSpike(pendingDirectedProfileId);
                    else
                        BeginDirectedKneelPitchSpike(
                            pendingDirectedDuration,
                            pendingDirectedFootForce,
                            pendingDirectedPelvisForce,
                            pendingDirectedDrop,
                            pendingDirectedForward,
                            pendingDirectedPitchDegrees);
                }

                if (pendingEntryConditionedKneePowerLoss)
                {
                    pendingEntryConditionedKneePowerLoss = false;
                    if (!BeginCollapseEntryConditioning(BeginKneePowerLossForwardPattern))
                        BeginKneePowerLossForwardPattern();
                }

                if (pendingKneePowerLossPattern)
                {
                    pendingKneePowerLossPattern = false;
                    BeginKneePowerLossForwardPattern();
                }
            }

            var stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            StepAndApply(dt);
            costTotalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stepStart;
            costFrames++;
        }
        catch (Exception ex)
        {
            log.Error(ex, "RagdollController: Error in render frame");
            Deactivate();
        }
    }

    /// <summary>
    /// Real wall-clock seconds since the previous render frame, clamped to a sane range.
    /// Returns one fixed timestep on the very first call (no prior timestamp yet).
    /// </summary>
    private float ComputeFrameDelta()
    {
        var now = clock.GetTimestamp(ClockSite.FrameDelta);
        if (lastFrameTimestamp == 0)
        {
            lastFrameTimestamp = now;
            return FixedTimestep;
        }
        var dt = (float)((now - lastFrameTimestamp) / (double)clock.Frequency);
        lastFrameTimestamp = now;
        if (dt < 0f) dt = 0f;
        if (dt > MaxFrameDelta) dt = MaxFrameDelta;
        return dt;
    }


    private void BeginBiomechanicalSettle(float duration = BiomechanicalSettleDuration)
    {
        biomechanicalSettleRemaining = MathF.Max(biomechanicalSettleRemaining, duration);
        prevAllAsleep = false;
    }

    private static float BodyBottom(in RagdollBone bone, BodyReference body)
    {
        if (bone.MeshGeometry != null)
            return bone.MeshGeometry.Bottom(body.Pose.Position, body.Pose.Orientation);
        if (bone.HullCorners != null)
        {
            var lowest = float.MaxValue;
            foreach (var corner in bone.HullCorners)
                lowest = MathF.Min(lowest, Vector3.Transform(corner, body.Pose.Orientation).Y);
            return body.Pose.Position.Y + lowest;
        }
        if (bone.ColliderShape == RagdollColliderShape.Box)
        {
            var x = Vector3.Transform(Vector3.UnitX, body.Pose.Orientation);
            var y = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
            var z = Vector3.Transform(Vector3.UnitZ, body.Pose.Orientation);
            var verticalExtent = MathF.Abs(x.Y) * bone.BoxHalfExtents.X +
                                 MathF.Abs(y.Y) * bone.BoxHalfExtents.Y +
                                 MathF.Abs(z.Y) * bone.BoxHalfExtents.Z;
            return body.Pose.Position.Y - verticalExtent;
        }

        var capsuleY = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        return body.Pose.Position.Y - MathF.Abs(capsuleY.Y) * bone.CapsuleHalfLength - bone.CapsuleRadius;
    }

    private void WakeRagdollBodiesForBiomechanicalSettle()
    {
        if (simulation == null) return;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            try
            {
                var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
                bodyRef.Awake = true;
            }
            catch { }
        }
    }

    // === Heavy body: fall gravity ===
    //
    // True gravity is not the gravity an action game runs on. A game camera's distance and field of
    // view flatten vertical travel, and an audience's sense of how fast things should fall comes from
    // films and from other games, not from a stopwatch — which is why nearly every action game falls at
    // one and a half to two and a half g, and why engines ship a "fall gravity multiplier" as standard.
    // A knockdown in a fighting game drops harder still. We were running at an honest 9.8, and honest
    // read as floaty.
    //
    // Applied only on the way DOWN, which is the usual shape of the trick: the arc a blow throws the
    // body into keeps its silhouette, and only the descent is made to bite.
    //
    // Falling is a property of the RIG, not of a body. Asked per body, against the ground height, the
    // answer is wrong in the most ordinary case there is: an arm draped over the chest, a shin crossed
    // over a thigh, a limb resting on a rock. Those sit well clear of the floor and are not falling at
    // all — something is holding them up. Push them anyway and they are driven into whatever holds them,
    // the contact shoves them back, and the two of them buzz. Worse, the answer differs from limb to
    // limb, so the joints see a relative violation on top.
    //
    // So the question is asked once, of the whole rig — is anything touching down, and is the thing as a
    // whole on its way down — and the answer is applied to every body identically. Uniform means no
    // joint sees anything at all, and there is nothing for the solver to argue with.
    private const float FallGravityMultiplier = 1.8f;
    private const float AirborneClearance = 0.06f;  // m of daylight before the rig counts as off the floor
    private const float FallGravityMinDescent = 1f; // m/s — below this it is not falling, it is sitting

    private void ApplyFallGravity(float dt)
    {
        if (simulation == null || ragdollBones.Count == 0) return;
        if (inWater) return; // sinking is not falling

        // Being held is not falling.
        if (grabSlots.Count > 0 || standingActive) return;

        var extra = RagdollTuning.Gravity * (FallGravityMultiplier - 1f) * dt;
        if (extra <= 0f) return;

        var lowest = float.MaxValue;
        var descent = 0f;
        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            lowest = MathF.Min(lowest, BodyBottom(rb, body));
            descent -= body.Velocity.Linear.Y;
        }
        descent /= ragdollBones.Count;

        if (lowest <= groundY + AirborneClearance) return; // something is touching down: nobody is falling
        if (descent < FallGravityMinDescent) return;       // airborne, but not on its way down

        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            if (body.Kinematic) continue;

            var velocity = body.Velocity.Linear;
            velocity.Y -= extra;
            body.Velocity.Linear = velocity;
        }
    }

    // Clamp per-body velocity each substep as a hard ceiling against energy blow-up.
    // Generic rigs inject energy through their stiff auto-built constraint network (small
    // bodies + dense joints); human rigs are normally stable but can still have the solver
    // diverge into a jitter-then-explode failure. Real ragdoll fall/fling speeds stay well
    // under the ceilings, so settling looks normal, but runaway growth is capped. Callers
    // pass tighter ceilings for generic rigs and looser ones for humans.
    private void ClampVelocities(float maxLinear, float maxAngular)
    {
        foreach (var rb in ragdollBones)
        {
            var body = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
            if (!body.Awake) continue;
            var lin = body.Velocity.Linear;
            var linSpeed = lin.Length();
            if (linSpeed > maxLinear)
                body.Velocity.Linear = lin * (maxLinear / linSpeed);
            var ang = body.Velocity.Angular;
            var angSpeed = ang.Length();
            if (angSpeed > maxAngular)
                body.Velocity.Angular = ang * (maxAngular / angSpeed);
        }
    }

    // Snapshot the current reconstructed bone world pose into the interpolation prev buffers,
    // using the same capsule-center → bone-origin reconstruction as Pass 1. Called just before
    // each physics tick so prev holds the pre-tick state to blend from.
    private void CapturePrevBodyPoses(int boneCount)
    {
        for (int i = 0; i < boneCount; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0) continue;
            var b = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
            prevWorldRotations![i] = Quaternion.Normalize(b.Pose.Orientation * rb.CapsuleToBoneOffset);
            var profileOrigin = b.Pose.Position - Vector3.Transform(rb.ShapeCenterOffset, b.Pose.Orientation);
            if (rb.SegmentHalfLength > 0)
            {
                var y = Vector3.Transform(Vector3.UnitY, b.Pose.Orientation);
                prevWorldPositions![i] = profileOrigin - rb.SegmentHalfLength * y;
            }
            else
            {
                prevWorldPositions![i] = profileOrigin;
            }
        }
    }

    /// <summary>
    /// True when the ragdoll target is still a live object. Matches against the object table rather
    /// than dereferencing <see cref="targetCharacterAddress"/> — once the actor is freed, its memory
    /// often still reads back the stale EntityId, so a pointer-based check happily passes and we hand
    /// a dangling pointer to native game code (that is an access violation, not a catchable exception).
    /// </summary>
    private bool TargetObjectAlive()
    {
        if (targetCharacterAddress == nint.Zero) return false;
        return actors.IsAlive(targetCharacterAddress, targetEntityId);
    }

    private void StepAndApply(float dt)
    {
        if (simulation == null) return;

        // Liveness guard: if the corpse despawned and its object-table slot was reused, the
        // address now points at an unrelated character with a different EntityId. Without this,
        // we would freeze that newcomer's animation and overwrite its bones with our ragdoll
        // pose. Deactivate cleanly instead (restores nothing — the original target is gone).
        if (targetCharacterAddress == nint.Zero ||
            actors.GetEntityId(targetCharacterAddress) != targetEntityId)
        {
            log.Info("RagdollController: target despawned or slot reused; deactivating.");
            animationFrozen = false; // do not write OverallSpeed onto whatever now occupies the slot
            Deactivate();
            return;
        }

        var skel = skeletons.TryGetSkeleton(targetCharacterAddress, SkeletonReadSite.FrameIn);
        if (skel == null) return;
        if (rec != null) RecFrameBegin(dt, skel);

        if (activeRagdollMeshVolumes != settings.RagdollMeshVolumes ||
            (activeRagdollMeshVolumes && activeMeshCacheRevision != settings.RagdollMeshCacheRevision))
        {
            log.Info("Frozen mesh collision option/cache refreshed; rebuilding physical geometry.");
            RemoveHairRig();
            DestroySimulation();
            physicsStarted = false;
            return;
        }

        // Manual refresh above can rebuild a changed skeleton. Without refresh, stop
        // before using old bone indices; never read a new model to replace the cache.
        if (skel.BoneCount != initialBoneCount)
        {
            log.Info($"RagdollController: skeleton bone count changed ({initialBoneCount}->{skel.BoneCount}); deactivating.");
            Deactivate();
            return;
        }

        // Keep animation frozen (game may recalculate OverallSpeed each frame)
        actors.SetAnimationSpeed(targetCharacterAddress, 0f);

        // The local player is always object-table index 0. Used below to choose a render-only
        // follow (DrawObject.Position, never the packet-synced GameObject.Position) and to
        // suppress the self-induced biomechanical settle while follow drags the corpse.
        var isLocalPlayerTarget =
            actors.GetObjectIndex(targetCharacterAddress) == 0;

        // Update skeleton transform for WorldToModel conversion.
        // The game may reposition the character root (e.g., dismount, death transition).
        // Physics bodies stay at correct world positions; we just need the current
        // transform to convert back to the model space the game expects for rendering.
        var skeletonMoved = false;
        if (skel.HasRoot)
        {
            var skeletonRoot = skel.Root;
            var newSkelPos = skeletonRoot.Position;

            // If skeleton moved significantly, re-raycast ground at new position
            var skelDist = (newSkelPos - skelWorldPos).Length();
            if (skelDist > 0.1f)
            {
                // A follow-driven move (we pushed the DrawObject to track the flung corpse)
                // must not restart the biomechanical settle — otherwise a corpse sliding under
                // follow wakes its bodies every frame and never rests. Only settle on EXTERNAL
                // repositions (dismount / death transition). followWasActive means we wrote a
                // follow position on the previous frame, so this frame's skeleton move is ours.
                // Ground is still re-raycast below in both cases so the floor tracks the corpse.
                if (!(followWasActive && isLocalPlayerTarget))
                    skeletonMoved = true;
                if (settings.RagdollVerboseLog)
                    log.Info($"[Ragdoll F{frameCount}] Skeleton moved {skelDist:F3}m: ({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3})→({newSkelPos.X:F3},{newSkelPos.Y:F3},{newSkelPos.Z:F3})");
                if (world.Raycast(RaycastSite.SkeletonMoved,
                        new Vector3(newSkelPos.X, newSkelPos.Y + TerrainRaycastStartYOffset, newSkelPos.Z),
                        new Vector3(0, -1, 0),
                        TerrainRaycastDistance,
                        out var hitInfo))
                {
                    realGroundY = hitInfo.Point.Y;
                    groundY = realGroundY;
                    if (settings.RagdollVerboseLog)
                        log.Info($"[Ragdoll F{frameCount}] Ground re-raycast: realY={realGroundY:F3} physicsY={groundY:F3}");
                }
            }

            skelWorldPos = newSkelPos;
            skelWorldRot = skeletonRoot.Rotation;
            skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        }


        if (skeletonMoved)
            BeginBiomechanicalSettle();

        // Consume Framework.Update requests and drive the finite-mass swarm carriers only from
        // the render/physics owner. This ordering also makes their target velocity visible to the
        // resting decision below before the simulation is allowed to skip a step.
        UpdateNpcTraversalProxies(dt);

        var biomechanicalSettleActive = biomechanicalSettleRemaining > 0f;
        if (biomechanicalSettleActive)
            WakeRagdollBodiesForBiomechanicalSettle();

        // Resting fast-path: once the rig is fully asleep there is nothing left to react to (an
        // asleep body has ~0 velocity, so there is no energy left to pump), so skip the physics
        // step, the per-frame NPC-collision static rebuild, and hair — we just re-assert the held
        // pose below. Applies to generic (monster) rigs too: if their auto-built network never
        // settles, prevAllAsleep simply stays false and this never triggers; if it does settle,
        // there is nothing to clamp. A grab or a moved skeleton forces a step.
        var externalBodyAwake = AnyExternalBodyAwake();

        // Rough corpse centre for the collider proximity gate. Any body serves — the wake
        // radius dwarfs the rig's own extent.
        if (ragdollBones.Count > 0)
            corpseWakeCenter = simulation.Bodies.GetBodyReference(ragdollBones[0].BodyHandle).Pose.Position;
        RefreshCorpseBounds();

        UpdateWaterProbe(dt);
        var activityBlocksRest = inWater || externalBodyAwake || HairNeedsStep || npcTraversalProxyActivityThisFrame ||
            npcColliderMovedNearCorpse || grabSlots.Count > 0 ||
            directedCollapseActive ||
            entryConditioningActive || kneePowerLossActive || skeletonMoved || biomechanicalSettleActive;
        // BEPU owns rest detection for the whole constraint island. No ground-height heuristic or
        // velocity zeroing may freeze a distal limb before contact has actually settled it.
        var resting = prevAllAsleep && !activityBlocksRest;
        if (!resting)
            costStepped++;
        var colliderStart = System.Diagnostics.Stopwatch.GetTimestamp();
        npcColliderMovedNearCorpse = false; // re-armed by this frame's collider updates for the next decision

        // Collapse controllers restrength their servos before stepping so updated torque ceilings
        // take effect this tick; each advances its own phase and retires itself.
        UpdateDirectedCollapseSpike(dt);
        UpdateCollapseEntryConditioning(dt);
        UpdateKneePowerLossPattern(dt);

        // Update NPC collision volumes to track their current animated bone positions.
        // Must call UpdateBounds() after repositioning — BEPU2 doesn't auto-update
        // broad phase AABBs for statics, so without it collisions are never detected.
        // The grabbing NPC's collision is parked far away so the player body can pass through.
        // Collider poses continue to track while the island sleeps. A nearby moving collider marks
        // activity, wakes the island through contact, and prevents the resting fast path next frame.
        var npcCollisionParkPos = new Vector3(0, -9999, 0);
        for (int i = 0; i < npcCollisionStates.Count; i++)
        {
            var npcState = npcCollisionStates[i];
            try
            {
                if (suspendedNpcAddress != nint.Zero && npcState.NpcAddress == suspendedNpcAddress)
                {
                    if (npcState.IsMesh)
                    {
                        MoveNpcKinematic(ref npcState, npcState.MeshHandle, npcCollisionParkPos, Quaternion.Identity, dt);
                    }
                    else if (npcState.IsFallback)
                    {
                        MoveNpcKinematic(ref npcState, npcState.FallbackHandle, npcCollisionParkPos, Quaternion.Identity, dt);
                    }
                    else
                    {
                        for (int b = 0; b < npcState.BoneStatics.Count; b++)
                        {
                            var bs = npcState.BoneStatics[b];
                            MoveNpcBoneKinematic(ref bs, npcCollisionParkPos, Quaternion.Identity, dt);
                            npcState.BoneStatics[b] = bs;
                        }
                    }
                    npcCollisionStates[i] = npcState;
                    continue;
                }

                // Distance gate: a character that cannot reach the corpse cannot contact it, so
                // skip the expensive skeleton read + per-bone reposition.
                var npcRootPos = actors.GetPosition(npcState.NpcAddress, ActorReadSite.ColliderRoot);
                if (rec != null) RecActorPosition(ActorSite.ColliderRoot, npcState.NpcAddress, npcRootPos);
                if (!NpcMayTouchCorpse(npcState, npcRootPos))
                {
                    // Park its capsules far below ONCE on exit so a character that walks away
                    // from the corpse doesn't leave its body-shaped statics frozen on top of
                    // the ragdoll (invisible "ghost" collision). Skip cheaply thereafter.
                    if (!npcState.Parked)
                    {
                        ParkNpcStatics(ref npcState, npcCollisionParkPos, dt);
                        npcState.Parked = true;
                        npcCollisionStates[i] = npcState;
                    }
                    continue;
                }
                costTracked++;
                if (npcState.Parked)
                {
                    // Back in range — the reposition below snaps the statics onto the skeleton.
                    npcState.Parked = false;
                    npcCollisionStates[i] = npcState;
                }

                if (npcState.IsMesh)
                {
                    var npcSkelMesh = skeletons.TryGetSkeleton(npcState.NpcAddress, SkeletonReadSite.ColliderMeshRoot);
                    Vector3 meshWorldPos;
                    Quaternion meshWorldRot;
                    if (npcSkelMesh != null && npcSkelMesh.HasRoot)
                    {
                        var meshRoot = npcSkelMesh.Root;
                        meshWorldPos = meshRoot.Position;
                        meshWorldRot = meshRoot.Rotation;
                    }
                    else
                    {
                        meshWorldPos = actors.GetPosition(npcState.NpcAddress, ActorReadSite.ColliderMesh);
                        meshWorldRot = Quaternion.Identity;
                    }

                    if (rec != null) RecActorTransform(ActorSite.ColliderMesh, npcState.NpcAddress, meshWorldPos, meshWorldRot, false, Vector3.One);
                    MoveNpcKinematic(ref npcState, npcState.MeshHandle, meshWorldPos, meshWorldRot, dt);
                    npcCollisionStates[i] = npcState;
                    continue;
                }

                if (npcState.IsFallback)
                {
                    // Simple single-capsule position update
                    var fallbackRoot = actors.GetPosition(npcState.NpcAddress, ActorReadSite.ColliderFallback);
                    var visualScale = TryGetDrawObjectScale(npcState.NpcAddress, out var drawScale)
                        ? drawScale
                        : Vector3.One;
                    if (rec != null) RecActorTransform(ActorSite.ColliderFallback, npcState.NpcAddress, fallbackRoot, Quaternion.Identity, true, visualScale);
                    RescaleNpcFallbackCollision(ref npcState, CollisionScale(visualScale));
                    MoveNpcKinematic(ref npcState, npcState.FallbackHandle,
                        new Vector3(fallbackRoot.X, fallbackRoot.Y + 0.8f * visualScale.Y, fallbackRoot.Z),
                        Quaternion.Identity, dt);
                    npcState.TraversalSampleRoot = fallbackRoot;
                    npcState.HasTraversalSampleRoot = true;
                    npcCollisionStates[i] = npcState;
                    continue;
                }

                // Bone-based update: read NPC skeleton and reposition each bone static
                var ns = skeletons.TryGetSkeleton(npcState.NpcAddress, SkeletonReadSite.ColliderFrame);
                if (rec != null) RecSkeleton(RecordType.SkeletonPose, PosePurpose.ColliderFrame, npcState.NpcAddress, ns, false);
                if (ns == null) continue; // skeleton temporarily unavailable, keep last positions

                if (!ns.HasRoot) continue;

                var npcRoot = ns.Root;
                var npcSkelPos = npcRoot.Position;
                var npcSkelRot = npcRoot.Rotation;
                var npcVisualScale = GetCharacterScale(npcState.NpcAddress, ns);
                RescaleNpcBoneCollision(ref npcState, CollisionScale(npcVisualScale));
                npcState.TraversalSampleRoot = npcSkelPos;
                npcState.HasTraversalSampleRoot = true;

                var doStrike = attackStrikeTimer > 0f &&
                               (npcState.NpcAddress == strikeColliderAddress ||
                                strikeColliderAddresses.Contains(npcState.NpcAddress));
                var lightweightAttackMode = npcState.Lightweight && doStrike &&
                                            npcState.LightweightAttackProxyIndex >= 0;
                if (npcState.LightweightAttackMode != lightweightAttackMode)
                {
                    npcState.LightweightAttackMode = lightweightAttackMode;
                    npcState.LightweightExtrasParked = false;
                }
                if (npcState.Lightweight && !npcState.LightweightExtrasParked)
                {
                    // Keep the bodies allocated: removing collidables while NarrowPhase is flushing
                    // is the exact BEPU beta path that raised SequentialFallbackBatch's NRE. Parking
                    // the inactive proxies is deterministic and lets full detail be restored safely.
                    for (var b = 0; b < npcState.BoneStatics.Count; b++)
                    {
                        if (IsNpcBoneProxyActive(npcState, b, lightweightAttackMode))
                            continue;
                        var parked = npcState.BoneStatics[b];
                        MoveNpcBoneKinematic(ref parked, npcCollisionParkPos, Quaternion.Identity, dt);
                        npcState.BoneStatics[b] = parked;
                    }
                    npcState.LightweightExtrasParked = true;
                }
                else if (!npcState.Lightweight)
                {
                    npcState.LightweightExtrasParked = false;
                }

                for (int b = 0; b < npcState.BoneStatics.Count; b++)
                {
                    if (!IsNpcBoneProxyActive(npcState, b, lightweightAttackMode))
                        continue;
                    var bs = npcState.BoneStatics[b];
                    if (bs.BoneIndex < 0 || bs.BoneIndex >= ns.BoneCount) continue;
                    if (bs.ParentBoneIndex < 0 || bs.ParentBoneIndex >= ns.BoneCount) continue;

                    // Read parent→child segment from current animation pose
                    ref var parentMt = ref ns.ModelPose[bs.ParentBoneIndex];
                    var parentWorldPos = NpcModelToWorld(
                        new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                        npcSkelPos, npcSkelRot, npcVisualScale);

                    ref var childMt = ref ns.ModelPose[bs.BoneIndex];
                    var childWorldPos = NpcModelToWorld(
                        new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                        npcSkelPos, npcSkelRot, npcVisualScale);

                    var segment = childWorldPos - parentWorldPos;
                    var segLen = segment.Length();

                    Vector3 capsuleCenter;
                    Quaternion capsuleRot;
                    if (segLen > 0.01f)
                    {
                        var segDir = segment / segLen;
                        capsuleCenter = parentWorldPos + (segLen * bs.CenterFactor) * segDir;
                        capsuleRot = RotationFromYToDirection(segment);
                    }
                    else
                    {
                        capsuleCenter = parentWorldPos;
                        capsuleRot = Quaternion.Identity;
                    }

                    var bodyRef = simulation.Bodies.GetBodyReference(bs.Handle);
                    var oldPos = bodyRef.Pose.Position -
                                 Vector3.Transform(bs.ShapeCenterOffset, bodyRef.Pose.Orientation);
                    var shapeCenter = capsuleCenter +
                                      Vector3.Transform(bs.ShapeCenterOffset, capsuleRot);
                    var strikeTravel = capsuleCenter - oldPos;
                    var strikeMotionValid = strikeTravel.LengthSquared() <=
                                            MathF.Pow(MathF.Max(2f, CollisionScale(npcVisualScale) * 2f), 2f);
                    MoveNpcBoneKinematic(ref bs, shapeCenter, capsuleRot, dt);
                    npcState.BoneStatics[b] = bs;
                    var collisionScale = bs.AppliedCollisionScale > 0f ? bs.AppliedCollisionScale : 1f;
                    var boneReach = Vector3.Distance(capsuleCenter, npcRootPos) +
                                    (bs.BaseHalfLength + bs.BaseRadius) * collisionScale;
                    if (boneReach > npcState.Reach && float.IsFinite(boneReach))
                        npcState.Reach = boneReach;

                    // Monster strike: this collider bone moved from oldPos→capsuleCenter this frame.
                    // During the attack window, impart that swing velocity to nearby ragdoll bodies,
                    // so a fast-swinging limb forcefully flings the body at the real contact point.
                    if (doStrike && dt > 0f && strikeMotionValid)
                    {
                        // Keep infinite-mass animated contact gentle. The separately measured
                        // strike impulse below is the user-controlled part of the reaction.
                        SoftenAttackContactVelocity(bodyRef);
                        var swingVel = strikeTravel / dt;
                        if (swingVel.LengthSquared() > StrikeMinSpeed * StrikeMinSpeed)
                            ApplyStrikeImpulse(capsuleCenter, swingVel, StrikeContactRadius);
                    }
                }
                npcCollisionStates[i] = npcState;
            }
            catch { }
        }

        costColliderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - colliderStart;
        var physicsStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Ensure per-frame buffers exist (cur reconstruction + interpolation prev), sized to
        // the bone count. Allocated here (before stepping) so prev can be snapshotted in the loop.
        var boneCount = ragdollBones.Count;
        if (cachedWorldPositions == null || cachedWorldPositions.Length < boneCount)
        {
            cachedWorldPositions = new Vector3[boneCount];
            cachedWorldRotations = new Quaternion[boneCount];
            cachedBoneValid = new bool[boneCount];
            prevWorldPositions = new Vector3[boneCount];
            prevWorldRotations = new Quaternion[boneCount];
            hasPrevPhysicsState = false; // buffers reallocated — old snapshot is stale
        }

        // Restore any recoil-relaxed joint motors whose window has elapsed.
        TickRecoilRelaxers(dt);

        // Step physics with a fixed timestep, advancing as many substeps as real
        // wall-clock time has accumulated. This keeps ragdoll motion the same speed
        // regardless of the game's framerate (a fixed 1/60 per render frame ran slow
        // below 60fps and fast above it). Below 60fps we take multiple substeps; above
        // 60fps some frames take zero — render interpolation (below) keeps motion smooth.
        // When resting (fully settled) we skip stepping entirely.
        int substeps = 0;
        if (impactFreezeRemaining > 0f)
        {
            // Impact hitstop. The corpse simply stops, mid-air, for a few dozen milliseconds — and this
            // is where most of the weight actually comes from. The same motion with a freeze frame in it
            // reads heavy; without one it reads light. It is why a fighting game's knockdown lands like
            // a sack of cement while a physically identical ragdoll reads like a balloon.
            //
            // The accumulator is dropped rather than banked, or the frozen time would all come back at
            // once as a burst of catch-up substeps and undo the pause.
            impactFreezeRemaining -= dt;
            physicsAccumulator = 0f;
        }
        else if (!resting)
        {
            physicsAccumulator += dt;
            var meshFrameStart = activeRagdollMeshVolumes && !activeRagdollIsGeneric
                ? clock.GetTimestamp(ClockSite.MeshFrameEarlyOut) : 0;
            meshCollisionTicks = meshSolveTicks = 0;
            var maxLinear = ActiveClassProfile.ClassConstants.MaxLinearVelocity;
            var maxAngular = ActiveClassProfile.ClassConstants.MaxAngularVelocity;
            // In a slow frame (a crowd of corpses, a hitch) one substep only: catching up with
            // several made each slow frame slower still, every corpse multiplying it. The corpse
            // moves in slow motion for those frames instead.
            var substepCap = dt > SlowFrameSeconds ? 1 : MaxSubstepsPerFrame;
            while (physicsAccumulator >= FixedTimestep && substeps < substepCap)
            {
                // Snapshot the pre-tick bone pose for render interpolation. Overwritten each
                // iteration, so it ends as the state immediately before the final tick.
                CapturePrevBodyPoses(boneCount);
                hasPrevPhysicsState = true;
                DriveGrabConstraints(FixedTimestep); // advance finite-force targets before solve
                ApplyCachedMeshLod();
                ApplyFallGravity(FixedTimestep); // a descent that bites, on top of the integrator's honest g
                ApplyWaterForces(FixedTimestep);
                var steppedExternally = false;
                if (extensionSet.Step.Length != 0) extensionSet.OnSubstep(FixedTimestep, ref steppedExternally);
                if (!steppedExternally) StepPhysicsSimulation(FixedTimestep);
                ClampVelocities(maxLinear, maxAngular);
                ApplyStandingAnchorCorrection();
                UpdateCharacterSurfaceRuntime();
                StepHairRig(); // one-way guides follow the accepted head pose
                DetectImpact(FixedTimestep);
                if (rec != null) RecStep(substeps, steppedExternally);
                physicsAccumulator -= FixedTimestep;
                substeps++;

                // The impact staged a hitstop: stop stepping NOW, or the rest of this frame's catch-up
                // substeps would step straight through the pause we just asked for.
                if (impactFreezeRemaining > 0f)
                {
                    physicsAccumulator = 0f;
                    break;
                }
                // A costly mesh tick must not trigger several more costly catch-up
                // ticks in the same render hook. Keep the fixed solver timestep;
                // discard excess wall time just as at the existing substep cap.
                if (meshFrameStart != 0 &&
                    clock.GetElapsedTime(ClockSite.MeshFrameEarlyOut, meshFrameStart).TotalSeconds >= FixedTimestep)
                {
                    if (rec != null) RecDecision("MeshFrameEarlyOut", true);
                    physicsAccumulator = 0f;
                    break;
                }
            }
            ReportMeshPhysicsBudget(meshFrameStart, substeps);
            // If we hit the substep cap (severe hitch / very low fps), drop the backlog so
            // we don't fire a burst of catch-up steps on the next frame (spiral of death).
            if (substeps == substepCap)
                physicsAccumulator = 0f;
        }
        else
        {
            physicsAccumulator = 0f;
        }
        costPhysicsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - physicsStart;

        CacheNpcTraversalProxyPoses();

        // Snap any soft-tissue body that diverged/NaN'd this frame back onto its anchor
        // BEFORE the write-back reads body poses — flung flesh must never reach the mesh.
        ContainSoftTissueBodies();
        UpdateCharacterSurfaceRuntime();

        if (biomechanicalSettleRemaining > 0f)
            biomechanicalSettleRemaining = MathF.Max(0f, biomechanicalSettleRemaining - dt);

        // Fraction of a tick elapsed past the last completed tick. The write-back blends the
        // current physics pose toward it from the previous tick's pose, so the rendered ragdoll
        // advances a little every frame instead of snapping once per 60Hz tick. Resting or no
        // prior tick → render the current pose directly (alpha = 1).
        var renderAlpha = (!resting && hasPrevPhysicsState)
            ? Math.Clamp(physicsAccumulator / FixedTimestep, 0f, 1f)
            : 1f;
        if (rec != null) RecFrameEnd(substeps, resting, renderAlpha);

        var pose = skel.ModelPose;

        // Save original positions/rotations for delta tracking (needed for j_kao propagation).
        // Reuse a cached result across frames — StepAndApply runs every render frame, so a
        // fresh allocation here is steady GC pressure per active ragdoll.
        var result = cachedResult ??= new BoneModificationResult(skel.BoneCount);
        result.Reset(skel.BoneCount);
        for (int i = 0; i < skel.BoneCount; i++)
        {
            ref var m = ref pose[i];
            result.OriginalPositions[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            result.OriginalRotations[i] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        frameCount++;
        var logThisFrame = settings.RagdollVerboseLog && (frameCount <= 3 || frameCount % 12 == 0);

        // --- Pass 1: Read physics bodies, compute bone world positions/rotations ---
        // We need all positions first to measure how far the ragdoll sank below
        // the real terrain (due to the lowered physics ground), then correct uniformly.
        var worldPositions = cachedWorldPositions;
        var worldRotations = cachedWorldRotations!;
        var boneValid = cachedBoneValid!;
        Array.Clear(boneValid, 0, boneCount);
        var anyAwake = false;

        for (int i = 0; i < boneCount; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;

            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            if (bodyRef.Awake) anyAwake = true;

            // Guard against NaN from physics explosion — deactivate instead of crashing.
            // Sum-check covers ALL pose components: a NaN in Orientation.X/Y/Z with a finite W
            // used to slip through for one frame and reach the mesh as garbage vertices.
            if (float.IsNaN(bodyRef.Pose.Position.X + bodyRef.Pose.Position.Y + bodyRef.Pose.Position.Z +
                            bodyRef.Pose.Orientation.X + bodyRef.Pose.Orientation.Y +
                            bodyRef.Pose.Orientation.Z + bodyRef.Pose.Orientation.W))
            {
                log.Warning($"RagdollController: NaN detected in body '{rb.Name}', deactivating");
                Deactivate();
                return;
            }

            // Reconstruct bone world rotation from physics capsule rotation + stored offset
            worldRotations[i] = Quaternion.Normalize(bodyRef.Pose.Orientation * rb.CapsuleToBoneOffset);

            // Convert capsule center (at segment midpoint) back to bone origin position.
            // The bone is at the parent-end of the capsule: center - halfLength * capsuleY.
            var profileOrigin = bodyRef.Pose.Position -
                                Vector3.Transform(rb.ShapeCenterOffset, bodyRef.Pose.Orientation);
            if (rb.SegmentHalfLength > 0)
            {
                var capsuleYAxis = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
                worldPositions[i] = profileOrigin - rb.SegmentHalfLength * capsuleYAxis;
            }
            else
            {
                worldPositions[i] = profileOrigin;
            }

            boneValid[i] = true;
        }
        if (AnyExternalBodyAwake() || HairNeedsStep) anyAwake = true;

        EnsureTerrainPatchCoverage(worldPositions, boneValid, boneCount);

        // Remember whether the rig is now fully asleep so next frame can take the
        // resting fast-path. Only meaningful once at least one body exists.
        prevAllAsleep = !anyAwake && boneCount > 0;

        // --- Floor offset correction ---
        // Currently a no-op: bodies settle naturally on the terrain mesh, so no uniform
        // Y shift is applied. Kept as a zero so the write-back loop below reads uniformly;
        // restore a real value here if capsule-vs-terrain sinking ever needs correcting.
        float yCorrection = 0f;

        // --- Frame summary (once per log frame, before per-bone data) ---
        if (logThisFrame)
        {
            var awakeBodies = 0;
            var maxLinVel = 0f;
            var maxAngVel = 0f;
            var lowestCapsuleBottomY = float.MaxValue;
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i]) continue;
                var rb = ragdollBones[i];
                var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
                if (bodyRef.Awake) awakeBodies++;
                var linSpeed = bodyRef.Velocity.Linear.Length();
                var angSpeed = bodyRef.Velocity.Angular.Length();
                if (linSpeed > maxLinVel) maxLinVel = linSpeed;
                if (angSpeed > maxAngVel) maxAngVel = angSpeed;

                activeDefByName.TryGetValue(rb.Name, out var boneDef);
                var capsuleYDir = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
                var capsuleBottomY = BodyBottom(rb, bodyRef);
                if (capsuleBottomY < lowestCapsuleBottomY)
                    lowestCapsuleBottomY = capsuleBottomY;
            }
            log.Info($"[Ragdoll F{frameCount}] t={frameCount / 60f:F2}s awake={awakeBodies}/{boneCount} " +
                     $"yCorr={yCorrection:F3} lowestY={lowestCapsuleBottomY:F3} realGnd={realGroundY:F3} " +
                     $"maxLinVel={maxLinVel:F3} maxAngVel={maxAngVel:F3}");
        }

        // --- Pass 2: Apply correction and write to ModelPose ---
        for (int i = 0; i < boneCount; i++)
        {
            if (!boneValid[i]) continue;
            var rb = ragdollBones[i];

            // Blend from the previous physics tick toward the current one for smooth motion
            // between 60Hz ticks. At alpha = 1 these return the current pose exactly.
            var boneWorldPos = renderAlpha >= 1f
                ? worldPositions[i]
                : Vector3.Lerp(prevWorldPositions![i], worldPositions[i], renderAlpha);
            var boneWorldRot = renderAlpha >= 1f
                ? worldRotations[i]
                : Quaternion.Slerp(prevWorldRotations![i], worldRotations[i], renderAlpha);
            boneWorldPos.Y += yCorrection;

            var modelPos = WorldToModel(boneWorldPos);
            var modelRot = WorldRotToModel(boneWorldRot);

            if (logThisFrame)
            {
                var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
                var wr = worldRotations[i];
                var lv = bodyRef.Velocity.Linear;
                var av = bodyRef.Velocity.Angular;
                log.Info($"[Ragdoll F{frameCount}] '{rb.Name}' " +
                         $"wPos=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                         $"wRot=({wr.X:F3},{wr.Y:F3},{wr.Z:F3},{wr.W:F3}) " +
                         $"linV=({lv.X:F3},{lv.Y:F3},{lv.Z:F3}) angV=({av.X:F3},{av.Y:F3},{av.Z:F3}) " +
                         $"awake={bodyRef.Awake}");
            }

            WriteBoneTransform(skel, rb.BoneIndex, modelPos, modelRot, result);
        }

        // Propagate ragdoll movement to non-ragdoll descendant bones (hands, fingers, toes).
        // Without this, these bones stay at their frozen animation positions while their
        // ragdoll parent bones move via physics, causing mesh tearing at boundaries.
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            if (ragdollBoneIndices.Contains(i)) continue; // already handled by physics

            var parentIdx = skel.Parent(i);
            if (parentIdx < 0 || !result.HasAccumulated[parentIdx]) continue;

            var parentDelta = result.AccumulatedDeltas[parentIdx];
            var parentOrigPos = result.OriginalPositions[parentIdx];
            ref var parentModel = ref pose[parentIdx];
            var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);

            // Rotate this bone around its parent's original position, then translate by parent displacement
            var relPos = result.OriginalPositions[i] - parentOrigPos;
            relPos = Vector3.Transform(relPos, parentDelta);
            var newPos = parentOrigPos + relPos + (parentNewPos - parentOrigPos);
            var newRot = Quaternion.Normalize(parentDelta * result.OriginalRotations[i]);

            WriteBoneTransform(skel, i, newPos, newRot, result);
        }

        // Propagate j_kao changes to face/hair partial skeletons
        if (kaoBodyBoneIndex >= 0 && result.HasAccumulated[kaoBodyBoneIndex])
        {
            PropagateToPartialSkeletons(skel, kaoBodyBoneIndex, Bones.Head, result);
        }

        // Soft-tissue squash & stretch: annotate per-bone scale after every positional write
        // of the frame (position/rotation writes leave the scale channel untouched, so order
        // within the frame only matters for reading an unmodified base scale on first touch).
        UpdateSquashAndStretch(skel, dt);

        // Dismemberment POC: collapse each selected limb's whole bone subtree to ~0 scale so it
        // vanishes from the body (the "hide" half of hide-and-substitute). Done last so it overrides
        // the physics/propagation writes for those bones.
        var severedBones = useLocalDismemberBones ? localDismemberBones : settings.DismemberPocBones;
        if (severedBones != null && severedBones.Count > 0)
            foreach (var boneName in severedBones)
                HideLimbSubtree(skel, boneName);

        // Reassert flexible hair after head propagation, including settled frames.
        if (hairRigActive) ReadbackHairRig(skel);
        if (rec != null) RecSkeleton(RecordType.PoseOut, PosePurpose.FrameOut, targetCharacterAddress, skel, true);

        // (Dev) Keep the character visible when the ragdoll is flung far from the frozen death
        // position. Culling tests the DrawObject's bounding sphere, whose centre tracks the
        // DrawObject's own position — NOT the skinned bone positions — so a corpse that flies
        // out of the original frustum gets culled even though its bones are drawn far away.
        //
        // Local player: move ONLY the DrawObject (client-side render transform). We must never
        // touch GameObject.Position here — that logical position feeds the server position-sync
        // packets, and teleporting it is a bannable action. DrawObject.Position is pure render
        // state (the knob SimpleHeels/Brio drive) and never leaves the client. The game
        // re-derives skeleton->Transform.Position from it, so next frame our world→model bone
        // conversion re-centres on the new root and the corpse renders in the exact same world
        // spot — only the culling sphere now follows it. GameObject.Position stays frozen at the
        // death spot (as MovementBlockHook already enforces), so the server sees no movement.
        //
        // NPC phantoms: the server has no knowledge of them, so we keep moving the real
        // GameObject.Position via SetApproachPosition, which also drags their nameplate / HP-bar
        // anchor along with the flying corpse.
        //
        if (ragdollBones.Count > 0 && targetCharacterAddress != nint.Zero)
        {
            try
            {
                // Record only — the actual transform write is deferred to the framework tick
                // (ApplyDeferredFollowTransform). Writing it here, inside the render hook,
                // corrupts the scene graph the game is currently walking.
                var rootBody = simulation.Bodies.GetBodyReference(ragdollBones[0].BodyHandle);
                pendingFollowPos = rootBody.Pose.Position;
                pendingFollowIsLocalPlayer = isLocalPlayerTarget;
            }
            catch { }
            followWasActive = true;
        }
        else if (followWasActive && targetCharacterAddress != nint.Zero)
        {
            // Toggled off — restore the frozen death position on whichever transform we moved
            // (same deferral).
            pendingFollowPos = savedCharacterPosition;
            pendingFollowIsLocalPlayer = isLocalPlayerTarget;
            followWasActive = false;
        }
    }
}
