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
    private readonly Npcs.NpcSelector npcSelector;
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

    // NPC bone collision — per-bone static capsules for active targets
    private readonly List<NpcCollisionState> npcCollisionStates = new();
    private TypedIndex npcFallbackShapeIndex;   // single-capsule fallback shape


    public bool IsActive => isActive;

    // Joint type determines which BEPU constraints are used:
    // Ball = BallSocket + SwingLimit + TwistLimit + AngularMotor (full 3-DOF rotation)
    // Hinge = Hinge + SwingLimit (bending range) + AngularMotor (1-DOF rotation)
    //   Per BEPU RagdollDemo: knees/elbows use SwingLimit (NOT TwistLimit) to limit
    //   the bending angle. TwistLimit measures twist around the Z axis of a basis —
    //   using it on the hinge axis can fight the Hinge constraint and prevent bending.
    //   SwingLimit compares two direction vectors and limits the angle between them,
    //   which naturally limits hinge bending when the axes are chosen correctly.
    private enum JointType { Ball, Hinge }

    // Ragdoll bone definition
    private struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit;      // Ball: cone angle (radians). Hinge: max bending angle (radians).
        public JointType Joint;       // constraint type
        public float TwistMinAngle;   // Ball: min axial twist (radians). Hinge: unused.
        public float TwistMaxAngle;   // Ball: max axial twist (radians). Hinge: unused.
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
    // Hinge joints: SwingLimit = max bending angle, TwistMin/Max = unused
    //   Per BEPU RagdollDemo, hinge bending is limited via SwingLimit (NOT TwistLimit).
    //   The SwingLimit compares a "forward" axis on the parent body with the child's
    //   segment axis. At full extension (straight limb) these are perpendicular.
    //   As the joint flexes, the angle decreases (allowed). Hyperextension would
    //   increase the angle beyond 90° (blocked by MaximumSwingAngle).
    private static readonly RagdollBoneDef[] BoneDefs = new[]
    {
        new RagdollBoneDef { Name = "j_kosi",    ParentName = null,       CapsuleRadius = 0.12f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,  Joint = JointType.Ball,  TwistMinAngle = 0f,     TwistMaxAngle = 0f    }, // pelvis — ~24cm diameter (hip volume)
        new RagdollBoneDef { Name = "j_sebo_a",  ParentName = "j_kosi",   CapsuleRadius = 0.10f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.2f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // lower spine — ~20cm diameter (waist volume)
        new RagdollBoneDef { Name = "j_sebo_b",  ParentName = "j_sebo_a", CapsuleRadius = 0.10f, CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f, Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // mid spine — ~20cm diameter (abdomen volume)
        new RagdollBoneDef { Name = "j_sebo_c",  ParentName = "j_sebo_b", CapsuleRadius = 0.10f, CapsuleHalfLength = 0.05f, Mass = 4.0f,  SwingLimit = 0.15f, Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // chest — ~20cm diameter (ribcage volume)
        new RagdollBoneDef { Name = "j_kubi",    ParentName = "j_sebo_c", CapsuleRadius = 0.04f, CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.25f, Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // neck
        new RagdollBoneDef { Name = "j_kao",     ParentName = "j_kubi",   CapsuleRadius = 0.08f, CapsuleHalfLength = 0.04f, Mass = 3.0f,  SwingLimit = 0.3f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // head
        new RagdollBoneDef { Name = "j_ude_a_l", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f  }, // shoulder
        new RagdollBoneDef { Name = "j_ude_a_r", ParentName = "j_sebo_c", CapsuleRadius = 0.03f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f  }, // shoulder
        new RagdollBoneDef { Name = "j_ude_b_l", ParentName = "j_ude_a_l",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = MathF.PI / 2,  Joint = JointType.Hinge, TwistMinAngle = 0f, TwistMaxAngle = 0f }, // elbow
        new RagdollBoneDef { Name = "j_ude_b_r", ParentName = "j_ude_a_r",CapsuleRadius = 0.025f,CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = MathF.PI / 2,  Joint = JointType.Hinge, TwistMinAngle = 0f, TwistMaxAngle = 0f }, // elbow
        new RagdollBoneDef { Name = "j_te_l",   ParentName = "j_ude_b_l",CapsuleRadius = 0.02f, CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // left wrist
        new RagdollBoneDef { Name = "j_te_r",   ParentName = "j_ude_b_r",CapsuleRadius = 0.02f, CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.8f,  Joint = JointType.Ball,  TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f  }, // right wrist
        new RagdollBoneDef { Name = "j_asi_a_l", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.3f,  Joint = JointType.Ball,  TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f  }, // hip — wide cone for death settling
        new RagdollBoneDef { Name = "j_asi_a_r", ParentName = "j_kosi",   CapsuleRadius = 0.04f, CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.3f,  Joint = JointType.Ball,  TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f  }, // hip — wide cone for death settling
        new RagdollBoneDef { Name = "j_asi_b_l", ParentName = "j_asi_a_l",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,  Joint = JointType.Hinge, TwistMinAngle = 0f, TwistMaxAngle = 0f }, // knee
        new RagdollBoneDef { Name = "j_asi_b_r", ParentName = "j_asi_a_r",CapsuleRadius = 0.035f,CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,  Joint = JointType.Hinge, TwistMinAngle = 0f, TwistMaxAngle = 0f }, // knee
        new RagdollBoneDef { Name = "j_asi_c_l", ParentName = "j_asi_b_l",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // foot
        new RagdollBoneDef { Name = "j_asi_c_r", ParentName = "j_asi_b_r",CapsuleRadius = 0.03f, CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,  Joint = JointType.Ball,  TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f  }, // foot
    };

    // Per-bone static collision body for an NPC (dynamically created from skeleton)
    private struct NpcBoneStatic
    {
        public StaticHandle Handle;
        public int BoneIndex;           // this bone's skeleton index
        public int ParentBoneIndex;     // parent bone for segment direction
        public float HalfLength;        // half the segment length (scaled)
    }

    // Default capsule radius for dynamically discovered bones
    private const float NpcDefaultBoneRadius = 0.04f;
    // Minimum segment length to create a collision capsule (skip face/hair bones)
    private const float NpcMinSegmentLength = 0.02f;

    // Per-NPC collision state (bone-based or single-capsule fallback)
    private struct NpcCollisionState
    {
        public nint NpcAddress;
        public List<NpcBoneStatic> BoneStatics;   // populated when skeleton readable
        public StaticHandle FallbackHandle;        // used when skeleton unreadable
        public bool IsFallback;
    }

    public RagdollController(BoneTransformService boneService, Npcs.NpcSelector npcSelector, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.npcSelector = npcSelector;
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

    // Static overloads for NPC skeletons (each NPC has its own transform)
    private static Vector3 NpcModelToWorld(Vector3 modelPos, Vector3 skelPos, Quaternion skelRot)
        => skelPos + Vector3.Transform(modelPos, skelRot);

    private static Quaternion NpcModelRotToWorld(Quaternion modelRot, Quaternion skelRot)
        => Quaternion.Normalize(skelRot * modelRot);

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
        // When self-collision is enabled, ConnectedPairs filters out nearby body pairs
        // (1-2 hops) while allowing distant body-body collisions (arms vs torso).
        // When disabled (null), only body-static collisions are allowed.
        var connectedPairs = config.RagdollSelfCollision ? new HashSet<(int, int)>() : null;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks { ConnectedPairs = connectedPairs },
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(8, 1));

        // Add ground plane as a thick static box. The box must be thick enough
        // to prevent capsules from tunneling through during fast impacts (e.g.,
        // knee bending pushes shin capsule downward at high speed). A 0.1m box
        // was easily penetrated; 10m prevents any tunneling. The box is positioned
        // so its TOP surface is at groundY.
        var groundThickness = 10f;
        var groundShapeIndex = simulation.Shapes.Add(new Box(1000, groundThickness, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY - groundThickness / 2f, 0),
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
        // Capsule center is offset from bone origin (joint) along the segment direction.
        // Capsule half-length is clamped to not exceed half the actual segment distance
        // in the current pose. Death poses bend joints severely (e.g., shin=0.06m vs
        // 0.22m anatomical), and oversized capsules overlap their neighbors, causing
        // explosive solver forces in the first frames.
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            Vector3 capsuleCenter;
            float segmentHalfLength;
            float effectiveHalfLength = def.CapsuleHalfLength;
            Quaternion capsuleWorldRot;

            if (boneToFirstChild.TryGetValue(def.Name, out var childName) &&
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
                    if (effectiveHalfLength > maxHalf)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' capsule clamped: halfLen {effectiveHalfLength:F3} -> {maxHalf:F3} (segLen={segLen:F3})");
                        effectiveHalfLength = maxHalf;
                    }

                    var segDir = segment / segLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * segDir;
                    segmentHalfLength = effectiveHalfLength;
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
                    var maxHalf = MathF.Max(0.02f, toSkelChildLen * 0.45f);
                    if (effectiveHalfLength > maxHalf)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' leaf capsule clamped: halfLen {effectiveHalfLength:F3} -> {maxHalf:F3} (segLen={toSkelChildLen:F3})");
                        effectiveHalfLength = maxHalf;
                    }

                    var dir = toSkelChild / toSkelChildLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * dir;
                    segmentHalfLength = effectiveHalfLength;
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

            // Clamp capsule center above ground so bodies don't start underground.
            // Underground capsules cause explosive ground-collision forces in the first
            // frames. Lift just enough so the capsule bottom (center - extent - radius)
            // is at the ground plane.
            var capsuleYAxis = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
            var capsuleBottomExtent = MathF.Abs(capsuleYAxis.Y) * effectiveHalfLength + def.CapsuleRadius;
            var minCenterY = groundY + capsuleBottomExtent + 0.005f; // 5mm clearance
            if (capsuleCenter.Y < minCenterY)
            {
                log.Info($"[Ragdoll Init] '{def.Name}' lifted above ground: Y {capsuleCenter.Y:F3} -> {minCenterY:F3} (ground={groundY:F3})");
                capsuleCenter.Y = minCenterY;
            }

            var capsuleLength = effectiveHalfLength * 2;
            var capsule = new Capsule(def.CapsuleRadius, capsuleLength);
            var shapeIndex = simulation.Shapes.Add(capsule);

            // SleepThreshold: 0.01 = normal (bodies sleep when settled).
            // -1 = never sleep (settle collision keeps bodies always active for NPC interaction).
            var sleepThreshold = (config.RagdollNpcSettleCollision && config.RagdollNpcCollision) ? -1f : 0.01f;
            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                capsule.ComputeInertia(def.Mass * config.RagdollMassScale),
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(sleepThreshold));

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

        // Joint stiffness: multiply constraint spring frequencies and motor damping.
        // Higher = stiffer joints, body resists twisting. 1.0 = default ragdoll behavior.
        var stiffness = config.RagdollJointStiffness;
        var jointSpring = new SpringSettings(30 * stiffness, 1);
        var limitSpring = new SpringSettings(15 * stiffness, 1);
        var motorDamping = 0.01f * stiffness;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            // When self-collision is enabled, exclude nearby pairs (1-2 hops)
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

            // --- Positional + angular constraint ---
            if (boneDef.Joint == JointType.Hinge)
            {
                // Hinge: constrains position AND restricts rotation to one plane.
                // Per BEPU RagdollDemo: Hinge + SwingLimit + AngularMotor (NO TwistLimit).
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
                        SpringSettings = jointSpring,
                    });

                // SwingLimit as bending range for the hinge (BEPU RagdollDemo pattern).
                // Body A = child (shin/forearm), Body B = parent (thigh/upper arm).
                // AxisLocalA = child's segment direction (capsule Y in child local space).
                // AxisLocalB = "forward" direction on the parent body, perpendicular to the
                //   parent's segment axis in the bending plane (Cross of hingeAxis x parentSegDir).
                // At full extension (straight limb): these axes are perpendicular (90°).
                // As the joint flexes: child axis rotates toward parent forward, angle decreases (allowed).
                // Hyperextension: child axis rotates away, angle exceeds 90° (blocked).
                if (boneDef.SwingLimit > 0)
                {
                    // "Forward" direction on the parent body = Cross(hingeAxis, parentSegDir).
                    // This is the direction the child limb swings toward during flexion.
                    var parentSegDir = Vector3.Transform(Vector3.UnitY, parentBodyRef.Pose.Orientation);
                    var forwardWorld = Vector3.Normalize(Vector3.Cross(hingeAxisWorld, parentSegDir));

                    // The cross product can point in either direction. We validate it
                    // against the ACTUAL child segment direction from the init pose.
                    // The death animation already has joints bent correctly, so the child
                    // segment (shin for knees, forearm for elbows) points in the direction
                    // of correct flexion. forwardWorld must agree with that direction.
                    // This is robust regardless of character orientation or facing convention.
                    if (Vector3.Dot(forwardWorld, segDirWorld) < 0)
                        forwardWorld = -forwardWorld;

                    var swingAxisLocalParent = Vector3.Normalize(Vector3.Transform(
                        forwardWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));
                    var swingAxisLocalChild = Vector3.Normalize(Vector3.Transform(
                        segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

                    // When tight knee limits are enabled, use a reduced swing angle
                    // that locks joints near their init pose (guided bend mode).
                    var effectiveSwingLimit = boneDef.SwingLimit;
                    if (config.RagdollTightKneeLimits)
                        effectiveSwingLimit = MathF.Min(effectiveSwingLimit, 0.5f);

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = swingAxisLocalChild,   // child body = A (matches Hinge body order)
                            AxisLocalB = swingAxisLocalParent,  // parent body = B
                            MaximumSwingAngle = effectiveSwingLimit,
                            SpringSettings = limitSpring,
                        });

                    log.Info($"[Ragdoll Constraint] '{rb.Name}' SwingLimit: parentFwd=({forwardWorld.X:F3},{forwardWorld.Y:F3},{forwardWorld.Z:F3}) childSeg=({segDirWorld.X:F3},{segDirWorld.Y:F3},{segDirWorld.Z:F3}) max={boneDef.SwingLimit:F2}rad dot={Vector3.Dot(forwardWorld, segDirWorld):F3}");

                    // TwistLimit as one-sided anti-hyperextension constraint.
                    // Twist axis = hinge axis. Reference direction = forwardWorld (flexion direction).
                    // The current angle between child segment and forwardWorld is the "init angle".
                    // Allow full flexion (angle can decrease toward 0) but block hyperextension
                    // (angle cannot increase much beyond the init value).
                    var initAngle = MathF.Acos(Math.Clamp(
                        Vector3.Dot(segDirWorld, forwardWorld), -1f, 1f));
                    var twistBasis = CreateTwistBasis(hingeAxisWorld, forwardWorld);
                    var twistBasisLocalChild = Quaternion.Normalize(
                        Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis);
                    var twistBasisLocalParent = Quaternion.Normalize(
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis);

                    // Min = -0.1 (allow tiny backward motion beyond init)
                    // Max = init angle + 0.1 (block further hyperextension)
                    // At init: angle ≈ initAngle. Flexion moves toward 0 (allowed).
                    // Hyperextension moves beyond initAngle (blocked by Max).
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = twistBasisLocalChild,
                            LocalBasisB = twistBasisLocalParent,
                            MinimumAngle = -0.1f,
                            MaximumAngle = initAngle + 0.15f,
                            SpringSettings = limitSpring,
                        });

                    log.Info($"[Ragdoll Constraint] '{rb.Name}' TwistLimit: initAngle={initAngle:F2}rad min={-0.1f:F2} max={initAngle + 0.15f:F2}");
                }

                log.Info($"[Ragdoll Constraint] '{rb.Name}' Hinge axis=({hingeAxisWorld.X:F3},{hingeAxisWorld.Y:F3},{hingeAxisWorld.Z:F3})");
            }
            else
            {
                // BallSocket: positional connection, allows full rotation
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new BallSocket
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalOffsetB = parentLocalAnchor,
                        SpringSettings = jointSpring,
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
                            SpringSettings = limitSpring,
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
                            SpringSettings = limitSpring,
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
                    Settings = new MotorSettings(float.MaxValue, motorDamping),
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

        // Create NPC collision volumes — dynamically discover bones from each NPC's skeleton.
        // Works for any model (humanoid, monster, dragon) — no hardcoded bone names.
        npcCollisionStates.Clear();
        if (config.RagdollNpcCollision)
        {
            var scale = config.RagdollNpcCollisionScale;
            var capsuleRadius = NpcDefaultBoneRadius * scale;

            // Fallback single-capsule shape for NPCs whose skeleton can't be read
            var fbRadius = 0.3f * scale;
            var fbLength = MathF.Max(0.2f, 1.6f - fbRadius * 2f);
            npcFallbackShapeIndex = simulation.Shapes.Add(new Capsule(fbRadius, fbLength));

            log.Info($"RagdollController: NPC bone collision — {npcSelector.SelectedNpcs.Count} NPCs, scale={scale:F2}");

            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.BattleChara == null)
                {
                    log.Warning($"RagdollController: NPC '{npc.Name}' has null BattleChara, skipping");
                    continue;
                }

                var npcAddr = npc.Address;
                var npcSkel = boneService.TryGetSkeleton(npcAddr);

                if (npcSkel == null)
                {
                    CreateFallbackNpcCollision(npc, npcAddr);
                    continue;
                }

                var ns = npcSkel.Value;
                var npcSkeleton = ns.CharBase->Skeleton;
                if (npcSkeleton == null)
                {
                    CreateFallbackNpcCollision(npc, npcAddr);
                    continue;
                }

                var npcSkelPos = new Vector3(
                    npcSkeleton->Transform.Position.X,
                    npcSkeleton->Transform.Position.Y,
                    npcSkeleton->Transform.Position.Z);
                var npcSkelRot = new Quaternion(
                    npcSkeleton->Transform.Rotation.X,
                    npcSkeleton->Transform.Rotation.Y,
                    npcSkeleton->Transform.Rotation.Z,
                    npcSkeleton->Transform.Rotation.W);

                var boneStatics = new List<NpcBoneStatic>();

                // Iterate all bones with a parent — each parent→child segment becomes a capsule
                var boneCount = Math.Min(ns.BoneCount, ns.ParentCount);
                for (int i = 1; i < boneCount; i++) // skip root (index 0, no parent)
                {
                    var parentIdx = ns.HavokSkeleton->ParentIndices[i];
                    if (parentIdx < 0 || parentIdx >= ns.BoneCount) continue;

                    // Read parent and child bone world positions
                    ref var parentMt = ref ns.Pose->ModelPose.Data[parentIdx];
                    var parentModelPos = new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z);
                    var parentWorldPos = NpcModelToWorld(parentModelPos, npcSkelPos, npcSkelRot);

                    ref var childMt = ref ns.Pose->ModelPose.Data[i];
                    var childModelPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                    var childWorldPos = NpcModelToWorld(childModelPos, npcSkelPos, npcSkelRot);

                    var segment = childWorldPos - parentWorldPos;
                    var segLen = segment.Length();
                    if (segLen < NpcMinSegmentLength) continue; // skip tiny segments (face, fingers)

                    var halfLen = segLen * 0.45f * scale;
                    var capsuleLength = halfLen * 2f;
                    var shapeIndex = simulation.Shapes.Add(new Capsule(capsuleRadius, capsuleLength));

                    var segDir = segment / segLen;
                    var capsuleCenter = parentWorldPos + halfLen * segDir;
                    var capsuleRot = RotationFromYToDirection(segment);

                    var staticHandle = simulation.Statics.Add(new StaticDescription(
                        capsuleCenter, capsuleRot, shapeIndex));

                    boneStatics.Add(new NpcBoneStatic
                    {
                        Handle = staticHandle,
                        BoneIndex = i,
                        ParentBoneIndex = parentIdx,
                        HalfLength = halfLen,
                    });
                }

                if (boneStatics.Count > 0)
                {
                    npcCollisionStates.Add(new NpcCollisionState
                    {
                        NpcAddress = npcAddr,
                        BoneStatics = boneStatics,
                        IsFallback = false,
                    });
                    log.Info($"RagdollController: NPC '{npc.Name}' bone collision — {boneStatics.Count} segments from {boneCount} bones");
                }
                else
                {
                    CreateFallbackNpcCollision(npc, npcAddr);
                }
            }
        }

        var totalNpcStatics = 0;
        foreach (var s in npcCollisionStates)
            totalNpcStatics += s.IsFallback ? 1 : s.BoneStatics.Count;
        log.Info($"RagdollController: Physics initialized — {ragdollBones.Count} bodies, {npcCollisionStates.Count} NPCs ({totalNpcStatics} collision volumes), ground={groundY:F3}");
        return ragdollBones.Count > 0;
    }

    /// <summary>
    /// Create a single fallback capsule for an NPC whose skeleton can't be read.
    /// </summary>
    private void CreateFallbackNpcCollision(Npcs.SimulatedNpc npc, nint npcAddr)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
        var npcPos = new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z);
        var handle = simulation!.Statics.Add(new StaticDescription(
            npcPos, Quaternion.Identity, npcFallbackShapeIndex));
        npcCollisionStates.Add(new NpcCollisionState
        {
            NpcAddress = npcAddr,
            BoneStatics = new List<NpcBoneStatic>(),
            FallbackHandle = handle,
            IsFallback = true,
        });
        log.Info($"RagdollController: NPC '{npc.Name}' using fallback single capsule");
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

        // Update skeleton transform for WorldToModel conversion.
        // The game may reposition the character root (e.g., dismount, death transition).
        // Physics bodies stay at correct world positions; we just need the current
        // transform to convert back to the model space the game expects for rendering.
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton != null)
        {
            var newSkelPos = new Vector3(
                skeleton->Transform.Position.X,
                skeleton->Transform.Position.Y,
                skeleton->Transform.Position.Z);

            // If skeleton moved significantly, re-raycast ground at new position
            var skelDist = (newSkelPos - skelWorldPos).Length();
            if (skelDist > 0.1f)
            {
                log.Info($"[Ragdoll F{frameCount}] Skeleton moved {skelDist:F3}m: ({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3})→({newSkelPos.X:F3},{newSkelPos.Y:F3},{newSkelPos.Z:F3})");
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(newSkelPos.X, newSkelPos.Y + 2.0f, newSkelPos.Z),
                        new Vector3(0, -1, 0),
                        out var hitInfo,
                        50f))
                {
                    realGroundY = hitInfo.Point.Y;
                    groundY = realGroundY - config.RagdollFloorOffset;
                    log.Info($"[Ragdoll F{frameCount}] Ground re-raycast: realY={realGroundY:F3} physicsY={groundY:F3}");
                }
            }

            skelWorldPos = newSkelPos;
            skelWorldRot = new Quaternion(
                skeleton->Transform.Rotation.X,
                skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z,
                skeleton->Transform.Rotation.W);
            skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        }

        // Update NPC collision volumes to track their current animated bone positions.
        // Must call UpdateBounds() after repositioning — BEPU2 doesn't auto-update
        // broad phase AABBs for statics, so without it collisions are never detected.
        for (int i = 0; i < npcCollisionStates.Count; i++)
        {
            var npcState = npcCollisionStates[i];
            try
            {
                if (npcState.IsFallback)
                {
                    // Simple single-capsule position update
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npcState.NpcAddress;
                    var staticRef = simulation.Statics.GetStaticReference(npcState.FallbackHandle);
                    staticRef.Pose.Position = new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z);
                    staticRef.UpdateBounds();
                    continue;
                }

                // Bone-based update: read NPC skeleton and reposition each bone static
                var npcSkel = boneService.TryGetSkeleton(npcState.NpcAddress);
                if (npcSkel == null) continue; // skeleton temporarily unavailable, keep last positions

                var ns = npcSkel.Value;
                var npcSkeleton = ns.CharBase->Skeleton;
                if (npcSkeleton == null) continue;

                var npcSkelPos = new Vector3(
                    npcSkeleton->Transform.Position.X,
                    npcSkeleton->Transform.Position.Y,
                    npcSkeleton->Transform.Position.Z);
                var npcSkelRot = new Quaternion(
                    npcSkeleton->Transform.Rotation.X,
                    npcSkeleton->Transform.Rotation.Y,
                    npcSkeleton->Transform.Rotation.Z,
                    npcSkeleton->Transform.Rotation.W);

                for (int b = 0; b < npcState.BoneStatics.Count; b++)
                {
                    var bs = npcState.BoneStatics[b];
                    if (bs.BoneIndex < 0 || bs.BoneIndex >= ns.BoneCount) continue;
                    if (bs.ParentBoneIndex < 0 || bs.ParentBoneIndex >= ns.BoneCount) continue;

                    // Read parent→child segment from current animation pose
                    ref var parentMt = ref ns.Pose->ModelPose.Data[bs.ParentBoneIndex];
                    var parentWorldPos = NpcModelToWorld(
                        new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                        npcSkelPos, npcSkelRot);

                    ref var childMt = ref ns.Pose->ModelPose.Data[bs.BoneIndex];
                    var childWorldPos = NpcModelToWorld(
                        new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                        npcSkelPos, npcSkelRot);

                    var segment = childWorldPos - parentWorldPos;
                    var segLen = segment.Length();

                    Vector3 capsuleCenter;
                    Quaternion capsuleRot;
                    if (segLen > 0.01f)
                    {
                        var segDir = segment / segLen;
                        capsuleCenter = parentWorldPos + bs.HalfLength * segDir;
                        capsuleRot = RotationFromYToDirection(segment);
                    }
                    else
                    {
                        capsuleCenter = parentWorldPos;
                        capsuleRot = Quaternion.Identity;
                    }

                    var staticRef = simulation.Statics.GetStaticReference(bs.Handle);
                    staticRef.Pose.Position = capsuleCenter;
                    staticRef.Pose.Orientation = capsuleRot;
                    staticRef.UpdateBounds();
                }
            }
            catch { }
        }

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
        var logThisFrame = frameCount <= 3 || frameCount % 12 == 0; // every 0.2s at 60fps

        // --- Pass 1: Read physics bodies, compute bone world positions/rotations ---
        // We need all positions first to measure how far the ragdoll sank below
        // the real terrain (due to the lowered physics ground), then correct uniformly.
        var boneCount = ragdollBones.Count;
        var worldPositions = new Vector3[boneCount];
        var worldRotations = new Quaternion[boneCount];
        var boneValid = new bool[boneCount];
        var lowestCapsuleBottomY = float.MaxValue;

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

            // Track lowest capsule bottom (not bone origin) for floor correction.
            // The capsule extends below its center by |capsuleY.Y| * halfLength + radius.
            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }
            var capsuleYDir = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
            var capsuleBottomY = bodyRef.Pose.Position.Y
                                 - MathF.Abs(capsuleYDir.Y) * boneDef.CapsuleHalfLength
                                 - boneDef.CapsuleRadius;
            if (capsuleBottomY < lowestCapsuleBottomY)
                lowestCapsuleBottomY = capsuleBottomY;

            boneValid[i] = true;
        }

        // --- Floor offset correction ---
        // Physics ground was lowered by RagdollFloorOffset for stable constraint solving.
        // Measure how far the lowest capsule bottom sank below the REAL terrain and shift
        // all bones up by that amount. Uses capsule extents (not bone origins) because
        // a bone origin can be above ground while its capsule extends below.
        // Skip floor correction during the first ~30 frames to let the ragdoll settle.
        // On the first frame, capsule bottoms from the init pose (e.g., standing on a mount)
        // may extend below realGroundY even though the character is above the ground.
        // Applying correction immediately causes a visible upward bounce.
        float yCorrection = 0f;
        if (frameCount > 30 && config.RagdollFloorOffset > 0 && lowestCapsuleBottomY < realGroundY)
        {
            yCorrection = realGroundY - lowestCapsuleBottomY;
            // Cap at the offset amount — any sinkage beyond the offset is genuine physics
            // (e.g., slopes), not an artifact of the lowered ground.
            if (yCorrection > config.RagdollFloorOffset)
                yCorrection = config.RagdollFloorOffset;
        }

        // --- Frame summary (once per log frame, before per-bone data) ---
        if (logThisFrame)
        {
            var awakeBodies = 0;
            var maxLinVel = 0f;
            var maxAngVel = 0f;
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i]) continue;
                var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
                if (bodyRef.Awake) awakeBodies++;
                var linSpeed = bodyRef.Velocity.Linear.Length();
                var angSpeed = bodyRef.Velocity.Angular.Length();
                if (linSpeed > maxLinVel) maxLinVel = linSpeed;
                if (angSpeed > maxAngVel) maxAngVel = angSpeed;
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

            var boneWorldPos = worldPositions[i];
            boneWorldPos.Y += yCorrection;

            var modelPos = WorldToModel(boneWorldPos);
            var modelRot = WorldRotToModel(worldRotations[i]);

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
        npcCollisionStates.Clear();
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
    // Connected body pairs that should NOT collide (parent-child joints).
    // All other body-body pairs DO collide (arms vs torso, etc.).
    public HashSet<(int, int)> ConnectedPairs;

    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Always allow body-static collisions (ragdoll vs ground)
        if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static)
            return true;

        // Body-body: allow UNLESS they are directly connected by a joint.
        // Connected pairs would explode because they share a constraint anchor point.
        if (ConnectedPairs != null && a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
        {
            var idA = a.BodyHandle.Value;
            var idB = b.BodyHandle.Value;
            var lo = Math.Min(idA, idB);
            var hi = Math.Max(idA, idB);
            if (ConnectedPairs.Contains((lo, hi)))
                return false;
            return true;
        }

        return false;
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
