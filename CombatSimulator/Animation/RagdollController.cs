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

    // Ground height (physics ground may be lowered by floor offset)
    private float groundY;
    // Real terrain ground (before floor offset), used for visual correction
    private float realGroundY;

    // Diagnostic frame counter
    private int frameCount;

    // Animation freeze state
    private float savedOverallSpeed = 1.0f;
    private readonly HashSet<int> ragdollBoneIndices = new();

    // Ancestor bone index — n_hara must follow j_kosi to prevent mesh tearing
    private int nHaraIndex = -1;


    public bool IsActive => isActive;

    // Joint type determines which BEPU constraints are used:
    // Ball = BallSocket + SwingLimit + TwistLimit + AngularMotor (full 3-DOF rotation)
    // Hinge = Hinge + TwistLimit (as angular range) + AngularMotor (1-DOF rotation)
    private enum JointType { Ball, Hinge }

    // Ragdoll bone definition
    private struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit;      // Ball: cone angle (radians). Hinge: unused.
        public JointType Joint;       // constraint type
        public float TwistMinAngle;   // Min rotation angle (radians). Hinge: angular range min (negative = hyperextension).
        public float TwistMaxAngle;   // Max rotation angle (radians). Hinge: angular range max (positive = flexion).
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
        // Half the distance from this bone to its child bone. Used to convert
        // the capsule center (at segment midpoint) back to the bone origin position.
        // 0 for leaf bones and degenerate (zero-length) segments.
        public float SegmentHalfLength;
    }

    // Bone definitions for the ragdoll skeleton
    // Ball joints: SwingLimit = cone angle, TwistMin/Max = axial rotation range
    // Hinge joints: SwingLimit unused, TwistMin/Max = angular range (min = hyperextension, max = flexion)
    private static readonly RagdollBoneDef[] BoneDefs = new[]
    {
        new RagdollBoneDef { Name = "j_kosi",    ParentName = null,       CapsuleRadius = 0.08f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,  Joint = JointType.Ball,  TwistMinAngle = 0f,     TwistMaxAngle = 0f    }, // pelvis (root)
        new RagdollBoneDef { Name = "j_sebo_a",  ParentName = "j_kosi",   CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.2f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // spine
        new RagdollBoneDef { Name = "j_sebo_b",  ParentName = "j_sebo_a", CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f, Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // spine
        new RagdollBoneDef { Name = "j_sebo_c",  ParentName = "j_sebo_b", CapsuleRadius = 0.06f, CapsuleHalfLength = 0.05f, Mass = 4.0f,  SwingLimit = 0.15f, Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // chest
        new RagdollBoneDef { Name = "j_kubi",    ParentName = "j_sebo_c", CapsuleRadius = 0.04f, CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.25f, Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // neck
        new RagdollBoneDef { Name = "j_kao",     ParentName = "j_kubi",   CapsuleRadius = 0.08f, CapsuleHalfLength = 0.04f, Mass = 3.0f,  SwingLimit = 0.3f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // head
        new RagdollBoneDef { Name = "j_ude_a_l", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f  }, // shoulder
        new RagdollBoneDef { Name = "j_ude_a_r", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f  }, // shoulder
        new RagdollBoneDef { Name = "j_ude_b_l", ParentName = "j_ude_a_l",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = 0f,    Joint = JointType.Hinge, TwistMinAngle = -0.17f, TwistMaxAngle = 2.6f  }, // elbow: -10° to 150°
        new RagdollBoneDef { Name = "j_ude_b_r", ParentName = "j_ude_a_r",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = 0f,    Joint = JointType.Hinge, TwistMinAngle = -0.17f, TwistMaxAngle = 2.6f  }, // elbow: -10° to 150°
        new RagdollBoneDef { Name = "j_asi_a_l", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 0.7f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // hip
        new RagdollBoneDef { Name = "j_asi_a_r", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 0.7f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // hip
        new RagdollBoneDef { Name = "j_asi_b_l", ParentName = "j_asi_a_l",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = 0f,    Joint = JointType.Hinge, TwistMinAngle = -0.17f, TwistMaxAngle = 2.4f  }, // knee: -10° to 140°
        new RagdollBoneDef { Name = "j_asi_b_r", ParentName = "j_asi_a_r",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = 0f,    Joint = JointType.Hinge, TwistMinAngle = -0.17f, TwistMaxAngle = 2.4f  }, // knee: -10° to 140°
        new RagdollBoneDef { Name = "j_asi_c_l", ParentName = "j_asi_b_l",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // foot
        new RagdollBoneDef { Name = "j_asi_c_r", ParentName = "j_asi_b_r",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // foot
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
        nHaraIndex = -1;

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

        // Convert rotation matrix (columns: x, y, z) to quaternion
        var m = new Matrix4x4(
            x.X, y.X, z.X, 0,
            x.Y, y.Y, z.Y, 0,
            x.Z, y.Z, z.Z, 0,
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
        return Vector3.Normalize(hingeAxis);
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
        log.Info($"RagdollController: Raycast ground Y={groundY:F3}");

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

        // Build first-child lookup from full skeleton hierarchy (not just BoneDefs).
        // Used for leaf bones to find the actual child bone direction (e.g., forearm
        // needs elbow→wrist direction from j_te_l, not shoulder→elbow from parent).
        var skelFirstChild = new Dictionary<int, int>();
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx >= 0 && !skelFirstChild.ContainsKey(parentIdx))
                skelFirstChild[parentIdx] = i;
        }

        // Store real terrain level before any offset
        realGroundY = groundY;

        // Lower physics ground to avoid joints starting at floor level, which causes
        // degenerate constraint solving (many simultaneous ground penetrations).
        // The visual correction in StepAndApply compensates so the character doesn't sink.
        if (config.RagdollFloorOffset > 0)
        {
            groundY -= config.RagdollFloorOffset;
            log.Info($"RagdollController: Floor offset applied, physics ground Y={groundY:F3} (real={realGroundY:F3}, offset={config.RagdollFloorOffset:F2})");
        }

        // --- Pass 2: Create physics bodies ---
        // Capsule center is offset from bone origin (joint) along the segment direction
        // by BoneDefs.CapsuleHalfLength. This gives proper lever arms at joints (per BEPU
        // RagdollDemo pattern). Capsule LENGTH comes from BoneDefs (fixed anatomical size),
        // NOT from the animation pose distance — death poses bend joints, making bone
        // distances unreliable (e.g., shin=0.06m in death pose vs 0.22m anatomical).
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            Vector3 capsuleCenter;
            float segmentHalfLength;
            float capsuleLength = def.CapsuleHalfLength * 2;
            Quaternion capsuleWorldRot;

            if (boneToFirstChild.TryGetValue(def.Name, out var childName) &&
                boneWorldPositions.TryGetValue(childName, out var childWorldPos))
            {
                var segment = childWorldPos - boneWorldPos;
                var segLen = segment.Length();

                if (segLen > 0.01f)
                {
                    // Offset capsule center from bone origin along segment direction.
                    // Direction from pose, distance from BoneDefs (pose-independent).
                    var segDir = segment / segLen;
                    capsuleCenter = boneWorldPos + def.CapsuleHalfLength * segDir;
                    segmentHalfLength = def.CapsuleHalfLength;
                    capsuleWorldRot = RotationFromYToDirection(segment);
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
                ref var childMt = ref pose->ModelPose.Data[skelChildIdx];
                var childModelPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                var skelChildWorldPos = ModelToWorld(childModelPos);
                var toSkelChild = skelChildWorldPos - boneWorldPos;
                var toSkelChildLen = toSkelChild.Length();

                if (toSkelChildLen > 0.01f)
                {
                    var dir = toSkelChild / toSkelChildLen;
                    capsuleCenter = boneWorldPos + def.CapsuleHalfLength * dir;
                    segmentHalfLength = def.CapsuleHalfLength;
                    capsuleWorldRot = RotationFromYToDirection(toSkelChild);
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

            // Rotation offset: capsuleWorldRot * offset = boneWorldRot
            var capsuleToBoneOffset = Quaternion.Normalize(
                Quaternion.Inverse(capsuleWorldRot) * boneWorldRot);

            // Clamp capsule center above ground — use orientation-aware Y extent
            // (horizontal capsules only need radius clearance, not full halfLength)
            var capsuleYDir = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
            var yExtentFromCenter = MathF.Abs(capsuleYDir.Y) * def.CapsuleHalfLength + def.CapsuleRadius;
            var minY = groundY + 0.05f + yExtentFromCenter; // ground box top at groundY + 0.05
            if (capsuleCenter.Y < minY)
            {
                log.Info($"[Ragdoll Init] Clamping '{def.Name}' Y from {capsuleCenter.Y:F3} to {minY:F3}");
                capsuleCenter.Y = minY;
            }

            var capsule = new Capsule(def.CapsuleRadius, capsuleLength);
            var shapeIndex = simulation.Shapes.Add(capsule);

            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                capsule.ComputeInertia(def.Mass),
                new CollidableDescription(shapeIndex, 0.04f),
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
                SegmentHalfLength = segmentHalfLength,
            });

            // Log initial state for diagnostics
            var logCapsuleY = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
            log.Info($"[Ragdoll Init] '{def.Name}' idx={boneIdx} " +
                     $"bonePos=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                     $"capsuleCenter=({capsuleCenter.X:F3},{capsuleCenter.Y:F3},{capsuleCenter.Z:F3}) " +
                     $"segHalf={segmentHalfLength:F3} capsuleLen={capsuleLength:F3} " +
                     $"capsuleY=({logCapsuleY.X:F3},{logCapsuleY.Y:F3},{logCapsuleY.Z:F3})");
        }

        // --- Pass 3: Add constraints between connected bones ---
        // Per the BEPU RagdollDemo, each joint gets a layered constraint set:
        //   Ball joints: BallSocket + SwingLimit (cone) + TwistLimit (axial, asymmetric) + AngularMotor
        //   Hinge joints: Hinge + TwistLimit (angular range, asymmetric) + AngularMotor
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

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

            // --- Positional + angular constraint ---
            if (boneDef.Joint == JointType.Hinge)
            {
                // Hinge: constrains position AND restricts rotation to one plane.
                var hingeAxisWorld = ComputeHingeAxis(segDirWorld);
                var hingeAxisLocalChild = Vector3.Normalize(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
                var hingeAxisLocalParent = Vector3.Normalize(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new Hinge
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalHingeAxisA = hingeAxisLocalChild,
                        LocalOffsetB = parentLocalAnchor,
                        LocalHingeAxisB = hingeAxisLocalParent,
                        SpringSettings = new SpringSettings(30, 5),
                    });

                // TwistLimit as asymmetric angular range for the hinge.
                // The twist axis = hinge axis, reference = segment direction.
                // At init angle=0, positive = flexion, negative = hyperextension.
                if (boneDef.TwistMinAngle != 0 || boneDef.TwistMaxAngle != 0)
                {
                    var hingeTwistBasis = CreateTwistBasis(hingeAxisWorld, segDirWorld);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * hingeTwistBasis),
                            LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * hingeTwistBasis),
                            MinimumAngle = boneDef.TwistMinAngle,
                            MaximumAngle = boneDef.TwistMaxAngle,
                            SpringSettings = new SpringSettings(15, 3),
                        });
                }

                log.Info($"[Ragdoll Constraint] '{rb.Name}' Hinge axis=({hingeAxisWorld.X:F3},{hingeAxisWorld.Y:F3},{hingeAxisWorld.Z:F3}) range=[{boneDef.TwistMinAngle:F2},{boneDef.TwistMaxAngle:F2}]");
            }
            else
            {
                // BallSocket: positional connection, allows full rotation
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new BallSocket
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalOffsetB = parentLocalAnchor,
                        SpringSettings = new SpringSettings(30, 5),
                    });

                // SwingLimit: symmetric cone limiting deviation from initial direction
                if (boneDef.SwingLimit > 0)
                {
                    var axisChildLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(childBodyRef.Pose.Orientation));
                    var axisParentLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation));

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = axisChildLocal,
                            AxisLocalB = axisParentLocal,
                            MaximumSwingAngle = boneDef.SwingLimit,
                            SpringSettings = new SpringSettings(15, 3),
                        });
                }

                // TwistLimit: asymmetric axial rotation around the bone's segment axis
                if (boneDef.TwistMinAngle != 0 || boneDef.TwistMaxAngle != 0)
                {
                    var refDir = Vector3.Cross(segDirWorld, Vector3.UnitY);
                    if (refDir.LengthSquared() < 0.001f)
                        refDir = Vector3.Cross(segDirWorld, Vector3.UnitX);
                    refDir = Vector3.Normalize(refDir);

                    var twistBasis = CreateTwistBasis(segDirWorld, refDir);

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis),
                            LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis),
                            MinimumAngle = boneDef.TwistMinAngle,
                            MaximumAngle = boneDef.TwistMaxAngle,
                            SpringSettings = new SpringSettings(15, 3),
                        });
                }
            }

            // --- AngularMotor: damp relative angular velocity ---
            // Damping 0.01 matches BEPU RagdollDemo. Higher values (like 1.0) make joints
            // almost rigid, preventing natural articulation and causing uniform-fall behavior.
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

        // Resolve ancestor bone — n_hara must follow j_kosi to prevent mesh tearing
        nHaraIndex = boneService.ResolveBoneIndex(skel, "n_hara");
        log.Info($"RagdollController: n_hara index={nHaraIndex}");

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

        // --- Pass 1: Read physics bodies, compute bone world positions/rotations ---
        // We need all positions first to measure how far the ragdoll sank below
        // the real terrain (due to the lowered physics ground), then correct uniformly.
        var boneCount = ragdollBones.Count;
        var worldPositions = new Vector3[boneCount];
        var worldRotations = new Quaternion[boneCount];
        var boneValid = new bool[boneCount];
        var lowestBoneY = float.MaxValue;

        for (int i = 0; i < boneCount; i++)
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
            worldRotations[i] = Quaternion.Normalize(bodyRef.Pose.Orientation * rb.CapsuleToBoneOffset);

            // Convert capsule center (at segment midpoint) back to bone origin position.
            // The bone is at the parent-end of the capsule: center - halfLength * capsuleY.
            if (rb.SegmentHalfLength > 0)
            {
                var capsuleYAxis = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
                worldPositions[i] = bodyRef.Pose.Position - rb.SegmentHalfLength * capsuleYAxis;
            }
            else
            {
                worldPositions[i] = bodyRef.Pose.Position;
            }

            if (worldPositions[i].Y < lowestBoneY)
                lowestBoneY = worldPositions[i].Y;

            boneValid[i] = true;
        }

        // --- Floor offset correction ---
        // Physics ground was lowered by RagdollFloorOffset for stable constraint solving.
        // Measure how far the lowest bone sank below the REAL terrain and shift all bones
        // up by that amount. This way: during the fall (bones above real ground) no correction
        // is applied; after settling on the lowered ground, correction matches the actual sinkage.
        float yCorrection = 0f;
        if (config.RagdollFloorOffset > 0 && lowestBoneY < realGroundY)
        {
            yCorrection = realGroundY - lowestBoneY;
            // Cap at the offset amount — any sinkage beyond the offset is genuine physics
            // (e.g., slopes), not an artifact of the lowered ground.
            if (yCorrection > config.RagdollFloorOffset)
                yCorrection = config.RagdollFloorOffset;
        }

        // --- Pass 2: Apply correction and write to ModelPose ---
        for (int i = 0; i < boneCount; i++)
        {
            if (!boneValid[i]) continue;
            var rb = ragdollBones[i];

            var boneWorldPos = worldPositions[i];
            boneWorldPos.Y += yCorrection;

            var modelPos = WorldToModel(boneWorldPos);
            var modelRot = WorldRotToModel(worldRotations[i]);

            if (logThisFrame)
            {
                ref var origM = ref pose->ModelPose.Data[rb.BoneIndex];
                var animPos = new Vector3(origM.Translation.X, origM.Translation.Y, origM.Translation.Z);
                log.Info($"[Ragdoll F{frameCount}] '{rb.Name}' " +
                         $"animPos=({animPos.X:F3},{animPos.Y:F3},{animPos.Z:F3}) " +
                         $"boneWorld=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                         $"yCorr={yCorrection:F3} →modelPos=({modelPos.X:F3},{modelPos.Y:F3},{modelPos.Z:F3})");
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
        pairMaterial.FrictionCoefficient = 1f;
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
