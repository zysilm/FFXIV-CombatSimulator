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

    // Weapon drop physics
    private BodyHandle? weaponMainHandBody;
    private BodyHandle? weaponOffHandBody;
    private int weaponMainHandBoneIndex = -1;
    private int weaponOffHandBoneIndex = -1;
    private Quaternion weaponMainHandCapsuleToBone;
    private Quaternion weaponOffHandCapsuleToBone;
    private float weaponMainHandSegHalf;
    private float weaponOffHandSegHalf;
    private static readonly string[] WeaponMainHandBones = { "n_buki_r", "j_buki_r", "n_hte_r" };
    private static readonly string[] WeaponOffHandBones = { "n_buki_l", "j_buki_l", "n_hte_l" };

    // Ancestor bone index — n_hara must follow j_kosi to prevent mesh tearing
    private int nHaraIndex = -1;
    // Head bone index (j_kao) — used for hair physics and partial skeleton propagation
    private int kaoBodyBoneIndex = -1;
    // Hair physics simulator
    private HairPhysicsSimulator? hairPhysics;

    // NPC bone collision — per-bone static capsules for active targets
    private readonly List<NpcCollisionState> npcCollisionStates = new();
    private TypedIndex npcFallbackShapeIndex;   // single-capsule fallback shape


    public bool IsActive => isActive;
    public nint TargetCharacterAddress => targetCharacterAddress;

    /// <summary>Debug draw data for a single ragdoll capsule body.</summary>
    public struct DebugCapsule
    {
        public Vector3 Position;      // capsule center (world space)
        public Quaternion Orientation; // capsule rotation
        public float Radius;
        public float HalfLength;      // half of the segment length (capsule extends along Y)
        public string Name;
        public JointType Joint;
        public float SwingLimit;
    }

    /// <summary>
    /// Get current capsule positions/sizes for debug rendering.
    /// Returns empty if ragdoll is not active.
    /// </summary>
    public List<DebugCapsule> GetDebugCapsules()
    {
        var result = new List<DebugCapsule>();
        if (!isActive || simulation == null) return result;

        var BoneDefs = GetBoneDefs();
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);

            // Find matching bone def for capsule dimensions
            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }

            result.Add(new DebugCapsule
            {
                Position = bodyRef.Pose.Position,
                Orientation = bodyRef.Pose.Orientation,
                Radius = boneDef.CapsuleRadius,
                HalfLength = boneDef.CapsuleHalfLength,
                Name = rb.Name,
                Joint = boneDef.Joint,
                SwingLimit = boneDef.SwingLimit,
            });
        }
        return result;
    }

    /// <summary>
    /// Get effective bone definitions: from config if populated, otherwise defaults.
    /// Filters to enabled bones and computes physics parents.
    /// </summary>
    private RagdollBoneDef[] GetBoneDefs()
    {
        if (config.RagdollBoneConfigs.Count == 0)
            return DefaultBoneDefs;

        return BuildBoneDefsFromConfigs(config.RagdollBoneConfigs.ToArray());
    }

    // Joint type determines which BEPU constraints are used:
    // Ball = BallSocket + SwingLimit + TwistLimit + AngularMotor (full 3-DOF rotation)
    // Hinge = Hinge + SwingLimit (bending range) + AngularMotor (1-DOF rotation)
    //   Per BEPU RagdollDemo: knees/elbows use SwingLimit (NOT TwistLimit) to limit
    //   the bending angle. TwistLimit measures twist around the Z axis of a basis —
    //   using it on the hinge axis can fight the Hinge constraint and prevent bending.
    //   SwingLimit compares two direction vectors and limits the angle between them,
    //   which naturally limits hinge bending when the axes are chosen correctly.
    public enum JointType { Ball, Hinge }

    // Ragdoll bone definition
    public struct RagdollBoneDef
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
    /// <summary>
    /// Complete bone catalog: all structural skeleton bones with skeleton parents,
    /// default enabled state, and physics parameters. Current v1.6.1 tested 18 bones
    /// are enabled by default. Additional bones default to disabled.
    ///
    /// Skeleton hierarchy (from Ktisis/xivmodding):
    ///   j_kosi (pelvis, root)
    ///     j_sebo_a (lumbar) → j_sebo_b (thoracic) → j_sebo_c (chest)
    ///       j_kubi (neck) → j_kao (head)
    ///       j_sako_l/r (clavicle) → j_ude_a (upper arm) → j_ude_b (forearm) → j_te (hand)
    ///       j_mune_l/r (breast, child of j_sebo_b)
    ///     j_asi_a (thigh) → j_asi_b (shin) → j_asi_c (calf) → j_asi_d (foot) → j_asi_e (toes)
    /// </summary>
    public static readonly RagdollBoneConfig[] AllBoneDefaults = new[]
    {
        // === SPINE CHAIN === (all enabled by default — core of ragdoll)
        new RagdollBoneConfig { Name = "j_kosi",    SkeletonParent = null,       Enabled = true,  CapsuleRadius = 0.12f,  CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Pelvis" },
        new RagdollBoneConfig { Name = "j_sebo_a",  SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Lower Spine" },
        new RagdollBoneConfig { Name = "j_sebo_b",  SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Mid Spine" },
        new RagdollBoneConfig { Name = "j_sebo_c",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 4.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Chest" },
        new RagdollBoneConfig { Name = "j_kubi",    SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.25f,               JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Neck" },
        new RagdollBoneConfig { Name = "j_kao",     SkeletonParent = "j_kubi",   Enabled = true,  CapsuleRadius = 0.08f,  CapsuleHalfLength = 0.04f, Mass = 3.0f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Head" },

        // === BREAST === (child of j_sebo_b, disabled by default — cosmetic)
        new RagdollBoneConfig { Name = "j_mune_l",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.02f, Mass = 0.5f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Breast" },
        new RagdollBoneConfig { Name = "j_mune_r",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.02f, Mass = 0.5f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Breast" },

        // === CLAVICLE === (child of j_sebo_c, disabled — enabling inserts between chest and arms)
        new RagdollBoneConfig { Name = "j_sako_l",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 1.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Left Clavicle" },
        new RagdollBoneConfig { Name = "j_sako_r",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 1.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Right Clavicle" },

        // === ARM CHAIN === (all enabled — skeleton: j_sako → j_ude_a → j_ude_b → j_te)
        new RagdollBoneConfig { Name = "j_ude_a_l", SkeletonParent = "j_sako_l", Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,                JointType = 0, TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f,  Description = "Left Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_a_r", SkeletonParent = "j_sako_r", Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,                JointType = 0, TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f,  Description = "Right Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_b_l", SkeletonParent = "j_ude_a_l",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Left Forearm" },
        new RagdollBoneConfig { Name = "j_ude_b_r", SkeletonParent = "j_ude_a_r",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.5f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Right Forearm" },
        new RagdollBoneConfig { Name = "j_te_l",    SkeletonParent = "j_ude_b_l",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.8f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Left Hand" },
        new RagdollBoneConfig { Name = "j_te_r",    SkeletonParent = "j_ude_b_r",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.8f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Right Hand" },

        // === LEG CHAIN === (j_asi_a/b/c enabled, j_asi_d/e disabled by default)
        new RagdollBoneConfig { Name = "j_asi_a_l", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Left Thigh" },
        new RagdollBoneConfig { Name = "j_asi_a_r", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.12f, Mass = 4.0f,  SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Right Thigh" },
        new RagdollBoneConfig { Name = "j_asi_b_l", SkeletonParent = "j_asi_a_l",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Left Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_b_r", SkeletonParent = "j_asi_a_r",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Right Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_c_l", SkeletonParent = "j_asi_b_l",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Left Calf" },
        new RagdollBoneConfig { Name = "j_asi_c_r", SkeletonParent = "j_asi_b_r",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Right Calf" },
        new RagdollBoneConfig { Name = "j_asi_d_l", SkeletonParent = "j_asi_c_l",Enabled = false, CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Left Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_d_r", SkeletonParent = "j_asi_c_r",Enabled = false, CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Right Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_e_l", SkeletonParent = "j_asi_d_l",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Toes" },
        new RagdollBoneConfig { Name = "j_asi_e_r", SkeletonParent = "j_asi_d_r",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Toes" },
    };

    /// <summary>
    /// Legacy accessor — returns only the enabled bones as RagdollBoneDef[],
    /// computing physics parents by walking up the skeleton tree to find the
    /// nearest enabled ancestor.
    /// </summary>
    public static readonly RagdollBoneDef[] DefaultBoneDefs = BuildDefaultBoneDefs();

    private static RagdollBoneDef[] BuildDefaultBoneDefs()
    {
        return BuildBoneDefsFromConfigs(AllBoneDefaults);
    }

    /// <summary>
    /// Convert config list to RagdollBoneDef[], filtering to enabled bones and
    /// computing physics parents (nearest enabled ancestor in skeleton tree).
    /// </summary>
    public static RagdollBoneDef[] BuildBoneDefsFromConfigs(RagdollBoneConfig[] configs)
    {
        // Build lookup of known skeleton parents from AllBoneDefaults (fallback)
        var defaultParents = new Dictionary<string, string?>();
        foreach (var d in AllBoneDefaults)
            defaultParents[d.Name] = d.SkeletonParent;

        var enabledNames = new HashSet<string>();
        foreach (var c in configs)
            if (c.Enabled) enabledNames.Add(c.Name);

        var configByName = new Dictionary<string, RagdollBoneConfig>();
        foreach (var c in configs)
            configByName[c.Name] = c;

        var result = new List<RagdollBoneDef>();
        foreach (var c in configs)
        {
            if (!c.Enabled) continue;

            // Walk up skeleton tree to find nearest enabled parent.
            // Use SkeletonParent from config, fall back to AllBoneDefaults if null.
            string? physicsParent = null;
            var skelParent = c.SkeletonParent;
            if (skelParent == null) defaultParents.TryGetValue(c.Name, out skelParent);
            var current = skelParent;
            while (current != null)
            {
                if (enabledNames.Contains(current))
                {
                    physicsParent = current;
                    break;
                }
                // Walk further up: check config, then default
                string? nextParent = null;
                if (configByName.TryGetValue(current, out var parentConfig))
                    nextParent = parentConfig.SkeletonParent;
                if (nextParent == null)
                    defaultParents.TryGetValue(current, out nextParent);
                current = nextParent;
            }

            result.Add(new RagdollBoneDef
            {
                Name = c.Name,
                ParentName = physicsParent,
                CapsuleRadius = c.CapsuleRadius,
                CapsuleHalfLength = c.CapsuleHalfLength,
                Mass = c.Mass,
                SwingLimit = c.SwingLimit,
                Joint = (JointType)c.JointType,
                TwistMinAngle = c.TwistMinAngle,
                TwistMaxAngle = c.TwistMaxAngle,
            });
        }
        return result.ToArray();
    }

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
        kaoBodyBoneIndex = -1;
        weaponMainHandBody = null;
        weaponOffHandBody = null;
        weaponMainHandBoneIndex = -1;
        weaponOffHandBoneIndex = -1;
        hairPhysics?.Reset();
        hairPhysics = null;

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
        var BoneDefs = GetBoneDefs();

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
            new SolveDescription(config.RagdollSolverIterations, 1));

        // Safety net: flat box well below the character prevents infinite falling
        // if the terrain mesh has gaps or winding issues.
        var groundThickness = 10f;
        var safetyBoxIndex = simulation.Shapes.Add(new Box(1000, groundThickness, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY - groundThickness / 2f - 2f, 0),
            Quaternion.Identity,
            safetyBoxIndex));

        // Build terrain mesh from raycasts to capture hills, slopes, and valleys.
        {
            var terrainRadius = 4.0f;
            var terrainStep = 0.5f;
            var gridSize = (int)(terrainRadius * 2 / terrainStep) + 1;
            var heights = new float[gridSize, gridSize];
            var ox = skelWorldPos.X - terrainRadius;
            var oz = skelWorldPos.Z - terrainRadius;

            for (int gz = 0; gz < gridSize; gz++)
            for (int gx = 0; gx < gridSize; gx++)
            {
                var wx = ox + gx * terrainStep;
                var wz = oz + gz * terrainStep;
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(wx, skelWorldPos.Y + 2.0f, wz),
                        new Vector3(0, -1, 0), out var gridHit, 50f))
                    heights[gx, gz] = gridHit.Point.Y;
                else
                    heights[gx, gz] = groundY;
            }

            var triCount = (gridSize - 1) * (gridSize - 1) * 2;
            bufferPool.Take<Triangle>(triCount, out var triangles);
            int ti = 0;
            for (int gz = 0; gz < gridSize - 1; gz++)
            for (int gx = 0; gx < gridSize - 1; gx++)
            {
                var x0 = ox + gx * terrainStep;
                var x1 = x0 + terrainStep;
                var z0 = oz + gz * terrainStep;
                var z1 = z0 + terrainStep;
                var v00 = new Vector3(x0, heights[gx, gz], z0);
                var v10 = new Vector3(x1, heights[gx + 1, gz], z0);
                var v01 = new Vector3(x0, heights[gx, gz + 1], z1);
                var v11 = new Vector3(x1, heights[gx + 1, gz + 1], z1);

                // CW winding from above → front face points UP for collision from above
                triangles[ti++] = new Triangle(v00, v10, v01);
                triangles[ti++] = new Triangle(v10, v11, v01);
            }

            var terrainMesh = new BepuPhysics.Collidables.Mesh(triangles, Vector3.One, bufferPool);
            var terrainIndex = simulation.Shapes.Add(terrainMesh);
            simulation.Statics.Add(new StaticDescription(
                Vector3.Zero, Quaternion.Identity, terrainIndex));
            log.Info($"RagdollController: Terrain mesh {gridSize}x{gridSize} ({triCount} tris) + safety box at Y={groundY - 2f:F3}");
        }

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
                capsule.ComputeInertia(def.Mass),
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

        var jointSpring = new SpringSettings(30, 1);
        var naturalPose = config.RagdollNaturalPose;
        var limitSpring = new SpringSettings(naturalPose ? 10 : 15, 1);
        var servoSpring = new SpringSettings(2f, 1f); // gentle rest-pose bias
        var servoStrength = config.RagdollServoStrength;
        var motorDamping = 0.01f;

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

                    var effectiveSwingLimit = boneDef.SwingLimit;

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

                    // Min = near zero (block hyperextension). Max = allow full flexion.
                    // At init: angle ≈ initAngle. Flexion moves toward 0 (allowed).
                    // Hyperextension moves beyond initAngle (blocked by Max).
                    var twistMin = naturalPose ? -0.03f : -0.1f;
                    var twistMax = initAngle + 0.15f;
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = twistBasisLocalChild,
                            LocalBasisB = twistBasisLocalParent,
                            MinimumAngle = twistMin,
                            MaximumAngle = twistMax,
                            SpringSettings = limitSpring,
                        });

                    log.Info($"[Ragdoll Constraint] '{rb.Name}' TwistLimit: initAngle={initAngle:F2}rad min={twistMin:F2} max={twistMax:F2}");
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

                    // Reduce hip cone when naturalPose is active (1.3→1.0 rad)
                    var effectiveSwing = boneDef.SwingLimit;
                    bool isHip = rb.Name is "j_asi_a_l" or "j_asi_a_r";
                    if (naturalPose && isHip)
                        effectiveSwing = MathF.Min(effectiveSwing, 1.0f);

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = axisChildLocal,
                            AxisLocalB = axisParentLocal,
                            MaximumSwingAngle = effectiveSwing,
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

            // --- Angular constraint: servo (rest pose targeting) or motor (velocity damping) ---
            if (naturalPose && servoStrength > 0)
            {
                // AngularServo: targets the init relative rotation (death pose) with weak springs.
                // This gently guides joints toward a natural resting position without fighting
                // gravity during the active fall. Per-joint MaxForce scales by body role:
                // spine/hip need more force (support more mass), extremities need less.
                bool isSpine = rb.Name is "j_sebo_a" or "j_sebo_b" or "j_sebo_c" or "j_kubi" or "j_kao";
                bool isHipBone = rb.Name is "j_asi_a_l" or "j_asi_a_r";
                bool isKnee = rb.Name is "j_asi_b_l" or "j_asi_b_r";
                bool isElbow = rb.Name is "j_ude_b_l" or "j_ude_b_r";

                float baseForce = isSpine ? 20f : isHipBone ? 12f : isKnee ? 8f : isElbow ? 6f : 4f;
                float maxForce = baseForce * servoStrength;

                // Capture the current relative rotation as the rest pose target
                var restRelativeRotation = Quaternion.Normalize(
                    Quaternion.Inverse(parentBodyRef.Pose.Orientation) * childBodyRef.Pose.Orientation);

                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularServo
                    {
                        TargetRelativeRotationLocalA = restRelativeRotation,
                        ServoSettings = new ServoSettings(float.MaxValue, 0f, maxForce),
                        SpringSettings = servoSpring,
                    });
            }
            else
            {
                // Fallback: AngularMotor (velocity damping only, no pose targeting)
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularMotor
                    {
                        TargetVelocityLocalA = Vector3.Zero,
                        Settings = new MotorSettings(float.MaxValue, motorDamping),
                    });
            }
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
        kaoBodyBoneIndex = boneService.ResolveBoneIndex(skel, "j_kao");
        log.Info($"RagdollController: n_hara index={nHaraIndex}, j_kao index={kaoBodyBoneIndex}");

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

        // Initialize weapon drop physics
        weaponMainHandBody = null;
        weaponOffHandBody = null;
        weaponMainHandBoneIndex = -1;
        weaponOffHandBoneIndex = -1;
        if (config.RagdollWeaponDrop)
            InitializeWeaponDrop(skel, pose);

        // Initialize hair physics
        if (config.RagdollHairPhysics && kaoBodyBoneIndex >= 0)
        {
            hairPhysics = new HairPhysicsSimulator(config, log);
            hairPhysics.Initialize(skel.CharBase, kaoBodyBoneIndex);
        }

        return ragdollBones.Count > 0;
    }

    private void InitializeWeaponDrop(SkeletonAccess skel, FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        if (simulation == null) return;

        const float weaponRadius = 0.025f;
        const float weaponHalfLength = 0.4f;
        const float weaponMass = 1.5f;
        var weaponShape = new Capsule(weaponRadius, weaponHalfLength * 2f);
        var weaponShapeIndex = simulation.Shapes.Add(weaponShape);
        var weaponInertia = weaponShape.ComputeInertia(weaponMass);

        weaponMainHandBody = TryCreateWeaponBody(skel, pose, WeaponMainHandBones, "j_te_r",
            weaponShapeIndex, weaponInertia, weaponHalfLength,
            out weaponMainHandBoneIndex, out weaponMainHandCapsuleToBone, out weaponMainHandSegHalf);

        weaponOffHandBody = TryCreateWeaponBody(skel, pose, WeaponOffHandBones, "j_te_l",
            weaponShapeIndex, weaponInertia, weaponHalfLength,
            out weaponOffHandBoneIndex, out weaponOffHandCapsuleToBone, out weaponOffHandSegHalf);

        var count = (weaponMainHandBody.HasValue ? 1 : 0) + (weaponOffHandBody.HasValue ? 1 : 0);
        if (count > 0)
        {
            ForceWeaponVisible();
            log.Info($"RagdollController: Weapon drop initialized — {count} weapon(s)");
        }
    }

    private BodyHandle? TryCreateWeaponBody(SkeletonAccess skel, FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose,
        string[] boneCandidates, string handBoneName, TypedIndex shapeIndex, BodyInertia inertia, float segHalf,
        out int boneIndex, out Quaternion capsuleToBone, out float segHalfOut)
    {
        boneIndex = -1;
        capsuleToBone = Quaternion.Identity;
        segHalfOut = segHalf;

        foreach (var name in boneCandidates)
        {
            var idx = boneService.ResolveBoneIndex(skel, name);
            if (idx >= 0) { boneIndex = idx; break; }
        }
        if (boneIndex < 0) return null;

        ref var mt = ref pose->ModelPose.Data[boneIndex];
        var boneWorldPos = ModelToWorld(new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z));
        var boneWorldRot = ModelRotToWorld(new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
        capsuleToBone = Quaternion.Normalize(Quaternion.Inverse(boneWorldRot) * boneWorldRot);

        var initVelocity = new BodyVelocity();
        foreach (var rb in ragdollBones)
            if (rb.Name == handBoneName)
            {
                var h = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
                initVelocity.Linear = h.Velocity.Linear;
                initVelocity.Angular = h.Velocity.Angular;
                break;
            }

        var rng = new Random();
        initVelocity.Linear += new Vector3((float)(rng.NextDouble()-0.5)*0.5f, (float)rng.NextDouble()*0.3f, (float)(rng.NextDouble()-0.5)*0.5f);
        initVelocity.Angular += new Vector3((float)(rng.NextDouble()-0.5)*4f, (float)(rng.NextDouble()-0.5)*2f, (float)(rng.NextDouble()-0.5)*4f);

        var handle = simulation!.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(boneWorldPos, boneWorldRot), initVelocity, inertia,
            new CollidableDescription(shapeIndex, 0.04f), new BodyActivityDescription(0.01f)));

        log.Info($"RagdollController: Weapon body created at ({boneWorldPos.X:F2},{boneWorldPos.Y:F2},{boneWorldPos.Z:F2})");
        return handle;
    }

    private void WriteWeaponBoneTransforms(SkeletonAccess skel, BoneModificationResult result)
    {
        if (simulation == null) return;
        bool hasWeapon = false;

        if (weaponMainHandBody.HasValue && weaponMainHandBoneIndex >= 0)
        {
            WriteWeaponBone(skel, result, weaponMainHandBody.Value, weaponMainHandBoneIndex, weaponMainHandCapsuleToBone);
            hasWeapon = true;
        }
        if (weaponOffHandBody.HasValue && weaponOffHandBoneIndex >= 0)
        {
            WriteWeaponBone(skel, result, weaponOffHandBody.Value, weaponOffHandBoneIndex, weaponOffHandCapsuleToBone);
            hasWeapon = true;
        }
        if (hasWeapon) ForceWeaponVisible();
    }

    private void ForceWeaponVisible()
    {
        if (targetCharacterAddress == nint.Zero) return;
        try
        {
            var character = (Character*)targetCharacterAddress;
            character->DrawData.HideWeapons(false);
            character->Timeline.IsWeaponDrawn = true;
        }
        catch { }
    }

    private void WriteWeaponBone(SkeletonAccess skel, BoneModificationResult result,
        BodyHandle bodyHandle, int boneIdx, Quaternion capsuleToBone)
    {
        var bodyRef = simulation!.Bodies.GetBodyReference(bodyHandle);
        var boneWorldRot = Quaternion.Normalize(bodyRef.Pose.Orientation * capsuleToBone);
        boneService.WriteBoneTransform(skel, boneIdx,
            WorldToModel(bodyRef.Pose.Position), WorldRotToModel(boneWorldRot), result);
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
        var BoneDefs = GetBoneDefs();

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
                if (config.RagdollVerboseLog)
                    log.Info($"[Ragdoll F{frameCount}] Skeleton moved {skelDist:F3}m: ({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3})→({newSkelPos.X:F3},{newSkelPos.Y:F3},{newSkelPos.Z:F3})");
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(newSkelPos.X, newSkelPos.Y + 2.0f, newSkelPos.Z),
                        new Vector3(0, -1, 0),
                        out var hitInfo,
                        50f))
                {
                    realGroundY = hitInfo.Point.Y;
                    groundY = realGroundY;
                    if (config.RagdollVerboseLog)
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
        var logThisFrame = config.RagdollVerboseLog && (frameCount <= 3 || frameCount % 12 == 0);

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

        // Write weapon physics body transforms (after descendant propagation)
        WriteWeaponBoneTransforms(skel, result);

        // Propagate j_kao changes to face/hair partial skeletons
        if (kaoBodyBoneIndex >= 0 && result.HasAccumulated[kaoBodyBoneIndex])
        {
            boneService.PropagateToPartialSkeletons(skel, kaoBodyBoneIndex, "j_kao", result);
        }

        // Apply hair physics (after rigid j_kao propagation)
        if (hairPhysics != null && kaoBodyBoneIndex >= 0)
        {
            hairPhysics.StepAndApply(
                skel.CharBase, kaoBodyBoneIndex,
                skelWorldPos, skelWorldRot, skelWorldRotInv,
                1.0f / 60.0f);
        }
    }

    // --- Grab constraint API (for cinematic victory sequence) ---
    private ConstraintHandle grabConstraintHandle;
    private bool grabConstraintActive;
    private BodyHandle grabBodyHandle;

    /// <summary>
    /// Create a OneBodyLinearServo constraint that pins a ragdoll bone to a world-space
    /// target position. The target is updated each frame via UpdateGrabTarget().
    /// Also ensures all ragdoll bodies stay awake (SleepThreshold = -1).
    /// </summary>
    public bool CreateGrabConstraint(string boneName, Vector3 initialTarget, float maxForce = 1000f, float maxSpeed = 50f, float springFreq = 120f)
    {
        if (simulation == null || !isActive) return false;

        // Find the body for this bone
        BodyHandle? targetBody = null;
        foreach (var rb in ragdollBones)
        {
            if (rb.Name == boneName)
            {
                targetBody = rb.BodyHandle;
                break;
            }
        }
        if (targetBody == null)
        {
            log.Warning($"RagdollController: Grab bone '{boneName}' not found in ragdoll");
            return false;
        }

        grabBodyHandle = targetBody.Value;

        // Ensure all bodies stay awake so the grab constraint is always active
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
            bodyRef.Activity.SleepThreshold = -1f;
        }

        // Create OneBodyLinearServo: pins a body to a world-space target
        grabConstraintHandle = simulation.Solver.Add(grabBodyHandle,
            new OneBodyLinearServo
            {
                LocalOffset = Vector3.Zero,
                Target = initialTarget,
                ServoSettings = new ServoSettings(maxSpeed, 1f, maxForce),
                SpringSettings = new SpringSettings(springFreq, 1),
            });

        grabConstraintActive = true;
        log.Info($"RagdollController: Grab constraint created on '{boneName}' → ({initialTarget.X:F2},{initialTarget.Y:F2},{initialTarget.Z:F2})");
        return true;
    }

    /// <summary>
    /// Update the grab constraint's target position (call each frame with NPC hand world pos).
    /// </summary>
    public void UpdateGrabTarget(Vector3 worldTarget)
    {
        if (!grabConstraintActive || simulation == null) return;

        try
        {
            var desc = new OneBodyLinearServo
            {
                LocalOffset = Vector3.Zero,
                Target = worldTarget,
                ServoSettings = new ServoSettings(50f, 1f, 1000f),
                SpringSettings = new SpringSettings(120, 1),
            };
            simulation.Solver.ApplyDescription(grabConstraintHandle, desc);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: Failed to update grab target");
            RemoveGrabConstraint();
        }
    }

    /// <summary>
    /// Remove the grab constraint and restore normal sleep thresholds.
    /// </summary>
    public void RemoveGrabConstraint()
    {
        if (!grabConstraintActive || simulation == null) return;

        try
        {
            simulation.Solver.Remove(grabConstraintHandle);
        }
        catch { }

        // Restore normal sleep threshold (unless settle collision wants them awake)
        var normalThreshold = (config.RagdollNpcSettleCollision && config.RagdollNpcCollision) ? -1f : 0.01f;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            try
            {
                var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
                bodyRef.Activity.SleepThreshold = normalThreshold;
            }
            catch { }
        }

        grabConstraintActive = false;
        log.Info("RagdollController: Grab constraint removed");
    }

    private void DestroySimulation()
    {
        grabConstraintActive = false;
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
