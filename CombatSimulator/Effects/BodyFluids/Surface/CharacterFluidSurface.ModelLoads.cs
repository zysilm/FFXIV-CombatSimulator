// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using Lumina;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    // Keys are compared only on Framework. They are identities, never dereferenced by workers.
    private readonly record struct ModelLoadKey(string Path, long Modified, long Length, nint Resource, nint ResourceData);
    private readonly record struct ModelLoadResult(FluidModelData? Model, string Reason, double SnapshotMilliseconds, double DecodeMilliseconds);
    private sealed class ModelLoadEntry
    {
        public Task<ModelLoadResult> Work = null!;
        public int Bytes;
    }
    private readonly Dictionary<ModelLoadKey, ModelLoadEntry> modelLoads = new();
    private ModelLoadKey? faceModelKey;
    private readonly List<ModelLoadEntry> awaitedModelLoads = new();
    private long cachedModelBytes;
    private bool topologyAwaitingModels;
    private bool builtFaceOnlyScope;
    /// <summary>Current proof phase reads the actual local player's face slot 11. Other models are deferred.</summary>
    public bool FaceOnlyCapture { get; set; } = true;
    /// <summary>Explicit full runtime request. The new generation is installed after its complete model batch is ready.</summary>
    public bool IncludeBodySurface { get => !FaceOnlyCapture; set => FaceOnlyCapture = !value; }
    public double LastModelSnapshotMilliseconds { get; private set; }
    public double LastModelDecodeMilliseconds { get; private set; }
    public string ModelLoadStatus { get; private set; } = "No model snapshot";

    private bool PendingModelsReady()
    {
        if (!topologyAwaitingModels || (awaitedModelLoads.Count == 0 && awaitedPbdLoads.Count == 0 && awaitedDeformedMeshes.Count == 0 && !deformationDeferred)) return false;
        foreach (var entry in awaitedModelLoads) if (!entry.Work.IsCompleted) return false;
        return PendingDeformationsReady();
    }

    private FluidModelData? AcquireModel(string path, bool disk, nint resource, nint resourceData, out string route, out bool pending)
    {
        pending = false;
        route = "managed byte snapshot / background raw MDL";
        var timer = Stopwatch.StartNew();
        try
        {
            long modified = 0, length = 0;
            if (disk)
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 64 * 1024 * 1024) { route = "file absent / over 64 MiB"; return null; }
                modified = file.LastWriteTimeUtc.Ticks; length = file.Length;
            }
            var key = new ModelLoadKey(path, modified, length, resource, resourceData);
            if (resource == resources[11] && resourceData == this.resourceData[11]) faceModelKey = key;
            if (!modelLoads.TryGetValue(key, out var entry))
            {
                if (awaitedModelLoads.Count == 0 && (modelLoads.Count >= 64 || cachedModelBytes + length > 128L * 1024 * 1024))
                {
                    // Dropping cache references never waits for a worker. Old jobs own only their managed snapshot.
                    modelLoads.Clear(); cachedModelBytes = 0;
                }
                // Installed Lumina GameData.GetFile uses pooled SqPack streams and locked resource caches.
                // Its FileHandle.Load explicitly calls this same method from the worker load queue.
                // Pass only managed GameData + copied path/metadata. No native identity is captured by this job.
                GameData gameData = Services.DataManager.GameData;
                entry = new ModelLoadEntry { Bytes = checked((int)length), Work = Task.Run(() => LoadAndDecodeSnapshot(gameData, path, disk, modified, length)) };
                modelLoads[key] = entry; cachedModelBytes += length;
            }
            if (!entry.Work.IsCompleted)
            {
                awaitedModelLoads.Add(entry); pending = true;
                ModelLoadStatus = "Managed model decode pending; emission paused";
                return null;
            }
            // IsCompleted is checked first. No wait, synchronous task result blocking, or native worker access.
            var result = entry.Work.GetAwaiter().GetResult();
            LastModelDecodeMilliseconds = result.DecodeMilliseconds;
            LastModelSnapshotMilliseconds = result.SnapshotMilliseconds;
            if (entry.Bytes == 0 && result.Model != null) { entry.Bytes = result.Model.Data.Length; cachedModelBytes += entry.Bytes; }
            route = "cached background raw MDL: " + result.Reason;
            ModelLoadStatus = result.Model == null ? result.Reason : "Managed model ready";
            return result.Model;
        }
        catch (Exception ex)
        {
            ModelLoadStatus = route = $"Snapshot failed: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static ModelLoadResult LoadAndDecodeSnapshot(GameData gameData, string path, bool disk, long modified, long length)
    {
        var timer = Stopwatch.StartNew();
        double snapshotTime = 0;
        try
        {
            byte[]? raw = disk ? File.ReadAllBytes(path) : gameData.GetFile(path)?.Data;
            if (raw == null || raw.Length == 0 || raw.Length > 64 * 1024 * 1024)
                return new(null, "Managed bytes unavailable / over 64 MiB", timer.Elapsed.TotalMilliseconds, 0);
            if (disk)
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified)
                    return new(null, "File changed during snapshot; redraw required", timer.Elapsed.TotalMilliseconds, 0);
            }
            var snapshot = (byte[])raw.Clone();
            snapshotTime = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            bool success = RagdollController.TryReadFluidModelData(snapshot, out var model);
            return new(success ? model : null, success ? "parsed" : "unsupported metadata", snapshotTime, timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) { return new(null, $"Decode failed: {ex.GetType().Name}", snapshotTime, timer.Elapsed.TotalMilliseconds); }
    }
}
