using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation.CollapseProfiles;
using CombatSimulator.Core;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;
using HkQsTransform = FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf;
using Lumina.Data.Files;
using Lumina.Data.Parsing;

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
    private int externalBodyGeneration;

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

    // Animation→physics handoff velocity sampling. During the activation-delay countdown the
    // death animation drives the bones; we snapshot each frame's model-space bone poses so that
    // at handoff InitializePhysics can finite-difference the last animated frame and seed the
    // physics bodies with the animation's velocity (instead of starting at rest). Stored by bone
    // INDEX to avoid a name→index map during the countdown.
    private bool handoffSampleValid;
    private float handoffSampleDt;
    private Vector3 handoffSkelPos;
    private Quaternion handoffSkelRot;
    private Vector3[]? handoffPrevPos;
    private Quaternion[]? handoffPrevRot;
    private int handoffPrevCount;
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
    private readonly List<ExternalBodyHandle> externalBodies = new();
    private readonly List<ExternalRigHandle> externalRigs = new();
    private readonly HashSet<int> externalDynamicBodyHandles = new();
    private readonly HashSet<int> externalRigDynamicBodyHandles = new();
    private readonly HashSet<int> externalRigNoRagdollContactBodyHandles = new();
    private readonly HashSet<(int, int)> externalRigConnectedPairs = new();
    // Bodies that must not collide with OTHER bodies in the same rig (a whole garment tube marked non-self-
    // colliding at once — cheaper than enumerating every O(N^2) internal pair as connected). Keyed by body
    // handle -> group id; two dynamics with the same non-zero group skip contact generation.
    private readonly Dictionary<int, int> externalRigSelfCollideGroupByBody = new();
    private int nextExternalRigSelfCollideGroup = 1;
    private readonly HashSet<int> softKinematicBodyHandles = new();

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
    private bool useLocalDismemberBones;
    private readonly List<string> localDismemberBones = new();

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
    public uint TargetEntityId => targetEntityId;

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

    public readonly struct ExternalShapePart
    {
        public readonly Vector3 HalfExtents;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalOrientation;

        public ExternalShapePart(Vector3 halfExtents, Vector3 localPosition, Quaternion localOrientation)
        {
            HalfExtents = halfExtents;
            LocalPosition = localPosition;
            LocalOrientation = localOrientation;
        }
    }

    public sealed class ExternalBodyHandle
    {
        internal BodyHandle Body;
        internal TypedIndex[] Shapes = Array.Empty<TypedIndex>();
        internal int Generation;
        internal bool Removed;
    }

    public readonly struct ExternalRigBodySpec
    {
        public readonly string Name;
        public readonly IReadOnlyList<ExternalShapePart> Parts;
        public readonly float Mass;
        public readonly Vector3 Position;
        public readonly Quaternion Orientation;
        public readonly Vector3 LinearVelocity;
        public readonly Vector3 AngularVelocity;

        public ExternalRigBodySpec(
            string name,
            IReadOnlyList<ExternalShapePart> parts,
            float mass,
            Vector3 position,
            Quaternion orientation,
            Vector3 linearVelocity,
            Vector3 angularVelocity)
        {
            Name = name;
            Parts = parts;
            Mass = mass;
            Position = position;
            Orientation = orientation;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }
    }

    public enum ExternalRigJointKind
    {
        // BallSocket + optional SwingLimit + AngularMotor + optional pose-guide servo (chain garments/limbs).
        BallSocketSwing = 0,
        // A single DistanceLimit between two anchor points: inextensible above MaxDistance, compressible to
        // MinDistance. The edge primitive of the garment tube (ring-adjacent + ring-to-ring + diagonals).
        DistanceLimit = 1,
    }

    public readonly struct ExternalRigJointSpec
    {
        public readonly ExternalRigJointKind Kind;
        public readonly int ParentIndex;
        public readonly int ChildIndex;
        // Ball-socket fields.
        public readonly Vector3 AnchorWorld;
        public readonly float SwingLimit;
        public readonly float InitialSwingFactor;
        public readonly float PoseGuideMaxForce;
        public readonly float PoseGuideFrequency;
        // Distance-limit fields (Kind == DistanceLimit). Offsets are in each body's local frame.
        public readonly Vector3 LocalOffsetChild;
        public readonly Vector3 LocalOffsetParent;
        public readonly float MinDistance;
        public readonly float MaxDistance;
        public readonly float DistanceFrequency;
        public readonly float DistanceDamping;

        public ExternalRigJointSpec(int parentIndex, int childIndex, Vector3 anchorWorld, float swingLimit,
            float initialSwingFactor = 0.28f, float poseGuideMaxForce = 0f, float poseGuideFrequency = 5f)
        {
            Kind = ExternalRigJointKind.BallSocketSwing;
            ParentIndex = parentIndex;
            ChildIndex = childIndex;
            AnchorWorld = anchorWorld;
            SwingLimit = swingLimit;
            InitialSwingFactor = Math.Clamp(initialSwingFactor, 0.05f, 1f);
            PoseGuideMaxForce = MathF.Max(0f, poseGuideMaxForce);
            PoseGuideFrequency = MathF.Max(0.1f, poseGuideFrequency);
            LocalOffsetChild = Vector3.Zero;
            LocalOffsetParent = Vector3.Zero;
            MinDistance = 0f;
            MaxDistance = 0f;
            DistanceFrequency = 0f;
            DistanceDamping = 0f;
        }

        private ExternalRigJointSpec(int parentIndex, int childIndex,
            Vector3 localOffsetChild, Vector3 localOffsetParent,
            float minDistance, float maxDistance, float frequency, float damping)
        {
            Kind = ExternalRigJointKind.DistanceLimit;
            ParentIndex = parentIndex;
            ChildIndex = childIndex;
            AnchorWorld = Vector3.Zero;
            SwingLimit = 0f;
            InitialSwingFactor = 1f;
            PoseGuideMaxForce = 0f;
            PoseGuideFrequency = 5f;
            LocalOffsetChild = localOffsetChild;
            LocalOffsetParent = localOffsetParent;
            MinDistance = MathF.Max(0f, minDistance);
            MaxDistance = MathF.Max(MinDistance, maxDistance);
            DistanceFrequency = MathF.Max(0.1f, frequency);
            DistanceDamping = Math.Clamp(damping, 0f, 8f);
        }

        /// <summary>A center-to-center (or offset) distance limit edge, e.g. a garment tube seam.</summary>
        public static ExternalRigJointSpec MakeDistanceLimit(int childIndex, int parentIndex,
            float minDistance, float maxDistance, float frequency = 15f, float damping = 1.5f)
            => new(parentIndex, childIndex, Vector3.Zero, Vector3.Zero, minDistance, maxDistance, frequency, damping);
    }

    public sealed class ExternalRigHandle
    {
        internal readonly List<ExternalBodyHandle> Bodies = new();
        internal readonly List<ConstraintHandle> Constraints = new();
        internal readonly List<(int, int)> ConnectedPairs = new();
        // Swing-limit constraints that start tight (hold garment shape at spawn) and relax to their full
        // ROM over the first ~second. Kept separately from Constraints so the target ROM can be re-applied
        // each frame without re-scanning the mixed constraint list.
        internal readonly List<RigSwingConstraint> SwingConstraints = new();
        internal readonly List<RigPoseGuideConstraint> PoseGuideConstraints = new();
        internal int Generation;
        internal bool Removed;
    }

    internal struct RigSwingConstraint
    {
        public ConstraintHandle Handle;
        public Vector3 AxisLocalA;
        public Vector3 AxisLocalB;
        public SpringSettings Spring;
        public float TargetSwing;
    }

    internal struct RigPoseGuideConstraint
    {
        public ConstraintHandle Handle;
        public Quaternion TargetRelativeRotationLocalA;
        public float Frequency;
        public float MaxForce;
    }

    /// <summary>Fraction of a garment joint's full swing ROM used at spawn, before it relaxes to full over
    /// ~1s. Shared with the DismembermentController local-sim rig so both hosts spawn equally stiff.</summary>
    public const float GarmentRigInitialSwingFactor = 0.28f;

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

    public bool TryCreateExternalDynamicBody(IReadOnlyList<ExternalShapePart> parts, float mass,
        Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity,
        out ExternalBodyHandle? handle)
    {
        handle = null;
        if (!isActive || simulation == null || bufferPool == null || parts.Count == 0)
            return false;

        try
        {
            var shapeIndex = CreateExternalBodyShape(parts, out var shapes, out var inertia, mass);
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position, Quaternion.Normalize(orientation)),
                default(BodyVelocity),
                inertia,
                new CollidableDescription(shapeIndex, 0.016f),
                new BodyActivityDescription(0.01f)));

            var body = simulation.Bodies.GetBodyReference(bodyHandle);
            body.Velocity.Linear = linearVelocity;
            body.Velocity.Angular = angularVelocity;
            body.Awake = true;
            externalDynamicBodyHandles.Add(bodyHandle.Value);
            WakeRagdollBodiesForBiomechanicalSettle();

            handle = new ExternalBodyHandle
            {
                Body = bodyHandle,
                Shapes = shapes.ToArray(),
                Generation = externalBodyGeneration,
            };
            externalBodies.Add(handle);
            prevAllAsleep = false;
            BeginBiomechanicalSettle(0.35f);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: failed to create external body");
            return false;
        }
    }

    public bool TryCreateExternalRig(
        IReadOnlyList<ExternalRigBodySpec> bodySpecs,
        IReadOnlyList<ExternalRigJointSpec> jointSpecs,
        out ExternalRigHandle? handle,
        bool collideWithRagdoll = true,
        bool selfCollide = true)
    {
        handle = null;
        if (!isActive || simulation == null || bufferPool == null || bodySpecs.Count == 0)
            return false;

        var selfCollideGroup = selfCollide ? 0 : nextExternalRigSelfCollideGroup++;
        var rig = new ExternalRigHandle { Generation = externalBodyGeneration };
        try
        {
            for (var i = 0; i < bodySpecs.Count; i++)
            {
                var spec = bodySpecs[i];
                if (spec.Parts.Count == 0)
                    throw new InvalidOperationException($"External rig body '{spec.Name}' has no shapes.");

                var shapeIndex = CreateExternalBodyShape(spec.Parts, out var shapes, out var inertia, spec.Mass);
                var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(spec.Position, Quaternion.Normalize(spec.Orientation)),
                    default(BodyVelocity),
                    inertia,
                    new CollidableDescription(shapeIndex, 0.016f),
                    new BodyActivityDescription(0.01f)));

                var body = simulation.Bodies.GetBodyReference(bodyHandle);
                body.Velocity.Linear = spec.LinearVelocity;
                body.Velocity.Angular = spec.AngularVelocity;
                body.Awake = true;

                var bodyHandleWrapper = new ExternalBodyHandle
                {
                    Body = bodyHandle,
                    Shapes = shapes.ToArray(),
                    Generation = externalBodyGeneration,
                };
                rig.Bodies.Add(bodyHandleWrapper);
                externalBodies.Add(bodyHandleWrapper);
                externalDynamicBodyHandles.Add(bodyHandle.Value);
                externalRigDynamicBodyHandles.Add(bodyHandle.Value);
                if (!collideWithRagdoll)
                    externalRigNoRagdollContactBodyHandles.Add(bodyHandle.Value);
                if (selfCollideGroup != 0)
                    externalRigSelfCollideGroupByBody[bodyHandle.Value] = selfCollideGroup;
            }

            foreach (var joint in jointSpecs)
            {
                if (joint.ParentIndex < 0 || joint.ParentIndex >= rig.Bodies.Count ||
                    joint.ChildIndex < 0 || joint.ChildIndex >= rig.Bodies.Count ||
                    joint.ParentIndex == joint.ChildIndex)
                    continue;

                AddExternalRigJoint(rig, joint);
            }

            externalRigs.Add(rig);
            handle = rig;
            prevAllAsleep = false;
            WakeRagdollBodiesForBiomechanicalSettle();
            BeginBiomechanicalSettle(0.45f);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: failed to create external rig");
            RemoveExternalRig(rig);
            return false;
        }
    }

    private void AddExternalRigJoint(ExternalRigHandle rig, ExternalRigJointSpec joint)
    {
        if (simulation == null)
            return;

        var parent = rig.Bodies[joint.ParentIndex];
        var child = rig.Bodies[joint.ChildIndex];

        if (joint.Kind == ExternalRigJointKind.DistanceLimit)
        {
            rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new DistanceLimit
            {
                LocalOffsetA = joint.LocalOffsetChild,
                LocalOffsetB = joint.LocalOffsetParent,
                MinimumDistance = joint.MinDistance,
                MaximumDistance = joint.MaxDistance,
                SpringSettings = new SpringSettings(joint.DistanceFrequency, joint.DistanceDamping),
            }));

            var loD = Math.Min(parent.Body.Value, child.Body.Value);
            var hiD = Math.Max(parent.Body.Value, child.Body.Value);
            var pairD = (loD, hiD);
            rig.ConnectedPairs.Add(pairD);
            externalRigConnectedPairs.Add(pairD);
            return;
        }

        var parentBody = simulation.Bodies.GetBodyReference(parent.Body);
        var childBody = simulation.Bodies.GetBodyReference(child.Body);

        var anchorWorld = joint.AnchorWorld;
        var childLocalAnchor = Vector3.Transform(anchorWorld - childBody.Pose.Position, Quaternion.Inverse(childBody.Pose.Orientation));
        var parentLocalAnchor = Vector3.Transform(anchorWorld - parentBody.Pose.Position, Quaternion.Inverse(parentBody.Pose.Orientation));
        var jointSpring = new SpringSettings(9f, 1.4f);
        var limitSpring = new SpringSettings(5f, 1.6f);

        rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new BallSocket
        {
            LocalOffsetA = childLocalAnchor,
            LocalOffsetB = parentLocalAnchor,
            SpringSettings = jointSpring,
        }));

        var childAxisWorld = NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, childBody.Pose.Orientation), Vector3.UnitY);
        if (joint.SwingLimit > 0f)
        {
            var axisLocalA = Vector3.Normalize(Vector3.Transform(childAxisWorld, Quaternion.Inverse(childBody.Pose.Orientation)));
            var axisLocalB = Vector3.Normalize(Vector3.Transform(childAxisWorld, Quaternion.Inverse(parentBody.Pose.Orientation)));
            // Spawn at a fraction of the target ROM so the garment holds its worn shape on handoff, then
            // relax to full via RelaxExternalRigSwingLimits over ~1s.
            var target = Math.Clamp(joint.SwingLimit, 0.05f, MathF.PI - 0.05f);
            var handle = simulation.Solver.Add(child.Body, parent.Body, new SwingLimit
            {
                AxisLocalA = axisLocalA,
                AxisLocalB = axisLocalB,
                MaximumSwingAngle = Math.Clamp(target * joint.InitialSwingFactor, 0.05f, MathF.PI - 0.05f),
                SpringSettings = limitSpring,
            });
            rig.Constraints.Add(handle);
            rig.SwingConstraints.Add(new RigSwingConstraint
            {
                Handle = handle,
                AxisLocalA = axisLocalA,
                AxisLocalB = axisLocalB,
                Spring = limitSpring,
                TargetSwing = target,
            });
        }

        rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new AngularMotor
        {
            TargetVelocityLocalA = Vector3.Zero,
            Settings = new MotorSettings(0.65f, 0.45f),
        }));

        if (joint.PoseGuideMaxForce > 0f)
        {
            var target = Quaternion.Normalize(Quaternion.Inverse(childBody.Pose.Orientation) * parentBody.Pose.Orientation);
            var handle = simulation.Solver.Add(child.Body, parent.Body, new AngularServo
            {
                TargetRelativeRotationLocalA = target,
                SpringSettings = new SpringSettings(joint.PoseGuideFrequency, 1.15f),
                ServoSettings = new ServoSettings(6f, 0f, joint.PoseGuideMaxForce),
            });
            rig.Constraints.Add(handle);
            rig.PoseGuideConstraints.Add(new RigPoseGuideConstraint
            {
                Handle = handle,
                TargetRelativeRotationLocalA = target,
                Frequency = joint.PoseGuideFrequency,
                MaxForce = joint.PoseGuideMaxForce,
            });
        }

        var lo = Math.Min(parent.Body.Value, child.Body.Value);
        var hi = Math.Max(parent.Body.Value, child.Body.Value);
        var pair = (lo, hi);
        rig.ConnectedPairs.Add(pair);
        externalRigConnectedPairs.Add(pair);
    }

    public bool TrySetExternalBodyShape(ExternalBodyHandle? handle,
        IReadOnlyList<ExternalShapePart> parts, float mass)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bufferPool == null || parts.Count == 0)
            return false;

        TypedIndex[]? newShapes = null;
        try
        {
            var newShape = CreateExternalBodyShape(parts, out var shapeList, out var inertia, mass);
            newShapes = shapeList.ToArray();
            var oldShapes = handle.Shapes;

            simulation.Bodies.SetShape(handle.Body, newShape);
            simulation.Bodies.SetLocalInertia(handle.Body, in inertia);
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Awake = true;

            handle.Shapes = newShapes;
            foreach (var shape in oldShapes)
                try { simulation.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }

            WakeRagdollBodiesForBiomechanicalSettle();
            prevAllAsleep = false;
            BeginBiomechanicalSettle(0.25f);
            return true;
        }
        catch (Exception ex)
        {
            if (newShapes != null)
                foreach (var shape in newShapes)
                    try { simulation?.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }
            log.Warning(ex, "RagdollController: failed to replace external body shape");
            return false;
        }
    }

    private TypedIndex CreateExternalBodyShape(IReadOnlyList<ExternalShapePart> parts,
        out List<TypedIndex> shapes, out BodyInertia inertia, float mass)
    {
        shapes = new List<TypedIndex>(parts.Count + 1);
        TypedIndex shapeIndex;
        if (parts.Count == 1 &&
            parts[0].LocalPosition.LengthSquared() < 1e-6f &&
            IsIdentity(parts[0].LocalOrientation))
        {
            var h = parts[0].HalfExtents;
            shapeIndex = simulation!.Shapes.Add(new Box(h.X * 2f, h.Y * 2f, h.Z * 2f));
            shapes.Add(shapeIndex);
        }
        else
        {
            bufferPool!.Take<CompoundChild>(parts.Count, out var children);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var h = p.HalfExtents;
                var childShape = simulation!.Shapes.Add(new Box(h.X * 2f, h.Y * 2f, h.Z * 2f));
                shapes.Add(childShape);
                children[i] = new CompoundChild
                {
                    ShapeIndex = childShape,
                    LocalPosition = p.LocalPosition,
                    LocalOrientation = p.LocalOrientation,
                };
            }

            shapeIndex = simulation!.Shapes.Add(new Compound(children));
            shapes.Add(shapeIndex);
        }

        var half = ComputeExternalShapeHalf(parts);
        inertia = new Box(half.X * 2f, half.Y * 2f, half.Z * 2f)
            .ComputeInertia(MathF.Max(0.01f, mass));
        return shapeIndex;
    }

    public bool TryGetExternalBodyPose(ExternalBodyHandle? handle, out Vector3 position, out Quaternion orientation,
        out Vector3 linearVelocity, out Vector3 angularVelocity)
    {
        position = Vector3.Zero;
        orientation = Quaternion.Identity;
        linearVelocity = Vector3.Zero;
        angularVelocity = Vector3.Zero;

        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            position = body.Pose.Position;
            orientation = body.Pose.Orientation;
            linearVelocity = body.Velocity.Linear;
            angularVelocity = body.Velocity.Angular;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetExternalRigBodyPose(ExternalRigHandle? handle, int bodyIndex,
        out Vector3 position, out Quaternion orientation, out Vector3 linearVelocity, out Vector3 angularVelocity)
    {
        position = Vector3.Zero;
        orientation = Quaternion.Identity;
        linearVelocity = Vector3.Zero;
        angularVelocity = Vector3.Zero;

        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bodyIndex < 0 || bodyIndex >= handle.Bodies.Count)
            return false;

        return TryGetExternalBodyPose(handle.Bodies[bodyIndex], out position, out orientation,
            out linearVelocity, out angularVelocity);
    }

    public bool TryApplyExternalRigVelocityDelta(ExternalRigHandle? handle, Vector3 velocityDelta)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        foreach (var bodyHandle in handle.Bodies)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(bodyHandle.Body);
                body.Velocity.Linear += velocityDelta;
                body.Awake = true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    public bool TryDampenExternalRigVelocity(ExternalRigHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        horizontalScale = Math.Clamp(horizontalScale, 0f, 1f);
        verticalScale = Math.Clamp(verticalScale, 0f, 1f);
        angularScale = Math.Clamp(angularScale, 0f, 1f);

        foreach (var bodyHandle in handle.Bodies)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(bodyHandle.Body);
                var lin = body.Velocity.Linear;
                body.Velocity.Linear = new Vector3(lin.X * horizontalScale, lin.Y * verticalScale, lin.Z * horizontalScale);
                body.Velocity.Angular *= angularScale;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Re-apply each garment swing constraint's ROM as <paramref name="factor"/> (0..1) of its
    /// stored target, widening the joints from their tight spawn ROM to full as the garment settles.</summary>
    public bool RelaxExternalRigSwingLimits(ExternalRigHandle? handle, float factor)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        factor = Math.Clamp(factor, 0f, 1f);
        foreach (var s in handle.SwingConstraints)
        {
            try
            {
                simulation.Solver.ApplyDescription(s.Handle, new SwingLimit
                {
                    AxisLocalA = s.AxisLocalA,
                    AxisLocalB = s.AxisLocalB,
                    MaximumSwingAngle = Math.Clamp(s.TargetSwing * factor, 0.05f, MathF.PI - 0.05f),
                    SpringSettings = s.Spring,
                });
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Fade temporary garment pose-guide servos from their captured handoff pose toward zero force.</summary>
    public bool ApplyExternalRigPoseGuidance(ExternalRigHandle? handle, float strength)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        strength = Math.Clamp(strength, 0f, 1f);
        foreach (var s in handle.PoseGuideConstraints)
        {
            try
            {
                simulation.Solver.ApplyDescription(s.Handle, new AngularServo
                {
                    TargetRelativeRotationLocalA = s.TargetRelativeRotationLocalA,
                    SpringSettings = new SpringSettings(s.Frequency, 1.15f),
                    ServoSettings = new ServoSettings(6f, 0f, s.MaxForce * strength),
                });
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    public bool TrySetExternalBodyRotation(ExternalBodyHandle? handle, Quaternion orientation, float angularVelocityScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Pose.Orientation = Quaternion.Normalize(orientation);
            body.Velocity.Angular *= Math.Clamp(angularVelocityScale, 0f, 1f);
            body.Awake = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryApplyExternalVelocityDelta(ExternalBodyHandle? handle, Vector3 velocityDelta)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Velocity.Linear += velocityDelta;
            body.Awake = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryDampenExternalBodyVelocity(ExternalBodyHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            horizontalScale = Math.Clamp(horizontalScale, 0f, 1f);
            verticalScale = Math.Clamp(verticalScale, 0f, 1f);
            angularScale = Math.Clamp(angularScale, 0f, 1f);

            var body = simulation.Bodies.GetBodyReference(handle.Body);
            var lin = body.Velocity.Linear;
            body.Velocity.Linear = new Vector3(lin.X * horizontalScale, lin.Y * verticalScale, lin.Z * horizontalScale);
            body.Velocity.Angular *= angularScale;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RemoveExternalBody(ExternalBodyHandle? handle)
    {
        if (handle == null || handle.Removed)
            return;

        if (simulation != null && bufferPool != null && handle.Generation == externalBodyGeneration)
        {
            try { simulation.Bodies.Remove(handle.Body); } catch { }
            foreach (var shape in handle.Shapes)
                try { simulation.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }
        }

        externalDynamicBodyHandles.Remove(handle.Body.Value);
        handle.Removed = true;
        externalBodies.Remove(handle);
    }

    public void RemoveExternalRig(ExternalRigHandle? handle)
    {
        if (handle == null || handle.Removed)
            return;

        if (simulation != null && handle.Generation == externalBodyGeneration)
        {
            foreach (var constraint in handle.Constraints)
                try { simulation.Solver.Remove(constraint); } catch { }
        }

        foreach (var pair in handle.ConnectedPairs)
            externalRigConnectedPairs.Remove(pair);
        handle.Constraints.Clear();
        handle.ConnectedPairs.Clear();

        foreach (var bodyHandle in handle.Bodies)
        {
            externalRigNoRagdollContactBodyHandles.Remove(bodyHandle.Body.Value);
            externalRigDynamicBodyHandles.Remove(bodyHandle.Body.Value);
            externalRigSelfCollideGroupByBody.Remove(bodyHandle.Body.Value);
            RemoveExternalBody(bodyHandle);
        }

        handle.Bodies.Clear();
        handle.Removed = true;
        externalRigs.Remove(handle);
    }

    private bool AnyExternalBodyAwake()
    {
        if (simulation == null || externalBodies.Count == 0)
            return false;

        var anyAwake = false;
        for (int i = externalBodies.Count - 1; i >= 0; i--)
        {
            var handle = externalBodies[i];
            if (handle.Removed || handle.Generation != externalBodyGeneration)
            {
                externalDynamicBodyHandles.Remove(handle.Body.Value);
                externalBodies.RemoveAt(i);
                continue;
            }

            try
            {
                if (simulation.Bodies.GetBodyReference(handle.Body).Awake)
                    anyAwake = true;
            }
            catch
            {
                externalDynamicBodyHandles.Remove(handle.Body.Value);
                handle.Removed = true;
                externalBodies.RemoveAt(i);
            }
        }

        return anyAwake;
    }

    private static bool IsIdentity(Quaternion q)
        => MathF.Abs(q.X) < 1e-5f && MathF.Abs(q.Y) < 1e-5f &&
           MathF.Abs(q.Z) < 1e-5f && MathF.Abs(q.W - 1f) < 1e-5f;

    private static Vector3 ComputeExternalShapeHalf(IReadOnlyList<ExternalShapePart> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in parts)
        {
            var half = RotatedLocalHalfExtent(p.LocalOrientation, p.HalfExtents);
            min = Vector3.Min(min, p.LocalPosition - half);
            max = Vector3.Max(max, p.LocalPosition + half);
        }

        return new Vector3(
            MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)),
            MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y)),
            MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z)));
    }

    private static Vector3 RotatedLocalHalfExtent(Quaternion rot, Vector3 half)
    {
        var x = Vector3.Transform(Vector3.UnitX, rot);
        var y = Vector3.Transform(Vector3.UnitY, rot);
        var z = Vector3.Transform(Vector3.UnitZ, rot);
        return new Vector3(
            MathF.Abs(x.X) * half.X + MathF.Abs(y.X) * half.Y + MathF.Abs(z.X) * half.Z,
            MathF.Abs(x.Y) * half.X + MathF.Abs(y.Y) * half.Y + MathF.Abs(z.Y) * half.Z,
            MathF.Abs(x.Z) * half.X + MathF.Abs(y.Z) * half.Y + MathF.Abs(z.Z) * half.Z);
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

    public void SetDismemberedBones(IEnumerable<string> bones)
    {
        localDismemberBones.Clear();
        foreach (var bone in bones)
            if (!string.IsNullOrWhiteSpace(bone) && !localDismemberBones.Contains(bone))
                localDismemberBones.Add(bone);
        useLocalDismemberBones = true;
    }

    private RagdollBoneDef[] BuildActivePhysicsDefsForDismemberment(
        SkeletonAccess skel,
        RagdollBoneDef[] defs,
        Dictionary<string, int> activeNameToIndex)
    {
        var severedBones = useLocalDismemberBones ? localDismemberBones : config.DismemberPocBones;
        if (severedBones == null || severedBones.Count == 0 || defs.Length == 0)
            return defs;

        var severedRoots = new List<int>();
        foreach (var boneName in severedBones)
        {
            if (string.IsNullOrWhiteSpace(boneName)) continue;
            var idx = boneService.ResolveBoneIndex(skel, boneName);
            if (idx >= 0 && !severedRoots.Contains(idx))
                severedRoots.Add(idx);
        }
        if (severedRoots.Count == 0)
            return defs;

        var physicsRoots = new List<int>();
        foreach (var def in defs)
            if (def.ParentName == null && activeNameToIndex.TryGetValue(def.Name, out var idx))
                physicsRoots.Add(idx);

        var removableRoots = new List<int>(severedRoots.Count);
        foreach (var root in severedRoots)
        {
            var containsPhysicsRoot = false;
            foreach (var physicsRoot in physicsRoots)
            {
                if (IsDescendantOrSelf(skel, physicsRoot, root))
                {
                    containsPhysicsRoot = true;
                    break;
                }
            }

            if (!containsPhysicsRoot)
                removableRoots.Add(root);
        }

        if (removableRoots.Count == 0)
            return defs;

        var kept = new List<RagdollBoneDef>(defs.Length);
        var removedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in defs)
        {
            if (!activeNameToIndex.TryGetValue(def.Name, out var idx))
                continue;

            var severed = false;
            foreach (var root in removableRoots)
            {
                if (IsDescendantOrSelf(skel, idx, root))
                {
                    severed = true;
                    break;
                }
            }

            if (severed)
                removedNames.Add(def.Name);
            else
                kept.Add(def);
        }

        if (removedNames.Count == 0)
            return defs;
        if (kept.Count < 2)
        {
            log.Warning($"RagdollController: active dismember physics would leave {kept.Count} bodies; keeping full rig.");
            return defs;
        }

        foreach (var name in removedNames)
            activeNameToIndex.Remove(name);

        log.Info($"RagdollController: active dismember physics removed {removedNames.Count}/{defs.Length} bodies.");
        return kept.ToArray();
    }

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

    // Tier D — Anthropometric segment masses as fractions of total body mass.
    // Winter, Biomechanics and Motor Control of Human Movement, Table 3.1.
    // Keyed by the side-stripped bone name (trailing _l/_r removed). Bones not in
    // this table (cloth j_sk_*, weapon j_buki*, breast j_mune_*) keep their literal
    // Mass and are excluded from anthropometric scaling.
    //
    // Limbs are bilateral, so each per-side fraction below is HALF of Winter's total
    // (e.g. Winter thigh .100 = both thighs; each thigh = .050).
    // Trunk (.497) is split so the pelvis is the heaviest of the four segments
    // (Winter has pelvis .142, abdomen .139, thorax .216; we further split the
    // thorax across our mid-spine + chest so all four sum to .497 and pelvis >= each).
    private static readonly Dictionary<string, float> AnthropometricMassFraction = new()
    {
        // Lower limb (per side)
        { "j_asi_a", 0.050f },  // thigh        (Winter thigh .100 / 2)
        { "j_asi_b", 0.0258f }, // shank (shin) (Winter leg .0465 minus foot folded? see below)
        { "j_asi_c", 0.0025f }, // calf segment (small; folded remainder of shank)
        { "j_asi_d", 0.00725f },// foot         (Winter foot .0145 / 2)
        // Upper limb (per side)
        { "j_ude_a", 0.014f },  // upper arm    (Winter .028 / 2)
        { "j_ude_b", 0.008f },  // forearm      (Winter .016 / 2)
        { "j_te",    0.003f },  // hand         (Winter .006 / 2)
        // Head + neck (.081 total)
        { "j_kao",   0.036f },  // head
        { "j_kubi",  0.045f },  // neck
        // Trunk (.497 total, pelvis heaviest)
        { "j_kosi",  0.142f },  // pelvis
        { "j_sebo_a",0.139f },  // lower spine
        { "j_sebo_b",0.110f },  // mid spine
        { "j_sebo_c",0.106f },  // chest
    };

    // Strip a trailing "_l" / "_r" side suffix for anthropometric-table lookup.
    private static string SideStrippedBoneName(string name)
    {
        if (name.Length > 2 && (name.EndsWith("_l") || name.EndsWith("_r")))
            return name.Substring(0, name.Length - 2);
        return name;
    }

    // Resolve a bone's effective mass. With anthropometric mass on, bones present in
    // AnthropometricMassFraction use (fraction x bodyMass); all others fall back to the
    // hand-picked def.Mass.
    private static float ResolveBoneMass(RagdollBoneDef def, bool anthropometric, float bodyMass)
    {
        if (anthropometric &&
            AnthropometricMassFraction.TryGetValue(SideStrippedBoneName(def.Name), out var frac))
        {
            return frac * bodyMass;
        }
        return def.Mass;
    }

    // === Tier C — Anatomical range-of-motion (ROM) table ============================
    // Solver-AGNOSTIC, clinical/ISB-derived per-DOF ranges of motion, stored in RADIANS.
    // This is the durable source of truth for joint ROM: it survives a future reduced-
    // coordinate (Tier A) builder, which would consume the SAME numbers. Keyed by the
    // side-stripped bone name (so left/right share one entry, matching the mass table).
    //
    // Source: standard healthy-adult goniometric / clinical ROM (AAOS averages) expressed
    // in the ISB Grood-Suntay joint coordinate system; rounded to typical means:
    //   Knee     flexion 0-140 deg, hyperextension 5,        axial (tibial) +/-10
    //   Elbow    flexion 0-145 deg, hyperextension 5,        axial (pron/sup) +/-80
    //   Hip      flexion 120, extension 20, abduction 45, adduction 25, axial +/-40
    //   Shoulder flexion 170, extension 50, abduction 170, adduction 40, axial +/-90
    //   Ankle    dorsiflexion 20, plantarflexion 45,         inv/eversion +/-20
    //   Neck     flexion/extension +/-45, lateral +/-40,     axial +/-70
    //   Spine    (per segment) flex/ext +/-15, lateral +/-15, axial +/-10
    //
    // WIRED IN TODAY (maximal-coordinate builder): the AXIAL twist range of every joint
    // (C2) and the knee/elbow FLEXION + HYPEREXTENSION bounds about the Tier-B hinge axis
    // (C3). DEFERRED to Tier A: the ball-joint (hip/shoulder) asymmetric SWING ellipse /
    // reach-cone (flex/ext/abd/add) — fiddly in maximal coordinates; see RAGDOLL_JOINT_MODEL.md
    // Part 2, Tier C. Those flex/ext/abd/add fields are populated here for that future use.
    private readonly struct AnatomicalRom
    {
        public readonly float FlexionMax;    // forward bend / dorsiflexion (rad, >=0)  [swing, deferred for ball joints]
        public readonly float ExtensionMax;  // backward bend / plantarflex / hyperext (rad, >=0)
        public readonly float AbductionMax;  // (rad) [swing, deferred]
        public readonly float AdductionMax;  // (rad) [swing, deferred]
        public readonly float AxialMin;      // axial twist lower bound (rad, signed)   [C2, wired]
        public readonly float AxialMax;      // axial twist upper bound (rad, signed)   [C2, wired]

        public AnatomicalRom(float flexionMax, float extensionMax, float abductionMax,
                             float adductionMax, float axialMin, float axialMax)
        {
            FlexionMax = flexionMax;
            ExtensionMax = extensionMax;
            AbductionMax = abductionMax;
            AdductionMax = adductionMax;
            AxialMin = axialMin;
            AxialMax = axialMax;
        }
    }

    private static float D2R(float degrees) => degrees * (MathF.PI / 180f);

    // Keyed by side-stripped bone name (see SideStrippedBoneName). Bones absent from this
    // table fall back to their hand-set boneDef twist values (and the legacy fold-stop).
    private static readonly Dictionary<string, AnatomicalRom> AnatomicalRomTable = new()
    {
        // Hinge joints
        // Knee flexion: relaxed passive max is ~135-145° (thigh↔shin interior angle
        // 35-45°). Connected pairs don't collide, so there is no soft-tissue backstop —
        // use the conservative end or the shin folds visibly INTO the thigh.
        { "j_asi_b", new AnatomicalRom(D2R(135f), D2R(5f),   0f,        0f,        D2R(-10f), D2R(10f)) }, // Knee (shin)
        { "j_ude_b", new AnatomicalRom(D2R(145f), D2R(5f),   0f,        0f,        D2R(-80f), D2R(80f)) }, // Elbow (forearm)
        // Ball joints (axial wired now; swing fields deferred to Tier A)
        // Hip flexion: 120° is the CLINICAL value measured with a bent knee. With the knee
        // extended the two-joint hamstrings cap passive flexion at ~80–90°; death poses mostly
        // have near-straight legs, so 95° is the relaxed-body compromise (no cross-joint
        // hamstring constraint is modelled). This is what stops the seated corpse folding
        // its chest flat onto its legs.
        { "j_asi_a", new AnatomicalRom(D2R(95f),  D2R(20f),  D2R(45f),  D2R(25f),  D2R(-40f), D2R(40f)) }, // Hip (thigh)
        { "j_ude_a", new AnatomicalRom(D2R(170f), D2R(50f),  D2R(170f), D2R(40f),  D2R(-90f), D2R(90f)) }, // Shoulder (upper arm)
        { "j_sako",  new AnatomicalRom(D2R(170f), D2R(50f),  D2R(170f), D2R(40f),  D2R(-90f), D2R(90f)) }, // Shoulder (clavicle)
        { "j_asi_d", new AnatomicalRom(D2R(20f),  D2R(45f),  0f,        0f,        D2R(-20f), D2R(20f)) }, // Ankle (foot): dorsi/plantar + inv/ev
        { "j_kubi",  new AnatomicalRom(D2R(45f),  D2R(45f),  D2R(40f),  D2R(40f),  D2R(-70f), D2R(70f)) }, // Neck
        { "j_sebo_a",new AnatomicalRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Lower spine
        { "j_sebo_b",new AnatomicalRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Mid spine
        { "j_sebo_c",new AnatomicalRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Chest
    };

    private static bool TryGetAnatomicalRom(string boneName, out AnatomicalRom rom)
        => AnatomicalRomTable.TryGetValue(SideStrippedBoneName(boneName), out rom);

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

    // Per-bone kinematic collision body for an NPC (dynamically created from skeleton)
    private struct NpcBoneStatic
    {
        public BodyHandle Handle;
        public int BoneIndex;           // this bone's skeleton index
        public int ParentBoneIndex;     // parent bone for segment direction
        public float HalfLength;        // half the capsule body segment length
        public float CenterFactor;      // parent->child fraction where the capsule is centered
        public Vector3 PreviousPosition;
        public Quaternion PreviousOrientation;
        public bool HasPreviousPose;
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
    private const int MeshCollisionMaxTriangles = 8000;
    private const int MeshCollisionPreferredLod = 0; // LOD0: matches the on-screen silhouette exactly (Low LOD shrank/coarsened the collision hull).
    private const float AnimatedMeshUpdateInterval = 0.15f;
    private const float AnimatedMeshSlowUpdateBackoff = 0.5f;
    private const float AnimatedMeshSlowUpdateMs = 5f;
    private const int AnimatedMeshInitialUpdateLogCount = 3;
    // A character whose root is farther than this (metres) from the corpse is skipped in
    // the per-frame static update — it cannot contact the ragdoll, so tracking its bones
    // is wasted work. It still resumes updating if it moves back into range.
    private const float NpcCollisionUpdateRadius = 8f;

    // Per-NPC collision state (bone-based, convex hull, or single-capsule fallback)
    private struct NpcCollisionState
    {
        public nint NpcAddress;
        public List<NpcBoneStatic> BoneStatics;   // populated when skeleton readable
        public BodyHandle FallbackHandle;          // used when skeleton unreadable
        public bool IsFallback;
        // Convex hull mode (non-humanoid mounts/monsters)
        public bool IsConvexHull;
        public BodyHandle ConvexHullHandle;
        public Vector3 HullCenterModelSpace; // hull centroid in skeleton-local (model) space
        public bool IsMesh;
        public bool IsAnimatedMesh;
        public BodyHandle MeshHandle;
        public TypedIndex MeshShapeIndex;
        public List<AnimatedMeshCollisionModel> AnimatedMeshModels;
        public float AnimatedMeshNextUpdateElapsed;
        public int AnimatedMeshTriangleCount;
        public int AnimatedMeshUpdateCount;
        public Vector3 PreviousPosition;
        public Quaternion PreviousOrientation;
        public bool HasPreviousPose;
        // True while this character's statics have been parked far away because it left the
        // update radius. Prevents leaving them frozen on the corpse ("ghost" capsules) and
        // avoids re-parking every frame; cleared when it re-enters range.
        public bool Parked;
    }

    private sealed class AnimatedMeshCollisionModel
    {
        public string ModelPath = string.Empty;
        public MeshCollisionMdlData Mdl = new();
        public int LodIndex;
        public int[] MeshIndices = Array.Empty<int>();
        public Dictionary<int, int[]> BoneMapsByMeshIndex = new();
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
        collapseProfileBook = new CollapseProfileBook(log);

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
        collapseProfileBook = new CollapseProfileBook(log);

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

        ArmConfiguredGuidedCollapse();

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

        // If follow moved the local player's client-side render transform, snap the DrawObject
        // back to the frozen death position so there is no one-frame pop before the game
        // re-syncs it from the (never-moved) logical position on revive. NPC phantoms are left
        // as-is (they despawn or are repositioned elsewhere), matching the prior behaviour.
        if (followWasActive && targetCharacterAddress != nint.Zero && Core.Services.ObjectTable.LocalPlayer != null)
        {
            try
            {
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
                if (gameObj->ObjectIndex == 0)
                {
                    var drawObject = gameObj->DrawObject;
                    if (drawObject != null)
                    {
                        ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)drawObject)->Position = savedCharacterPosition;
                        drawObject->NotifyTransformChanged();
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "RagdollController: Failed to restore DrawObject position on deactivate");
            }
        }
        followWasActive = false;

        StopCollapseSpike();
        pendingCollapseSpike = false;
        StopDirectedCollapseSpike();
        pendingDirectedCollapseSpike = false;
        StopWholeBodyCollapse();
        pendingWholeBodyCollapse = false;
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
                if (elapsed < activationDelay)
                {
                    // Keep sampling the animated pose so the last frame before handoff carries
                    // its velocity into the physics bodies (no freeze on activation).
                    if (config.RagdollCarryAnimationVelocity)
                        SampleHandoffPose(dt);
                    return;
                }
                if (!InitializePhysics()) { Deactivate(); return; }
                physicsStarted = true;
                frameCount = 0;
                BeginBiomechanicalSettle();
                physicsAccumulator = 0f; // start clean — don't carry the activation-delay wait

                // Bodies are at their freshly-created death-instant poses (zero velocity) — the
                // ideal moment to snapshot "muscle tone" for an armed collapse spike.
                if (pendingCollapseSpike)
                {
                    pendingCollapseSpike = false;
                    if (BeginCollapseSpike(pendingCollapseArchetype, pendingCollapseStrength,
                        pendingCollapseHold, pendingCollapseFade, pendingCollapseHinge))
                    {
                        ApplyCollapseDirectionImpulse(pendingCollapseDirection, pendingCollapseImpulse);
                    }
                }

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

                if (pendingWholeBodyCollapse)
                {
                    pendingWholeBodyCollapse = false;
                    BeginWholeBodyCollapse(pendingWholeBodyDirection, pendingWholeBodyFuse);
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

    // Snapshot the current animated bone poses (model space) + skeleton transform so the next
    // call to InitializePhysics can derive handoff velocities. Called each frame while the
    // activation-delay death animation plays.
    private void SampleHandoffPose(float dt)
    {
        if (dt <= 1e-5f) return;
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;

        handoffSkelPos = new Vector3(
            skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        handoffSkelRot = new Quaternion(
            skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);

        var pose = skel.Pose;
        var n = skel.BoneCount;
        if (handoffPrevPos == null || handoffPrevPos.Length < n)
        {
            handoffPrevPos = new Vector3[n];
            handoffPrevRot = new Quaternion[n];
        }
        for (int i = 0; i < n; i++)
        {
            ref var mt = ref pose->ModelPose.Data[i];
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

    // Collapse the given limb's entire bone subtree to ~0 scale so the limb vanishes from the body.
    private void HideLimbSubtree(SkeletonAccess skel, string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return;

        var rootIdx = boneService.ResolveBoneIndex(skel, rootName);
        if (rootIdx < 0)
        {
            foreach (var rb in ragdollBones)
                if (rb.Name == rootName) { rootIdx = rb.BoneIndex; break; }
        }
        if (rootIdx < 0) return;

        var pose = skel.Pose;
        // Severance point = the root bone's model-space position (its joint). Collapse every subtree
        // bone to BOTH ~0 scale AND that single point, so the limb has no length and no leftover
        // sliver — it fully vanishes into the joint instead of compressing into a thin model.
        ref var rootM = ref pose->ModelPose.Data[rootIdx];
        var rx = rootM.Translation.X;
        var ry = rootM.Translation.Y;
        var rz = rootM.Translation.Z;

        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 0; i < n; i++)
        {
            if (!IsDescendantOrSelf(skel, i, rootIdx)) continue;
            ref var m = ref pose->ModelPose.Data[i];
            m.Translation.X = rx;
            m.Translation.Y = ry;
            m.Translation.Z = rz;
            m.Scale.X = 0.0001f;
            m.Scale.Y = 0.0001f;
            m.Scale.Z = 0.0001f;
        }

        // Face/hair (and similar) live on SEPARATE partial skeletons attached to a body bone, so they
        // are not in the main parent chain above — collapse those too or the head stays visible.
        HideSubtreePartialSkeletons(skel, rootIdx, new Vector3(rx, ry, rz));
    }

    private void HideSubtreePartialSkeletons(SkeletonAccess skel, int rootIdx, Vector3 collapsePoint)
    {
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;

        for (int ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
        {
            var partial = &skeleton->PartialSkeletons[ps];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || pose->ModelInSync == 0) continue;

            var cnt = pose->ModelPose.Length;
            if (cnt < 1) continue;

            // This partial attaches to its root bone (e.g. "j_kao"); collapse it only if that bone is
            // inside the hidden subtree.
            var rootName = pose->Skeleton->Bones[0].Name.String;
            if (string.IsNullOrEmpty(rootName)) continue;
            var mainIdx = boneService.ResolveBoneIndex(skel, rootName);
            if (mainIdx < 0 || !IsDescendantOrSelf(skel, mainIdx, rootIdx)) continue;

            for (int b = 0; b < cnt; b++)
            {
                ref var m = ref pose->ModelPose.Data[b];
                m.Translation.X = collapsePoint.X;
                m.Translation.Y = collapsePoint.Y;
                m.Translation.Z = collapsePoint.Z;
                m.Scale.X = 0.0001f;
                m.Scale.Y = 0.0001f;
                m.Scale.Z = 0.0001f;
            }
        }
    }

    private static bool IsDescendantOrSelf(SkeletonAccess skel, int bone, int root)
    {
        var guard = 0;
        while (bone >= 0 && guard++ < 256)
        {
            if (bone == root) return true;
            bone = skel.HavokSkeleton->ParentIndices[bone];
        }
        return false;
    }

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

        // Tier B — anatomy-fixed hinge axis. Derive the knee/elbow hinge from the
        // skeleton's medial-lateral (character RIGHT) axis projected perpendicular to the
        // bone segment, instead of Cross(parent,child) which is degenerate for a near-
        // straight limb. This is stable regardless of how bent the limb is, so both knees
        // resolve to near-mirror axes (~±character-right) rather than one sideways + one
        // forward. Sign is aligned to the legacy axis so flexion direction is preserved.
        if (config.RagdollAnatomicalHingeAxis &&
            (role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow))
        {
            var anatAxis = ComputeAnatomicalHingeAxis(parentSegmentDir, childN);
            if (anatAxis.HasValue)
                return anatAxis.Value;
            // Degenerate (e.g. limb segment is itself along character-right): fall through.
        }

        if (config.RagdollExperimentalJointFrames &&
            (role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow))
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

    /// <summary>
    /// Tier B — stable knee/elbow hinge axis = skeleton medial-lateral (character RIGHT)
    /// axis projected perpendicular to the child bone segment, normalized. Returns null
    /// when the projection is degenerate (segment nearly parallel to character-right), so
    /// the caller can fall back to the legacy pose-derived axis. The sign is aligned to the
    /// legacy ComputeExperimentalHingeAxis so the flexion direction (and all downstream
    /// SwingLimit / AngularHinge / FoldStop / TwistBasis math) is unchanged.
    /// </summary>
    private Vector3? ComputeAnatomicalHingeAxis(Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var childN = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var right = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        var projected = ProjectOntoPlane(right, childN);
        if (projected.LengthSquared() < 0.001f)
            return null;

        var axis = Vector3.Normalize(projected);

        // Keep the same sign as the legacy axis so both knees flex identically (shin swings
        // backward = flexion). The legacy/experimental fallback already enforces a consistent
        // sign relative to the world-up cross.
        var legacy = ComputeExperimentalHingeAxis(parentSegmentDir, childN);
        if (Vector3.Dot(axis, legacy) < 0)
            axis = -axis;

        return axis;
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
        SpringSettings limitSpring,
        float? foldCapOverride = null)
    {
        if (simulation == null)
            return;
        // foldCapOverride (Tier C) lets the anatomical ROM drive the max-fold cap; when it is
        // supplied we honour it even if SwingMinLimit was 0/disabled for this bone.
        if (foldCapOverride == null && (boneDef.SwingMinLimit <= 0 || boneDef.SwingMinLimit >= MathF.PI))
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
        var foldFloor = foldCapOverride ?? boneDef.SwingMinLimit;
        var foldStopMaxAngle = MathF.Max(foldFloor, initBendAngle + foldBuffer);
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

    /// <summary>
    /// Tier C (C3) — directional flexion / hyperextension limit for a knee or elbow about the
    /// Tier-B hinge axis. Unlike the fold-stop (which measures the UNSIGNED angle between the
    /// two segments and so cannot tell forward flexion from backward hyperextension), this is a
    /// SIGNED limit about the hinge axis: flexion is allowed up to <c>rom.FlexionMax</c> while
    /// bending backward past straight is blocked beyond <c>rom.ExtensionMax</c> (hyperextension).
    ///
    /// Robustness (this is the medium-risk part the plan warns about):
    ///  - Anatomical anchoring: each body's twist basis shares the hinge axis (Z) but uses its
    ///    OWN segment as the in-plane reference, so the measured twist equals the TRUE flexion
    ///    angle (0 = anatomically straight), independent of the bent death-instant pose. Using a
    ///    single shared world basis instead would read 0 at the death pose and mis-anchor the
    ///    hyperextension floor to wherever the limb happened to die.
    ///  - Snap-proof: the init signed flexion is measured here in the SAME convention the solver
    ///    uses, and the range is widened (never tightened past anatomy) so the init pose is
    ///    strictly inside [min,max]. The widening uses |initFlexion| so it is correct even if the
    ///    solver's twist sign differs from ours — the wall can never fire on activation, so the
    ///    joint cannot snap or freeze when the ragdoll spawns, from any death pose (incl. ORZ).
    /// </summary>
    private void AddAnatomicalHingeFlexionLimit(
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        RagdollBoneDef boneDef,
        Vector3 hingeAxisWorld,
        Vector3 parentSegDir,
        Vector3 segDirWorld,
        AnatomicalRom rom,
        SpringSettings limitSpring)
    {
        if (simulation == null)
            return;

        var hingeN = NormalizeOrFallback(hingeAxisWorld, Vector3.UnitX);

        // Per-body bases: same twist axis (hinge), each referenced to its own segment so the
        // measured relative twist == true flexion (see method summary).
        var basisAWorld = CreateTwistBasis(hingeN, segDirWorld);    // child  (shin / forearm)
        var basisBWorld = CreateTwistBasis(hingeN, parentSegDir);   // parent (thigh / upper arm)

        // Measure init signed flexion in the solver's convention: the angle of the child's
        // in-plane axis relative to the parent's about the hinge axis. CreateTwistBasis maps the
        // local X axis to ProjectOntoPlane(segment, hingeAxis), so we compare those directly.
        var xA = ProjectOntoPlane(segDirWorld, hingeN);
        var xB = ProjectOntoPlane(parentSegDir, hingeN);
        float initFlexion = 0f;
        if (xA.LengthSquared() > 1e-6f && xB.LengthSquared() > 1e-6f)
        {
            xA = Vector3.Normalize(xA);
            xB = Vector3.Normalize(xB);
            var s = Vector3.Dot(Vector3.Cross(xB, xA), hingeN);
            var c = Math.Clamp(Vector3.Dot(xB, xA), -1f, 1f);
            initFlexion = MathF.Atan2(s, c);
        }

        // Anatomical bounds: flexion positive, hyperextension a small negative floor.
        var minAngle = -rom.ExtensionMax; // e.g. knee -5°
        var maxAngle =  rom.FlexionMax;   // e.g. knee +140°

        // Snap-proof widening: keep the init pose strictly inside the range, sign-agnostically.
        const float margin = 0.09f; // ~5°
        var initPad = MathF.Abs(initFlexion) + margin;
        minAngle = MathF.Min(minAngle, -initPad);
        maxAngle = MathF.Max(maxAngle,  initPad);
        // Never exceed the solver's measurable twist range.
        minAngle = MathF.Max(minAngle, -(MathF.PI - 0.05f));
        maxAngle = MathF.Min(maxAngle,  MathF.PI - 0.05f);

        var basisALocal = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * basisAWorld);
        var basisBLocal = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * basisBWorld);

        simulation.Solver.Add(childHandle, parentHandle,
            new TwistLimit
            {
                LocalBasisA = basisALocal,
                LocalBasisB = basisBLocal,
                MinimumAngle = minAngle,
                MaximumAngle = maxAngle,
                SpringSettings = limitSpring,
            });

        // One-time validation log (per knee/elbow): resolved flexion min/max + init flexion.
        log.Info($"[Ragdoll ROM] '{boneDef.Name}' flexion limit: initFlexion={initFlexion * 180f / MathF.PI:F1}° " +
                 $"range=[{minAngle * 180f / MathF.PI:F1}°,{maxAngle * 180f / MathF.PI:F1}°] " +
                 $"(hyperext block>{rom.ExtensionMax * 180f / MathF.PI:F0}°, flexMax={rom.FlexionMax * 180f / MathF.PI:F0}°)");
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
        if (!AnatomicalHingeRestBiasEnabled())
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

    private static bool AnatomicalHingeRestBiasEnabled() => false;

    /// <summary>
    /// Tier C (swing) — directional anatomical limits for ball joints (hips, arm-side
    /// shoulders). Expresses per-direction tilt caps a symmetric cone cannot: each
    /// direction d with cap θ becomes one SwingLimit between the child segment axis and
    /// −d with MaximumSwingAngle = 90° + θ ("stay at least 90°−θ away from d").
    /// Near-unbounded directions (90°+θ ≈ 180°) are skipped — the widened bounding cone
    /// owns those. Axes are anatomical (character facing at activation), baked into the
    /// parent body's local frame; lateral-out is resolved from which side of the parent
    /// the joint anchor sits on, so left/right need no handedness convention.
    /// </summary>
    private void AddDirectionalSwingLimits(
        string boneName,
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        Vector3 segDirWorld,
        Vector3 anchorWorld,
        Vector3 anatForwardWorld,
        Vector3 anatLateralWorld,
        AnatomicalRom rom,
        SpringSettings coneSpring,
        SpringSettings capSpring)
    {
        if (simulation == null)
            return;

        float outSign = MathF.Sign(Vector3.Dot(anchorWorld - parentBodyRef.Pose.Position, anatLateralWorld));
        if (outSign == 0f)
            outSign = 1f;
        var lateralOut = anatLateralWorld * outSign;

        var axisChildLocal = Vector3.Normalize(Vector3.Transform(
            segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

        // Symmetric ROM (the spine chain: 15/15/15/15, neck ~45): the intersection of the
        // four directional caps IS a cone around the anatomical vertical — emit ONE
        // constraint instead of four. This matters beyond tidiness: every constraint on a
        // shared body (the pelvis especially) occupies a solver batch slot, and pushing a
        // body past the batch threshold lands contacts in BEPU's fallback batch, which
        // NREs on kinematic-contact removal in 2.5.0-beta.
        var maxCap = MathF.Max(MathF.Max(rom.FlexionMax, rom.ExtensionMax),
                               MathF.Max(rom.AbductionMax, rom.AdductionMax));
        var minCap = MathF.Min(MathF.Min(rom.FlexionMax, rom.ExtensionMax),
                               MathF.Min(rom.AbductionMax, rom.AdductionMax));
        if (maxCap - minCap < 0.12f)
        {
            // Anchor to whichever world vertical the segment points along (spine: up).
            var neutralWorld = Vector3.Dot(segDirWorld, Vector3.UnitY) >= 0f ? Vector3.UnitY : -Vector3.UnitY;
            var neutralLocalParent = Vector3.Normalize(Vector3.Transform(
                neutralWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = axisChildLocal,
                AxisLocalB = neutralLocalParent,
                MaximumSwingAngle = maxCap,
                SpringSettings = coneSpring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = axisChildLocal,
                AxisLocalParent = neutralLocalParent,
                LimitAngle = maxCap,
            });

            if (config.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' symmetric anatomical cone: {maxCap * 180f / MathF.PI:F0}°");
            return;
        }

        AddCap(anatForwardWorld, rom.FlexionMax, "flex");
        AddCap(-anatForwardWorld, rom.ExtensionMax, "ext");
        AddCap(lateralOut, rom.AbductionMax, "abd");
        AddCap(-lateralOut, rom.AdductionMax, "add");

        // Diagonal caps: four cardinal caps alone leave the 45°-azimuth corners
        // inflated — a tilt toward forward+out projects only its cosine onto the pure
        // abduction cap, so a leg can rise ~80° on the flex-abd diagonal while the 45°
        // abduction cap stays satisfied. That diagonal IS the M-spread pose. Cap the
        // corners with neighbour-interpolated tilts to round the box toward the real
        // elliptical envelope.
        var diag = 1f / MathF.Sqrt(2f);
        AddCap((anatForwardWorld + lateralOut) * diag, (rom.FlexionMax + rom.AbductionMax) * 0.5f, "flex-abd");
        AddCap((anatForwardWorld - lateralOut) * diag, (rom.FlexionMax + rom.AdductionMax) * 0.5f, "flex-add");
        AddCap((-anatForwardWorld + lateralOut) * diag, (rom.ExtensionMax + rom.AbductionMax) * 0.5f, "ext-abd");
        AddCap((-anatForwardWorld - lateralOut) * diag, (rom.ExtensionMax + rom.AdductionMax) * 0.5f, "ext-add");

        void AddCap(Vector3 dir, float tilt, string label)
        {
            var max = MathF.PI / 2f + tilt;
            if (max >= MathF.PI - 0.15f)
                return; // effectively unbounded — the cone backstop covers it

            var oppositeLocalParent = Vector3.Normalize(Vector3.Transform(
                -dir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation!.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = axisChildLocal,
                AxisLocalB = oppositeLocalParent,
                MaximumSwingAngle = max,
                SpringSettings = capSpring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = axisChildLocal,
                AxisLocalParent = oppositeLocalParent,
                LimitAngle = max,
            });

            if (config.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' directional swing '{label}': tilt cap={tilt * 180f / MathF.PI:F0}° (swing max={max * 180f / MathF.PI:F0}°)");
        }
    }

    // Relaxed passive hip axial rotation. Clinical standing values are ~35°/45°
    // (int/ext); deep flexion winds the capsular ligaments and shrinks them further,
    // which the kneecap-facing construction below approximates by making combined
    // flexion+rotation spend the same budget as pure rotation.
    private static readonly float HipInternalRotationCap = D2R(30f);
    private static readonly float HipExternalRotationCap = D2R(45f);

    /// <summary>
    /// Tier C (swing) — hip axial-rotation caps that survive any flexion angle.
    /// BEPU's TwistLimit degenerates once the joint's swing exceeds ~90°, i.e. exactly
    /// in the kicked-up pose where a corpse thigh would otherwise spin freely; and an
    /// independent swing×twist box allows the anatomically impossible corner (deep
    /// flexion + full internal rotation — capsular ligaments wind tight there).
    /// Constrain the KNEECAP-FACING axis (⊥ femur, anterior at rest) instead: internal
    /// rotation sweeps it medially and external laterally, while pure flexion rotates
    /// it inside the sagittal plane and never touches these caps. Direction-vector
    /// limits never degenerate, and flexion+rotation combos consume the same budget as
    /// pure rotation — the swing↔twist coupling real ligaments provide.
    /// </summary>
    private void AddKneecapFacingLimits(
        string boneName,
        BodyHandle childHandle,
        BodyHandle parentHandle,
        BodyReference childBodyRef,
        BodyReference parentBodyRef,
        Vector3 segDirWorld,
        Vector3 anchorWorld,
        Vector3 anatForwardWorld,
        Vector3 anatLateralWorld,
        SpringSettings spring)
    {
        if (simulation == null)
            return;

        // Kneecap-forward at activation: anatomical forward made perpendicular to the
        // femur axis. Anatomically anchored — a death pose that froze mid-rotation
        // consumes its own budget (and gets gently un-rotated if past it).
        var femurAxis = Vector3.Normalize(segDirWorld);
        var kneecapFwd = anatForwardWorld - femurAxis * Vector3.Dot(anatForwardWorld, femurAxis);
        if (kneecapFwd.LengthSquared() < 0.01f)
            return; // femur ~aligned with anatomical forward (unusual death pose): skip
        kneecapFwd = Vector3.Normalize(kneecapFwd);

        float outSign = MathF.Sign(Vector3.Dot(anchorWorld - parentBodyRef.Pose.Position, anatLateralWorld));
        if (outSign == 0f)
            outSign = 1f;
        var lateralOut = anatLateralWorld * outSign;

        var kneecapLocalChild = Vector3.Normalize(Vector3.Transform(
            kneecapFwd, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

        AddCap(-lateralOut, HipInternalRotationCap, "int-rot");
        AddCap(lateralOut, HipExternalRotationCap, "ext-rot");

        void AddCap(Vector3 dir, float tilt, string label)
        {
            var max = MathF.PI / 2f + tilt;
            var oppositeLocalParent = Vector3.Normalize(Vector3.Transform(
                -dir, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

            simulation!.Solver.Add(childHandle, parentHandle, new SwingLimit
            {
                AxisLocalA = kneecapLocalChild,
                AxisLocalB = oppositeLocalParent,
                MaximumSwingAngle = max,
                SpringSettings = spring,
            });

            swingStressMonitors.Add(new SwingStressMonitor
            {
                Bone = boneName,
                Child = childHandle,
                Parent = parentHandle,
                AxisLocalChild = kneecapLocalChild,
                AxisLocalParent = oppositeLocalParent,
                LimitAngle = max,
            });

            if (config.RagdollVerboseLog)
                log.Info($"[Ragdoll ROM] '{boneName}' kneecap-facing '{label}': cap={tilt * 180f / MathF.PI:F0}°");
        }
    }

    // === Tier C (swing) debug: joint-vs-limit stress for the overlay =====================
    public readonly record struct JointLimitStress(string BoneName, Vector3 WorldPosition, float Stress);

    private struct SwingStressMonitor
    {
        public string Bone;
        public BodyHandle Child;
        public BodyHandle Parent;
        public Vector3 AxisLocalChild;
        public Vector3 AxisLocalParent;
        public float LimitAngle;
    }

    private readonly List<SwingStressMonitor> swingStressMonitors = new();
    private readonly Dictionary<string, JointLimitStress> stressWorstByBone = new();

    /// <summary>
    /// Per-bone worst swing-limit utilisation (current angle / limit angle; ≈1 = the joint
    /// is pinned on its range edge). Covers ball cones and the Tier-C directional limits.
    /// Overlay diagnostic for "spread-eagle" poses: pinned joints light up.
    /// </summary>
    public void CollectJointLimitStress(List<JointLimitStress> output)
    {
        output.Clear();
        if (simulation == null || !isActive || swingStressMonitors.Count == 0)
            return;

        stressWorstByBone.Clear();
        foreach (var m in swingStressMonitors)
        {
            var child = simulation.Bodies.GetBodyReference(m.Child);
            var parent = simulation.Bodies.GetBodyReference(m.Parent);
            var a = Vector3.Transform(m.AxisLocalChild, child.Pose.Orientation);
            var b = Vector3.Transform(m.AxisLocalParent, parent.Pose.Orientation);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));
            var stress = m.LimitAngle > 0.01f ? angle / m.LimitAngle : 0f;

            if (!stressWorstByBone.TryGetValue(m.Bone, out var worst) || stress > worst.Stress)
                stressWorstByBone[m.Bone] = new JointLimitStress(m.Bone, child.Pose.Position, stress);
        }

        foreach (var entry in stressWorstByBone.Values)
            output.Add(entry);
    }

    // === Knee anti-flip guard ============================================================
    // Twist about a bone's own long axis leaves its segment direction unchanged, so every
    // direction-based constraint is blind to it. The knee's only twist witnesses are the
    // soft ±11° TwistLimit (10 Hz; its measurement wraps at ±180° and then actively holds
    // the flip) and the planar AngularHinge (anti-parallel axes = saddle point). One hard
    // off-axis impulse through the foot can therefore leave the shin twisted ~180° with
    // the foot facing backwards — permanently. Three layers:
    //   L2 (build time) — hinge-axis hemisphere SwingLimit (see the hinge branch).
    //   L1 (per step)   — clamp the twist RATE about the shin axis relative to the thigh,
    //                     so an impulse can't tunnel through the soft wall in one step.
    //   L3 (per step)   — measure the actual twist; past ~100° teleport-unwind the shin
    //                     and everything below it about the shin axis. Points on the axis
    //                     are invariant, so visually the foot simply snaps back forward.
    private const float HingeHemisphereLockAngle = 1.3f;  // ~75°; untouched by legal motion
    private const float TwistGuardMaxRate = 6f;           // rad/s relative to the parent
    private const float HipTwistCeiling = 0.65f;          // ~37°; legal hip rotation caps 30/45°
    private const float KneeTwistCeiling = 0.35f;         // ~20°; legal knee twist ±11°
    private const float AnkleTwistCeiling = 0.9f;         // ~52°; legal ankle twist ±37°
    private const float TwistGuardLogThreshold = 1.4f;    // log genuine flips (>80°), not grazes

    private sealed class TwistGuardMonitor
    {
        public string Bone = "";
        public BodyHandle Child;
        public BodyHandle Parent;
        public Quaternion InitialRelative;
        public float Ceiling;
        public List<BodyHandle> UnwindBodies = new();
    }

    private readonly List<TwistGuardMonitor> twistGuards = new();

    private void RegisterTwistGuard(string boneName, int boneIndex, BodyHandle childHandle,
        BodyHandle parentHandle, BodyReference childBodyRef, BodyReference parentBodyRef, float ceiling)
    {
        var monitor = new TwistGuardMonitor
        {
            Bone = boneName,
            Child = childHandle,
            Parent = parentHandle,
            Ceiling = ceiling,
            InitialRelative = Quaternion.Normalize(
                Quaternion.Inverse(parentBodyRef.Pose.Orientation) * childBodyRef.Pose.Orientation),
        };

        // The child plus every ragdoll descendant — the whole sub-limb rides the unwind
        // rotation so it moves as one piece.
        monitor.UnwindBodies.Add(childHandle);
        var indices = new HashSet<int> { boneIndex };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var other in ragdollBones)
            {
                if (indices.Contains(other.BoneIndex))
                    continue;
                if (other.ParentBoneIndex >= 0 && indices.Contains(other.ParentBoneIndex))
                {
                    indices.Add(other.BoneIndex);
                    monitor.UnwindBodies.Add(other.BodyHandle);
                    grew = true;
                }
            }
        }

        twistGuards.Add(monitor);
    }

    /// <summary>Signed twist (rad, wrapped to ±π) of the child relative to its parent about
    /// the child's long axis, measured against the build-time reference.
    ///
    /// The delta MUST be composed and decomposed in the CHILD-LOCAL frame: a parent-frame
    /// decomposition books most of the twist as swing once the joint is also flexed, so a
    /// bent knee could sit fully flipped while measuring near-zero twist (the governor
    /// then never fires — the original "bent knee still flips" failure). The local
    /// swing-twist factorisation q_local = q_swing ∘ q_twist(Y) extracts the exact twist
    /// component at any flexion, and unwinding by −twist about the child's current world
    /// Y removes exactly that factor, leaving pure swing.</summary>
    private float MeasureGuardTwist(TwistGuardMonitor m, out Vector3 axisWorld)
    {
        var child = simulation!.Bodies.GetBodyReference(m.Child);
        var parent = simulation.Bodies.GetBodyReference(m.Parent);
        axisWorld = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, child.Pose.Orientation));

        var relNow = Quaternion.Normalize(
            Quaternion.Inverse(parent.Pose.Orientation) * child.Pose.Orientation);
        var qLocal = Quaternion.Normalize(Quaternion.Inverse(m.InitialRelative) * relNow);

        var twist = 2f * MathF.Atan2(qLocal.Y, qLocal.W);
        while (twist > MathF.PI) twist -= MathF.Tau;
        while (twist < -MathF.PI) twist += MathF.Tau;
        return twist;
    }

    /// <summary>Rate layer: cap the sub-limb's angular rate about the child axis relative
    /// to the parent. Run both BEFORE and after Timestep — the solver's substeps integrate
    /// poses internally, so a post-step-only clamp lets one impulse flip a joint within a
    /// single outer step.</summary>
    private void ClampTwistRates()
    {
        if (simulation == null || twistGuards.Count == 0)
            return;

        foreach (var m in twistGuards)
        {
            var child = simulation.Bodies.GetBodyReference(m.Child);
            var parent = simulation.Bodies.GetBodyReference(m.Parent);
            var axis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, child.Pose.Orientation));
            var parentRate = Vector3.Dot(parent.Velocity.Angular, axis);

            foreach (var handle in m.UnwindBodies)
            {
                var body = simulation.Bodies.GetBodyReference(handle);
                var w = body.Velocity.Angular;
                var rate = Vector3.Dot(w, axis);
                var relative = rate - parentRate;
                if (MathF.Abs(relative) <= TwistGuardMaxRate)
                    continue;
                var clamped = parentRate + MathF.Sign(relative) * TwistGuardMaxRate;
                body.Velocity.Angular = w + axis * (clamped - rate);
            }
        }
    }

    /// <summary>
    /// Projection layer (PBD-style twist governor): every fixed step, any axial twist
    /// beyond the joint's ceiling is positionally unwound back TO the ceiling. Unlike the
    /// impulse constraints (which wrap at ±180° and then hold a flip, or lose their
    /// gradient at exactly anti-parallel), a projection has no barrier to tunnel through
    /// and no degenerate parking spot — a flipped state cannot survive even one step.
    /// Idle within the legal range; pushes the same direction as the solver's own twist
    /// limits when active, so the two do not fight.
    /// </summary>
    private void ApplyTwistGuards()
    {
        if (simulation == null || twistGuards.Count == 0)
            return;

        foreach (var m in twistGuards)
        {
            var twist = MeasureGuardTwist(m, out var axisWorld);
            var excess = MathF.Abs(twist) - m.Ceiling;
            if (excess <= 0f)
                continue;

            var child = simulation.Bodies.GetBodyReference(m.Child);
            var delta = Quaternion.CreateFromAxisAngle(axisWorld, -MathF.Sign(twist) * excess);
            var pivot = child.Pose.Position;

            foreach (var handle in m.UnwindBodies)
            {
                var body = simulation.Bodies.GetBodyReference(handle);
                body.Pose.Position = pivot + Vector3.Transform(body.Pose.Position - pivot, delta);
                body.Pose.Orientation = Quaternion.Normalize(delta * body.Pose.Orientation);
                // Strip the axial rate that drove past the ceiling so it doesn't rewind.
                var w = body.Velocity.Angular;
                w -= axisWorld * Vector3.Dot(w, axisWorld);
                body.Velocity.Angular = w;
                body.Awake = true;
            }

            if (MathF.Abs(twist) > TwistGuardLogThreshold)
                log.Info($"[Ragdoll] '{m.Bone}' twist flip unwound ({twist * 180f / MathF.PI:F0}°).");
        }
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

        BoneDefs = BuildActivePhysicsDefsForDismemberment(skel, BoneDefs, nameToIndex);

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
        // FallbackBatchThreshold raised from the default 64: the pelvis alone carries the
        // hip/spine/cloth constraint fan-out plus self-collision and NPC-volume contacts,
        // and 2.5.0-beta's SequentialFallbackBatch NREs when removing kinematic-body
        // contacts that overflowed into it. Keep everything in synchronized batches.
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks
            {
                ConnectedPairs = connectedPairs,
                ExternalDynamicBodies = externalDynamicBodyHandles,
                ExternalRigDynamicBodies = externalRigDynamicBodyHandles,
                ExternalRigNoRagdollContactBodies = externalRigNoRagdollContactBodyHandles,
                ExternalRigConnectedPairs = externalRigConnectedPairs,
                ExternalRigSelfCollideGroupByBody = externalRigSelfCollideGroupByBody,
                SoftKinematicBodies = softKinematicBodyHandles,
                Friction = config.RagdollFriction,
                Config = config,
            },
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(solverIterations, Math.Max(1, config.RagdollSolverSubsteps))
            {
                FallbackBatchThreshold = 128,
            });

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
        // Handoff velocity per bone, finite-differenced against the last animated frame so the
        // physics bodies continue the animation's motion instead of starting at rest.
        var handoffSeedVel = new Dictionary<string, (Vector3 Lin, Vector3 Ang)>();
        var canSeedHandoff = config.RagdollCarryAnimationVelocity && handoffSampleValid
            && handoffPrevPos != null && handoffSampleDt > 1e-5f;
        var initialPoseLift = Vector3.Zero;

        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var idx)) continue;
            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            var worldPos = ModelToWorld(modelPos);
            var worldRot = ModelRotToWorld(modelRot);
            boneWorldPositions[def.Name] = worldPos;
            boneWorldRotations[def.Name] = worldRot;

            if (canSeedHandoff && idx < handoffPrevCount)
            {
                var prevWorldPos = handoffSkelPos + Vector3.Transform(handoffPrevPos![idx], handoffSkelRot);
                var prevWorldRot = Quaternion.Normalize(handoffSkelRot * handoffPrevRot![idx]);
                var lin = (worldPos - prevWorldPos) / handoffSampleDt;
                var ang = AngularVelocityFromQuats(prevWorldRot, worldRot, handoffSampleDt);
                handoffSeedVel[def.Name] = (lin, ang);
            }
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
        float resolvedTotalMass = 0f; // Tier D — sum of all enabled bodies' effective mass.
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

            // Tier D — resolve effective mass (anthropometric fraction x body mass, or
            // the hand-picked Mass when the toggle is off / bone not in the table).
            var effectiveMass = ResolveBoneMass(def, config.RagdollAnthropometricMass, config.RagdollBodyMass);
            resolvedTotalMass += effectiveMass;

            TypedIndex shapeIndex;
            BodyInertia bodyInertia;
            if (def.ColliderShape == RagdollColliderShape.Box)
            {
                var extents = ResolveBoxHalfExtents(def, effectiveHalfLength);
                var box = new Box(extents.X * 2f, extents.Y * 2f, extents.Z * 2f);
                shapeIndex = simulation.Shapes.Add(box);
                bodyInertia = box.ComputeInertia(effectiveMass);
            }
            else
            {
                var capsuleLength = effectiveHalfLength * 2;
                var capsule = new Capsule(def.CapsuleRadius, capsuleLength);
                shapeIndex = simulation.Shapes.Add(capsule);
                bodyInertia = capsule.ComputeInertia(effectiveMass);
            }

            // Allow sleeping even with settle collision; external kinematic colliders can wake
            // the corpse on contact, while permanent awake bodies keep solving tiny jitter.
            var sleepThreshold = 0.01f;
            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                bodyInertia,
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(sleepThreshold));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

            // Seed the body with the velocity the death animation was carrying at handoff, so an
            // in-motion animation flows continuously into physics instead of freezing at rest.
            if (handoffSeedVel.TryGetValue(def.Name, out var seed))
            {
                var maxLin = activeRagdollIsGeneric ? GenericMaxLinearVelocity : HumanMaxLinearVelocity;
                var maxAng = activeRagdollIsGeneric ? GenericMaxAngularVelocity : HumanMaxAngularVelocity;
                var scale = MathF.Max(0f, config.RagdollHandoffVelocityScale);
                var br = simulation.Bodies.GetBodyReference(bodyHandle);
                br.Velocity.Linear = ClampVectorLength(seed.Lin * scale, maxLin);
                br.Velocity.Angular = ClampVectorLength(seed.Ang * scale, maxAng);
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

        // Tier D — one-time sanity log: resolved total should be ~RagdollBodyMass minus the
        // excluded cloth/weapon/breast bones (which keep their tiny literal masses).
        if (config.RagdollAnthropometricMass)
            log.Info($"[Ragdoll Init] Anthropometric mass on: resolved total = {resolvedTotalMass:F2} kg (body mass = {config.RagdollBodyMass:F1} kg)");

        // --- Pass 3: Add constraints between connected bones ---
        // Per the BEPU RagdollDemo, each joint gets a layered constraint set:
        //   Ball joints: BallSocket + SwingLimit (cone) + TwistLimit (axial, asymmetric) + AngularMotor
        //   Hinge joints: Hinge + TwistLimit (angular range, asymmetric) + AngularMotor
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        var jointSpring = new SpringSettings(config.RagdollJointSpringFrequency, 1);
        // Foot positional joint is firmer than the body default: it absorbs the hardest
        // ground-impact impulses and otherwise rubber-bands first. Falls back to the body
        // default when the foot frequency is left at 0.
        var footJointFreq = config.RagdollFootJointSpringFrequency > 0f
            ? config.RagdollFootJointSpringFrequency
            : config.RagdollJointSpringFrequency;
        var footJointSpring = new SpringSettings(footJointFreq, 1);
        var limitSpring = new SpringSettings(config.RagdollLimitSpringFrequency, 1);
        // Tier C (swing) — soft-edge spring for BALL swing limits: low frequency +
        // overdamped, so a joint sliding into its range edge decelerates and settles
        // instead of pinning on a hard 90 Hz wall at the extreme angle. Hinges keep
        // the hard wall (a soft knee fold-stop reads as hyperextension).
        var ballSwingSpring = config.RagdollSoftLimits
            ? new SpringSettings(
                Math.Clamp(config.RagdollSoftLimitFrequency, 1f, 60f),
                MathF.Max(0.5f, config.RagdollSoftLimitDamping))
            : limitSpring;
        var motorDamping = 0.01f;

        // Tier C (swing) — anatomical reference axes for the directional ball limits,
        // taken from the character's facing at activation (deaths start standing) and
        // baked into the PARENT body's local frame so they tumble with the body. NOT
        // taken from each limb's death pose — a mid-stride leg must not shift its own
        // abduction/adduction window sideways.
        var anatYaw = targetCharacterAddress != nint.Zero
            ? ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress)->Rotation
            : 0f;
        var anatForwardWorld = new Vector3(MathF.Sin(anatYaw), 0f, MathF.Cos(anatYaw));
        var anatLateralWorld = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, anatForwardWorld));
        swingStressMonitors.Clear();
        twistGuards.Clear();

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

                // Tier B validation — one-time log of the base-construction knee/elbow hinge
                // axis. With RagdollAnatomicalHingeAxis on, L and R should be near-mirror and
                // both roughly aligned with the character left-right axis (NOT one forward).
                if (boneDef.AnatomicalRole == AnatomicalRole.Knee || boneDef.AnatomicalRole == AnatomicalRole.Elbow)
                    log.Info($"[Ragdoll Joint] '{rb.Name}' hinge axis=({hingeAxisWorld.X:F2},{hingeAxisWorld.Y:F2},{hingeAxisWorld.Z:F2}) anatomical={config.RagdollAnatomicalHingeAxis}");

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

                // Planar hinge: pin the bend to the sagittal plane so the knee/elbow folds
                // forward/back only, never sideways. AngularHinge keeps the two hinge axes
                // parallel (removing the 2 off-axis angular DOF) while leaving flexion about the
                // axis free — the BallSocket above already owns position. This is the part the
                // cone SwingLimit can't express (a cone permits any swing direction). Soft spring
                // + the substep solver keep it from re-triggering the stiff full-Hinge freeze.
                if (config.RagdollKneeElbowPlanarHinge)
                {
                    var hingeAxisLocalChild = Vector3.Normalize(Vector3.Transform(
                        hingeAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
                    var hingeAxisLocalParent = Vector3.Normalize(Vector3.Transform(
                        hingeAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new AngularHinge
                        {
                            LocalHingeAxisA = hingeAxisLocalChild,
                            LocalHingeAxisB = hingeAxisLocalParent,
                            SpringSettings = new SpringSettings(
                                Math.Clamp(config.RagdollKneeHingeFrequency, 1f, 60f), 1f),
                        });
                }

                // Hemisphere lock (anti-flip L2): the AngularHinge above keeps the two hinge
                // axes PARALLEL but treats the 180°-flipped (anti-parallel) state as a saddle
                // point it cannot escape, and the soft axial TwistLimit below wraps at ±180°
                // and then HOLDS the flip — the "shin twisted backwards, foot facing the
                // wrong way" stable failure. A direction-vector SwingLimit between the same
                // two axes never engages in legal motion (they stay parallel) but produces a
                // monotonic restoring torque from any flip angle, so the flipped state stops
                // being an equilibrium.
                {
                    var hemiChild = Vector3.Normalize(Vector3.Transform(
                        hingeAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
                    var hemiParent = Vector3.Normalize(Vector3.Transform(
                        hingeAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));
                    simulation.Solver.Add(rb.BodyHandle, parentHandle, new SwingLimit
                    {
                        AxisLocalA = hemiChild,
                        AxisLocalB = hemiParent,
                        MaximumSwingAngle = HingeHemisphereLockAngle,
                        SpringSettings = limitSpring,
                    });
                    swingStressMonitors.Add(new SwingStressMonitor
                    {
                        Bone = rb.Name,
                        Child = rb.BodyHandle,
                        Parent = parentHandle,
                        AxisLocalChild = hemiChild,
                        AxisLocalParent = hemiParent,
                        LimitAngle = HingeHemisphereLockAngle,
                    });
                }

                // Twist governor bookkeeping (knees; hips/ankles register in the ball branch).
                if (boneDef.AnatomicalRole == AnatomicalRole.Knee)
                    RegisterTwistGuard(rb.Name, rb.BoneIndex, rb.BodyHandle, parentHandle,
                        childBodyRef, parentBodyRef, KneeTwistCeiling);

                // Tier C — asymmetric ROM for this hinge (knee/elbow). Resolved once here so
                // it drives the fold-stop cap (C3), the directional flexion limit (C3) and the
                // axial twist guard (C2) below.
                AnatomicalRom hingeRom = default;
                bool hasHingeRom = config.RagdollAnatomicalRom &&
                    (boneDef.AnatomicalRole == AnatomicalRole.Knee || boneDef.AnatomicalRole == AnatomicalRole.Elbow) &&
                    TryGetAnatomicalRom(rb.Name, out hingeRom);

                // C3: when ROM is on, loosen the symmetric fold-stop cap to the anatomical
                // flexion max so it no longer blocks legal flexion before the directional
                // limit does; the directional TwistLimit below now owns hyperextension.
                AddAnatomicalHingeFoldStop(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld, limitSpring,
                    hasHingeRom ? hingeRom.FlexionMax : (float?)null);
                AddAnatomicalHingeRestBias(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                    boneDef, hingeAxisWorld, parentSegDir, segDirWorld);

                // C3: directional flexion/hyperextension limit about the Tier-B hinge axis.
                // This is what actually blocks the knee/elbow bending BACKWARD past (nearly)
                // straight — the symmetric fold-stop above cannot tell flexion from
                // hyperextension. Anatomically anchored + snap-proof (see method).
                if (hasHingeRom)
                    AddAnatomicalHingeFlexionLimit(rb.BodyHandle, parentHandle, childBodyRef, parentBodyRef,
                        boneDef, hingeAxisWorld, parentSegDir, segDirWorld, hingeRom, limitSpring);

                // Axial-rotation guard: prevents the shin/forearm from spinning around its
                // long axis. Anatomical limits differ: the tibia (shin) allows only ~±10°
                // of axial rotation in a relaxed dead leg, so knees use a tight ±0.2 rad
                // (≈±11°). The forearm has genuine pronation/supination (~±80°) so elbows
                // use a wider ±0.8 rad. Soft 10 Hz spring — inequality constraint only fires
                // at the boundary, avoiding the constant large impulses that froze the old
                // Hinge equality constraints.
                {
                    var isKnee = boneDef.AnatomicalRole == AnatomicalRole.Knee;
                    // C2: asymmetric axial twist from the anatomical ROM table when ROM is on
                    // (knee tibial +/-10 deg, elbow pron/sup +/-80 deg); else the legacy
                    // symmetric guard (knee +/-0.2, elbow +/-0.8).
                    var twistMin = isKnee ? -0.2f : -0.8f;
                    var twistMax = isKnee ?  0.2f :  0.8f;
                    if (hasHingeRom)
                    {
                        twistMin = hingeRom.AxialMin;
                        twistMax = hingeRom.AxialMax;
                    }
                    var twistBasis = CreateTwistBasis(segDirWorld, hingeAxisWorld);
                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis),
                            LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis),
                            MinimumAngle = twistMin,
                            MaximumAngle = twistMax,
                            // 10 Hz predates the substep solver and was tunnelled by any
                            // decent impact (the twist-flip failure). Inequality constraint —
                            // only fires at the boundary, so stiff is safe with 8 substeps.
                            SpringSettings = new SpringSettings(40f, 1f),
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
                            : boneDef.AnatomicalRole == AnatomicalRole.Foot
                                ? footJointSpring
                                : jointSpring,
                    });

                // Tier C (swing): hips, arm-side shoulders, and the spine chain get
                // DIRECTIONAL anatomical limits below — a symmetric cone cannot hold
                // flexion 95° / abduction 45° / adduction 25° at once (hip splay), and a
                // pose-anchored cone re-zeroes at the death animation's already-flexed
                // spine, double-counting flexion (the head-buried-between-knees fold).
                // Directional limits are anchored to the anatomical frame instead, so a
                // pre-flexed death pose consumes its own flexion budget; if it froze past
                // budget, the soft edge gently unfolds it — a relaxed-body settle. With
                // ROM on, the cone widens to the largest legal direction (pure backstop).
                AnatomicalRom swingRom = default;
                var hasSwingRom = config.RagdollAnatomicalRom &&
                    (boneDef.AnatomicalRole == AnatomicalRole.Hip ||
                     boneDef.AnatomicalRole == AnatomicalRole.Spine ||
                     (boneDef.AnatomicalRole == AnatomicalRole.Shoulder &&
                      rb.Name.StartsWith("j_ude_a_", StringComparison.Ordinal))) &&
                    TryGetAnatomicalRom(rb.Name, out swingRom);
                var hipKneecapCaps = hasSwingRom && boneDef.AnatomicalRole == AnatomicalRole.Hip;

                // SwingLimit: symmetric cone limiting deviation from initial direction.
                if (boneDef.SwingLimit > 0)
                {
                    var axisChildLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(childBodyRef.Pose.Orientation));
                    var axisParentLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation));

                    var effectiveCone = boneDef.SwingLimit;
                    var coneAxisParentLocal = axisParentLocal;
                    var coneSpring = ballSwingSpring;
                    if (hasSwingRom)
                    {
                        effectiveCone = MathF.Max(effectiveCone, MathF.Max(
                            MathF.Max(swingRom.FlexionMax, swingRom.ExtensionMax),
                            MathF.Max(swingRom.AbductionMax, swingRom.AdductionMax)));

                        // Pure-flexion caps beyond 90° are inexpressible as directional
                        // SwingLimits (90°+θ exceeds 180°), and the diagonal caps only see
                        // a forward tilt's cosine projection — this bounding cone IS the
                        // flexion stop. It must therefore be a hard wall anchored on the
                        // anatomical vertical, not a soft suggestion around the death pose
                        // (side-lying gravity folds the top leg to the chest through a
                        // 12 Hz cone).
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

                    // Anatomical boundary caps are WALLS (hard limitSpring): riding them on
                    // the soft-edge spring let body weight shove the legs 40° past every
                    // limit (the unconstrained-hip look). The soft spring stays only on
                    // the symmetric bounding cone, where the settle aesthetic belongs.
                    if (hasSwingRom)
                        AddDirectionalSwingLimits(rb.Name, rb.BodyHandle, parentHandle,
                            childBodyRef, parentBodyRef, segDirWorld, anchorWorld,
                            anatForwardWorld, anatLateralWorld, swingRom,
                            ballSwingSpring, limitSpring);

                    // Hip axial rotation: capped via the kneecap-facing axis (below),
                    // which stays valid at any flexion — see AddKneecapFacingLimits.
                    if (hipKneecapCaps)
                        AddKneecapFacingLimits(rb.Name, rb.BodyHandle, parentHandle,
                            childBodyRef, parentBodyRef, segDirWorld, anchorWorld,
                            anatForwardWorld, anatLateralWorld, limitSpring);
                }

                // Twist governor bookkeeping: hips and ankles have the same twist-wrap /
                // barrier-tunneling flip modes as knees (the kneecap-facing caps are
                // satisfied again at a full 180° rotation, and the ankle's TwistLimit
                // wraps like any other) — the per-step projection is what actually
                // guarantees no parked flip.
                if (boneDef.AnatomicalRole == AnatomicalRole.Hip)
                    RegisterTwistGuard(rb.Name, rb.BoneIndex, rb.BodyHandle, parentHandle,
                        childBodyRef, parentBodyRef, HipTwistCeiling);
                else if (boneDef.AnatomicalRole == AnatomicalRole.Foot &&
                         rb.Name.StartsWith("j_asi_d_", StringComparison.Ordinal))
                    RegisterTwistGuard(rb.Name, rb.BoneIndex, rb.BodyHandle, parentHandle,
                        childBodyRef, parentBodyRef, AnkleTwistCeiling);

                // TwistLimit: asymmetric axial rotation around the bone's segment axis.
                // Skipped on wide-swing joints where the twist basis becomes unreliable.
                // C2: when ROM is on, take the axial range from the anatomical table (correct
                // clinical asymmetry); otherwise use the hand-set boneDef twist values.
                var ballTwistMin = boneDef.TwistMinAngle;
                var ballTwistMax = boneDef.TwistMaxAngle;
                if (config.RagdollAnatomicalRom && TryGetAnatomicalRom(rb.Name, out var ballRom))
                {
                    ballTwistMin = ballRom.AxialMin;
                    ballTwistMax = ballRom.AxialMax;
                }
                // Hips with ROM: the kneecap-facing caps above replace this TwistLimit —
                // its measurement degenerates past ~90° of swing, i.e. exactly in the
                // kicked-up poses where a corpse thigh would otherwise spin freely.
                if ((ballTwistMin != 0 || ballTwistMax != 0) &&
                    !hipKneecapCaps &&
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
                            MinimumAngle = ballTwistMin,
                            MaximumAngle = ballTwistMax,
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
                var motorHandle = simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularMotor
                    {
                        TargetVelocityLocalA = Vector3.Zero,
                        Settings = new MotorSettings(float.MaxValue, motorDamping),
                    });
                angularMotorByBone[rb.Name] = motorHandle;
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
            totalNpcStatics += (s.IsFallback || s.IsConvexHull || s.IsMesh) ? 1 : s.BoneStatics.Count;
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
    /// Build collision proxies for a live character (enemy, companion, or player).
    /// Discovers bones from the character's skeleton and creates one kinematic capsule
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
        if (config.RagdollNpcCollisionMode == RagdollNpcCollisionMode.ConvexHull)
        {
            BuildConvexHullCollision(address, ns, npcSkelPos, npcSkelRot, label);
            return;
        }

        if (config.RagdollNpcCollisionMode == RagdollNpcCollisionMode.AnimatedMesh)
        {
            if (IsMountObject(address) && BuildAnimatedMeshCollision(address, ns, npcSkelPos, npcSkelRot, label))
                return;
            if (BuildMeshCollision(address, ns, npcSkelPos, npcSkelRot, label))
                return;
        }

        if (config.RagdollNpcCollisionMode == RagdollNpcCollisionMode.Mesh &&
            BuildMeshCollision(address, ns, npcSkelPos, npcSkelRot, label))
            return;

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

        // Pass 2: create the kinematic capsule for each kept segment.
        foreach (var c in candidates)
        {
            var shapeIndex = simulation!.Shapes.Add(new Capsule(c.Radius, c.HalfLen * 2f));
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
                new RigidPose(c.Center, c.Rot),
                default(BodyVelocity),
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(0.01f)));
            boneStatics.Add(new NpcBoneStatic
            {
                Handle = bodyHandle,
                BoneIndex = c.BoneIdx,
                ParentBoneIndex = c.ParentIdx,
                HalfLength = c.HalfLen,
                CenterFactor = c.CenterFactor,
                PreviousPosition = c.Center,
                PreviousOrientation = c.Rot,
                HasPreviousPose = true,
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

    private bool BuildMeshCollision(nint address, SkeletonAccess ns, Vector3 npcSkelPos, Quaternion npcSkelRot, string label)
    {
        if (simulation == null || bufferPool == null)
            return false;

        try
        {
            if (!TryBuildSkinDeltas(ns, out var skinDeltas))
            {
                log.Warning($"RagdollController: {label} mesh collision skipped; skin transforms unavailable");
                return false;
            }

            var triangles = new List<Triangle>(Math.Min(MeshCollisionMaxTriangles, 1024));
            var slotCount = Math.Clamp(ns.CharBase->SlotCount, 0, 32);
            var loadedModels = 0;
            var processedMeshes = 0;
            for (int slot = 0; slot < slotCount && triangles.Count < MeshCollisionMaxTriangles; slot++)
            {
                var renderModel = ns.CharBase->Models == null ? null : ns.CharBase->Models[slot];
                if (renderModel == null || renderModel->ModelResourceHandle == null)
                    continue;

                var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
                var modelPath = resourceHandle->FileName.ToString();
                if (string.IsNullOrWhiteSpace(modelPath) ||
                    !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var mdl = Services.DataManager.GameData.GetFile<MdlFile>(modelPath);
                    if (mdl == null)
                    {
                        log.Warning($"RagdollController: {label} mesh collision slot {slot} '{modelPath}' returned null MdlFile");
                        continue;
                    }

                    loadedModels++;
                    var beforeMeshes = processedMeshes;
                    var beforeTriangles = triangles.Count;
                    processedMeshes += AppendSkinnedMdlTriangles(modelPath, MeshCollisionMdlData.FromMdlFile(mdl), ns, skinDeltas, triangles);
                    if (config.RagdollVerboseLog)
                    {
                        log.Info($"RagdollController: {label} mesh slot {slot} '{modelPath}' geometry-only mdl: meshes={processedMeshes - beforeMeshes}, triangles={triangles.Count - beforeTriangles}");
                    }
                }
                catch (Exception ex)
                {
                    var rawData = TryGetRawFileData(modelPath);
                    string? rawParseError = null;
                    if (rawData != null &&
                        TryParseRawMdlCollisionData(rawData, out var rawMdl, out rawParseError))
                    {
                        loadedModels++;
                        var beforeMeshes = processedMeshes;
                        var beforeTriangles = triangles.Count;
                        processedMeshes += AppendSkinnedMdlTriangles(modelPath, rawMdl, ns, skinDeltas, triangles);
                        log.Warning($"RagdollController: {label} mesh collision mdl parser failed for slot {slot} '{modelPath}' ({ex.GetType().Name}: {ex.Message}), raw geometry fallback used: meshes={processedMeshes - beforeMeshes}, triangles={triangles.Count - beforeTriangles}");
                    }
                    else
                    {
                        var rawLength = rawData?.Length ?? -1;
                        log.Warning(ex, $"RagdollController: {label} mesh collision failed to load mdl slot {slot} '{modelPath}' (rawBytes={(rawLength >= 0 ? rawLength.ToString() : "unavailable")}, rawFallback={rawParseError ?? "unavailable"})");
                    }
                }
            }

            if (triangles.Count < 4)
            {
                log.Warning($"RagdollController: {label} mesh collision produced {triangles.Count} triangles from {loadedModels} models; using bone capsules");
                return false;
            }

            if (TryCreateMeshShape(triangles, GetSkeletonScale(ns), out var shapeIndex))
            {
                var rootRot = Quaternion.Normalize(npcSkelRot);
                var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
                    new RigidPose(npcSkelPos, rootRot),
                    default(BodyVelocity),
                    new CollidableDescription(shapeIndex, 0.04f),
                    new BodyActivityDescription(0.01f)));

                npcCollisionStates.Add(new NpcCollisionState
                {
                    NpcAddress = address,
                    BoneStatics = new List<NpcBoneStatic>(),
                    IsFallback = false,
                    IsMesh = true,
                    MeshHandle = bodyHandle,
                    MeshShapeIndex = shapeIndex,
                    PreviousPosition = npcSkelPos,
                    PreviousOrientation = rootRot,
                    HasPreviousPose = true,
                });

                var meshScale = GetSkeletonScale(ns);
                log.Info($"RagdollController: {label} mesh collision - {triangles.Count} triangles from {loadedModels} models/{processedMeshes} meshes, rootScale=({meshScale.X:F3},{meshScale.Y:F3},{meshScale.Z:F3})");
                return true;
            }

            log.Warning($"RagdollController: {label} mesh collision failed to create BEPU mesh shape; using bone capsules");
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"RagdollController: {label} mesh collision build failed; using bone capsules");
            return false;
        }
    }

    private bool BuildAnimatedMeshCollision(nint address, SkeletonAccess ns, Vector3 npcSkelPos, Quaternion npcSkelRot, string label)
    {
        if (simulation == null || bufferPool == null)
            return false;

        try
        {
            if (!TryBuildSkinDeltas(ns, out var skinDeltas))
            {
                log.Warning($"RagdollController: {label} animated mesh collision skipped; skin transforms unavailable");
                return false;
            }

            var models = new List<AnimatedMeshCollisionModel>();
            var slotCount = Math.Clamp(ns.CharBase->SlotCount, 0, 32);
            for (int slot = 0; slot < slotCount; slot++)
            {
                var renderModel = ns.CharBase->Models == null ? null : ns.CharBase->Models[slot];
                if (renderModel == null || renderModel->ModelResourceHandle == null)
                    continue;

                var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
                var modelPath = resourceHandle->FileName.ToString();
                if (string.IsNullOrWhiteSpace(modelPath) ||
                    !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryLoadMeshCollisionMdlData(label, slot, modelPath, out var mdl) &&
                    TryCreateAnimatedMeshCollisionModel(modelPath, mdl, ns, out var model))
                {
                    models.Add(model);
                }
            }

            if (models.Count == 0)
            {
                log.Warning($"RagdollController: {label} animated mesh collision found no usable models; using static mesh/bone fallback");
                return false;
            }

            var triangles = new List<Triangle>(Math.Min(MeshCollisionMaxTriangles, 1024));
            var processedMeshes = AppendAnimatedMeshTriangles(models, skinDeltas, triangles);
            if (triangles.Count < 4)
            {
                log.Warning($"RagdollController: {label} animated mesh collision produced {triangles.Count} triangles from {models.Count} models; using static mesh/bone fallback");
                return false;
            }

            if (!TryCreateMeshShape(triangles, GetSkeletonScale(ns), out var shapeIndex))
            {
                log.Warning($"RagdollController: {label} animated mesh collision failed to create BEPU mesh shape; using static mesh/bone fallback");
                return false;
            }

            var rootRot = Quaternion.Normalize(npcSkelRot);
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
                new RigidPose(npcSkelPos, rootRot),
                default(BodyVelocity),
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(0.01f)));
            softKinematicBodyHandles.Add(bodyHandle.Value);

            npcCollisionStates.Add(new NpcCollisionState
            {
                NpcAddress = address,
                BoneStatics = new List<NpcBoneStatic>(),
                IsFallback = false,
                IsMesh = true,
                IsAnimatedMesh = true,
                MeshHandle = bodyHandle,
                MeshShapeIndex = shapeIndex,
                AnimatedMeshModels = models,
                AnimatedMeshNextUpdateElapsed = elapsed + AnimatedMeshUpdateInterval,
                AnimatedMeshTriangleCount = triangles.Count,
                AnimatedMeshUpdateCount = 0,
                PreviousPosition = npcSkelPos,
                PreviousOrientation = rootRot,
                HasPreviousPose = true,
            });

            var animMeshScale = GetSkeletonScale(ns);
            log.Info($"RagdollController: {label} animated mesh collision - {triangles.Count} triangles from {models.Count} models/{processedMeshes} meshes, kind={GetObjectKindName(address)}, rootScale=({animMeshScale.X:F3},{animMeshScale.Y:F3},{animMeshScale.Z:F3}), update={AnimatedMeshUpdateInterval:F2}s");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"RagdollController: {label} animated mesh collision build failed; using static mesh/bone fallback");
            return false;
        }
    }

    private bool TryLoadMeshCollisionMdlData(string label, int slot, string modelPath, out MeshCollisionMdlData mdl)
    {
        mdl = new MeshCollisionMdlData();
        try
        {
            var luminaMdl = Services.DataManager.GameData.GetFile<MdlFile>(modelPath);
            if (luminaMdl == null)
            {
                log.Warning($"RagdollController: {label} mesh collision slot {slot} '{modelPath}' returned null MdlFile");
                return false;
            }

            mdl = MeshCollisionMdlData.FromMdlFile(luminaMdl);
            return true;
        }
        catch (Exception ex)
        {
            var rawData = TryGetRawFileData(modelPath);
            string? rawParseError = null;
            if (rawData != null && TryParseRawMdlCollisionData(rawData, out var rawMdl, out rawParseError))
            {
                mdl = rawMdl;
                log.Warning($"RagdollController: {label} mesh collision mdl parser failed for slot {slot} '{modelPath}' ({ex.GetType().Name}: {ex.Message}), raw geometry fallback used");
                return true;
            }

            var rawLength = rawData?.Length ?? -1;
            log.Warning(ex, $"RagdollController: {label} mesh collision failed to load mdl slot {slot} '{modelPath}' (rawBytes={(rawLength >= 0 ? rawLength.ToString() : "unavailable")}, rawFallback={rawParseError ?? "unavailable"})");
            return false;
        }
    }

    private bool TryCreateAnimatedMeshCollisionModel(string modelPath, MeshCollisionMdlData mdl, SkeletonAccess ns, out AnimatedMeshCollisionModel model)
    {
        model = new AnimatedMeshCollisionModel();
        if (!TrySelectMdlLod(mdl, out var lodIndex, out var lod))
        {
            log.Warning($"RagdollController: animated mesh collision '{modelPath}' has no usable LOD");
            return false;
        }

        if (mdl.Data.Length == 0 || mdl.Meshes.Length == 0 || mdl.VertexDeclarations.Length == 0 ||
            mdl.BoneTables.Length == 0 || mdl.BoneNameOffsets.Length == 0 || mdl.Strings.Length == 0)
        {
            log.Warning($"RagdollController: animated mesh collision '{modelPath}' has incomplete MdlFile data");
            return false;
        }

        if (mdl.FileHeader.VertexOffset == null || mdl.FileHeader.VertexBufferSize == null ||
            mdl.FileHeader.IndexOffset == null || mdl.FileHeader.IndexBufferSize == null ||
            lodIndex >= mdl.FileHeader.VertexOffset.Length || lodIndex >= mdl.FileHeader.VertexBufferSize.Length ||
            lodIndex >= mdl.FileHeader.IndexOffset.Length || lodIndex >= mdl.FileHeader.IndexBufferSize.Length)
        {
            log.Warning($"RagdollController: animated mesh collision '{modelPath}' has incomplete LOD{lodIndex} buffer header");
            return false;
        }

        if (!TryGetIntRange(mdl.FileHeader.VertexOffset[lodIndex], mdl.FileHeader.VertexBufferSize[lodIndex], mdl.Data.Length, out _, out _) ||
            !TryGetIntRange(mdl.FileHeader.IndexOffset[lodIndex], mdl.FileHeader.IndexBufferSize[lodIndex], mdl.Data.Length, out _, out _))
        {
            log.Warning($"RagdollController: animated mesh collision '{modelPath}' LOD{lodIndex} buffer ranges are outside file data");
            return false;
        }

        var processedMeshIndices = new HashSet<int>();
        AddAnimatedMeshRange(mdl, lod.MeshIndex, lod.MeshCount, processedMeshIndices);
        if (mdl.ExtraLodEnabled && lodIndex < mdl.ExtraLods.Length)
        {
            var extra = mdl.ExtraLods[lodIndex];
            AddAnimatedMeshRange(mdl, extra.GlassMeshIndex, extra.GlassMeshCount, processedMeshIndices);
            AddAnimatedMeshRange(mdl, extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount, processedMeshIndices);
            AddAnimatedMeshRange(mdl, extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount, processedMeshIndices);
        }

        if (processedMeshIndices.Count == 0)
            return false;

        var boneMaps = new Dictionary<int, int[]>();
        foreach (var meshIndex in processedMeshIndices)
        {
            if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length)
                continue;
            boneMaps[meshIndex] = BuildMdlMeshBoneMap(mdl, mdl.Meshes[meshIndex], ns);
        }

        model = new AnimatedMeshCollisionModel
        {
            ModelPath = modelPath,
            Mdl = mdl,
            LodIndex = lodIndex,
            MeshIndices = processedMeshIndices.OrderBy(x => x).ToArray(),
            BoneMapsByMeshIndex = boneMaps,
        };
        return true;
    }

    private static void AddAnimatedMeshRange(MeshCollisionMdlData mdl, ushort meshStart, ushort meshCount, HashSet<int> meshIndices)
    {
        var meshEnd = Math.Min(mdl.Meshes.Length, meshStart + meshCount);
        for (int meshIndex = meshStart; meshIndex < meshEnd; meshIndex++)
            meshIndices.Add(meshIndex);
    }

    private int AppendAnimatedMeshTriangles(List<AnimatedMeshCollisionModel> models, Matrix4x4[] skinDeltas, List<Triangle> triangles)
    {
        var processedMeshes = 0;
        foreach (var model in models)
        {
            foreach (var meshIndex in model.MeshIndices)
            {
                if (triangles.Count >= MeshCollisionMaxTriangles)
                    return processedMeshes;
                if (!model.BoneMapsByMeshIndex.TryGetValue(meshIndex, out var localToHavok))
                    localToHavok = Array.Empty<int>();
                if (AppendSkinnedMdlMeshTriangles(model.ModelPath, model.Mdl, model.LodIndex, meshIndex, localToHavok, skinDeltas, triangles))
                    processedMeshes++;
            }
        }

        return processedMeshes;
    }

    // Read the skeleton root's world scale. Skinned vertices live in the skeleton's local
    // model space (unscaled); the visible model applies the root Transform's scale on top,
    // so we must bake it into the BEPU mesh shape or the collision hull ends up the wrong
    // size (many mounts render at a non-unit root scale).
    private static Vector3 GetSkeletonScale(SkeletonAccess ns)
    {
        if (ns.CharBase == null || ns.CharBase->Skeleton == null)
            return Vector3.One;

        var s = ns.CharBase->Skeleton->Transform.Scale;
        var scale = new Vector3(s.X, s.Y, s.Z);
        if (!IsFinite(scale) || scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
            return Vector3.One;

        return scale;
    }

    private bool TryCreateMeshShape(List<Triangle> triangles, Vector3 scale, out TypedIndex shapeIndex)
    {
        shapeIndex = default;
        if (simulation == null || bufferPool == null || triangles.Count < 4)
            return false;

        Buffer<Triangle> triangleBuffer = default;
        var ownsTriangleBuffer = false;
        try
        {
            bufferPool.Take<Triangle>(triangles.Count, out triangleBuffer);
            ownsTriangleBuffer = true;
            for (int i = 0; i < triangles.Count; i++)
                triangleBuffer[i] = triangles[i];

            var meshShape = new BepuPhysics.Collidables.Mesh(triangleBuffer, scale, bufferPool);
            ownsTriangleBuffer = false;
            shapeIndex = simulation.Shapes.Add(meshShape);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ownsTriangleBuffer)
                bufferPool.Return(ref triangleBuffer);
        }
    }

    private static bool IsMountObject(nint address)
    {
        if (address == nint.Zero)
            return false;

        var go = (GameObject*)address;
        return go->ObjectKind == ObjectKind.Mount;
    }

    private static string GetObjectKindName(nint address)
    {
        if (address == nint.Zero)
            return "None";

        var go = (GameObject*)address;
        return go->ObjectKind.ToString();
    }

    private bool TryBuildSkinDeltas(SkeletonAccess ns, out Matrix4x4[] skinDeltas)
    {
        skinDeltas = Array.Empty<Matrix4x4>();
        if (ns.HavokSkeleton == null || ns.Pose == null ||
            ns.HavokSkeleton->ReferencePose.Data == null ||
            ns.Pose->ModelPose.Data == null)
            return false;

        var boneCount = Math.Min(ns.BoneCount, Math.Min(ns.ParentCount,
            Math.Min(ns.HavokSkeleton->ReferencePose.Length, ns.Pose->ModelPose.Length)));
        if (boneCount <= 0)
            return false;

        var refModel = new Matrix4x4[boneCount];
        skinDeltas = new Matrix4x4[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            var refLocal = QsToMatrix(ns.HavokSkeleton->ReferencePose.Data[i]);
            var parent = ns.HavokSkeleton->ParentIndices[i];
            refModel[i] = parent >= 0 && parent < i
                ? refLocal * refModel[parent]
                : refLocal;
        }

        for (int i = 0; i < boneCount; i++)
        {
            var curModel = QsToMatrix(ns.Pose->ModelPose.Data[i]);
            if (!Matrix4x4.Invert(refModel[i], out var invRef))
                invRef = Matrix4x4.Identity;
            skinDeltas[i] = invRef * curModel;
        }

        return true;
    }

    private sealed class MeshCollisionMdlData
    {
        public byte[] Data = Array.Empty<byte>();
        public MdlStructs.ModelFileHeader FileHeader;
        public MdlStructs.VertexDeclarationStruct[] VertexDeclarations = Array.Empty<MdlStructs.VertexDeclarationStruct>();
        public MdlStructs.LodStruct[] Lods = Array.Empty<MdlStructs.LodStruct>();
        public MdlStructs.ExtraLodStruct[] ExtraLods = Array.Empty<MdlStructs.ExtraLodStruct>();
        public MdlStructs.MeshStruct[] Meshes = Array.Empty<MdlStructs.MeshStruct>();
        public uint[] BoneNameOffsets = Array.Empty<uint>();
        public MdlStructs.BoneTableStruct[] BoneTables = Array.Empty<MdlStructs.BoneTableStruct>();
        public byte[] Strings = Array.Empty<byte>();
        public bool ExtraLodEnabled;

        public static MeshCollisionMdlData FromMdlFile(MdlFile mdl) => new()
        {
            Data = mdl.Data ?? Array.Empty<byte>(),
            FileHeader = mdl.FileHeader,
            VertexDeclarations = mdl.VertexDeclarations ?? Array.Empty<MdlStructs.VertexDeclarationStruct>(),
            Lods = mdl.Lods ?? Array.Empty<MdlStructs.LodStruct>(),
            ExtraLods = mdl.ExtraLods ?? Array.Empty<MdlStructs.ExtraLodStruct>(),
            Meshes = mdl.Meshes ?? Array.Empty<MdlStructs.MeshStruct>(),
            BoneNameOffsets = mdl.BoneNameOffsets ?? Array.Empty<uint>(),
            BoneTables = mdl.BoneTables ?? Array.Empty<MdlStructs.BoneTableStruct>(),
            Strings = mdl.Strings ?? Array.Empty<byte>(),
            ExtraLodEnabled = mdl.ModelHeader.ExtraLodEnabled,
        };
    }

    private int AppendSkinnedMdlTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (!TrySelectMdlLod(mdl, out var lodIndex, out var lod))
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has no usable LOD");
            return 0;
        }

        if (mdl.Data.Length == 0 || mdl.Meshes.Length == 0 || mdl.VertexDeclarations.Length == 0 ||
            mdl.BoneTables.Length == 0 || mdl.BoneNameOffsets.Length == 0 || mdl.Strings.Length == 0)
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has incomplete MdlFile data");
            return 0;
        }
        if (mdl.FileHeader.VertexOffset == null || mdl.FileHeader.VertexBufferSize == null ||
            mdl.FileHeader.IndexOffset == null || mdl.FileHeader.IndexBufferSize == null ||
            lodIndex >= mdl.FileHeader.VertexOffset.Length || lodIndex >= mdl.FileHeader.VertexBufferSize.Length ||
            lodIndex >= mdl.FileHeader.IndexOffset.Length || lodIndex >= mdl.FileHeader.IndexBufferSize.Length)
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has incomplete LOD{lodIndex} buffer header");
            return 0;
        }

        if (!TryGetIntRange(mdl.FileHeader.VertexOffset[lodIndex], mdl.FileHeader.VertexBufferSize[lodIndex], mdl.Data.Length, out _, out _) ||
            !TryGetIntRange(mdl.FileHeader.IndexOffset[lodIndex], mdl.FileHeader.IndexBufferSize[lodIndex], mdl.Data.Length, out _, out _))
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' LOD{lodIndex} buffer ranges are outside file data");
            return 0;
        }

        var processedMeshIndices = new HashSet<int>();
        var processedMeshes = 0;
        processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, lod.MeshIndex, lod.MeshCount,
            ns, skinDeltas, triangles, processedMeshIndices);

        if (mdl.ExtraLodEnabled && lodIndex < mdl.ExtraLods.Length)
        {
            var extra = mdl.ExtraLods[lodIndex];
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.GlassMeshIndex, extra.GlassMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
        }

        return processedMeshes;
    }

    private int AppendSkinnedMdlMeshRange(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        ushort meshStart,
        ushort meshCount,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles,
        HashSet<int> processedMeshIndices)
    {
        if (meshCount == 0)
            return 0;

        var meshEnd = Math.Min(mdl.Meshes.Length, meshStart + meshCount);
        var processedMeshes = 0;
        for (int meshIndex = meshStart; meshIndex < meshEnd && triangles.Count < MeshCollisionMaxTriangles; meshIndex++)
        {
            if (!processedMeshIndices.Add(meshIndex))
                continue;
            if (AppendSkinnedMdlMeshTriangles(modelPath, mdl, lodIndex, meshIndex, ns, skinDeltas, triangles))
                processedMeshes++;
        }

        return processedMeshes;
    }

    private bool AppendSkinnedMdlMeshTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length || meshIndex >= mdl.VertexDeclarations.Length)
            return false;

        var mesh = mdl.Meshes[meshIndex];
        if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
            return false;

        var localToHavok = BuildMdlMeshBoneMap(mdl, mesh, ns);
        return AppendSkinnedMdlMeshTriangles(modelPath, mdl, lodIndex, meshIndex, localToHavok, skinDeltas, triangles);
    }

    private bool AppendSkinnedMdlMeshTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        int[] localToHavok,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length || meshIndex >= mdl.VertexDeclarations.Length)
            return false;

        var mesh = mdl.Meshes[meshIndex];
        if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
            return false;

        var skinnedVertices = new Vector3[mesh.VertexCount];
        for (int i = 0; i < skinnedVertices.Length; i++)
        {
            var vertex = ReadMdlCollisionVertex(mdl.Data, mdl.FileHeader.VertexOffset[lodIndex],
                mesh, mdl.VertexDeclarations[meshIndex], i);
            skinnedVertices[i] = SkinVertex(vertex, localToHavok, skinDeltas);
        }

        var indexByteOffset = (ulong)mdl.FileHeader.IndexOffset[lodIndex] + ((ulong)mesh.StartIndex * 2UL);
        var indexByteLength = (ulong)mesh.IndexCount * 2UL;
        if (!TryGetIntRange(indexByteOffset, indexByteLength, mdl.Data.Length, out var indexStart, out _))
        {
            if (config.RagdollVerboseLog)
                log.Warning($"RagdollController: mesh collision '{modelPath}' mesh {meshIndex} index range outside file data");
            return false;
        }

        var indexData = mdl.Data.AsSpan(indexStart, checked((int)indexByteLength));
        for (int i = 0; i + 2 < mesh.IndexCount && triangles.Count < MeshCollisionMaxTriangles; i += 3)
        {
            var ia = ReadUInt16(indexData, i * 2);
            var ib = ReadUInt16(indexData, (i + 1) * 2);
            var ic = ReadUInt16(indexData, (i + 2) * 2);
            if (ia >= skinnedVertices.Length || ib >= skinnedVertices.Length || ic >= skinnedVertices.Length)
                continue;

            var a = skinnedVertices[ia];
            var b = skinnedVertices[ib];
            var c = skinnedVertices[ic];
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                continue;
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-8f)
                continue;

            // Reverse the model's triangle winding. BEPU meshes are one-sided (a body only
            // collides with a triangle's front face). FFXIV model geometry is authored in a
            // left-handed space, so feeding its raw winding into BEPU's right-handed normal
            // convention points every face INWARD — the ragdoll then falls straight through
            // the mount's outer shell and catches on the interior. Swapping b/c flips the
            // normals outward so the corpse rests on the surface.
            triangles.Add(new Triangle(a, c, b));
        }

        return true;
    }

    private int[] BuildMdlMeshBoneMap(MeshCollisionMdlData mdl, MdlStructs.MeshStruct mesh, SkeletonAccess ns)
    {
        if (mesh.BoneTableIndex == 255 || mesh.BoneTableIndex >= mdl.BoneTables.Length)
            return Array.Empty<int>();

        var boneTable = mdl.BoneTables[mesh.BoneTableIndex].BoneIndex;
        if (boneTable == null || boneTable.Length == 0)
            return Array.Empty<int>();

        var result = new int[boneTable.Length];
        Array.Fill(result, -1);

        var resolved = 0;
        string? firstUnresolved = null;
        for (int i = 0; i < boneTable.Length; i++)
        {
            var globalBoneIndex = boneTable[i];
            if (globalBoneIndex >= mdl.BoneNameOffsets.Length)
                continue;

            var boneName = ReadNullTerminatedString(mdl.Strings, mdl.BoneNameOffsets[globalBoneIndex]);
            if (string.IsNullOrEmpty(boneName))
                continue;

            result[i] = boneService.ResolveBoneIndex(ns, boneName);
            if (result[i] >= 0)
                resolved++;
            else
                firstUnresolved ??= boneName;
        }

        // When no bone name resolves against the target skeleton, every vertex weighted to this
        // mesh falls back to its bind pose in SkinVertex — the collision hull freezes in the
        // reference pose instead of the animated one. Surface that: it's the prime suspect for a
        // mesh whose shape doesn't match the on-screen (posed) model.
        if (resolved == 0)
        {
            // Decisive dump: is the bone table / bone-name-offset section drifted, or is the
            // string base off? Show the raw global bone indices, the offsets they map to, and
            // the strings actually read there, plus the section sizes.
            var probe = new System.Text.StringBuilder();
            var probeCount = Math.Min(6, boneTable.Length);
            for (int i = 0; i < probeCount; i++)
            {
                var gi = boneTable[i];
                var off = gi < mdl.BoneNameOffsets.Length ? (long)mdl.BoneNameOffsets[gi] : -1;
                var name = off >= 0 ? ReadNullTerminatedString(mdl.Strings, (uint)off) : "<oob>";
                probe.Append($" [{i}]gi={gi}->off={off}'{name}'");
            }
            log.Warning($"RagdollController: mesh collision bone map resolved 0/{boneTable.Length} bones (e.g. '{firstUnresolved}'); btIdx={mesh.BoneTableIndex}, boneNameOffsets={mdl.BoneNameOffsets.Length}, stringsLen={mdl.Strings.Length};{probe}");
        }
        else if (config.RagdollVerboseLog)
            log.Info($"RagdollController: mesh collision bone map resolved {resolved}/{boneTable.Length} bones");

        return result;
    }

    private struct MeshCollisionVertex
    {
        public Vector4? Position;
        public Vector4? BlendWeights;
        public byte[]? BlendIndices;
    }

    private static MeshCollisionVertex ReadMdlCollisionVertex(
        byte[] data,
        uint lodVertexOffset,
        MdlStructs.MeshStruct mesh,
        MdlStructs.VertexDeclarationStruct declaration,
        int vertexIndex)
    {
        var vertex = new MeshCollisionVertex();
        foreach (var element in declaration.VertexElements)
        {
            var usage = (CollisionVertexUsage)element.Usage;
            if (usage != CollisionVertexUsage.Position &&
                usage != CollisionVertexUsage.BlendWeights &&
                usage != CollisionVertexUsage.BlendIndices)
                continue;

            if (element.Stream >= mesh.VertexBufferOffset.Length ||
                element.Stream >= mesh.VertexBufferStride.Length)
                continue;

            var stride = mesh.VertexBufferStride[element.Stream];
            if (stride == 0)
                continue;

            var elementSize = GetVertexElementSize(element.Type);
            if (elementSize <= 0)
                continue;

            var elementOffset = (ulong)lodVertexOffset +
                                mesh.VertexBufferOffset[element.Stream] +
                                ((ulong)vertexIndex * stride) +
                                element.Offset;
            if (!TryGetIntRange(elementOffset, (uint)elementSize, data.Length, out var start, out var length))
                continue;

            var bytes = data.AsSpan(start, length);
            switch (usage)
            {
                case CollisionVertexUsage.Position:
                    if (TryReadVertexVector(bytes, element.Type, out var position))
                        vertex.Position = position;
                    break;
                case CollisionVertexUsage.BlendWeights:
                    if (TryReadVertexVector(bytes, element.Type, out var weights))
                        vertex.BlendWeights = weights;
                    break;
                case CollisionVertexUsage.BlendIndices:
                    if (element.Type == (byte)CollisionVertexType.UInt && bytes.Length >= 4)
                        vertex.BlendIndices = new[] { bytes[0], bytes[1], bytes[2], bytes[3] };
                    break;
            }
        }

        return vertex;
    }

    private static Vector3 SkinVertex(MeshCollisionVertex vertex, int[] localToHavok, Matrix4x4[] skinDeltas)
    {
        if (vertex.Position == null)
            return Vector3.Zero;

        var p4 = vertex.Position.Value;
        var bindPos = new Vector3(p4.X, p4.Y, p4.Z);
        if (vertex.BlendWeights == null || vertex.BlendIndices == null || localToHavok.Length == 0)
            return bindPos;

        var weights = vertex.BlendWeights.Value;
        var blendIndices = vertex.BlendIndices;
        var skinned = Vector3.Zero;
        var weightSum = 0f;
        var influenceCount = Math.Min(4, blendIndices.Length);
        for (int i = 0; i < influenceCount; i++)
        {
            var weight = GetBlendWeight(weights, i);
            if (weight <= 0f)
                continue;

            var localBoneIndex = blendIndices[i];
            if (localBoneIndex >= localToHavok.Length)
                continue;

            var havokIndex = localToHavok[localBoneIndex];
            if (havokIndex < 0 || havokIndex >= skinDeltas.Length)
                continue;

            skinned += Vector3.Transform(bindPos, skinDeltas[havokIndex]) * weight;
            weightSum += weight;
        }

        return weightSum > 1e-5f ? skinned / weightSum : bindPos;
    }

    private static float GetBlendWeight(Vector4 weights, int index) => index switch
    {
        0 => weights.X,
        1 => weights.Y,
        2 => weights.Z,
        3 => weights.W,
        _ => 0f,
    };

    private enum CollisionVertexType : byte
    {
        Single3 = 2,
        Single4 = 3,
        UInt = 5,
        ByteFloat4 = 8,
        Half2 = 13,
        Half4 = 14,
    }

    private enum CollisionVertexUsage : byte
    {
        Position = 0,
        BlendWeights = 1,
        BlendIndices = 2,
    }

    private static int GetVertexElementSize(byte type) => (CollisionVertexType)type switch
    {
        CollisionVertexType.Single3 => 12,
        CollisionVertexType.Single4 => 16,
        CollisionVertexType.UInt => 4,
        CollisionVertexType.ByteFloat4 => 4,
        CollisionVertexType.Half2 => 4,
        CollisionVertexType.Half4 => 8,
        _ => 0,
    };

    private static bool TryReadVertexVector(ReadOnlySpan<byte> bytes, byte type, out Vector4 value)
    {
        value = default;
        switch ((CollisionVertexType)type)
        {
            case CollisionVertexType.Single3:
                if (bytes.Length < 12) return false;
                value = new Vector4(ReadSingle(bytes, 0), ReadSingle(bytes, 4), ReadSingle(bytes, 8), 1f);
                return true;
            case CollisionVertexType.Single4:
                if (bytes.Length < 16) return false;
                value = new Vector4(ReadSingle(bytes, 0), ReadSingle(bytes, 4), ReadSingle(bytes, 8), ReadSingle(bytes, 12));
                return true;
            case CollisionVertexType.ByteFloat4:
                if (bytes.Length < 4) return false;
                value = new Vector4(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
                return true;
            case CollisionVertexType.Half2:
                if (bytes.Length < 4) return false;
                value = new Vector4(ReadHalf(bytes, 0), ReadHalf(bytes, 2), 0f, 0f);
                return true;
            case CollisionVertexType.Half4:
                if (bytes.Length < 8) return false;
                value = new Vector4(ReadHalf(bytes, 0), ReadHalf(bytes, 2), ReadHalf(bytes, 4), ReadHalf(bytes, 6));
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseRawMdlCollisionData(byte[] data, out MeshCollisionMdlData mdl, out string? error)
    {
        mdl = new MeshCollisionMdlData();
        error = null;
        try
        {
            var span = data.AsSpan();
            var offset = 0;

            if (!TryReadModelFileHeader(span, ref offset, out var fileHeader))
                return FailRawMdl("file header truncated", out mdl, out error);

            if (fileHeader.VertexDeclarationCount > 512)
                return FailRawMdl($"unreasonable vertex declaration count {fileHeader.VertexDeclarationCount}", out mdl, out error);

            var vertexDeclarations = new MdlStructs.VertexDeclarationStruct[fileHeader.VertexDeclarationCount];
            for (int i = 0; i < vertexDeclarations.Length; i++)
            {
                if (!TryReadVertexDeclaration(span, ref offset, out vertexDeclarations[i]))
                    return FailRawMdl($"vertex declaration {i} truncated", out mdl, out error);
            }

            if (!TryReadUInt16(span, ref offset, out var stringCount) ||
                !TrySkip(span, ref offset, 2) ||
                !TryReadUInt32(span, ref offset, out var stringByteCount))
                return FailRawMdl("string table header truncated", out mdl, out error);

            if (stringCount > 20000 || stringByteCount > data.Length)
                return FailRawMdl($"unreasonable string table count={stringCount} bytes={stringByteCount}", out mdl, out error);
            if (!TryReadBytes(span, ref offset, checked((int)stringByteCount), out var strings))
                return FailRawMdl("string table truncated", out mdl, out error);

            if (!TryReadModelHeader(span, ref offset, out var header, out var extraLodEnabled))
                return FailRawMdl("model header truncated", out mdl, out error);
            if (header.MeshCount > 20000 || header.BoneCount > 20000 || header.BoneTableCount > 4096)
                return FailRawMdl($"unreasonable model counts meshes={header.MeshCount} bones={header.BoneCount} boneTables={header.BoneTableCount}", out mdl, out error);

            if (!TrySkip(span, ref offset, checked(header.ElementIdCount * 32)))
                return FailRawMdl("element id section truncated", out mdl, out error);

            var lods = new MdlStructs.LodStruct[3];
            for (int i = 0; i < lods.Length; i++)
            {
                if (!TryReadLod(span, ref offset, out lods[i]))
                    return FailRawMdl($"lod {i} truncated", out mdl, out error);
            }

            var extraLods = Array.Empty<MdlStructs.ExtraLodStruct>();
            if (extraLodEnabled)
            {
                extraLods = new MdlStructs.ExtraLodStruct[3];
                for (int i = 0; i < extraLods.Length; i++)
                {
                    if (!TryReadExtraLod(span, ref offset, out extraLods[i]))
                        return FailRawMdl($"extra lod {i} truncated", out mdl, out error);
                }
            }

            var meshes = new MdlStructs.MeshStruct[header.MeshCount];
            for (int i = 0; i < meshes.Length; i++)
            {
                if (!TryReadMesh(span, ref offset, out meshes[i]))
                    return FailRawMdl($"mesh {i} truncated", out mdl, out error);
            }

            if (!TrySkip(span, ref offset, checked(header.AttributeCount * 4)))
                return FailRawMdl("attribute offsets truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.TerrainShadowMeshCount * 20)))
                return FailRawMdl("terrain shadow meshes truncated", out mdl, out error);
            // SubmeshStruct is 16 bytes (uint IndexOffset + uint IndexCount + uint
            // AttributeIndexMask + ushort BoneStartIndex + ushort BoneCount), NOT 20.
            // Over-skipping by 4 bytes/submesh drifted the cursor past BoneNameOffsets into
            // the bone tables, so bone name lookups read garbage and skinning silently fell
            // back to the bind pose.
            if (!TrySkip(span, ref offset, checked(header.SubmeshCount * 16)))
                return FailRawMdl("submeshes truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.TerrainShadowSubmeshCount * 12)))
                return FailRawMdl("terrain shadow submeshes truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.MaterialCount * 4)))
                return FailRawMdl("material offsets truncated", out mdl, out error);

            var boneNameOffsets = new uint[header.BoneCount];
            for (int i = 0; i < boneNameOffsets.Length; i++)
            {
                if (!TryReadUInt32(span, ref offset, out boneNameOffsets[i]))
                    return FailRawMdl($"bone name offset {i} truncated", out mdl, out error);
            }

            var boneTables = new MdlStructs.BoneTableStruct[header.BoneTableCount];
            for (int i = 0; i < boneTables.Length; i++)
            {
                if (!TryReadBoneTable(span, ref offset, out boneTables[i]))
                    return FailRawMdl($"bone table {i} truncated", out mdl, out error);
            }

            mdl = new MeshCollisionMdlData
            {
                Data = data,
                FileHeader = fileHeader,
                VertexDeclarations = vertexDeclarations,
                Lods = lods,
                ExtraLods = extraLods,
                Meshes = meshes,
                BoneNameOffsets = boneNameOffsets,
                BoneTables = boneTables,
                Strings = strings.ToArray(),
                ExtraLodEnabled = extraLodEnabled,
            };
            return true;
        }
        catch (Exception ex)
        {
            return FailRawMdl(ex.Message, out mdl, out error);
        }
    }

    private static bool FailRawMdl(string message, out MeshCollisionMdlData mdl, out string error)
    {
        mdl = new MeshCollisionMdlData();
        error = message;
        return false;
    }

    private static bool TryReadModelFileHeader(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ModelFileHeader header)
    {
        header = default;
        if (!TryReadUInt32(data, ref offset, out header.Version) ||
            !TryReadUInt32(data, ref offset, out header.StackSize) ||
            !TryReadUInt32(data, ref offset, out header.RuntimeSize) ||
            !TryReadUInt16(data, ref offset, out header.VertexDeclarationCount) ||
            !TryReadUInt16(data, ref offset, out header.MaterialCount))
            return false;

        header.VertexOffset = new uint[3];
        header.IndexOffset = new uint[3];
        header.VertexBufferSize = new uint[3];
        header.IndexBufferSize = new uint[3];
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.VertexOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.IndexOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.VertexBufferSize[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.IndexBufferSize[i])) return false;
        if (!TryReadByte(data, ref offset, out header.LodCount) ||
            !TrySkip(data, ref offset, 3))
            return false;
        return true;
    }

    private static bool TryReadVertexDeclaration(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.VertexDeclarationStruct declaration)
    {
        declaration = default;
        if (!TryGetIntRange((ulong)offset, 136, data.Length, out var start, out _))
            return false;

        var elements = new List<MdlStructs.VertexElement>();
        for (int elementOffset = 0; elementOffset + 8 <= 136; elementOffset += 8)
        {
            var stream = data[start + elementOffset];
            if (stream == byte.MaxValue)
                break;
            elements.Add(new MdlStructs.VertexElement
            {
                Stream = stream,
                Offset = data[start + elementOffset + 1],
                Type = data[start + elementOffset + 2],
                Usage = data[start + elementOffset + 3],
                UsageIndex = data[start + elementOffset + 4],
            });
        }

        offset += 136;
        declaration.VertexElements = elements.ToArray();
        return true;
    }

    private static bool TryReadModelHeader(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ModelHeader header, out bool extraLodEnabled)
    {
        header = default;
        extraLodEnabled = false;
        if (!TryReadSingle(data, ref offset, out header.Radius) ||
            !TryReadUInt16(data, ref offset, out header.MeshCount) ||
            !TryReadUInt16(data, ref offset, out header.AttributeCount) ||
            !TryReadUInt16(data, ref offset, out header.SubmeshCount) ||
            !TryReadUInt16(data, ref offset, out header.MaterialCount) ||
            !TryReadUInt16(data, ref offset, out header.BoneCount) ||
            !TryReadUInt16(data, ref offset, out header.BoneTableCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeMeshCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeValueCount) ||
            !TryReadByte(data, ref offset, out header.LodCount) ||
            !TryReadByte(data, ref offset, out _) ||
            !TryReadUInt16(data, ref offset, out header.ElementIdCount) ||
            !TryReadByte(data, ref offset, out header.TerrainShadowMeshCount) ||
            !TryReadByte(data, ref offset, out var flags2) ||
            !TryReadSingle(data, ref offset, out header.ModelClipOutDistance) ||
            !TryReadSingle(data, ref offset, out header.ShadowClipOutDistance) ||
            !TryReadUInt16(data, ref offset, out header.Unknown4) ||
            !TryReadUInt16(data, ref offset, out header.TerrainShadowSubmeshCount) ||
            !TryReadByte(data, ref offset, out _) ||
            !TryReadByte(data, ref offset, out header.BGChangeMaterialIndex) ||
            !TryReadByte(data, ref offset, out header.BGCrestChangeMaterialIndex) ||
            !TryReadByte(data, ref offset, out header.Unknown6) ||
            !TryReadUInt16(data, ref offset, out header.Unknown7) ||
            !TryReadUInt16(data, ref offset, out header.Unknown8) ||
            !TryReadUInt16(data, ref offset, out header.Unknown9) ||
            !TrySkip(data, ref offset, 6))
            return false;

        extraLodEnabled = (flags2 & 0x10) != 0;
        return true;
    }

    private static bool TryReadLod(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.LodStruct lod)
    {
        lod = default;
        return TryReadUInt16(data, ref offset, out lod.MeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.MeshCount) &&
               TryReadSingle(data, ref offset, out lod.ModelLodRange) &&
               TryReadSingle(data, ref offset, out lod.TextureLodRange) &&
               TryReadUInt16(data, ref offset, out lod.WaterMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.WaterMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.ShadowMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.ShadowMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.TerrainShadowMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.TerrainShadowMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.VerticalFogMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.VerticalFogMeshCount) &&
               TryReadUInt32(data, ref offset, out lod.EdgeGeometrySize) &&
               TryReadUInt32(data, ref offset, out lod.EdgeGeometryDataOffset) &&
               TryReadUInt32(data, ref offset, out lod.PolygonCount) &&
               TryReadUInt32(data, ref offset, out lod.Unknown1) &&
               TryReadUInt32(data, ref offset, out lod.VertexBufferSize) &&
               TryReadUInt32(data, ref offset, out lod.IndexBufferSize) &&
               TryReadUInt32(data, ref offset, out lod.VertexDataOffset) &&
               TryReadUInt32(data, ref offset, out lod.IndexDataOffset);
    }

    private static bool TryReadExtraLod(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ExtraLodStruct lod)
    {
        lod = default;
        return TryReadUInt16(data, ref offset, out lod.LightShaftMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.LightShaftMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.GlassMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.GlassMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.MaterialChangeMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.MaterialChangeMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.CrestChangeMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.CrestChangeMeshCount) &&
               TrySkip(data, ref offset, 24);
    }

    private static bool TryReadMesh(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.MeshStruct mesh)
    {
        mesh = default;
        mesh.VertexBufferOffset = new uint[3];
        mesh.VertexBufferStride = new byte[3];
        if (!TryReadUInt16(data, ref offset, out mesh.VertexCount) ||
            !TrySkip(data, ref offset, 2) ||
            !TryReadUInt32(data, ref offset, out mesh.IndexCount) ||
            !TryReadUInt16(data, ref offset, out mesh.MaterialIndex) ||
            !TryReadUInt16(data, ref offset, out mesh.SubMeshIndex) ||
            !TryReadUInt16(data, ref offset, out mesh.SubMeshCount) ||
            !TryReadUInt16(data, ref offset, out mesh.BoneTableIndex) ||
            !TryReadUInt32(data, ref offset, out mesh.StartIndex))
            return false;

        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out mesh.VertexBufferOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadByte(data, ref offset, out mesh.VertexBufferStride[i])) return false;
        return TryReadByte(data, ref offset, out mesh.VertexStreamCount);
    }

    private static bool TryReadBoneTable(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.BoneTableStruct table)
    {
        table = default;
        table.BoneIndex = new ushort[64];
        for (int i = 0; i < table.BoneIndex.Length; i++)
            if (!TryReadUInt16(data, ref offset, out table.BoneIndex[i])) return false;
        if (!TryReadByte(data, ref offset, out table.BoneCount))
            return false;
        return TrySkip(data, ref offset, 3);
    }

    private static bool TrySelectMdlLod(MeshCollisionMdlData mdl, out int lodIndex, out MdlStructs.LodStruct lod)
    {
        lodIndex = -1;
        lod = default;
        if (mdl.Lods == null || mdl.Lods.Length == 0)
            return false;

        var lodCount = Math.Min(mdl.Lods.Length, Math.Max(1, (int)mdl.FileHeader.LodCount));
        var preferred = Math.Min(MeshCollisionPreferredLod, lodCount - 1);
        for (int i = preferred; i >= 0; i--)
        {
            if (mdl.Lods[i].MeshCount == 0)
                continue;
            lodIndex = i;
            lod = mdl.Lods[i];
            return true;
        }

        for (int i = preferred + 1; i < lodCount; i++)
        {
            if (mdl.Lods[i].MeshCount == 0)
                continue;
            lodIndex = i;
            lod = mdl.Lods[i];
            return true;
        }

        return false;
    }

    private static bool TryGetIntRange(ulong offset, ulong length, int dataLength, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (offset > int.MaxValue || length > int.MaxValue)
            return false;
        var end = offset + length;
        if (end < offset || end > (ulong)dataLength)
            return false;
        start = (int)offset;
        count = (int)length;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > data.Length || count > data.Length - offset)
            return false;
        offset += count;
        return true;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> data, ref int offset, int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (count < 0 || offset < 0 || offset > data.Length || count > data.Length - offset)
            return false;
        value = data.Slice(offset, count);
        offset += count;
        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        value = 0;
        if (offset < 0 || offset >= data.Length)
            return false;
        value = data[offset++];
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 2)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 4)
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, ref int offset, out float value)
    {
        value = 0f;
        if (offset < 0 || offset > data.Length - 4)
            return false;
        value = ReadSingle(data, offset);
        offset += 4;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));

    private static float ReadHalf(ReadOnlySpan<byte> data, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)));

    private static int TryGetRawFileLength(string modelPath)
    {
        try
        {
            return Services.DataManager.GameData.GetFile(modelPath)?.Data?.Length ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static byte[]? TryGetRawFileData(string modelPath)
    {
        try
        {
            return Services.DataManager.GameData.GetFile(modelPath)?.Data;
        }
        catch
        {
            return null;
        }
    }

    private static Matrix4x4 QsToMatrix(HkQsTransform transform)
    {
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        var rotation = Quaternion.Normalize(new Quaternion(
            transform.Rotation.X,
            transform.Rotation.Y,
            transform.Rotation.Z,
            transform.Rotation.W));
        var translation = new Vector3(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static string ReadNullTerminatedString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length)
            return string.Empty;

        var start = (int)offset;
        var end = start;
        while (end < strings.Length && strings[end] != 0)
            end++;

        return Encoding.UTF8.GetString(strings, start, end - start);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

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
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
                new RigidPose(worldCenter, npcSkelRot),
                default(BodyVelocity),
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(0.01f)));

            npcCollisionStates.Add(new NpcCollisionState
            {
                NpcAddress = address,
                BoneStatics = new List<NpcBoneStatic>(),
                IsFallback = false,
                IsConvexHull = true,
                ConvexHullHandle = bodyHandle,
                HullCenterModelSpace = hullCenter,
                PreviousPosition = worldCenter,
                PreviousOrientation = npcSkelRot,
                HasPreviousPose = true,
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
        var handle = simulation!.Bodies.Add(BodyDescription.CreateKinematic(
            new RigidPose(npcPos, Quaternion.Identity),
            default(BodyVelocity),
            new CollidableDescription(npcFallbackShapeIndex, 0.04f),
            new BodyActivityDescription(0.01f)));
        npcCollisionStates.Add(new NpcCollisionState
        {
            NpcAddress = address,
            BoneStatics = new List<NpcBoneStatic>(),
            FallbackHandle = handle,
            IsFallback = true,
            PreviousPosition = npcPos,
            PreviousOrientation = Quaternion.Identity,
            HasPreviousPose = true,
        });
        log.Info($"RagdollController: {label} using fallback single capsule");
    }

    /// <summary>Move all of a character's collision proxies to a far-away park position
    /// so they stop colliding with the ragdoll.</summary>
    private void ParkNpcStatics(ref NpcCollisionState npcState, Vector3 parkPos, float dt = FixedTimestep)
    {
        if (simulation == null) return;
        if (npcState.IsMesh)
        {
            MoveNpcKinematic(ref npcState, npcState.MeshHandle, parkPos, Quaternion.Identity, dt);
            return;
        }
        if (npcState.IsConvexHull)
        {
            MoveNpcKinematic(ref npcState, npcState.ConvexHullHandle, parkPos, Quaternion.Identity, dt);
            return;
        }
        if (npcState.IsFallback)
        {
            MoveNpcKinematic(ref npcState, npcState.FallbackHandle, parkPos, Quaternion.Identity, dt);
            return;
        }
        for (int b = 0; b < npcState.BoneStatics.Count; b++)
        {
            var bs = npcState.BoneStatics[b];
            MoveNpcBoneKinematic(ref bs, parkPos, Quaternion.Identity, dt);
            npcState.BoneStatics[b] = bs;
        }
    }

    private void MoveNpcKinematic(ref NpcCollisionState state, BodyHandle handle,
        Vector3 targetPosition, Quaternion targetOrientation, float dt)
    {
        if (simulation == null) return;
        var body = simulation.Bodies.GetBodyReference(handle);
        MoveKinematicBody(body, targetPosition, targetOrientation,
            ref state.PreviousPosition, ref state.PreviousOrientation, ref state.HasPreviousPose, dt);
    }

    private void TryUpdateAnimatedMeshCollision(ref NpcCollisionState state, SkeletonAccess ns, float nowElapsed)
    {
        if (!state.IsAnimatedMesh || state.AnimatedMeshModels == null || state.AnimatedMeshModels.Count == 0 ||
            simulation == null || bufferPool == null || nowElapsed < state.AnimatedMeshNextUpdateElapsed)
            return;

        state.AnimatedMeshNextUpdateElapsed = nowElapsed + AnimatedMeshUpdateInterval;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!TryBuildSkinDeltas(ns, out var skinDeltas))
            {
                state.AnimatedMeshNextUpdateElapsed = nowElapsed + AnimatedMeshSlowUpdateBackoff;
                return;
            }

            var triangles = new List<Triangle>(Math.Min(MeshCollisionMaxTriangles, Math.Max(1024, state.AnimatedMeshTriangleCount)));
            var processedMeshes = AppendAnimatedMeshTriangles(state.AnimatedMeshModels, skinDeltas, triangles);
            if (triangles.Count < 4 || !TryCreateMeshShape(triangles, GetSkeletonScale(ns), out var newShapeIndex))
            {
                state.AnimatedMeshNextUpdateElapsed = nowElapsed + AnimatedMeshSlowUpdateBackoff;
                if (config.RagdollVerboseLog)
                    log.Warning($"RagdollController: animated mesh update skipped for 0x{state.NpcAddress:X}; triangles={triangles.Count}, meshes={processedMeshes}");
                return;
            }

            var oldShapeIndex = state.MeshShapeIndex;
            simulation.Bodies.SetShape(state.MeshHandle, newShapeIndex);
            var body = simulation.Bodies.GetBodyReference(state.MeshHandle);
            body.Awake = true;
            state.MeshShapeIndex = newShapeIndex;
            state.AnimatedMeshTriangleCount = triangles.Count;
            state.AnimatedMeshUpdateCount++;

            try { simulation.Shapes.RemoveAndDispose(oldShapeIndex, bufferPool); } catch { }

            stopwatch.Stop();
            if (state.AnimatedMeshUpdateCount <= AnimatedMeshInitialUpdateLogCount ||
                (config.RagdollVerboseLog && state.AnimatedMeshUpdateCount % 30 == 0))
            {
                log.Info($"RagdollController: animated mesh updated for 0x{state.NpcAddress:X} - #{state.AnimatedMeshUpdateCount}, {triangles.Count} triangles/{processedMeshes} meshes in {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
            }

            if (stopwatch.Elapsed.TotalMilliseconds > AnimatedMeshSlowUpdateMs)
            {
                state.AnimatedMeshNextUpdateElapsed = nowElapsed + AnimatedMeshSlowUpdateBackoff;
                if (config.RagdollVerboseLog)
                    log.Info($"RagdollController: animated mesh update throttled for 0x{state.NpcAddress:X}; {stopwatch.Elapsed.TotalMilliseconds:F2}ms, triangles={triangles.Count}, meshes={processedMeshes}");
            }
        }
        catch (Exception ex)
        {
            state.AnimatedMeshNextUpdateElapsed = nowElapsed + AnimatedMeshSlowUpdateBackoff;
            if (config.RagdollVerboseLog)
                log.Warning(ex, $"RagdollController: animated mesh update failed for 0x{state.NpcAddress:X}");
        }
    }

    private void MoveNpcBoneKinematic(ref NpcBoneStatic bone, Vector3 targetPosition, Quaternion targetOrientation, float dt)
    {
        if (simulation == null) return;
        var body = simulation.Bodies.GetBodyReference(bone.Handle);
        MoveKinematicBody(body, targetPosition, targetOrientation,
            ref bone.PreviousPosition, ref bone.PreviousOrientation, ref bone.HasPreviousPose, dt);
    }

    private static void MoveKinematicBody(BodyReference body, Vector3 targetPosition, Quaternion targetOrientation,
        ref Vector3 previousPosition, ref Quaternion previousOrientation, ref bool hasPreviousPose, float dt)
    {
        targetOrientation = Quaternion.Normalize(targetOrientation);
        var linearVelocity = hasPreviousPose && dt > 1e-5f
            ? (targetPosition - previousPosition) / dt
            : Vector3.Zero;
        var angularVelocity = hasPreviousPose && dt > 1e-5f
            ? AngularVelocityFromQuats(previousOrientation, targetOrientation, dt)
            : Vector3.Zero;

        body.Pose.Position = targetPosition;
        body.Pose.Orientation = targetOrientation;
        body.Velocity.Linear = linearVelocity;
        body.Velocity.Angular = angularVelocity;
        body.Awake = true;

        previousPosition = targetPosition;
        previousOrientation = targetOrientation;
        hasPreviousPose = true;
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

        // The local player is always object-table index 0. Used below to choose a render-only
        // follow (DrawObject.Position, never the packet-synced GameObject.Position) and to
        // suppress the self-induced biomechanical settle while follow drags the corpse.
        var isLocalPlayerTarget =
            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress)->ObjectIndex == 0;

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
                // A follow-driven move (we pushed the DrawObject to track the flung corpse)
                // must not restart the biomechanical settle — otherwise a corpse sliding under
                // follow wakes its bodies every frame and never rests. Only settle on EXTERNAL
                // repositions (dismount / death transition). followWasActive means we wrote a
                // follow position on the previous frame, so this frame's skeleton move is ours.
                // Ground is still re-raycast below in both cases so the floor tracks the corpse.
                if (!(config.RagdollFollowPosition && followWasActive && isLocalPlayerTarget))
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
        var settleCollisionActive = config.RagdollNpcCollision && config.RagdollNpcSettleCollision;
        var externalBodyAwake = AnyExternalBodyAwake();
        var resting = !settleCollisionActive && prevAllAsleep && !externalBodyAwake && !grabConstraintActive && !collapseSpikeActive && !directedCollapseActive && !wholeBodyCollapseActive && !entryConditioningActive && !kneePowerLossActive && !skeletonMoved && !biomechanicalSettleActive;

        // Death-collapse spike: restrength the per-joint "muscle" servos before stepping so the
        // updated torque ceilings take effect this tick. Advances the fade and retires itself.
        UpdateCollapseSpike(dt);
        UpdateDirectedCollapseSpike(dt);
        UpdateWholeBodyCollapse(dt);
        UpdateCollapseEntryConditioning(dt);
        UpdateKneePowerLossPattern(dt);

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
                    if (npcState.IsMesh)
                    {
                        MoveNpcKinematic(ref npcState, npcState.MeshHandle, npcCollisionParkPos, Quaternion.Identity, dt);
                    }
                    else if (npcState.IsConvexHull)
                    {
                        MoveNpcKinematic(ref npcState, npcState.ConvexHullHandle, npcCollisionParkPos, Quaternion.Identity, dt);
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
                        ParkNpcStatics(ref npcState, npcCollisionParkPos, dt);
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

                if (npcState.IsMesh)
                {
                    var npcSkelMesh = boneService.TryGetSkeleton(npcState.NpcAddress);
                    Vector3 meshWorldPos;
                    Quaternion meshWorldRot;
                    if (npcSkelMesh != null && npcSkelMesh.Value.CharBase->Skeleton != null)
                    {
                        var sk = npcSkelMesh.Value.CharBase->Skeleton;
                        meshWorldPos = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
                        meshWorldRot = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);
                        if (npcState.IsAnimatedMesh)
                            TryUpdateAnimatedMeshCollision(ref npcState, npcSkelMesh.Value, elapsed);
                    }
                    else
                    {
                        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npcState.NpcAddress;
                        meshWorldPos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
                        meshWorldRot = Quaternion.Identity;
                    }

                    MoveNpcKinematic(ref npcState, npcState.MeshHandle, meshWorldPos, meshWorldRot, dt);
                    npcCollisionStates[i] = npcState;
                    continue;
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
                    MoveNpcKinematic(ref npcState, npcState.ConvexHullHandle, hullWorldPos, hullWorldRot, dt);
                    npcCollisionStates[i] = npcState;
                    continue;
                }

                if (npcState.IsFallback)
                {
                    // Simple single-capsule position update
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npcState.NpcAddress;
                    MoveNpcKinematic(ref npcState, npcState.FallbackHandle,
                        new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z),
                        Quaternion.Identity, dt);
                    npcCollisionStates[i] = npcState;
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

                    var bodyRef = simulation.Bodies.GetBodyReference(bs.Handle);
                    var oldPos = bodyRef.Pose.Position;
                    MoveNpcBoneKinematic(ref bs, capsuleCenter, capsuleRot, dt);
                    npcState.BoneStatics[b] = bs;

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
                npcCollisionStates[i] = npcState;
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

        // Restore any recoil-relaxed joint motors whose window has elapsed.
        TickRecoilRelaxers(dt);

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
                ClampTwistRates(); // pre-step too: the solver's substeps integrate poses internally
                simulation.Timestep(FixedTimestep);
                ClampVelocities(maxLinear, maxAngular);
                ClampTwistRates();
                ApplyTwistGuards();
                ApplyStandingAnchorCorrection();
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
        if (AnyExternalBodyAwake()) anyAwake = true;

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

        // Dismemberment POC: collapse each selected limb's whole bone subtree to ~0 scale so it
        // vanishes from the body (the "hide" half of hide-and-substitute). Done last so it overrides
        // the physics/propagation writes for those bones.
        var severedBones = useLocalDismemberBones ? localDismemberBones : config.DismemberPocBones;
        if (severedBones != null && severedBones.Count > 0)
            foreach (var boneName in severedBones)
                HideLimbSubtree(skel, boneName);

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
        if (config.RagdollFollowPosition && ragdollBones.Count > 0 && targetCharacterAddress != nint.Zero)
        {
            try
            {
                var rootBody = simulation.Bodies.GetBodyReference(ragdollBones[0].BodyHandle);
                var rootPos = rootBody.Pose.Position;
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;

                if (isLocalPlayerTarget)
                {
                    var drawObject = gameObj->DrawObject;
                    if (drawObject != null)
                    {
                        ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)drawObject)->Position = rootPos;
                        drawObject->NotifyTransformChanged();
                    }
                }
                else if (movementBlockHook != null)
                {
                    movementBlockHook.SetApproachPosition(gameObj, rootPos.X, rootPos.Y, rootPos.Z);
                }
            }
            catch { }
            followWasActive = true;
        }
        else if (followWasActive && targetCharacterAddress != nint.Zero)
        {
            // Toggled off — restore the frozen death position on whichever transform we moved.
            try
            {
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetCharacterAddress;
                if (isLocalPlayerTarget)
                {
                    var drawObject = gameObj->DrawObject;
                    if (drawObject != null)
                    {
                        ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)drawObject)->Position = savedCharacterPosition;
                        drawObject->NotifyTransformChanged();
                    }
                }
                else if (movementBlockHook != null)
                {
                    movementBlockHook.SetApproachPosition(gameObj,
                        savedCharacterPosition.X, savedCharacterPosition.Y, savedCharacterPosition.Z);
                }
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

        var normalThreshold = 0.01f;
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

    // --- Death-collapse spike (experimental proof-of-concept) ---
    // Active-ragdoll spike: instead of letting the corpse go instantly limp, snapshot the
    // current ("death-instant") relative orientation of every joint and pin it with an
    // AngularServo ("muscle tone"), then fade each joint's strength toward zero so gravity
    // drives a controlled collapse. The per-tier fade order/curve decides the collapse style.
    // NOTE: this reuses the AngularServo *mechanism* only and is tuned from scratch — it
    // inherits nothing from (and proves nothing about) the poor soft-body bones.
    public enum CollapseArchetype { StiffHold, UniformCollapse }
    public enum CollapseDirection { None, Random, Forward, Backward, Sideways }

    private struct CollapseServo
    {
        public ConstraintHandle Handle;
        public int Tier;          // 0 = legs, 1 = core (pelvis/spine), 2 = upper body
        public Quaternion Target; // captured TargetRelativeRotationLocalA
        public float Freq;        // this joint's spring frequency
        public float MaxForce;    // this joint's full torque ceiling (gain multiplies this)
        public BodyHandle ChildBody;  // body A — for reading the live relative rotation
        public BodyHandle ParentBody; // body B
        public float TimeShift;   // staged-failure + lead-side offset (s); + = this joint fails sooner
    }

    private readonly List<CollapseServo> collapseServos = new();
    private bool collapseSpikeActive;
    private CollapseArchetype collapseArchetype;
    private float collapseElapsed;
    // +1 / -1: which side leads the collapse this death (its leg buckles first, body leans/twists
    // toward it). Shared between the spike and the fused whole-body topple.
    private float collapseLeadSign = 1f;
    private float collapseHold;
    private float collapseFade;
    private float collapseFreq;
    private float collapseMaxForce;
    private const float CollapseDamping = 1f;
    private const float CollapseMaxSpeed = 12f;

    // --- Eccentric braking (the muscle "pays out" under load instead of going slack) ---
    // Real failing muscle keeps resisting while it lengthens (eccentric contraction), so a real
    // collapse is *braked* — it sinks, gets slowed, sinks again — not a free-fall to limp. After
    // the position-hold gain fades, we don't drop the joint to zero torque; we hand it to a
    // velocity-damping "brake" servo: target tracks the *current* orientation (so there's no
    // spring snap-back, it can't hold the pose), but a high damping ratio + residual force ceiling
    // resists angular velocity, so the descent stays slow and controlled. The brake itself then
    // tails off over collapseBrakeFade and the joint retires to the passive ragdoll.
    private const float CollapseBrakeDamping = 4f;     // overdamped while paying out (vs critical=1)
    private const float CollapseBrakeFreqScale = 0.5f; // soften the position spring as it releases
    // Per-profile (GuidedCollapse.Relaxation): set in BeginCollapseSpike from config.
    private float collapseBrakeForceFrac = 0.3f; // residual torque ceiling = fraction of full
    private float collapseBrakeFade = 0.7f;      // seconds the brake decays after the hold is gone

    // One-shot request to auto-begin the spike the instant physics initializes, so it captures
    // the *standing death-instant* pose before gravity collapses it. Manual BeginCollapseSpike
    // after death is too late — the body has already crumpled by the time you click. Arm this
    // before triggering death instead.
    private bool pendingCollapseSpike;
    private CollapseArchetype pendingCollapseArchetype;
    private float pendingCollapseStrength;
    private float pendingCollapseHold;
    private float pendingCollapseFade;
    private float pendingCollapseHinge;
    private CollapseDirection pendingCollapseDirection;
    private float pendingCollapseImpulse;

    public bool CollapseSpikeActive => collapseSpikeActive;

    private void ArmConfiguredGuidedCollapse()
    {
        var guided = config.GuidedCollapse;
        if (!guided.Enabled) return;

        if (guided.Mode == 1)
        {
            RequestEntryConditionedKneePowerLossOnReady();
            return;
        }

        var relaxation = guided.Relaxation;
        var archetype = relaxation.Archetype == 0
            ? CollapseArchetype.StiffHold
            : CollapseArchetype.UniformCollapse;
        var direction = (CollapseDirection)Math.Clamp(relaxation.Direction, 0, 4);

        // Fuse a whole-body COM topple with the muscle-failure spike: the body loses balance over
        // its support base and falls like an inverted pendulum while the joints brake, instead of
        // sagging in place plus a one-shot shove. Only for the collapsing archetype (StiffHold is a
        // permanent hold and must not be toppled). The topple drives direction, so suppress the
        // legacy single impulse to avoid double-pushing.
        var topple = config.RagdollRelaxationTopple && archetype == CollapseArchetype.UniformCollapse;

        RequestCollapseSpikeOnReady(
            archetype,
            Math.Clamp(relaxation.Strength, 0.5f, 40f),
            Math.Clamp(relaxation.Hold, 0f, 5f),
            Math.Clamp(relaxation.Fade, 0.05f, 8f),
            Math.Clamp(relaxation.HingeSoften, 0f, 1f),
            direction,
            topple ? 0f : Math.Clamp(relaxation.Impulse, 0f, 8f));

        if (topple)
            RequestWholeBodyCollapseOnReady(direction, fuseWithSpike: true);
    }

    /// <summary>
    /// Arm the collapse spike to fire automatically the moment the ragdoll's physics is ready
    /// (capturing the standing death-instant pose). If the ragdoll is already simulating, begins
    /// immediately. Call this BEFORE triggering death so the spike captures the upright pose.
    /// </summary>
    public void RequestCollapseSpikeOnReady(CollapseArchetype archetype, float strength, float holdDuration, float fadeDuration, float hingeFactor,
        CollapseDirection direction = CollapseDirection.None, float impulse = 0f)
    {
        if (IsSimulationReady)
        {
            if (BeginCollapseSpike(archetype, strength, holdDuration, fadeDuration, hingeFactor))
                ApplyCollapseDirectionImpulse(direction, impulse);
            return;
        }
        pendingCollapseArchetype = archetype;
        pendingCollapseStrength = strength;
        pendingCollapseHold = holdDuration;
        pendingCollapseFade = fadeDuration;
        pendingCollapseHinge = hingeFactor;
        pendingCollapseDirection = direction;
        pendingCollapseImpulse = impulse;
        pendingCollapseSpike = true;
        log.Info($"RagdollController: collapse spike armed for next death — archetype={archetype}");
    }

    /// <summary>
    /// Begin the death-collapse spike: snapshot every parented body's current relative
    /// orientation, pin it with an AngularServo, then fade strength to zero per the chosen
    /// archetype. <paramref name="strength"/> is the servo spring frequency (Hz); the max
    /// corrective torque scales with it. Returns false if the ragdoll isn't simulating.
    /// </summary>
    public bool BeginCollapseSpike(CollapseArchetype archetype, float strength, float holdDuration, float fadeDuration, float hingeFactor)
    {
        if (simulation == null || !isActive) return false;
        StopCollapseSpike();
        hingeFactor = Math.Clamp(hingeFactor, 0f, 1f);

        collapseArchetype = archetype;
        collapseElapsed = 0f;
        collapseHold = MathF.Max(0f, holdDuration);
        collapseFade = MathF.Max(0.01f, fadeDuration);
        collapseFreq = MathF.Max(0.5f, strength);
        // Torque ceiling scales with strength so a weaker "muscle" both responds softer and
        // saturates sooner. Spike default — tuned live from the GUI, not gospel.
        collapseMaxForce = strength * 60f;

        // Eccentric brake shape — per-profile (GuidedCollapse.Relaxation).
        var relax = config.GuidedCollapse.Relaxation;
        collapseBrakeForceFrac = Math.Clamp(relax.BrakeStrength, 0f, 1f);
        collapseBrakeFade = Math.Clamp(relax.BrakeFade, 0.05f, 4f);

        // Asymmetry + staged failure: pick a lead side (its leg buckles first) and stagger the
        // muscle groups in time so legs give before trunk before arms.
        var asymmetry = Math.Clamp(config.RagdollCollapseAsymmetry, 0f, 1f);
        collapseLeadSign = ShakeRng.Next(2) == 0 ? -1f : 1f;
        var staged = config.RagdollStagedFailure;

        var handleByBoneIdx = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            handleByBoneIdx[rb.BoneIndex] = rb.BodyHandle;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!handleByBoneIdx.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            var child = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var parent = simulation.Bodies.GetBodyReference(parentHandle);
            child.Awake = true;
            parent.Awake = true;

            // Preserve the current relative orientation: OrientationB = OrientationA * T.
            var target = Quaternion.Normalize(
                Quaternion.Inverse(child.Pose.Orientation) * parent.Pose.Orientation);

            activeDefByName.TryGetValue(rb.Name, out var def);
            var tier = CollapseTier(def.AnatomicalRole);

            // A hinge (knee/elbow) needs to FLEX to kneel; pinning its full orientation at the
            // standing angle makes it a rigid strut that can't buckle. Scale its servo down so it
            // yields under body weight (controlled buckle) instead of locking. Ball joints
            // (spine/hips/shoulders/neck) keep full strength.
            var isHinge = def.Joint == JointType.Hinge;
            var jointFreq = isHinge ? collapseFreq * 0.6f : collapseFreq;
            var jointForce = isHinge ? collapseMaxForce * hingeFactor : collapseMaxForce;

            // Per-joint failure timing: staged group stagger + lead-side leg gives soonest.
            var timeShift = staged ? StagedTierTimeShift(tier) : 0f;
            if (asymmetry > 0f && IsLowerLimbRole(def.AnatomicalRole)
                && BoneSideSign(rb.Name) == collapseLeadSign)
                timeShift += asymmetry * LeadLegTimeShift;

            var handle = simulation.Solver.Add(rb.BodyHandle, parentHandle,
                new AngularServo
                {
                    TargetRelativeRotationLocalA = target,
                    SpringSettings = new SpringSettings(jointFreq, CollapseDamping),
                    ServoSettings = new ServoSettings(CollapseMaxSpeed, 0f, jointForce),
                });

            collapseServos.Add(new CollapseServo
            {
                Handle = handle, Tier = tier, Target = target, Freq = jointFreq, MaxForce = jointForce,
                ChildBody = rb.BodyHandle, ParentBody = parentHandle, TimeShift = timeShift,
            });
        }

        if (collapseServos.Count == 0) return false;

        collapseSpikeActive = true;
        prevAllAsleep = false;
        log.Info($"RagdollController: collapse spike begun — archetype={archetype} servos={collapseServos.Count} freq={collapseFreq:F1} maxForce={collapseMaxForce:F0} hingeFactor={hingeFactor:F2} hold={collapseHold:F2} fade={collapseFade:F2}");
        return true;
    }

    private void ApplyCollapseDirectionImpulse(CollapseDirection direction, float impulse)
    {
        if (simulation == null || direction == CollapseDirection.None || impulse <= 0.001f) return;

        var dir = ResolveCollapseDirection(direction);
        if (dir.LengthSquared() < 0.0001f) return;

        var handle = FindBodyHandle("j_sebo_c")
            ?? FindBodyHandle("j_sebo_b")
            ?? FindBodyHandle("j_kosi");
        if (!handle.HasValue) return;

        WakeRagdollBodiesForBiomechanicalSettle();
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Linear += dir * impulse;
        body.Awake = true;
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: collapse direction impulse - direction={direction} vector=({dir.X:F2},{dir.Y:F2},{dir.Z:F2}) impulse={impulse:F2}");
    }

    private Vector3 ResolveCollapseDirection(CollapseDirection direction)
    {
        if (direction == CollapseDirection.Random)
        {
            var roll = ShakeRng.Next(118); // video-fall survey weighting: forward 44, sideways 41, backward 33.
            direction = roll < 44
                ? CollapseDirection.Forward
                : roll < 85
                    ? CollapseDirection.Sideways
                    : CollapseDirection.Backward;
        }

        var forward = FlatNormalize(Vector3.Transform(Vector3.UnitZ, skelWorldRot), Vector3.UnitZ);
        var right = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        return direction switch
        {
            CollapseDirection.Forward => forward,
            CollapseDirection.Backward => -forward,
            CollapseDirection.Sideways => ShakeRng.Next(2) == 0 ? -right : right,
            _ => Vector3.Zero,
        };
    }

    private static Vector3 FlatNormalize(Vector3 v, Vector3 fallback)
    {
        v.Y = 0f;
        return v.LengthSquared() > 0.0001f ? Vector3.Normalize(v) : fallback;
    }

    public void StopCollapseSpike()
    {
        if (simulation != null)
            foreach (var s in collapseServos)
                try { simulation.Solver.Remove(s.Handle); } catch { }

        collapseServos.Clear();
        if (collapseSpikeActive)
        {
            collapseSpikeActive = false;
            BeginBiomechanicalSettle();
            log.Info("RagdollController: collapse spike stopped");
        }
    }

    private static int CollapseTier(AnatomicalRole role) => role switch
    {
        AnatomicalRole.Hip or AnatomicalRole.Knee or AnatomicalRole.Ankle or AnatomicalRole.Foot => 0,
        AnatomicalRole.Pelvis or AnatomicalRole.Spine => 1,
        _ => 2,
    };

    private static bool IsLowerLimbRole(AnatomicalRole role) =>
        role is AnatomicalRole.Hip or AnatomicalRole.Knee or AnatomicalRole.Ankle or AnatomicalRole.Foot;

    // +1 for left bones, -1 for right, 0 for midline — used to pick the lead (first-to-buckle) side.
    private static float BoneSideSign(string name)
        => name.EndsWith("_l", StringComparison.Ordinal) ? 1f
         : name.EndsWith("_r", StringComparison.Ordinal) ? -1f : 0f;

    private const float LeadLegTimeShift = 0.25f; // lead-side leg fails up to this many seconds sooner

    // Staged muscle failure: shift each tier's fade earlier (+) or later (-) so groups give in
    // sequence — legs first, trunk in the middle, arms trailing last.
    private static float StagedTierTimeShift(int tier) => tier switch
    {
        0 => 0.18f,   // legs give first
        2 => -0.18f,  // arms trail last
        _ => 0f,      // trunk in the middle
    };

    /// <summary>Position-hold strength in [0,1] for a joint whose failure is shifted in time by
    /// <paramref name="timeShift"/> seconds (+ = fails sooner). Drives the staged + asymmetric
    /// collapse.</summary>
    private float CollapseGain(float timeShift)
    {
        switch (collapseArchetype)
        {
            case CollapseArchetype.StiffHold:
                return 1f; // never fades — pure "can the servos hold the pose?" test

            case CollapseArchetype.UniformCollapse:
            {
                var t = collapseElapsed + timeShift - collapseHold;
                if (t <= 0f) return 1f;
                return Math.Clamp(1f - t / collapseFade, 0f, 1f);
            }
        }
        return 1f;
    }

    /// <summary>Velocity-brake envelope in [0,1]: full while the position-hold fades, then decays
    /// over collapseBrakeFade so the joint pays out slowly before retiring. StiffHold has no
    /// brake phase (it never releases).</summary>
    private float CollapseBrake()
    {
        if (collapseArchetype == CollapseArchetype.StiffHold) return 0f;
        var tAfterHold = collapseElapsed - collapseHold - collapseFade; // time since stiffness fully gone
        if (tAfterHold <= 0f) return 1f;
        return Math.Clamp(1f - tAfterHold / collapseBrakeFade, 0f, 1f);
    }

    /// <summary>Advance the collapse spike: blend each joint from position-hold to an eccentric
    /// velocity brake, then retire to the passive ragdoll once hold and brake are both spent.</summary>
    private void UpdateCollapseSpike(float dt)
    {
        if (!collapseSpikeActive || simulation == null) return;
        collapseElapsed += dt;

        var brake = CollapseBrake();
        var anyAlive = false;
        for (int i = 0; i < collapseServos.Count; i++)
        {
            var s = collapseServos[i];
            var hold = CollapseGain(s.TimeShift); // position-hold strength, 1 → 0
            if (hold > 0.001f || brake > 0.001f) anyAlive = true;

            // As the muscle releases (hold → 0) blend the captured pose target toward the *live*
            // relative rotation, soften the position spring, and raise damping. With the target
            // tracking the current pose there's no restoring snap-back — only velocity resistance
            // — so the joint can't hold against gravity but brakes how fast it gives way.
            var target = s.Target;
            if (hold < 0.999f)
            {
                var current = ReadRelativeRotation(s.ChildBody, s.ParentBody);
                target = Quaternion.Normalize(Quaternion.Slerp(current, s.Target, hold));
            }
            var freq = s.Freq * (CollapseBrakeFreqScale + (1f - CollapseBrakeFreqScale) * hold);
            var damping = CollapseBrakeDamping + (CollapseDamping - CollapseBrakeDamping) * hold;
            var force = s.MaxForce * MathF.Max(hold, collapseBrakeForceFrac * brake);

            try
            {
                simulation.Solver.ApplyDescription(s.Handle,
                    new AngularServo
                    {
                        TargetRelativeRotationLocalA = target,
                        SpringSettings = new SpringSettings(freq, damping),
                        ServoSettings = new ServoSettings(CollapseMaxSpeed, 0f, force),
                    });
            }
            catch { }
        }

        // StiffHold never retires; the others release to a pure passive ragdoll (constraints
        // removed, bodies free to sleep) once both the hold and the brake are spent.
        if (!anyAlive && collapseArchetype != CollapseArchetype.StiffHold)
        {
            log.Info("RagdollController: collapse spike fully released — handing to passive ragdoll");
            StopCollapseSpike();
        }
    }

    private Quaternion ReadRelativeRotation(BodyHandle child, BodyHandle parent)
    {
        if (simulation == null) return Quaternion.Identity;
        var c = simulation.Bodies.GetBodyReference(child).Pose.Orientation;
        var p = simulation.Bodies.GetBodyReference(parent).Pose.Orientation;
        return Quaternion.Normalize(Quaternion.Inverse(c) * p);
    }

    // --- Directed collapse spike (experimental proof-of-concept) ---
    // Goal: validate "foot plant + staged body drive" for a forward kneel/pitch death.
    // This is intentionally separate from the validated relaxation-family Death Collapse.
    private ConstraintHandle? directedLeftFootConstraint;
    private ConstraintHandle? directedRightFootConstraint;
    private ConstraintHandle? directedPelvisLinearConstraint;
    private ConstraintHandle? directedPelvisAngularConstraint;
    private ConstraintHandle? directedChestAngularConstraint;
    private bool directedCollapseActive;
    private bool pendingDirectedCollapseSpike;
    private CollapseProfile? directedProfile;
    private int directedPhaseIndex;
    private float directedPhaseElapsed;
    private string pendingDirectedProfileId = string.Empty;
    private float directedElapsed;
    private float directedDuration;
    private float directedFootForce;
    private float directedPelvisForce;
    private float directedDrop;
    private float directedForwardDistance;
    private float directedPitchRadians;
    private float pendingDirectedDuration;
    private float pendingDirectedFootForce;
    private float pendingDirectedPelvisForce;
    private float pendingDirectedDrop;
    private float pendingDirectedForward;
    private float pendingDirectedPitchDegrees;
    private readonly CollapseProfileBook collapseProfileBook;
    private Vector3 directedForward;
    private Vector3 directedRight;
    private Vector3 directedLeftFootTarget;
    private Vector3 directedRightFootTarget;
    private Vector3 directedPelvisStart;
    private Quaternion directedPelvisStartRot;
    private Quaternion directedChestStartRot;

    public bool DirectedCollapseSpikeActive => directedCollapseActive;

    public void RequestDirectedKneelPitchOnReady(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        if (IsSimulationReady)
        {
            BeginDirectedKneelPitchSpike(duration, footForce, pelvisForce, drop, forward, pitchDegrees);
            return;
        }

        pendingDirectedDuration = duration;
        pendingDirectedFootForce = footForce;
        pendingDirectedPelvisForce = pelvisForce;
        pendingDirectedDrop = drop;
        pendingDirectedForward = forward;
        pendingDirectedPitchDegrees = pitchDegrees;
        pendingDirectedProfileId = string.Empty;
        pendingDirectedCollapseSpike = true;
        log.Info("RagdollController: directed kneel-pitch armed for next death");
    }

    public void RequestProfileDirectedCollapseOnReady(string profileId)
    {
        if (IsSimulationReady)
        {
            BeginProfileDirectedCollapseSpike(profileId);
            return;
        }

        pendingDirectedProfileId = profileId;
        pendingDirectedCollapseSpike = true;
        log.Info($"RagdollController: directed collapse profile armed for next death - profile={profileId}");
    }

    public bool BeginProfileDirectedCollapseSpike(string profileId)
    {
        var profile = collapseProfileBook.FindById(profileId);
        if (profile == null)
        {
            log.Warning($"RagdollController: directed collapse profile not found - profile={profileId}");
            return false;
        }

        return BeginProfileDirectedCollapseSpike(profile);
    }

    private bool BeginProfileDirectedCollapseSpike(CollapseProfile profile)
    {
        if (simulation == null || !isActive || profile.Phases.Count == 0) return false;

        StopCollapseSpike();
        StopDirectedCollapseSpike();

        directedProfile = profile;
        directedPhaseIndex = 0;
        directedPhaseElapsed = 0f;
        directedElapsed = 0f;
        directedDuration = 0f;
        foreach (var phase in profile.Phases)
            directedDuration += MathF.Max(0.01f, phase.Duration);

        directedForward = ResolveCollapseDirection(CollapseDirection.Forward);
        directedRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        var pelvis = FindBodyHandle("j_kosi");
        if (pelvis.HasValue)
        {
            var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
            directedPelvisStart = pelvisBody.Pose.Position;
            directedPelvisStartRot = pelvisBody.Pose.Orientation;
        }

        var chest = FindBodyHandle("j_sebo_c") ?? FindBodyHandle("j_sebo_b");
        if (chest.HasValue)
            directedChestStartRot = simulation.Bodies.GetBodyReference(chest.Value).Pose.Orientation;

        var leftFoot = FindBodyHandle("j_asi_d_l");
        if (leftFoot.HasValue)
            directedLeftFootTarget = simulation.Bodies.GetBodyReference(leftFoot.Value).Pose.Position;
        var rightFoot = FindBodyHandle("j_asi_d_r");
        if (rightFoot.HasValue)
            directedRightFootTarget = simulation.Bodies.GetBodyReference(rightFoot.Value).Pose.Position;

        WakeRagdollBodiesForBiomechanicalSettle();
        EnsureProfileConstraints(profile);

        directedCollapseActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: directed collapse profile begun - profile={profile.Id} phases={profile.Phases.Count} duration={directedDuration:F2}");
        return true;
    }

    public bool BeginDirectedKneelPitchSpike(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        if (simulation == null || !isActive) return false;

        var leftFoot = FindBodyHandle("j_asi_d_l");
        var rightFoot = FindBodyHandle("j_asi_d_r");
        var pelvis = FindBodyHandle("j_kosi");
        var chest = FindBodyHandle("j_sebo_c") ?? FindBodyHandle("j_sebo_b");
        if (!leftFoot.HasValue || !rightFoot.HasValue || !pelvis.HasValue || !chest.HasValue)
        {
            log.Warning("RagdollController: directed kneel-pitch failed - missing foot/pelvis/chest body");
            return false;
        }

        StopCollapseSpike();
        StopDirectedCollapseSpike();

        directedProfile = null;
        directedPhaseIndex = 0;
        directedPhaseElapsed = 0f;
        directedDuration = Math.Clamp(duration, 0.25f, 4f);
        directedFootForce = Math.Clamp(footForce, 50f, 10000f);
        directedPelvisForce = Math.Clamp(pelvisForce, 50f, 10000f);
        directedDrop = Math.Clamp(drop, 0.05f, 1.5f);
        directedForwardDistance = Math.Clamp(forward, 0f, 2f);
        directedPitchRadians = Math.Clamp(pitchDegrees, -90f, 90f) * (MathF.PI / 180f);
        directedElapsed = 0f;

        directedForward = ResolveCollapseDirection(CollapseDirection.Forward);
        directedRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);

        var leftBody = simulation.Bodies.GetBodyReference(leftFoot.Value);
        var rightBody = simulation.Bodies.GetBodyReference(rightFoot.Value);
        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var chestBody = simulation.Bodies.GetBodyReference(chest.Value);

        directedLeftFootTarget = leftBody.Pose.Position;
        directedRightFootTarget = rightBody.Pose.Position;
        directedPelvisStart = pelvisBody.Pose.Position;
        directedPelvisStartRot = pelvisBody.Pose.Orientation;
        directedChestStartRot = chestBody.Pose.Orientation;

        WakeRagdollBodiesForBiomechanicalSettle();

        directedLeftFootConstraint = simulation.Solver.Add(leftFoot.Value, DirectedLinearServo(directedLeftFootTarget, directedFootForce, 90f));
        directedRightFootConstraint = simulation.Solver.Add(rightFoot.Value, DirectedLinearServo(directedRightFootTarget, directedFootForce, 90f));
        directedPelvisLinearConstraint = simulation.Solver.Add(pelvis.Value, DirectedLinearServo(directedPelvisStart, directedPelvisForce, 45f));
        directedPelvisAngularConstraint = simulation.Solver.Add(pelvis.Value, DirectedAngularServo(directedPelvisStartRot, directedPelvisForce * 0.25f, 20f));
        directedChestAngularConstraint = simulation.Solver.Add(chest.Value, DirectedAngularServo(directedChestStartRot, directedPelvisForce * 0.35f, 25f));

        directedCollapseActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: directed kneel-pitch begun - duration={directedDuration:F2} footForce={directedFootForce:F0} pelvisForce={directedPelvisForce:F0} drop={directedDrop:F2} forward={directedForwardDistance:F2} pitch={pitchDegrees:F0}");
        return true;
    }

    public void StopDirectedCollapseSpike()
    {
        if (simulation != null)
        {
            RemoveDirectedConstraint(ref directedLeftFootConstraint);
            RemoveDirectedConstraint(ref directedRightFootConstraint);
            RemoveDirectedConstraint(ref directedPelvisLinearConstraint);
            RemoveDirectedConstraint(ref directedPelvisAngularConstraint);
            RemoveDirectedConstraint(ref directedChestAngularConstraint);
        }
        else
        {
            directedLeftFootConstraint = null;
            directedRightFootConstraint = null;
            directedPelvisLinearConstraint = null;
            directedPelvisAngularConstraint = null;
            directedChestAngularConstraint = null;
        }

        if (directedCollapseActive)
        {
            directedCollapseActive = false;
            directedProfile = null;
            directedPhaseIndex = 0;
            directedPhaseElapsed = 0f;
            BeginBiomechanicalSettle();
            log.Info("RagdollController: directed kneel-pitch stopped");
        }
    }

    private void UpdateDirectedCollapseSpike(float dt)
    {
        if (!directedCollapseActive || simulation == null) return;
        if (directedProfile != null)
        {
            UpdateProfileDirectedCollapseSpike(dt);
            return;
        }

        directedElapsed += dt;

        var t = Math.Clamp(directedElapsed / directedDuration, 0f, 1f);
        var plantGain = 1f - SmoothStep(Math.Clamp((t - 0.70f) / 0.25f, 0f, 1f));
        var driveGain = 1f - SmoothStep(Math.Clamp((t - 0.78f) / 0.20f, 0f, 1f));
        var buckle = SmoothStep(Math.Clamp(t / 0.62f, 0f, 1f));
        var pitch = SmoothStep(Math.Clamp((t - 0.32f) / 0.46f, 0f, 1f));

        var pelvisTarget = directedPelvisStart
            + directedForward * (directedForwardDistance * buckle)
            - Vector3.UnitY * (directedDrop * buckle);

        var pelvisTargetRot = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(directedRight, directedPitchRadians * 0.35f * pitch) * directedPelvisStartRot);
        var chestTargetRot = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(directedRight, directedPitchRadians * pitch) * directedChestStartRot);

        ApplyDirectedLinear(directedLeftFootConstraint, directedLeftFootTarget, directedFootForce * plantGain, 90f);
        ApplyDirectedLinear(directedRightFootConstraint, directedRightFootTarget, directedFootForce * plantGain, 90f);
        ApplyDirectedLinear(directedPelvisLinearConstraint, pelvisTarget, directedPelvisForce * driveGain, 45f);
        ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot, directedPelvisForce * 0.25f * driveGain, 20f);
        ApplyDirectedAngular(directedChestAngularConstraint, chestTargetRot, directedPelvisForce * 0.35f * driveGain, 25f);

        if (directedElapsed >= directedDuration)
            StopDirectedCollapseSpike();
    }

    private void UpdateProfileDirectedCollapseSpike(float dt)
    {
        if (directedProfile == null || simulation == null) return;
        if (directedPhaseIndex >= directedProfile.Phases.Count)
        {
            StopDirectedCollapseSpike();
            return;
        }

        directedElapsed += dt;
        directedPhaseElapsed += dt;

        var phase = directedProfile.Phases[directedPhaseIndex];
        var phaseDuration = MathF.Max(0.01f, phase.Duration);
        var phaseT = Math.Clamp(directedPhaseElapsed / phaseDuration, 0f, 1f);

        foreach (var controller in phase.Controllers)
            ApplyProfileController(controller, phaseT);

        if (directedPhaseElapsed >= phaseDuration)
        {
            directedPhaseIndex++;
            directedPhaseElapsed = 0f;
            if (directedPhaseIndex >= directedProfile.Phases.Count)
                StopDirectedCollapseSpike();
        }
    }

    private void EnsureProfileConstraints(CollapseProfile profile)
    {
        foreach (var phase in profile.Phases)
        foreach (var controller in phase.Controllers)
        {
            switch (controller.Type)
            {
                case "footPlant":
                    EnsureFootPlantConstraints(controller);
                    break;
                case "pelvisDrop":
                    EnsurePelvisLinearConstraint(controller);
                    EnsurePelvisAngularConstraint(controller);
                    break;
                case "torsoPitch":
                    EnsureChestAngularConstraint(controller);
                    EnsurePelvisAngularConstraint(controller);
                    break;
            }
        }
    }

    private void ApplyProfileController(CollapseControllerProfile controller, float phaseT)
    {
        var localT = ControllerLocalT(controller, phaseT);
        var eased = SmoothStep(localT);
        var strength = Lerp(controller.StrengthFrom, controller.StrengthTo, eased);

        switch (controller.Type)
        {
            case "footPlant":
                ApplyDirectedLinear(directedLeftFootConstraint, directedLeftFootTarget,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 90f);
                ApplyDirectedLinear(directedRightFootConstraint, directedRightFootTarget,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 90f);
                break;

            case "pelvisDrop":
            {
                var direction = ResolveProfileDirection(controller.Direction);
                var drop = Lerp(controller.DropFrom, controller.Drop, eased);
                var distance = Lerp(controller.DistanceFrom, controller.Distance, eased);
                var target = directedPelvisStart
                    + direction * distance
                    - Vector3.UnitY * drop;
                ApplyDirectedLinear(directedPelvisLinearConstraint, target,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 45f);

                var pelvisPitch = Lerp(controller.PitchDegreesFrom, controller.PitchDegrees, eased)
                    * controller.PelvisPitchScale * (MathF.PI / 180f);
                var pelvisTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pelvisPitch) * directedPelvisStartRot);
                ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot,
                    controller.Force * 0.25f * strength, 20f);
                break;
            }

            case "torsoPitch":
            {
                var pitchDegrees = Lerp(controller.PitchDegreesFrom, controller.PitchDegrees, eased);
                var pitch = pitchDegrees * (MathF.PI / 180f);
                var chestTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pitch) * directedChestStartRot);
                ApplyDirectedAngular(directedChestAngularConstraint, chestTargetRot,
                    controller.Force * strength, controller.Frequency > 0 ? controller.Frequency : 25f);

                var pelvisPitch = pitchDegrees * controller.PelvisPitchScale * (MathF.PI / 180f);
                var pelvisTargetRot = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(directedRight, pelvisPitch) * directedPelvisStartRot);
                ApplyDirectedAngular(directedPelvisAngularConstraint, pelvisTargetRot,
                    controller.Force * 0.45f * strength, 20f);
                break;
            }

            case "releaseToPassive":
                StopDirectedCollapseSpike();
                break;
        }
    }

    private static OneBodyLinearServo DirectedLinearServo(Vector3 target, float force, float freq)
        => DirectedLinearServo(target, force, freq, Vector3.Zero);

    private static OneBodyLinearServo DirectedLinearServo(Vector3 target, float force, float freq, Vector3 localOffset) => new()
    {
        LocalOffset = localOffset,
        Target = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private static OneBodyAngularServo DirectedAngularServo(Quaternion target, float force, float freq) => new()
    {
        TargetOrientation = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private static AngularServo DirectedRelativeAngularServo(Quaternion target, float force, float freq) => new()
    {
        TargetRelativeRotationLocalA = target,
        ServoSettings = new ServoSettings(12f, 1f, MathF.Max(0f, force)),
        SpringSettings = new SpringSettings(freq, 1f),
    };

    private void EnsureFootPlantConstraints(CollapseControllerProfile controller)
    {
        if (simulation == null) return;

        foreach (var bone in controller.Bones)
        {
            var handle = FindBodyHandle(bone);
            if (!handle.HasValue) continue;

            if (bone.EndsWith("_l", StringComparison.Ordinal))
            {
                if (!directedLeftFootConstraint.HasValue)
                {
                    directedLeftFootTarget = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
                    directedLeftFootConstraint = simulation.Solver.Add(handle.Value,
                        DirectedLinearServo(directedLeftFootTarget, 0f, controller.Frequency > 0 ? controller.Frequency : 90f));
                }
            }
            else if (bone.EndsWith("_r", StringComparison.Ordinal))
            {
                if (!directedRightFootConstraint.HasValue)
                {
                    directedRightFootTarget = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
                    directedRightFootConstraint = simulation.Solver.Add(handle.Value,
                        DirectedLinearServo(directedRightFootTarget, 0f, controller.Frequency > 0 ? controller.Frequency : 90f));
                }
            }
        }
    }

    private void EnsurePelvisLinearConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedPelvisLinearConstraint.HasValue) return;
        var handle = FindBodyHandle(string.IsNullOrWhiteSpace(controller.Bone) ? "j_kosi" : controller.Bone);
        if (!handle.HasValue) return;
        directedPelvisStart = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
        directedPelvisLinearConstraint = simulation.Solver.Add(handle.Value,
            DirectedLinearServo(directedPelvisStart, 0f, controller.Frequency > 0 ? controller.Frequency : 45f));
    }

    private void EnsurePelvisAngularConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedPelvisAngularConstraint.HasValue) return;
        var handle = FindBodyHandle("j_kosi");
        if (!handle.HasValue) return;
        directedPelvisStartRot = simulation.Bodies.GetBodyReference(handle.Value).Pose.Orientation;
        directedPelvisAngularConstraint = simulation.Solver.Add(handle.Value,
            DirectedAngularServo(directedPelvisStartRot, 0f, 20f));
    }

    private void EnsureChestAngularConstraint(CollapseControllerProfile controller)
    {
        if (simulation == null || directedChestAngularConstraint.HasValue) return;
        var handle = FindBodyHandle(string.IsNullOrWhiteSpace(controller.Bone) ? "j_sebo_c" : controller.Bone)
            ?? FindBodyHandle("j_sebo_b");
        if (!handle.HasValue) return;
        directedChestStartRot = simulation.Bodies.GetBodyReference(handle.Value).Pose.Orientation;
        directedChestAngularConstraint = simulation.Solver.Add(handle.Value,
            DirectedAngularServo(directedChestStartRot, 0f, controller.Frequency > 0 ? controller.Frequency : 25f));
    }

    private Vector3 ResolveProfileDirection(string direction)
    {
        return direction.Equals("backward", StringComparison.OrdinalIgnoreCase) ? -directedForward :
               direction.Equals("left", StringComparison.OrdinalIgnoreCase) ? -directedRight :
               direction.Equals("right", StringComparison.OrdinalIgnoreCase) ? directedRight :
               direction.Equals("sideways", StringComparison.OrdinalIgnoreCase) ? (ShakeRng.Next(2) == 0 ? -directedRight : directedRight) :
               directedForward;
    }

    private static float ControllerLocalT(CollapseControllerProfile controller, float phaseT)
    {
        var start = Math.Clamp(controller.StartTime, 0f, 1f);
        var end = Math.Clamp(controller.EndTime, 0f, 1f);
        if (end <= start) end = start + 0.0001f;
        return Math.Clamp((phaseT - start) / (end - start), 0f, 1f);
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private void ApplyDirectedLinear(ConstraintHandle? handle, Vector3 target, float force, float freq)
        => ApplyDirectedLinear(handle, target, force, freq, Vector3.Zero);

    private void ApplyDirectedLinear(ConstraintHandle? handle, Vector3 target, float force, float freq, Vector3 localOffset)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedLinearServo(target, force, freq, localOffset)); } catch { }
    }

    private void ApplyDirectedAngular(ConstraintHandle? handle, Quaternion target, float force, float freq)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedAngularServo(target, force, freq)); } catch { }
    }

    private void ApplyDirectedRelativeAngular(ConstraintHandle? handle, Quaternion target, float force, float freq)
    {
        if (!handle.HasValue || simulation == null) return;
        try { simulation.Solver.ApplyDescription(handle.Value, DirectedRelativeAngularServo(target, force, freq)); } catch { }
    }

    private void RemoveDirectedConstraint(ref ConstraintHandle? handle)
    {
        if (!handle.HasValue || simulation == null) { handle = null; return; }
        try { simulation.Solver.Remove(handle.Value); } catch { }
        handle = null;
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // --- Whole-body balance-loss collapse (experimental, paper-aligned spike) ---
    // This controller follows the common physics-character pattern from SIMBICON/falling
    // controllers: reason about the whole-body COM relative to the support base, then bias
    // core velocities so the body loses balance. Knees are not driven to a pose; they respond
    // passively through the existing anatomical constraints, contacts, gravity, and friction.
    private bool pendingWholeBodyCollapse;
    private CollapseDirection pendingWholeBodyDirection = CollapseDirection.Forward;
    private bool pendingWholeBodyFuse;
    private bool wholeBodyCollapseActive;
    private float wholeBodyElapsed;
    private Vector3 wholeBodyForward;
    private Vector3 wholeBodyRight;
    private Vector3 wholeBodyInitialCom;
    private Vector3 wholeBodyInitialSupport;

    public bool WholeBodyCollapseActive => wholeBodyCollapseActive;

    public void RequestWholeBodyCollapseOnReady()
        => RequestWholeBodyCollapseOnReady(CollapseDirection.Forward, fuseWithSpike: false);

    // direction = which way to topple; fuseWithSpike = run alongside the muscle-failure collapse
    // spike (don't tear it down) so the body topples while the joints brake — the Relaxation path.
    public void RequestWholeBodyCollapseOnReady(CollapseDirection direction, bool fuseWithSpike)
    {
        if (IsSimulationReady)
        {
            BeginWholeBodyCollapse(direction, fuseWithSpike);
            return;
        }

        pendingWholeBodyCollapse = true;
        pendingWholeBodyDirection = direction;
        pendingWholeBodyFuse = fuseWithSpike;
        log.Info($"RagdollController: whole-body collapse armed for next death (dir={direction} fuse={fuseWithSpike})");
    }

    public bool BeginWholeBodyCollapse() => BeginWholeBodyCollapse(CollapseDirection.Forward, false);

    public bool BeginWholeBodyCollapse(CollapseDirection direction, bool fuseWithSpike)
    {
        if (simulation == null || !isActive) return false;

        if (!TryComputeWholeBodyState(out wholeBodyInitialCom, out wholeBodyInitialSupport, out var coreVelocity))
        {
            log.Warning("RagdollController: whole-body collapse failed - missing COM/support bodies");
            return false;
        }

        // When fused with the Relaxation spike, keep that spike alive (it brakes the joints while
        // we topple) and reuse the lead side it already picked; standalone use clears the other
        // controllers and rolls its own lead side.
        if (!fuseWithSpike)
        {
            StopCollapseSpike();
            StopDirectedCollapseSpike();
            StopKneePowerLossPattern();
            collapseLeadSign = ShakeRng.Next(2) == 0 ? -1f : 1f;
        }
        StopWholeBodyCollapse();

        var resolved = ResolveCollapseDirection(direction == CollapseDirection.None ? CollapseDirection.Forward : direction);
        if (resolved.LengthSquared() < 1e-4f)
            resolved = ResolveCollapseDirection(CollapseDirection.Forward);

        // Momentum steering: bias the topple toward the body's actual horizontal motion at handoff
        // (carried from the death animation), so a moving corpse falls the way it was going.
        var momentumBias = Math.Clamp(config.RagdollToppleMomentumBias, 0f, 1f);
        if (momentumBias > 0f)
        {
            var horiz = new Vector3(coreVelocity.X, 0f, coreVelocity.Z);
            if (horiz.Length() > 0.5f) // ignore near-static handoffs
            {
                var momentumDir = Vector3.Normalize(horiz);
                var blended = Vector3.Lerp(resolved, momentumDir, momentumBias);
                if (blended.LengthSquared() > 1e-4f)
                    resolved = Vector3.Normalize(blended);
            }
        }
        wholeBodyForward = resolved;
        // Lateral-balance axis ⟂ to the topple direction, so the forward/pitch drive tips the body
        // toward wholeBodyForward for ANY chosen direction (not just character-forward).
        var rightRaw = Vector3.Cross(Vector3.UnitY, wholeBodyForward);
        wholeBodyRight = rightRaw.LengthSquared() > 1e-4f
            ? Vector3.Normalize(rightRaw)
            : FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        wholeBodyElapsed = 0f;
        wholeBodyCollapseActive = true;

        WakeRagdollBodiesForBiomechanicalSettle();
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: whole-body collapse begun - dir={direction} fuse={fuseWithSpike} com=({wholeBodyInitialCom.X:F2},{wholeBodyInitialCom.Y:F2},{wholeBodyInitialCom.Z:F2}) support=({wholeBodyInitialSupport.X:F2},{wholeBodyInitialSupport.Y:F2},{wholeBodyInitialSupport.Z:F2})");
        return true;
    }

    public void StopWholeBodyCollapse()
    {
        if (!wholeBodyCollapseActive) return;
        wholeBodyCollapseActive = false;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: whole-body collapse stopped");
    }

    private void UpdateWholeBodyCollapse(float dt)
    {
        if (!wholeBodyCollapseActive || simulation == null) return;

        wholeBodyElapsed += dt;
        if (!TryComputeWholeBodyState(out var com, out var support, out var comVelocity))
        {
            StopWholeBodyCollapse();
            return;
        }

        var t = SmoothStep(Math.Clamp(wholeBodyElapsed / 1.20f, 0f, 1f));
        var release = SmoothStep(Math.Clamp((wholeBodyElapsed - 1.10f) / 0.35f, 0f, 1f));
        var gain = 1f - release;
        var stepScale = Math.Clamp(dt * 60f, 0.35f, 2.0f);

        var forwardOffset = Vector3.Dot(com - support, wholeBodyForward);
        var lateralOffset = Vector3.Dot(com - support, wholeBodyRight);
        var lateralVelocity = Vector3.Dot(comVelocity, wholeBodyRight);

        // Move the COM projection toward and then beyond the front of the foot support. This
        // creates a balance failure; it is not a knee pose target.
        var targetForwardOffset = 0.12f + 0.34f * t;
        var forwardError = targetForwardOffset - forwardOffset;
        var forwardSpeed = Math.Clamp(0.35f + forwardError * 2.1f, 0f, 1.25f) * gain;
        var downSpeed = (0.20f + 0.42f * t) * gain;
        var pitchSpeed = (0.55f + 1.15f * t) * gain;

        // Asymmetry: lean (and below, twist) toward the lead side so the fall is lopsided and
        // rotating instead of a flat, mirror-image topple. The lean is folded into the lateral
        // drive (which otherwise only corrects drift back to centre).
        // leadSign +1 = left side leads (left leg buckles first); the body then falls toward its
        // LEFT, i.e. -wholeBodyRight, so the lean bias is the negated lead sign.
        var asym = Math.Clamp(config.RagdollCollapseAsymmetry, 0f, 1f);
        var leanBias = -collapseLeadSign * asym * 0.45f;
        var lateralSpeed = (Math.Clamp(-lateralOffset * 2.8f - lateralVelocity * 0.65f, -0.55f, 0.55f) + leanBias) * gain;

        ApplyCoreAxisVelocity("j_kosi", wholeBodyForward, forwardSpeed, 0.105f * stepScale);
        ApplyCoreAxisVelocity("j_kosi", -Vector3.UnitY, downSpeed, 0.090f * stepScale);
        ApplyCoreAxisVelocity("j_kosi", wholeBodyRight, lateralSpeed, 0.080f * stepScale);

        ApplyCoreAxisVelocity("j_sebo_b", wholeBodyForward, forwardSpeed * 0.82f, 0.085f * stepScale);
        ApplyCoreAxisVelocity("j_sebo_c", wholeBodyForward, forwardSpeed * 0.95f, 0.085f * stepScale);
        ApplyCoreAxisVelocity("j_sebo_c", wholeBodyRight, lateralSpeed * 0.85f, 0.065f * stepScale);
        ApplyCoreAxisVelocity("j_kao", wholeBodyRight, lateralSpeed * 0.65f, 0.045f * stepScale);

        ApplyCoreAngularVelocity("j_sebo_c", wholeBodyRight, pitchSpeed, 0.105f * stepScale);
        ApplyCoreAngularVelocity("j_sebo_b", wholeBodyRight, pitchSpeed * 0.65f, 0.075f * stepScale);
        ApplyCoreAngularVelocity("j_kosi", wholeBodyRight, pitchSpeed * 0.30f, 0.050f * stepScale);

        // Asymmetry twist: a yaw about vertical so the body rotates as it falls (about a different
        // axis than the pitch, so this does not fight the forward topple).
        if (asym > 0f)
        {
            var yawSpeed = collapseLeadSign * asym * 0.9f * gain;
            ApplyCoreAngularVelocity("j_kosi", Vector3.UnitY, yawSpeed, 0.06f * stepScale);
            ApplyCoreAngularVelocity("j_sebo_c", Vector3.UnitY, yawSpeed * 0.7f, 0.05f * stepScale);
        }

        if (wholeBodyElapsed >= 1.55f)
            StopWholeBodyCollapse();
    }

    private bool TryComputeWholeBodyState(out Vector3 com, out Vector3 support, out Vector3 comVelocity)
    {
        com = Vector3.Zero;
        support = Vector3.Zero;
        comVelocity = Vector3.Zero;
        if (simulation == null || ragdollBones.Count == 0) return false;

        var totalWeight = 0f;
        foreach (var rb in ragdollBones)
        {
            activeDefByName.TryGetValue(rb.Name, out var def);
            var weight = WholeBodyComWeight(def.AnatomicalRole);
            if (weight <= 0f) continue;

            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            com += body.Pose.Position * weight;
            comVelocity += body.Velocity.Linear * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0.001f) return false;
        com /= totalWeight;
        comVelocity /= totalWeight;

        if (!TryGetSupportPoint(true, out var left) || !TryGetSupportPoint(false, out var right))
            return false;
        support = (left + right) * 0.5f;
        support.Y = groundY;
        return true;
    }

    private static float WholeBodyComWeight(AnatomicalRole role) => role switch
    {
        AnatomicalRole.Pelvis => 4.0f,
        AnatomicalRole.Spine => 3.2f,
        AnatomicalRole.Head => 1.4f,
        AnatomicalRole.Hip => 2.0f,
        AnatomicalRole.Knee => 1.4f,
        AnatomicalRole.Ankle or AnatomicalRole.Foot => 0.7f,
        AnatomicalRole.Shoulder => 1.0f,
        AnatomicalRole.Elbow or AnatomicalRole.Hand => 0.55f,
        AnatomicalRole.Cloth or AnatomicalRole.SoftBody or AnatomicalRole.Weapon => 0f,
        _ => 1.0f,
    };

    private bool TryGetSupportPoint(bool left, out Vector3 point)
    {
        point = Vector3.Zero;
        if (simulation == null) return false;

        var suffix = left ? "_l" : "_r";
        var names = new[]
        {
            "j_asi_e" + suffix,
            "j_asi_d" + suffix,
            "j_asi_c" + suffix,
        };

        var found = false;
        var bestY = float.PositiveInfinity;
        foreach (var name in names)
        {
            var handle = FindBodyHandle(name);
            if (!handle.HasValue) continue;
            var pos = simulation.Bodies.GetBodyReference(handle.Value).Pose.Position;
            if (!found || pos.Y < bestY)
            {
                found = true;
                bestY = pos.Y;
                point = pos;
            }
        }
        return found;
    }

    private void ApplyCoreAxisVelocity(string boneName, Vector3 axis, float targetSpeed, float maxDelta)
    {
        if (simulation == null || axis.LengthSquared() < 0.0001f) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        var n = Vector3.Normalize(axis);
        var current = Vector3.Dot(body.Velocity.Linear, n);
        var delta = Math.Clamp(targetSpeed - current, -maxDelta, maxDelta);
        body.Velocity.Linear += n * delta;
        body.Awake = true;
    }

    private void ApplyCoreAngularVelocity(string boneName, Vector3 axis, float targetSpeed, float maxDelta)
    {
        if (simulation == null || axis.LengthSquared() < 0.0001f) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        var n = Vector3.Normalize(axis);
        var current = Vector3.Dot(body.Velocity.Angular, n);
        var delta = Math.Clamp(targetSpeed - current, -maxDelta, maxDelta);
        body.Velocity.Angular += n * delta;
        body.Awake = true;
    }

    // --- Collapse entry conditioning ---
    // Common pre-pass for narrow upright death poses. When the stance is too closed or the knees
    // are locked nearly straight, downstream collapse profiles inherit an unstable support base
    // and tend to fold the knees inward. This pre-pass briefly opens the support geometry and
    // starts a small pelvis drop before handing off to the requested collapse pattern.
    private bool pendingEntryConditionedKneePowerLoss;
    private bool entryConditioningActive;
    private float entryConditioningElapsed;
    private Func<bool>? entryConditioningContinuation;
    private Vector3 entryForward;
    private Vector3 entryRight;
    private Vector3 entryLeftKneeStart;
    private Vector3 entryRightKneeStart;
    private float entryStanceWidth;
    private float entryLeftKneeAngle;
    private float entryRightKneeAngle;
    private bool entryWasNeeded;

    public void RequestEntryConditionedKneePowerLossOnReady()
    {
        var knee = EffectiveKneePowerLossSettings();
        if (!knee.EntryConditioningEnabled)
        {
            RequestKneePowerLossForwardOnReady();
            return;
        }

        if (IsSimulationReady)
        {
            if (!BeginCollapseEntryConditioning(BeginKneePowerLossForwardPattern))
                BeginKneePowerLossForwardPattern();
            return;
        }

        pendingEntryConditionedKneePowerLoss = true;
        log.Info("RagdollController: entry-conditioned knee power-loss armed for next death");
    }

    private bool BeginCollapseEntryConditioning(Func<bool> continuation)
    {
        if (simulation == null || !isActive) return false;

        if (!TryMeasureEntryPose(out entryStanceWidth, out entryLeftKneeAngle, out entryRightKneeAngle,
                out entryLeftKneeStart, out entryRightKneeStart))
        {
            log.Warning("RagdollController: entry conditioning skipped - missing leg pose data");
            return false;
        }

        entryForward = ResolveCollapseDirection(CollapseDirection.Forward);
        entryRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        var knee = EffectiveKneePowerLossSettings();
        var straightestKnee = MathF.Min(entryLeftKneeAngle, entryRightKneeAngle);
        entryWasNeeded = entryStanceWidth < Math.Clamp(knee.EntryStanceThreshold, 0.05f, 1.0f) ||
                         straightestKnee < Math.Clamp(knee.EntryReadyKneeAngle, 1f, 60f);
        if (!entryWasNeeded)
        {
            log.Info($"RagdollController: entry conditioning skipped - stance={entryStanceWidth:F2} kneeAngles=({entryLeftKneeAngle:F1},{entryRightKneeAngle:F1})");
            return false;
        }

        StopCollapseSpike();
        StopDirectedCollapseSpike();
        StopWholeBodyCollapse();
        StopKneePowerLossPattern();
        StopCollapseEntryConditioning();

        entryConditioningContinuation = continuation;
        entryConditioningElapsed = 0f;
        entryConditioningActive = true;
        WakeRagdollBodiesForBiomechanicalSettle();
        BeginBiomechanicalSettle();
        log.Info($"RagdollController: entry conditioning begun - stance={entryStanceWidth:F2} kneeAngles=({entryLeftKneeAngle:F1},{entryRightKneeAngle:F1})");
        return true;
    }

    private void StopCollapseEntryConditioning()
    {
        if (!entryConditioningActive) return;
        entryConditioningActive = false;
        entryConditioningContinuation = null;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: entry conditioning stopped");
    }

    private void UpdateCollapseEntryConditioning(float dt)
    {
        if (!entryConditioningActive || simulation == null) return;

        entryConditioningElapsed += dt;
        var knee = EffectiveKneePowerLossSettings();
        var minDuration = Math.Clamp(knee.EntryMinDuration, 0.05f, 1.0f);
        var maxDuration = Math.Clamp(MathF.Max(knee.EntryMaxDuration, minDuration), minDuration, 1.5f);
        var t = SmoothStep(Math.Clamp(entryConditioningElapsed / maxDuration, 0f, 1f));
        var stepScale = Math.Clamp(dt * 60f, 0.35f, 2.0f);

        var targetStance = Math.Clamp(knee.EntryTargetStanceStart, 0.05f, 1.2f)
            + (Math.Clamp(knee.EntryTargetStanceEnd, 0.05f, 1.2f) - Math.Clamp(knee.EntryTargetStanceStart, 0.05f, 1.2f)) * t;
        var pelvisDown = Math.Clamp(knee.EntryPelvisDownStart, 0f, 2f)
            + (Math.Clamp(knee.EntryPelvisDownEnd, 0f, 2f) - Math.Clamp(knee.EntryPelvisDownStart, 0f, 2f)) * t;

        ApplyEntryKneeSeparation(targetStance, 0.10f * stepScale);
        ApplyCoreAxisVelocity("j_kosi", -Vector3.UnitY, pelvisDown, 0.075f * stepScale);
        ApplyCoreAxisVelocity("j_kosi", entryForward, 0.20f + 0.18f * t, 0.055f * stepScale);
        ApplyCoreAxisVelocity("j_sebo_b", entryForward, 0.10f + 0.10f * t, 0.040f * stepScale);

        var ready = TryMeasureEntryPose(out var stance, out var leftAngle, out var rightAngle, out _, out _) &&
                    stance >= Math.Clamp(knee.EntryReadyStance, 0.05f, 1.2f) &&
                    MathF.Min(leftAngle, rightAngle) >= Math.Clamp(knee.EntryReadyKneeAngle, 1f, 60f);
        if (entryConditioningElapsed < minDuration || (!ready && entryConditioningElapsed < maxDuration))
            return;

        var continuation = entryConditioningContinuation;
        entryConditioningActive = false;
        entryConditioningContinuation = null;
        log.Info($"RagdollController: entry conditioning complete - stance={stance:F2} kneeAngles=({leftAngle:F1},{rightAngle:F1}) ready={ready}");
        _ = continuation?.Invoke();
    }

    private bool TryMeasureEntryPose(out float stanceWidth, out float leftKneeAngle, out float rightKneeAngle,
        out Vector3 leftKnee, out Vector3 rightKnee)
    {
        stanceWidth = 0f;
        leftKneeAngle = 0f;
        rightKneeAngle = 0f;
        leftKnee = Vector3.Zero;
        rightKnee = Vector3.Zero;

        if (!TryGetSupportPoint(true, out var leftSupport) || !TryGetSupportPoint(false, out var rightSupport))
            return false;
        var rightAxis = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        stanceWidth = MathF.Abs(Vector3.Dot(rightSupport - leftSupport, rightAxis));

        leftKneeAngle = MeasureLegBendAngle("j_asi_a_l", "j_asi_b_l", "j_asi_c_l", out leftKnee);
        rightKneeAngle = MeasureLegBendAngle("j_asi_a_r", "j_asi_b_r", "j_asi_c_r", out rightKnee);
        return leftKneeAngle >= 0f && rightKneeAngle >= 0f;
    }

    private float MeasureLegBendAngle(string hipBone, string kneeBone, string ankleBone, out Vector3 knee)
    {
        knee = Vector3.Zero;
        if (!TryGetBoneOriginPosition(hipBone, out var hip) ||
            !TryGetBoneOriginPosition(kneeBone, out knee) ||
            !TryGetBoneOriginPosition(ankleBone, out var ankle))
            return -1f;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        return MathF.Acos(Math.Clamp(Vector3.Dot(thigh, shin), -1f, 1f)) * 180f / MathF.PI;
    }

    private void ApplyEntryKneeSeparation(float targetWidth, float maxDelta)
    {
        if (simulation == null) return;
        var leftHandle = FindBodyHandle("j_asi_b_l");
        var rightHandle = FindBodyHandle("j_asi_b_r");
        if (!leftHandle.HasValue || !rightHandle.HasValue) return;

        var lateral = FlatNormalize(entryRight, Vector3.UnitX);
        var left = simulation.Bodies.GetBodyReference(leftHandle.Value);
        var right = simulation.Bodies.GetBodyReference(rightHandle.Value);
        var currentWidth = MathF.Abs(Vector3.Dot(right.Pose.Position - left.Pose.Position, lateral));
        var error = MathF.Max(0f, targetWidth - currentWidth);
        if (error <= 0.001f) return;

        var leftSign = Vector3.Dot(left.Pose.Position - right.Pose.Position, lateral) >= 0f ? 1f : -1f;
        var leftDir = lateral * leftSign;
        var rightDir = -leftDir;
        var speed = Math.Clamp(error * 3.2f, 0f, 0.55f);
        ApplyBodyVelocityAlong(left, leftDir, speed, maxDelta);
        ApplyBodyVelocityAlong(right, rightDir, speed, maxDelta);
    }

    private static void ApplyBodyVelocityAlong(BodyReference body, Vector3 axis, float targetSpeed, float maxDelta)
    {
        if (axis.LengthSquared() < 0.0001f) return;
        var n = Vector3.Normalize(axis);
        var current = Vector3.Dot(body.Velocity.Linear, n);
        var delta = Math.Clamp(targetSpeed - current, -maxDelta, maxDelta);
        body.Velocity.Linear += n * delta;
        body.Awake = true;
    }

    private GuidedCollapseKneePowerLossSettings EffectiveKneePowerLossSettings()
    {
        return config.GuidedCollapse.KneePowerLoss;
    }

    // --- Knee power-loss forward pattern (C# pattern spike) ---
    // Biomechanics-inspired failure controller: feet provide soft support, knee flexion bias
    // nudges legs into the sagittal plane, pelvis/chest only receive weak feedback. This should
    // feel less like the body is being pulled into a pose and more like leg extensor tone failed.
    private enum KneePowerLossPhase { Buckle, TorsoLoss, Release }

    private bool pendingKneePowerLossPattern;
    private bool kneePowerLossActive;
    private KneePowerLossPhase kneePowerLossPhase;
    private float kneePowerLossElapsed;
    private float kneePowerLossPhaseElapsed;
    private ConstraintHandle? kneeLeftFootSupport;
    private ConstraintHandle? kneeRightFootSupport;
    private ConstraintHandle? kneeLeftFlexBias;
    private ConstraintHandle? kneeRightFlexBias;
    private ConstraintHandle? kneePelvisSupport;
    private ConstraintHandle? kneeChestPitch;
    private Vector3 kneeForward;
    private Vector3 kneeRight;
    private Vector3 kneeLeftFootStart;
    private Vector3 kneeRightFootStart;
    private Vector3 kneeLeftFootLocalOffset;
    private Vector3 kneeRightFootLocalOffset;
    private Vector3 kneeLeftStart;
    private Vector3 kneeRightStart;
    private Vector3 kneePelvisStart;
    private Quaternion kneeLeftKneeStartTarget;
    private Quaternion kneeRightKneeStartTarget;
    private Quaternion kneeLeftKneeFlexTarget;
    private Quaternion kneeRightKneeFlexTarget;
    private Quaternion kneeChestStartRot;

    public bool KneePowerLossPatternActive => kneePowerLossActive;

    public void RequestKneePowerLossForwardOnReady()
    {
        if (IsSimulationReady)
        {
            BeginKneePowerLossForwardPattern();
            return;
        }

        pendingKneePowerLossPattern = true;
        log.Info("RagdollController: knee power-loss forward armed for next death");
    }

    private bool TryBuildKneeFootSupportProxy(BodyHandle footHandle, bool left, out Vector3 target, out Vector3 localOffset)
    {
        target = Vector3.Zero;
        localOffset = Vector3.Zero;
        if (simulation == null) return false;

        var settings = EffectiveKneePowerLossSettings();
        if (!settings.FootProxyEnabled) return false;

        var body = simulation.Bodies.GetBodyReference(footHandle);
        var forward = FlatNormalize(kneeForward, Vector3.UnitZ);
        var forwardOffset = Math.Clamp(settings.FootProxyForwardOffset, -0.05f, 0.25f);
        var downOffset = Math.Clamp(settings.FootProxyDownOffset, 0f, 0.16f);
        var clearance = Math.Clamp(settings.FootProxyGroundClearance, 0.004f, 0.08f);

        // FFXIV's foot/toe bones are not reliable sole vectors; j_asi_d often behaves like an
        // ankle/downward marker. Anchor a virtual sole point in the intended sagittal direction
        // instead, and apply the foot support servo at that local point on the foot body.
        target = body.Pose.Position + forward * forwardOffset - Vector3.UnitY * downOffset;
        target.Y = groundY + clearance;
        localOffset = Vector3.Transform(target - body.Pose.Position, Quaternion.Inverse(body.Pose.Orientation));

        log.Info(
            $"RagdollController: knee foot proxy {(left ? "L" : "R")} " +
            $"target=({target.X:F2},{target.Y:F2},{target.Z:F2}) " +
            $"local=({localOffset.X:F2},{localOffset.Y:F2},{localOffset.Z:F2})");
        return true;
    }

    public bool BeginKneePowerLossForwardPattern()
    {
        if (simulation == null || !isActive) return false;

        var leftFoot = FindBodyHandle("j_asi_d_l");
        var rightFoot = FindBodyHandle("j_asi_d_r");
        var pelvis = FindBodyHandle("j_kosi");
        var chest = FindBodyHandle("j_sebo_c") ?? FindBodyHandle("j_sebo_b");
        var leftKnee = FindBodyHandle("j_asi_b_l");
        var rightKnee = FindBodyHandle("j_asi_b_r");
        var leftThigh = FindBodyHandle("j_asi_a_l");
        var rightThigh = FindBodyHandle("j_asi_a_r");

        if (!leftFoot.HasValue || !rightFoot.HasValue || !pelvis.HasValue || !chest.HasValue ||
            !leftKnee.HasValue || !rightKnee.HasValue || !leftThigh.HasValue || !rightThigh.HasValue)
        {
            log.Warning("RagdollController: knee power-loss failed - missing leg/core body");
            return false;
        }

        StopCollapseSpike();
        StopDirectedCollapseSpike();
        StopKneePowerLossPattern();

        kneeForward = ResolveCollapseDirection(CollapseDirection.Forward);
        kneeRight = FlatNormalize(Vector3.Transform(Vector3.UnitX, skelWorldRot), Vector3.UnitX);
        kneePowerLossElapsed = 0f;
        kneePowerLossPhaseElapsed = 0f;
        kneePowerLossPhase = KneePowerLossPhase.Buckle;

        var leftFootBody = simulation.Bodies.GetBodyReference(leftFoot.Value);
        var rightFootBody = simulation.Bodies.GetBodyReference(rightFoot.Value);
        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var chestBody = simulation.Bodies.GetBodyReference(chest.Value);

        kneeLeftFootStart = leftFootBody.Pose.Position;
        kneeRightFootStart = rightFootBody.Pose.Position;
        kneeLeftFootLocalOffset = Vector3.Zero;
        kneeRightFootLocalOffset = Vector3.Zero;
        kneePelvisStart = pelvisBody.Pose.Position;
        kneeChestStartRot = chestBody.Pose.Orientation;

        if (TryBuildKneeFootSupportProxy(leftFoot.Value, true, out var leftProxyTarget, out var leftProxyLocal))
        {
            kneeLeftFootStart = leftProxyTarget;
            kneeLeftFootLocalOffset = leftProxyLocal;
        }
        if (TryBuildKneeFootSupportProxy(rightFoot.Value, false, out var rightProxyTarget, out var rightProxyLocal))
        {
            kneeRightFootStart = rightProxyTarget;
            kneeRightFootLocalOffset = rightProxyLocal;
        }

        var leftKneeBody = simulation.Bodies.GetBodyReference(leftKnee.Value);
        var rightKneeBody = simulation.Bodies.GetBodyReference(rightKnee.Value);
        var leftThighBody = simulation.Bodies.GetBodyReference(leftThigh.Value);
        var rightThighBody = simulation.Bodies.GetBodyReference(rightThigh.Value);
        kneeLeftStart = leftKneeBody.Pose.Position;
        kneeRightStart = rightKneeBody.Pose.Position;

        kneeLeftKneeStartTarget = Quaternion.Normalize(Quaternion.Inverse(leftKneeBody.Pose.Orientation) * leftThighBody.Pose.Orientation);
        kneeRightKneeStartTarget = Quaternion.Normalize(Quaternion.Inverse(rightKneeBody.Pose.Orientation) * rightThighBody.Pose.Orientation);
        TryBuildLegAnatomyFrame("j_asi_a_l", "j_asi_b_l", "j_asi_c_l", "j_asi_d_l",
            leftKneeBody.Pose.Orientation, out var leftHingeAxis, out var leftHingeForward);
        TryBuildLegAnatomyFrame("j_asi_a_r", "j_asi_b_r", "j_asi_c_r", "j_asi_d_r",
            rightKneeBody.Pose.Orientation, out var rightHingeAxis, out var rightHingeForward);

        var kneeSettings = EffectiveKneePowerLossSettings();
        var kneeFlexDegrees = Math.Clamp(kneeSettings.KneeFlexDegrees, 0f, 90f);
        kneeLeftKneeFlexTarget = MakeRelativeFlexTarget(leftKneeBody.Pose.Orientation, leftThighBody.Pose.Orientation,
            leftHingeAxis, leftHingeForward, kneeFlexDegrees);
        kneeRightKneeFlexTarget = MakeRelativeFlexTarget(rightKneeBody.Pose.Orientation, rightThighBody.Pose.Orientation,
            rightHingeAxis, rightHingeForward, kneeFlexDegrees);

        log.Info($"RagdollController: knee power-loss axes L axis=({leftHingeAxis.X:F2},{leftHingeAxis.Y:F2},{leftHingeAxis.Z:F2}) fwd=({leftHingeForward.X:F2},{leftHingeForward.Y:F2},{leftHingeForward.Z:F2}); " +
                 $"R axis=({rightHingeAxis.X:F2},{rightHingeAxis.Y:F2},{rightHingeAxis.Z:F2}) fwd=({rightHingeForward.X:F2},{rightHingeForward.Y:F2},{rightHingeForward.Z:F2})");
        LogLegAnatomyDiagnostics("L", "j_asi_a_l", "j_asi_b_l", "j_asi_c_l", "j_asi_d_l", leftHingeAxis, leftHingeForward);
        LogLegAnatomyDiagnostics("R", "j_asi_a_r", "j_asi_b_r", "j_asi_c_r", "j_asi_d_r", rightHingeAxis, rightHingeForward);

        WakeRagdollBodiesForBiomechanicalSettle();

        kneeLeftFootSupport = simulation.Solver.Add(leftFoot.Value, DirectedLinearServo(kneeLeftFootStart, 0f, 55f, kneeLeftFootLocalOffset));
        kneeRightFootSupport = simulation.Solver.Add(rightFoot.Value, DirectedLinearServo(kneeRightFootStart, 0f, 55f, kneeRightFootLocalOffset));
        kneeLeftFlexBias = simulation.Solver.Add(leftKnee.Value, leftThigh.Value, DirectedRelativeAngularServo(kneeLeftKneeStartTarget, 0f, 18f));
        kneeRightFlexBias = simulation.Solver.Add(rightKnee.Value, rightThigh.Value, DirectedRelativeAngularServo(kneeRightKneeStartTarget, 0f, 18f));
        kneePelvisSupport = simulation.Solver.Add(pelvis.Value, DirectedLinearServo(kneePelvisStart, 0f, 20f));
        kneeChestPitch = simulation.Solver.Add(chest.Value, DirectedAngularServo(kneeChestStartRot, 0f, 18f));

        kneePowerLossActive = true;
        prevAllAsleep = false;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: knee power-loss forward begun");
        return true;
    }

    public void StopKneePowerLossPattern()
    {
        if (simulation != null)
        {
            RemoveDirectedConstraint(ref kneeLeftFootSupport);
            RemoveDirectedConstraint(ref kneeRightFootSupport);
            RemoveDirectedConstraint(ref kneeLeftFlexBias);
            RemoveDirectedConstraint(ref kneeRightFlexBias);
            RemoveDirectedConstraint(ref kneePelvisSupport);
            RemoveDirectedConstraint(ref kneeChestPitch);
        }
        else
        {
            kneeLeftFootSupport = null;
            kneeRightFootSupport = null;
            kneeLeftFlexBias = null;
            kneeRightFlexBias = null;
            kneePelvisSupport = null;
            kneeChestPitch = null;
        }

        if (kneePowerLossActive)
        {
            kneePowerLossActive = false;
            BeginBiomechanicalSettle();
            log.Info("RagdollController: knee power-loss forward stopped");
        }
    }

    private void UpdateKneePowerLossPattern(float dt)
    {
        if (!kneePowerLossActive || simulation == null) return;

        kneePowerLossElapsed += dt;
        kneePowerLossPhaseElapsed += dt;

        var pelvis = FindBodyHandle("j_kosi");
        var chest = FindBodyHandle("j_sebo_c") ?? FindBodyHandle("j_sebo_b");
        if (!pelvis.HasValue || !chest.HasValue) { StopKneePowerLossPattern(); return; }

        var pelvisBody = simulation.Bodies.GetBodyReference(pelvis.Value);
        var pelvisDrop = MathF.Max(0f, kneePelvisStart.Y - pelvisBody.Pose.Position.Y);
        var kneeSettings = EffectiveKneePowerLossSettings();
        var kneeNearGround = BoneHeight("j_asi_b_l") < groundY + 0.18f || BoneHeight("j_asi_b_r") < groundY + 0.18f;
        var minKneeAngle = MeasureCurrentKneePowerLossMinAngle();
        var forwardOffset = MeasureCurrentKneePowerLossForwardOffset();

        if (kneePowerLossPhase == KneePowerLossPhase.Buckle &&
            kneePowerLossPhaseElapsed >= Math.Clamp(kneeSettings.BuckleMinDuration, 0.05f, 1.5f) &&
            (pelvisDrop > Math.Clamp(kneeSettings.BucklePelvisDropToTorso, 0.05f, 1.5f) ||
             minKneeAngle > Math.Clamp(kneeSettings.BuckleKneeAngleToTorso, 1f, 90f) ||
             kneeNearGround ||
             kneePowerLossPhaseElapsed > Math.Clamp(kneeSettings.BuckleTimeout, 0.1f, 3f)))
        {
            log.Info($"RagdollController: knee power-loss buckle complete - drop={pelvisDrop:F2} knee={minKneeAngle:F1} fwd={forwardOffset:F2} nearGround={kneeNearGround}");
            SwitchKneePowerLossPhase(KneePowerLossPhase.TorsoLoss);
        }
        else if (kneePowerLossPhase == KneePowerLossPhase.TorsoLoss &&
                 kneePowerLossPhaseElapsed >= Math.Clamp(kneeSettings.TorsoMinDuration, 0.05f, 2f) &&
                 (forwardOffset > 0.24f || kneePowerLossPhaseElapsed > Math.Clamp(kneeSettings.TorsoTimeout, 0.1f, 3f)))
        {
            log.Info($"RagdollController: knee power-loss torso complete - drop={pelvisDrop:F2} knee={minKneeAngle:F1} fwd={forwardOffset:F2}");
            SwitchKneePowerLossPhase(KneePowerLossPhase.Release);
        }

        switch (kneePowerLossPhase)
        {
            case KneePowerLossPhase.Buckle:
                UpdateKneeBucklePhase(pelvisDrop);
                break;
            case KneePowerLossPhase.TorsoLoss:
                UpdateKneeTorsoLossPhase();
                break;
            case KneePowerLossPhase.Release:
                StopKneePowerLossPattern();
                break;
        }
    }

    private void UpdateKneeBucklePhase(float pelvisDrop)
    {
        var t = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.75f, 0f, 1f));
        var supportGain = 1f - 0.45f * t;
        var flexGain = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.55f, 0f, 1f));
        var kneeSettings = EffectiveKneePowerLossSettings();

        ApplySoftFootSupport(kneeLeftFootSupport, kneeLeftFootStart, kneeLeftFootLocalOffset, supportGain, Math.Clamp(kneeSettings.BuckleFootSupportForce, 0f, 5000f));
        ApplySoftFootSupport(kneeRightFootSupport, kneeRightFootStart, kneeRightFootLocalOffset, supportGain, Math.Clamp(kneeSettings.BuckleFootSupportForce, 0f, 5000f));

        ApplyKneeLateralStability(5.5f, 0.45f);

        var flexForce = Math.Clamp(kneeSettings.KneeBuckleFlexForce, 0f, 500f);
        ApplyDirectedRelativeAngular(kneeLeftFlexBias, Quaternion.Slerp(kneeLeftKneeStartTarget, kneeLeftKneeFlexTarget, flexGain), flexForce * flexGain, 14f);
        ApplyDirectedRelativeAngular(kneeRightFlexBias, Quaternion.Slerp(kneeRightKneeStartTarget, kneeRightKneeFlexTarget, flexGain), flexForce * flexGain, 14f);

        var needMoreDrop = pelvisDrop < 0.22f ? 1f : pelvisDrop < 0.38f ? 0.45f : 0.1f;
        var target = kneePelvisStart
            + kneeForward * (0.10f + 0.18f * t)
            - Vector3.UnitY * (0.28f + 0.18f * t);
        ApplyDirectedLinear(kneePelvisSupport, target, Math.Clamp(kneeSettings.BucklePelvisForce, 0f, 3000f) * needMoreDrop, 14f);
    }

    private void UpdateKneeTorsoLossPhase()
    {
        var t = SmoothStep(Math.Clamp(kneePowerLossPhaseElapsed / 0.65f, 0f, 1f));
        var supportGain = 1f - t;
        var kneeSettings = EffectiveKneePowerLossSettings();

        ApplySoftFootSupport(kneeLeftFootSupport, kneeLeftFootStart, kneeLeftFootLocalOffset, supportGain, Math.Clamp(kneeSettings.TorsoFootSupportForce, 0f, 5000f));
        ApplySoftFootSupport(kneeRightFootSupport, kneeRightFootStart, kneeRightFootLocalOffset, supportGain, Math.Clamp(kneeSettings.TorsoFootSupportForce, 0f, 5000f));
        ApplyKneeLateralStability(3.0f * supportGain, 0.32f);
        var flexForce = Math.Clamp(kneeSettings.KneeTorsoFlexForce, 0f, 500f);
        ApplyDirectedRelativeAngular(kneeLeftFlexBias, kneeLeftKneeFlexTarget, flexForce * supportGain, 10f);
        ApplyDirectedRelativeAngular(kneeRightFlexBias, kneeRightKneeFlexTarget, flexForce * supportGain, 10f);

        var chestPitch = Math.Clamp(kneeSettings.ChestPitchDegrees, -90f, 90f) * MathF.PI / 180f;
        var chestTarget = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(kneeRight, chestPitch * t) * kneeChestStartRot);
        ApplyDirectedAngular(kneeChestPitch, chestTarget, 260f * (1f - 0.45f * t), 18f);

        var pelvisTarget = kneePelvisStart + kneeForward * (0.25f + 0.18f * t) - Vector3.UnitY * 0.46f;
        ApplyDirectedLinear(kneePelvisSupport, pelvisTarget, Math.Clamp(kneeSettings.TorsoPelvisForce, 0f, 3000f) * supportGain, 10f);
    }

    private void ApplySoftFootSupport(ConstraintHandle? handle, Vector3 start, Vector3 localOffset, float gain, float force)
    {
        var target = start;
        var settings = EffectiveKneePowerLossSettings();
        var clearance = settings.FootProxyEnabled
            ? Math.Clamp(settings.FootProxyGroundClearance, 0.004f, 0.08f)
            : 0.02f;
        target.Y = groundY + MathF.Max(clearance, start.Y - groundY);
        ApplyDirectedLinear(handle, target, force * Math.Clamp(gain, 0f, 1f), 38f, localOffset);
    }

    private void ApplyKneeLateralStability(float gain, float maxSpeed)
    {
        if (simulation == null || gain <= 0f) return;
        var leftKnee = FindBodyHandle("j_asi_b_l");
        var rightKnee = FindBodyHandle("j_asi_b_r");
        if (!leftKnee.HasValue || !rightKnee.HasValue) return;

        var lateral = FlatNormalize(kneeRight, Vector3.UnitX);
        var leftBody = simulation.Bodies.GetBodyReference(leftKnee.Value);
        var rightBody = simulation.Bodies.GetBodyReference(rightKnee.Value);
        var mid = (leftBody.Pose.Position + rightBody.Pose.Position) * 0.5f;
        var startMid = (kneeLeftStart + kneeRightStart) * 0.5f;

        var leftOffset = Vector3.Dot(kneeLeftStart - startMid, lateral);
        var rightOffset = Vector3.Dot(kneeRightStart - startMid, lateral);
        var leftTarget = mid + lateral * leftOffset;
        var rightTarget = mid + lateral * rightOffset;

        ApplyKneeLateralVelocity(leftBody, lateral, Vector3.Dot(leftTarget - leftBody.Pose.Position, lateral), gain, maxSpeed);
        ApplyKneeLateralVelocity(rightBody, lateral, Vector3.Dot(rightTarget - rightBody.Pose.Position, lateral), gain, maxSpeed);
    }

    private static void ApplyKneeLateralVelocity(BodyReference body, Vector3 lateral, float error, float gain, float maxSpeed)
    {
        var velocity = Math.Clamp(error * gain, -maxSpeed, maxSpeed);
        var existing = Vector3.Dot(body.Velocity.Linear, lateral);
        // Only correct the inward/outward spacing error. Leave vertical drop and forward kneel motion free.
        body.Velocity.Linear += lateral * (velocity - existing * 0.35f);
    }

    private void SwitchKneePowerLossPhase(KneePowerLossPhase phase)
    {
        kneePowerLossPhase = phase;
        kneePowerLossPhaseElapsed = 0f;
        log.Info($"RagdollController: knee power-loss phase -> {phase}");
    }

    private float BoneHeight(string boneName)
    {
        if (simulation == null) return float.PositiveInfinity;
        return TryGetBoneOriginPosition(boneName, out var position)
            ? position.Y
            : float.PositiveInfinity;
    }

    private float MeasureCurrentKneePowerLossMinAngle()
    {
        var left = MeasureLegBendAngle("j_asi_a_l", "j_asi_b_l", "j_asi_c_l", out _);
        var right = MeasureLegBendAngle("j_asi_a_r", "j_asi_b_r", "j_asi_c_r", out _);
        if (left < 0f && right < 0f) return 0f;
        if (left < 0f) return right;
        if (right < 0f) return left;
        return MathF.Min(left, right);
    }

    private float MeasureCurrentKneePowerLossForwardOffset()
    {
        if (simulation == null) return 0f;
        var pelvis = FindBodyHandle("j_kosi");
        if (!pelvis.HasValue) return 0f;

        var pelvisPos = simulation.Bodies.GetBodyReference(pelvis.Value).Pose.Position;
        var footCenter = (kneeLeftFootStart + kneeRightFootStart) * 0.5f;
        return Vector3.Dot(pelvisPos - footCenter, kneeForward);
    }

    private void LogLegAnatomyDiagnostics(string side, string thighBone, string shinBone, string calfBone, string footBone,
        Vector3 currentHingeAxis, Vector3 currentHingeForward)
    {
        if (simulation == null) return;
        if (!TryGetBoneOriginPosition(thighBone, out var hip) ||
            !TryGetBoneOriginPosition(shinBone, out var knee) ||
            !TryGetBoneOriginPosition(calfBone, out var ankle) ||
            !TryGetBoneOriginPosition(footBone, out var foot))
            return;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        var footDir = NormalizeOrFallback(foot - ankle, kneeForward);
        var rawAxis = Vector3.Cross(thigh, shin);
        var geomAxis = rawAxis.LengthSquared() > 0.0005f
            ? Vector3.Normalize(rawAxis)
            : NormalizeOrFallback(Vector3.Cross(kneeForward, shin), currentHingeAxis);
        if (Vector3.Dot(geomAxis, currentHingeAxis) < 0)
            geomAxis = -geomAxis;

        var geomForward = NormalizeOrFallback(ProjectOntoPlane(kneeForward, geomAxis), currentHingeForward);
        var footForward = NormalizeOrFallback(ProjectOntoPlane(footDir, geomAxis), geomForward);
        if (Vector3.Dot(geomForward, currentHingeForward) < 0)
            geomForward = -geomForward;

        var thighShinAngle = MathF.Acos(Math.Clamp(Vector3.Dot(thigh, shin), -1f, 1f)) * 180f / MathF.PI;
        var frameAxisDot = Vector3.Dot(geomAxis, currentHingeAxis);
        var frameForwardDot = Vector3.Dot(geomForward, currentHingeForward);

        log.Info(
            $"RagdollController: leg anatomy {side} " +
            $"hip=({hip.X:F2},{hip.Y:F2},{hip.Z:F2}) knee=({knee.X:F2},{knee.Y:F2},{knee.Z:F2}) ankle=({ankle.X:F2},{ankle.Y:F2},{ankle.Z:F2}) foot=({foot.X:F2},{foot.Y:F2},{foot.Z:F2}) " +
            $"thigh=({thigh.X:F2},{thigh.Y:F2},{thigh.Z:F2}) shin=({shin.X:F2},{shin.Y:F2},{shin.Z:F2}) footDir=({footDir.X:F2},{footDir.Y:F2},{footDir.Z:F2}) " +
            $"geomAxis=({geomAxis.X:F2},{geomAxis.Y:F2},{geomAxis.Z:F2}) geomFwd=({geomForward.X:F2},{geomForward.Y:F2},{geomForward.Z:F2}) footFwd=({footForward.X:F2},{footForward.Y:F2},{footForward.Z:F2}) " +
            $"angle={thighShinAngle:F1} axisDot={frameAxisDot:F2} fwdDot={frameForwardDot:F2}");
    }

    private bool TryBuildLegAnatomyFrame(string thighBone, string shinBone, string calfBone, string footBone,
        Quaternion childBodyRot, out Vector3 hingeAxis, out Vector3 flexForward)
    {
        hingeAxis = Vector3.Transform(Vector3.UnitX, childBodyRot);
        flexForward = Vector3.Transform(Vector3.UnitZ, childBodyRot);

        if (!TryGetBoneOriginPosition(thighBone, out var hip) ||
            !TryGetBoneOriginPosition(shinBone, out var knee) ||
            !TryGetBoneOriginPosition(calfBone, out var ankle) ||
            !TryGetBoneOriginPosition(footBone, out var foot))
            return false;

        var thigh = NormalizeOrFallback(knee - hip, Vector3.UnitY);
        var shin = NormalizeOrFallback(ankle - knee, Vector3.UnitY);
        var footDir = NormalizeOrFallback(foot - ankle, kneeForward);

        // Tier B — anatomy-fixed knee hinge axis. The skeleton medial-lateral (character
        // RIGHT, = kneeRight) axis projected perpendicular to the shin is stable regardless
        // of how bent the leg is, so both legs resolve to near-mirror axes (~±character-
        // right) instead of the Cross(thigh,shin) result that goes degenerate (and pointed
        // FORWARD for one knee) when the leg is near-straight at death.
        var anatomicalAxis = ProjectOntoPlane(kneeRight, shin);
        if (config.RagdollAnatomicalHingeAxis && anatomicalAxis.LengthSquared() > 0.0005f)
        {
            hingeAxis = Vector3.Normalize(anatomicalAxis);
        }
        else
        {
            var rawAxis = Vector3.Cross(thigh, shin);
            if (rawAxis.LengthSquared() > 0.0005f)
            {
                hingeAxis = Vector3.Normalize(rawAxis);
            }
            else
            {
                var footProjected = ProjectOntoPlane(footDir, shin);
                if (footProjected.LengthSquared() > 0.0005f)
                    hingeAxis = NormalizeOrFallback(Vector3.Cross(footProjected, shin), hingeAxis);
                else
                    hingeAxis = NormalizeOrFallback(Vector3.Cross(kneeForward, shin), hingeAxis);
            }
        }

        // The flexion direction is the character sagittal direction projected into the knee
        // hinge plane. Do NOT use j_asi_d as "foot forward": in FFXIV this bone origin is often
        // mostly below the ankle, so ankle->foot points downward and drives the knee into a fake
        // vertical/sideways fold.
        var sagittalForward = ProjectOntoPlane(kneeForward, hingeAxis);
        var bodyForward = ProjectOntoPlane(Vector3.Transform(Vector3.UnitZ, childBodyRot), hingeAxis);
        flexForward = NormalizeOrFallback(sagittalForward, NormalizeOrFallback(bodyForward, kneeForward));

        return true;
    }

    private bool TryGetBoneOriginPosition(string boneName, out Vector3 position)
    {
        position = Vector3.Zero;
        if (simulation == null) return false;
        RagdollBone? bone = null;
        foreach (var rb in ragdollBones)
        {
            if (rb.Name == boneName)
            {
                bone = rb;
                break;
            }
        }
        if (!bone.HasValue) return false;

        var body = simulation.Bodies.GetBodyReference(bone.Value.BodyHandle);
        var yAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        position = body.Pose.Position - yAxis * bone.Value.SegmentHalfLength;
        return true;
    }

    private static Quaternion MakeRelativeFlexTarget(Quaternion childOrientation, Quaternion parentOrientation,
        Vector3 flexAxisWorld, Vector3 flexForwardWorld, float degrees)
    {
        if (flexAxisWorld.LengthSquared() < 0.0001f) flexAxisWorld = Vector3.UnitX;
        if (flexForwardWorld.LengthSquared() < 0.0001f) flexForwardWorld = Vector3.UnitZ;

        var axis = Vector3.Normalize(flexAxisWorld);
        var forward = Vector3.Normalize(flexForwardWorld);
        var radians = degrees * (MathF.PI / 180f);

        Quaternion bestChild = childOrientation;
        var bestDot = float.NegativeInfinity;
        for (int i = 0; i < 2; i++)
        {
            var sign = i == 0 ? 1f : -1f;
            var flexWorld = Quaternion.CreateFromAxisAngle(axis, radians * sign);
            var candidateChild = Quaternion.Normalize(flexWorld * childOrientation);
            var candidateAxis = Vector3.Transform(Vector3.UnitY, candidateChild);
            var dot = Vector3.Dot(candidateAxis, forward);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestChild = candidateChild;
            }
        }

        return Quaternion.Normalize(Quaternion.Inverse(bestChild) * parentOrientation);
    }

    // --- Standing support constraint API (execution mode) ---
    // Pelvis: LinearServo to a computed standing height + AngularServo upright.
    // Spine chain: progressively weaker AngularServos.
    // Legs/arms/head: fully dynamic — gravity + joints handle them naturally.
    private readonly List<ConstraintHandle> standingConstraints = new();
    private bool standingActive;
    private BodyHandle? standingAnchorHandle;
    private Vector3 standingAnchorTarget;

    public bool IsStandingSupportActive => standingActive;

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
        ApplyStandingAnchorCorrection();
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
        standingAnchorHandle = null;

        BuildStandingConstraints(anchorWorldPos, uprightRot, anchorBoneName);
        ApplyStandingAnchorCorrection();
        return true;
    }

    private void BuildStandingConstraints(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName)
    {
        var sim = simulation;
        if (sim == null) return;

        var anchorHandle = FindBodyHandle(anchorBoneName);
        if (anchorHandle.HasValue)
        {
            standingAnchorHandle = anchorHandle.Value;
            standingAnchorTarget = anchorWorldPos;

            standingConstraints.Add(sim.Solver.Add(anchorHandle.Value,
                new OneBodyLinearServo
                {
                    LocalOffset    = Vector3.Zero,
                    Target         = anchorWorldPos,
                    ServoSettings  = new ServoSettings(8f, 1f, 3000f),
                    SpringSettings = new SpringSettings(120f, 1f),
                }));

            standingConstraints.Add(sim.Solver.Add(anchorHandle.Value,
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
                standingConstraints.Add(sim.Solver.Add(h.Value,
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

    private void ApplyStandingAnchorCorrection()
    {
        if (!standingActive || simulation == null || !standingAnchorHandle.HasValue)
            return;

        var anchor = simulation.Bodies.GetBodyReference(standingAnchorHandle.Value);
        var correction = standingAnchorTarget - anchor.Pose.Position;
        var anchorVelocity = anchor.Velocity.Linear;

        // Keep the held point fixed without disabling contact response. If collision pushes the
        // ragdoll as a whole, translate every body back by the same delta and remove the global
        // anchor velocity; relative limb motion from the contact is preserved.
        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            body.Pose.Position += correction;
            body.Velocity.Linear -= anchorVelocity;
            body.Awake = true;
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
        standingAnchorHandle = null;

        var normalThreshold = 0.01f;
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

    // Each joint's angular motor drives the parent/child relative angular velocity to zero with
    // unlimited force, and a SwingLimit caps the cone the child may swing into — together they make
    // a recoil kick invisible. For a brief window we drop the motor force AND widen the swing limit
    // (keyed by child bone), so the stump actually swings up, then restore both.
    private readonly Dictionary<string, ConstraintHandle> angularMotorByBone = new();
    private readonly Dictionary<string, (ConstraintHandle Handle, SwingLimit Original)> swingLimitByBone = new();
    private const float JointMotorDamping = 0.01f;
    private const float RecoilRelaxDuration = 0.45f;
    private const float RecoilSwingAngle = 1.7f; // ~97° — wide arc during the recoil window
    private struct RecoilRelax { public string Bone; public float Remaining; }
    private readonly List<RecoilRelax> recoilRelaxers = new();

    /// <summary>Add an angular velocity to a bone's body (a rotational kick) and briefly release its
    /// joint (drop the motor, widen the swing limit) so the stump above a severed cut actually swings
    /// up instead of being held in place. Used for the body-side recoil when a limb is kicked off.</summary>
    public void ApplyAngularVelocity(string boneName, Vector3 angularVelocityDelta)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Angular += angularVelocityDelta;
        body.Awake = true;
        RelaxJointForRecoil(boneName, RecoilRelaxDuration);
        BeginBiomechanicalSettle();
    }

    /// <summary>
    /// World-space position of a ragdoll rigid body by bone name; null while the
    /// ragdoll is inactive or the bone has no body. Unlike reading the skeleton's
    /// ModelPose at framework time (which still reflects the animation pose at the
    /// death spot), this is where the physics actually put the body — cameras that
    /// track a kicked-around corpse must use it.
    /// </summary>
    public Vector3? GetBodyWorldPosition(string boneName)
    {
        if (simulation == null || !isActive) return null;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return null;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        var p = body.Pose.Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    // Temporarily free a joint so its child can swing: zero the angular motor force and widen the
    // swing-limit cone. Both are restored by TickRecoilRelaxers after the window.
    private void RelaxJointForRecoil(string boneName, float duration)
    {
        if (simulation == null) return;
        var relaxed = false;
        if (angularMotorByBone.TryGetValue(boneName, out var motorHandle))
        {
            try
            {
                simulation.Solver.ApplyDescription(motorHandle,
                    new AngularMotor { TargetVelocityLocalA = Vector3.Zero, Settings = new MotorSettings(0f, 0f) });
                relaxed = true;
            }
            catch { }
        }
        if (swingLimitByBone.TryGetValue(boneName, out var swing))
        {
            var wide = swing.Original;
            wide.MaximumSwingAngle = MathF.Max(swing.Original.MaximumSwingAngle, RecoilSwingAngle);
            try { simulation.Solver.ApplyDescription(swing.Handle, wide); relaxed = true; }
            catch { }
        }
        if (relaxed)
            recoilRelaxers.Add(new RecoilRelax { Bone = boneName, Remaining = duration });
    }

    private void TickRecoilRelaxers(float dt)
    {
        if (recoilRelaxers.Count == 0 || simulation == null) return;
        for (int i = recoilRelaxers.Count - 1; i >= 0; i--)
        {
            var r = recoilRelaxers[i];
            r.Remaining -= dt;
            if (r.Remaining > 0f) { recoilRelaxers[i] = r; continue; }
            recoilRelaxers.RemoveAt(i);
            if (angularMotorByBone.TryGetValue(r.Bone, out var motorHandle))
                try { simulation.Solver.ApplyDescription(motorHandle,
                    new AngularMotor { TargetVelocityLocalA = Vector3.Zero, Settings = new MotorSettings(float.MaxValue, JointMotorDamping) }); }
                catch { /* constraint gone (sim torn down) */ }
            if (swingLimitByBone.TryGetValue(r.Bone, out var swing))
                try { simulation.Solver.ApplyDescription(swing.Handle, swing.Original); }
                catch { }
        }
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
        if (standingActive) return;
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
        if (standingActive) return;
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
            var npcState = npcCollisionStates[i];
            ParkNpcStatics(ref npcState, new Vector3(0, -9999, 0));
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
        if (standingActive) return;
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
        externalBodyGeneration++;
        grabConstraintActive = false;
        suspendedNpcAddress = nint.Zero;
        standingActive = false;
        standingConstraints.Clear();
        wristConstraints.Clear();
        angularMotorByBone.Clear();
        swingLimitByBone.Clear();
        recoilRelaxers.Clear();
        biomechanicalSettleRemaining = 0f;
        ragdollBones.Clear();
        externalBodies.Clear();
        externalRigs.Clear();
        externalDynamicBodyHandles.Clear();
        externalRigDynamicBodyHandles.Clear();
        externalRigNoRagdollContactBodyHandles.Clear();
        externalRigConnectedPairs.Clear();
        externalRigSelfCollideGroupByBody.Clear();
        nextExternalRigSelfCollideGroup = 1;
        softKinematicBodyHandles.Clear();
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
    public Configuration? Config;

    // Connected body pairs that should NOT collide (parent-child joints).
    // All other body-body pairs DO collide (arms vs torso, etc.).
    public HashSet<(int, int)>? ConnectedPairs;
    public HashSet<int>? ExternalDynamicBodies;
    public HashSet<int>? ExternalRigDynamicBodies;
    public HashSet<int>? ExternalRigNoRagdollContactBodies;
    public HashSet<(int, int)>? ExternalRigConnectedPairs;
    public Dictionary<int, int>? ExternalRigSelfCollideGroupByBody;
    public HashSet<int>? SoftKinematicBodies;
    public HashSet<int>? RestrictedStatics;
    public HashSet<int>? AllowedDynamicBodiesForRestrictedStatics;

    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Always allow body-static collisions (ragdoll vs ground)
        if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static)
        {
            var dynamicRef = a.Mobility == CollidableMobility.Dynamic ? a :
                b.Mobility == CollidableMobility.Dynamic ? b : default;
            if (dynamicRef.Mobility == CollidableMobility.Dynamic && IsExternalDynamic(dynamicRef.BodyHandle.Value))
                speculativeMargin = MathF.Min(speculativeMargin, 0.016f);

            if (RestrictedStatics != null && AllowedDynamicBodiesForRestrictedStatics != null &&
                ((a.Mobility == CollidableMobility.Static && RestrictedStatics.Contains(a.StaticHandle.Value)) ||
                 (b.Mobility == CollidableMobility.Static && RestrictedStatics.Contains(b.StaticHandle.Value))))
            {
                return dynamicRef.Mobility == CollidableMobility.Dynamic &&
                       AllowedDynamicBodiesForRestrictedStatics.Contains(dynamicRef.BodyHandle.Value);
            }
            return true;
        }

        if ((a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Kinematic) ||
            (b.Mobility == CollidableMobility.Dynamic && a.Mobility == CollidableMobility.Kinematic))
        {
            return true;
        }

        // Body-body: allow UNLESS they are directly connected by a joint.
        // Connected pairs would explode because they share a constraint anchor point.
        if (a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
        {
            var aId = a.BodyHandle.Value;
            var bId = b.BodyHandle.Value;
            var aExternal = IsExternalDynamic(aId);
            var bExternal = IsExternalDynamic(bId);
            var aRig = IsExternalRigDynamic(aId);
            var bRig = IsExternalRigDynamic(bId);
            if (aRig || bRig)
            {
                var rigLo = Math.Min(aId, bId);
                var rigHi = Math.Max(aId, bId);
                if (ExternalRigConnectedPairs != null && ExternalRigConnectedPairs.Contains((rigLo, rigHi)))
                    return false;

                // Bodies inside the same non-self-colliding rig (e.g. a garment tube) never collide with
                // each other: the tube is held in shape by its distance-limit edges, and letting its own
                // panels pile up on each other makes it jitter and refuse to settle.
                if (aRig && bRig && IsSameSelfCollideGroup(aId, bId))
                    return false;

                // The garment rig should collide with the corpse ragdoll, but not with legacy
                // single-piece dropped gear. Single gear remains ground-only by design below.
                if ((aRig && bExternal && !bRig) || (bRig && aExternal && !aRig))
                    return false;

                // Body garments spawn around the torso, where the corpse ragdoll capsules overlap the
                // visual clothing volume. Let those rigs keep ground/static contacts but skip corpse
                // dynamic contacts so handoff does not shove the shirt backward in one frame.
                var aNoRagdoll = IsExternalRigNoRagdollContact(aId);
                var bNoRagdoll = IsExternalRigNoRagdollContact(bId);
                if ((aNoRagdoll && !bExternal && !bRig) || (bNoRagdoll && !aExternal && !aRig))
                    return false;

                return true;
            }

            if (aExternal || bExternal)
            {
                // Dropped gear falls independently straight to the ground: it must NOT rest on the
                // corpse's invisible limb capsules or pile on other dropped pieces. That dynamic-vs-
                // dynamic stacking is what left garments hanging in the air (and only dropping when an
                // NPC kick knocked the stack loose). Ground (static) and NPC strikes (kinematic) are
                // handled above, so gear still lands and can still be struck.
                return false;
            }

            if (ConnectedPairs == null)
                return false;

            var lo = Math.Min(aId, bId);
            var hi = Math.Max(aId, bId);
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

        // Self-collision (limb vs own limb) is dynamic-vs-dynamic. At rest the corpse's
        // capsules overlap (thighs touching, forearms on the torso, hands on legs); a crisp
        // 2 m/s recovery shoves those overlaps apart every step, the joints pull them back,
        // and the velocity never decays below the sleep threshold — so the body writhes
        // forever and never settles. Give self-contacts a gentle, overdamped recovery so
        // resting overlaps stop pumping energy and the rig can sleep. Ground (static) and
        // external strikes (kinematic) keep the firm 2 m/s recovery for crisp response.
        if (UseAdvancedGearContactFriction() && IsGearGroundContact(pair))
        {
            pairMaterial.MaximumRecoveryVelocity = 0.25f;
            pairMaterial.FrictionCoefficient = MathF.Max(Friction, 3.5f);
            pairMaterial.SpringSettings = new SpringSettings(12, 3);
        }
        else if (UseAdvancedGearContactFriction() && IsGearContact(pair))
        {
            pairMaterial.MaximumRecoveryVelocity = 0.08f;
            pairMaterial.FrictionCoefficient = Math.Clamp(Friction, 0.45f, 0.9f);
            pairMaterial.SpringSettings = new SpringSettings(6, 4);
        }
        else if (pair.A.Mobility == CollidableMobility.Dynamic && pair.B.Mobility == CollidableMobility.Dynamic)
        {
            pairMaterial.MaximumRecoveryVelocity = 0.2f;
            pairMaterial.SpringSettings = new SpringSettings(20, 2);
        }
        else if (IsSoftKinematicContact(pair))
        {
            pairMaterial.MaximumRecoveryVelocity = 0.25f;
            pairMaterial.SpringSettings = new SpringSettings(12, 3);
        }
        else
        {
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings(30, 1);
        }
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }

    private bool IsExternalDynamic(int bodyHandle)
        => ExternalDynamicBodies != null && ExternalDynamicBodies.Contains(bodyHandle);

    private bool IsExternalRigDynamic(int bodyHandle)
        => ExternalRigDynamicBodies != null && ExternalRigDynamicBodies.Contains(bodyHandle);

    private bool IsExternalRigNoRagdollContact(int bodyHandle)
        => ExternalRigNoRagdollContactBodies != null && ExternalRigNoRagdollContactBodies.Contains(bodyHandle);

    private bool IsSameSelfCollideGroup(int aHandle, int bHandle)
        => ExternalRigSelfCollideGroupByBody != null &&
           ExternalRigSelfCollideGroupByBody.TryGetValue(aHandle, out var ga) &&
           ExternalRigSelfCollideGroupByBody.TryGetValue(bHandle, out var gb) &&
           ga == gb;

    private bool IsSoftKinematicContact(CollidablePair pair)
        => SoftKinematicBodies != null &&
           ((pair.A.Mobility == CollidableMobility.Kinematic && SoftKinematicBodies.Contains(pair.A.BodyHandle.Value)) ||
            (pair.B.Mobility == CollidableMobility.Kinematic && SoftKinematicBodies.Contains(pair.B.BodyHandle.Value)));

    private bool UseAdvancedGearContactFriction()
        => Config?.KoStripPhysicsDropClothing == true &&
           Config.KoStripAdvancedClothPhysics;

    // "Gear" for contact-material purposes = single dropped pieces (ExternalDynamic) AND articulated
    // garment-rig bodies (ExternalRigDynamic). In the player-ragdoll host sim a rig body is in BOTH sets,
    // so folding the rig set in changes nothing there; it only matters in the DismembermentController's
    // LOCAL fallback sim, where rig bodies are ExternalRigDynamic-only. Without this, local-sim garments
    // fell through to the firm 2 m/s / (30,1) default material and visibly popped apart on the first step.
    private bool IsGearDynamic(int bodyHandle)
        => IsExternalDynamic(bodyHandle) || IsExternalRigDynamic(bodyHandle);

    private bool IsGearContact(CollidablePair pair)
    {
        var aGear = pair.A.Mobility == CollidableMobility.Dynamic && IsGearDynamic(pair.A.BodyHandle.Value);
        var bGear = pair.B.Mobility == CollidableMobility.Dynamic && IsGearDynamic(pair.B.BodyHandle.Value);
        return aGear || bGear;
    }

    private bool IsGearGroundContact(CollidablePair pair)
    {
        var aGear = pair.A.Mobility == CollidableMobility.Dynamic && IsGearDynamic(pair.A.BodyHandle.Value);
        var bGear = pair.B.Mobility == CollidableMobility.Dynamic && IsGearDynamic(pair.B.BodyHandle.Value);
        return (aGear && pair.B.Mobility == CollidableMobility.Static) ||
               (bGear && pair.A.Mobility == CollidableMobility.Static);
    }
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
