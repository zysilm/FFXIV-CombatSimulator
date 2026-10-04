// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Core;
using Lumina;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private readonly record struct PbdKey(string Path, long Modified, long Length);
    private readonly record struct PbdResult(FluidRaceDeformation? Data, string Reason);
    private readonly Dictionary<PbdKey, Task<PbdResult>> pbdLoads = new();
    private readonly Dictionary<PbdKey, long> pbdCharges = new();
    private long pbdCacheBytes, deformedCacheBytes;
    private bool deformationDeferred;
    private const long MaximumPbdCacheBytes = 32L * 1024 * 1024;
    private const long MaximumDeformedCacheBytes = 64L * 1024 * 1024;
    private readonly List<Task<PbdResult>> awaitedPbdLoads = new();
    private readonly Dictionary<(FluidRaceDeformation, ushort, ushort), FluidRaceDeformation.Chain> deformationChains = new();
    private readonly record struct DeformedMeshKey(FluidModelData Model, int Mesh, string MapSignature, FluidRaceDeformation.Chain Chain);
    private sealed record DeformedMeshResult(Vertex[] Vertices, bool[] Valid, int Rejected, string Reason);
    private readonly Dictionary<DeformedMeshKey, Task<DeformedMeshResult>> deformedMeshes = new();
    private readonly List<Task<DeformedMeshResult>> awaitedDeformedMeshes = new();
    private readonly FluidRaceDeformationSource deformationSource = new();
    private bool builtRaceDeformation;
    /// <summary>Apply collection-resolved/pre-skin racial deformation. False is raw diagnostic geometry only.</summary>
    public bool ApplyRaceDeformation { get; set; } = true;
    public string DeformationStatus { get; private set; } = "Racial deformation awaiting source snapshot";
    private FluidRaceDeformationSource.Context? deformationContext;
    private string deformationContextFailure = "No actor context";
    private long nextDeformationPoll;
    private ulong deformationEpoch;
    private bool deformationNeedsRebuild;

    private bool RefreshDeformationContext(CombatSimulator.Animation.SkeletonAccess ns, nint address, bool force)
    {
        if (!ApplyRaceDeformation) return false;
        long now = Stopwatch.GetTimestamp();
        if (!force && now < nextDeformationPoll) return false;
        nextDeformationPoll = now + Stopwatch.Frequency;
        var human = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)ns.CharBase;
        int objectIndex = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->ObjectIndex;
        bool success = deformationSource.TryCaptureContext(human, objectIndex, out var fresh, out var reason);
        string failure = success ? "" : reason;
        bool changed = deformationContext != fresh || deformationContextFailure != failure;
        if (changed)
        {
            deformationContext = fresh; deformationContextFailure = failure;
            unchecked { deformationEpoch++; }
            deformationSource.Invalidate();
        }
        return changed;
    }

    // A completed topology can be installed between one-second polls. Recheck its single
    // actor context immediately before publication; never read every slot through IPC here.
    private bool ValidateDeformationInstall()
    {
        if (!ApplyRaceDeformation) return true;
        var access = bones.TryGetSkeleton(actor);
        if (access == null) { deformationNeedsRebuild = true; return false; }
        if (RefreshDeformationContext(access.Value, actor, true))
        {
            deformationNeedsRebuild = true;
            return false;
        }
        return true;
    }

    private bool PendingDeformationsReady()
    {
        foreach (var work in awaitedPbdLoads) if (!work.IsCompleted) return false;
        foreach (var work in awaitedDeformedMeshes) if (!work.IsCompleted) return false;
        // A deferred job has not been launched because the four-job limit is occupied.
        // Wait only by polling: old actor jobs are retained/accounted until completion.
        if (deformationDeferred && RunningDeformationJobs() >= 4) return false;
        return true;
    }

    private int RunningDeformationJobs()
    {
        int count = 0;
        foreach (var work in pbdLoads.Values) if (!work.IsCompleted) count++;
        foreach (var work in deformedMeshes.Values) if (!work.IsCompleted) count++;
        return count;
    }

    private FluidRaceDeformation.Chain? AcquireDeformation(FluidRaceDeformationSource.Snapshot source,
        out bool pending, out string reason)
    {
        pending = false;
        reason = "Identity race; raw vertex positions";
        if (!source.RequiresDeformation) return null;
        try
        {
            string path = source.ResolvedPbdPath;
            bool disk = Path.IsPathRooted(path);
            long modified = 0, length = 0;
            if (disk)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > FluidRaceDeformation.MaximumFileBytes)
                { reason = "PBD file absent / outside size bounds"; return null; }
                modified = info.LastWriteTimeUtc.Ticks; length = info.Length;
                if (modified != source.ContextStamp.PbdModified || length != source.ContextStamp.PbdLength)
                { reason = "PBD identity changed after actor context snapshot"; return null; }
            }
            var key = new PbdKey(path, modified, length);
            if (!pbdLoads.TryGetValue(key, out var work))
            {
                if (RunningDeformationJobs() >= 4)
                { pending = deformationDeferred = true; reason = "PBD job deferred by four-worker bound"; return null; }
                long charge = disk ? length : FluidRaceDeformation.MaximumFileBytes;
                if (pbdCacheBytes + charge > MaximumPbdCacheBytes || pbdLoads.Count >= 16)
                {
                    // Evict completed entries only. Running jobs remain owned and charged.
                    var evict = new List<PbdKey>();
                    foreach (var item in pbdLoads) if (item.Value.IsCompleted) evict.Add(item.Key);
                    foreach (var old in evict)
                    { pbdLoads.Remove(old); pbdCacheBytes -= pbdCharges[old]; pbdCharges.Remove(old); }
                    deformationChains.Clear();
                }
                if (pbdCacheBytes + charge > MaximumPbdCacheBytes)
                { reason = "PBD snapshot rejected by 32 MiB input-byte cache bound"; return null; }
                GameData data = Services.DataManager.GameData;
                work = Task.Run(() => LoadPbdSnapshot(data, key, disk));
                pbdLoads.Add(key, work);
                pbdCharges.Add(key, charge); pbdCacheBytes += charge;
            }
            if (!work.IsCompleted)
            {
                awaitedPbdLoads.Add(work); pending = true;
                reason = "Managed PBD snapshot pending";
                return null;
            }
            var result = work.GetAwaiter().GetResult();
            if (result.Data == null) { reason = result.Reason; return null; }
            var chainKey = (result.Data, source.SourceRace, source.TargetRace);
            if (!deformationChains.TryGetValue(chainKey, out var chain))
            {
                if (!result.Data.TryGetChain(source.SourceRace, source.TargetRace, out chain, out reason) || chain == null)
                    return null;
                deformationChains.Add(chainKey, chain);
            }
            reason = $"Resolved PBD {source.SourceRace}->{source.TargetRace}, steps={chain.StepCount}; native slot chain unobserved";
            return chain;
        }
        catch (Exception ex) { reason = "PBD snapshot failed: " + ex.GetType().Name + ": " + ex.Message; return null; }
    }

    private static PbdResult LoadPbdSnapshot(GameData gameData, PbdKey key, bool disk)
    {
        try
        {
            byte[]? bytes = disk ? File.ReadAllBytes(key.Path) : gameData.GetFile(key.Path)?.Data;
            if (bytes == null || bytes.Length <= 0 || bytes.Length > FluidRaceDeformation.MaximumFileBytes)
                return new(null, "PBD bytes unavailable / outside size bounds");
            if (disk)
            {
                var info = new FileInfo(key.Path);
                if (!info.Exists || info.Length != key.Length || info.LastWriteTimeUtc.Ticks != key.Modified)
                    return new(null, "PBD changed during snapshot; redraw required");
            }
            return FluidRaceDeformation.TryParse(bytes, out var data, out var error)
                ? new(data, "PBD parsed") : new(null, error);
        }
        catch (Exception ex) { return new(null, "PBD decode failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private DeformedMeshResult? AcquireDeformedMesh(FluidModelData mdl, int mi, int[] map, string[] names,
        FluidRaceDeformation.Chain chain, out bool pending, out string reason)
    {
        pending = false; reason = "";
        var key = new DeformedMeshKey(mdl, mi, string.Join(",", map) + ":" + string.Join("|", names), chain);
        if (!deformedMeshes.TryGetValue(key, out var work))
        {
            if (RunningDeformationJobs() >= 4)
            { pending = deformationDeferred = true; reason = "PBD vertex job deferred by four-worker bound"; return null; }
            long charge = (long)mdl.Meshes[mi].VertexCount * 128;
            if (deformedCacheBytes + charge > MaximumDeformedCacheBytes || deformedMeshes.Count >= 256)
            {
                var evict = new List<DeformedMeshKey>();
                foreach (var item in deformedMeshes) if (item.Value.IsCompleted) evict.Add(item.Key);
                foreach (var old in evict)
                { deformedMeshes.Remove(old); deformedCacheBytes -= (long)old.Model.Meshes[old.Mesh].VertexCount * 128; }
            }
            if (deformedCacheBytes + charge > MaximumDeformedCacheBytes)
            { reason = "PBD vertex snapshot rejected by 64 MiB conservative buffer bound"; return null; }
            // Only immutable managed buffers enter the worker. The job never reads actor/rig state.
            var privateMap = (int[])map.Clone();
            var privateNames = (string[])names.Clone();
            work = Task.Run(() => DecodeDeformedMesh(mdl, mi, privateMap, privateNames, chain));
            deformedMeshes.Add(key, work);
            deformedCacheBytes += charge;
        }
        pending = !work.IsCompleted;
        if (pending) { awaitedDeformedMeshes.Add(work); return null; }
        return work.GetAwaiter().GetResult();
    }

    private static DeformedMeshResult DecodeDeformedMesh(FluidModelData mdl, int mi, int[] map, string[] names,
        FluidRaceDeformation.Chain chain)
    {
        var mesh = mdl.Meshes[mi];
        var points = new Vertex[mesh.VertexCount];
        var valid = new bool[mesh.VertexCount];
        var globalNames = new Dictionary<int, string>();
        for (int i = 0; i < map.Length; i++) if (map[i] >= 0) globalNames[map[i]] = names[i];
        var boneNames = new string[8];
        Span<float> weights = stackalloc float[8];
        int rejected = 0;
        string firstReason = "";
        for (int i = 0; i < points.Length; i++)
        {
            if (!ReadVertex(mdl, mesh, mdl.VertexDeclarations[mi], i, map, out var point, out int missing))
            { rejected++; if (firstReason.Length == 0) firstReason = $"MDL vertex decode/rig rejected (local bone {missing})"; continue; }
            weights[0] = point.Weights.X; weights[1] = point.Weights.Y; weights[2] = point.Weights.Z; weights[3] = point.Weights.W;
            weights[4] = point.ExtraWeights.X; weights[5] = point.ExtraWeights.Y; weights[6] = point.ExtraWeights.Z; weights[7] = point.ExtraWeights.W;
            for (int b = 0; b < 8; b++)
            {
                int bone = b switch
                { 0 => point.B0, 1 => point.B1, 2 => point.B2, 3 => point.B3, 4 => point.B4, 5 => point.B5, 6 => point.B6, _ => point.B7 };
                boneNames[b] = weights[b] > 0 && globalNames.TryGetValue(bone, out var name) ? name : "";
            }
            if (!chain.TryDeform(point.Position, boneNames, weights, out var corrected, out var error))
            { rejected++; if (firstReason.Length == 0) firstReason = error; continue; }
            point.Position = corrected;
            points[i] = point; valid[i] = true;
        }
        return new(points, valid, rejected, firstReason);
    }
}
