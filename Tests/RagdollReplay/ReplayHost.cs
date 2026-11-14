// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSimulator;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.Recording;

namespace RagdollReplay;

// The host services of a replayed controller. Every answer comes from the recording: each query takes the
// record its own call produced in the game, checks that it was asked the same thing, and answers what the game
// answered. A query the recording cannot answer, or a question asked differently, is noted as a divergence.

internal static class Rec
{
    public static Vector3[] Vectors(RecValue value) => MemoryMarshal.Cast<byte, Vector3>(value.Data).ToArray();

    public static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    public static bool SameBits(Vector3 a, Vector3 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);

    public static string F(Vector3 v) => $"({v.X:R}, {v.Y:R}, {v.Z:R})";
}

/// <summary>One character's skeleton as the recording last showed it, plus whatever the controller wrote into it since.</summary>
internal sealed class ReplaySkeleton : ISkeletonPose
{
    public sealed class Part
    {
        public BoneQs[]? Pose;
        public bool InSync = true;
        public bool HasTopology;
        public string?[] Names = Array.Empty<string?>();
        public short[] Parents = Array.Empty<short>();
        public BoneQs[] Reference = Array.Empty<BoneQs>();
        public int Connected = -1, ConnectedParent = -1;
        public nint TopologyId;
    }

    public readonly List<Part> Parts = new();
    public bool Available;
    public RootTransform RootValue;
    public bool DrawScaleValid;
    public Vector3 DrawScale = Vector3.One;
    public int Count = 1;
    private readonly ReplaySource source;

    public ReplaySkeleton(nint actor, ReplaySource source)
    {
        Actor = actor;
        this.source = source;
    }

    public Part At(int part)
    {
        while (Parts.Count <= part) Parts.Add(new Part());
        return Parts[part];
    }

    public nint Actor { get; }
    public nint DrawIdentity => Actor;
    public int BoneCount => At(0).Pose?.Length ?? 0;
    public int ParentCount => At(0).Parents.Length;
    public Span<BoneQs> ModelPose => At(0).Pose;
    public short Parent(int bone) => At(0).Parents[bone];
    public int BoneNameCount => At(0).Names.Length;
    public string? BoneName(int bone) => At(0).Names[bone];
    public ReadOnlySpan<BoneQs> ReferencePose => At(0).Reference;
    public bool HasRoot => true;
    public RootTransform Root => RootValue;

    public bool TryGetDominantAnimation(out RagdollAnimationSample sample)
    {
        sample = default;
        var inputs = source.Take(RecordType.TriggerInputs, inFrameOnly: true);
        if (inputs == null)
        {
            source.Diverged("the dominant animation was read, but this frame has no TriggerInputs record");
            return false;
        }
        if (!inputs[0].AsBool) return false;
        sample = new RagdollAnimationSample((nint)inputs[1].AsULong, inputs[2].AsFloat, inputs[3].AsFloat);
        return true;
    }

    public int PartCount => Count;
    public bool PartAvailable(int part) => At(part).HasTopology && At(part).Pose != null;
    public bool PartHasPose(int part) => At(part).Pose != null;
    public bool PartInSync(int part) => At(part).InSync;
    public nint PartIdentity(int part) => At(part).TopologyId;
    public nint PartPoseIdentity(int part) => At(part).TopologyId + 0x40000000;
    public Span<BoneQs> PartModelPose(int part) => At(part).Pose;
    public int PartModelPoseLength(int part) => At(part).Pose?.Length ?? 0;
    public int PartBoneNameCount(int part) => At(part).Names.Length;
    public string? PartBoneName(int part, int bone) => At(part).Names[bone];
    public int PartParentCount(int part) => At(part).Parents.Length;
    public short PartParent(int part, int bone) => At(part).Parents[bone];
    public ReadOnlySpan<BoneQs> PartReferencePose(int part) => At(part).Reference;
    public int PartReferenceCount(int part) => At(part).Reference.Length;
    public int PartConnectedBone(int part) => At(part).Connected;
    public int PartConnectedParentBone(int part) => At(part).ConnectedParent;
    public void ResyncPartModelPose(int part) { }
}

internal sealed class ReplaySkeletons : ISkeletonSource
{
    private readonly ReplaySource source;
    private readonly Dictionary<nint, ReplaySkeleton> skeletons = new();
    private readonly Dictionary<(nint Actor, int Purpose, int Partial), byte[]> lastPose = new();
    private readonly Dictionary<nint, int> topologyApplied = new();

    public ReplaySkeletons(ReplaySource source) => this.source = source;

    public ReplaySkeleton? Known(nint actor) => skeletons.TryGetValue(actor, out var s) ? s : null;

    /// <summary>Loads a SkeletonPose record (and the topology records before it) into the actor's state.</summary>
    public ReplaySkeleton Load(RagdollRecord pose, int poseIndex)
    {
        var actor = (nint)pose[0].AsULong;
        if (!skeletons.TryGetValue(actor, out var skeleton))
            skeletons[actor] = skeleton = new ReplaySkeleton(actor, source);

        // Topology first: every SkeletonTopology of this actor recorded since its last pose, before this one.
        topologyApplied.TryGetValue(actor, out var from);
        foreach (var index in source.IndicesOf(RecordType.SkeletonTopology))
        {
            if (index <= from) continue;
            if (index >= poseIndex) break;
            var t = source.Records[index];
            if ((nint)t[0].AsULong == actor) ApplyTopology(skeleton, t, index);
        }
        topologyApplied[actor] = poseIndex;

        var purpose = pose[1].AsInt;
        skeleton.Available = pose[2].AsBool;
        if (!skeleton.Available) return skeleton;
        skeleton.RootValue = new RootTransform(pose[3].AsVector3, pose[4].AsQuaternion, pose[5].AsVector3);
        skeleton.DrawScaleValid = pose[6].AsBool;
        skeleton.DrawScale = pose[7].AsVector3;
        var partials = pose[8].AsInt;
        if (partials > 1 || purpose is (int)PosePurpose.Init or (int)PosePurpose.FrameIn or (int)PosePurpose.FrameOut)
            skeleton.Count = partials;
        var at = 9;
        for (var p = 0; p < partials; p++)
        {
            var index = pose[at].AsInt;
            var state = pose[at + 1].AsInt;
            at += 2;
            var part = skeleton.At(index);
            part.InSync = (state & 4) == 0;
            byte[]? bytes;
            switch (state & 3)
            {
                case 0:
                    bytes = pose[at].Data;
                    at++;
                    lastPose[(actor, purpose, index)] = bytes;
                    break;
                case 1:
                    lastPose.TryGetValue((actor, purpose, index), out bytes);
                    if (bytes == null) source.Diverged($"pose of actor 0x{actor:X} partial {index} says 'same as before' with nothing before");
                    break;
                default:
                    bytes = null;
                    break;
            }
            part.Pose = bytes == null ? null : MemoryMarshal.Cast<byte, BoneQs>(bytes).ToArray();
        }
        return skeleton;
    }

    private static void ApplyTopology(ReplaySkeleton skeleton, RagdollRecord t, int index)
    {
        // actor, partial, boneCount, parentCount, connected, connectedParent, names (List), parents, reference bytes.
        var part = skeleton.At(t[1].AsInt);
        part.HasTopology = true;
        part.TopologyId = 0x1000 + index;
        part.Connected = t[4].AsInt;
        part.ConnectedParent = t[5].AsInt;
        var names = t[6].Count;
        part.Names = new string?[names];
        for (var i = 0; i < names; i++) part.Names[i] = t[7 + i].IsNull ? null : t[7 + i].AsString;
        var parents = t[7 + names];
        part.Parents = new short[parents.Count];
        for (var i = 0; i < parents.Count; i++) part.Parents[i] = (short)parents.IntAt(i);
        part.Reference = MemoryMarshal.Cast<byte, BoneQs>(t[8 + names].Data).ToArray();
    }

    public ISkeletonPose? TryGetSkeleton(nint actor, SkeletonReadSite site = SkeletonReadSite.Other)
    {
        if (site != SkeletonReadSite.Other)
        {
            var (record, index) = source.Peek(RecordType.SkeletonPose,
                r => (nint)r[0].AsULong == actor && r[1].AsInt == (int)site);
            if (record == null)
            {
                source.Diverged($"skeleton of 0x{actor:X} read at {site}, but no such pose is recorded");
                return null;
            }
            source.Take(RecordType.SkeletonPose, r => ReferenceEquals(r, record));
            var loaded = Load(record, index);
            lastLoaded[actor] = index;
            return loaded.Available ? loaded : null;
        }
        // Any other read sees the game memory of this moment: if the actor's next capture is still in this
        // frame (nothing changes the game's memory between two reads in a frame but our own writes, which
        // come after the captures), that capture shows it; otherwise the last one does.
        var (next, nextIndex) = source.Peek(RecordType.SkeletonPose, r => (nint)r[0].AsULong == actor);
        lastLoaded.TryGetValue(actor, out var last);
        if (next != null && nextIndex < source.FrameEnd && nextIndex > last)
        {
            lastLoaded[actor] = nextIndex;
            var early = Load(next, nextIndex);
            return early.Available ? early : null;
        }
        return skeletons.TryGetValue(actor, out var known) && known.Available ? known : null;
    }

    private readonly Dictionary<nint, int> lastLoaded = new();

    public Vector3? GetBoneWorldPosition(nint actor, string boneName)
    {
        var r = source.Take(RecordType.BoneWorldPosition);
        if (r == null)
        {
            source.Diverged($"bone world position of {boneName} read, none recorded");
            return null;
        }
        if ((nint)r[1].AsULong != actor || r[2].AsString != boneName)
            source.Diverged($"bone world position asked for 0x{actor:X} {boneName}, recorded for {r[1]} {r[2]}");
        return r[3].AsBool ? r[4].AsVector3 : null;
    }
}

internal sealed class ReplayWorld : IWorldQuery
{
    private readonly ReplaySource source;
    public ReplayWorld(ReplaySource source) => this.source = source;

    public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        hit = default;
        if (site == RaycastSite.AnimatedHair)
        {
            source.Diverged("the live hair cast a ray (not recorded)");
            return false;
        }
        var r = source.Take(RecordType.Raycast);
        if (r == null)
        {
            source.Diverged($"raycast {site} from {Rec.F(origin)}, none recorded");
            return false;
        }
        if (r[0].AsInt != (int)site || !Rec.SameBits(r[1].AsVector3, origin) || !Rec.SameBits(r[2].AsVector3, direction) ||
            !Rec.SameBits(r[3].AsFloat, maxDistance))
            source.Diverged($"raycast asked {site} {Rec.F(origin)} {Rec.F(direction)} {maxDistance:R}; recorded {(RaycastSite)r[0].AsInt} {r[1]} {r[2]} {r[3]}");
        hit.Point = r[5].AsVector3;
        hit.Normal = r[6].AsVector3;
        hit.Distance = r[7].AsFloat;
        hit.Material = r[8].AsULong;
        hit.V1 = r[9].AsVector3;
        hit.V2 = r[10].AsVector3;
        hit.V3 = r[11].AsVector3;
        return r[4].AsBool;
    }

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
    {
        var r = source.Take(RecordType.GroundTriangles);
        triangles.Clear();
        if (r == null)
        {
            source.Diverged("ground triangles collected, none recorded");
            return false;
        }
        if (!Rec.SameBits(r[0].AsVector3, min) || !Rec.SameBits(r[1].AsVector3, max) || r[2].AsInt != maxTriangles)
            source.Diverged($"ground triangles asked {Rec.F(min)}..{Rec.F(max)} budget {maxTriangles}; recorded {r[0]}..{r[1]} {r[2]}");
        triangles.AddRange(Rec.Vectors(r[4]));
        return r[3].AsBool;
    }

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        surfaceY = 0;
        var r = source.Take(RecordType.WaterSurface);
        if (r == null)
        {
            source.Diverged($"water surface searched at {Rec.F(position)}, none recorded");
            return false;
        }
        if (!Rec.SameBits(r[0].AsVector3, position) || !Rec.SameBits(r[1].AsFloat, searchAbove) || !Rec.SameBits(r[2].AsFloat, searchBelow))
            source.Diverged($"water surface asked at {Rec.F(position)}; recorded {r[0]}");
        surfaceY = r[4].AsFloat;
        return r[3].AsBool;
    }
}

internal sealed class ReplayActors : IRagdollActor
{
    private readonly ReplaySource source;
    private readonly ReplaySkeletons skeletons;
    private RagdollRecord? identity;
    private readonly RagdollRecord? firstIdentity;
    private readonly nint target;

    public ReplayActors(ReplaySource source, ReplaySkeletons skeletons)
    {
        this.source = source;
        this.skeletons = skeletons;
        target = (nint)source.Activate[0].AsULong;
        firstIdentity = source.Peek(RecordType.Identity).Record;
        identity = firstIdentity;
    }

    public bool GameRunning => true;
    public nint LocalPlayer => source.Header[21].AsBool ? target : 0;
    public bool IsAlive(nint actor, uint entityId) => true;

    public Vector3 GetPosition(nint actor, ActorReadSite site)
    {
        switch (site)
        {
            case ActorReadSite.Activation:
                return source.Activate[4].AsVector3;
            case ActorReadSite.Identity:
                return identity?[6].AsVector3 ?? default;
            case ActorReadSite.ColliderMesh or ActorReadSite.ColliderFallback or ActorReadSite.ColliderFallbackBuild:
            {
                var r = source.Take(RecordType.ActorTransform, x => x[0].AsInt == (int)site && (nint)x[1].AsULong == actor);
                if (r == null) { source.Diverged($"{site} transform of 0x{actor:X} read, none recorded"); return default; }
                return r[2].AsVector3;
            }
            default:
            {
                var r = source.Take(RecordType.ActorPosition, x => x[0].AsInt == (int)site);
                if (r == null) { source.Diverged($"{site} position of 0x{actor:X} read, none recorded"); return default; }
                if ((nint)r[1].AsULong != actor) source.Diverged($"{site} position asked for 0x{actor:X}, recorded for {r[1]}");
                return r[2].AsVector3;
            }
        }
    }

    public float GetFacing(nint actor, ActorReadSite site) => identity?[5].AsFloat ?? 0f;
    public uint GetEntityId(nint actor) => source.Activate[5].AsUInt;
    public ulong GetGameObjectId(nint actor) => 0;
    public int GetObjectIndex(nint actor) => source.Activate[6].AsInt;

    public bool TryGetDrawScale(nint actor, out Vector3 scale)
    {
        scale = Vector3.One;
        var known = skeletons.Known(actor);
        if (known == null || !known.DrawScaleValid) return false;
        scale = known.DrawScale;
        return true;
    }

    public void ReadAppearance(nint actor, Span<byte> customize, out int modelCharaId)
    {
        modelCharaId = 0;
        if (firstIdentity == null) { source.Diverged("appearance read, no Identity record"); return; }
        firstIdentity[1].Data.AsSpan(0, Math.Min(customize.Length, firstIdentity[1].Data.Length)).CopyTo(customize);
        modelCharaId = firstIdentity[2].AsInt;
    }

    public void ReadModelType(nint actor, ISkeletonPose? skeleton, out int modelType, out int raceSexId)
    {
        // Asked once per Identity record, by the recorder.
        identity = source.Take(RecordType.Identity) ?? identity;
        modelType = identity?[3].AsInt ?? -1;
        raceSexId = identity?[4].AsInt ?? -1;
    }

    public float GetAnimationSpeed(nint actor) => source.Peek(RecordType.InitEnd).Record?[12].AsFloat ?? 1f;
    public void SetAnimationSpeed(nint actor, float speed) { }
    public void SetDrawPosition(nint actor, Vector3 position) { }
    public bool SetApproachPosition(nint actor, Vector3 position) => source.Activate[9].AsBool;
}

internal sealed class ReplayColliders : IColliderActorProvider
{
    private readonly ReplaySource source;
    public ReplayColliders(ReplaySource source) => this.source = source;

    public bool HasSelector => source.Activate[8].AsBool;
    public bool HasMovementBlock => source.Activate[9].AsBool;
    public bool HasExtraColliders => source.Activate[10].AsBool;

    public void GetSelectedNpcs(List<ColliderCandidate> destination)
    {
        destination.Clear();
        var r = source.Take(RecordType.ActorList, x => x[0].AsString is "selectedNpcs" or "terrainPatchCandidates");
        if (r == null) { source.Diverged("selected NPCs read, none recorded"); return; }
        var count = r[1].Count;
        for (var i = 0; i < count; i++) destination.Add(new ColliderCandidate((nint)r[2 + i].AsULong, true, "replayed"));
        var missing = source.Peek(RecordType.ActorList, x => x[0].AsString == r[0].AsString + ".withoutCharacter");
        if (missing.Record != null && missing.Index == source.Records.IndexOf(r) + 1)
        {
            source.Take(RecordType.ActorList, x => ReferenceEquals(x, missing.Record));
            for (var i = 0; i < missing.Record[1].Count; i++)
                destination.Add(new ColliderCandidate((nint)missing.Record[2 + i].AsULong, false, "replayed"));
        }
    }

    public IReadOnlyList<nint> GetExtraColliders()
    {
        var r = source.Take(RecordType.ActorList, x => x[0].AsString == "extraCollisionProvider");
        if (r == null) { source.Diverged("extra colliders read, none recorded"); return Array.Empty<nint>(); }
        var list = new nint[r[1].Count];
        for (var i = 0; i < list.Length; i++) list[i] = (nint)r[2 + i].AsULong;
        return list;
    }

    public bool TryGetWeaponTransform(nint actor, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var r = source.Take(RecordType.WeaponSample, inFrameOnly: true);
        if (r == null) return false;
        position = r[1].AsVector3;
        rotation = r[2].AsQuaternion;
        return true;
    }
}

internal sealed class ReplaySettings : IRagdollSettingsSource
{
    private readonly ReplaySource source;
    private readonly Dictionary<string, RecValue> values = new(StringComparer.Ordinal);
    private int appliedUpTo;

    public ReplaySettings(ReplaySource source)
    {
        this.source = source;
        var snapshot = source.Take(RecordType.ConfigSnapshot) ?? throw new ReplayUnsupportedException("no ConfigSnapshot record");
        for (var i = 1; i + 1 < snapshot.Values.Length; i += 2)
            values[snapshot[i].AsString!] = snapshot[i + 1];
        appliedUpTo = source.Records.IndexOf(snapshot);
    }

    public bool Refresh(RagdollSettings settings)
    {
        // Changes the game noticed at the start of this render frame (written just before its RenderFrame record).
        var upTo = source.FrameIndex < 0 ? appliedUpTo : source.FrameIndex;
        foreach (var index in source.IndicesOf(RecordType.ConfigChange))
        {
            if (index <= appliedUpTo || index >= upTo) continue;
            var change = source.Records[index];
            values[change[0].AsString!] = change[1];
        }
        appliedUpTo = Math.Max(appliedUpTo, upTo);
        foreach (var property in typeof(RagdollSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!values.TryGetValue(property.Name, out var value))
                throw new ReplayUnsupportedException($"setting {property.Name} is not in the recording");
            property.SetValue(settings, Decode(property.PropertyType, value));
        }
        return true;
    }

    private static object? Decode(Type type, RecValue value)
    {
        if (type == typeof(bool)) return value.AsBool;
        if (type == typeof(int)) return value.AsInt;
        if (type == typeof(float)) return value.AsFloat;
        if (type == typeof(string)) return value.AsString;
        if (type.IsEnum) return Enum.ToObject(type, value.AsInt);
        if (type == typeof(List<string>))
            return value.IsNull ? null : JsonSerializer.Deserialize<List<string>>(value.AsString!) ?? new List<string>();
        if (type == typeof(GuidedCollapseSettings))
            return JsonSerializer.Deserialize<GuidedCollapseSettings>(value.AsString!) ?? new GuidedCollapseSettings();
        throw new ReplayUnsupportedException($"setting type {type.Name} cannot be read back");
    }
}

internal sealed class ReplayClock : IRagdollClock
{
    private readonly ReplaySource source;
    private long frameTicks = 1_000_000_000_000;

    public ReplayClock(ReplaySource source) => this.source = source;

    public long Frequency => source.StopwatchFrequency;

    /// <summary>Advances the frame clock by the recorded dt of the frame about to run.</summary>
    public void AdvanceFrame(float dt)
    {
        var ticks = (long)Math.Round(dt * (double)Frequency);
        for (var probe = ticks - 3; probe <= ticks + 3; probe++)
            if (probe >= 0 && (float)(probe / (double)Frequency) == dt) { ticks = probe; break; }
        frameTicks += ticks;
    }

    public long GetTimestamp(ClockSite site)
    {
        switch (site)
        {
            case ClockSite.FrameDelta:
                return frameTicks;
            case ClockSite.NpcTraversalQuery:
            {
                var r = source.Take(RecordType.Clock, x => x[0].AsString == nameof(ClockSite.NpcTraversalQuery));
                if (r == null) { source.Diverged("walking-support query time read, none recorded"); return 0; }
                return r[1].AsLong;
            }
            default:
                source.Diverged($"wall clock read at {site} (not replayable in stage A2a)");
                return System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public TimeSpan GetElapsedTime(ClockSite site, long since) => TimeSpan.Zero;
}

internal sealed class ReplayRandom : IRagdollRandom
{
    private readonly ReplaySource source;
    public ReplayRandom(ReplaySource source) => this.source = source;

    private double Next(RandomSite site)
    {
        var r = source.Take(RecordType.Random, x => x[0].AsString == site.ToString());
        if (r == null) { source.Diverged($"random number drawn at {site}, none recorded"); return 0; }
        return r[1].AsDouble;
    }

    public double NextDouble(RandomSite site) => Next(site);
    public int Next(RandomSite site, int maxExclusive) => (int)Next(site);
}

internal sealed class ReplayMeshProducts : IRagdollMeshProducts
{
    private readonly ReplaySource source;
    public ReplayMeshProducts(ReplaySource source) => this.source = source;

    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor)
    {
        var r = source.Peek(RecordType.StructuralFits).Record;
        if (r == null) return null;
        switch (r[0].AsInt)
        {
            case 2: return new TaskCompletionSource<RagdollController.StructuralFitResult>().Task; // timed out
            case 3: return Task.FromResult(new RagdollController.StructuralFitResult(DecodeFits(r), "replayed"));
            case 4: return Task.FromException<RagdollController.StructuralFitResult>(new InvalidOperationException("the recorded fit failed"));
            default: return null;
        }
    }

    /// <summary>The fitted soft-tissue bodies, read back from the bone definitions they became (each fit field is
    /// copied into one definition field: radius, half length, centre offset, axis).</summary>
    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, HashSet<int> candidates)
    {
        var fits = new Dictionary<int, RagdollController.SoftTissueMeshFit>();
        var r = source.Peek(RecordType.BoneDefs).Record;
        if (r == null) { source.Diverged("soft-tissue fits asked, no BoneDefs record"); return fits; }
        var count = r[1].AsInt;
        var at = 2;
        for (var d = 0; d < count; d++)
        {
            var pairs = r[at++].Count;
            var fields = new Dictionary<string, RecValue>(StringComparer.Ordinal);
            for (var p = 0; p < pairs; p += 2) fields[r[at + p].AsString!] = r[at + p + 1];
            at += pairs;
            var index = r[at++].AsInt;
            if (!fields.TryGetValue("HasMeshFit", out var has) || !has.AsBool || !candidates.Contains(index)) continue;
            fits[index] = new RagdollController.SoftTissueMeshFit(fields["CapsuleRadius"].AsFloat, fields["CapsuleHalfLength"].AsFloat,
                fields["MeshFitCenterModelOffset"].AsVector3, fields["MeshFitAxisModel"].AsVector3, 0);
        }
        return fits;
    }

    private static Dictionary<string, StructuralMeshFitter.Fit> DecodeFits(RagdollRecord r)
    {
        var fits = new Dictionary<string, StructuralMeshFitter.Fit>(StringComparer.Ordinal);
        var count = r[1].AsInt;
        var at = 2;
        for (var i = 0; i < count; i++)
        {
            var bone = r[at++].AsString!;
            var pairs = r[at++].Count;
            var fit = new StructuralMeshFitter.Fit();
            for (var p = 0; p < pairs; p += 2)
            {
                var name = r[at++].AsString!;
                var value = r[at++];
                var field = typeof(StructuralMeshFitter.Fit).GetField(name, BindingFlags.Public | BindingFlags.Instance)
                            ?? throw new ReplayUnsupportedException($"fit field {name} unknown");
                field.SetValue(fit, DecodeField(field.FieldType, value));
            }
            fits[bone] = fit;
        }
        return fits;
    }

    private static object? DecodeField(Type type, RecValue value)
    {
        if (value.IsNull) return null;
        if (type == typeof(float)) return value.AsFloat;
        if (type == typeof(int)) return value.AsInt;
        if (type == typeof(bool)) return value.AsBool;
        if (type == typeof(Vector3)) return value.AsVector3;
        if (type == typeof(Quaternion)) return value.AsQuaternion;
        if (type == typeof(Vector3[])) return Rec.Vectors(value);
        throw new ReplayUnsupportedException($"fit field type {type.Name} cannot be read back");
    }
}

internal sealed class ReplayLog : IRagdollLog
{
    public readonly List<string> Problems = new();
    public void Info(string message) { }
    public void Warning(string message) => Problems.Add("warning: " + message);
    public void Warning(Exception exception, string message) => Problems.Add($"warning: {message} ({exception.GetType().Name}: {exception.Message})\n{exception.StackTrace}");
    public void Error(string message) => Problems.Add("error: " + message);
    public void Error(Exception exception, string message) => Problems.Add($"error: {message} ({exception.GetType().Name}: {exception.Message})\n{exception.StackTrace}");
}
