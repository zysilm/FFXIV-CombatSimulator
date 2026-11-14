// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Recording;
using CombatSimulator.Animation.SkeletonProfiles;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using CombatSimulator.Animation.Hosting;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private bool InitializePhysics()
    {
        waterLevel = float.NaN;
        waterProbeCountdown = 0f;
        waterClock = 0f;
        inWater = false;
        activeRagdollMeshVolumes = settings.RagdollMeshVolumes;
        activeMeshCacheRevision = settings.RagdollMeshCacheRevision;
        var skel = skeletons.TryGetSkeleton(targetCharacterAddress, SkeletonReadSite.Init);
        if (skel == null) return false;
        initialBoneCount = skel.BoneCount;
        if (rec != null) RecInitBegin(skel);
        var BoneDefs = GetBoneDefs();

        // Get skeleton transform for proper world↔model conversion
        // This is the authoritative transform — NOT GameObject.Rotation (which is just a float yaw).
        if (!skel.HasRoot) return false;

        var skeletonRoot = skel.Root;
        skelWorldPos = skeletonRoot.Position;
        skelWorldRot = skeletonRoot.Rotation;
        skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        skelWorldScale = GetCharacterScale(targetCharacterAddress, skel);

        log.Info($"RagdollController: Skeleton transform pos=({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3}) " +
                 $"rot=({skelWorldRot.X:F3},{skelWorldRot.Y:F3},{skelWorldRot.Z:F3},{skelWorldRot.W:F3}) " +
                 $"scale=({skelWorldScale.X:F3},{skelWorldScale.Y:F3},{skelWorldScale.Z:F3})");

        // Resolve bone indices
        var nameToIndex = new Dictionary<string, int>();
        // Name → definition, used to walk the config parent chain when a bone's
        // direct parent is missing from the skeleton (see parent resolution below).
        var defByName = new Dictionary<string, RagdollBoneDef>();
        ragdollBones.Clear();
        softBodyBodyHandles.Clear();
        softBodyLeashes.Clear();
        // Squash state must not survive into a new rig: stale TouchedScale/BaseScale entries
        // from the previous corpse would map onto different bone indices.
        squashStates = null;

        foreach (var def in BoneDefs)
        {
            defByName[def.Name] = def;

            var idx = ResolveBoneIndex(skel, def.Name);
            if (idx < 0)
            {
                log.Warning($"RagdollController: Bone '{def.Name}' not found, skipping");
                continue;
            }
            nameToIndex[def.Name] = idx;
        }

        // --- Humanoid vs non-humanoid dispatch ---
        // Humanoid skeletons keep the hand-tuned human profile and existing passes,
        // completely unchanged. Non-humanoid skeletons (bats, birds, dragons,
        // quadrupeds) get a ragdoll generated from their real bone topology with
        // adaptive capsule sizing and generic ball joints, then run the SAME passes.
        bool genericSkeleton = !IsHumanoidSkeleton(nameToIndex);
        activeRagdollIsGeneric = genericSkeleton;
        if (genericSkeleton)
        {
            var (genDefs, genNameToIndex) = BuildGenericSkeletonDefs(skel);
            log.Info($"RagdollController: non-humanoid skeleton — generated {genDefs.Length} generic ragdoll bones (human matches were {nameToIndex.Count})");
            BoneDefs = genDefs;
            nameToIndex = genNameToIndex;
            defByName.Clear();
            foreach (var def in BoneDefs)
                defByName[def.Name] = def;
        }

        // Mod-skeleton soft tissue (Rue/YAS/IVCS): humanoid rigs only — the generic path
        // renames bones (gen_N) and auto-sizes everything itself.
        //
        // And the LOCAL PLAYER only. Soft tissue is by far the most expensive thing a rig can carry:
        // under All-bones coverage it quadruples the body count (~40 -> ~168) and adds several
        // hundred constraints, all of which are solved every substep until the corpse settles. That
        // was being paid per humanoid NPC corpse too, so a wave that died together multiplied it by
        // the wave — which is exactly the activation spike, and it cleared up as they settled onto
        // the resting fast path. The camera is on your own body; a mook's flesh jiggle is not worth
        // one frame of everyone else's.
        if (!genericSkeleton && targetIsLocalPlayer && settings.RagdollSoftTissueModBones)
            BoneDefs = AppendModSoftTissueDefs(skel, BoneDefs, nameToIndex, defByName);

        BoneDefs = BuildActivePhysicsDefsForDismemberment(skel, BoneDefs, nameToIndex);

        // Record the active defs so StepAndApply reads capsule extents from the set
        // actually in use (human or generated) rather than re-deriving the human set.
        activeDefByName.Clear();
        foreach (var def in BoneDefs)
            activeDefByName[def.Name] = def;
        if (rec != null) RecBoneDefs(BoneDefs, nameToIndex, genericSkeleton);

        // Raycast for ground height
        groundY = skelWorldPos.Y;
        if (world.Raycast(RaycastSite.InitGround,
                new Vector3(skelWorldPos.X, skelWorldPos.Y + TerrainRaycastStartYOffset, skelWorldPos.Z),
                new Vector3(0, -1, 0),
                TerrainRaycastDistance,
                out var hitInfo))
        {
            groundY = hitInfo.Point.Y;
        }
        log.Info($"RagdollController: Raycast ground Y={groundY:F3}");

        // Create BEPU simulation
        // When self-collision is enabled, ConnectedPairs filters only directly jointed bodies;
        // every non-adjacent pair can contact (forearm vs torso, legs vs legs, and so on).
        // When disabled (null), only body-static collisions are allowed.
        // Generic (non-humanoid) ragdolls force self-collision OFF: their generated
        // capsules commonly overlap (fat/compact bodies like toads), and body-body
        // contact on overlapping capsules produces explosive separation forces. The
        // human profile is hand-tuned so its capsules don't overlap, so it keeps it.
        var connectedPairs = !genericSkeleton
            ? new HashSet<(int, int)>()
            : null;

        // Solver budget. An NPC corpse gets its own, much cheaper allocation: the expensive part of
        // a ragdoll is the couple of seconds before it settles, and when a wave dies together those
        // windows all overlap. Nobody is looking closely at the fourth body on the floor, so it does
        // not need the player's solve rate.
        var solverIterations = targetIsLocalPlayer
            ? RagdollTuning.SolverIterations
            : RagdollTuning.NpcSolverIterations;

        // Party combat ragdolls deal with many more colliding bodies/statics, so hold a FLOOR under
        // the player's iteration count while party combat ragdolls are enabled.
        //
        // A floor, not an override: this used to assign 8 outright, which also dragged a deliberately
        // raised setting back DOWN to 8 — the opposite of the stability it was added for, and it made
        // the Advanced slider look broken (silently ignored) for anyone running with companions on.
        //
        // Player only. The floor is a blunt "the scene is busy" heuristic, and applying it to NPC
        // corpses would quietly overrule the cheap budget they were just given — in precisely the
        // busy scene that budget exists for.
        var partyRagdollActive = settings.EnableCombatCompanions &&
            (settings.PartyCompanionDeathRagdoll || settings.EnableNpcDeathRagdoll);
        if (targetIsLocalPlayer && partyRagdollActive)
            solverIterations = Math.Max(solverIterations, PartyMinSolverIterations);
        // Generic rigs build a stiffer, less-conditioned constraint network — give the
        // solver more iterations so it converges instead of pumping energy each frame.
        // This one is a stability requirement of the rig's own topology, not a scene heuristic,
        // so it holds for NPC corpses too.
        if (genericSkeleton)
            solverIterations = Math.Max(solverIterations, SkeletonProfile.Auto().ClassConstants.MinSolverIterations);

        var solverSubsteps = targetIsLocalPlayer
            ? RagdollTuning.SolverSubsteps
            : settings.NpcRagdollFineDetail
                ? RagdollTuning.NpcSolverSubsteps
                : RagdollTuning.NpcLightSolverSubsteps;

        bufferPool = new BufferPool();
        // FallbackBatchThreshold raised from the default 64: the pelvis alone carries the
        // hip/spine/cloth constraint fan-out plus self-collision and NPC-volume contacts,
        // and 2.5.0-beta's SequentialFallbackBatch NREs when removing kinematic-body
        // contacts that overflowed into it. Keep everything in synchronized batches.
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks
            {
                ConnectedPairs = connectedPairs,
                StartOverlap = startOverlap = new StartOverlapAllowance(),
                ExternalDynamicBodies = externalDynamicBodyHandles,
                ExternalRigDynamicBodies = externalRigDynamicBodyHandles,
                ExternalRigNoRagdollContactBodies = externalRigNoRagdollContactBodyHandles,
                ExternalRigConnectedPairs = externalRigConnectedPairs,
                SoftKinematicBodies = softKinematicBodyHandles,
                NpcCollisionKinematicBodies = npcCollisionKinematicBodyHandles,
                NpcTraversalGhostKinematicBodies = npcTraversalGhostKinematicBodyHandles,
                NpcTraversalDynamicBodies = npcTraversalDynamicBodyHandles,
                SoftBodyBodies = softBodyBodyHandles,
                WeaponContactBodies = weaponContactBodyHandles,
                WeaponContactProxyBodies = weaponContactProxyHandles,
                WeaponContactProxiedBodies = weaponContactProxiedBodyHandles,
                SoftBodyStaticCollision = settings.RagdollSoftTissueCollision,
                Friction = RagdollTuning.Friction,
                Config = settings,
                Extensions = extensionSet,
            },
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -RagdollTuning.Gravity, 0),
                RagdollTuning.Damping),
            new SolveDescription(solverIterations, solverSubsteps)
            {
                // Lightweight swarm collision normally keeps the actual count below the old 128
                // threshold. Leave additional headroom for the ragdoll's own joints and unusual
                // poses so the known beta fallback-removal path remains a last resort.
                FallbackBatchThreshold = 256,
            });
        if (settings.RagdollMeshVolumes && !genericSkeleton)
        {
            CollisionWorkBudget.Register(simulation.NarrowPhase.CollisionTaskRegistry, () => settings.RagdollMeshContactQuality);
            InitializeMeshTiming();
        }

        // Safety net: flat box well below the character prevents infinite falling
        // if the terrain mesh has gaps or winding issues.
        var groundThickness = 10f;
        var safetyBoxIndex = simulation.Shapes.Add(new Box(1000, groundThickness, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY - groundThickness / 2f - SafetyGroundDrop, 0),
            Quaternion.Identity,
            safetyBoxIndex));

        // Build terrain mesh(es) from raycasts to capture hills, slopes, and valleys.
        // The player patch covers the death spot. A victory-sequence grab can drag
        // the player ragdoll onto an enemy and then release it; without ground there
        // the body falls through to the safety box (~2m under the floor). So we also
        // lay a patch under each nearby enemy (their positions don't move during the
        // grab), bounded by distance + count so a large spawn wave doesn't fire tens
        // of thousands of raycasts in one frame.
        // Size the patches to this body: the ground must reach at least twice as far as the body does.
        var bodyReach = 0f;
        var reachBones = Math.Min(skel.BoneCount, skel.ParentCount);
        for (var i = 0; i < reachBones; i++)
        {
            ref var bone = ref skel.ModelPose[i];
            var offset = new Vector3(bone.Translation.X, bone.Translation.Y, bone.Translation.Z) * skelWorldScale;
            if (float.IsFinite(offset.LengthSquared())) bodyReach = MathF.Max(bodyReach, offset.Length());
        }
        (terrainPatchRadius, terrainPatchRefreshDistance, terrainPatchStep) = CollisionMeshGeometry.PatchFor(bodyReach);

        int enemyPatches = 0;
        AddTerrainPatch(skelWorldPos.X, skelWorldPos.Z, groundY);
        if (colliders.HasSelector && settings.ExtendTerrainDetection)
        {
            const float maxEnemyPatchDist = 40f;
            const int maxEnemyPatches = 16;

            var nearby = new List<(float DistSq, float X, float Z)>();
            var candidates = new List<ColliderCandidate>();
            colliders.GetSelectedNpcs(candidates);
            if (rec != null) RecSelectedNpcs("terrainPatchCandidates", candidates);
            foreach (var npc in candidates)
            {
                if (!npc.HasCharacter || npc.Address == targetCharacterAddress)
                    continue;
                var pos = actors.GetPosition(npc.Address, ActorReadSite.TerrainPatchCandidate);
                if (rec != null) RecActorPosition(ActorSite.TerrainPatchCandidate, npc.Address, pos);
                var dx = pos.X - skelWorldPos.X;
                var dz = pos.Z - skelWorldPos.Z;
                var distSq = dx * dx + dz * dz;
                if (distSq <= maxEnemyPatchDist * maxEnemyPatchDist)
                    nearby.Add((distSq, pos.X, pos.Z));
            }
            nearby.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
            int count = Math.Min(nearby.Count, maxEnemyPatches);
            for (int i = 0; i < count; i++)
            {
                AddTerrainPatch(nearby[i].X, nearby[i].Z, groundY);
                enemyPatches++;
            }
        }
        log.Info($"RagdollController: terrain patches built (player + {enemyPatches} enemies) + safety box at Y={groundY - SafetyGroundDrop:F3}");

        // --- Pass 1: Collect bone world positions and rotations ---
        var pose = skel.ModelPose;
        var boneWorldPositions = new Dictionary<string, Vector3>();
        var boneWorldRotations = new Dictionary<string, Quaternion>();
        // Handoff velocity per bone, finite-differenced against the last animated frame so the
        // physics bodies continue the animation's motion instead of starting at rest.
        var handoffSeedVel = new Dictionary<string, (Vector3 Lin, Vector3 Ang)>();
        var canSeedHandoff = handoffSampleValid
            && handoffPrevPos != null && handoffSampleDt > 1e-5f;
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var idx)) continue;
            ref var mt = ref pose[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            var worldPos = ModelToWorld(modelPos);
            var worldRot = ModelRotToWorld(modelRot);
            boneWorldPositions[def.Name] = worldPos;
            boneWorldRotations[def.Name] = worldRot;

            if (canSeedHandoff && idx < handoffPrevCount)
            {
                var prevWorldPos = handoffSkelPos + Vector3.Transform(handoffPrevPos![idx] * handoffSkelScale, handoffSkelRot);
                var prevWorldRot = Quaternion.Normalize(handoffSkelRot * handoffPrevRot![idx]);
                var lin = (worldPos - prevWorldPos) / handoffSampleDt;
                var ang = AngularVelocityFromQuats(prevWorldRot, worldRot, handoffSampleDt);
                handoffSeedVel[def.Name] = (lin, ang);
            }
        }

        // Build child lookup: for each bone, find its first child in BoneDefs
        var boneToFirstChild = new Dictionary<string, string>();
        foreach (var def in BoneDefs)
        {
            if (def.ParentName != null && !boneToFirstChild.ContainsKey(def.ParentName))
                boneToFirstChild[def.ParentName] = def.Name;
        }

        // Build first-child lookup from full skeleton hierarchy (not just BoneDefs).
        // Used for leaf bones to find the actual child bone direction (e.g., forearm
        // needs elbow→wrist direction from j_te_l, not shoulder→elbow from parent).
        var skelFirstChild = new Dictionary<int, int>();
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            var parentIdx = skel.Parent(i);
            if (parentIdx >= 0 && !skelFirstChild.ContainsKey(parentIdx))
                skelFirstChild[parentIdx] = i;
        }

        // Store real terrain level before any offset
        realGroundY = groundY;

        // Fit collision geometry before body creation. Bodies may be offset from their bone and use
        // a measured axis/length, but joint anchors and topology remain on the skeleton pivots.
        if (rec != null) RecMeshCache(frozenMeshCache, "body");
        var bodyMeshVolumes = settings.RagdollMeshVolumes && !genericSkeleton
            ? BuildBodyMeshVolumes(skel, defByName, nameToIndex, boneWorldPositions, boneWorldRotations)
            : new Dictionary<int, BodyMeshGeometry>();
        if (!settings.RagdollMeshVolumes) MeshGeometryStatus = "Rendered mesh collision disabled";
        var structuralMeshFits = !genericSkeleton &&
                                 ReadCharacterSurfaceIdentity().IsHumanoid
            ? ResolveStructuralMeshFits(skel)
            : new Dictionary<string, StructuralMeshFitter.Fit>(StringComparer.Ordinal);
        if (rec != null) RecStructuralFits(structuralMeshFits);

        // Knee/ankle hinge frames from the bind pose (see BuildBindPoseHingeFrames). Always on
        // for humanoids — unlike structural mesh geometry, this is not an Advanced Filter; it
        // replaces a fragile axis computation, not an authored collision volume.
        var bindPoseHingeFrames = !genericSkeleton
            ? BuildBindPoseHingeFrames(skel)
            : new Dictionary<string, BindPoseHingeFrame>(StringComparer.Ordinal);

        // Deliberately NOT gated on RagdollVerboseLog: that flag also turns on per-step/per-joint
        // logging elsewhere, which floods the log file. This fires once per activation regardless.
        if (!genericSkeleton)
            LogLegBoneStructureDiagnostics(skel, boneWorldPositions, boneWorldRotations);

        // --- Pass 2: Create physics bodies ---
        // Capsule center is offset from bone origin (joint) along the segment direction.
        // Capsule half-length is usually clamped to the current pose segment distance.
        // Anatomical hinges keep their profile length instead; ORZ-style death poses can
        // collapse the observed knee/elbow segment and bake a fake short limb into physics.
        var massByBone = !genericSkeleton
            ? BuildMassBudget(BoneDefs, nameToIndex, RagdollTuning.BodyMass)
            : null;
        float resolvedTotalMass = 0f; // Tier D: sum of all enabled bodies' effective mass.
        float requiredRigLift = 0f;
        float initialMeshPenetration = 0f;
        string initialMeshPenetrationBone = string.Empty;
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            Vector3 capsuleCenter;
            float segmentHalfLength;
            var ragdollShapeScale = CollisionScale(skelWorldScale);
            float effectiveHalfLength = ResolveBodyHalfLength(def) * ragdollShapeScale;
            Quaternion capsuleWorldRot;
            var fittedShapeCenterOffset = Vector3.Zero;
            var preserveAnatomicalLength = def.Joint == JointType.Hinge;
            var useStructuralMeshFit = structuralMeshFits.TryGetValue(def.Name, out var structuralMeshFit) && structuralMeshFit != null;

            if (useStructuralMeshFit && structuralMeshFit != null)
            {
                var worldAxis = NormalizeOrFallback(Vector3.Transform(
                    structuralMeshFit.AxisBoneLocal, boneWorldRot), Vector3.UnitY);
                var worldOffset = Vector3.Transform(
                    structuralMeshFit.CenterBoneLocal * ragdollShapeScale, boneWorldRot);
                capsuleWorldRot = CreateCapsuleRotation(worldAxis, boneWorldRot);
                if (structuralMeshFit.RollRadians != 0f)
                {
                    // Reapply the fit-time PCA roll correction on top of the fresh bone-roll frame
                    // (composed the same way it was measured: base rotation, then roll around its
                    // own local long axis) — see StructuralMeshFitter.TryFit.
                    capsuleWorldRot = Quaternion.Normalize(capsuleWorldRot *
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, structuralMeshFit.RollRadians));
                }
                capsuleCenter = boneWorldPos + worldOffset;
                segmentHalfLength = 0f;
                effectiveHalfLength = structuralMeshFit.HalfLength * ragdollShapeScale;
                fittedShapeCenterOffset = Vector3.Transform(
                    worldOffset, Quaternion.Inverse(capsuleWorldRot));
            }
            else if (def.HasMeshFit)
            {
                // The fit lives in unscaled model space. Apply the actor's real (possibly
                // non-uniform) scale before rotating it into world space. The body is centered on
                // the fitted vertex cluster rather than halfway toward an unrelated rig child.
                var scaledAxis = def.MeshFitAxisModel * skelWorldScale;
                var worldAxis = Vector3.Transform(
                    NormalizeOrFallback(scaledAxis, Vector3.UnitY), skelWorldRot);
                var scaledOffset = def.MeshFitCenterModelOffset * skelWorldScale;
                var worldOffset = Vector3.Transform(scaledOffset, skelWorldRot);

                capsuleWorldRot = CreateCapsuleRotation(worldAxis, boneWorldRot);
                capsuleCenter = boneWorldPos + worldOffset;
                segmentHalfLength = 0f;
                fittedShapeCenterOffset = Vector3.Transform(
                    worldOffset, Quaternion.Inverse(capsuleWorldRot));
            }
            else if (boneToFirstChild.TryGetValue(def.Name, out var childName) &&
                boneWorldPositions.TryGetValue(childName, out var childWorldPos))
            {
                var segment = childWorldPos - boneWorldPos;
                var segLen = segment.Length();

                if (segLen > 0.01f)
                {
                    // Clamp capsule half-length so the capsule doesn't extend past the
                    // child bone. Without this, bent-limb poses create overlapping capsules
                    // (e.g., shin capsule overshoots the foot when the knee is bent).
                    // Use 45% of segment length (not 50% minus radius) to preserve capsule
                    // elongation for rotational stability — near-spheres lose orientation.
                    var maxHalf = MathF.Max(0.02f, segLen * 0.45f);
                    if (!preserveAnatomicalLength && effectiveHalfLength > maxHalf)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' capsule clamped: halfLen {effectiveHalfLength:F3} -> {maxHalf:F3} (segLen={segLen:F3})");
                        effectiveHalfLength = maxHalf;
                    }
                    else if (preserveAnatomicalLength && effectiveHalfLength > maxHalf && settings.RagdollVerboseLog)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' kept anatomical halfLen {effectiveHalfLength:F3} despite pose segLen={segLen:F3}");
                    }

                    var segDir = segment / segLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * segDir;
                    segmentHalfLength = effectiveHalfLength;
                    capsuleWorldRot = CreateCapsuleRotation(segment, boneWorldRot);
                }
                else
                {
                    // Degenerate (co-located bones like j_kosi↔j_sebo_a): keep at bone position
                    capsuleCenter = boneWorldPos;
                    segmentHalfLength = 0f;
                    capsuleWorldRot = boneWorldRot;
                }
            }
            else if (skelFirstChild.TryGetValue(boneIdx, out var skelChildIdx) &&
                     skelChildIdx < skel.BoneCount)
            {
                // Leaf bone in BoneDefs but has a child in the full skeleton (e.g.,
                // forearm j_ude_b → hand j_te, foot j_asi_c → toe).
                // Use bone→skelChild direction for capsule orientation so the capsule
                // extends along the actual limb, not along the parent segment.
                ref var childMt = ref pose[skelChildIdx];
                var childModelPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                var skelChildWorldPos = ModelToWorld(childModelPos);
                var toSkelChild = skelChildWorldPos - boneWorldPos;
                var toSkelChildLen = toSkelChild.Length();

                if (toSkelChildLen > 0.01f)
                {
                    var maxHalf = MathF.Max(0.02f, toSkelChildLen * 0.45f);
                    if (!preserveAnatomicalLength && effectiveHalfLength > maxHalf)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' leaf capsule clamped: halfLen {effectiveHalfLength:F3} -> {maxHalf:F3} (segLen={toSkelChildLen:F3})");
                        effectiveHalfLength = maxHalf;
                    }
                    else if (preserveAnatomicalLength && effectiveHalfLength > maxHalf && settings.RagdollVerboseLog)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' kept anatomical leaf halfLen {effectiveHalfLength:F3} despite pose segLen={toSkelChildLen:F3}");
                    }

                    var dir = toSkelChild / toSkelChildLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * dir;
                    segmentHalfLength = effectiveHalfLength;
                    capsuleWorldRot = CreateCapsuleRotation(toSkelChild, boneWorldRot);
                }
                else
                {
                    capsuleCenter = boneWorldPos;
                    segmentHalfLength = 0f;
                    capsuleWorldRot = boneWorldRot;
                }
            }
            else if (def.ParentName != null &&
                     boneWorldPositions.TryGetValue(def.ParentName, out var parentForLeafWorldPos))
            {
                // Leaf bone with no children in either BoneDefs or skeleton, but with
                // a parent in BoneDefs (e.g., terminal skirt C-tier under chained
                // parenting). Use parent→bone direction so the capsule extends along
                // the chain instead of inheriting the rest-pose rotation, which for
                // some radial slots points upward and produces "skirt floats in air".
                var fromParent = boneWorldPos - parentForLeafWorldPos;
                var fromParentLen = fromParent.Length();
                if (fromParentLen > 0.01f)
                {
                    var dir = fromParent / fromParentLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * dir;
                    segmentHalfLength = effectiveHalfLength;
                    capsuleWorldRot = CreateCapsuleRotation(fromParent, boneWorldRot);
                }
                else
                {
                    capsuleCenter = boneWorldPos;
                    segmentHalfLength = 0f;
                    capsuleWorldRot = boneWorldRot;
                }
            }
            else
            {
                // True root with no parent: keep at bone position
                capsuleCenter = boneWorldPos;
                segmentHalfLength = 0f;
                capsuleWorldRot = boneWorldRot;
            }

            // Preserve the handoff pose; joint zero and bounds come from the fixed anatomical frame.

            var capsuleToBoneOffset = Quaternion.Normalize(
                Quaternion.Inverse(capsuleWorldRot) * boneWorldRot);

            var meshGeometry = structuralMeshFit ?? new StructuralMeshFitter.Fit();
            var useStructuralMeshGeometry = useStructuralMeshFit;
            var runtimeColliderShape = def.ColliderShape;
            float shapeRadius;
            float shapeHalfLength;
            var shapeBoxHalfExtents = Vector3.Zero;
            if (useStructuralMeshGeometry && meshGeometry.UseBox)
            {
                runtimeColliderShape = RagdollColliderShape.Box;
                shapeBoxHalfExtents = new Vector3(
                    meshGeometry.RadiusX * ragdollShapeScale,
                    effectiveHalfLength,
                    meshGeometry.RadiusZ * ragdollShapeScale);
                shapeRadius = MathF.Min(shapeBoxHalfExtents.X, shapeBoxHalfExtents.Z);
                shapeHalfLength = effectiveHalfLength;
            }
            else if (useStructuralMeshGeometry)
            {
                runtimeColliderShape = RagdollColliderShape.Capsule;
                shapeRadius = meshGeometry.RadiusX * ragdollShapeScale;
                shapeHalfLength = effectiveHalfLength;
            }
            else if (def.ColliderShape == RagdollColliderShape.Box)
            {
                runtimeColliderShape = RagdollColliderShape.Box;
                shapeBoxHalfExtents = ResolveBoxHalfExtents(def, ResolveBodyHalfLength(def)) * ragdollShapeScale;
                shapeRadius = MathF.Min(shapeBoxHalfExtents.X, shapeBoxHalfExtents.Z);
                shapeHalfLength = shapeBoxHalfExtents.Y;
            }
            else
            {
                runtimeColliderShape = RagdollColliderShape.Capsule;
                shapeRadius = def.CapsuleRadius * ragdollShapeScale;
                shapeHalfLength = effectiveHalfLength;
            }

            var effectiveMass = ResolveBoneMass(def, massByBone);
            resolvedTotalMass += effectiveMass;
            TypedIndex shapeIndex = default;
            BodyInertia bodyInertia = default;
            BodyMeshGeometry? meshVolume = null;
            MeshCollisionLodResult? meshLod = null;
            if (bodyMeshVolumes.TryGetValue(boneIdx, out var worldMesh) &&
                TryCreateBodyMesh(def.Name, worldMesh, boneWorldPos, capsuleWorldRot, effectiveMass,
                    out shapeIndex, out bodyInertia, out var meshCenter, out var localMesh, out meshLod))
            {
                meshVolume = localMesh;
                runtimeColliderShape = RagdollColliderShape.Mesh;
                capsuleCenter = meshCenter;
                segmentHalfLength = 0f;
                fittedShapeCenterOffset = Vector3.Transform(meshCenter - boneWorldPos,
                    Quaternion.Inverse(capsuleWorldRot));
                // Bounds are compatibility broad-phase metadata only. Contact and support use
                // the exact local triangle surface, never this capsule envelope.
                localMesh.Bounds(Vector3.Zero, Quaternion.Identity, out var minMesh, out var maxMesh);
                var envelope = Vector3.Max(Vector3.Abs(minMesh), Vector3.Abs(maxMesh));
                shapeRadius = new Vector2(envelope.X, envelope.Z).Length();
                shapeHalfLength = envelope.Y;
                shapeBoxHalfExtents = envelope;
            }

            // The torso and thighs collide with a hull fitted to the character's own surface. The
            // box or capsule measured above stays as the body's envelope for everything that asks
            // about its rough size; contact, centre of mass and inertia come from the hull.
            Vector3[]? hullCorners = null;
            Vector3[]? hullEdges = null;
            if (meshVolume == null && useStructuralMeshGeometry && meshGeometry.HullPoints != null &&
                TryCreateStructuralHull(meshGeometry.HullPoints, ragdollShapeScale, out var hull, out var hullCenter,
                    out var createdCorners, out var createdEdges))
            {
                hullCorners = createdCorners;
                hullEdges = createdEdges;
                shapeIndex = simulation.Shapes.Add(hull);
                bodyInertia = hull.ComputeInertia(effectiveMass);
                capsuleCenter = boneWorldPos + Vector3.Transform(hullCenter, capsuleWorldRot);
                segmentHalfLength = 0f;
                fittedShapeCenterOffset = hullCenter;
                if (runtimeColliderShape == RagdollColliderShape.Box)
                {
                    Vector3 minCorner = new(float.MaxValue), maxCorner = new(float.MinValue);
                    foreach (var corner in hullCorners)
                    {
                        minCorner = Vector3.Min(minCorner, corner);
                        maxCorner = Vector3.Max(maxCorner, corner);
                    }
                    shapeBoxHalfExtents = (maxCorner - minCorner) * 0.5f;
                    shapeRadius = MathF.Min(shapeBoxHalfExtents.X, shapeBoxHalfExtents.Z);
                    shapeHalfLength = shapeBoxHalfExtents.Y;
                }
            }

            // Clamp body center above ground so bodies don't start underground.
            // Underground capsules cause explosive ground-collision forces in the first
            // frames. Lift just enough so the capsule bottom (center - extent - radius)
            // is at the ground plane.
            // Accumulate one common lift from the actual runtime shapes. Translating the whole rig
            // together avoids preloading joints even when mesh-derived body centers are asymmetric.
            float bodyBottomExtent;
            if (meshVolume != null)
            {
                bodyBottomExtent = capsuleCenter.Y - meshVolume.Bottom(capsuleCenter, capsuleWorldRot);
                var penetration = groundY + bodyBottomExtent - capsuleCenter.Y;
                if (penetration > initialMeshPenetration)
                {
                    initialMeshPenetration = penetration;
                    initialMeshPenetrationBone = def.Name;
                }
            }
            else if (hullCorners != null)
            {
                bodyBottomExtent = 0f;
                foreach (var corner in hullCorners)
                    bodyBottomExtent = MathF.Max(bodyBottomExtent, -Vector3.Transform(corner, capsuleWorldRot).Y);
            }
            else if (runtimeColliderShape == RagdollColliderShape.Box)
            {
                var xAxis = Vector3.Transform(Vector3.UnitX, capsuleWorldRot);
                var yAxis = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
                var zAxis = Vector3.Transform(Vector3.UnitZ, capsuleWorldRot);
                bodyBottomExtent = MathF.Abs(xAxis.Y) * shapeBoxHalfExtents.X +
                                   MathF.Abs(yAxis.Y) * shapeBoxHalfExtents.Y +
                                   MathF.Abs(zAxis.Y) * shapeBoxHalfExtents.Z;
            }
            else
            {
                var yAxis = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
                bodyBottomExtent = MathF.Abs(yAxis.Y) * shapeHalfLength + shapeRadius;
            }
            var minCenterY = groundY + bodyBottomExtent + 0.005f; // 5mm clearance
            // Contact-disabled Soft Tissue bodies are inertial drivers, not ground colliders.
            // Lifting those bodies independently deforms the skinned region at activation and made
            // an oversized proxy visible even though NarrowPhase correctly rejected its contacts.
            if ((!def.SoftBody || settings.RagdollSoftTissueCollision) && capsuleCenter.Y < minCenterY)
            {
                // Collect the worst penetration. After all bodies exist, translate the entire rig
                // and every joint anchor by one common offset so no joint is preloaded.
                requiredRigLift = MathF.Max(requiredRigLift, minCenterY - capsuleCenter.Y);
            }

            // Tier D — resolve effective mass (anthropometric fraction x body mass, or
            // the hand-picked Mass when the toggle is off / bone not in the table).
            // Kept alongside the body so callers can ask for the volume the physics is actually
            // using (see TryGetBoneCapsule) rather than re-deriving it from the defs.
            var shapeCenterOffset = fittedShapeCenterOffset;
            if (meshVolume != null || hullCorners != null)
            {
                // Shape and inertia were constructed and recentred together above.
            }
            else if (runtimeColliderShape == RagdollColliderShape.Box)
            {
                var box = new Box(
                    shapeBoxHalfExtents.X * 2f,
                    shapeBoxHalfExtents.Y * 2f,
                    shapeBoxHalfExtents.Z * 2f);
                shapeIndex = simulation.Shapes.Add(box);
                bodyInertia = box.ComputeInertia(effectiveMass);
            }
            else
            {
                var capsule = new Capsule(shapeRadius, shapeHalfLength * 2f);
                shapeIndex = simulation.Shapes.Add(capsule);
                bodyInertia = capsule.ComputeInertia(effectiveMass);
            }

            bodyInertia = ApplyLimbInertia(
                bodyInertia, def, meshVolume != null || useStructuralMeshGeometry || def.HasMeshFit);

            // Allow sleeping even with settle collision; external kinematic colliders can wake
            // the corpse on contact, while permanent awake bodies keep solving tiny jitter.
            var sleepThreshold = RagdollSleepThreshold;
            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                bodyInertia,
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(sleepThreshold));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

            if (def.SoftBody)
                softBodyBodyHandles.Add(bodyHandle.Value);

            // Seed the body with the velocity the death animation was carrying at handoff, so an
            // in-motion animation flows continuously into physics instead of freezing at rest.
            if (handoffSeedVel.TryGetValue(def.Name, out var seed))
            {
                var maxLin = ActiveClassProfile.ClassConstants.MaxLinearVelocity;
                var maxAng = ActiveClassProfile.ClassConstants.MaxAngularVelocity;
                var br = simulation.Bodies.GetBodyReference(bodyHandle);
                var centerVelocity = seed.Lin;
                if (meshVolume != null)
                    centerVelocity += Vector3.Cross(seed.Ang, capsuleCenter - boneWorldPos);
                br.Velocity.Linear = ClampVectorLength(centerVelocity, maxLin);
                br.Velocity.Angular = ClampVectorLength(seed.Ang, maxAng);
            }

            // Resolve the constraint parent by walking up the config parent chain to
            // the nearest ancestor that actually exists in this skeleton. On a complete
            // human skeleton the direct parent always resolves, so this matches on the
            // first step and behaves identically to a direct lookup. On monster skeletons
            // that lack intermediate bones (e.g. no neck j_kubi between chest and head),
            // this keeps the leaf bone attached to its nearest present ancestor instead of
            // orphaning it into an unconstrained free body that drifts away from the corpse.
            int parentBoneIdx = -1;
            var ancestorName = def.ParentName;
            while (ancestorName != null)
            {
                if (nameToIndex.TryGetValue(ancestorName, out var pIdx))
                {
                    parentBoneIdx = pIdx;
                    if (ancestorName != def.ParentName)
                        log.Info($"[Ragdoll Init] '{def.Name}' parent '{def.ParentName}' missing; attached to ancestor '{ancestorName}'");
                    break;
                }

                ancestorName = defByName.TryGetValue(ancestorName, out var ancestorDef)
                    ? ancestorDef.ParentName
                    : null;
            }

            ragdollBones.Add(new RagdollBone
            {
                BoneIndex = boneIdx,
                ParentBoneIndex = parentBoneIdx,
                BodyHandle = bodyHandle,
                Name = def.Name,
                CapsuleToBoneOffset = capsuleToBoneOffset,
                SegmentHalfLength = segmentHalfLength,
                CapsuleRadius = shapeRadius,
                CapsuleHalfLength = shapeHalfLength,
                ColliderShape = runtimeColliderShape,
                BoxHalfExtents = shapeBoxHalfExtents,
                ShapeCenterOffset = shapeCenterOffset,
                HullCorners = hullCorners,
                HullEdges = hullEdges,
                MeshGeometry = meshVolume,
                MeshLod = meshLod,
                Mass = effectiveMass,
            });


            // Log initial state for diagnostics
            if (settings.RagdollVerboseLog)
            {
                var logCapsuleY = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
                log.Info($"[Ragdoll Init] '{def.Name}' idx={boneIdx} " +
                         $"bonePos=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                         $"capsuleCenter=({capsuleCenter.X:F3},{capsuleCenter.Y:F3},{capsuleCenter.Z:F3}) " +
                         $"shape={runtimeColliderShape} meshGeometry={useStructuralMeshGeometry} hull={(hullCorners?.Length ?? 0)} " +
                         $"radius={shapeRadius:F3} segHalf={segmentHalfLength:F3} bodyHalfY={shapeHalfLength:F3} " +
                         $"capsuleY=({logCapsuleY.X:F3},{logCapsuleY.Y:F3},{logCapsuleY.Z:F3})");
            }
        }

        if (settings.RagdollMeshVolumes && !genericSkeleton)
        {
            var acceptedMeshes = ragdollBones.Count(b => b.MeshGeometry != null);
            var fallbackBodies = ragdollBones.Count - acceptedMeshes;
            MeshGeometryStatus += $"; accepted {acceptedMeshes} mesh / {fallbackBodies} fallback bodies";
            int fullFaces = 0, collisionFaces = 0;
            float measuredError = 0;
            foreach (var rb in ragdollBones)
            {
                fullFaces += rb.MeshGeometry?.Indices.Length / 3 ?? 0;
                collisionFaces += rb.MeshLod?.CollisionFaceCount ?? rb.MeshGeometry?.Indices.Length / 3 ?? 0;
                measuredError = MathF.Max(measuredError, rb.MeshLod?.MaximumSampledSurfaceError ?? 0);
            }
            MeshGeometryStatus += $"; initial collision {collisionFaces}/{fullFaces} faces, sampled initial error {measuredError * 1000:F2} mm";
            log.Info($"Ragdoll collision geometry: {MeshGeometryStatus}");
        }

        // Proxy envelopes are never lifted as a whole: that fights Ragdoll Follow, which disagrees
        // with a post-lift root about where the corpse actually is and pops visibly.
        // Surface meshes cannot rely on solid-volume overlap recovery. Only their
        // real surfaces request this mandatory startup correction; proxy envelopes are left to
        // damped contact recovery (a uniform lift would fight Ragdoll Follow).
        var startupLift = initialMeshPenetration > 0f ? initialMeshPenetration + 0.005f : 0f;
        if (startupLift > 0f)
        {
            // Ordinary corrections are centimetres. Cap corrupt/remote terrain samples so one bad
            // ray cannot teleport the corpse; residual overlap is handled by damped contact recovery.
            var lift = MathF.Min(startupLift, 0.35f);
            var offset = Vector3.UnitY * lift;
            foreach (var rb in ragdollBones)
            {
                var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
                body.Pose.Position += offset;
                body.UpdateBounds();
            }
            var liftedNames = new List<string>(boneWorldPositions.Keys);
            foreach (var name in liftedNames)
                boneWorldPositions[name] += offset;
            log.Info($"[Ragdoll Init] uniformly lifted rig by {lift:F3}m (requested {startupLift:F3}m; deepest mesh='{initialMeshPenetrationBone}').");
        }
        else if (requiredRigLift > 0f)
        {
            log.Info($"[Ragdoll Init] proxy ground penetration {requiredRigLift:F3}m left to contact recovery.");
        }

        // What the rig actually weighs, kept so that anything asked to pick it up can size itself
        // against it instead of against a number somebody guessed once.
        ragdollTotalMass = resolvedTotalMass;

        // Build one compact final-collision surface cache after the rigid bodies exist. It uses
        // their real scaled dimensions and follows those same poses for every consumer.
        BuildCharacterSurfaceRuntime(genericSkeleton);

        // The bodies that actually arrive when this rig lands. Empty on a rig with no recognisable torso
        // (a non-humanoid), and IsTorsoBody then falls back to treating every body as one.
        torsoBodyIndices.Clear();
        for (int i = 0; i < ragdollBones.Count; i++)
            if (TorsoBones.IndexOf(ragdollBones[i].Name) >= 0)
                torsoBodyIndices.Add(i);

        // Tier D — one-time sanity log: resolved total should be ~RagdollBodyMass minus the
        // excluded cloth/weapon/breast bones (which keep their tiny literal masses).
        log.Info($"[Ragdoll Init] Anthropometric mass: resolved total = {resolvedTotalMass:F2} kg (body mass = {RagdollTuning.BodyMass:F1} kg)");

        // --- Pass 3: Add constraints between connected bones ---
        // One invariant anatomical topology: positional sockets own pivots; passive angular
        // constraints own allowed coordinates and bounds; finite motors provide only friction.
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        // One structural stiffness for fall, contact and grab. Keeping this above 45 Hz removes
        // visible anchor sag without retuning the rig when an external grab begins.
        var jointSpring = new SpringSettings(RagdollTuning.JointSpringFrequency, 1.5f);
        // Foot positional joint is firmer than the body default: it absorbs the hardest
        // ground-impact impulses and otherwise rubber-bands first.
        var footJointSpring = new SpringSettings(RagdollTuning.FootJointSpringFrequency, 1.5f);
        // The shoulder girdle has two positional links (chest->clavicle->upper arm). At the
        // generic stiffness both links can stretch in the same direction, making a loaded arm look
        // detached even though neither individual error is dramatic. Foot joints already prove
        // 60 Hz is stable at this solver cadence, so apply the same floor locally to the girdle.
        var shoulderJointSpring = new SpringSettings(
            MathF.Max(60f, RagdollTuning.JointSpringFrequency), 1.5f);
        var limitSpring = new SpringSettings(RagdollTuning.LimitSpringFrequency, 1);
        // Tier C (swing) — soft-edge spring for BALL swing limits: low frequency +
        // overdamped, so a joint sliding into its range edge decelerates and settles
        // instead of pinning on a hard 90 Hz wall at the extreme angle. Hinges keep
        // the hard wall (a soft knee fold-stop reads as hyperextension).
        var ballSwingSpring = new SpringSettings(RagdollTuning.SoftLimitFrequency, RagdollTuning.SoftLimitDamping);
        var motorDamping = 0.01f;

        // Capture the character-facing frame used by anatomical hinge axes.
        var anatYaw = targetCharacterAddress != nint.Zero
            ? actors.GetFacing(targetCharacterAddress, ActorReadSite.Facing)
            : 0f;
        var anatForwardWorld = new Vector3(MathF.Sin(anatYaw), 0f, MathF.Cos(anatYaw));
        // Tier C (swing) — anatomical reference axes for the directional ball limits, taken
        // from the character's facing at activation and baked into the PARENT body's local
        // frame so they tumble with the body, not from each limb's death pose.
        var anatLateralWorld = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, anatForwardWorld));
        anatomicalForward = anatForwardWorld;
        swingStressMonitors.Clear();

        // BEPU's TwistLimit measurement degenerates once a ball joint's swing exceeds ~90
        // degrees. Keep the full configured swing cone and skip twist limits on wide joints.
        const float ballJointMaxSwing = 1.40f;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            // When self-collision is enabled, exclude nearby pairs (1-2 hops). A moving limb
            // (the swing arc of an active fall, not just a resting pose) can sweep a 2-hop
            // neighbor — foot into thigh, toe into shin — and that contact fights the joint
            // constraints connecting them, which a resting corpse never triggers.
            if (connectedPairs != null)
            {
                // Exclude direct parent-child (they share a joint anchor)
                var lo = Math.Min(rb.BodyHandle.Value, parentHandle.Value);
                var hi = Math.Max(rb.BodyHandle.Value, parentHandle.Value);
                connectedPairs.Add((lo, hi));

                // Also exclude grandparent (2 hops) — nearby bodies in the chain would
                // cause collision forces that stretch the spring constraints between them.
                var parentRb = ragdollBones.Find(r => r.BoneIndex == rb.ParentBoneIndex);
                if (parentRb.ParentBoneIndex >= 0 && boneIdxToBodyHandle.TryGetValue(parentRb.ParentBoneIndex, out var grandparentHandle))
                {
                    lo = Math.Min(rb.BodyHandle.Value, grandparentHandle.Value);
                    hi = Math.Max(rb.BodyHandle.Value, grandparentHandle.Value);
                    connectedPairs.Add((lo, hi));
                }
            }

            // Find the bone definition for this bone
            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }

            // Joint anchor is at the child bone's world position
            var anchorWorld = boneWorldPositions[rb.Name];

            var childBodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var parentBodyRef = simulation.Bodies.GetBodyReference(parentHandle);

            // Local anchor offsets (world anchor → each body's local space)
            var childLocalAnchor = Vector3.Transform(
                anchorWorld - childBodyRef.Pose.Position,
                Quaternion.Inverse(childBodyRef.Pose.Orientation));
            var parentLocalAnchor = Vector3.Transform(
                anchorWorld - parentBodyRef.Pose.Position,
                Quaternion.Inverse(parentBodyRef.Pose.Orientation));

            // Segment direction along the child body's capsule axis (used for swing/twist/hinge).
            // This is the direction the limb extends in, NOT the direction from parent body
            // center to anchor (which is wrong for branching joints like shoulders where
            // the parent body is the chest and the child branches off to the side).
            var segDirWorld = Vector3.Transform(Vector3.UnitY, childBodyRef.Pose.Orientation);
            if (segDirWorld.LengthSquared() < 0.001f)
                segDirWorld = Vector3.UnitY;
            var parentSegDir = Vector3.Transform(Vector3.UnitY, parentBodyRef.Pose.Orientation);
            if (parentSegDir.LengthSquared() < 0.001f)
                parentSegDir = Vector3.UnitY;

            // --- Positional + angular constraint ---
            // SoftBody bones are never hinges even when their config says so (j_mune shipped
            // with JointType=1): the hinge path would ignore their soft spring settings and
            // strap knee-style swing/twist limits onto them. They take the ball path below,
            // where the SoftBody BallSocket + AngularServo split actually applies.
            if ((boneDef.Joint == JointType.Hinge || IsToeBone(rb.Name)) && !boneDef.SoftBody)
            {
                var toeHinge = IsToeBone(rb.Name);
                Vector3 hingeAxisWorld;
                Vector3 flexionDirectionWorld;
                if (bindPoseHingeFrames.TryGetValue(rb.Name, out var kneeBindFrame))
                {
                    // Bind-pose-derived axis (see BuildBindPoseHingeFrames): pose-independent,
                    // reapplied through the bone's LIVE skeleton rotation every activation.
                    var boneWorldRot = boneWorldRotations[rb.Name];
                    hingeAxisWorld = NormalizeOrFallback(
                        Vector3.Transform(kneeBindFrame.AxisBoneLocal, boneWorldRot), Vector3.UnitX);
                    flexionDirectionWorld = NormalizeOrFallback(
                        Vector3.Transform(kneeBindFrame.FlexionBoneLocal, boneWorldRot), anatomicalForward);
                }
                else if (toeHinge)
                {
                    hingeAxisWorld = NormalizeOrFallback(ProjectOntoPlane(
                        Vector3.Transform(Vector3.UnitX, childBodyRef.Pose.Orientation), segDirWorld), Vector3.UnitX);
                    flexionDirectionWorld = NormalizeOrFallback(Vector3.Cross(hingeAxisWorld, parentSegDir), anatomicalForward);
                }
                else
                {
                    hingeAxisWorld = ComputeProfileHingeAxis(
                        boneDef.AnatomicalRole, parentSegDir, segDirWorld, childBodyRef.Pose.Orientation);
                    flexionDirectionWorld = ComputeHingeForward(
                        boneDef.AnatomicalRole, hingeAxisWorld, parentSegDir, segDirWorld);
                }
                var hingeAxisLocalChild = NormalizeOrFallback(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitX);
                var hingeAxisLocalParent = NormalizeOrFallback(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitX);

                // Position is owned by one joint at the anatomical pivot. Its stiffness never changes
                // for grab/carry; the same load path is used in every physical state.
                var hingeBallSocket = new BallSocket
                {
                    LocalOffsetA = childLocalAnchor,
                    LocalOffsetB = parentLocalAnchor,
                    SpringSettings = toeHinge ? footJointSpring : jointSpring,
                };
                simulation.Solver.Add(rb.BodyHandle, parentHandle, hingeBallSocket);

                var alignmentSpring = new SpringSettings(
                    RagdollTuning.JointSpringFrequency, 2f);
                // Signed hinge boundaries stay firm and passive. Soft swing limits are deliberately
                // ball-only; applying them here would soften knee hyperextension.
                var passiveLimitSpring = new SpringSettings(RagdollTuning.LimitSpringFrequency, 1.5f);

                if (boneDef.AnatomicalRole == AnatomicalRole.Elbow || IsForearmBone(rb.Name))
                {
                    // The elbow is a two-coordinate joint in this one-bone forearm rig: flexion
                    // around the upper-arm hinge plus pronation/supination around the forearm.
                    // AngularSwivelHinge removes only varus/valgus and leaves those two real axes.
                    var forearmAxisLocalChild = NormalizeOrFallback(Vector3.Transform(
                        segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitY);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new AngularSwivelHinge
                        {
                            LocalSwivelAxisA = forearmAxisLocalChild,
                            LocalHingeAxisB = hingeAxisLocalParent,
                            SpringSettings = alignmentSpring,
                        });

                    // Hyperextension wall: at straight the forearm is 90 degrees from the flexion
                    // direction; only a small excursion to the wrong side is legal.
                    var flexionLocalParent = NormalizeOrFallback(Vector3.Transform(
                        flexionDirectionWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitZ);
                    var forearmLocalChild = NormalizeOrFallback(Vector3.Transform(
                        segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitY);
                    var extensionLimit = new SwingLimit
                    {
                        AxisLocalA = forearmLocalChild,
                        AxisLocalB = flexionLocalParent,
                        MaximumSwingAngle = DegreesToRadians(95f),
                        SpringSettings = passiveLimitSpring,
                    };
                    var extensionHandle = simulation.Solver.Add(rb.BodyHandle, parentHandle, extensionLimit);
                    swingStressMonitors.Add(new SwingStressMonitor
                    {
                        Bone = rb.Name,
                        Child = rb.BodyHandle,
                        Parent = parentHandle,
                        AxisLocalChild = extensionLimit.AxisLocalA,
                        AxisLocalParent = extensionLimit.AxisLocalB,
                        LimitAngle = extensionLimit.MaximumSwingAngle,
                    });

                    // Flexion wall, measured directly between the upper-arm and forearm axes.
                    var parentSegmentLocal = NormalizeOrFallback(Vector3.Transform(
                        parentSegDir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitY);
                    var flexionLimit = new SwingLimit
                    {
                        AxisLocalA = forearmLocalChild,
                        AxisLocalB = parentSegmentLocal,
                        MaximumSwingAngle = DegreesToRadians(140f),
                        SpringSettings = passiveLimitSpring,
                    };
                    simulation.Solver.Add(rb.BodyHandle, parentHandle, flexionLimit);

                    // A single, conservative pronation/supination coordinate. The wrist does not
                    // receive another axial degree of freedom, preventing candy-wrapper twisting.
                    var axialBasis = CreateTwistBasis(segDirWorld, hingeAxisWorld);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(
                                Quaternion.Inverse(childBodyRef.Pose.Orientation) * axialBasis),
                            LocalBasisB = Quaternion.Normalize(
                                Quaternion.Inverse(parentBodyRef.Pose.Orientation) * axialBasis),
                            MinimumAngle = DegreesToRadians(-65f),
                            MaximumAngle = DegreesToRadians(65f),
                            SpringSettings = passiveLimitSpring,
                        });
                }
                else
                {
                    // Knee: one true rotational degree of freedom. AngularHinge owns the plane;
                    // TwistLimit owns the signed interval. There is no target angle and therefore
                    // no source that can pump a suspended shin toward straight.
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new AngularHinge
                        {
                            LocalHingeAxisA = hingeAxisLocalChild,
                            LocalHingeAxisB = hingeAxisLocalParent,
                            SpringSettings = alignmentSpring,
                        });

                    // TwistLimit measures parent B relative to child A. Reversing the measurement
                    // axis makes anatomical flexion positive for the axis convention above.
                    var childFlexionBasis = CreateTwistBasis(-hingeAxisWorld, segDirWorld);
                    var parentFlexionBasis = CreateTwistBasis(-hingeAxisWorld, parentSegDir);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(
                                Quaternion.Inverse(childBodyRef.Pose.Orientation) * childFlexionBasis),
                            LocalBasisB = Quaternion.Normalize(
                                Quaternion.Inverse(parentBodyRef.Pose.Orientation) * parentFlexionBasis),
                            MinimumAngle = DegreesToRadians(toeHinge ? -10f : -5f),
                            MaximumAngle = DegreesToRadians(toeHinge ? 45f : 145f),
                            SpringSettings = passiveLimitSpring,
                        });
                }

                // Tier C — asymmetric ROM for this hinge (knee/elbow; a no-op for the toe, whose
                // SwingMinLimit/HasPassiveHingeRest both default to "off"). Resolved once here so
                // it drives the fold-stop cap and the passive rest bias below, and (only with the
                // RagdollAnatomicalRom Advanced Filter on) the directional flexion limit.
                JointRom hingeRom = default;
                bool hasHingeRom = settings.RagdollAnatomicalRom &&
                    (boneDef.AnatomicalRole == AnatomicalRole.Knee || boneDef.AnatomicalRole == AnatomicalRole.Elbow) &&
                    TryGetAnatomicalRom(rb.Name, out hingeRom);

                AddAnatomicalHingeFoldStop(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld, passiveLimitSpring,
                    hasHingeRom ? hingeRom.FlexionMax : (float?)null);
                AddAnatomicalHingeRestBias(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld);

                if (hasHingeRom)
                    AddAnatomicalHingeFlexionLimit(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                        boneDef, hingeAxisWorld, parentSegDir, segDirWorld, hingeRom, passiveLimitSpring);

                if (settings.RagdollVerboseLog)
                    log.Info($"[Ragdoll Joint] '{rb.Name}' passive signed joint axis=({hingeAxisWorld.X:F2},{hingeAxisWorld.Y:F2},{hingeAxisWorld.Z:F2})");
            }
            else
            {

                // BallSocket: positional connection
                // Soft bodies use low frequency + low damping for jiggle
                var ballSocket = new BallSocket
                {
                    LocalOffsetA = childLocalAnchor,
                    LocalOffsetB = parentLocalAnchor,
                    SpringSettings = boneDef.SoftBody
                        ? new SpringSettings(boneDef.SoftSpringFreq, boneDef.SoftSpringDamp)
                        : boneDef.AnatomicalRole == AnatomicalRole.Foot
                            ? footJointSpring
                            : (IsClavicleBone(rb.Name) || IsUpperArmBone(rb.Name))
                                ? shoulderJointSpring
                            : jointSpring,
                };
                // A jiggle joint is soft on purpose — firming it up for a carry would just make the
                // chest rigid.
                simulation.Solver.Add(rb.BodyHandle, parentHandle, ballSocket);

                // Feet went through this two-axis swivel-hinge frame briefly; reverted back to
                // the generic ball cone below (see the else branch) — under a hard landing
                // impulse the extra rigid coupling fought the solver and showed up as ankle
                // stretch/distortion. Wrists keep it; there was no report of a wrist problem.
                var isDistalFrame = IsHandBone(rb.Name);
                if (isDistalFrame)
                {
                    var longAxisWorld = NormalizeOrFallback(segDirWorld, Vector3.UnitY);
                    var transverseWorld = ProjectOntoPlane(
                        Vector3.Transform(Vector3.UnitX, childBodyRef.Pose.Orientation), longAxisWorld);
                    transverseWorld = NormalizeOrFallback(transverseWorld,
                        ComputeFallbackBallTwistReference(longAxisWorld));
                    var secondTransverseWorld = NormalizeOrFallback(
                        Vector3.Cross(longAxisWorld, transverseWorld), Vector3.UnitZ);
                    var swivelWorld = secondTransverseWorld;
                    var hingeWorld = transverseWorld;

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new AngularSwivelHinge
                        {
                            LocalSwivelAxisA = NormalizeOrFallback(Vector3.Transform(
                                swivelWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitZ),
                            LocalHingeAxisB = NormalizeOrFallback(Vector3.Transform(
                                hingeWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitX),
                            SpringSettings = new SpringSettings(
                                RagdollTuning.JointSpringFrequency, 2f),
                        });

                    // The swivel-hinge also accepts an inverted transverse axis. Keep the
                    // hand's transverse normal in its original forearm-relative hemisphere
                    // so axial flipping cannot hide from the longitudinal swing cone.
                    // The 80-degree cap leaves room for the full 55-degree wrist bend.
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = NormalizeOrFallback(Vector3.Transform(
                                secondTransverseWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitZ),
                            AxisLocalB = NormalizeOrFallback(Vector3.Transform(
                                secondTransverseWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitZ),
                            MaximumSwingAngle = DegreesToRadians(80f),
                            SpringSettings = new SpringSettings(
                                RagdollTuning.JointSpringFrequency, 2f),
                        });

                    // Limit the two permitted bend axes as a compact reach cone.
                    var childLongLocal = NormalizeOrFallback(Vector3.Transform(
                        longAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)), Vector3.UnitY);
                    var parentReferenceLocal = NormalizeOrFallback(Vector3.Transform(
                        longAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)), Vector3.UnitY);
                    var distalSwing = new SwingLimit
                    {
                        AxisLocalA = childLongLocal,
                        AxisLocalB = parentReferenceLocal,
                        MaximumSwingAngle = DegreesToRadians(55f),
                        SpringSettings = ballSwingSpring,
                    };
                    var distalSwingHandle = simulation.Solver.Add(rb.BodyHandle, parentHandle, distalSwing);
                    swingLimitByBone[rb.Name] = (distalSwingHandle, distalSwing);
                    swingStressMonitors.Add(new SwingStressMonitor
                    {
                        Bone = rb.Name,
                        Child = rb.BodyHandle,
                        Parent = parentHandle,
                        AxisLocalChild = distalSwing.AxisLocalA,
                        AxisLocalParent = distalSwing.AxisLocalB,
                        LimitAngle = distalSwing.MaximumSwingAngle,
                    });
                }
                else
                {
                    // Generic ball joints retain their configured cone. Shoulder/clavicle edges use
                    // the firm boundary because the whole arm can otherwise hold them far outside it.
                    // Tier C (swing): hips, arm-side shoulders, and the spine chain get DIRECTIONAL
                    // anatomical limits below — a symmetric cone cannot hold flexion 95° /
                    // abduction 45° / adduction 25° at once (hip splay). Only while the
                    // RagdollAnatomicalRom Advanced Filter is on (off by default).
                    JointRom swingRom = default;
                    var hasSwingRom = settings.RagdollAnatomicalRom &&
                        (boneDef.AnatomicalRole == AnatomicalRole.Hip ||
                         boneDef.AnatomicalRole == AnatomicalRole.Spine ||
                         (boneDef.AnatomicalRole == AnatomicalRole.Shoulder &&
                          IsUpperArmBone(rb.Name))) &&
                        TryGetAnatomicalRom(rb.Name, out swingRom);
                    var hipKneecapCaps = hasSwingRom && boneDef.AnatomicalRole == AnatomicalRole.Hip;

                    if (boneDef.SwingLimit > 0)
                    {
                        var axisChildLocal = Vector3.Transform(segDirWorld,
                            Quaternion.Inverse(childBodyRef.Pose.Orientation));
                        var axisParentLocal = Vector3.Transform(segDirWorld,
                            Quaternion.Inverse(parentBodyRef.Pose.Orientation));
                        // Neck/head get the same hard wall as the shoulder girdle: their swing cone
                        // is narrow (~14°) by design, and on the soft spring the head can overshoot
                        // it under a fast torso motion and visibly lag/float before snapping back —
                        // a hard wall stops it right at the limit instead.
                        var coneSpring = IsClavicleBone(rb.Name) || IsUpperArmBone(rb.Name) ||
                                          rb.Name == Bones.Neck || rb.Name == Bones.Head
                            ? limitSpring
                            : ballSwingSpring;

                        var effectiveCone = boneDef.SwingLimit;
                        var coneAxisParentLocal = axisParentLocal;
                        if (hasSwingRom)
                        {
                            effectiveCone = MathF.Max(effectiveCone, MathF.Max(
                                MathF.Max(swingRom.FlexionMax, swingRom.ExtensionMax),
                                MathF.Max(swingRom.AbductionMax, swingRom.AdductionMax)));

                            // The directional caps below own the real anatomical shape; this
                            // bounding cone becomes a hard wall anchored on the anatomical
                            // vertical (not a soft suggestion around the death pose).
                            var anatConeAxis = Vector3.Dot(segDirWorld, Vector3.UnitY) >= 0f
                                ? Vector3.UnitY
                                : -Vector3.UnitY;
                            coneAxisParentLocal = Vector3.Normalize(Vector3.Transform(
                                anatConeAxis, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));
                            coneSpring = limitSpring;
                        }

                        var swingLimit = new SwingLimit
                        {
                            AxisLocalA = axisChildLocal,
                            AxisLocalB = coneAxisParentLocal,
                            MaximumSwingAngle = effectiveCone,
                            SpringSettings = coneSpring,
                        };
                        var swingHandle = simulation.Solver.Add(rb.BodyHandle, parentHandle, swingLimit);
                        swingLimitByBone[rb.Name] = (swingHandle, swingLimit);
                        swingStressMonitors.Add(new SwingStressMonitor
                        {
                            Bone = rb.Name,
                            Child = rb.BodyHandle,
                            Parent = parentHandle,
                            AxisLocalChild = axisChildLocal,
                            AxisLocalParent = coneAxisParentLocal,
                            LimitAngle = effectiveCone,
                        });

                        // Anatomical boundary caps are WALLS (hard limitSpring): riding them on the
                        // soft-edge spring let body weight shove the legs 40° past every limit.
                        if (hasSwingRom)
                            AddDirectionalSwingLimits(rb.Name, rb.BodyHandle, parentHandle,
                                childBodyRef, parentBodyRef, segDirWorld, anchorWorld,
                                anatForwardWorld, anatLateralWorld, swingRom,
                                ballSwingSpring, limitSpring);

                        // Hip axial rotation: capped via the kneecap-facing axis (below), which
                        // stays valid at any flexion — see AddKneecapFacingLimits.
                        if (hipKneecapCaps)
                            AddKneecapFacingLimits(rb.Name, rb.BodyHandle, parentHandle,
                                childBodyRef, parentBodyRef, segDirWorld, anchorWorld,
                                anatForwardWorld, anatLateralWorld, limitSpring);

                        // Ankle dorsiflexion wall. The symmetric cone above treats "sole tips up
                        // toward the shin" and "toes point down away from the shin" as equally
                        // legal, but real dorsiflexion (~20°) is far more restricted than
                        // plantarflexion (~45°) — this is what let the foot fold up flush against
                        // the shin. Always on (not the RagdollAnatomicalRom Advanced Filter): at
                        // neutral standing the foot axis is ~90° from the shin's own down axis;
                        // dorsiflexing swings the foot axis TOWARD "up" (away from "down", angle
                        // increases past 90°), plantarflexing swings it TOWARD "down" (angle
                        // decreases, unaffected by this wall). One-sided by construction.
                        if (boneDef.AnatomicalRole == AnatomicalRole.Foot)
                        {
                            var dorsiflexionMax = TryGetAnatomicalRom(rb.Name, out var footRom)
                                ? footRom.FlexionMax
                                : DegreesToRadians(20f);
                            var shinDownLocalParent = Vector3.Normalize(Vector3.Transform(
                                parentSegDir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));
                            var dorsiflexionLimit = new SwingLimit
                            {
                                AxisLocalA = axisChildLocal,
                                AxisLocalB = shinDownLocalParent,
                                MaximumSwingAngle = MathF.PI / 2f + dorsiflexionMax,
                                SpringSettings = limitSpring,
                            };
                            simulation.Solver.Add(rb.BodyHandle, parentHandle, dorsiflexionLimit);
                            swingStressMonitors.Add(new SwingStressMonitor
                            {
                                Bone = rb.Name,
                                Child = rb.BodyHandle,
                                Parent = parentHandle,
                                AxisLocalChild = axisChildLocal,
                                AxisLocalParent = shinDownLocalParent,
                                LimitAngle = dorsiflexionLimit.MaximumSwingAngle,
                            });
                        }
                    }

                    // C2: when ROM is on, take the axial range from the anatomical table (correct
                    // clinical asymmetry); otherwise use the hand-set boneDef twist values.
                    var ballTwistMin = boneDef.TwistMinAngle;
                    var ballTwistMax = boneDef.TwistMaxAngle;
                    if (settings.RagdollAnatomicalRom && TryGetAnatomicalRom(rb.Name, out var ballRom))
                    {
                        ballTwistMin = ballRom.AxialMin;
                        ballTwistMax = ballRom.AxialMax;
                    }
                    // Hips with ROM: the kneecap-facing caps above replace this TwistLimit — its
                    // measurement degenerates past ~90° of swing, exactly in the kicked-up poses
                    // where a corpse thigh would otherwise spin freely.
                    if ((ballTwistMin != 0 || ballTwistMax != 0) &&
                        !hipKneecapCaps &&
                        boneDef.SwingLimit <= ballJointMaxSwing)
                    {
                        var refDir = ComputeAnatomicalBallTwistReference(segDirWorld, parentSegDir);
                        var twistBasis = CreateTwistBasis(segDirWorld, refDir);
                        simulation.Solver.Add(rb.BodyHandle, parentHandle,
                            new TwistLimit
                            {
                                LocalBasisA = Quaternion.Normalize(
                                    Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis),
                                LocalBasisB = Quaternion.Normalize(
                                    Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis),
                                MinimumAngle = ballTwistMin,
                                MaximumAngle = ballTwistMax,
                                SpringSettings = limitSpring,
                            });
                    }
                }
            }

            // Angular constraint: soft bodies use AngularServo (spring return to rest pose),
            // rigid bodies use AngularMotor (velocity damping only).
            if (boneDef.SoftBody)
            {
                // Target the ASSEMBLY-TIME relative orientation (same convention as the Ankle
                // Weld above), not Identity: the child capsule is aligned along parent→bone
                // while the parent capsule follows its own segment, so an Identity target
                // would wrench the soft bone toward the parent's orientation the instant the
                // ragdoll fires — a visible twist snap on activation.
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularServo
                    {
                        TargetRelativeRotationLocalA = Quaternion.Normalize(
                            Quaternion.Inverse(childBodyRef.Pose.Orientation) * parentBodyRef.Pose.Orientation),
                        // Finite force cap (was float.MaxValue): the spring settings define the
                        // stiffness; the cap only saturates when the servo FIGHTS another
                        // constraint (SwingLimit at the cone edge), where unlimited torque on a
                        // centimeter-scale body is exactly what flings it.
                        ServoSettings = new ServoSettings(float.MaxValue, 0f, 100f),
                        SpringSettings = new SpringSettings(boneDef.SoftServoFreq, boneDef.SoftServoDamp),
                    });

                // Leash: remember the assembly offset so a diverging soft body can be snapped
                // back to its anchor instead of dragging its skinned flesh off-screen.
                softBodyLeashes.Add(new SoftBodyLeash
                {
                    Child = rb.BodyHandle,
                    Parent = parentHandle,
                    Name = rb.Name,
                    MaxDistance = Vector3.Distance(childBodyRef.Pose.Position, parentBodyRef.Pose.Position) + 0.35f,
                    RelPos = Vector3.Transform(
                        childBodyRef.Pose.Position - parentBodyRef.Pose.Position,
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation)),
                    RelRot = Quaternion.Normalize(
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation) * childBodyRef.Pose.Orientation),
                });
                softLeashLogBudget = 6;
            }
            else
            {
                // Friction, not a weld — see JointFrictionFraction.
                var motor = new AngularMotor
                {
                    TargetVelocityLocalA = Vector3.Zero,
                    Settings = new MotorSettings(ResolveJointFriction(rb), motorDamping),
                };
                angularMotorByBone[rb.Name] = (
                    simulation.Solver.Add(rb.BodyHandle, parentHandle, motor), motor);
            }
        }

        // Nothing to simulate (e.g. a non-humanoid skeleton with no qualifying segments).
        // Bail BEFORE freezing the animation — freezing a rig we can't drive would leave the
        // corpse stuck mid-pose. Caller (OnRenderFrame) Deactivates on the false return.
        if (ragdollBones.Count == 0)
        {
            if (rec != null) RecInitEnd(false, solverIterations, solverSubsteps, genericSkeleton);
            log.Warning("RagdollController: no ragdoll bodies were created; aborting activation.");
            return false;
        }

        // Build ragdoll bone index set for fast lookup during propagation
        ragdollBoneIndices.Clear();
        foreach (var rb in ragdollBones)
            ragdollBoneIndices.Add(rb.BoneIndex);

        // Freeze animation so the death animation stops fighting physics.
        // With OverallSpeed=0, the animation system stops advancing time,
        // so it writes the same frozen frame each render. We then overwrite
        // ragdoll bones with physics, and propagate to non-ragdoll descendants.
        savedOverallSpeed = actors.GetAnimationSpeed(targetCharacterAddress);
        actors.SetAnimationSpeed(targetCharacterAddress, 0f);
        animationFrozen = true;
        log.Info($"RagdollController: Animation frozen (saved speed={savedOverallSpeed:F2})");

        // Resolve ancestor bone — n_hara must follow j_kosi to prevent mesh tearing
        nHaraIndex = ResolveBoneIndex(skel, Bones.BellyFollow);
        kaoBodyBoneIndex = ResolveBoneIndex(skel, Bones.Head);
        CaptureFaceTraversalLandmarks(skel);
        log.Info($"RagdollController: n_hara index={nHaraIndex}, j_kao index={kaoBodyBoneIndex}");

        // Create NPC collision volumes — dynamically discover bones from each NPC's skeleton.
        // Works for any model (humanoid, monster, dragon) — no hardcoded bone names.
        npcCollisionStates.Clear();
        npcTraversalAnchors.Clear();
        if (settings.NpcCollisionActive && colliders.HasSelector)
        {
            var scale = settings.RagdollNpcCollisionScale;
            var capsuleRadius = settings.RagdollNpcCollisionAutoSize ? NpcDefaultBoneRadius : NpcDefaultBoneRadius * scale;

            var selectedNpcs = new List<ColliderCandidate>();
            colliders.GetSelectedNpcs(selectedNpcs);
            log.Info($"RagdollController: NPC bone collision — {selectedNpcs.Count} NPCs, autoSize={settings.RagdollNpcCollisionAutoSize}, scale={scale:F2}");

            // Dedupe across both sources so a character (e.g. a companion that also
            // appears elsewhere) only gets one set of collision statics.
            var addedCollisionAddresses = new HashSet<nint>();

            // Extra collision actors — in party mode the local player and every spawned
            // companion. They get the SAME full per-bone collision as enemies so every
            // ragdoll collides with every living character, not just enemies. Evaluated
            // here (at activation) so a dying player's ragdoll sees the live companions.
            var extraCollisionAddresses = colliders.GetExtraColliders();
            if (rec != null) { RecActorList("extraCollisionProvider", extraCollisionAddresses); RecSelectedNpcs("selectedNpcs", selectedNpcs); }
            foreach (var address in extraCollisionAddresses)
            {
                if (address == nint.Zero || address == targetCharacterAddress)
                    continue;
                if (!addedCollisionAddresses.Add(address))
                    continue;

                BuildCharacterCollision(address, "extra actor", scale, capsuleRadius);
            }

            foreach (var npc in selectedNpcs)
            {
                if (!npc.HasCharacter)
                {
                    log.Warning($"RagdollController: NPC '{npc.Name}' has null BattleChara, skipping");
                    continue;
                }

                var npcAddr = npc.Address;
                if (npcAddr == targetCharacterAddress)
                    continue;
                if (!addedCollisionAddresses.Add(npcAddr))
                    continue;

                BuildCharacterCollision(npcAddr, $"NPC '{npc.Name}'", scale, capsuleRadius);
            }
        }

        var totalNpcStatics = 0;
        var activeNpcStatics = 0;
        foreach (var s in npcCollisionStates)
        {
            totalNpcStatics += (s.IsFallback || s.IsMesh) ? 1 : s.BoneStatics.Count;
            activeNpcStatics += (s.IsFallback || s.IsMesh)
                ? 1
                : s.Lightweight
                    ? Math.Min(NpcLightweightCollisionSegments, s.BoneStatics.Count)
                    : s.BoneStatics.Count;
        }
        var collisionVolumeSummary = activeNpcStatics == totalNpcStatics
            ? $"{totalNpcStatics} collision volumes"
            : $"{activeNpcStatics} active / {totalNpcStatics} allocated collision volumes";
        log.Info($"RagdollController: Physics initialized — {ragdollBones.Count} bodies, {npcCollisionStates.Count} NPCs ({collisionVolumeSummary}), ground={groundY:F3}");

        // Initialize hair physics — the BEPU rig is the only implementation (the legacy
        // pendulum simulator is retired). If the rig cannot build (no head ragdoll body /
        // no simulatable hair partial), hair simply stays rigid this activation.
        if (settings.RagdollHairPhysics && kaoBodyBoneIndex >= 0)
            BuildHairRig(skel);

        if (rec != null) RecInitEnd(ragdollBones.Count > 0, solverIterations, solverSubsteps, genericSkeleton);
        return ragdollBones.Count > 0;
    }

    // === Heavy body: limb inertia ===
    //
    // This is the lever that mass is NOT. Gravity's torque on a limb scales with its mass, and the
    // limb's resistance to being turned scales with its mass too — they cancel exactly, so multiplying
    // the whole rig's mass moves it not one bit differently. That is the entire answer to why turning
    // the mass up never did anything. Inertia is a different quantity: raise it and a limb takes longer
    // to spin up and longer to stop, which is the whole visual signature of weight. An oak door and a
    // cardboard one of the same size swing quite differently.
    //
    // But the correction has to be ANISOTROPIC, and the first attempt at this was not — it scaled the
    // whole tensor by 2.5, and that is where the syrupy, stiff feel came from.
    //
    // Inertia goes as the square of the radius, so the error in these capsules is not spread evenly:
    //   - About the limb's own LONG axis (a shin barrel-rolling about itself) inertia is governed almost
    //     entirely by the radius. Ours is 35mm where a real shin is nearer 60mm — an underestimate of
    //     roughly threefold. A capsule is very nearly a line about this axis, so it barrel-rolls for
    //     almost nothing: exactly the twitchiness that reads as weightless.
    //   - About the TRANSVERSE axes (the shin swinging end-over-end about the knee) inertia is dominated
    //     by the limb's LENGTH, which the model gets right. That number was already close to correct.
    //
    // Slowing the transverse axes by 2.5 therefore did not add weight; it added treacle, and it did it
    // on precisely the swings a body's weight is read from. Fix the axis that is wrong and leave the one
    // that is right.
    private const float LimbInertiaLongAxis = 3f;      // barrel-roll: badly underestimated
    private const float LimbInertiaTransverse = 1.2f;  // end-over-end: already about right

    /// <summary>
    /// Both the capsule and the box shapes here are built with their LOCAL Y along the bone segment (see
    /// CreateCapsuleRotation), so Y is the long axis and X/Z are the transverse ones — and for these
    /// shapes the tensor is diagonal in that frame, which is what makes this clean.
    /// </summary>
    private BodyInertia ApplyLimbInertia(
        BodyInertia inertia,
        in RagdollBoneDef def,
        bool geometryIsMeshDerived)
    {
        if (geometryIsMeshDerived) return inertia;

        // This Advanced Filter only corrects deterministic fallback volumes whose thin radius
        // underestimates long-axis inertia. A mesh-derived shape already computed inertia from the
        // visible geometry; applying this again would double-correct it. Applying
        // the same local-Y tensor change to pelvis, spine, head, palms, fingers, cloth and soft
        // tissue would give unrelated shapes a made-up preferred axis.
        var name = SideStrippedBoneName(def.Name);
        var isLongLimb = BoneClasses.LongLimbRoots.Matches(name);
        if (!isLongLimb)
            return inertia;

        // The solver stores the INVERSE tensor, so scaling inertia up means scaling this down.
        var longAxis = 1f / MathF.Max(0.01f, LimbInertiaLongAxis);
        var transverse = 1f / MathF.Max(0.01f, LimbInertiaTransverse);

        inertia.InverseInertiaTensor.XX *= transverse;
        inertia.InverseInertiaTensor.YY *= longAxis;
        inertia.InverseInertiaTensor.ZZ *= transverse;
        // Off-diagonals are zero for these shapes in their own frame; scale them with the transverse
        // terms anyway so a future non-diagonal shape degrades sensibly rather than silently.
        inertia.InverseInertiaTensor.YX *= transverse;
        inertia.InverseInertiaTensor.ZX *= transverse;
        inertia.InverseInertiaTensor.ZY *= transverse;
        return inertia;
    }
}
