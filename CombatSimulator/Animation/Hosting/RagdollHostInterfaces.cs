// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CombatSimulator.Animation.Recording;

namespace CombatSimulator.Animation.Hosting;

// What the ragdoll asks of its host (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.3, stage A2a).
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
}

public interface IRagdollClock
{
    long Frequency { get; }
    long GetTimestamp(ClockSite site);
    /// <summary>Wall time since <paramref name="since"/> (a time stamp of the same clock).</summary>
    TimeSpan GetElapsedTime(ClockSite site, long since);
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

/// <summary>
/// Stage A2a stand-in for the products the controller's own model-reading code makes (A2b moves that code
/// out and replaces this with IMeshSource). Null on the game: the controller then reads the models itself.
/// </summary>
internal interface IRagdollMeshProducts
{
    /// <summary>The hull fit for the target, as the fit task would deliver it; null when none was started.</summary>
    Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor);

    /// <summary>The soft-tissue bodies fitted to the rendered mesh, by skeleton bone index, for the given candidates.</summary>
    Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, HashSet<int> candidates);
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
    internal IRagdollMeshProducts? MeshProducts { get; init; }
}

/// <summary>Default clock: the Stopwatch, as the controller always used.</summary>
public sealed class StopwatchRagdollClock : IRagdollClock
{
    public static readonly StopwatchRagdollClock Instance = new();
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;
    public long GetTimestamp(ClockSite site) => System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(ClockSite site, long since) => System.Diagnostics.Stopwatch.GetElapsedTime(since);
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
