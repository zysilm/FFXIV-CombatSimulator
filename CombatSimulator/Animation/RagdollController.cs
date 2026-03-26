using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

public unsafe class RagdollController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Physics simulation
    private BufferPool? bufferPool;
    private BepuSimulation? simulation;

    // State
    private bool isActive;
    private nint targetCharacterAddress;
    private float elapsed;
    private float activationDelay;
    private bool physicsStarted;

    // Skeleton world transform (captured at activation from Skeleton.Transform)
    // ModelPose is in skeleton-local space; these convert to/from world space.
    private Vector3 skelWorldPos;
    private Quaternion skelWorldRot;
    private Quaternion skelWorldRotInv;

    // Bone-to-body mapping
    private readonly List<RagdollBone> ragdollBones = new();

    // Ground height
    private float groundY;

    // Diagnostic frame counter
    private int frameCount;

    // Animation freeze state
    private float savedOverallSpeed = 1.0f;
    private readonly HashSet<int> ragdollBoneIndices = new();

    public bool IsActive => isActive;

    // Ragdoll bone definition
    private struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit; // radians
    }

    // Runtime bone with physics body
    private struct RagdollBone
    {
        public int BoneIndex;       // index in FFXIV skeleton
        public int ParentBoneIndex; // parent bone index (-1 if root)
        public BodyHandle BodyHandle;
        public string Name;
        // Rotation offset: physicsBodyRot * CapsuleToBoneOffset = boneWorldRot
        // Capsule Y axis is oriented along the bone segment, which differs from the bone's rotation.
        public Quaternion CapsuleToBoneOffset;
    }

    // Bone definitions for the ragdoll skeleton
    private static readonly RagdollBoneDef[] BoneDefs = new[]
    {
        new RagdollBoneDef { Name = "j_kosi",    ParentName = null,       CapsuleRadius = 0.08f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.3f },
        new RagdollBoneDef { Name = "j_sebo_a",  ParentName = "j_kosi",   CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.4f },
        new RagdollBoneDef { Name = "j_sebo_b",  ParentName = "j_sebo_a", CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.3f },
        new RagdollBoneDef { Name = "j_sebo_c",  ParentName = "j_sebo_b", CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 4.0f,  SwingLimit = 0.3f },
        new RagdollBoneDef { Name = "j_kubi",    ParentName = "j_sebo_c", CapsuleRadius = 0.04f, CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.5f },
        new RagdollBoneDef { Name = "j_kao",     ParentName = "j_kubi",   CapsuleRadius = 0.08f, CapsuleHalfLength = 0.04f, Mass = 3.0f,  SwingLimit = 0.4f },
        new RagdollBoneDef { Name = "j_ude_a_l", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.2f },
        new RagdollBoneDef { Name = "j_ude_a_r", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.2f },
        new RagdollBoneDef { Name = "j_ude_b_l", ParentName = "j_ude_a_l",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = 2.4f },
        new RagdollBoneDef { Name = "j_ude_b_r", ParentName = "j_ude_a_r",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = 2.4f },
        new RagdollBoneDef { Name = "j_asi_a_l", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.0f },
        new RagdollBoneDef { Name = "j_asi_a_r", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.0f },
        new RagdollBoneDef { Name = "j_asi_b_l", ParentName = "j_asi_a_l",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = 2.4f },
        new RagdollBoneDef { Name = "j_asi_b_r", ParentName = "j_asi_a_r",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = 2.4f },
        new RagdollBoneDef { Name = "j_asi_c_l", ParentName = "j_asi_b_l",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.6f },
        new RagdollBoneDef { Name = "j_asi_c_r", ParentName = "j_asi_b_r",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.6f },
    };

    public RagdollController(BoneTransformService boneService, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
    }

    public void Activate(nint characterAddress)
    {
        if (isActive) Deactivate();

        targetCharacterAddress = characterAddress;
        activationDelay = config.RagdollActivationDelay;
        elapsed = 0f;
        physicsStarted = false;
        isActive = true;

        log.Info($"RagdollController: Activated (delay={activationDelay:F1}s)");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;

        // Restore animation speed before clearing the address
        if (targetCharacterAddress != nint.Zero && physicsStarted)
        {
            try
            {
                var character = (Character*)targetCharacterAddress;
                character->Timeline.OverallSpeed = savedOverallSpeed;
                log.Info($"RagdollController: Animation restored (speed={savedOverallSpeed:F2})");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "RagdollController: Failed to restore animation speed");
            }
        }

        targetCharacterAddress = nint.Zero;
        physicsStarted = false;
        ragdollBoneIndices.Clear();

        DestroySimulation();
        log.Info("RagdollController: Deactivated");
    }

    private void OnRenderFrame()
    {
        if (!isActive) return;

        try
        {
            elapsed += 1.0f / 60.0f;

            // Wait for activation delay (death animation plays first)
            if (!physicsStarted)
            {
                if (elapsed < activationDelay) return;
                if (!InitializePhysics()) { Deactivate(); return; }
                physicsStarted = true;
                frameCount = 0;
            }

            StepAndApply();
        }
        catch (Exception ex)
        {
            log.Error(ex, "RagdollController: Error in render frame");
            Deactivate();
        }
    }

    // --- Coordinate conversion using Skeleton.Transform ---
    // ModelPose is in skeleton-local space (NOT character-position-offset space).
    // The proper conversion uses the skeleton's full transform (position + rotation).
    //   WorldPos  = skelPos + Rotate(modelPos, skelRot)
    //   ModelPos  = Rotate(worldPos - skelPos, skelRotInv)
    //   WorldRot  = skelRot * modelRot
    //   ModelRot  = skelRotInv * worldRot

    private Vector3 ModelToWorld(Vector3 modelPos)
        => skelWorldPos + Vector3.Transform(modelPos, skelWorldRot);

    private Vector3 WorldToModel(Vector3 worldPos)
        => Vector3.Transform(worldPos - skelWorldPos, skelWorldRotInv);

    private Quaternion ModelRotToWorld(Quaternion modelRot)
        => Quaternion.Normalize(skelWorldRot * modelRot);

    private Quaternion WorldRotToModel(Quaternion worldRot)
        => Quaternion.Normalize(skelWorldRotInv * worldRot);

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

    private bool InitializePhysics()
    {
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return false;
        var skel = skelNullable.Value;

        // Get skeleton transform for proper world↔model conversion
        // This is the authoritative transform — NOT GameObject.Rotation (which is just a float yaw).
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return false;

        skelWorldPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        skelWorldRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);
        skelWorldRotInv = Quaternion.Inverse(skelWorldRot);

        log.Info($"RagdollController: Skeleton transform pos=({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3}) " +
                 $"rot=({skelWorldRot.X:F3},{skelWorldRot.Y:F3},{skelWorldRot.Z:F3},{skelWorldRot.W:F3})");

        // Resolve bone indices
        var nameToIndex = new Dictionary<string, int>();
        ragdollBones.Clear();

        foreach (var def in BoneDefs)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0)
            {
                log.Warning($"RagdollController: Bone '{def.Name}' not found, skipping");
                continue;
            }
            nameToIndex[def.Name] = idx;
        }

        // Raycast for ground height
        groundY = skelWorldPos.Y;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(skelWorldPos.X, skelWorldPos.Y + 2.0f, skelWorldPos.Z),
                new Vector3(0, -1, 0),
                out var hitInfo,
                50f))
        {
            groundY = hitInfo.Point.Y;
        }
        log.Info($"RagdollController: Ground Y={groundY:F3}");

        // Create BEPU simulation
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks(),
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(8, 1));

        // Add ground plane as static body
        var groundShapeIndex = simulation.Shapes.Add(new Box(1000, 0.1f, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY, 0),
            Quaternion.Identity,
            groundShapeIndex));

        // --- Pass 1: Collect bone world positions and rotations ---
        var pose = skel.Pose;
        var boneWorldPositions = new Dictionary<string, Vector3>();
        var boneWorldRotations = new Dictionary<string, Quaternion>();

        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var idx)) continue;
            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            boneWorldPositions[def.Name] = ModelToWorld(modelPos);
            boneWorldRotations[def.Name] = ModelRotToWorld(modelRot);
        }

        // Build child lookup: for each bone, find its first child in BoneDefs
        var boneToFirstChild = new Dictionary<string, string>();
        foreach (var def in BoneDefs)
        {
            if (def.ParentName != null && !boneToFirstChild.ContainsKey(def.ParentName))
                boneToFirstChild[def.ParentName] = def.Name;
        }

        // --- Pass 2: Create physics bodies ---
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            // Determine capsule orientation from bone→child segment direction.
            // BEPU capsules have length along local Y, so we rotate Y to align with the segment.
            Quaternion capsuleWorldRot;
            if (boneToFirstChild.TryGetValue(def.Name, out var childName) &&
                boneWorldPositions.TryGetValue(childName, out var childWorldPos))
            {
                var segment = childWorldPos - boneWorldPos;
                capsuleWorldRot = segment.Length() > 0.001f
                    ? RotationFromYToDirection(segment)
                    : boneWorldRot;
            }
            else
            {
                // Leaf bone: no child to orient toward, use bone's own rotation
                capsuleWorldRot = boneWorldRot;
            }

            // Rotation offset: capsuleWorldRot * offset = boneWorldRot
            // => offset = Inverse(capsuleWorldRot) * boneWorldRot
            var capsuleToBoneOffset = Quaternion.Normalize(
                Quaternion.Inverse(capsuleWorldRot) * boneWorldRot);

            // Create capsule body at the bone's world position
            var capsule = new Capsule(def.CapsuleRadius, def.CapsuleHalfLength * 2);
            var shapeIndex = simulation.Shapes.Add(capsule);

            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(boneWorldPos, capsuleWorldRot),
                capsule.ComputeInertia(def.Mass),
                new CollidableDescription(shapeIndex),
                new BodyActivityDescription(0.01f));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

            int parentBoneIdx = -1;
            if (def.ParentName != null && nameToIndex.TryGetValue(def.ParentName, out var pIdx))
                parentBoneIdx = pIdx;

            ragdollBones.Add(new RagdollBone
            {
                BoneIndex = boneIdx,
                ParentBoneIndex = parentBoneIdx,
                BodyHandle = bodyHandle,
                Name = def.Name,
                CapsuleToBoneOffset = capsuleToBoneOffset,
            });

            // Log initial state for diagnostics
            var capsuleY = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
            log.Info($"[Ragdoll Init] '{def.Name}' idx={boneIdx} " +
                     $"worldPos=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                     $"boneRot=({boneWorldRot.X:F3},{boneWorldRot.Y:F3},{boneWorldRot.Z:F3},{boneWorldRot.W:F3}) " +
                     $"capsuleY=({capsuleY.X:F3},{capsuleY.Y:F3},{capsuleY.Z:F3}) " +
                     $"offset=({capsuleToBoneOffset.X:F3},{capsuleToBoneOffset.Y:F3},{capsuleToBoneOffset.Z:F3},{capsuleToBoneOffset.W:F3})");
        }

        // --- Pass 3: Add constraints between connected bones ---
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            // Joint anchor is at the child bone's world position
            var anchorWorld = boneWorldPositions[rb.Name];

            var childBodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var parentBodyRef = simulation.Bodies.GetBodyReference(parentHandle);

            // BallSocket local offsets: transform world anchor into each body's local space
            // Pattern from BEPU RagdollDemo: Transform(worldOffset, Conjugate(bodyOrientation))
            var childLocalAnchor = Vector3.Transform(
                anchorWorld - childBodyRef.Pose.Position,
                Quaternion.Inverse(childBodyRef.Pose.Orientation));
            var parentLocalAnchor = Vector3.Transform(
                anchorWorld - parentBodyRef.Pose.Position,
                Quaternion.Inverse(parentBodyRef.Pose.Orientation));

            // BallSocket: positional connection at joint
            simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new BallSocket
                {
                    LocalOffsetA = childLocalAnchor,
                    LocalOffsetB = parentLocalAnchor,
                    SpringSettings = new SpringSettings(30, 5),
                });

            // SwingLimit: restrict angle between connected bodies
            float swingLimit = 0.5f;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { swingLimit = def.SwingLimit; break; }

            // CRITICAL: Both axes must represent the SAME world direction in their respective
            // local frames so the constraint starts satisfied (angle=0). Use the segment direction
            // from parent body to joint (child position) as the shared reference direction.
            // Previously AxisLocalA was always (0,1,0) (capsule Y), which only works for non-leaf
            // bones where capsule Y aligns with the segment. For leaf bones, capsule Y can point
            // in any direction, causing massive initial violations (e.g., head: 135° vs 23° limit).
            var segDirWorld = anchorWorld - parentBodyRef.Pose.Position;
            if (segDirWorld.Length() > 0.001f)
                segDirWorld = Vector3.Normalize(segDirWorld);
            else
                segDirWorld = Vector3.UnitY;

            var axisChildLocal = Vector3.Transform(segDirWorld,
                Quaternion.Inverse(childBodyRef.Pose.Orientation));
            var axisParentLocal = Vector3.Transform(segDirWorld,
                Quaternion.Inverse(parentBodyRef.Pose.Orientation));

            simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new SwingLimit
                {
                    AxisLocalA = axisChildLocal,
                    AxisLocalB = axisParentLocal,
                    MaximumSwingAngle = swingLimit,
                    SpringSettings = new SpringSettings(15, 3),
                });

            // AngularMotor: damp relative angular velocity between connected bodies
            // Prevents wild oscillation. Pattern from BEPU RagdollDemo.
            simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new AngularMotor
                {
                    TargetVelocityLocalA = Vector3.Zero,
                    Settings = new MotorSettings(float.MaxValue, 0.01f),
                });
        }

        // Build ragdoll bone index set for fast lookup during propagation
        ragdollBoneIndices.Clear();
        foreach (var rb in ragdollBones)
            ragdollBoneIndices.Add(rb.BoneIndex);

        // Freeze animation so the death animation stops fighting physics.
        // With OverallSpeed=0, the animation system stops advancing time,
        // so it writes the same frozen frame each render. We then overwrite
        // ragdoll bones with physics, and propagate to non-ragdoll descendants.
        var character = (Character*)targetCharacterAddress;
        savedOverallSpeed = character->Timeline.OverallSpeed;
        character->Timeline.OverallSpeed = 0f;
        log.Info($"RagdollController: Animation frozen (saved speed={savedOverallSpeed:F2})");

        log.Info($"RagdollController: Physics initialized — {ragdollBones.Count} bodies, ground={groundY:F3}");
        return ragdollBones.Count > 0;
    }

    private void StepAndApply()
    {
        if (simulation == null) return;

        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;

        // Keep animation frozen (game may recalculate OverallSpeed each frame)
        var character = (Character*)targetCharacterAddress;
        character->Timeline.OverallSpeed = 0f;

        // Step physics
        simulation.Timestep(1.0f / 60.0f);

        var pose = skel.Pose;

        // Save original positions/rotations for delta tracking (needed for j_kao propagation)
        var result = new BoneModificationResult(skel.BoneCount);
        for (int i = 0; i < skel.BoneCount; i++)
        {
            ref var m = ref pose->ModelPose.Data[i];
            result.OriginalPositions[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            result.OriginalRotations[i] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        frameCount++;
        var logThisFrame = frameCount <= 3 || frameCount % 60 == 0;

        // Write physics body transforms back to ModelPose
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;

            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);

            // Guard against NaN from physics explosion — deactivate instead of crashing
            if (float.IsNaN(bodyRef.Pose.Position.X) || float.IsNaN(bodyRef.Pose.Position.Y) ||
                float.IsNaN(bodyRef.Pose.Position.Z) || float.IsNaN(bodyRef.Pose.Orientation.W))
            {
                log.Warning($"RagdollController: NaN detected in body '{rb.Name}', deactivating");
                Deactivate();
                return;
            }

            // Reconstruct bone world rotation from physics capsule rotation + stored offset
            var boneWorldRot = Quaternion.Normalize(bodyRef.Pose.Orientation * rb.CapsuleToBoneOffset);

            // Convert physics world-space results back to skeleton model-space
            var modelPos = WorldToModel(bodyRef.Pose.Position);
            var modelRot = WorldRotToModel(boneWorldRot);

            if (logThisFrame)
            {
                ref var origM = ref pose->ModelPose.Data[rb.BoneIndex];
                var animPos = new Vector3(origM.Translation.X, origM.Translation.Y, origM.Translation.Z);
                var animRot = new Quaternion(origM.Rotation.X, origM.Rotation.Y, origM.Rotation.Z, origM.Rotation.W);
                log.Info($"[Ragdoll F{frameCount}] '{rb.Name}' " +
                         $"animPos=({animPos.X:F3},{animPos.Y:F3},{animPos.Z:F3}) " +
                         $"physWorldPos=({bodyRef.Pose.Position.X:F3},{bodyRef.Pose.Position.Y:F3},{bodyRef.Pose.Position.Z:F3}) " +
                         $"→modelPos=({modelPos.X:F3},{modelPos.Y:F3},{modelPos.Z:F3}) " +
                         $"animRot=({animRot.X:F3},{animRot.Y:F3},{animRot.Z:F3},{animRot.W:F3}) " +
                         $"→modelRot=({modelRot.X:F3},{modelRot.Y:F3},{modelRot.Z:F3},{modelRot.W:F3})");
            }

            boneService.WriteBoneTransform(skel, rb.BoneIndex, modelPos, modelRot, result);
        }

        // Propagate ragdoll movement to non-ragdoll descendant bones (hands, fingers, toes).
        // Without this, these bones stay at their frozen animation positions while their
        // ragdoll parent bones move via physics, causing mesh tearing at boundaries.
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            if (ragdollBoneIndices.Contains(i)) continue; // already handled by physics

            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || !result.HasAccumulated[parentIdx]) continue;

            var parentDelta = result.AccumulatedDeltas[parentIdx];
            var parentOrigPos = result.OriginalPositions[parentIdx];
            ref var parentModel = ref pose->ModelPose.Data[parentIdx];
            var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);

            // Rotate this bone around its parent's original position, then translate by parent displacement
            var relPos = result.OriginalPositions[i] - parentOrigPos;
            relPos = Vector3.Transform(relPos, parentDelta);
            var newPos = parentOrigPos + relPos + (parentNewPos - parentOrigPos);
            var newRot = Quaternion.Normalize(parentDelta * result.OriginalRotations[i]);

            boneService.WriteBoneTransform(skel, i, newPos, newRot, result);
        }

        // Propagate j_kao changes to face/hair partial skeletons
        var kaoIdx = -1;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            if (ragdollBones[i].Name == "j_kao") { kaoIdx = ragdollBones[i].BoneIndex; break; }
        }
        if (kaoIdx >= 0 && result.HasAccumulated[kaoIdx])
        {
            boneService.PropagateToPartialSkeletons(skel, kaoIdx, "j_kao", result);
        }
    }

    private void DestroySimulation()
    {
        ragdollBones.Clear();
        simulation?.Dispose();
        simulation = null;
        bufferPool?.Clear();
        bufferPool = null;
    }

    public void Dispose()
    {
        Deactivate();
        boneService.OnRenderFrame -= OnRenderFrame;
    }
}

// --- BEPU Callbacks ---

struct RagdollNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Only allow body-static collisions (ragdoll vs ground).
        // Disable all body-body collisions to prevent self-collision explosions.
        return a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static;
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = 0.8f;
        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

struct RagdollPoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private Vector3 gravity;
    private float linearDamping;
    private Vector3Wide gravityDt;
    private Vector<float> dampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public RagdollPoseIntegratorCallbacks(Vector3 gravity, float linearDamping)
    {
        this.gravity = gravity;
        this.linearDamping = linearDamping;
        this.gravityDt = default;
        this.dampingDt = default;
    }

    public void Initialize(BepuSimulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        gravityDt.X = new Vector<float>(gravity.X * dt);
        gravityDt.Y = new Vector<float>(gravity.Y * dt);
        gravityDt.Z = new Vector<float>(gravity.Z * dt);
        dampingDt = new Vector<float>(MathF.Pow(linearDamping, dt * 60f));
    }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear.X = (velocity.Linear.X + gravityDt.X) * dampingDt;
        velocity.Linear.Y = (velocity.Linear.Y + gravityDt.Y) * dampingDt;
        velocity.Linear.Z = (velocity.Linear.Z + gravityDt.Z) * dampingDt;
        velocity.Angular.X *= dampingDt;
        velocity.Angular.Y *= dampingDt;
        velocity.Angular.Z *= dampingDt;
    }
}
