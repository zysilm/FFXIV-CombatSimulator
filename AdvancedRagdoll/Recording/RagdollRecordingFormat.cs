// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Animation.Recording;

/// <summary>
/// The on-disk format of a ragdoll recording (docs/REFACTOR_PLAN.md, stage A0).
///
/// A file is: 8 bytes of <see cref="Magic"/>, then records until the end of the file. A record is
/// <c>[u16 type][u32 payload length][payload]</c>, little-endian. A payload is a flat sequence of
/// tagged values (<see cref="ValueTag"/>): one tag byte followed by the value. Floats are stored as
/// their raw IEEE-754 bits and are never rounded or normalised, so NaN payloads and the sign of
/// zero survive a round trip.
///
/// One file holds one ragdoll activation of one controller. Records appear in the exact order the
/// events happened. The first record is always <see cref="RecordType.Header"/>. A file that was cut
/// short (crash, disk full) is still readable up to its last complete record.
/// </summary>
public static class RagdollRecordingFormat
{
    /// <summary>"RGDREC" + two zero bytes.</summary>
    public static ReadOnlySpan<byte> Magic => "RGDREC\0\0"u8;

    /// <summary>Bumped whenever the meaning of an existing record changes, or a record gains values a replay
    /// cannot do without. Adding a record type or appending optional values to the end of a record does not
    /// need a bump: readers ignore what they do not know.
    /// <list type="bullet">
    /// <item>1: first version.</item>
    /// <item>2: <see cref="RecordType.Activate"/> carries the state that survives from one activation of a
    /// controller to the next (see <see cref="ActivateCarriedStateIndex"/>); a record whose body did not
    /// finish is replaced by a note (<see cref="DiscardedRecordNote"/>); the traversal look-ahead query
    /// tags its reads <see cref="ActorSite.TraversalPreviewRoot"/>; <c>TryResolveGrabAttachment</c> is a
    /// recorded call; <see cref="RecordType.Identity"/> reports model type -1 when it was not read.</item>
    /// </list></summary>
    public const int Version = 2;

    /// <summary>The oldest version whose <see cref="RecordType.Activate"/> record holds the carried-over state.</summary>
    public const int FirstVersionWithCarriedState = 2;

    /// <summary>Index of the first carried-over value (impactRandomState) in an <see cref="RecordType.Activate"/>
    /// record, given the two lists in front of it: 25 fixed values, then the two lists with their items.</summary>
    public static int ActivateCarriedStateIndex(RagdollRecord activate)
    {
        var at = 25;
        for (var list = 0; list < 2; list++)
        {
            if (at >= activate.Values.Length || activate.Values[at].Tag != ValueTag.List) return -1;
            at += 1 + activate.Values[at].Count;
        }
        return at;
    }

    /// <summary>Start of the text of the <see cref="RecordType.Note"/> written in place of a record whose body
    /// did not finish. The record type's name and number follow.</summary>
    public const string DiscardedRecordNote = "partial record discarded: ";

    public const string FileExtension = ".rgdrec";

    /// <summary>Floats per body in <see cref="RecordType.BodyStates"/>: position, orientation, linear and angular velocity.</summary>
    public const int BodyStateFloats = 13;

    /// <summary>Bytes per bone in a pose block: translation (4 floats), rotation (4), scale (4), as the game stores them.</summary>
    public const int PoseBoneBytes = 48;

    public static readonly string[] BodyStateFieldNames =
    {
        "position.x", "position.y", "position.z",
        "orientation.x", "orientation.y", "orientation.z", "orientation.w",
        "linear.x", "linear.y", "linear.z",
        "angular.x", "angular.y", "angular.z",
    };

    public static readonly string[] PoseFieldNames =
    {
        "translation.x", "translation.y", "translation.z", "translation.w",
        "rotation.x", "rotation.y", "rotation.z", "rotation.w",
        "scale.x", "scale.y", "scale.z", "scale.w",
    };

    /// <summary>True for records that are produced by the simulation (compared by the checker);
    /// everything else is an input or a marker.</summary>
    public static bool IsOutput(RecordType type) => type is
        RecordType.BodyStates or RecordType.PoseOut or RecordType.RigBodies or
        RecordType.ApiReturn or RecordType.InitEnd or RecordType.FrameEnd or RecordType.Event;

    /// <summary>True for records that are an answer of the game world to a query.</summary>
    public static bool IsWorldQuery(RecordType type) => type is
        RecordType.Raycast or RecordType.GroundTriangles or RecordType.WaterSurface;
}

/// <summary>Tag byte in front of every value of a record payload.</summary>
public enum ValueTag : byte
{
    Null = 0,
    Bool = 1,       // 1 byte
    I32 = 2,        // 4 bytes
    U32 = 3,        // 4 bytes
    I64 = 4,        // 8 bytes
    U64 = 5,        // 8 bytes
    F32 = 6,        // 4 bytes, raw bits
    F64 = 7,        // 8 bytes, raw bits
    String = 8,     // i32 byte count + UTF-8
    Vector2 = 9,    // 2 floats
    Vector3 = 10,   // 3 floats
    Quaternion = 11,// 4 floats (x, y, z, w)
    Bytes = 12,     // i32 byte count + bytes
    F32Array = 13,  // i32 element count + 4 bytes each
    I32Array = 14,  // i32 element count + 4 bytes each
    Vector3Array = 15, // i32 element count + 12 bytes each
    QuaternionArray = 16, // i32 element count + 16 bytes each
    List = 17,      // i32 item count; the next "count" values (flat) belong to it
}

/// <summary>
/// Record types and the values each one carries, in order. "actor" is the game object address as an
/// opaque u64 (only equality matters). Positions are world space, metres, Y up.
/// </summary>
public enum RecordType : ushort
{
    // ---- file ----

    /// <summary>formatVersion:i32, pluginVersion:str, devExperimental:bool, publicBuild:bool, bepuVersion:str,
    /// runtime:str, os:str, architecture:str, processorCount:i32, vectorWidth:i32 (floats per SIMD vector),
    /// simd:str (instruction sets), littleEndian:bool, utcTicks:i64, stopwatchFrequency:i64,
    /// session:str, fileIndex:i32, controller:i32 (opaque id of the controller instance),
    /// mainController:bool, actor:u64, entityId:u32, objectIndex:i32, isLocalPlayer:bool.</summary>
    Header = 1,

    /// <summary>reason:str, renderFrames:i32, physicsFrames:i32, fixedSteps:i32, records:i64, seconds:f64.</summary>
    End = 2,

    /// <summary>text:str. Free-form remark (for example a recorder error). A text that starts with
    /// <see cref="RagdollRecordingFormat.DiscardedRecordNote"/> stands where a record would have been: its
    /// body threw half-way, so the record itself was not written.</summary>
    Note = 3,

    // ---- configuration ----

    /// <summary>List of (name:str, value) pairs: every configuration property the ragdoll code reads, at activation.
    /// Values keep their type (bool, i32, f32, str); enums are i32; lists and objects are a JSON string.</summary>
    ConfigSnapshot = 10,

    /// <summary>name:str, value. One of the snapshot's properties was seen with a different value; written
    /// at the start of the render frame in which the difference was first seen.</summary>
    ConfigChange = 11,

    // ---- activation ----

    /// <summary>actor:u64, delay:f32, mode:i32, stagger:f32, position:Vector3 (GameObject.Position),
    /// entityId:u32, objectIndex:i32, isLocalPlayer:bool, hasNpcSelector:bool, hasMovementBlock:bool,
    /// hasExtraCollisionProvider:bool, runAnimatedHair:bool, grabRigid:bool, hasCarryCallback:bool,
    /// then state that calls made before the activation may have left behind: pendingDirectedCollapse:bool,
    /// pendingProfile:str, pendingDuration:f32, pendingFootForce:f32, pendingPelvisForce:f32, pendingDrop:f32,
    /// pendingForward:f32, pendingPitchDegrees:f32, pendingEntryConditionedKneePowerLoss:bool,
    /// pendingKneePowerLoss:bool, useLocalSeveredBones:bool, localSeveredBones: List of str,
    /// lightweightColliders: List of u64,
    /// then (format version 2) state the controller carries over from its previous activations, which neither
    /// Activate nor the teardown resets: impactRandomState:u32 (the landing-jitter generator),
    /// nextGrabSlotId:i32, externalBodyGeneration:i32 (stamped on external body and rig handles),
    /// struckThisWindow: List of i32 (body handles already hit in the current strike window),
    /// strikeColliders: List of u64, traversalProxyRequests:i32, then per request (in the order the
    /// simulation will walk them): actor:u64, enabled:bool, initialRoot:Vector3, targetRoot:Vector3, floorY:f32,
    /// visualScale:f32, moveSpeed:f32, maxClimb:f32, stompResponse:f32, concurrentStompers:i32, locomoting:bool.</summary>
    Activate = 12,

    /// <summary>actor:u64, customize:Bytes (the character's customize block), modelCharaId:i32,
    /// modelType:i32 (CharacterBase model type; -1 when it was not read: the recorder asks for it only
    /// where the ragdoll code itself would, that is once a skeleton with models was obtained), raceSexId:i32 (-1 unless human),
    /// facing:f32 (GameObject.Rotation), position:Vector3.</summary>
    Identity = 13,

    // ---- skeletons ----

    /// <summary>actor:u64, partial:i32, boneCount:i32, parentCount:i32, connectedBone:i32, connectedParentBone:i32,
    /// names: List of str, parents:I32Array, referencePose:Bytes (48 bytes per bone, local space).
    /// Written the first time a skeleton is seen and again if its identity changes.</summary>
    SkeletonTopology = 20,

    /// <summary>actor:u64, purpose:i32 (<see cref="PosePurpose"/>), available:bool. When available:
    /// position:Vector3, rotation:Quaternion, scale:Vector3 (Skeleton.Transform), drawScaleValid:bool,
    /// drawScale:Vector3 (DrawObject.Scale), partialCount:i32, then per captured partial:
    /// partial:i32, state:i32 (low two bits: 0 = data follows, 1 = identical to the previous record of the same
    /// purpose and partial, 2 = unreadable; bit 2 set = the game marked the model pose as not in sync),
    /// [modelPose:Bytes (48 bytes per bone, model space)].</summary>
    SkeletonPose = 21,

    /// <summary>Same layout as <see cref="SkeletonPose"/>: the target's pose after this frame's write-back
    /// (physics bones, bones propagated from them, attached partial skeletons, scale writes, hair).</summary>
    PoseOut = 22,

    // ---- before physics starts ----

    /// <summary>renderFrame:i32, dt:f32, elapsed:f32 (after adding dt), physicsStarted:bool,
    /// attackStrikeTimer:f32, strikePower:f32, strikeCollider:u64.</summary>
    RenderFrame = 30,

    /// <summary>hasAnimation:bool, animation:u64, localTime:f32, duration:f32, hasMiddleHeight:bool, middleHeight:f32,
    /// hasMotion:bool, motion:f32. What the start trigger was given this frame.</summary>
    TriggerInputs = 31,

    /// <summary>Unused (the decision is a Decision record with site "ShouldStartPhysics").</summary>
    TriggerDecision = 32,

    // ---- InitializePhysics ----

    /// <summary>handoffValid:bool, handoffDt:f32, handoffPosition:Vector3, handoffRotation:Quaternion,
    /// handoffScale:Vector3, handoffBoneCount:i32, handoffPositions:Vector3Array, handoffRotations:QuaternionArray.</summary>
    InitBegin = 40,

    /// <summary>generic:bool, count:i32, then per definition a List of (field:str, value) pairs for every field
    /// of the bone definition actually used (after configuration, soft-tissue discovery and severed limbs),
    /// followed by skeletonIndex:i32.</summary>
    BoneDefs = 41,

    /// <summary>outcome:i32 (0 not attempted, 1 no fit started, 2 timed out: built-in volumes, 3 completed,
    /// 4 failed), count:i32, then per fit: bone:str and a List of (field:str, value) pairs.</summary>
    StructuralFits = 42,

    /// <summary>use:str, id:i32 (opaque, per recording), count:i32, then per part: name:str, vertices:Vector3Array,
    /// indices:I32Array, hasLod:bool. Bone-local surface of the character as cached (the frozen mesh cache).</summary>
    MeshCache = 43,

    /// <summary>site:str, bone:str, vertices:Vector3Array, indices:I32Array, sourceVertexIndices:I32Array,
    /// sourceFaceCount:i32. A collision surface that was taken from the cache and installed: a reduced one
    /// (site "init" or "step") or the surface given to a weapon-contact twin (site "weaponContactProxy").</summary>
    MeshLod = 44,

    /// <summary>use:str, scale:Vector3, triangles:Vector3Array (3 per triangle). Finished triangle soup handed to
    /// the simulation (another actor's skinned mesh collider).</summary>
    Triangles = 45,

    /// <summary>present:bool, animatedHair:bool, visibleVertices:i32, count:i32, then per region: bone:str, positions:Vector3Array,
    /// influenceCounts:I32Array, influenceBones: List of str, influenceWeights:F32Array.</summary>
    HairMesh = 46,

    /// <summary>count:i32, then per body: name:str, skeletonIndex:i32, handle:i32, and a List of (field:str, value)
    /// pairs for the simple fields of the body entry.</summary>
    RigBodies = 47,

    /// <summary>ok:bool, bodies:i32, groundY:f32, realGroundY:f32, skeletonPosition:Vector3,
    /// skeletonRotation:Quaternion, skeletonScale:Vector3, generic:bool, solverIterations:i32,
    /// solverSubsteps:i32, totalBodies:i32, statics:i32, savedAnimationSpeed:f32, colliderActors:i32,
    /// hairChains:i32.</summary>
    InitEnd = 48,

    // ---- physics frames ----

    /// <summary>physicsFrame:i32, dt:f32, accumulator:f32 (before this frame), prevAllAsleep:bool,
    /// entityId:u32, objectIndex:i32, followWasActive:bool, impactFreezeRemaining:f32,
    /// settleRemaining:f32, grabSlots:i32, externalBodies:i32, externalRigs:i32.</summary>
    FrameBegin = 50,

    /// <summary>physicsFrame:i32, substeps:i32, resting:bool, accumulator:f32 (after), renderAlpha:f32, tick:i32
    /// (fixed steps taken so far in this recording).</summary>
    FrameEnd = 51,

    /// <summary>tick:i32 (0 = right after InitializePhysics; then 1, 2, ... one per fixed step),
    /// physicsFrame:i32, substep:i32, steppedExternally:bool, handles:I32Array, flags:Bytes (bit 0 awake,
    /// bit 1 kinematic), state:F32Array (13 floats per body: position, orientation, linear, angular).</summary>
    BodyStates = 52,

    // ---- world queries ----

    /// <summary>site:i32 (<see cref="RaycastSite"/>), origin:Vector3, direction:Vector3, maxDistance:f32, hit:bool,
    /// point:Vector3, normal:Vector3, distance:f32, material:u64, v1:Vector3, v2:Vector3, v3:Vector3.</summary>
    Raycast = 60,

    /// <summary>min:Vector3, max:Vector3, budget:i32, ok:bool, triangles:Vector3Array (3 per triangle).</summary>
    GroundTriangles = 61,

    /// <summary>position:Vector3, up:f32, down:f32, found:bool, surface:f32.</summary>
    WaterSurface = 62,

    // ---- other actors ----

    /// <summary>site:i32 (<see cref="ActorSite"/>), actor:u64, position:Vector3 (GameObject.Position).</summary>
    ActorPosition = 70,

    /// <summary>site:i32, actor:u64, position:Vector3, rotation:Quaternion, scaleValid:bool, scale:Vector3.
    /// The transform another actor's collider was driven from when no skeleton pose was read.</summary>
    ActorTransform = 71,

    /// <summary>site:str, actors: List of u64. An ordered list of actors given to the ragdoll.</summary>
    ActorList = 72,

    /// <summary>attached:bool, position:Vector3, rotation:Quaternion. The striking actor's weapon, read once a frame.</summary>
    WeaponSample = 73,

    /// <summary>site:str, actor:u64, bone:str, found:bool, position:Vector3. A bone's world position read from the
    /// game outside the per-frame pose capture (for example when a grab attaches to a bone that has no body).</summary>
    BoneWorldPosition = 74,

    // ---- calls from the rest of the plugin ----

    /// <summary>depth:i32 (0 = called from outside; more = called while another recorded call or a frame was
    /// running), method:str, then the arguments in declaration order.</summary>
    ApiCall = 80,

    /// <summary>method:str, then the returned value and any out arguments.</summary>
    ApiReturn = 81,

    // ---- clocks, dice, decisions ----

    /// <summary>site:str, value:f64. A number drawn from an unseeded random source.</summary>
    Random = 90,

    /// <summary>site:str, value (bool, i32 or str). A decision that depended on wall-clock time, on a background
    /// task or on code outside this recording; a replay must take the same one.</summary>
    Decision = 91,

    /// <summary>site:str, ticks:i64 (Stopwatch timestamp; frequency in the header).</summary>
    Clock = 92,

    /// <summary>name:str, then values. Something the ragdoll told the rest of the plugin (for example a hard landing).</summary>
    Event = 93,
}

/// <summary>Why a skeleton pose was captured.</summary>
public enum PosePurpose
{
    /// <summary>Target, a render frame before physics started (hand-over sample, trigger input).</summary>
    PreFrame = 0,
    /// <summary>Target, at the start of InitializePhysics (every partial skeleton).</summary>
    Init = 1,
    /// <summary>Target, a physics frame, before anything was written back (every partial skeleton).</summary>
    FrameIn = 2,
    /// <summary>Target, after the frame's write-back (<see cref="RecordType.PoseOut"/>).</summary>
    FrameOut = 3,
    /// <summary>Another actor, when its collider was built.</summary>
    ColliderBuild = 4,
    /// <summary>Another actor, when its collider was moved this frame.</summary>
    ColliderFrame = 5,
    /// <summary>Target, when the hull fit was started at activation.</summary>
    Activation = 6,
}

public enum RaycastSite
{
    InitGround = 0,
    PatchCenter = 1,
    PatchGrid = 2,
    SkeletonMoved = 3,
    /// <summary>The live (animated) hair's ground samples. Not part of a ragdoll activation: never recorded.</summary>
    AnimatedHair = 4,
}

public enum ActorSite
{
    TerrainPatchCandidate = 0,
    ColliderRoot = 1,
    ColliderMesh = 2,
    ColliderFallback = 3,
    ColliderFallbackBuild = 4,
    /// <summary>Read by <c>TryGetNpcTraversalRootHeight</c> (a recorded call).</summary>
    TraversalRoot = 5,
    /// <summary>Read by the look-ahead query <c>TryPreviewNpcTraversalRootHeight</c>, which is not a recorded
    /// call and changes nothing: a replay skips these records.</summary>
    TraversalPreviewRoot = 6,
}
