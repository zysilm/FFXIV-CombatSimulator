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
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

public unsafe class RagdollController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly Npcs.NpcSelector? npcSelector;
    private readonly Safety.MovementBlockHook? movementBlockHook;
    // Evaluated lazily when collision is built (at Activate), not at construction —
    // the player's persistent controller is created before any companions spawn, so
    // a snapshot would always be empty. A provider lets the player ragdoll pick up
    // the live party members.
    private readonly Func<IReadOnlyList<nint>>? extraCollisionProvider;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Physics simulation
    private BufferPool? bufferPool;
    private BepuSimulation? simulation;

    // State
    private bool isActive;
    private nint targetCharacterAddress;
    // EntityId captured at Activate — used every frame to detect that the target despawned
    // and its object-table slot was reused by an unrelated character (so we don't freeze
    // and write ragdoll bones onto the newcomer).
    private uint targetEntityId;
    private Vector3 savedCharacterPosition; // original position before follow moved it
    private bool followWasActive;           // tracks toggle-off to restore position
    private float elapsed;
    private float activationDelay;
    private bool physicsStarted;
    // Skeleton bone count captured at InitializePhysics. If it changes mid-ragdoll the draw
    // object was rebuilt and our bone indices are stale → deactivate.
    private int initialBoneCount;
    // True once Timeline.OverallSpeed has been zeroed (animation frozen). Gates the restore
    // in Deactivate. NOT the same as physicsStarted: the freeze happens inside
    // InitializePhysics, which can still fail or throw afterwards (e.g. zero qualifying
    // bones), and the restore must run on that path too or the character stays frozen forever.
    private bool animationFrozen;

    // Frame-rate-independent physics timing. The render hook fires once per rendered
    // frame at whatever framerate the game runs (30/60/120/144…). Stepping a fixed
    // 1/60 every call made the ragdoll run in slow-motion below 60fps and fast above it.
    // We measure real wall-clock dt and advance the simulation in fixed-size substeps.
    private long lastFrameTimestamp;
    private float physicsAccumulator;
    private const float FixedTimestep = 1f / 60f;
    private const int MaxSubstepsPerFrame = 4;  // cap catch-up to avoid a spiral of death
    private const float MaxFrameDelta = 0.1f;   // ignore huge gaps (loading screens, hitches)
    private const float BiomechanicalSettleDuration = 1.25f;

    // True once every ragdoll body has gone to sleep (settled). When resting we skip the
    // physics step, the per-frame NPC-collision static rebuild, and hair integration —
    // only re-asserting the held pose. Bodies can only sleep when settle-collision is off,
    // so a resting corpse has nothing left to react to. Reset whenever the rig is rebuilt.
    private bool prevAllAsleep;
    // Short post-wake window where passive anatomical constraints are allowed to solve even
    // if the ragdoll had already gone to sleep. This prevents grab/release from preserving a
    // contact-supported kneel or bent-elbow pose as a permanent "resting" posture.
    private float biomechanicalSettleRemaining;

    // Skeleton world transform (captured at activation from Skeleton.Transform)
    // ModelPose is in skeleton-local space; these convert to/from world space.
    private Vector3 skelWorldPos;
    private Quaternion skelWorldRot;
    private Quaternion skelWorldRotInv;

    // Bone-to-body mapping
    private readonly List<RagdollBone> ragdollBones = new();

    // Bone defs actually used for the active ragdoll, keyed by name. For humanoid
    // skeletons these are the tuned human defs; for non-humanoid skeletons they are
    // generated from the real skeleton topology (see BuildGenericSkeletonDefs).
    // StepAndApply reads capsule extents from here instead of re-deriving the human set.
    private readonly Dictionary<string, RagdollBoneDef> activeDefByName = new();

    // True while the active ragdoll was built by the generic (non-humanoid) path.
    // Generic rigs get extra stabilization (more solver iterations + velocity clamp)
    // because their auto-generated constraint network is stiffer/less conditioned
    // than the hand-tuned human profile.
    private bool activeRagdollIsGeneric;

    // Generic (non-humanoid) ragdoll generation tuning.
    // The min-segment threshold is adaptive to skeleton size (see BuildGenericSkeletonDefs):
    // large rigs (toads) use the cap so we don't simulate a dense cluster of tiny bodies;
    // small rigs (bats) use the floor so their small bones (wings/limbs) still get bodies and
    // the ragdoll actually articulates instead of falling as one rigid clump.
    private const float GenericMinSegmentCap = 0.08f;    // upper bound for big skeletons
    private const float GenericMinSegmentFloor = 0.02f;  // lower bound for small skeletons (bats)
    private const float GenericMinSegmentFactor = 0.06f; // fraction of the skeleton's largest segment
    private const int GenericMaxBodies = 40;             // cap solver load on large rigs
    private const float GenericSwingLimit = 0.6f;        // ball cone half-angle (rad)
    private const float GenericTwistLimit = 0.35f;       // ball axial twist (rad)
    private const int GenericSolverIterations = 16;      // generic rigs need more iterations to converge
    private const float GenericMaxLinearVelocity = 12f;  // m/s — clamp per frame to stop energy blow-up
    private const float GenericMaxAngularVelocity = 16f; // rad/s — clamp per frame (tiny bodies spin up fastest)

    // Human/player rigs are hand-tuned and normally stable, but their constraint solver can
    // still occasionally diverge. Keep the ceiling above normal ragdoll motion so it only
    // catches runaway energy, not ordinary falls or cinematic drags.
    private const float HumanMaxLinearVelocity = 18f;
    private const float HumanMaxAngularVelocity = 14f;
    private const float TerrainPatchRadius = 6.0f;
    private const float TerrainPatchStep = 1.0f;
    private const float TerrainRaycastStartYOffset = 10.0f;
    private const float TerrainRaycastDistance = 80.0f;
    private const float TerrainPatchRefreshDistance = 4.5f;
    private const int MaxTerrainPatches = 32;
    private const float SafetyGroundDrop = 0.75f;

    // Ground height (physics ground may be lowered by floor offset)
    private float groundY;
    // Real terrain ground (before floor offset), used for visual correction
    private float realGroundY;
    private readonly List<Vector2> terrainPatchCenters = new();

    // Diagnostic frame counter
    private int frameCount;

    // Reused per-frame buffers for StepAndApply (avoid per-frame GC pressure)
    private BoneModificationResult? cachedResult;
    private Vector3[]? cachedWorldPositions;
    private Quaternion[]? cachedWorldRotations;
    private bool[]? cachedBoneValid;

    // Render interpolation: physics advances in fixed 60Hz ticks but we render every frame
    // (often >60fps). Without interpolation the ragdoll shows the same pose for several
    // frames then jumps on each tick — visible micro-judder. We snapshot the bone world
    // pose just before each physics tick (prev) and after (cur, in cachedWorld*), then blend
    // by the leftover-accumulator fraction so the rendered pose advances smoothly every frame.
    private Vector3[]? prevWorldPositions;
    private Quaternion[]? prevWorldRotations;
    private bool hasPrevPhysicsState;
    // Animation freeze state
    private float savedOverallSpeed = 1.0f;
    private readonly HashSet<int> ragdollBoneIndices = new();

    // Weapon drop physics moved to WeaponDropController (separate, plugin-scoped simulation).

    // Ancestor bone index — n_hara must follow j_kosi to prevent mesh tearing
    private int nHaraIndex = -1;
    // Head bone index (j_kao) — used for hair physics and partial skeleton propagation
    private int kaoBodyBoneIndex = -1;
    // Hair physics simulator
    private HairPhysicsSimulator? hairPhysics;

    // NPC bone collision — per-bone static capsules for active targets
    private readonly List<NpcCollisionState> npcCollisionStates = new();
    private TypedIndex npcFallbackShapeIndex;   // single-capsule fallback shape
    private bool npcFallbackShapeReady;         // whether npcFallbackShapeIndex has been created this sim
    // Monster strike: during the attack window the monster's collider bones impart their swing
    // velocity as an impulse to nearby ragdoll bodies (a fast swing = a heavy hit, at the limb's
    // real contact point).
    private float attackStrikeTimer;
    private float strikePower;
    private nint strikeColliderAddress;
    // Weapon strike: a humanoid's weapon is a separate draw object (not in the bone capsules), so
    // track its blade endpoints to give a sword swing its own velocity-driven hit.
    private Vector3 prevBladeA, prevBladeB;
    private bool weaponPrevValid;
    // Each body is struck at most once per swing — the window spans many frames, so striking every
    // frame accumulated into a launch. One hit per swing makes strike power linear/predictable.
    private readonly HashSet<int> struckThisWindow = new();


    public bool IsActive => isActive;
    public bool IsSimulationReady => isActive && simulation != null;
    public nint TargetCharacterAddress => targetCharacterAddress;

    /// <summary>Debug draw data for a single ragdoll capsule body.</summary>
    public struct DebugCapsule
    {
        public Vector3 Position;      // capsule center (world space)
        public Quaternion Orientation; // capsule rotation
        public float Radius;
        public float HalfLength;      // half of the segment length (capsule extends along Y)
        public RagdollColliderShape ColliderShape;
        public Vector3 BoxHalfExtents;
        public string Name;
        public JointType Joint;
        public float SwingLimit;
    }

    /// <summary>Debug data for visualizing joint rotation limits.</summary>
    public struct DebugJointVis
    {
        public bool Valid;
        public Vector3 JointPosition;    // world-space joint point (where parent meets child)
        public Vector3 ParentAxis;       // parent bone segment direction (world)
        public Vector3 ChildAxis;        // child bone segment direction (world)
        public Vector3 ParentRight;      // perpendicular axis for drawing arcs
        public Vector3 ParentForward;    // perpendicular axis for drawing arcs
        public JointType Joint;
        public float SwingLimit;
        public float TwistMinAngle;
        public float TwistMaxAngle;
    }

    /// <summary>
    /// Get joint visualization data for a specific bone (computed from live physics bodies).
    /// Returns Invalid if bone not found or ragdoll not active.
    /// </summary>
    public DebugJointVis GetDebugJointVis(string boneName)
    {
        var result = new DebugJointVis { Valid = false };
        if (!isActive || simulation == null) return result;

        // Find the bone and its parent
        RagdollBone? childBone = null;
        int childIndex = -1;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            if (ragdollBones[i].Name == boneName)
            {
                childBone = ragdollBones[i];
                childIndex = i;
                break;
            }
        }
        if (childBone == null || childBone.Value.ParentBoneIndex < 0) return result;

        // Find parent ragdoll bone
        RagdollBone? parentBone = null;
        foreach (var rb in ragdollBones)
        {
            if (rb.BoneIndex == childBone.Value.ParentBoneIndex)
            {
                parentBone = rb;
                break;
            }
        }
        if (parentBone == null) return result;

        // Find bone def for limits
        var BoneDefs = GetBoneDefs();
        RagdollBoneDef boneDef = default;
        foreach (var def in BoneDefs)
            if (def.Name == boneName) { boneDef = def; break; }

        // Read live physics body poses
        var childBody = simulation.Bodies.GetBodyReference(childBone.Value.BodyHandle);
        var parentBody = simulation.Bodies.GetBodyReference(parentBone.Value.BodyHandle);

        // Segment directions (capsule Y-axis in world space)
        var childAxis = Vector3.Transform(Vector3.UnitY, childBody.Pose.Orientation);
        var parentAxis = Vector3.Transform(Vector3.UnitY, parentBody.Pose.Orientation);

        // Joint position: bottom of child capsule (= top of parent towards child)
        var jointPos = childBody.Pose.Position - childAxis * childBone.Value.SegmentHalfLength;

        // Build perpendicular axes from parent direction (for drawing cones/arcs)
        var up = MathF.Abs(Vector3.Dot(parentAxis, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var parentRight = Vector3.Normalize(Vector3.Cross(parentAxis, up));
        var parentForward = Vector3.Normalize(Vector3.Cross(parentRight, parentAxis));

        result.Valid = true;
        result.JointPosition = jointPos;
        result.ParentAxis = parentAxis;
        result.ChildAxis = childAxis;
        result.ParentRight = parentRight;
        result.ParentForward = parentForward;
        result.Joint = boneDef.Joint;
        result.SwingLimit = boneDef.SwingLimit;
        result.TwistMinAngle = boneDef.TwistMinAngle;
        result.TwistMaxAngle = boneDef.TwistMaxAngle;
        return result;
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
                HalfLength = ResolveBodyHalfLength(boneDef),
                ColliderShape = boneDef.ColliderShape,
                BoxHalfExtents = ResolveBoxHalfExtents(boneDef, ResolveBodyHalfLength(boneDef)),
                Name = rb.Name,
                Joint = boneDef.Joint,
                SwingLimit = boneDef.SwingLimit,
            });
        }
        return result;
    }

    /// <summary>
    /// Get capsule positions from the live skeleton pose (for overlay when ragdoll is OFF).
    /// Reads bone world positions from the character's current animation pose.
    /// </summary>
    public List<DebugCapsule> GetDebugCapsulesFromSkeleton(nint characterAddress)
    {
        var result = new List<DebugCapsule>();
        var skelNullable = boneService.TryGetSkeleton(characterAddress);
        if (skelNullable == null) return result;
        var skel = skelNullable.Value;

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return result;
        var skelPos = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        var skelRot = new Quaternion(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y, skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);

        var BoneDefs = GetBoneDefs();
        var pose = skel.Pose;

        foreach (var def in BoneDefs)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || idx >= skel.BoneCount) continue;

            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);

            // Convert to world space
            var worldPos = skelPos + Vector3.Transform(modelPos, skelRot);
            var worldRot = Quaternion.Normalize(skelRot * modelRot);

            result.Add(new DebugCapsule
            {
                Position = worldPos,
                Orientation = worldRot,
                Radius = def.CapsuleRadius,
                HalfLength = ResolveBodyHalfLength(def),
                ColliderShape = def.ColliderShape,
                BoxHalfExtents = ResolveBoxHalfExtents(def, ResolveBodyHalfLength(def)),
                Name = def.Name,
                Joint = def.Joint,
                SwingLimit = def.SwingLimit,
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

    // Structural bones every humanoid skeleton has. If all resolve, the hand-tuned
    // human profile fits and the existing passes run unchanged. If any is missing
    // (bats, birds, dragons, quadrupeds, voidsent) the skeleton is treated as
    // non-humanoid and gets a generated ragdoll instead.
    private static readonly string[] HumanoidSignatureBones =
    {
        "j_kosi", "j_sebo_a", "j_kubi",
        "j_ude_a_l", "j_ude_a_r",
        "j_asi_a_l", "j_asi_a_r",
    };

    private bool IsHumanoidSkeleton(Dictionary<string, int> humanNameToIndex)
    {
        foreach (var bone in HumanoidSignatureBones)
            if (!humanNameToIndex.ContainsKey(bone))
                return false;
        return true;
    }

    private bool IsNpcHumanoidSkeleton(SkeletonAccess skel)
    {
        foreach (var bone in HumanoidSignatureBones)
            if (boneService.ResolveBoneIndex(skel, bone) < 0)
                return false;
        return true;
    }

    /// <summary>
    /// Build a ragdoll bone definition set from a skeleton's real topology, for
    /// non-humanoid characters where the human profile does not fit. Capsule size
    /// and mass scale with actual bone lengths (so small rigs like bats do not get
    /// oversized human capsules that explode), parents follow the real skeleton
    /// hierarchy (no human-name mapping, so no orphaning), and all joints are loose
    /// ball joints. The returned defs feed the same Pass 1/2/3 body/constraint build
    /// and StepAndApply write-back as the human path.
    /// </summary>
    private (RagdollBoneDef[] Defs, Dictionary<string, int> NameToIndex) BuildGenericSkeletonDefs(SkeletonAccess skel)
    {
        var pose = skel.Pose;
        int n = skel.BoneCount;
        int pc = skel.ParentCount;

        // World position of every bone in the current (death) pose.
        var wpos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            ref var mt = ref pose->ModelPose.Data[i];
            wpos[i] = ModelToWorld(new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z));
        }

        int ParentOf(int i) => (i >= 0 && i < pc) ? skel.HavokSkeleton->ParentIndices[i] : -1;

        // Distance to direct parent, and the longest child segment a bone owns.
        var distToParent = new float[n];
        var longestChildLen = new float[n];
        for (int i = 0; i < n; i++)
        {
            int p = ParentOf(i);
            if (p < 0 || p >= n) continue;
            var d = Vector3.Distance(wpos[i], wpos[p]);
            distToParent[i] = d;
            if (d > longestChildLen[p]) longestChildLen[p] = d;
        }

        // Adaptive min-segment threshold scaled to the skeleton's own size: a fraction of
        // its largest segment, clamped to [floor, cap]. Big rigs (toad, longest segment
        // ~1.7m) land at the cap and stay sparse; small rigs (bat, ~0.2m) drop to the floor
        // so their wing/limb bones are simulated instead of being skipped as "twigs".
        float maxSegment = 0f;
        for (int i = 0; i < n; i++)
            if (longestChildLen[i] > maxSegment) maxSegment = longestChildLen[i];
        float minSegment = Math.Clamp(maxSegment * GenericMinSegmentFactor, GenericMinSegmentFloor, GenericMinSegmentCap);
        log.Info($"RagdollController: generic min-segment threshold {minSegment:F3}m (skeleton largest segment {maxSegment:F3}m)");

        // A bone gets a body if it owns a real forward segment. Coincident/twig bones
        // (fingers, tips) are skipped and follow via StepAndApply propagation.
        var significant = new bool[n];
        int significantCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (longestChildLen[i] >= minSegment)
            {
                significant[i] = true;
                significantCount++;
            }
        }

        // Cap body count on large rigs: keep the longest segments, but force-keep the
        // significant ancestors of kept bones so the constraint tree stays connected
        // (a kept bone whose ancestors were dropped would become a free-floating root).
        if (significantCount > GenericMaxBodies)
        {
            var ranked = new List<int>();
            for (int i = 0; i < n; i++)
                if (significant[i]) ranked.Add(i);
            ranked.Sort((a, b) => longestChildLen[b].CompareTo(longestChildLen[a]));

            var keep = new bool[n];
            for (int k = 0; k < GenericMaxBodies && k < ranked.Count; k++)
            {
                keep[ranked[k]] = true;
                int p = ParentOf(ranked[k]);
                while (p >= 0)
                {
                    if (significant[p]) keep[p] = true;
                    p = ParentOf(p);
                }
            }
            for (int i = 0; i < n; i++)
                significant[i] = significant[i] && keep[i];
        }

        int SignificantAncestor(int i)
        {
            int p = ParentOf(i);
            while (p >= 0)
            {
                if (significant[p]) return p;
                p = ParentOf(p);
            }
            return -1;
        }

        string Name(int i) => "gen_" + i.ToString();

        var simIndices = new List<int>();
        for (int i = 0; i < n; i++)
            if (significant[i]) simIndices.Add(i);

        // Emit roots first, then longest-from-parent first, so boneToFirstChild picks
        // each bone's longest child as its capsule axis (best rotational stability).
        simIndices.Sort((a, b) =>
        {
            bool aRoot = SignificantAncestor(a) < 0;
            bool bRoot = SignificantAncestor(b) < 0;
            if (aRoot != bRoot) return aRoot ? -1 : 1;
            return distToParent[b].CompareTo(distToParent[a]);
        });

        var nameToIndex = new Dictionary<string, int>();
        foreach (var i in simIndices)
            nameToIndex[Name(i)] = i;

        var defs = new List<RagdollBoneDef>(simIndices.Count);
        foreach (var i in simIndices)
        {
            float segLen = MathF.Max(longestChildLen[i], minSegment);
            int anc = SignificantAncestor(i);
            defs.Add(new RagdollBoneDef
            {
                Name = Name(i),
                ParentName = anc >= 0 ? Name(anc) : null,
                CapsuleRadius = Math.Clamp(segLen * 0.28f, 0.02f, 0.18f),
                CapsuleHalfLength = segLen * 0.45f,
                Mass = Math.Clamp(segLen * 20f, 0.5f, 12f),
                SwingLimit = GenericSwingLimit,
                Joint = JointType.Ball,
                TwistMinAngle = -GenericTwistLimit,
                TwistMaxAngle = GenericTwistLimit,
                AnatomicalRole = AnatomicalRole.Generic,
                ColliderShape = RagdollColliderShape.Capsule,
                BoxHalfExtents = new Vector3(Math.Clamp(segLen * 0.28f, 0.02f, 0.18f), segLen * 0.45f, Math.Clamp(segLen * 0.28f, 0.02f, 0.18f)),
            });
        }

        return (defs.ToArray(), nameToIndex);
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
    public enum AnatomicalRole { Auto, Generic, Pelvis, Spine, Head, Shoulder, Elbow, Hand, Hip, Knee, Ankle, Foot, Cloth, SoftBody, Weapon }
    public enum RagdollColliderShape { Capsule, Box }

    // Ragdoll bone definition
    public struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit;
        public float SwingMinLimit;
        public float HingeRestAngle;
        public float HingeRestSpringFreq;
        public float HingeRestMaxForce;
        public JointType Joint;
        public float TwistMinAngle;
        public float TwistMaxAngle;
        public AnatomicalRole AnatomicalRole;
        public RagdollColliderShape ColliderShape;
        public Vector3 BoxHalfExtents;
        public bool SoftBody;          // use soft springs + AngularServo (for breast/jiggle)
        public float SoftSpringFreq;   // BallSocket frequency (Hz)
        public float SoftSpringDamp;   // BallSocket damping ratio
        public float SoftServoFreq;    // AngularServo frequency (Hz)
        public float SoftServoDamp;    // AngularServo damping ratio
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
        new RagdollBoneConfig { Name = "j_kosi",    SkeletonParent = null,       Enabled = true,  CapsuleRadius = 0.105f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Pelvis" },
        new RagdollBoneConfig { Name = "j_sebo_a",  SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 10.0f, SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Lower Spine" },
        new RagdollBoneConfig { Name = "j_sebo_b",  SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Mid Spine" },
        new RagdollBoneConfig { Name = "j_sebo_c",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 6.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Chest" },
        new RagdollBoneConfig { Name = "j_kubi",    SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.25f,               JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Neck" },
        new RagdollBoneConfig { Name = "j_kao",     SkeletonParent = "j_kubi",   Enabled = true,  CapsuleRadius = 0.08f,  CapsuleHalfLength = 0.04f, Mass = 3.5f,  SwingLimit = 0.25f,               JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Head" },

        // === CLOTH/SKIRT BONES === (chained per radial slot: A→j_sebo_a, B→matching A, C→matching B; b=back, f=front, s=side)
        new RagdollBoneConfig { Name = "j_sk_b_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A L" },
        new RagdollBoneConfig { Name = "j_sk_b_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A R" },
        new RagdollBoneConfig { Name = "j_sk_f_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A L" },
        new RagdollBoneConfig { Name = "j_sk_f_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A R" },
        new RagdollBoneConfig { Name = "j_sk_s_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A L" },
        new RagdollBoneConfig { Name = "j_sk_s_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A R" },
        new RagdollBoneConfig { Name = "j_sk_b_b_l", SkeletonParent = "j_sk_b_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B L" },
        new RagdollBoneConfig { Name = "j_sk_b_b_r", SkeletonParent = "j_sk_b_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B R" },
        new RagdollBoneConfig { Name = "j_sk_f_b_l", SkeletonParent = "j_sk_f_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B L" },
        new RagdollBoneConfig { Name = "j_sk_f_b_r", SkeletonParent = "j_sk_f_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B R" },
        new RagdollBoneConfig { Name = "j_sk_s_b_l", SkeletonParent = "j_sk_s_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B L" },
        new RagdollBoneConfig { Name = "j_sk_s_b_r", SkeletonParent = "j_sk_s_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B R" },
        new RagdollBoneConfig { Name = "j_sk_b_c_l", SkeletonParent = "j_sk_b_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C L" },
        new RagdollBoneConfig { Name = "j_sk_b_c_r", SkeletonParent = "j_sk_b_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C R" },
        new RagdollBoneConfig { Name = "j_sk_f_c_l", SkeletonParent = "j_sk_f_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C L" },
        new RagdollBoneConfig { Name = "j_sk_f_c_r", SkeletonParent = "j_sk_f_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C R" },
        new RagdollBoneConfig { Name = "j_sk_s_c_l", SkeletonParent = "j_sk_s_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C L" },
        new RagdollBoneConfig { Name = "j_sk_s_c_r", SkeletonParent = "j_sk_s_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C R" },

        // === WEAPON HOLSTER/SHEATHE === (disabled by default — enable for sheathed weapon physics)
        new RagdollBoneConfig { Name = "j_buki_kosi_l",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Sheathe" },
        new RagdollBoneConfig { Name = "j_buki_kosi_r",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Sheathe" },
        new RagdollBoneConfig { Name = "j_buki2_kosi_l", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Holster" },
        new RagdollBoneConfig { Name = "j_buki2_kosi_r", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Holster" },
        new RagdollBoneConfig { Name = "j_buki_sebo_l",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Back Scabbard" },
        new RagdollBoneConfig { Name = "j_buki_sebo_r",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Back Scabbard" },

        // === BREAST === (child of j_sebo_b, disabled by default — cosmetic)
        new RagdollBoneConfig { Name = "j_mune_l",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.02f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Left Breast",  SoftBody = true, SoftSpringFreq = 1f, SoftSpringDamp = 0.05f, SoftServoFreq = 4f, SoftServoDamp = 0.35f },
        new RagdollBoneConfig { Name = "j_mune_r",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.02f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Right Breast",  SoftBody = true, SoftSpringFreq = 1f, SoftSpringDamp = 0.05f, SoftServoFreq = 4f, SoftServoDamp = 0.35f },

        // === CLAVICLE === (child of j_sebo_c, inserts between chest and arms)
        new RagdollBoneConfig { Name = "j_sako_l",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.35f,               JointType = 0, TwistMinAngle = -0.25f, TwistMaxAngle = 0.25f, Description = "Left Clavicle" },
        new RagdollBoneConfig { Name = "j_sako_r",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.35f,               JointType = 0, TwistMinAngle = -0.25f, TwistMaxAngle = 0.25f, Description = "Right Clavicle" },

        // === ARM CHAIN === (all enabled — skeleton: j_sako → j_ude_a → j_ude_b → j_te)
        new RagdollBoneConfig { Name = "j_ude_a_l", SkeletonParent = "j_sako_l", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.35f,               JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Left Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_a_r", SkeletonParent = "j_sako_r", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.35f,               JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Right Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_b_l", SkeletonParent = "j_ude_a_l",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -1.25f, TwistMaxAngle = 1.25f, Description = "Left Forearm" },
        new RagdollBoneConfig { Name = "j_ude_b_r", SkeletonParent = "j_ude_a_r",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -1.25f, TwistMaxAngle = 1.25f, Description = "Right Forearm" },
        new RagdollBoneConfig { Name = "j_te_l",    SkeletonParent = "j_ude_b_l",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Left Hand" },
        new RagdollBoneConfig { Name = "j_te_r",    SkeletonParent = "j_ude_b_r",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Right Hand" },

        // === LEG CHAIN === (j_asi_a/b/c/d enabled, j_asi_e disabled by default)
        new RagdollBoneConfig { Name = "j_asi_a_l", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Left Thigh" },
        new RagdollBoneConfig { Name = "j_asi_a_r", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Right Thigh" },
        new RagdollBoneConfig { Name = "j_asi_b_l", SkeletonParent = "j_asi_a_l",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_b_r", SkeletonParent = "j_asi_a_r",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_c_l", SkeletonParent = "j_asi_b_l",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.1f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Calf" },
        new RagdollBoneConfig { Name = "j_asi_c_r", SkeletonParent = "j_asi_b_r",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.1f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Calf" },
        new RagdollBoneConfig { Name = "j_asi_d_l", SkeletonParent = "j_asi_c_l",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.29f,               JointType = 0, TwistMinAngle = -0.64f, TwistMaxAngle = 0.65f, Description = "Left Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_d_r", SkeletonParent = "j_asi_c_r",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Right Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_e_l", SkeletonParent = "j_asi_d_l",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Toes" },
        new RagdollBoneConfig { Name = "j_asi_e_r", SkeletonParent = "j_asi_d_r",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Toes" },
    };

    /// <summary>
    /// Legacy accessor — returns only the enabled bones as RagdollBoneDef[],
    /// computing physics parents by walking up the skeleton tree to find the
    /// nearest enabled ancestor.
    /// </summary>
    public static readonly RagdollBoneDef[] DefaultBoneDefs = BuildDefaultBoneDefs();

    public static void FillProfileDefaults(RagdollBoneConfig bone)
    {
        if (bone.AnatomicalRole == (int)AnatomicalRole.Auto)
            bone.AnatomicalRole = (int)InferAnatomicalRole(bone.Name, bone.Description, bone.SoftBody);

        bone.SwingMinLimit ??= DefaultSwingMinLimit((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        bone.HingeRestAngle ??= DefaultHingeRestAngle((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        bone.HingeRestSpringFreq ??= DefaultHingeRestSpringFreq((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        bone.HingeRestMaxForce ??= DefaultHingeRestMaxForce((AnatomicalRole)bone.AnatomicalRole, bone.Name);

        var hasBoxMetadata = bone.BoxHalfExtentX > 0 || bone.BoxHalfExtentY > 0 || bone.BoxHalfExtentZ > 0;
        if (bone.ColliderShape == 0 && !hasBoxMetadata &&
            (IsHandBone(bone.Name) || IsFootBone(bone.Name) || IsShinBone(bone.Name) || IsForearmBone(bone.Name) || IsUpperArmBone(bone.Name)))
            bone.ColliderShape = (int)RagdollColliderShape.Box;

        if (bone.BoxHalfExtentX <= 0 || bone.BoxHalfExtentY <= 0 || bone.BoxHalfExtentZ <= 0)
        {
            var extents = DefaultBoxHalfExtents(bone);
            if (bone.BoxHalfExtentX <= 0) bone.BoxHalfExtentX = extents.X;
            if (bone.BoxHalfExtentY <= 0) bone.BoxHalfExtentY = extents.Y;
            if (bone.BoxHalfExtentZ <= 0) bone.BoxHalfExtentZ = extents.Z;
        }
    }

    private static AnatomicalRole InferAnatomicalRole(string name, string? description, bool softBody)
    {
        if (softBody || name.StartsWith("j_mune_", StringComparison.Ordinal)) return AnatomicalRole.SoftBody;
        if (name.StartsWith("j_sk_", StringComparison.Ordinal)) return AnatomicalRole.Cloth;
        if (name.StartsWith("j_buki", StringComparison.Ordinal)) return AnatomicalRole.Weapon;
        if (name == "j_kosi") return AnatomicalRole.Pelvis;
        if (name.StartsWith("j_sebo_", StringComparison.Ordinal) || name == "j_kubi") return AnatomicalRole.Spine;
        if (name == "j_kao") return AnatomicalRole.Head;
        if (name.StartsWith("j_sako_", StringComparison.Ordinal) || name.StartsWith("j_ude_a_", StringComparison.Ordinal)) return AnatomicalRole.Shoulder;
        if (name.StartsWith("j_ude_b_", StringComparison.Ordinal)) return AnatomicalRole.Elbow;
        if (name.StartsWith("j_te_", StringComparison.Ordinal)) return AnatomicalRole.Hand;
        if (name.StartsWith("j_asi_a_", StringComparison.Ordinal)) return AnatomicalRole.Hip;
        if (name.StartsWith("j_asi_b_", StringComparison.Ordinal)) return AnatomicalRole.Knee;
        if (name.StartsWith("j_asi_c_", StringComparison.Ordinal)) return AnatomicalRole.Ankle;
        if (name.StartsWith("j_asi_d_", StringComparison.Ordinal) || name.StartsWith("j_asi_e_", StringComparison.Ordinal)) return AnatomicalRole.Foot;
        if (!string.IsNullOrEmpty(description) && description.Contains("knee", StringComparison.OrdinalIgnoreCase)) return AnatomicalRole.Knee;
        if (!string.IsNullOrEmpty(description) && description.Contains("elbow", StringComparison.OrdinalIgnoreCase)) return AnatomicalRole.Elbow;
        return AnatomicalRole.Generic;
    }

    private static bool IsHandBone(string name) => name.StartsWith("j_te_", StringComparison.Ordinal);
    private static bool IsFootBone(string name) => name.StartsWith("j_asi_d_", StringComparison.Ordinal);
    private static bool IsShinBone(string name) => name.StartsWith("j_asi_b_", StringComparison.Ordinal);
    private static bool IsKneeOrCalfStartupLiftBone(string name)
    {
        if (IsShinBone(name))
            return true;

        return name.Contains("knee", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("kneel", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("calf", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("shin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForearmBone(string name) => name.StartsWith("j_ude_b_", StringComparison.Ordinal);
    private static bool IsUpperArmBone(string name) => name.StartsWith("j_ude_a_", StringComparison.Ordinal);
    private static bool IsClavicleBone(string name) => name.StartsWith("j_sako_", StringComparison.Ordinal);

    private static Vector3 DefaultBoxHalfExtents(RagdollBoneConfig bone)
    {
        if (IsUpperArmBone(bone.Name))
            return new Vector3(MathF.Max(0.032f, bone.CapsuleRadius * 1.15f), MathF.Max(0.075f, bone.CapsuleHalfLength), MathF.Max(0.024f, bone.CapsuleRadius * 0.85f));
        if (IsShinBone(bone.Name))
            return new Vector3(MathF.Max(0.042f, bone.CapsuleRadius * 1.15f), MathF.Max(0.09f, bone.CapsuleHalfLength), MathF.Max(0.030f, bone.CapsuleRadius * 0.85f));
        if (IsForearmBone(bone.Name))
            return new Vector3(MathF.Max(0.030f, bone.CapsuleRadius * 1.10f), MathF.Max(0.060f, bone.CapsuleHalfLength), MathF.Max(0.022f, bone.CapsuleRadius * 0.85f));
        if (IsFootBone(bone.Name))
            return new Vector3(0.035f, MathF.Max(0.045f, bone.CapsuleHalfLength), 0.018f);
        if (IsHandBone(bone.Name))
            return new Vector3(0.025f, MathF.Max(0.035f, bone.CapsuleHalfLength), 0.014f);

        var r = MathF.Max(0.01f, bone.CapsuleRadius);
        var h = MathF.Max(0.01f, bone.CapsuleHalfLength);
        return new Vector3(r, h, r);
    }

    private static float DefaultSwingMinLimit(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || name.StartsWith("j_asi_b_", StringComparison.Ordinal))
            return 0.75f;
        if (role == AnatomicalRole.Elbow || name.StartsWith("j_ude_b_", StringComparison.Ordinal))
            return 0.45f;

        return 0f;
    }

    private static float DefaultHingeRestAngle(AnatomicalRole role, string name)
    {
        return 0f;
    }

    private static float DefaultHingeRestSpringFreq(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || name.StartsWith("j_asi_b_", StringComparison.Ordinal))
            return 3.5f;
        if (role == AnatomicalRole.Elbow || name.StartsWith("j_ude_b_", StringComparison.Ordinal))
            return 3.5f;
        return 0f;
    }

    private static float DefaultHingeRestMaxForce(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || name.StartsWith("j_asi_b_", StringComparison.Ordinal))
            return 50.0f;
        if (role == AnatomicalRole.Elbow || name.StartsWith("j_ude_b_", StringComparison.Ordinal))
            return 30.0f;
        return 0f;
    }

    private static bool HasPassiveHingeRest(AnatomicalRole role, string name)
    {
        return role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow ||
               name.StartsWith("j_asi_b_", StringComparison.Ordinal) ||
               name.StartsWith("j_ude_b_", StringComparison.Ordinal);
    }


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
            FillProfileDefaults(c);

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
                SwingMinLimit = c.SwingMinLimit ?? 0f,
                HingeRestAngle = c.HingeRestAngle ?? 0f,
                HingeRestSpringFreq = c.HingeRestSpringFreq ?? 0f,
                HingeRestMaxForce = c.HingeRestMaxForce ?? 0f,
                Joint = (JointType)c.JointType,
                TwistMinAngle = c.TwistMinAngle,
                TwistMaxAngle = c.TwistMaxAngle,
                AnatomicalRole = (AnatomicalRole)c.AnatomicalRole,
                ColliderShape = (RagdollColliderShape)c.ColliderShape,
                BoxHalfExtents = new Vector3(c.BoxHalfExtentX, c.BoxHalfExtentY, c.BoxHalfExtentZ),
                SoftBody = c.SoftBody,
                SoftSpringFreq = c.SoftSpringFreq,
                SoftSpringDamp = c.SoftSpringDamp,
                SoftServoFreq = c.SoftServoFreq,
                SoftServoDamp = c.SoftServoDamp,
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
        public float HalfLength;        // half the capsule body segment length
        public float CenterFactor;      // parent->child fraction where the capsule is centered
    }

    // Default capsule radius for dynamically discovered bones
    private const float NpcDefaultBoneRadius = 0.04f;
    private const float NpcAutoMinRadius = 0.025f;
    private const float NpcAutoMaxRadius = 0.55f;
    // Minimum segment length to create a collision capsule. Raised from 0.02 to drop the
    // dense cluster of tiny face/finger/hair segments — they don't meaningfully block a
    // falling ragdoll but each one costs a per-frame reposition + UpdateBounds.
    private const float NpcMinSegmentLength = 0.05f;
    // Hard cap on collision capsules per character: keep the longest segments (torso,
    // limbs, head) and drop the rest. Bounds the per-frame static-update cost so a wave
    // of high-bone-count enemies can't add tens of thousands of UpdateBounds calls.
    private const int NpcMaxCollisionSegments = 24;
    // A character whose root is farther than this (metres) from the corpse is skipped in
    // the per-frame static update — it cannot contact the ragdoll, so tracking its bones
    // is wasted work. It still resumes updating if it moves back into range.
    private const float NpcCollisionUpdateRadius = 8f;

    // Per-NPC collision state (bone-based, convex hull, or single-capsule fallback)
    private struct NpcCollisionState
    {
        public nint NpcAddress;
        public List<NpcBoneStatic> BoneStatics;   // populated when skeleton readable
        public StaticHandle FallbackHandle;        // used when skeleton unreadable
        public bool IsFallback;
        // Convex hull mode (non-humanoid mounts/monsters)
        public bool IsConvexHull;
        public StaticHandle ConvexHullHandle;
        public Vector3 HullCenterModelSpace; // hull centroid in skeleton-local (model) space
        // True while this character's statics have been parked far away because it left the
        // update radius. Prevents leaving them frozen on the corpse ("ghost" capsules) and
        // avoids re-parking every frame; cleared when it re-enters range.
        public bool Parked;
    }

    public RagdollController(BoneTransformService boneService, Npcs.NpcSelector npcSelector,
        Safety.MovementBlockHook movementBlockHook, Configuration config, IPluginLog log,
        Func<IReadOnlyList<nint>>? extraCollisionProvider = null)
    {
        this.boneService = boneService;
        this.npcSelector = npcSelector;
        this.movementBlockHook = movementBlockHook;
        this.extraCollisionProvider = extraCollisionProvider;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
    }

    /// <summary>Lightweight constructor for NPC ragdolls (no NPC collision volumes or movement blocking).</summary>
    public RagdollController(BoneTransformService boneService, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.npcSelector = null;
        this.movementBlockHook = null;
        this.extraCollisionProvider = null;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
    }

    public void Activate(nint characterAddress)
    {
        Activate(characterAddress, config.RagdollActivationDelay);
    }

    public void Activate(nint characterAddress, float delayOverride)
    {
        if (isActive) Deactivate();

        targetCharacterAddress = characterAddress;
        activationDelay = delayOverride;
        elapsed = 0f;
        physicsStarted = false;
        animationFrozen = false;
        followWasActive = false;
        isActive = true;
        lastFrameTimestamp = 0;
        physicsAccumulator = 0f;
        prevAllAsleep = false;
        biomechanicalSettleRemaining = 0f;
        hasPrevPhysicsState = false;
        // Save original position so we can restore if FollowPosition is toggled off
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)characterAddress;
        savedCharacterPosition = go->Position;
        targetEntityId = go->EntityId;

        log.Info($"RagdollController: Activated for 0x{characterAddress:X} (delay={activationDelay:F1}s)");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;

        // Restore animation speed before clearing the address. Gated on animationFrozen
        // (not physicsStarted) so a freeze that happened during a FAILED InitializePhysics
        // is still undone — otherwise the corpse would be stuck at OverallSpeed=0 forever,
        // and a later re-Activate would save that 0 as the "original" speed (sticky freeze).
        // Skip the character write during game shutdown — the actor is already freed then and the
        // write would crash (this runs from Dispose on game close). LocalPlayer null => closing.
        if (targetCharacterAddress != nint.Zero && animationFrozen && Core.Services.ObjectTable.LocalPlayer != null)
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
        targetEntityId = 0;
        physicsStarted = false;
        animationFrozen = false;
        ragdollBoneIndices.Clear();
        activeDefByName.Clear();
        activeRagdollIsGeneric = false;
        terrainPatchCenters.Clear();
        nHaraIndex = -1;
        kaoBodyBoneIndex = -1;
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
            var dt = ComputeFrameDelta();
            elapsed += dt;

            if (attackStrikeTimer > 0f) attackStrikeTimer -= dt;
            UpdateWeaponStrike(dt);

            // Wait for activation delay (death animation plays first)
            if (!physicsStarted)
            {
                if (elapsed < activationDelay) return;
                if (!InitializePhysics()) { Deactivate(); return; }
                physicsStarted = true;
                frameCount = 0;
                BeginBiomechanicalSettle();
                physicsAccumulator = 0f; // start clean — don't carry the activation-delay wait
            }

            StepAndApply(dt);
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
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastFrameTimestamp == 0)
        {
            lastFrameTimestamp = now;
            return FixedTimestep;
        }
        var dt = (float)((now - lastFrameTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency);
        lastFrameTimestamp = now;
        if (dt < 0f) dt = 0f;
        if (dt > MaxFrameDelta) dt = MaxFrameDelta;
        return dt;
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

    private static Quaternion CreateCapsuleRotation(Vector3 segmentDir, Quaternion boneWorldRot)
    {
        var y = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitZ, boneWorldRot), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitX, boneWorldRot), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(MathF.Abs(Vector3.Dot(y, Vector3.UnitY)) > 0.9f ? Vector3.UnitZ : Vector3.UnitY, y);

        z = NormalizeOrFallback(z, Vector3.UnitZ);
        var x = NormalizeOrFallback(Vector3.Cross(y, z), Vector3.UnitX);
        z = NormalizeOrFallback(Vector3.Cross(x, y), z);

        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
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

        // Pack the basis as a rotation matrix whose local X/Y/Z axes map to x/y/z in world.
        // System.Numerics uses the ROW-vector convention (Vector3.Transform(v, m) = v·M, so
        // the image of UnitX is row 1), therefore the basis axes go in the ROWS, not the
        // columns. Placing them in columns yields the transpose = the conjugate rotation,
        // which made every TwistLimit measure twist about the wrong axis.
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
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
        // Both crosses can still degenerate (near-vertical segment AND a pitched skeleton
        // transform whose forward is also near-vertical, e.g. a mounted/flying death). Fall
        // back to a fixed horizontal axis rather than normalizing a zero vector into NaN,
        // which would otherwise propagate into the Hinge constraint and trip the NaN guard.
        return NormalizeOrFallback(hingeAxis, Vector3.UnitX);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-6f)
            return Vector3.Normalize(value);

        return fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        var normal = NormalizeOrFallback(planeNormal, Vector3.UnitY);
        return value - Vector3.Dot(value, normal) * normal;
    }

    private static float ResolveBodyHalfLength(RagdollBoneDef def)
    {
        if (def.ColliderShape == RagdollColliderShape.Box && def.BoxHalfExtents.Y > 0)
            return def.BoxHalfExtents.Y;

        return MathF.Max(0f, def.CapsuleHalfLength);
    }

    private static Vector3 ResolveBoxHalfExtents(RagdollBoneDef def, float bodyHalfLength)
    {
        var x = def.BoxHalfExtents.X > 0 ? def.BoxHalfExtents.X : MathF.Max(0.01f, def.CapsuleRadius);
        var y = def.BoxHalfExtents.Y > 0 ? def.BoxHalfExtents.Y : MathF.Max(0.01f, bodyHalfLength);
        var z = def.BoxHalfExtents.Z > 0 ? def.BoxHalfExtents.Z : MathF.Max(0.01f, def.CapsuleRadius);
        return new Vector3(x, y, z);
    }

    private static float ComputeVerticalExtent(RagdollBoneDef def, Quaternion bodyWorldRot, float bodyHalfLength)
    {
        var yAxis = Vector3.Transform(Vector3.UnitY, bodyWorldRot);
        if (def.ColliderShape != RagdollColliderShape.Box)
            return MathF.Abs(yAxis.Y) * bodyHalfLength + def.CapsuleRadius;

        var extents = ResolveBoxHalfExtents(def, bodyHalfLength);
        var xAxis = Vector3.Transform(Vector3.UnitX, bodyWorldRot);
        var zAxis = Vector3.Transform(Vector3.UnitZ, bodyWorldRot);
        return MathF.Abs(xAxis.Y) * extents.X +
               MathF.Abs(yAxis.Y) * extents.Y +
               MathF.Abs(zAxis.Y) * extents.Z;
    }

    private Vector3 ComputeLegacyBallTwistReference(Vector3 segmentDir)
    {
        var segN = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var refDir = Vector3.Cross(segN, Vector3.UnitY);
        if (refDir.LengthSquared() < 0.001f)
            refDir = Vector3.Cross(segN, Vector3.UnitX);

        return NormalizeOrFallback(refDir, Vector3.UnitX);
    }

    private Vector3 ComputeExperimentalBallTwistReference(Vector3 segmentDir, Vector3 parentSegmentDir)
    {
        var segN = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);

        // Prefer the parent's segment projected into the child's twist plane. This makes
        // ball-joint twist limits use an anatomical parent/child frame instead of world up.
        var refDir = ProjectOntoPlane(parentN, segN);
        if (refDir.LengthSquared() < 0.001f)
            return ComputeLegacyBallTwistReference(segN);

        return NormalizeOrFallback(refDir, ComputeLegacyBallTwistReference(segN));
    }

    private Vector3 ComputeExperimentalHingeAxis(Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var hingeAxis = Vector3.Cross(parentN, childN);

        // Anatomical hinge axes are undefined for near-straight limbs. Keep main's
        // world-up/character-forward fallback in that case.
        if (hingeAxis.LengthSquared() < 0.001f)
            return ComputeHingeAxis(childN);

        var legacyAxis = ComputeHingeAxis(childN);
        hingeAxis = Vector3.Normalize(hingeAxis);
        if (Vector3.Dot(hingeAxis, legacyAxis) < 0)
            hingeAxis = -hingeAxis;

        return hingeAxis;
    }

    private Vector3 ComputeProfileHingeAxis(AnatomicalRole role, Vector3 parentSegmentDir, Vector3 childSegmentDir, Quaternion childBodyRot)
    {
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);

        if (role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow)
        {
            var frameAxis = ProjectOntoPlane(Vector3.Transform(Vector3.UnitX, childBodyRot), childN);
            if (frameAxis.LengthSquared() > 0.001f)
            {
                var axis = Vector3.Normalize(frameAxis);
                var experimental = ComputeExperimentalHingeAxis(parentSegmentDir, childN);
                if (Vector3.Dot(axis, experimental) < 0)
                    axis = -axis;
                return axis;
            }
        }

        return ComputeExperimentalHingeAxis(parentSegmentDir, childN);
    }

    private Vector3 ComputeHingeForward(Vector3 hingeAxis, Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var parentN = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var forward = Vector3.Cross(hingeAxis, parentN);
        if (forward.LengthSquared() < 0.001f)
            forward = ProjectOntoPlane(childN, hingeAxis);

        forward = NormalizeOrFallback(forward, childN);
        if (Vector3.Dot(forward, childN) < 0)
            forward = -forward;

        return forward;
    }

    private void AddAnatomicalHingeFoldStop(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld,
        SpringSettings limitSpring)
    {
        if (boneDef.SwingMinLimit <= 0 || boneDef.SwingMinLimit >= MathF.PI || simulation == null)
            return;

        // Measure the true anatomical bend angle (angle between child and parent segment dirs).
        // This is pose-independent: 0° = straight, 90° = ORZ-bent, up to 180° = folded back.
        var initBendAngle = MathF.Acos(Math.Clamp(
            Vector3.Dot(Vector3.Normalize(segDirWorld), Vector3.Normalize(parentSegDir)), -1f, 1f));

        // Set the fold stop limit to max(SwingMinLimit, initBend + buffer) so it never fires
        // at the initial pose — even from ORZ (90°) — but still protects against hyper-flexion.
        // Straight init: max(43°, 0°+15°) = 43°   — normal anatomical protection.
        // ORZ init:      max(43°, 90°+15°) = 105°  — fires only at extreme over-bending.
        const float foldBuffer = 0.26f; // ~15°
        var foldStopMaxAngle = MathF.Max(boneDef.SwingMinLimit, initBendAngle + foldBuffer);
        foldStopMaxAngle = MathF.Min(foldStopMaxAngle, MathF.PI - 0.05f);

        // Both axes use segment directions so the constraint measures the actual anatomical
        // bend angle, not a pose-derived reference that can flip between init poses.
        var shinAxisLocalChild = Vector3.Normalize(Vector3.Transform(
            segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
        var thighAxisLocalParent = Vector3.Normalize(Vector3.Transform(
            parentSegDir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

        simulation.Solver.Add(childHandle, parentHandle,
            new SwingLimit
            {
                AxisLocalA = shinAxisLocalChild,
                AxisLocalB = thighAxisLocalParent,
                MaximumSwingAngle = foldStopMaxAngle,
                SpringSettings = limitSpring,
            });

        if (config.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' fold stop: initBend={initBendAngle * 180f / MathF.PI:F1}° limit={foldStopMaxAngle * 180f / MathF.PI:F1}°");
    }

    private void AddAnatomicalHingeRestBias(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld)
    {
        // TwistServo-based extension bias is disabled. The AngularMotor on hinge joints
        // now provides velocity damping that lets gravity drive extension naturally — the
        // gravity torque on the hanging shin/forearm decelerates to zero as the joint
        // reaches straight, so no active spring constraint is needed.
        return;

        if (!HasPassiveHingeRest(boneDef.AnatomicalRole, boneDef.Name) ||
            boneDef.HingeRestSpringFreq <= 0 ||
            boneDef.HingeRestMaxForce <= 0 ||
            simulation == null)
            return;

        var childBasis = CreateTwistBasis(hingeAxisWorld, segDirWorld);
        var parentBasis = CreateTwistBasis(hingeAxisWorld, parentSegDir);

        var initBendCos = Vector3.Dot(Vector3.Normalize(segDirWorld), Vector3.Normalize(parentSegDir));
        var initBendDeg = MathF.Acos(Math.Clamp(initBendCos, -1f, 1f)) * (180f / MathF.PI);
        if (config.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' hinge rest init bend={initBendDeg:F1}° (0=straight, 90=ORZ)");

        simulation.Solver.Add(childHandle, parentHandle,
            new TwistServo
            {
                LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * childBasis),
                LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * parentBasis),
                TargetAngle = boneDef.HingeRestAngle,
                SpringSettings = new SpringSettings(boneDef.HingeRestSpringFreq, 0.8f),
                ServoSettings = new ServoSettings(10.0f, 0f, boneDef.HingeRestMaxForce),
            });

        if (config.RagdollVerboseLog)
            log.Info($"[Ragdoll Constraint] '{boneDef.Name}' passive hinge rest: angle={boneDef.HingeRestAngle:F2} freq={boneDef.HingeRestSpringFreq:F2} force={boneDef.HingeRestMaxForce:F2}");
    }

    private void BeginBiomechanicalSettle(float duration = BiomechanicalSettleDuration)
    {
        biomechanicalSettleRemaining = MathF.Max(biomechanicalSettleRemaining, duration);
        prevAllAsleep = false;
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

    private Vector3 ApplyInitialUndergroundBoneLift(Dictionary<string, Vector3> boneWorldPositions)
    {
        if (!config.RagdollLiftUndergroundBonesOnStart || boneWorldPositions.Count == 0)
            return Vector3.Zero;

        const float defaultClearance = 0.03f;
        const float kneeCalfClearance = 0.15f;
        var requiredLift = 0f;
        var worstBone = string.Empty;
        var worstGround = groundY;
        var worstBoneY = 0f;
        var sampled = 0;

        foreach (var (name, pos) in boneWorldPositions)
        {
            var localGroundY = groundY;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(pos.X, pos.Y + TerrainRaycastStartYOffset, pos.Z),
                    new Vector3(0, -1, 0),
                    out var hit,
                    TerrainRaycastDistance))
            {
                localGroundY = hit.Point.Y;
                sampled++;
            }

            var clearance = IsKneeOrCalfStartupLiftBone(name) ? kneeCalfClearance : defaultClearance;
            var lift = localGroundY + clearance - pos.Y;
            if (lift <= requiredLift)
                continue;

            requiredLift = lift;
            worstBone = name;
            worstGround = localGroundY;
            worstBoneY = pos.Y;
        }

        if (requiredLift <= 0f)
            return Vector3.Zero;

        var offset = new Vector3(0f, requiredLift, 0f);
        var keys = new List<string>(boneWorldPositions.Keys);
        foreach (var key in keys)
            boneWorldPositions[key] += offset;

        log.Info($"RagdollController: lifted initial ragdoll pose by {requiredLift:F3}m " +
                 $"({worstBone} y={worstBoneY:F3}, ground={worstGround:F3}, sampled={sampled}/{boneWorldPositions.Count}).");
        return offset;
    }

    private bool InitializePhysics()
    {
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return false;
        var skel = skelNullable.Value;
        initialBoneCount = skel.BoneCount;
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
        // Name → definition, used to walk the config parent chain when a bone's
        // direct parent is missing from the skeleton (see parent resolution below).
        var defByName = new Dictionary<string, RagdollBoneDef>();
        ragdollBones.Clear();

        foreach (var def in BoneDefs)
        {
            defByName[def.Name] = def;

            var idx = boneService.ResolveBoneIndex(skel, def.Name);
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

        // Record the active defs so StepAndApply reads capsule extents from the set
        // actually in use (human or generated) rather than re-deriving the human set.
        activeDefByName.Clear();
        foreach (var def in BoneDefs)
            activeDefByName[def.Name] = def;

        // Raycast for ground height
        groundY = skelWorldPos.Y;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(skelWorldPos.X, skelWorldPos.Y + TerrainRaycastStartYOffset, skelWorldPos.Z),
                new Vector3(0, -1, 0),
                out var hitInfo,
                TerrainRaycastDistance))
        {
            groundY = hitInfo.Point.Y;
        }
        log.Info($"RagdollController: Raycast ground Y={groundY:F3}");

        // Create BEPU simulation
        // When self-collision is enabled, ConnectedPairs filters out nearby body pairs
        // (1-2 hops) while allowing distant body-body collisions (arms vs torso).
        // When disabled (null), only body-static collisions are allowed.
        // Generic (non-humanoid) ragdolls force self-collision OFF: their generated
        // capsules commonly overlap (fat/compact bodies like toads), and body-body
        // contact on overlapping capsules produces explosive separation forces. The
        // human profile is hand-tuned so its capsules don't overlap, so it keeps it.
        var connectedPairs = (config.RagdollSelfCollision && !genericSkeleton)
            ? new HashSet<(int, int)>()
            : null;

        // Party combat ragdolls deal with many more colliding bodies/statics, so
        // force a higher solver iteration count for stability while party combat
        // ragdolls are enabled.
        var partyRagdollActive = config.EnableCombatCompanions &&
            (config.PartyCompanionDeathRagdoll || config.EnableNpcDeathRagdoll);
        var solverIterations = partyRagdollActive ? 8 : config.RagdollSolverIterations;
        // Generic rigs build a stiffer, less-conditioned constraint network — give the
        // solver more iterations so it converges instead of pumping energy each frame.
        if (genericSkeleton)
            solverIterations = Math.Max(solverIterations, GenericSolverIterations);

        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks { ConnectedPairs = connectedPairs, Friction = config.RagdollFriction },
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(solverIterations, Math.Max(1, config.RagdollSolverSubsteps)));

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
        int enemyPatches = 0;
        AddTerrainPatch(skelWorldPos.X, skelWorldPos.Z, groundY);
        if (npcSelector != null && config.ExtendTerrainDetection)
        {
            const float maxEnemyPatchDist = 40f;
            const int maxEnemyPatches = 16;

            var nearby = new List<(float DistSq, float X, float Z)>();
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.BattleChara == null || npc.Address == targetCharacterAddress)
                    continue;
                var pos = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara)->Position;
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
        var pose = skel.Pose;
        var boneWorldPositions = new Dictionary<string, Vector3>();
        var boneWorldRotations = new Dictionary<string, Quaternion>();
        var initialPoseLift = Vector3.Zero;

        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var idx)) continue;
            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            boneWorldPositions[def.Name] = ModelToWorld(modelPos);
            boneWorldRotations[def.Name] = ModelRotToWorld(modelRot);
        }

        initialPoseLift = ApplyInitialUndergroundBoneLift(boneWorldPositions);

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
        // Capsule half-length is usually clamped to the current pose segment distance.
        // Anatomical hinges keep their profile length instead; ORZ-style death poses can
        // collapse the observed knee/elbow segment and bake a fake short limb into physics.
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            Vector3 capsuleCenter;
            float segmentHalfLength;
            float effectiveHalfLength = ResolveBodyHalfLength(def);
            Quaternion capsuleWorldRot;
            var preserveAnatomicalLength = def.Joint == JointType.Hinge &&
                                           HasPassiveHingeRest(def.AnatomicalRole, def.Name);

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
                    if (!preserveAnatomicalLength && effectiveHalfLength > maxHalf)
                    {
                        log.Info($"[Ragdoll Init] '{def.Name}' capsule clamped: halfLen {effectiveHalfLength:F3} -> {maxHalf:F3} (segLen={segLen:F3})");
                        effectiveHalfLength = maxHalf;
                    }
                    else if (preserveAnatomicalLength && effectiveHalfLength > maxHalf && config.RagdollVerboseLog)
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
                ref var childMt = ref pose->ModelPose.Data[skelChildIdx];
                var childModelPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                var skelChildWorldPos = ModelToWorld(childModelPos) + initialPoseLift;
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
                    else if (preserveAnatomicalLength && effectiveHalfLength > maxHalf && config.RagdollVerboseLog)
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

            // Rotation offset: capsuleWorldRot * offset = boneWorldRot
            // Ankle (j_asi_c) and Knee (j_asi_b): override capsule direction to match
            // the parent bone's direction at activation.
            //
            // The death animation bends these bones before the ragdoll fires. Any
            // constraint that references the body's initial orientation (Weld, SwingLimit
            // axes, FoldStop, TwistLimit basis) then bakes that bend permanently, causing
            // a zigzag (Ankle) or unnatural knee fold (Knee).
            //
            // Fix: recompute capsuleWorldRot from the parent→child world segment so both
            // bodies start collinear. For Ankle the Weld then snapshots ≈ Identity; for
            // Knee the Hinge constraint axes (SwingLimit forward, FoldStop, TwistLimit)
            // are derived from a neutral straight-leg pose and physics gravity handles the
            // natural fold.
            if ((def.AnatomicalRole == AnatomicalRole.Ankle || def.AnatomicalRole == AnatomicalRole.Knee) &&
                def.ParentName != null &&
                boneWorldPositions.TryGetValue(def.ParentName, out var alignParentPos) &&
                boneWorldRotations.TryGetValue(def.ParentName, out var alignParentBoneRot))
            {
                var parentToChild = boneWorldPos - alignParentPos;
                var ptcLen = parentToChild.Length();
                if (ptcLen > 0.01f)
                {
                    capsuleWorldRot = CreateCapsuleRotation(parentToChild, alignParentBoneRot);
                    capsuleCenter   = boneWorldPos + (effectiveHalfLength / ptcLen) * parentToChild;
                }
            }

            var capsuleToBoneOffset = Quaternion.Normalize(
                Quaternion.Inverse(capsuleWorldRot) * boneWorldRot);

            // Clamp body center above ground so bodies don't start underground.
            // Underground capsules cause explosive ground-collision forces in the first
            // frames. Lift just enough so the capsule bottom (center - extent - radius)
            // is at the ground plane.
            var bodyBottomExtent = ComputeVerticalExtent(def, capsuleWorldRot, effectiveHalfLength);
            var minCenterY = groundY + bodyBottomExtent + 0.005f; // 5mm clearance
            if (capsuleCenter.Y < minCenterY)
            {
                log.Info($"[Ragdoll Init] '{def.Name}' lifted above ground: Y {capsuleCenter.Y:F3} -> {minCenterY:F3} (ground={groundY:F3})");
                capsuleCenter.Y = minCenterY;
            }

            TypedIndex shapeIndex;
            BodyInertia bodyInertia;
            if (def.ColliderShape == RagdollColliderShape.Box)
            {
                var extents = ResolveBoxHalfExtents(def, effectiveHalfLength);
                var box = new Box(extents.X * 2f, extents.Y * 2f, extents.Z * 2f);
                shapeIndex = simulation.Shapes.Add(box);
                bodyInertia = box.ComputeInertia(def.Mass);
            }
            else
            {
                var capsuleLength = effectiveHalfLength * 2;
                var capsule = new Capsule(def.CapsuleRadius, capsuleLength);
                shapeIndex = simulation.Shapes.Add(capsule);
                bodyInertia = capsule.ComputeInertia(def.Mass);
            }

            // SleepThreshold: 0.01 = normal (bodies sleep when settled).
            // -1 = never sleep (settle collision keeps bodies always active for NPC interaction).
            var sleepThreshold = (config.RagdollNpcSettleCollision && config.RagdollNpcCollision) ? -1f : 0.01f;
            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                bodyInertia,
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(sleepThreshold));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

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
            });

            // Log initial state for diagnostics
            if (config.RagdollVerboseLog)
            {
                var logCapsuleY = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
                log.Info($"[Ragdoll Init] '{def.Name}' idx={boneIdx} " +
                         $"bonePos=({boneWorldPos.X:F3},{boneWorldPos.Y:F3},{boneWorldPos.Z:F3}) " +
                         $"capsuleCenter=({capsuleCenter.X:F3},{capsuleCenter.Y:F3},{capsuleCenter.Z:F3}) " +
                         $"shape={def.ColliderShape} segHalf={segmentHalfLength:F3} bodyHalfY={effectiveHalfLength:F3} " +
                         $"capsuleY=({logCapsuleY.X:F3},{logCapsuleY.Y:F3},{logCapsuleY.Z:F3})");
            }
        }

        // --- Pass 3: Add constraints between connected bones ---
        // Per the BEPU RagdollDemo, each joint gets a layered constraint set:
        //   Ball joints: BallSocket + SwingLimit (cone) + TwistLimit (axial, asymmetric) + AngularMotor
        //   Hinge joints: Hinge + TwistLimit (angular range, asymmetric) + AngularMotor
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        var jointSpring = new SpringSettings(30, 1);
        var limitSpring = new SpringSettings(config.RagdollLimitSpringFrequency, 1);
        var motorDamping = 0.01f;

        // BEPU's TwistLimit measurement degenerates once a ball joint's swing exceeds ~90
        // degrees. Keep the full configured swing cone and skip twist limits on wide joints.
        const float ballJointMaxSwing = 1.40f;

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
            var parentSegDir = Vector3.Transform(Vector3.UnitY, parentBodyRef.Pose.Orientation);
            if (parentSegDir.LengthSquared() < 0.001f)
                parentSegDir = Vector3.UnitY;

            // --- Positional + angular constraint ---
            if (boneDef.Joint == JointType.Hinge)
            {
                // Knee/elbow: BallSocket (position-only) + SwingLimits — same pattern as shoulder.
                // A strict Hinge (5-DOF) constrains 2 angular axes with SpringSettings(30,1),
                // producing ~83,000 N·m/rad stiffness. Any force perpendicular to the hinge axis
                // generates ~1400 N·m corrective torque per degree of error — far more than the
                // 8-iteration PGS solver can resolve in one frame. The joint appears completely
                // frozen under grab and external collision, and the first-grab "twist snap" is
                // the angular corrective impulse firing on frame 1.
                // BallSocket constrains position only (3 DOF, like the shoulder), so the limb
                // responds naturally to any applied force. SwingLimits provide anatomical range.
                var hingeAxisWorld = config.RagdollExperimentalJointFrames
                    ? ComputeProfileHingeAxis(boneDef.AnatomicalRole, parentSegDir, segDirWorld, childBodyRef.Pose.Orientation)
                    : ComputeHingeAxis(segDirWorld);

                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new BallSocket
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalOffsetB = parentLocalAnchor,
                        SpringSettings = jointSpring,
                    });

                // SwingLimit: bending range for the knee/elbow.
                // AxisLocalA = child segment direction; AxisLocalB = "forward" on parent body
                // (perpendicular to parent segment, in the bend plane).
                // At full extension the axes are ~perpendicular → legal range spans
                // "fully straight" through "fully folded" while blocking hyperextension.
                if (boneDef.SwingLimit > 0)
                {
                    // "Forward" direction on the parent body = Cross(hingeAxis, parentSegDir).
                    // This is the direction the child limb swings toward during flexion.
                    var forwardWorld = ComputeHingeForward(hingeAxisWorld, parentSegDir, segDirWorld);

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

                    if (config.RagdollVerboseLog)
                        log.Info($"[Ragdoll Constraint] '{rb.Name}' SwingLimit: parentFwd=({forwardWorld.X:F3},{forwardWorld.Y:F3},{forwardWorld.Z:F3}) childSeg=({segDirWorld.X:F3},{segDirWorld.Y:F3},{segDirWorld.Z:F3}) max={boneDef.SwingLimit:F2}rad dot={Vector3.Dot(forwardWorld, segDirWorld):F3}");
                }

                AddAnatomicalHingeFoldStop(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld, limitSpring);
                AddAnatomicalHingeRestBias(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld);

                // Axial-rotation guard: prevents the shin/forearm from spinning around its
                // long axis. Anatomical limits differ: the tibia (shin) allows only ~±10°
                // of axial rotation in a relaxed dead leg, so knees use a tight ±0.2 rad
                // (≈±11°). The forearm has genuine pronation/supination (~±80°) so elbows
                // use a wider ±0.8 rad. Soft 10 Hz spring — inequality constraint only fires
                // at the boundary, avoiding the constant large impulses that froze the old
                // Hinge equality constraints.
                {
                    var isKnee = boneDef.AnatomicalRole == AnatomicalRole.Knee;
                    var twistRange = isKnee ? 0.2f : 0.8f;
                    var twistBasis = CreateTwistBasis(segDirWorld, hingeAxisWorld);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis),
                            LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis),
                            MinimumAngle = -twistRange,
                            MaximumAngle =  twistRange,
                            SpringSettings = new SpringSettings(10f, 1f),
                        });
                }

                if (config.RagdollVerboseLog)
                    log.Info($"[Ragdoll Constraint] '{rb.Name}' BallSocket (hinge replaced) hingeAxis=({hingeAxisWorld.X:F3},{hingeAxisWorld.Y:F3},{hingeAxisWorld.Z:F3})");
            }
            else
            {
                // j_asi_c (Ankle): Weld to parent (j_asi_b). The capsule was already
                // aligned to the parent direction in Pass 2, so the snapshot here is
                // LocalOrientation ≈ Identity — no animation bend is baked in.
                if (boneDef.AnatomicalRole == AnatomicalRole.Ankle)
                {
                    var weldOffset = parentBodyRef.Pose.Position - childBodyRef.Pose.Position;
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new Weld
                        {
                            LocalOffset = Vector3.Transform(
                                weldOffset, Quaternion.Inverse(childBodyRef.Pose.Orientation)),
                            LocalOrientation = Quaternion.Normalize(
                                Quaternion.Inverse(childBodyRef.Pose.Orientation) * parentBodyRef.Pose.Orientation),
                            SpringSettings = jointSpring,
                        });
                    continue;
                }

                // BallSocket: positional connection
                // Soft bodies use low frequency + low damping for jiggle
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new BallSocket
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalOffsetB = parentLocalAnchor,
                        SpringSettings = boneDef.SoftBody
                            ? new SpringSettings(boneDef.SoftSpringFreq, boneDef.SoftSpringDamp)
                            : jointSpring,
                    });

                // SwingLimit: symmetric cone limiting deviation from initial direction.
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
                            SpringSettings = limitSpring, // stiff wall even for soft bodies
                        });
                }

                // TwistLimit: asymmetric axial rotation around the bone's segment axis.
                // Skipped on wide-swing joints where the twist basis becomes unreliable.
                if ((boneDef.TwistMinAngle != 0 || boneDef.TwistMaxAngle != 0) &&
                    boneDef.SwingLimit <= ballJointMaxSwing)
                {
                    var refDir = config.RagdollExperimentalJointFrames
                        ? ComputeExperimentalBallTwistReference(segDirWorld, parentSegDir)
                        : ComputeLegacyBallTwistReference(segDirWorld);

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

            // Angular constraint: soft bodies use AngularServo (spring return to rest pose),
            // rigid bodies use AngularMotor (velocity damping only).
            if (boneDef.SoftBody)
            {
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularServo
                    {
                        TargetRelativeRotationLocalA = Quaternion.Identity,
                        ServoSettings = new ServoSettings(float.MaxValue, 0f, float.MaxValue),
                        SpringSettings = new SpringSettings(boneDef.SoftServoFreq, boneDef.SoftServoDamp),
                    });
            }
            else
            {
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularMotor
                    {
                        TargetVelocityLocalA = Vector3.Zero,
                        Settings = new MotorSettings(float.MaxValue, motorDamping),
                    });
            }
        }

        // Nothing to simulate (e.g. a non-humanoid skeleton with no qualifying segments).
        // Bail BEFORE freezing the animation — freezing a rig we can't drive would leave the
        // corpse stuck mid-pose. Caller (OnRenderFrame) Deactivates on the false return.
        if (ragdollBones.Count == 0)
        {
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
        var character = (Character*)targetCharacterAddress;
        savedOverallSpeed = character->Timeline.OverallSpeed;
        character->Timeline.OverallSpeed = 0f;
        animationFrozen = true;
        log.Info($"RagdollController: Animation frozen (saved speed={savedOverallSpeed:F2})");

        // Resolve ancestor bone — n_hara must follow j_kosi to prevent mesh tearing
        nHaraIndex = boneService.ResolveBoneIndex(skel, "n_hara");
        kaoBodyBoneIndex = boneService.ResolveBoneIndex(skel, "j_kao");
        log.Info($"RagdollController: n_hara index={nHaraIndex}, j_kao index={kaoBodyBoneIndex}");

        // Create NPC collision volumes — dynamically discover bones from each NPC's skeleton.
        // Works for any model (humanoid, monster, dragon) — no hardcoded bone names.
        npcCollisionStates.Clear();
        if (config.RagdollNpcCollision && npcSelector != null)
        {
            var scale = config.RagdollNpcCollisionScale;
            var capsuleRadius = config.RagdollNpcCollisionAutoSize ? NpcDefaultBoneRadius : NpcDefaultBoneRadius * scale;

            // Fallback single-capsule shape for NPCs whose skeleton can't be read
            var fbRadius = config.RagdollNpcCollisionAutoSize ? 0.35f : 0.3f * scale;
            var fbLength = config.RagdollNpcCollisionAutoSize ? 1.2f : MathF.Max(0.2f, 1.6f - fbRadius * 2f);
            npcFallbackShapeIndex = simulation.Shapes.Add(new Capsule(fbRadius, fbLength));
            npcFallbackShapeReady = true;

            log.Info($"RagdollController: NPC bone collision — {npcSelector.SelectedNpcs.Count} NPCs, autoSize={config.RagdollNpcCollisionAutoSize}, scale={scale:F2}");

            // Dedupe across both sources so a character (e.g. a companion that also
            // appears elsewhere) only gets one set of collision statics.
            var addedCollisionAddresses = new HashSet<nint>();

            // Extra collision actors — in party mode the local player and every spawned
            // companion. They get the SAME full per-bone collision as enemies so every
            // ragdoll collides with every living character, not just enemies. Evaluated
            // here (at activation) so a dying player's ragdoll sees the live companions.
            var extraCollisionAddresses = extraCollisionProvider?.Invoke() ?? Array.Empty<nint>();
            foreach (var address in extraCollisionAddresses)
            {
                if (address == nint.Zero || address == targetCharacterAddress)
                    continue;
                if (!addedCollisionAddresses.Add(address))
                    continue;

                BuildCharacterCollision(address, "extra actor", scale, capsuleRadius);
            }

            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.BattleChara == null)
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
        foreach (var s in npcCollisionStates)
            totalNpcStatics += s.IsFallback ? 1 : s.BoneStatics.Count;
        log.Info($"RagdollController: Physics initialized — {ragdollBones.Count} bodies, {npcCollisionStates.Count} NPCs ({totalNpcStatics} collision volumes), ground={groundY:F3}");

        // Initialize hair physics
        if (config.RagdollHairPhysics && kaoBodyBoneIndex >= 0)
        {
            hairPhysics = new HairPhysicsSimulator(config, log);
            hairPhysics.Initialize(skel.CharBase, kaoBodyBoneIndex);
        }

        return ragdollBones.Count > 0;
    }

    /// <summary>
    /// Lay one terrain collision patch centered on (centerX, centerZ): raycast
    /// a grid of ground heights and add it to the simulation as a static mesh. Used
    /// for both the player's death spot and nearby enemies so a grabbed-and-released
    /// ragdoll always has ground beneath it. <paramref name="defaultGroundY"/> is the
    /// fallback height for the center raycast and any grid ray that misses.
    /// </summary>
    private void AddTerrainPatch(float centerX, float centerZ, float defaultGroundY)
    {
        if (simulation == null || bufferPool == null)
            return;

        var center = new Vector2(centerX, centerZ);
        foreach (var existing in terrainPatchCenters)
            if (Vector2.DistanceSquared(existing, center) < 1.0f)
                return;

        if (terrainPatchCenters.Count >= MaxTerrainPatches)
            return;

        int gridSize = (int)(TerrainPatchRadius * 2 / TerrainPatchStep) + 1;

        // Ground estimate at the patch center — used as the ray origin height and the
        // miss fallback, so a patch under an enemy on different ground still works.
        float patchGroundY = defaultGroundY;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(centerX, defaultGroundY + TerrainRaycastStartYOffset, centerZ),
                new Vector3(0, -1, 0), out var centerHit, TerrainRaycastDistance))
            patchGroundY = centerHit.Point.Y;

        var heights = new float[gridSize, gridSize];
        var ox = centerX - TerrainPatchRadius;
        var oz = centerZ - TerrainPatchRadius;
        for (int gz = 0; gz < gridSize; gz++)
        for (int gx = 0; gx < gridSize; gx++)
        {
            var wx = ox + gx * TerrainPatchStep;
            var wz = oz + gz * TerrainPatchStep;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(wx, patchGroundY + TerrainRaycastStartYOffset, wz),
                    new Vector3(0, -1, 0), out var gridHit, TerrainRaycastDistance))
                heights[gx, gz] = gridHit.Point.Y;
            else
                heights[gx, gz] = patchGroundY;
        }

        var triCount = (gridSize - 1) * (gridSize - 1) * 2;
        bufferPool.Take<Triangle>(triCount, out var triangles);
        int ti = 0;
        for (int gz = 0; gz < gridSize - 1; gz++)
        for (int gx = 0; gx < gridSize - 1; gx++)
        {
            var x0 = ox + gx * TerrainPatchStep;
            var x1 = x0 + TerrainPatchStep;
            var z0 = oz + gz * TerrainPatchStep;
            var z1 = z0 + TerrainPatchStep;
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
        terrainPatchCenters.Add(center);
    }

    private void EnsureTerrainPatchCoverage(Vector3[] worldPositions, bool[] boneValid, int boneCount)
    {
        if (terrainPatchCenters.Count >= MaxTerrainPatches)
            return;

        Vector3 sample = default;
        var hasSample = false;
        if (nHaraIndex >= 0)
        {
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i] || ragdollBones[i].BoneIndex != nHaraIndex)
                    continue;

                sample = worldPositions[i];
                hasSample = true;
                break;
            }
        }

        if (!hasSample)
        {
            var sum = Vector3.Zero;
            var count = 0;
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i])
                    continue;

                sum += worldPositions[i];
                count++;
            }

            if (count == 0)
                return;

            sample = sum / count;
        }

        var sample2 = new Vector2(sample.X, sample.Z);
        foreach (var center in terrainPatchCenters)
            if (Vector2.DistanceSquared(center, sample2) <= TerrainPatchRefreshDistance * TerrainPatchRefreshDistance)
                return;

        AddTerrainPatch(sample.X, sample.Z, realGroundY);
        if (config.RagdollVerboseLog)
            log.Info($"RagdollController: added dynamic terrain patch at ({sample.X:F2}, {sample.Z:F2}); total={terrainPatchCenters.Count}");
    }

    /// <summary>
    /// Build collision statics for a live character (enemy, companion, or player).
    /// Discovers bones from the character's skeleton and creates one static capsule
    /// per parent→child segment. Falls back to a single capsule when the skeleton
    /// can't be read. Works for any model (humanoid, monster, dragon).
    /// </summary>
    private void BuildCharacterCollision(nint address, string label, float scale, float capsuleRadius)
    {
        var charSkel = boneService.TryGetSkeleton(address);
        if (charSkel == null)
        {
            CreateFallbackCharacterCollision(label, address);
            return;
        }

        var ns = charSkel.Value;
        var npcSkeleton = ns.CharBase->Skeleton;
        if (npcSkeleton == null)
        {
            CreateFallbackCharacterCollision(label, address);
            return;
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

        // Convex hull mode: one hull shape per character built from the bone point cloud.
        // Eliminates inter-capsule gaps on any skeleton type; the shape is a snapshot of
        // the activation pose and tracks only root translation/rotation each frame.
        if (config.RagdollNpcCollisionConvexHull)
        {
            BuildConvexHullCollision(address, ns, npcSkelPos, npcSkelRot, label);
            return;
        }

        var boneStatics = new List<NpcBoneStatic>();
        var autoSize = config.RagdollNpcCollisionAutoSize;
        var profileRadii = autoSize ? BuildHumanoidCollisionRadiusMap(ns) : null;
        var autoContext = autoSize ? BuildNpcAutoCollisionContext(ns, npcSkelPos, npcSkelRot) : default;

        // Pass 1: collect every qualifying parent→child segment with its length so we can
        // keep only the longest NpcMaxCollisionSegments (the big body-blocking segments)
        // rather than building a static for every twig.
        var candidates = new List<(float SegLen, int BoneIdx, int ParentIdx, float HalfLen, float Radius, float CenterFactor, Vector3 Center, Quaternion Rot)>();
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

            var radius = autoSize
                ? EstimateNpcCollisionRadius(i, parentIdx, segLen, profileRadii, autoContext)
                : capsuleRadius;
            var halfLen = autoSize ? MathF.Max(0.01f, (segLen * 0.5f) - radius) : segLen * 0.45f * scale;
            var centerFactor = autoSize ? 0.5f : halfLen / segLen;
            var segDir = segment / segLen;
            candidates.Add((segLen, i, parentIdx, halfLen,
                radius, centerFactor, parentWorldPos + (segLen * centerFactor) * segDir, RotationFromYToDirection(segment)));
        }

        // Keep only the longest segments when a high-bone-count rig exceeds the cap.
        if (candidates.Count > NpcMaxCollisionSegments)
        {
            candidates.Sort((a, b) => b.SegLen.CompareTo(a.SegLen));
            candidates.RemoveRange(NpcMaxCollisionSegments, candidates.Count - NpcMaxCollisionSegments);
        }

        // Pass 2: create the static capsule for each kept segment.
        foreach (var c in candidates)
        {
            var shapeIndex = simulation!.Shapes.Add(new Capsule(c.Radius, c.HalfLen * 2f));
            var staticHandle = simulation.Statics.Add(new StaticDescription(c.Center, c.Rot, shapeIndex));
            boneStatics.Add(new NpcBoneStatic
            {
                Handle = staticHandle,
                BoneIndex = c.BoneIdx,
                ParentBoneIndex = c.ParentIdx,
                HalfLength = c.HalfLen,
                CenterFactor = c.CenterFactor,
            });
        }

        if (boneStatics.Count > 0)
        {
            npcCollisionStates.Add(new NpcCollisionState
            {
                NpcAddress = address,
                BoneStatics = boneStatics,
                IsFallback = false,
            });
            log.Info($"RagdollController: {label} bone collision — {boneStatics.Count} segments from {boneCount} bones");
        }
        else
        {
            CreateFallbackCharacterCollision(label, address);
        }
    }

    private Dictionary<int, float> BuildHumanoidCollisionRadiusMap(SkeletonAccess skel)
    {
        var result = new Dictionary<int, float>();
        foreach (var signatureBone in HumanoidSignatureBones)
            if (boneService.ResolveBoneIndex(skel, signatureBone) < 0)
                return result;

        foreach (var def in GetBoneDefs())
        {
            var index = boneService.ResolveBoneIndex(skel, def.Name);
            if (index < 0)
                continue;

            var radius = def.CapsuleRadius;
            if (def.ColliderShape == RagdollColliderShape.Box)
            {
                var extents = ResolveBoxHalfExtents(def, ResolveBodyHalfLength(def));
                radius = MathF.Max(extents.X, extents.Z);
            }

            result[index] = Math.Clamp(radius, NpcAutoMinRadius, NpcAutoMaxRadius);
        }

        return result;
    }

    private struct NpcAutoCollisionContext
    {
        public float SkeletonRadius;
        public float[] NearestBoneDistances;
    }

    private NpcAutoCollisionContext BuildNpcAutoCollisionContext(SkeletonAccess skel, Vector3 skelPos, Quaternion skelRot)
    {
        var boneCount = Math.Min(skel.BoneCount, skel.ParentCount);
        var positions = new Vector3[boneCount];
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < boneCount; i++)
        {
            ref var mt = ref skel.Pose->ModelPose.Data[i];
            var world = NpcModelToWorld(new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z), skelPos, skelRot);
            positions[i] = world;
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        var nearest = new float[boneCount];
        Array.Fill(nearest, float.MaxValue);
        for (int i = 0; i < boneCount; i++)
        {
            for (int j = i + 1; j < boneCount; j++)
            {
                var d = Vector3.Distance(positions[i], positions[j]);
                if (d <= 0.001f)
                    continue;
                if (d < nearest[i]) nearest[i] = d;
                if (d < nearest[j]) nearest[j] = d;
            }
        }

        var extents = max - min;
        var skeletonRadius = Math.Clamp(MathF.Max(extents.X, extents.Z) * 0.08f, NpcAutoMinRadius, NpcAutoMaxRadius);
        return new NpcAutoCollisionContext
        {
            SkeletonRadius = skeletonRadius,
            NearestBoneDistances = nearest,
        };
    }

    private static float EstimateNpcCollisionRadius(
        int boneIndex,
        int parentIndex,
        float segmentLength,
        Dictionary<int, float>? profileRadii,
        NpcAutoCollisionContext context)
    {
        if (profileRadii != null && profileRadii.TryGetValue(boneIndex, out var profileRadius))
            return Math.Clamp(MathF.Min(profileRadius, segmentLength * 0.45f), NpcAutoMinRadius, NpcAutoMaxRadius);

        var childNearest = boneIndex >= 0 && boneIndex < context.NearestBoneDistances.Length
            ? context.NearestBoneDistances[boneIndex]
            : float.MaxValue;
        var parentNearest = parentIndex >= 0 && parentIndex < context.NearestBoneDistances.Length
            ? context.NearestBoneDistances[parentIndex]
            : float.MaxValue;
        var nearest = MathF.Min(childNearest, parentNearest);
        var densityRadius = float.IsFinite(nearest) ? nearest * 0.42f : context.SkeletonRadius;
        var segmentRadius = segmentLength * 0.22f;
        var radius = MathF.Max(context.SkeletonRadius, MathF.Max(densityRadius, segmentRadius));
        return Math.Clamp(MathF.Min(radius, segmentLength * 0.7f), NpcAutoMinRadius, NpcAutoMaxRadius);
    }

    /// <summary>
    /// Build a single convex hull collision static for a non-humanoid character (mount,
    /// monster). Collects all bone MODEL-space positions as input points, constructs a
    /// <see cref="ConvexHull"/> shape, and stores the hull centroid so the per-frame
    /// update can reposition the static by transforming only the centroid — no shape
    /// rebuild required.
    /// </summary>
    private void BuildConvexHullCollision(nint address, SkeletonAccess ns, Vector3 npcSkelPos, Quaternion npcSkelRot, string label)
    {
        if (simulation == null || bufferPool == null) return;

        var boneCount = Math.Min(ns.BoneCount, ns.ParentCount);
        if (boneCount < 4)
        {
            log.Info($"RagdollController: {label} has only {boneCount} bones — too few for convex hull, using fallback");
            CreateFallbackCharacterCollision(label, address);
            return;
        }

        bufferPool.Take<System.Numerics.Vector3>(boneCount, out var points);
        try
        {
            for (int i = 0; i < boneCount; i++)
            {
                ref var mt = ref ns.Pose->ModelPose.Data[i];
                points[i] = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            }

            var hull = new BepuPhysics.Collidables.ConvexHull(points, bufferPool, out var hullCenter);
            var shapeIndex = simulation.Shapes.Add(hull);

            // The hull's local origin is at hullCenter (model space). Transform to world.
            var worldCenter = npcSkelPos + Vector3.Transform(hullCenter, npcSkelRot);
            var staticHandle = simulation.Statics.Add(new StaticDescription(worldCenter, npcSkelRot, shapeIndex));

            npcCollisionStates.Add(new NpcCollisionState
            {
                NpcAddress = address,
                BoneStatics = new List<NpcBoneStatic>(),
                IsFallback = false,
                IsConvexHull = true,
                ConvexHullHandle = staticHandle,
                HullCenterModelSpace = hullCenter,
            });

            log.Info($"RagdollController: {label} convex hull collision — {boneCount} points, center=({hullCenter.X:F3},{hullCenter.Y:F3},{hullCenter.Z:F3})");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"RagdollController: {label} convex hull build failed, using fallback");
            CreateFallbackCharacterCollision(label, address);
        }
        finally
        {
            bufferPool.Return(ref points);
        }
    }

    private void CreateFallbackCharacterCollision(string label, nint address)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        var npcPos = new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z);
        var handle = simulation!.Statics.Add(new StaticDescription(
            npcPos, Quaternion.Identity, npcFallbackShapeIndex));
        npcCollisionStates.Add(new NpcCollisionState
        {
            NpcAddress = address,
            BoneStatics = new List<NpcBoneStatic>(),
            FallbackHandle = handle,
            IsFallback = true,
        });
        log.Info($"RagdollController: {label} using fallback single capsule");
    }

    /// <summary>Move all of a character's collision statics to a far-away park position and
    /// refresh their broad-phase bounds, so they stop colliding with the ragdoll.</summary>
    private void ParkNpcStatics(NpcCollisionState npcState, Vector3 parkPos)
    {
        if (simulation == null) return;
        if (npcState.IsConvexHull)
        {
            var s = simulation.Statics.GetStaticReference(npcState.ConvexHullHandle);
            s.Pose.Position = parkPos;
            s.UpdateBounds();
            return;
        }
        if (npcState.IsFallback)
        {
            var s = simulation.Statics.GetStaticReference(npcState.FallbackHandle);
            s.Pose.Position = parkPos;
            s.UpdateBounds();
            return;
        }
        for (int b = 0; b < npcState.BoneStatics.Count; b++)
        {
            var s = simulation.Statics.GetStaticReference(npcState.BoneStatics[b].Handle);
            s.Pose.Position = parkPos;
            s.UpdateBounds();
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
            if (rb.SegmentHalfLength > 0)
            {
                var y = Vector3.Transform(Vector3.UnitY, b.Pose.Orientation);
                prevWorldPositions![i] = b.Pose.Position - rb.SegmentHalfLength * y;
            }
            else
            {
                prevWorldPositions![i] = b.Pose.Position;
            }
        }
    }

    private void StepAndApply(float dt)
    {
        if (simulation == null) return;

        // Liveness guard: if the corpse despawned and its object-table slot was reused, the
        // address now points at an unrelated character with a different EntityId. Without this,
        // we would freeze that newcomer's animation and overwrite its bones with our ragdoll
        // pose. Deactivate cleanly instead (restores nothing — the original target is gone).
        if (targetCharacterAddress == nint.Zero ||
            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress)->EntityId != targetEntityId)
        {
            log.Info("RagdollController: target despawned or slot reused; deactivating.");
            animationFrozen = false; // do not write OverallSpeed onto whatever now occupies the slot
            Deactivate();
            return;
        }

        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;

        // The draw object can be rebuilt mid-ragdoll (gear/glamour change) with a different
        // bone count. Our stored bone indices and the resting/awake sampling would then be
        // stale (and resting could latch true forever on a now-empty rig). Bail cleanly.
        if (skel.BoneCount != initialBoneCount)
        {
            log.Info($"RagdollController: skeleton bone count changed ({initialBoneCount}->{skel.BoneCount}); deactivating.");
            Deactivate();
            return;
        }

        // Keep animation frozen (game may recalculate OverallSpeed each frame)
        var character = (Character*)targetCharacterAddress;
        character->Timeline.OverallSpeed = 0f;

        // Update skeleton transform for WorldToModel conversion.
        // The game may reposition the character root (e.g., dismount, death transition).
        // Physics bodies stay at correct world positions; we just need the current
        // transform to convert back to the model space the game expects for rendering.
        var skeleton = skel.CharBase->Skeleton;
        var skeletonMoved = false;
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
                skeletonMoved = true;
                if (config.RagdollVerboseLog)
                    log.Info($"[Ragdoll F{frameCount}] Skeleton moved {skelDist:F3}m: ({skelWorldPos.X:F3},{skelWorldPos.Y:F3},{skelWorldPos.Z:F3})→({newSkelPos.X:F3},{newSkelPos.Y:F3},{newSkelPos.Z:F3})");
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(newSkelPos.X, newSkelPos.Y + TerrainRaycastStartYOffset, newSkelPos.Z),
                        new Vector3(0, -1, 0),
                        out var hitInfo,
                        TerrainRaycastDistance))
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

        if (skeletonMoved)
            BeginBiomechanicalSettle();

        var biomechanicalSettleActive = biomechanicalSettleRemaining > 0f;
        if (biomechanicalSettleActive)
            WakeRagdollBodiesForBiomechanicalSettle();

        // Resting fast-path: once the rig is fully asleep there is nothing left to react
        // to (bodies only sleep when settle-collision is off, and an asleep body has ~0
        // velocity so there is no energy left to pump), so skip the physics step, the
        // per-frame NPC-collision static rebuild, and hair — we just re-assert the held
        // pose below. Applies to generic (monster) rigs too: if their auto-built network
        // never settles, prevAllAsleep simply stays false and this never triggers; if it
        // does settle, there is nothing to clamp. A grab or a moved skeleton forces a step.
        var resting = prevAllAsleep && !grabConstraintActive && !skeletonMoved && !biomechanicalSettleActive;

        // Update NPC collision volumes to track their current animated bone positions.
        // Must call UpdateBounds() after repositioning — BEPU2 doesn't auto-update
        // broad phase AABBs for statics, so without it collisions are never detected.
        // The grabbing NPC's collision is parked far away so the player body can pass through.
        var npcCollisionParkPos = new Vector3(0, -9999, 0);
        if (!resting)
        for (int i = 0; i < npcCollisionStates.Count; i++)
        {
            var npcState = npcCollisionStates[i];
            try
            {
                if (suspendedNpcAddress != nint.Zero && npcState.NpcAddress == suspendedNpcAddress)
                {
                    if (npcState.IsConvexHull)
                    {
                        var staticRef = simulation.Statics.GetStaticReference(npcState.ConvexHullHandle);
                        staticRef.Pose.Position = npcCollisionParkPos;
                        staticRef.UpdateBounds();
                    }
                    else if (npcState.IsFallback)
                    {
                        var staticRef = simulation.Statics.GetStaticReference(npcState.FallbackHandle);
                        staticRef.Pose.Position = npcCollisionParkPos;
                        staticRef.UpdateBounds();
                    }
                    else
                    {
                        for (int b = 0; b < npcState.BoneStatics.Count; b++)
                        {
                            var staticRef = simulation.Statics.GetStaticReference(npcState.BoneStatics[b].Handle);
                            staticRef.Pose.Position = npcCollisionParkPos;
                            staticRef.UpdateBounds();
                        }
                    }
                    continue;
                }

                // Distance gate: a character whose root is far from the corpse cannot
                // contact it, so skip the expensive skeleton read + per-bone reposition.
                var npcGo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npcState.NpcAddress;
                var npcRootPos = new Vector3(npcGo->Position.X, npcGo->Position.Y, npcGo->Position.Z);
                if ((npcRootPos - skelWorldPos).LengthSquared() > NpcCollisionUpdateRadius * NpcCollisionUpdateRadius)
                {
                    // Park its capsules far below ONCE on exit so a character that walks away
                    // from the corpse doesn't leave its body-shaped statics frozen on top of
                    // the ragdoll (invisible "ghost" collision). Skip cheaply thereafter.
                    if (!npcState.Parked)
                    {
                        ParkNpcStatics(npcState, npcCollisionParkPos);
                        npcState.Parked = true;
                        npcCollisionStates[i] = npcState;
                    }
                    continue;
                }
                if (npcState.Parked)
                {
                    // Back in range — the reposition below snaps the statics onto the skeleton.
                    npcState.Parked = false;
                    npcCollisionStates[i] = npcState;
                }

                if (npcState.IsConvexHull)
                {
                    // Convex hull update: transform the stored model-space centroid by the
                    // current skeleton transform — no shape rebuild, just a pose update.
                    var npcSkelHull = boneService.TryGetSkeleton(npcState.NpcAddress);
                    Vector3 hullWorldPos;
                    Quaternion hullWorldRot;
                    if (npcSkelHull != null && npcSkelHull.Value.CharBase->Skeleton != null)
                    {
                        var sk = npcSkelHull.Value.CharBase->Skeleton;
                        var sp = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
                        var sr = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);
                        hullWorldPos = sp + Vector3.Transform(npcState.HullCenterModelSpace, sr);
                        hullWorldRot = sr;
                    }
                    else
                    {
                        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npcState.NpcAddress;
                        hullWorldPos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
                        hullWorldRot = Quaternion.Identity;
                    }
                    var hullRef = simulation.Statics.GetStaticReference(npcState.ConvexHullHandle);
                    hullRef.Pose.Position = hullWorldPos;
                    hullRef.Pose.Orientation = hullWorldRot;
                    hullRef.UpdateBounds();
                    continue;
                }

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

                var doStrike = attackStrikeTimer > 0f && npcState.NpcAddress == strikeColliderAddress;

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
                        capsuleCenter = parentWorldPos + (segLen * bs.CenterFactor) * segDir;
                        capsuleRot = RotationFromYToDirection(segment);
                    }
                    else
                    {
                        capsuleCenter = parentWorldPos;
                        capsuleRot = Quaternion.Identity;
                    }

                    var staticRef = simulation.Statics.GetStaticReference(bs.Handle);
                    var oldPos = staticRef.Pose.Position;
                    staticRef.Pose.Position = capsuleCenter;
                    staticRef.Pose.Orientation = capsuleRot;
                    staticRef.UpdateBounds();

                    // Monster strike: this collider bone moved from oldPos→capsuleCenter this frame.
                    // During the attack window, impart that swing velocity to nearby ragdoll bodies,
                    // so a fast-swinging limb forcefully flings the body at the real contact point.
                    if (doStrike && dt > 0f)
                    {
                        var swingVel = (capsuleCenter - oldPos) / dt;
                        if (swingVel.LengthSquared() > StrikeMinSpeed * StrikeMinSpeed)
                            ApplyStrikeImpulse(capsuleCenter, swingVel, StrikeContactRadius);
                    }
                }
            }
            catch { }
        }

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

        // Step physics with a fixed timestep, advancing as many substeps as real
        // wall-clock time has accumulated. This keeps ragdoll motion the same speed
        // regardless of the game's framerate (a fixed 1/60 per render frame ran slow
        // below 60fps and fast above it). Below 60fps we take multiple substeps; above
        // 60fps some frames take zero — render interpolation (below) keeps motion smooth.
        // When resting (fully settled) we skip stepping entirely.
        int substeps = 0;
        if (!resting)
        {
            physicsAccumulator += dt;
            var maxLinear = activeRagdollIsGeneric ? GenericMaxLinearVelocity : HumanMaxLinearVelocity;
            var maxAngular = activeRagdollIsGeneric ? GenericMaxAngularVelocity : HumanMaxAngularVelocity;
            while (physicsAccumulator >= FixedTimestep && substeps < MaxSubstepsPerFrame)
            {
                // Snapshot the pre-tick bone pose for render interpolation. Overwritten each
                // iteration, so it ends as the state immediately before the final tick.
                CapturePrevBodyPoses(boneCount);
                hasPrevPhysicsState = true;
                simulation.Timestep(FixedTimestep);
                ClampVelocities(maxLinear, maxAngular);
                physicsAccumulator -= FixedTimestep;
                substeps++;
            }
            // If we hit the substep cap (severe hitch / very low fps), drop the backlog so
            // we don't fire a burst of catch-up steps on the next frame (spiral of death).
            if (substeps == MaxSubstepsPerFrame)
                physicsAccumulator = 0f;
        }
        else
        {
            physicsAccumulator = 0f;
        }

        if (biomechanicalSettleRemaining > 0f)
            biomechanicalSettleRemaining = MathF.Max(0f, biomechanicalSettleRemaining - dt);

        // Fraction of a tick elapsed past the last completed tick. The write-back blends the
        // current physics pose toward it from the previous tick's pose, so the rendered ragdoll
        // advances a little every frame instead of snapping once per 60Hz tick. Resting or no
        // prior tick → render the current pose directly (alpha = 1).
        var renderAlpha = (!resting && hasPrevPhysicsState)
            ? Math.Clamp(physicsAccumulator / FixedTimestep, 0f, 1f)
            : 1f;

        var pose = skel.Pose;

        // Save original positions/rotations for delta tracking (needed for j_kao propagation).
        // Reuse a cached result across frames — StepAndApply runs every render frame, so a
        // fresh allocation here is steady GC pressure per active ragdoll.
        var result = cachedResult ??= new BoneModificationResult(skel.BoneCount);
        result.Reset(skel.BoneCount);
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

            boneValid[i] = true;
        }

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
                var capsuleBottomY = bodyRef.Pose.Position.Y
                                     - MathF.Abs(capsuleYDir.Y) * boneDef.CapsuleHalfLength
                                     - boneDef.CapsuleRadius;
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
        if (kaoBodyBoneIndex >= 0 && result.HasAccumulated[kaoBodyBoneIndex])
        {
            boneService.PropagateToPartialSkeletons(skel, kaoBodyBoneIndex, "j_kao", result);
        }

        // Apply hair physics (after rigid j_kao propagation). Skipped while resting —
        // the body is settled, so hair has settled too.
        if (hairPhysics != null && kaoBodyBoneIndex >= 0 && !resting)
        {
            // Advance hair by exactly the time the body physics advanced this frame,
            // so hair stays in step with the ragdoll across framerates (zero if no substep ran).
            hairPhysics.StepAndApply(
                skel.CharBase, kaoBodyBoneIndex,
                skelWorldPos, skelWorldRot, skelWorldRotInv,
                substeps * FixedTimestep);
        }

        // (Dev) Update GameObject.Position to follow ragdoll root bone.
        // Prevents model unload when ragdoll falls far from the frozen position.
        if (config.RagdollFollowPosition && movementBlockHook != null && ragdollBones.Count > 0 && targetCharacterAddress != nint.Zero)
        {
            try
            {
                var rootBody = simulation.Bodies.GetBodyReference(ragdollBones[0].BodyHandle);
                var rootPos = rootBody.Pose.Position;
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
                movementBlockHook.SetApproachPosition(gameObj, rootPos.X, rootPos.Y, rootPos.Z);
            }
            catch { }
            followWasActive = true;
        }
        else if (followWasActive && movementBlockHook != null && targetCharacterAddress != nint.Zero)
        {
            // Toggled off — restore original position
            try
            {
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
                movementBlockHook.SetApproachPosition(gameObj,
                    savedCharacterPosition.X, savedCharacterPosition.Y, savedCharacterPosition.Z);
            }
            catch { }
            followWasActive = false;
        }
    }

    // --- Grab constraint API (for cinematic victory sequence) ---
    private ConstraintHandle grabConstraintHandle;
    private bool grabConstraintActive;
    private BodyHandle grabBodyHandle;
    // Servo/spring tuning captured at CreateGrabConstraint and reused by every
    // UpdateGrabTarget — otherwise the per-frame update would overwrite the caller's
    // configured force/speed/stiffness with hardcoded defaults after a single frame.
    private ServoSettings grabServoSettings;
    private SpringSettings grabSpringSettings;
    // Address of the grabbing NPC whose collision is parked during grab (0 = none)
    private nint suspendedNpcAddress;

    /// <summary>
    /// Create a OneBodyLinearServo constraint that pins a ragdoll bone to a world-space
    /// target position. The target is updated each frame via UpdateGrabTarget().
    /// Also ensures all ragdoll bodies stay awake (SleepThreshold = -1).
    /// </summary>
    public bool CreateGrabConstraint(string boneName, Vector3 initialTarget, nint grabbingNpcAddress = 0, float maxForce = 1000f, float maxSpeed = 50f, float springFreq = 120f)
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

        // Remember the tuning so UpdateGrabTarget reuses it instead of resetting to defaults.
        grabServoSettings = new ServoSettings(maxSpeed, 1f, maxForce);
        grabSpringSettings = new SpringSettings(springFreq, 1);

        // Create OneBodyLinearServo: pins a body to a world-space target
        grabConstraintHandle = simulation.Solver.Add(grabBodyHandle,
            new OneBodyLinearServo
            {
                LocalOffset = Vector3.Zero,
                Target = initialTarget,
                ServoSettings = grabServoSettings,
                SpringSettings = grabSpringSettings,
            });

        grabConstraintActive = true;
        suspendedNpcAddress = grabbingNpcAddress;

        log.Info($"RagdollController: Grab constraint created on '{boneName}' → ({initialTarget.X:F2},{initialTarget.Y:F2},{initialTarget.Z:F2}), suspend NPC 0x{grabbingNpcAddress:X}");
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
                ServoSettings = grabServoSettings,
                SpringSettings = grabSpringSettings,
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
                bodyRef.Awake = true;
            }
            catch { }
        }

        grabConstraintActive = false;
        suspendedNpcAddress = nint.Zero;
        BeginBiomechanicalSettle();

        log.Info("RagdollController: Grab constraint removed");
    }



    // --- Standing support constraint API (execution mode) ---
    // Pelvis: LinearServo to a computed standing height + AngularServo upright.
    // Spine chain: progressively weaker AngularServos.
    // Legs/arms/head: fully dynamic — gravity + joints handle them naturally.
    private readonly List<ConstraintHandle> standingConstraints = new();
    private bool standingActive;

    private static readonly string[] StandingSpineBones = { "j_sebo_a", "j_sebo_b", "j_sebo_c" };

    public bool CreateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = "j_kosi")
    {
        if (simulation == null || !isActive) return false;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
            bodyRef.Activity.SleepThreshold = -1f;
            bodyRef.Awake = true;
        }
        BeginBiomechanicalSettle();

        BuildStandingConstraints(anchorWorldPos, uprightRot, anchorBoneName);
        standingActive = true;
        log.Info($"RagdollController: Standing support created — {standingConstraints.Count} constraints, anchor={anchorBoneName} ({anchorWorldPos.X:F2},{anchorWorldPos.Y:F2},{anchorWorldPos.Z:F2})");
        return true;
    }

    /// <summary>Swap the anchor bone/position while the support is already active, without re-waking bodies.</summary>
    public bool UpdateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = "j_kosi")
    {
        if (!standingActive || simulation == null) return false;

        foreach (var h in standingConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        standingConstraints.Clear();

        BuildStandingConstraints(anchorWorldPos, uprightRot, anchorBoneName);
        return true;
    }

    private void BuildStandingConstraints(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName)
    {
        var anchorHandle = FindBodyHandle(anchorBoneName);
        if (anchorHandle.HasValue)
        {
            standingConstraints.Add(simulation.Solver.Add(anchorHandle.Value,
                new OneBodyLinearServo
                {
                    LocalOffset    = Vector3.Zero,
                    Target         = anchorWorldPos,
                    ServoSettings  = new ServoSettings(8f, 1f, 3000f),
                    SpringSettings = new SpringSettings(120f, 1f),
                }));

            standingConstraints.Add(simulation.Solver.Add(anchorHandle.Value,
                new OneBodyAngularServo
                {
                    TargetOrientation = uprightRot,
                    ServoSettings     = new ServoSettings(8f, 1f, 600f),
                    SpringSettings    = new SpringSettings(40f, 1f),
                }));
        }

        float spineForce = 350f;
        float spineFreq  = 25f;
        foreach (var name in StandingSpineBones)
        {
            var h = FindBodyHandle(name);
            if (h.HasValue)
            {
                standingConstraints.Add(simulation.Solver.Add(h.Value,
                    new OneBodyAngularServo
                    {
                        TargetOrientation = uprightRot,
                        ServoSettings     = new ServoSettings(10f, 1f, spineForce),
                        SpringSettings    = new SpringSettings(spineFreq, 1f),
                    }));
            }
            spineForce *= 0.7f;
            spineFreq  *= 0.8f;
        }
    }

    public void RemoveStandingSupport()
    {
        if (!standingActive || simulation == null) return;

        foreach (var h in standingConstraints)
        {
            try { simulation.Solver.Remove(h); } catch { }
        }
        standingConstraints.Clear();

        var normalThreshold = (config.RagdollNpcSettleCollision && config.RagdollNpcCollision) ? -1f : 0.01f;
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

        standingActive = false;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: Standing support removed");
    }

    // ─── Wrist constraints ───────────────────────────────────────────────────

    private readonly List<ConstraintHandle> wristConstraints = new();
    private float wristForce = 500f;
    private float wristFreq  = 80f;

    public bool CreateWristConstraints(Vector3 leftTarget, Vector3 rightTarget,
        float force = 500f, float freq = 80f)
    {
        if (simulation == null || !isActive) return false;

        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();

        wristForce = force;
        wristFreq  = freq;
        BuildWristConstraint("j_te_l", leftTarget);
        BuildWristConstraint("j_te_r", rightTarget);
        BeginBiomechanicalSettle();
        return wristConstraints.Count > 0;
    }

    public bool UpdateWristConstraints(Vector3 leftTarget, Vector3 rightTarget)
    {
        if (simulation == null || wristConstraints.Count == 0) return false;

        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();

        BuildWristConstraint("j_te_l", leftTarget);
        BuildWristConstraint("j_te_r", rightTarget);
        return true;
    }

    public void RemoveWristConstraints()
    {
        if (simulation == null) return;
        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();
    }

    private void BuildWristConstraint(string boneName, Vector3 target)
    {
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        wristConstraints.Add(simulation!.Solver.Add(handle.Value,
            new OneBodyLinearServo
            {
                LocalOffset    = Vector3.Zero,
                Target         = target,
                ServoSettings  = new ServoSettings(8f, 1f, wristForce),
                SpringSettings = new SpringSettings(wristFreq, 1f),
            }));
    }

    // ─── Direct impulse ──────────────────────────────────────────────────────

    public void ApplyImpulse(string boneName, Vector3 velocityDelta)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Linear += velocityDelta;
        body.Awake = true;
        BeginBiomechanicalSettle();
    }

    /// <summary>
    /// Punt the ragdoll body nearest to <paramref name="from"/> within <paramref name="maxDist"/>.
    /// Uses the full set of ragdoll bodies (the body "point cloud") as the hit volume rather than a
    /// single named bone, so contact anywhere on the body registers. The impulse points away from
    /// <paramref name="from"/> horizontally plus an upward kick. Returns true if a body was hit.
    /// </summary>
    public bool PuntNearest(Vector3 from, float maxDist, float strength, float upBias = 0.5f)
    {
        if (simulation == null || !isActive) return false;

        BodyHandle? best = null;
        var bestDistSq = maxDist * maxDist;
        var bestPos = Vector3.Zero;
        foreach (var rb in ragdollBones)
        {
            var pos = simulation.Bodies.GetBodyReference(rb.BodyHandle).Pose.Position;
            var dSq = Vector3.DistanceSquared(pos, from);
            if (dSq <= bestDistSq) { bestDistSq = dSq; best = rb.BodyHandle; bestPos = pos; }
        }
        if (!best.HasValue) return false;

        var horiz = new Vector3(bestPos.X - from.X, 0f, bestPos.Z - from.Z);
        var dir = horiz.LengthSquared() > 0.0001f ? Vector3.Normalize(horiz) : Vector3.UnitX;
        var impulse = dir * strength + Vector3.UnitY * (strength * upBias);

        // A fully settled corpse is asleep. Setting velocity on a sleeping body before it is
        // activated doesn't stick, and a single woken body anchored to a sleeping constraint
        // island won't move. Wake the whole ragdoll first, THEN apply the impulse.
        WakeRagdollBodiesForBiomechanicalSettle();
        var body = simulation.Bodies.GetBodyReference(best.Value);
        body.Awake = true;
        body.Velocity.Linear += impulse;
        BeginBiomechanicalSettle();
        return true;
    }

    // Monster strike tuning: a collider bone must move faster than this to register a hit, and only
    // ragdoll bodies within this radius of the bone are hit.
    private const float StrikeMinSpeed = 1.5f;      // m/s
    private const float StrikeContactRadius = 0.6f; // m

    // A strike knocks the body AWAY from the contact point (outward + a little up), with strength
    // from the swing SPEED — using the raw swing velocity would drag the body along the arc (toward
    // the attacker on the retract phase).
    private const float StrikeUpBias = 0.3f;

    private Vector3 StrikeImpulse(Vector3 bodyPos, Vector3 contactPoint, float swingSpeed)
    {
        var away = new Vector3(bodyPos.X - contactPoint.X, 0f, bodyPos.Z - contactPoint.Z);
        var dir = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitX;
        return (dir + Vector3.UnitY * StrikeUpBias) * (swingSpeed * strikePower);
    }

    /// <summary>Impart a swing as an impulse to ragdoll bodies near <paramref name="point"/>.</summary>
    private void ApplyStrikeImpulse(Vector3 point, Vector3 swingVel, float radius)
    {
        if (simulation == null) return;
        var speed = swingVel.Length();
        var radiusSq = radius * radius;
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var p = bodyRef.Pose.Position;
            if (Vector3.DistanceSquared(p, point) > radiusSq) continue;
            if (!struckThisWindow.Add(rb.BodyHandle.Value)) continue; // one hit per swing
            bodyRef.Velocity.Linear += StrikeImpulse(p, point, speed);
            bodyRef.Awake = true;
        }
    }

    private const float WeaponStrikeRadius = 0.55f; // generous around the blade line (weapons are thin)

    /// <summary>
    /// A humanoid's weapon is a separate draw object, so it isn't in the bone collision capsules.
    /// Treat the blade as a line segment (its world transform lives on the weapon's own skeleton root
    /// — DrawObject.Position alone doesn't move the mesh) and, during the attack window, impart its
    /// swing velocity to ragdoll bodies near the blade. No-op for unarmed/creature models.
    /// </summary>
    private void UpdateWeaponStrike(float dt)
    {
        if (simulation == null || strikeColliderAddress == nint.Zero) { weaponPrevValid = false; return; }
        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)strikeColliderAddress;
        if (gameObj->DrawObject == null) { weaponPrevValid = false; return; }
        var character = (Character*)strikeColliderAddress;
        var weaponDraw = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject;
        if (weaponDraw == null) { weaponPrevValid = false; return; }

        var sk = ((CharacterBase*)weaponDraw)->Skeleton;
        if (sk == null) { weaponPrevValid = false; return; }
        var center = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
        var rot = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);
        // Weapon-drop models the weapon as a Y-aligned capsule, so the blade runs along local Y.
        // Sample both ends so the blade direction's sign doesn't matter.
        var dir = Vector3.Transform(Vector3.UnitY, rot);
        var half = MathF.Max(0.4f, config.WeaponDropHalfLength);
        var a = center - dir * half;
        var b = center + dir * half;

        if (attackStrikeTimer > 0f && weaponPrevValid && dt > 0f)
        {
            var vel = ((a - prevBladeA) + (b - prevBladeB)) * (0.5f / dt);
            if (vel.LengthSquared() > StrikeMinSpeed * StrikeMinSpeed)
                ApplyStrikeImpulseSegment(a, b, vel, WeaponStrikeRadius);
        }
        prevBladeA = a;
        prevBladeB = b;
        weaponPrevValid = true;
    }

    /// <summary>Strike ragdoll bodies near the blade line segment a→b.</summary>
    private void ApplyStrikeImpulseSegment(Vector3 a, Vector3 b, Vector3 swingVel, float radius)
    {
        if (simulation == null) return;
        var speed = swingVel.Length();
        var ab = b - a;
        var abLenSq = ab.LengthSquared();
        var radiusSq = radius * radius;
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var p = bodyRef.Pose.Position;
            var t = abLenSq > 1e-6f ? Math.Clamp(Vector3.Dot(p - a, ab) / abLenSq, 0f, 1f) : 0f;
            var closest = a + ab * t;
            if (Vector3.DistanceSquared(p, closest) > radiusSq) continue;
            if (!struckThisWindow.Add(rb.BodyHandle.Value)) continue; // one hit per swing
            bodyRef.Velocity.Linear += StrikeImpulse(p, closest, speed);
            bodyRef.Awake = true;
        }
    }

    /// <summary>Distance from <paramref name="from"/> to the nearest ragdoll body, or null if none.</summary>
    public float? NearestBodyDistance(Vector3 from)
    {
        if (simulation == null || !isActive) return null;
        float? best = null;
        foreach (var rb in ragdollBones)
        {
            var pos = simulation.Bodies.GetBodyReference(rb.BodyHandle).Pose.Position;
            var d = Vector3.Distance(pos, from);
            if (best == null || d < best.Value) best = d;
        }
        return best;
    }

    /// <summary>
    /// Register a live character as a moving collider in the ragdoll simulation AFTER activation
    /// (the per-bone collision built at init only captures party/NPC actors that existed then).
    /// Used by Monster mode so the creature physically pushes the ragdoll when it walks into it,
    /// not just on an explicit attack. Returns false if the sim isn't ready yet — callers should
    /// retry until it succeeds. Pair with <see cref="RemoveLiveCollider"/> before the actor despawns.
    /// </summary>
    public bool AddLiveCollider(nint address)
    {
        if (simulation == null || !isActive || !physicsStarted) return false;
        if (address == nint.Zero || address == targetCharacterAddress) return false;
        // Already a collision actor (e.g. the controlled killer was a selected NPC captured when the
        // ragdoll activated) — its bone capsules already track it; just mark it as the strike collider
        // so the bone/weapon strike activates. Without this, the killer's strike never fired.
        foreach (var s in npcCollisionStates)
            if (s.NpcAddress == address) { strikeColliderAddress = address; return true; }
        // Need a readable skeleton so we take the bone-capsule (or hull) path, not the fallback.
        if (boneService.TryGetSkeleton(address) == null) return false;

        // The fallback shape is only created at init when NPC collision was enabled — ensure it
        // exists in case BuildCharacterCollision falls back.
        if (!npcFallbackShapeReady)
        {
            npcFallbackShapeIndex = simulation.Shapes.Add(new Capsule(0.35f, 1.2f));
            npcFallbackShapeReady = true;
        }

        var scale = config.RagdollNpcCollisionScale;
        var capsuleRadius = config.RagdollNpcCollisionAutoSize ? NpcDefaultBoneRadius : NpcDefaultBoneRadius * scale;
        var before = npcCollisionStates.Count;
        BuildCharacterCollision(address, "monster", scale, capsuleRadius);
        if (npcCollisionStates.Count <= before) return false;
        strikeColliderAddress = address; // this collider's bones drive the attack strike
        return true;
    }

    /// <summary>Remove a live collider registered via <see cref="AddLiveCollider"/> (parks its
    /// statics far away and drops the per-frame tracking entry so a despawned actor isn't read).</summary>
    public void RemoveLiveCollider(nint address)
    {
        if (simulation == null) return;
        if (strikeColliderAddress == address) strikeColliderAddress = nint.Zero;
        for (int i = npcCollisionStates.Count - 1; i >= 0; i--)
        {
            if (npcCollisionStates[i].NpcAddress != address) continue;
            ParkNpcStatics(npcCollisionStates[i], new Vector3(0, -9999, 0));
            npcCollisionStates.RemoveAt(i);
        }
    }

    /// <summary>
    /// Open a brief strike window: while it's open, the monster's collider bones impart their
    /// swing velocity (× <paramref name="power"/>) as an impulse to nearby ragdoll bodies, so the
    /// attack flings the body where the limb actually lands. Auto-closes after <paramref name="duration"/>s.
    /// </summary>
    public void BeginAttackStrike(float duration, float power)
    {
        strikePower = power;
        attackStrikeTimer = MathF.Max(attackStrikeTimer, duration);
        struckThisWindow.Clear(); // a fresh swing may hit each body again
        WakeRagdollBodiesForBiomechanicalSettle();
        BeginBiomechanicalSettle();
    }

    private static readonly System.Random ShakeRng = new();

    public void ApplyShake(float intensity)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle("j_kosi");
        if (!handle.HasValue) return;
        var angle = (float)(ShakeRng.NextDouble() * MathF.PI * 2f);
        var impulse = new Vector3(MathF.Cos(angle) * intensity, 0f, MathF.Sin(angle) * intensity);
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Linear += impulse;
        body.Awake = true;
        BeginBiomechanicalSettle();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private BodyHandle? FindBodyHandle(string boneName)
    {
        foreach (var rb in ragdollBones)
            if (rb.Name == boneName) return rb.BodyHandle;
        return null;
    }

    private void DestroySimulation()
    {
        grabConstraintActive = false;
        suspendedNpcAddress = nint.Zero;
        standingActive = false;
        standingConstraints.Clear();
        wristConstraints.Clear();
        biomechanicalSettleRemaining = 0f;
        ragdollBones.Clear();
        npcCollisionStates.Clear();
        npcFallbackShapeReady = false;
        attackStrikeTimer = 0f;
        strikePower = 0f;
        strikeColliderAddress = nint.Zero;
        weaponPrevValid = false;
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
    public float Friction;

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
        pairMaterial.FrictionCoefficient = Friction;
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
