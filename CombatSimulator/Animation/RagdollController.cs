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

    // Bone-to-body mapping
    private readonly List<RagdollBone> ragdollBones = new();

    // Ground height
    private float groundY;

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
        targetCharacterAddress = nint.Zero;
        physicsStarted = false;

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
            }

            StepAndApply();
        }
        catch (Exception ex)
        {
            log.Error(ex, "RagdollController: Error in render frame");
            Deactivate();
        }
    }

    private bool InitializePhysics()
    {
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return false;
        var skel = skelNullable.Value;

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

        // Get character world position for ground height estimation
        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
        var charWorldPos = gameObj->Position;

        // Raycast for ground height
        groundY = charWorldPos.Y;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(charWorldPos.X, charWorldPos.Y + 2.0f, charWorldPos.Z),
                new Vector3(0, -1, 0),
                out var hitInfo,
                50f))
        {
            groundY = hitInfo.Point.Y;
            log.Info($"RagdollController: Ground height from raycast: {groundY:F3}");
        }
        else
        {
            log.Info($"RagdollController: Using character Y as ground: {groundY:F3}");
        }

        // Create BEPU simulation
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks(),
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(4, 1));

        // Add ground plane as static body
        var groundShapeIndex = simulation.Shapes.Add(new Box(1000, 0.1f, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY, 0),
            Quaternion.Identity,
            groundShapeIndex));

        // Read current bone positions from ModelPose (death pose) and create physics bodies
        var pose = skel.Pose;
        var charPos = new Vector3(charWorldPos.X, charWorldPos.Y, charWorldPos.Z);

        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;

            // Get bone position in model space, convert to world space
            ref var modelTransform = ref pose->ModelPose.Data[boneIdx];
            var boneModelPos = new Vector3(modelTransform.Translation.X, modelTransform.Translation.Y, modelTransform.Translation.Z);
            var boneWorldPos = boneModelPos + charPos;

            var boneModelRot = new Quaternion(modelTransform.Rotation.X, modelTransform.Rotation.Y, modelTransform.Rotation.Z, modelTransform.Rotation.W);

            // Create capsule body
            var capsule = new Capsule(def.CapsuleRadius, def.CapsuleHalfLength * 2);
            var shapeIndex = simulation.Shapes.Add(capsule);

            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(boneWorldPos, boneModelRot),
                capsule.ComputeInertia(def.Mass),
                new CollidableDescription(shapeIndex),
                new BodyActivityDescription(0.01f));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

            // Find parent bone index
            int parentBoneIdx = -1;
            if (def.ParentName != null && nameToIndex.TryGetValue(def.ParentName, out var pIdx))
                parentBoneIdx = pIdx;

            ragdollBones.Add(new RagdollBone
            {
                BoneIndex = boneIdx,
                ParentBoneIndex = parentBoneIdx,
                BodyHandle = bodyHandle,
                Name = def.Name,
            });
        }

        // Add joint constraints between connected bones
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            // Get bone positions for computing joint anchor
            ref var childModel = ref pose->ModelPose.Data[rb.BoneIndex];
            ref var parentModel = ref pose->ModelPose.Data[rb.ParentBoneIndex];

            var childPos = new Vector3(childModel.Translation.X, childModel.Translation.Y, childModel.Translation.Z) + charPos;
            var parentPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z) + charPos;

            // Joint anchor is at the child bone position (where the joint connects)
            var childBodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var parentBodyRef = simulation.Bodies.GetBodyReference(parentHandle);

            // Compute local offsets for ball-socket constraint
            var anchorWorld = childPos;
            var childLocalOffset = anchorWorld - childBodyRef.Pose.Position;
            var parentLocalOffset = anchorWorld - parentBodyRef.Pose.Position;

            // Transform to body-local space
            var childLocalAnchor = Vector3.Transform(childLocalOffset, Quaternion.Inverse(childBodyRef.Pose.Orientation));
            var parentLocalAnchor = Vector3.Transform(parentLocalOffset, Quaternion.Inverse(parentBodyRef.Pose.Orientation));

            // Ball-socket constraint (keeps bones connected)
            simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new BallSocket
                {
                    LocalOffsetA = childLocalAnchor,
                    LocalOffsetB = parentLocalAnchor,
                    SpringSettings = new SpringSettings(30, 5),
                });

            // Find the swing limit for this bone
            float swingLimit = 0.5f;
            foreach (var def in BoneDefs)
            {
                if (def.Name == rb.Name) { swingLimit = def.SwingLimit; break; }
            }

            // Swing limit (prevents unrealistic bending)
            simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new SwingLimit
                {
                    AxisLocalA = new Vector3(0, 1, 0),
                    AxisLocalB = new Vector3(0, 1, 0),
                    MaximumSwingAngle = swingLimit,
                    SpringSettings = new SpringSettings(15, 3),
                });
        }

        log.Info($"RagdollController: Physics initialized with {ragdollBones.Count} bodies, ground={groundY:F3}");
        return ragdollBones.Count > 0;
    }

    private void StepAndApply()
    {
        if (simulation == null) return;

        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;

        // Step physics
        simulation.Timestep(1.0f / 60.0f);

        // Get character world position
        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
        var charWorldPos = gameObj->Position;
        var charPos = new Vector3(charWorldPos.X, charWorldPos.Y, charWorldPos.Z);

        // Save original rotations to compute deltas
        var pose = skel.Pose;

        // Build rotation deltas from physics results
        var deltas = new Dictionary<int, Quaternion>();

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;

            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var physicsRot = bodyRef.Pose.Orientation;

            // Current bone rotation in model space
            ref var model = ref pose->ModelPose.Data[rb.BoneIndex];
            var currentRot = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);

            // Physics rotation is in world space; bone rotation is in model space
            // For now, directly set the rotation from physics (they're equivalent for non-rotated characters)
            var targetRot = new Quaternion(physicsRot.X, physicsRot.Y, physicsRot.Z, physicsRot.W);

            // Compute delta: targetRot = delta * currentRot  =>  delta = targetRot * inverse(currentRot)
            var delta = Quaternion.Normalize(targetRot * Quaternion.Inverse(currentRot));

            // Only apply if delta is meaningful
            if (MathF.Abs(delta.W) < 0.9999f)
                deltas[rb.BoneIndex] = Quaternion.Normalize(currentRot * delta * Quaternion.Inverse(currentRot));
        }

        if (deltas.Count == 0) return;

        // Apply via BoneTransformService
        var result = boneService.ApplyRotationDeltas(skel, deltas);

        // Also update positions from physics (ragdoll moves bones)
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;

            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var physicsPos = bodyRef.Pose.Position;
            var physicsRot = bodyRef.Pose.Orientation;

            // Convert world position back to model space
            var modelPos = new Vector3(physicsPos.X, physicsPos.Y, physicsPos.Z) - charPos;
            var modelRot = new Quaternion(physicsRot.X, physicsRot.Y, physicsRot.Z, physicsRot.W);

            boneService.WriteBoneTransform(skel, rb.BoneIndex, modelPos, modelRot, result);
        }

        // Propagate j_kao to face partial skeletons
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

// BEPU callbacks — prefixed to avoid ambiguity with other project types

struct RagdollNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        return a.Mobility != CollidableMobility.Static || b.Mobility != CollidableMobility.Static;
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        return true;
    }

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = 0.8f;
        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold)
    {
        return true;
    }

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
