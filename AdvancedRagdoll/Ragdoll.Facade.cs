// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using AdvancedRagdoll.CollapseProfiles;
using AdvancedRagdoll.Hosting;
using AdvancedRagdoll.Recording;
using AdvancedRagdoll.SkeletonProfiles;

namespace AdvancedRagdoll;

/// <summary>How and when a ragdoll takes over: the start trigger, the delay (the trigger's wait in
/// <see cref="RagdollTriggerMode.FixedDelay"/>, its fallback in the other modes), and an extra stagger after the moment
/// is reached (to spread out ragdolls that start together).</summary>
public readonly record struct RagdollActivation(RagdollTriggerMode Trigger = RagdollTriggerMode.FixedDelay, float Delay = 0f,
    float Stagger = 0f);

/// <summary>Where a ragdoll is.</summary>
public enum RagdollPhase
{
    /// <summary>Not activated (or deactivated).</summary>
    Inactive,
    /// <summary>Activated; the animation still drives the body until the start trigger fires.</summary>
    Waiting,
    /// <summary>Physics drives the body.</summary>
    Running,
    /// <summary>Physics drives the body, and every body has come to rest.</summary>
    Resting,
}

// The public face of Ragdoll (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.3, stage A7): a constructor on the public host
// API and the Activate / Step / WritePose / Deactivate cycle. It drives the same ragdoll the plugin drives,
// through adapters onto the internal host services (FacadeHost below): an ActorId is mapped to an opaque id, the
// skeleton is read into a buffer once per step and written back from it in WritePose, the frame time is the dt
// passed to Step, and the settings are taken per activation and per step.
public unsafe partial class Ragdoll
{
    // Set only for a ragdoll made through the public constructor.
    private readonly FacadeHost? facade;

    /// <summary>
    /// A ragdoll for one character. <paramref name="profile"/> names the skeleton's bones and carries the hand-tuned
    /// humanoid tables; a skeleton without the profile's signature bones (or any skeleton, with
    /// <see cref="SkeletonProfile.Auto"/>) gets a rig generated from its own bones. Not thread-safe: call every member
    /// from one thread (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §2.2).
    /// </summary>
    public Ragdoll(RagdollHost host, SkeletonProfile profile)
        : this(FacadeHost.Create(host), profile)
    {
    }

    private Ragdoll(FacadeHost facade, SkeletonProfile profile)
        : this(facade.Services, NoCollapseProfiles.Instance, profile)
    {
        this.facade = facade;
    }

    /// <summary>Where the ragdoll is now.</summary>
    public RagdollPhase Phase => !isActive ? RagdollPhase.Inactive
        : !physicsStarted ? RagdollPhase.Waiting
        : prevAllAsleep ? RagdollPhase.Resting
        : RagdollPhase.Running;

    /// <summary>Arms the ragdoll: from the next <see cref="Step"/> on it watches for the start trigger, and then
    /// builds the physics from the current pose (carrying the animation's velocity over). Takes the settings
    /// (<see cref="IRagdollSettingsSource.Capture"/>). An active ragdoll is deactivated first.</summary>
    public void Activate(in RagdollActivation activation)
    {
        var host = RequireFacade();
        // A ragdoll that stopped on its own (the character vanished) never gave the body back, so the facade still
        // thinks physics runs; forget that, or the next start would not raise OnPhysicsStarted. An active ragdoll keeps
        // it: the internal Activate deactivates it first and raises OnDeactivated.
        if (!isActive) host.ResetPhysicsStarted();
        host.CaptureSettings();
        // The new settings apply to the activation itself (it arms the knee buckle), not only from the next step on.
        settingsSource.Refresh(settings);
        Activate(host.Target, activation.Delay, activation.Trigger, activation.Stagger);
    }

    /// <summary>
    /// Advances the ragdoll by <paramref name="dt"/> seconds of game time (clamped to 0.1 s; the first step after
    /// <see cref="Activate"/> counts as 1/60 s): while waiting, samples the animated pose and checks the start
    /// trigger; once physics runs, steps it in fixed 1/60 s steps and computes the pose, which
    /// <see cref="WritePose"/> then hands to the skeleton.
    /// </summary>
    public RagdollPhase Step(float dt)
    {
        var host = RequireFacade();
        host.BeginStep(dt);
        OnRenderFrame();
        // The ragdoll deactivates itself when the character is gone, leaving the (vanished) body alone.
        if (!isActive) host.ResetPhysicsStarted();
        if (isActive && physicsStarted && ragdollBones.Count > 0 && GetBodyWorldPosition(ragdollBones[0].Name) is { } root)
            host.Actor.OnPhysicsFrame(root);
        return Phase;
    }

    /// <summary>Writes the pose computed by the last <see cref="Step"/> into the skeleton (only the bones that
    /// changed). False, and nothing written, until physics runs.</summary>
    public bool WritePose()
    {
        var host = RequireFacade();
        if (!isActive || !physicsStarted) return false;
        host.Skeleton.Flush();
        return true;
    }

    private FacadeHost RequireFacade()
        => facade ?? throw new InvalidOperationException("This ragdoll was not made with the public constructor.");
}

/// <summary>A host without guided-collapse profiles.</summary>
internal sealed class NoCollapseProfiles : ICollapseProfileSource
{
    public static readonly NoCollapseProfiles Instance = new();
    public CollapseProfile? FindById(string id) => null;
}

/// <summary>
/// The public host API (<see cref="RagdollHost"/>) adapted onto the internal host services the ragdoll core is written
/// against. The ragdoll's own character gets the opaque id 1; nobody else exists for it (no other colliders).
/// </summary>
internal sealed class FacadeHost
{
    public readonly RagdollHostServices Services;
    public readonly FacadeSkeleton Skeleton;
    public readonly IRagdollActor Actor;
    private readonly FacadeClock clock;
    private readonly FacadeSettings settings;
    private readonly FacadeActors actors;

    /// <summary>The internal id of the ragdoll's character.</summary>
    public readonly nint Target = 1;

    private FacadeHost(RagdollHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(host.Skeleton);
        ArgumentNullException.ThrowIfNull(host.World);
        ArgumentNullException.ThrowIfNull(host.Actor);
        Actor = host.Actor;
        Skeleton = new FacadeSkeleton(Target, host.Skeleton, host.Actor);
        clock = new FacadeClock();
        settings = new FacadeSettings(host.Settings ?? FixedRagdollSettings.Default);
        Services = new RagdollHostServices
        {
            Skeletons = new FacadeSkeletons(Target, Skeleton),
            World = new FacadeWorld(host.World),
            Actors = actors = new FacadeActors(Target, Skeleton, host.Actor, settings),
            Colliders = NoColliders.Instance,
            Settings = settings,
            Log = host.Log ?? NullRagdollLog.Instance,
            Clock = clock,
            Build = new RagdollBuildInfo(RestoresFollowPosition: false, PublicBuild: true,
                HostVersion: typeof(FacadeHost).Assembly.GetName().Version?.ToString()),
        };
    }

    public static FacadeHost Create(RagdollHost host) => new(host);

    public void CaptureSettings() => settings.Capture();

    /// <summary>Forgets that physics had started, without telling the actor (call only while the ragdoll is inactive).</summary>
    public void ResetPhysicsStarted() => actors.ResetFrozen();

    public void BeginStep(float dt)
    {
        clock.Advance(float.IsFinite(dt) && dt > 0f ? dt : 0f);
        Skeleton.Invalidate();
    }

    /// <summary>The frame time is the dt the host passes to Step; every other reading is the Stopwatch.</summary>
    private sealed class FacadeClock : IRagdollClock
    {
        private long frameTicks = 1;
        public long Frequency => Stopwatch.Frequency;
        public void Advance(float dt) => frameTicks += (long)Math.Round(dt * (double)Stopwatch.Frequency);
        public long GetTimestamp(ClockSite site) => site == ClockSite.FrameDelta ? frameTicks : Stopwatch.GetTimestamp();
        public TimeSpan GetElapsedTime(ClockSite site, long since) => Stopwatch.GetElapsedTime(since);
        public TimeSpan GetElapsedTime(ClockSite site, long since, long until) => Stopwatch.GetElapsedTime(since, until);
        public long GetTickCount64(ClockSite site) => Environment.TickCount64;
    }

    private sealed class FacadeSkeletons : IHostSkeletonSource
    {
        private readonly nint target;
        private readonly FacadeSkeleton skeleton;
        public FacadeSkeletons(nint target, FacadeSkeleton skeleton) { this.target = target; this.skeleton = skeleton; }

        public IHostSkeleton? TryGetSkeleton(nint actor, PosePurpose site = PosePurpose.Other)
            => actor == target && skeleton.Refresh() ? skeleton : null;

        public Vector3? GetBoneWorldPosition(nint actor, string boneName)
        {
            if (actor != target || !skeleton.Refresh()) return null;
            var bone = Ragdoll.ResolveBoneIndex(skeleton, boneName);
            if (bone < 0) return null;
            var root = skeleton.Root;
            var p = skeleton.ModelPose[bone].Translation;
            return root.Position + Vector3.Transform(new Vector3(p.X, p.Y, p.Z) * root.Scale, root.Rotation);
        }
    }

    private sealed class FacadeWorld : IHostWorldQuery
    {
        private readonly IWorldQuery world;
        public FacadeWorld(IWorldQuery world) => this.world = world;
        public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
            => world.Raycast(origin, direction, maxDistance, out hit);
        public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
            => world.TryCollectGroundTriangles(min, max, triangles, maxTriangles);
        public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
            => world.TryFindWaterSurface(position, searchAbove, searchBelow, out surfaceY);
    }

    private sealed class FacadeActors : IHostActors
    {
        private readonly nint target;
        private readonly FacadeSkeleton skeleton;
        private readonly IRagdollActor actor;
        private readonly FacadeSettings settings;
        private bool frozen;

        public FacadeActors(nint target, FacadeSkeleton skeleton, IRagdollActor actor, FacadeSettings settings)
        {
            this.target = target;
            this.skeleton = skeleton;
            this.actor = actor;
            this.settings = settings;
        }

        public void ResetFrozen() => frozen = false;

        public bool GameRunning => true;
        public nint LocalPlayer => 0;
        public bool IsAlive(nint id, uint entityId) => id == target && actor.Exists;
        public Vector3 GetPosition(nint id, ActorSite site) => id == target ? skeleton.Host.GetRoot().Position : Vector3.Zero;
        public float GetFacing(nint id, ActorSite site) => actor.FacingYaw;
        // The ragdoll compares the id it saw at activation with the one it sees each frame: 0 once the character is gone.
        public uint GetEntityId(nint id) => id == target && actor.Exists ? 1u : 0u;
        public ulong GetGameObjectId(nint id) => id == target ? actor.Id.Value : 0;
        // Object index 0 is the primary character (the full solver budget and the exact ground).
        public int GetObjectIndex(nint id) => id == target && actor.Fidelity == RagdollFidelity.Primary ? 0 : 1;
        public bool TryGetDrawScale(nint id, out Vector3 scale) { scale = Vector3.One; return false; }
        public void ReadAppearance(nint id, Span<byte> customize, out int modelCharaId) { customize.Clear(); modelCharaId = 0; }
        public void ReadModelType(nint id, IHostSkeleton? skel, out int modelType, out int raceSexId) { modelType = -1; raceSexId = -1; }
        public float GetAnimationSpeed(nint id) => 1f;

        // The ragdoll stops the animation when physics takes over (speed 0, re-asserted every frame) and restores it
        // when it gives the body back.
        public void SetAnimationSpeed(nint id, float speed)
        {
            if (id != target) return;
            if (speed == 0f)
            {
                if (frozen) return;
                frozen = true;
                actor.OnPhysicsStarted();
            }
            else if (frozen)
            {
                frozen = false;
                actor.OnDeactivated();
            }
        }

        public void SetDrawPosition(nint id, Vector3 position) { }
        public bool SetApproachPosition(nint id, Vector3 position) => false;
    }

    private sealed class NoColliders : IHostColliders
    {
        public static readonly NoColliders Instance = new();
        public bool HasSelector => false;
        public bool HasExtraColliders => false;
        public bool HasMovementBlock => false;
        public void GetSelectedNpcs(List<ColliderCandidate> destination) => destination.Clear();
        public IReadOnlyList<nint> GetExtraColliders() => Array.Empty<nint>();
        public bool TryGetWeaponTransform(nint actor, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.Identity;
            return false;
        }
    }

    /// <summary>The public settings mapped onto the ragdoll's settings copy; everything the public API does not
    /// offer stays at the value that keeps it off.</summary>
    private sealed class FacadeSettings : IHostSettingsSource
    {
        private readonly IRagdollSettingsSource source;
        private RagdollSettings captured = RagdollSettings.Default;

        public FacadeSettings(IRagdollSettingsSource source) => this.source = source;

        public void Capture() => captured = source.Capture() ?? RagdollSettings.Default;

        public bool Refresh(HostSettings s)
        {
            var live = source.GetLive();
            s.RagdollAnatomicalRom = captured.AnatomicalRangeOfMotion;
            s.GuidedCollapse.Enabled = captured.KneeBuckle;
            s.RagdollExactGroundCollision = captured.ExactGround;
            // Secondary ragdolls get the finer of the two smaller budgets.
            s.NpcRagdollFineDetail = true;
            s.RagdollWaterBuoyancy = live.Water != RagdollWaterMode.Off;
            s.RagdollWaterLiquid = live.Water == RagdollWaterMode.DenseLiquid ? 1 : 0;
            s.RagdollVerboseLog = live.VerboseLog;
            s.RagdollMeshContactQuality = 50;
            s.RagdollMeshLodToleranceMm = 5.5f;
            s.RagdollSquashIntensity = 0.5f;
            s.RagdollNpcCollisionScale = 1f;
            s.RagdollActivationDelay = 0f;
            return true;
        }
    }
}

/// <summary>
/// The host's skeleton seen as the internal skeleton: every part is read into a buffer once per step (at the first
/// read of the step), the ragdoll reads and writes the buffer in place, and <see cref="Flush"/> writes the bones that
/// changed back to the host.
/// </summary>
internal sealed class FacadeSkeleton : IHostSkeleton
{
    private sealed class Part
    {
        public SkeletonPart Topology = null!;
        public string?[] Names = Array.Empty<string?>();
        public BoneQs[] Reference = Array.Empty<BoneQs>();
        public BoneQs[] Pose = Array.Empty<BoneQs>();
        public BoneQs[] Read = Array.Empty<BoneQs>();
        public bool HasPose;
        public nint Identity;
    }

    public readonly ISkeletonPose Host;
    private readonly IRagdollActor actor;
    private readonly List<Part> parts = new();
    private BoneTransform[] scratch = Array.Empty<BoneTransform>();
    private bool[] changed = Array.Empty<bool>();
    private int topologyVersion = int.MinValue;
    private int topologyStamp;
    private bool current;
    private RootTransform root;

    public FacadeSkeleton(nint actorId, ISkeletonPose host, IRagdollActor actor)
    {
        Actor = actorId;
        Host = host;
        this.actor = actor;
    }

    /// <summary>The next read takes a fresh copy from the host.</summary>
    public void Invalidate() => current = false;

    /// <summary>Reads the host skeleton if this step has not yet; false when it cannot be read.</summary>
    public bool Refresh()
    {
        if (current) return true;
        if (!Host.IsValid || Host.PartCount < 1) return false;
        if (Host.TopologyVersion != topologyVersion || parts.Count != Host.PartCount) LoadTopology();
        root = Host.GetRoot();
        for (var p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var n = part.Names.Length;
            if (scratch.Length < n) scratch = new BoneTransform[n];
            part.HasPose = Host.TryReadModelPose(p, scratch.AsSpan(0, n));
            if (!part.HasPose)
            {
                if (p == 0) return false;
                continue;
            }
            for (var i = 0; i < n; i++) part.Pose[i] = ToQs(scratch[i]);
            Array.Copy(part.Pose, part.Read, n);
        }
        current = true;
        return true;
    }

    private void LoadTopology()
    {
        topologyVersion = Host.TopologyVersion;
        topologyStamp++;
        parts.Clear();
        for (var p = 0; p < Host.PartCount; p++)
        {
            var topology = Host.GetPart(p);
            var n = topology.BoneNames.Count;
            if (topology.ParentIndices.Length != n)
                throw new InvalidOperationException($"Skeleton part {p}: {n} bone names but {topology.ParentIndices.Length} parents.");
            var part = new Part
            {
                Topology = topology,
                Names = new string?[n],
                Pose = new BoneQs[n],
                Read = new BoneQs[n],
                Reference = new BoneQs[topology.ReferencePose.Length],
                // Distinct per part and per topology, never 0.
                Identity = (nint)(((long)topologyStamp << 8) | (uint)(p + 1)),
            };
            for (var i = 0; i < n; i++) part.Names[i] = topology.BoneNames[i];
            for (var i = 0; i < part.Reference.Length; i++) part.Reference[i] = ToQs(topology.ReferencePose[i]);
            parts.Add(part);
        }
    }

    /// <summary>Writes the bones the ragdoll changed since the last read or flush back to the host.</summary>
    public void Flush()
    {
        if (!current) return;
        for (var p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            if (!part.HasPose) continue;
            var n = part.Pose.Length;
            if (scratch.Length < n) scratch = new BoneTransform[n];
            if (changed.Length < n) changed = new bool[n];
            var any = false;
            for (var i = 0; i < n; i++)
            {
                var c = !SameBits(part.Pose[i], part.Read[i]);
                changed[i] = c;
                any |= c;
                scratch[i] = FromQs(part.Pose[i]);
            }
            if (!any) continue;
            Host.WriteModelPose(p, scratch.AsSpan(0, n), changed.AsSpan(0, n));
            Array.Copy(part.Pose, part.Read, n);
        }
    }

    private static BoneQs ToQs(in BoneTransform t) => new()
    {
        Translation = new Vector4(t.Position, 0f),
        Rotation = t.Rotation,
        Scale = new Vector4(t.Scale, 0f),
    };

    private static BoneTransform FromQs(in BoneQs q) => new(
        new Vector3(q.Translation.X, q.Translation.Y, q.Translation.Z), q.Rotation, new Vector3(q.Scale.X, q.Scale.Y, q.Scale.Z));

    private static bool SameBits(in BoneQs a, in BoneQs b)
        => Same(a.Translation.X, b.Translation.X) && Same(a.Translation.Y, b.Translation.Y) && Same(a.Translation.Z, b.Translation.Z)
           && Same(a.Rotation.X, b.Rotation.X) && Same(a.Rotation.Y, b.Rotation.Y) && Same(a.Rotation.Z, b.Rotation.Z)
           && Same(a.Rotation.W, b.Rotation.W) && Same(a.Scale.X, b.Scale.X) && Same(a.Scale.Y, b.Scale.Y) && Same(a.Scale.Z, b.Scale.Z);

    private static bool Same(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);

    private Part Body => parts[0];

    public nint Actor { get; }
    public nint DrawIdentity => (nint)(long)topologyStamp;
    public int BoneCount => Body.Pose.Length;
    public int ParentCount => Body.Topology.ParentIndices.Length;
    public Span<BoneQs> ModelPose => Body.Pose;
    public short Parent(int bone) => Body.Topology.ParentIndices[bone];
    public int BoneNameCount => Body.Names.Length;
    public string? BoneName(int bone) => Body.Names[bone];
    public ReadOnlySpan<BoneQs> ReferencePose => Body.Reference;
    public bool HasRoot => true;
    public RootTransform Root => root;

    public bool TryGetDominantAnimation(out RagdollAnimationSample sample)
    {
        if (actor.TryGetAnimationSample(out var s))
        {
            sample = new RagdollAnimationSample((nint)(long)s.Id, s.Time, s.Duration);
            return true;
        }
        sample = default;
        return false;
    }

    public int PartCount => parts.Count;
    public bool PartAvailable(int part) => part < parts.Count && parts[part].HasPose;
    public bool PartHasPose(int part) => part < parts.Count && parts[part].HasPose;
    public bool PartInSync(int part) => true;
    public nint PartIdentity(int part) => parts[part].Identity;
    public nint PartPoseIdentity(int part) => parts[part].Identity + 0x40;
    public Span<BoneQs> PartModelPose(int part) => parts[part].HasPose ? parts[part].Pose : Span<BoneQs>.Empty;
    public int PartModelPoseLength(int part) => parts[part].HasPose ? parts[part].Pose.Length : 0;
    public int PartBoneNameCount(int part) => parts[part].Names.Length;
    public string? PartBoneName(int part, int bone) => parts[part].Names[bone];
    public int PartParentCount(int part) => parts[part].Topology.ParentIndices.Length;
    public short PartParent(int part, int bone) => parts[part].Topology.ParentIndices[bone];
    public ReadOnlySpan<BoneQs> PartReferencePose(int part) => parts[part].Reference;
    public int PartReferenceCount(int part) => parts[part].Reference.Length;
    public int PartConnectedBone(int part) => part == 0 ? -1 : parts[part].Topology.AttachingBone;
    public int PartConnectedParentBone(int part) => part == 0 ? -1 : parts[part].Topology.AttachedToBone;
    // The next step reads the host's pose again anyway.
    public void ResyncPartModelPose(int part) { }
}
