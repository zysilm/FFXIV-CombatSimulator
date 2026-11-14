// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AdvancedRagdoll.Hosting;

// The public host API (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.3, stage A7): what a game gives a Ragdoll. Conventions:
// world Y up, metres, seconds, radians, System.Numerics; poses are in model space (relative to the skeleton root),
// the root transform maps model space to world space. Characters are identified by an opaque ActorId. Nothing here
// exposes a BepuPhysics type (the advanced extension points live in AdvancedRagdoll.Bepu).

/// <summary>An opaque identity of a character. The library never interprets the value.</summary>
public readonly record struct ActorId(ulong Value);

/// <summary>One bone's transform in model space (relative to the skeleton root), or parent-local in a reference
/// pose.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BoneTransform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public BoneTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>No translation, no rotation, unit scale.</summary>
    public static BoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// The topology of one part of a skeleton. Part 0 is the body; further parts (a face, hair, ...) hang from a body
/// bone. A part's topology must not change while its <see cref="ISkeletonPose.TopologyVersion"/> stays the same.
/// </summary>
public sealed class SkeletonPart
{
    /// <summary>Bone names, one per bone. The ragdoll finds the bones a profile names by these.</summary>
    public required IReadOnlyList<string> BoneNames { get; init; }
    /// <summary>Parent index of every bone; -1 for a root. A parent comes before its children.</summary>
    public required short[] ParentIndices { get; init; }
    /// <summary>The reference (bind) pose, parent-local; may be empty when the skeleton has none.</summary>
    public BoneTransform[] ReferencePose { get; init; } = Array.Empty<BoneTransform>();
    /// <summary>For an attached part: the body bone it hangs from; -1 for the body itself.</summary>
    public int AttachedToBone { get; init; } = -1;
    /// <summary>For an attached part: its own bone that coincides with <see cref="AttachedToBone"/>.</summary>
    public int AttachingBone { get; init; }
}

/// <summary>
/// A character's skeleton as the ragdoll reads and writes it, in batches: one read and at most one write per part
/// and frame. <see cref="Ragdoll.Step"/> reads (the animated pose before physics starts, the pose the ragdoll last
/// wrote after), <see cref="Ragdoll.WritePose"/> writes.
/// </summary>
public interface ISkeletonPose
{
    /// <summary>False while the skeleton cannot be read (the ragdoll then skips the frame).</summary>
    bool IsValid { get; }
    /// <summary>Changes whenever any part's topology (bones, names, parents) changes.</summary>
    int TopologyVersion { get; }
    /// <summary>Number of parts, the body included (at least 1).</summary>
    int PartCount { get; }
    /// <summary>The topology of a part.</summary>
    SkeletonPart GetPart(int part);
    /// <summary>The skeleton root: model space to world space.</summary>
    RootTransform GetRoot();
    /// <summary>Copies a part's current model-space pose into <paramref name="destination"/> (one entry per bone of
    /// the part); false when the part cannot be read now.</summary>
    bool TryReadModelPose(int part, Span<BoneTransform> destination);
    /// <summary>Writes a part's model-space pose; only the bones whose <paramref name="changed"/> entry is true
    /// carry new values.</summary>
    void WriteModelPose(int part, ReadOnlySpan<BoneTransform> pose, ReadOnlySpan<bool> changed);
}

/// <summary>Questions about the static world (the ground the ragdoll lands on).</summary>
public interface IWorldQuery
{
    /// <summary>Casts a ray against the static world; false when nothing is hit within
    /// <paramref name="maxDistance"/>. <see cref="RayHit.V1"/>..<see cref="RayHit.V3"/> may be left zero.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit);
    /// <summary>The ground triangles (three vertices each, world space) in a box; false when the world cannot hand
    /// them out (the ragdoll then samples the ground with rays).</summary>
    bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles);
    /// <summary>The still water surface around a point, if there is water.</summary>
    bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY);
}

/// <summary>How much simulation a ragdoll gets.</summary>
public enum RagdollFidelity
{
    /// <summary>The character the camera follows: the full solver budget and, when allowed, the exact ground.</summary>
    Primary,
    /// <summary>Everyone else: a smaller solver budget.</summary>
    Secondary,
}

/// <summary>The animation with the most weight on the body: an opaque id (the same while the same animation plays),
/// its time and its length, in seconds. Used by the start triggers that look at the death animation.</summary>
public readonly record struct AnimationSample(ulong Id, float Time, float Duration);

/// <summary>The character a ragdoll belongs to.</summary>
public interface IRagdollActor
{
    ActorId Id { get; }
    /// <summary>True while this is still the same character. When it turns false (the character despawned, or the
    /// host reused its slot) the ragdoll deactivates itself and leaves the skeleton alone (no <see cref="OnDeactivated"/>:
    /// there is no body to give back). It can be activated again later.</summary>
    bool Exists { get; }
    RagdollFidelity Fidelity { get; }
    /// <summary>The character's facing: radians around world Y, 0 = the profile's forward axis.</summary>
    float FacingYaw { get; }
    /// <summary>The dominant animation now; false when there is none or the host does not know.</summary>
    bool TryGetAnimationSample(out AnimationSample sample);
    /// <summary>Physics took the body over: stop the animation, or it overwrites the pose the ragdoll writes.</summary>
    void OnPhysicsStarted();
    /// <summary>After every <see cref="Ragdoll.Step"/> while physics runs: the world position of the ragdoll's root
    /// body (to keep a culling volume or a camera on a thrown body).</summary>
    void OnPhysicsFrame(Vector3 rootBodyPosition);
    /// <summary>The ragdoll gave the body back after <see cref="OnPhysicsStarted"/>: the animation may resume.</summary>
    void OnDeactivated();
}

/// <summary>How the ragdoll treats water.</summary>
public enum RagdollWaterMode
{
    /// <summary>Water is ignored.</summary>
    Off,
    /// <summary>A body floats in water.</summary>
    Water,
    /// <summary>A denser liquid: the body floats higher.</summary>
    DenseLiquid,
}

/// <summary>
/// The settings a ragdoll takes once per activation (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §6): taken from
/// <see cref="IRagdollSettingsSource.Capture"/> when <see cref="Ragdoll.Activate"/> is called and unchanged until the
/// next activation.
/// </summary>
public sealed record RagdollSettings
{
    /// <summary>Anatomical ranges of motion from the profile instead of the per-bone limits.</summary>
    public bool AnatomicalRangeOfMotion { get; init; }
    /// <summary>The knees buckle when physics starts (needs a profile that names the legs).</summary>
    public bool KneeBuckle { get; init; }
    /// <summary>A <see cref="RagdollFidelity.Primary"/> ragdoll lies on the world's ground triangles
    /// (<see cref="IWorldQuery.TryCollectGroundTriangles"/>) where the world hands them out.</summary>
    public bool ExactGround { get; init; } = true;

    /// <summary>The defaults.</summary>
    public static RagdollSettings Default { get; } = new();
}

/// <summary>The settings a ragdoll reads at the start of every <see cref="Ragdoll.Step"/>; the whole step (the
/// physics worker threads included) uses that copy.</summary>
public readonly record struct RagdollLiveSettings
{
    public RagdollLiveSettings() { }

    public RagdollWaterMode Water { get; init; } = RagdollWaterMode.Water;
    /// <summary>Detailed log lines (through <see cref="IRagdollLog.Info"/>).</summary>
    public bool VerboseLog { get; init; }

    /// <summary>The defaults.</summary>
    public static RagdollLiveSettings Default => new();
}

/// <summary>Where a ragdoll gets its settings.</summary>
public interface IRagdollSettingsSource
{
    /// <summary>Called once per activation.</summary>
    RagdollSettings Capture();
    /// <summary>Called at the start of every step.</summary>
    RagdollLiveSettings GetLive();
}

/// <summary>Fixed settings.</summary>
public sealed class FixedRagdollSettings : IRagdollSettingsSource
{
    public static FixedRagdollSettings Default { get; } = new(RagdollSettings.Default, RagdollLiveSettings.Default);

    private readonly RagdollSettings settings;
    private readonly RagdollLiveSettings live;

    public FixedRagdollSettings(RagdollSettings settings, RagdollLiveSettings live)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.live = live;
    }

    public RagdollSettings Capture() => settings;
    public RagdollLiveSettings GetLive() => live;
}

/// <summary>A log that drops everything.</summary>
public sealed class NullRagdollLog : IRagdollLog
{
    public static NullRagdollLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Warning(Exception exception, string message) { }
    public void Error(string message) { }
    public void Error(Exception exception, string message) { }
}

/// <summary>What a ragdoll needs from its host. <see cref="Skeleton"/>, <see cref="World"/> and <see cref="Actor"/>
/// are required.</summary>
public sealed class RagdollHost
{
    public required ISkeletonPose Skeleton { get; init; }
    public required IWorldQuery World { get; init; }
    public required IRagdollActor Actor { get; init; }
    public IRagdollSettingsSource Settings { get; init; } = FixedRagdollSettings.Default;
    public IRagdollLog Log { get; init; } = NullRagdollLog.Instance;
}

/// <summary>
/// The simplest <see cref="ISkeletonPose"/>: one part, held in arrays. The host sets the root and the animated pose
/// before every step (<see cref="SetRoot"/>, <see cref="SetModelPose"/>) and reads what the ragdoll wrote from
/// <see cref="ModelPose"/> after <see cref="Ragdoll.WritePose"/>.
/// </summary>
public sealed class ArraySkeleton : ISkeletonPose
{
    private readonly SkeletonPart part;
    private readonly BoneTransform[] pose;
    private RootTransform root = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <param name="boneNames">One name per bone.</param>
    /// <param name="parentIndices">Parent of every bone, -1 for the root; parents come first.</param>
    /// <param name="referencePose">The parent-local reference pose; also the starting model pose is derived from it
    /// when <paramref name="modelPose"/> is null.</param>
    /// <param name="modelPose">The starting model-space pose (one per bone), or null.</param>
    public ArraySkeleton(IReadOnlyList<string> boneNames, short[] parentIndices, BoneTransform[] referencePose,
        BoneTransform[]? modelPose = null)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        ArgumentNullException.ThrowIfNull(parentIndices);
        ArgumentNullException.ThrowIfNull(referencePose);
        if (parentIndices.Length != boneNames.Count || referencePose.Length != boneNames.Count)
            throw new ArgumentException("One name, parent and reference transform per bone.");
        for (var i = 0; i < parentIndices.Length; i++)
            if (parentIndices[i] >= i)
                throw new ArgumentException($"Bone {i}: its parent must come before it.", nameof(parentIndices));
        part = new SkeletonPart { BoneNames = boneNames, ParentIndices = parentIndices, ReferencePose = referencePose };
        pose = modelPose != null ? (BoneTransform[])modelPose.Clone() : ToModelSpace(parentIndices, referencePose);
        if (pose.Length != boneNames.Count) throw new ArgumentException("One model transform per bone.", nameof(modelPose));
    }

    /// <summary>The current model-space pose (what the ragdoll last wrote, once physics runs).</summary>
    public ReadOnlySpan<BoneTransform> ModelPose => pose;
    public int BoneCount => pose.Length;
    public IReadOnlyList<string> BoneNames => part.BoneNames;

    public void SetRoot(Vector3 position, Quaternion rotation, Vector3 scale) => root = new RootTransform(position, rotation, scale);
    public void SetModelPose(ReadOnlySpan<BoneTransform> modelPose)
    {
        if (modelPose.Length != pose.Length) throw new ArgumentException("One transform per bone.", nameof(modelPose));
        modelPose.CopyTo(pose);
    }

    /// <summary>The world-space position of a bone in the current pose.</summary>
    public Vector3 GetWorldPosition(int bone) =>
        root.Position + Vector3.Transform(pose[bone].Position * root.Scale, root.Rotation);

    bool ISkeletonPose.IsValid => true;
    int ISkeletonPose.TopologyVersion => 1;
    int ISkeletonPose.PartCount => 1;
    SkeletonPart ISkeletonPose.GetPart(int index) => index == 0 ? part : throw new ArgumentOutOfRangeException(nameof(index));
    RootTransform ISkeletonPose.GetRoot() => root;

    bool ISkeletonPose.TryReadModelPose(int index, Span<BoneTransform> destination)
    {
        if (index != 0 || destination.Length < pose.Length) return false;
        pose.CopyTo(destination);
        return true;
    }

    void ISkeletonPose.WriteModelPose(int index, ReadOnlySpan<BoneTransform> written, ReadOnlySpan<bool> changed)
    {
        if (index != 0) return;
        var n = Math.Min(pose.Length, Math.Min(written.Length, changed.Length));
        for (var i = 0; i < n; i++)
            if (changed[i]) pose[i] = written[i];
    }

    /// <summary>Composes a parent-local pose into model space.</summary>
    public static BoneTransform[] ToModelSpace(short[] parentIndices, BoneTransform[] local)
    {
        var model = new BoneTransform[local.Length];
        for (var i = 0; i < local.Length; i++)
        {
            var p = parentIndices[i];
            if (p < 0) { model[i] = local[i]; continue; }
            var parent = model[p];
            model[i] = new BoneTransform(
                parent.Position + Vector3.Transform(local[i].Position * parent.Scale, parent.Rotation),
                Quaternion.Normalize(parent.Rotation * local[i].Rotation),
                parent.Scale * local[i].Scale);
        }
        return model;
    }
}

/// <summary>An <see cref="IWorldQuery"/> made of delegates: only the ray is required; ground triangles and water
/// default to "not available" and "no water".</summary>
public sealed class DelegateWorldQuery : IWorldQuery
{
    public delegate bool RaycastFunc(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit);
    public delegate bool WaterFunc(Vector3 position, float searchAbove, float searchBelow, out float surfaceY);

    private readonly RaycastFunc raycast;
    private readonly Func<Vector3, Vector3, List<Vector3>, int, bool>? groundTriangles;
    private readonly WaterFunc? water;

    public DelegateWorldQuery(RaycastFunc raycast, Func<Vector3, Vector3, List<Vector3>, int, bool>? groundTriangles = null,
        WaterFunc? water = null)
    {
        this.raycast = raycast ?? throw new ArgumentNullException(nameof(raycast));
        this.groundTriangles = groundTriangles;
        this.water = water;
    }

    /// <summary>A flat, endless ground plane at height <paramref name="y"/>.</summary>
    public static DelegateWorldQuery FlatGround(float y)
    {
        return new DelegateWorldQuery((Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) =>
        {
            hit = default;
            if (MathF.Abs(direction.Y) < 1e-6f) return false;
            var t = (y - origin.Y) / direction.Y;
            if (t < 0f || t > maxDistance) return false;
            hit.Point = origin + direction * t;
            hit.Normal = Vector3.UnitY;
            hit.Distance = t;
            return true;
        });
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) => raycast(origin, direction, maxDistance, out hit);

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
        => groundTriangles != null && groundTriangles(min, max, triangles, maxTriangles);

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        surfaceY = 0f;
        return water != null && water(position, searchAbove, searchBelow, out surfaceY);
    }
}
