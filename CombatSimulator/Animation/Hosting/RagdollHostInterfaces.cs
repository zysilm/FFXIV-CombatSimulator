// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Recording;

namespace CombatSimulator.Animation.Hosting;

// What the ragdoll asks of its host (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.3, stages A2a and A2b).
//
// Stage A2a keeps these in the plugin assembly; they move to the library in A4. Two departures from the
// §2.3 sketch, both to keep every memory access and every float operation exactly as it was:
// - actors are still identified by the game object address (nint, used only as an opaque id); the
//   nint -> ActorId swap is the mechanical A4 step the design describes;
// - a skeleton hands out its model pose as a writable span over the live pose (the game's memory, or the
//   replay's copy of it) instead of the batch copy-in / copy-out of §2.3. The code reads and writes the
//   same bytes in the same order as before; the batch form comes with the library API in A4.

/// <summary>One bone transform with the exact layout of the game's hkQsTransformf (48 bytes):
/// translation (x, y, z, w), rotation (x, y, z, w), scale (x, y, z, w).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BoneQs
{
    public Vector4 Translation;
    public Quaternion Rotation;
    public Vector4 Scale;
}

/// <summary>The skeleton root: model space to world space (the game's Skeleton.Transform).</summary>
public readonly record struct RootTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// One character's skeleton for the current frame. Part 0 is the body; the others are attached partial
/// skeletons (face, hair, ...). The body members below are part 0 as the ragdoll has always read it.
/// </summary>
public interface ISkeletonPose
{
    /// <summary>Opaque identity of the character the skeleton belongs to.</summary>
    nint Actor { get; }
    /// <summary>Opaque identity of the draw object: changes when the character is redrawn.</summary>
    nint DrawIdentity { get; }

    /// <summary>Body bones in the pose (the game's LocalPose and ModelPose lengths, which are equal).</summary>
    int BoneCount { get; }
    /// <summary>Entries of the body's parent index table.</summary>
    int ParentCount { get; }
    /// <summary>The body's model-space pose: read and written in place.</summary>
    Span<BoneQs> ModelPose { get; }
    /// <summary>Parent index of a body bone (-1 for the root); <paramref name="bone"/> below <see cref="ParentCount"/>.</summary>
    short Parent(int bone);
    /// <summary>Number of body bone names.</summary>
    int BoneNameCount { get; }
    /// <summary>Name of a body bone, below <see cref="BoneNameCount"/>.</summary>
    string? BoneName(int bone);
    /// <summary>The body's reference pose (parent-local), empty when the skeleton has none.</summary>
    ReadOnlySpan<BoneQs> ReferencePose { get; }

    /// <summary>False when the draw object has no skeleton root (the ragdoll then treats the skeleton as unusable).</summary>
    bool HasRoot { get; }
    /// <summary>The skeleton root transform; only meaningful when <see cref="HasRoot"/>.</summary>
    RootTransform Root { get; }

    /// <summary>The animation with the most weight on the body right now, if any.</summary>
    bool TryGetDominantAnimation(out RagdollAnimationSample sample);

    /// <summary>Number of partial skeletons, the body included.</summary>
    int PartCount { get; }
    /// <summary>The partial has a pose and a skeleton.</summary>
    bool PartAvailable(int part);
    /// <summary>The partial has a pose with model-space data (its skeleton may still be missing).</summary>
    bool PartHasPose(int part);
    /// <summary>The game marked the partial's model pose as current.</summary>
    bool PartInSync(int part);
    /// <summary>Opaque identity of the partial's skeleton definition: changes when the partial is rebuilt.</summary>
    nint PartIdentity(int part);
    /// <summary>Opaque identity of the partial's pose object.</summary>
    nint PartPoseIdentity(int part);
    /// <summary>The partial's model pose, read and written in place (empty when it has none).</summary>
    Span<BoneQs> PartModelPose(int part);
    /// <summary>Bones in the partial's model pose, as the game reports them.</summary>
    int PartModelPoseLength(int part);
    int PartBoneNameCount(int part);
    string? PartBoneName(int part, int bone);
    int PartParentCount(int part);
    short PartParent(int part, int bone);
    ReadOnlySpan<BoneQs> PartReferencePose(int part);
    /// <summary>Entries of the partial's reference pose, as the game reports them.</summary>
    int PartReferenceCount(int part);
    int PartConnectedBone(int part);
    int PartConnectedParentBone(int part);
    /// <summary>Has the game rebuild the partial's model pose from its local pose (hands the pose back to the animation).</summary>
    void ResyncPartModelPose(int part);
}

/// <summary>Why a skeleton is read. The named sites are where the recorder captures the pose that was read
/// (their values are those of <see cref="PosePurpose"/>); every other read is <see cref="Other"/>.</summary>
public enum SkeletonReadSite
{
    Other = -1,
    PreFrame = PosePurpose.PreFrame,
    Init = PosePurpose.Init,
    FrameIn = PosePurpose.FrameIn,
    ColliderBuild = PosePurpose.ColliderBuild,
    ColliderFrame = PosePurpose.ColliderFrame,
    /// <summary>A mesh collider follows its character's skeleton root each frame; only the root is used, and it is
    /// recorded as the ActorTransform record of <see cref="ActorSite.ColliderMesh"/> (stage A2b names the site so a
    /// replay can answer it from there).</summary>
    ColliderMeshRoot = 100,
}

/// <summary>Skeletons by character.</summary>
public interface ISkeletonSource
{
    /// <summary>The body skeleton of a character, or null when it cannot be read this frame.</summary>
    ISkeletonPose? TryGetSkeleton(nint actor, SkeletonReadSite site = SkeletonReadSite.Other);
    /// <summary>A body bone's world position read now (outside the per-frame pose capture), or null.</summary>
    Vector3? GetBoneWorldPosition(nint actor, string boneName);
}

/// <summary>Answer of a ray query; field for field what the game's raycast returns.</summary>
public struct RayHit
{
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public ulong Material;
    public Vector3 V1, V2, V3;
}

/// <summary>Questions about the static world.</summary>
public interface IWorldQuery
{
    bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit);
    /// <summary>Ground triangles (3 vertices each) in a box; false when the scene could not be read or had too many.</summary>
    bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles);
    /// <summary>The water surface around a point, if there is water.</summary>
    bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY);
}

/// <summary>Where a character's position or transform is read; recorded sites are those of <see cref="ActorSite"/>.</summary>
public enum ActorReadSite
{
    TerrainPatchCandidate = ActorSite.TerrainPatchCandidate,
    ColliderRoot = ActorSite.ColliderRoot,
    ColliderMesh = ActorSite.ColliderMesh,
    ColliderFallback = ActorSite.ColliderFallback,
    ColliderFallbackBuild = ActorSite.ColliderFallbackBuild,
    TraversalRoot = ActorSite.TraversalRoot,
    TraversalPreviewRoot = ActorSite.TraversalPreviewRoot,
    /// <summary>The target at activation (recorded inside the Activate record).</summary>
    Activation = 100,
    /// <summary>The recorder's read for the Identity record.</summary>
    Identity = 102,
    /// <summary>The target's facing when the joints are built (recorded with the Identity record).</summary>
    Facing = 101,
}

/// <summary>A character the ragdoll may collide with, as the selector lists it.</summary>
public readonly record struct ColliderCandidate(nint Address, bool HasCharacter, string Name);

/// <summary>The characters: the ragdoll's own target and everyone else. Addresses are opaque ids.</summary>
public interface IRagdollActor
{
    /// <summary>False while the game is shutting down or logged out: then nothing may be written to characters.</summary>
    bool GameRunning { get; }
    /// <summary>The local player, or 0.</summary>
    nint LocalPlayer { get; }
    /// <summary>The character is still in the object table with this entity id (0 = any).</summary>
    bool IsAlive(nint actor, uint entityId);

    Vector3 GetPosition(nint actor, ActorReadSite site);
    float GetFacing(nint actor, ActorReadSite site);
    uint GetEntityId(nint actor);
    ulong GetGameObjectId(nint actor);
    int GetObjectIndex(nint actor);
    /// <summary>The draw object's scale, false when there is no draw object.</summary>
    bool TryGetDrawScale(nint actor, out Vector3 scale);

    /// <summary>The 26-byte customize block and the model id (appearance identity).</summary>
    void ReadAppearance(nint actor, Span<byte> customize, out int modelCharaId);
    /// <summary>The draw object's model type and race (-1 each when not read): only asked where the ragdoll
    /// code itself reads them, once a skeleton with models was obtained.</summary>
    void ReadModelType(nint actor, ISkeletonPose? skeleton, out int modelType, out int raceSexId);

    float GetAnimationSpeed(nint actor);
    void SetAnimationSpeed(nint actor, float speed);

    /// <summary>Moves the local player's draw object (render only) for the anti-culling follow.</summary>
    void SetDrawPosition(nint actor, Vector3 position);
    /// <summary>Moves an NPC phantom's game object (the movement block hook); false when there is no hook.</summary>
    bool SetApproachPosition(nint actor, Vector3 position);
}

/// <summary>Who else may touch the corpse.</summary>
public interface IColliderActorProvider
{
    bool HasSelector { get; }
    bool HasExtraColliders { get; }
    bool HasMovementBlock { get; }
    /// <summary>The selected NPCs in selector order.</summary>
    void GetSelectedNpcs(List<ColliderCandidate> destination);
    /// <summary>The extra collision actors (party members), evaluated now.</summary>
    IReadOnlyList<nint> GetExtraColliders();
    /// <summary>The main-hand weapon's skeleton root of a striking character; false when it has no weapon draw object.</summary>
    bool TryGetWeaponTransform(nint actor, out Vector3 position, out Quaternion rotation);
}

public interface IRagdollLog
{
    void Info(string message);
    void Warning(string message);
    void Warning(Exception exception, string message);
    void Error(string message);
    void Error(Exception exception, string message);
}

/// <summary>Where a wall-clock reading is used. Only some are recorded (see the recorder).</summary>
public enum ClockSite
{
    /// <summary>The render frame's dt.</summary>
    FrameDelta,
    /// <summary>The time stamp of a walking-support query.</summary>
    NpcTraversalQuery,
    /// <summary>The mesh-frame early out (recorded as a decision when taken).</summary>
    MeshFrameEarlyOut,
    /// <summary>Throttle of installing reduced meshes.</summary>
    MeshLodInstall,
    /// <summary>The live hair's own dt.</summary>
    AnimatedHair,
    /// <summary>Retry pause of a body-surface capture that found no model (Environment.TickCount64, milliseconds).</summary>
    MeshCacheRetry,
}

public interface IRagdollClock
{
    long Frequency { get; }
    long GetTimestamp(ClockSite site);
    /// <summary>Wall time since <paramref name="since"/> (a time stamp of the same clock).</summary>
    TimeSpan GetElapsedTime(ClockSite site, long since);
    /// <summary>Wall time between two time stamps of this clock (no new reading).</summary>
    TimeSpan GetElapsedTime(ClockSite site, long since, long until);
    /// <summary>Milliseconds since the system started (Environment.TickCount64).</summary>
    long GetTickCount64(ClockSite site);
}

public enum RandomSite
{
    ApplyShake,
    DirectedCollapseSideways,
}

public interface IRagdollRandom
{
    double NextDouble(RandomSite site);
    int Next(RandomSite site, int maxExclusive);
}

public interface IRagdollScheduler
{
    Task<T> Run<T>(Func<T> work);
    /// <summary>Waits at most <paramref name="milliseconds"/> for a task: false when it is not done by then.
    /// A faulted task throws as Task.Result does.</summary>
    bool TryWait<T>(Task<T> task, int milliseconds, out T result);
}

public interface IRagdollSettingsSource
{
    /// <summary>Copies the current settings into <paramref name="settings"/>; true when anything changed.
    /// Called once at the start of every render frame (and when the controller is made).</summary>
    bool Refresh(RagdollSettings settings);
}

/// <summary>Why a character's body surface is captured (<see cref="IMeshSource.ProvideBodySurface"/>).</summary>
public enum BodySurfaceUse
{
    /// <summary>The controller's own frozen surface: mesh volumes, hair contacts, and weapon contacts on the main controller.</summary>
    Frozen = 0,
    /// <summary>A surface for weapon contacts only, when the frozen one does not hold that character.</summary>
    WeaponContact = 1,
}

/// <summary>Where a reduced collision surface is taken from a cached body surface.</summary>
public enum CachedLodUse
{
    /// <summary>A mesh body built in InitializePhysics.</summary>
    Init = 0,
    /// <summary>Installed on a mesh body between fixed steps, once its reduced surface is ready.</summary>
    Step = 1,
}

/// <summary>
/// The character models, as the ragdoll needs them (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.3, stage A2b). The
/// ragdoll never reads model files or the game's model memory itself: it asks for finished products (frozen body
/// surfaces, rendered vertices with their bone weights, triangle soups, fitted volumes, hair samples).
/// <para>Departures from the §2.3 sketch, all to keep every read, every float operation and every background task
/// where it was: characters are addressed by their game object address (as in A2a); the body-surface capture keeps
/// the controller's own partitioning code (<see cref="ProvideBodySurface"/> is handed it); and the reduced surfaces
/// of a cached body surface, which a background queue builds, are asked of the mesh source too, so a replay can say
/// when each one was ready.</para>
/// </summary>
internal interface IMeshSource
{
    /// <summary>Opaque identity of the model a character is drawn with: changes when the character is redrawn.</summary>
    nint ModelIdentity(nint actor);

    /// <summary>A character's frozen body surface (bone-local regions), or null when it cannot be read. The game
    /// runs <paramref name="capture"/> (the controller's capture over <see cref="CaptureRenderedGeometry"/>); a replay
    /// hands back the surface the game captured.</summary>
    FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture);

    /// <summary>The visible models of a character as drawn now: world-space vertices with their skeleton bone
    /// weights, triangles, and notes on what was left out. Render or framework thread only.</summary>
    RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton);

    /// <summary>Starts (or stops) building the reduced collision surfaces of a cached body surface in the background.</summary>
    void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance);

    /// <summary>Takes one finished background build of a cached surface; false when none finished.</summary>
    bool PollLod(FrozenMeshGeometryCache cache, out Exception? error);

    /// <summary>The reduced collision surface of a region, rebased onto <paramref name="local"/>, once it is ready.</summary>
    bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod);

    /// <summary>A region's bone-local collision surface: the reduced one once it is ready, otherwise the full one.</summary>
    bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local);

    /// <summary>Starts fitting the target's torso and thigh hulls to the models it is drawn with (the models are
    /// read now, the fit runs on a background task); null when there is nothing to fit.</summary>
    Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton);

    /// <summary>Capsules fitted to the body-mesh vertices that each candidate bone (skeleton index) skins.</summary>
    Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog);

    /// <summary>Another character's skinned models as one triangle soup (model space, unscaled, outward winding);
    /// false when its skin transforms cannot be read.</summary>
    bool TryCollectActorTriangles(nint actor, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes);

    /// <summary>Framework thread, under the controller's hair lock: follows the local player's hair model and loads
    /// its samples in the background; when <see cref="HairMesh"/> changes the live hair rebuilds
    /// (<see cref="TakeHairMeshRebuild"/>).</summary>
    void PollHairMesh();

    /// <summary>The local player's hair samples, as last loaded (null while none).</summary>
    HairMeshBinding? HairMesh { get; }

    /// <summary>True once after <see cref="HairMesh"/> changed: the live hair must rebuild.</summary>
    bool TakeHairMeshRebuild();

    /// <summary>A weapon's collision hulls from the model it is drawn with, or null.</summary>
    WeaponMeshGeometry? BuildWeaponMesh(nint weaponDrawObject, bool verboseLog);
}

/// <summary>A host without models: nothing is read and every product is absent.</summary>
internal sealed class NoMeshSource : IMeshSource
{
    public static readonly NoMeshSource Instance = new();
    public nint ModelIdentity(nint actor) => 0;
    public FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture) => null;
    public RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton) => new();
    public void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance) { }
    public bool PollLod(FrozenMeshGeometryCache cache, out Exception? error) { error = null; return false; }
    public bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod) { lod = null!; return false; }
    public bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local)
        => cache.TryGetSurfaceGeometry(bone, out local);
    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton) => null;
    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog) => new();
    public bool TryCollectActorTriangles(nint actor, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes)
    { loadedModels = processedMeshes = 0; return false; }
    public void PollHairMesh() { }
    public HairMeshBinding? HairMesh => null;
    public bool TakeHairMeshRebuild() => false;
    public WeaponMeshGeometry? BuildWeaponMesh(nint weaponDrawObject, bool verboseLog) => null;
}

/// <summary>The host services of one controller.</summary>
public sealed class RagdollHost
{
    public required ISkeletonSource Skeletons { get; init; }
    public required IWorldQuery World { get; init; }
    public required IRagdollActor Actors { get; init; }
    public required IColliderActorProvider Colliders { get; init; }
    public required IRagdollSettingsSource Settings { get; init; }
    public required IRagdollLog Log { get; init; }
    public IRagdollClock Clock { get; init; } = StopwatchRagdollClock.Instance;
    public IRagdollRandom Random { get; init; } = SharedRagdollRandom.Instance;
    public IRagdollScheduler Scheduler { get; init; } = TaskRagdollScheduler.Instance;
    /// <summary>The character models (stage A2b); none by default.</summary>
    internal IMeshSource Meshes { get; init; } = NoMeshSource.Instance;
}

/// <summary>Default clock: the Stopwatch, as the controller always used.</summary>
public sealed class StopwatchRagdollClock : IRagdollClock
{
    public static readonly StopwatchRagdollClock Instance = new();
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;
    public long GetTimestamp(ClockSite site) => System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(ClockSite site, long since) => System.Diagnostics.Stopwatch.GetElapsedTime(since);
    public TimeSpan GetElapsedTime(ClockSite site, long since, long until) => System.Diagnostics.Stopwatch.GetElapsedTime(since, until);
    public long GetTickCount64(ClockSite site) => Environment.TickCount64;
}

/// <summary>Default random source: one unseeded generator shared by every controller in the process, as before.</summary>
public sealed class SharedRagdollRandom : IRagdollRandom
{
    public static readonly SharedRagdollRandom Instance = new();
    private static readonly System.Random Shared = new();
    public double NextDouble(RandomSite site) => Shared.NextDouble();
    public int Next(RandomSite site, int maxExclusive) => Shared.Next(maxExclusive);
}

/// <summary>Default scheduler: Task.Run and Task.Wait.</summary>
public sealed class TaskRagdollScheduler : IRagdollScheduler
{
    public static readonly TaskRagdollScheduler Instance = new();
    public Task<T> Run<T>(Func<T> work) => Task.Run(work);

    public bool TryWait<T>(Task<T> task, int milliseconds, out T result)
    {
        if (!task.IsCompleted && !task.Wait(milliseconds))
        {
            result = default!;
            return false;
        }
        result = task.Result;
        return true;
    }
}
