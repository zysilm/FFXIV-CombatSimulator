// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>Read-only Framework snapshot of collection-resolved racial-deformation inputs.
/// The source race is inferred from original game resource paths, not observed native slot state.
/// Per-slot native deformer state remains unmapped; surface support is bounded to resolved chains.</summary>
public sealed unsafe class FluidRaceDeformationSource
{
    public const string GamePbdPath = "chara/xls/boneDeformer/human.pbd";
    public sealed record Context(nint DrawIdentity, int ObjectIndex, ushort TargetRace, Guid CollectionId,
        string CollectionName, string ResolvedPbdPath, long PbdModified, long PbdLength, bool Penumbra);

    public sealed record Snapshot(nint DrawIdentity, nint ModelIdentity, int ObjectIndex, int Slot,
        Guid CollectionId, string CollectionName, string LoadedModelPath, string OriginalModelPath,
        string ResolvedPbdPath, ushort SourceRace, ushort TargetRace)
    {
        public Context ContextStamp { get; init; } = null!;
        public bool NativeSlotChainVerified => false;
        public bool RequiresDeformation => SourceRace != TargetRace;
        public string Proof => "source race from actual game resource/original game paths; native slot chain unobserved";
    }

    private readonly record struct Key(Context Context, nint Model, int Slot, string LoadedPath);
    private readonly Dictionary<Key, Snapshot> cache = new();
    public string Status { get; private set; } = "No deformation source snapshot";

    /// <summary>Invoke only during topology/resource snapshot, never for every skin vertex or pose.
    /// Does not dereference modelIdentity, call CreateDeformer, or read undocumented native offsets.</summary>
    public bool TryCapture(Human* human, nint modelIdentity, int objectIndex, int slot,
        string loadedModelPath, out Snapshot? snapshot, out string reason)
    {
        snapshot = null;
        if (!TryCaptureContext(human, objectIndex, out var context, out reason) || context == null) return false;
        return TryCaptureSlot(human, modelIdentity, slot, loadedModelPath, context, out snapshot, out reason);
    }

    /// <summary>One actor-level refresh, called at resource rebuild, at most once per second while active,
    /// and immediately before installing a completed topology. No per-slot IPC or file contents read.</summary>
    public bool TryCaptureContext(Human* human, int objectIndex, out Context? context, out string reason)
    {
        context = null;
        reason = "";
        if (!Services.Framework.IsInFrameworkUpdateThread || human == null || objectIndex < 0)
            return Fail("Invalid Framework human/object context", out reason);
        if (human->CharacterBase.GetModelType() != CharacterBase.ModelType.Human || human->RaceSexId == 0)
            return Fail("Human target race is unavailable", out reason);
        try
        {
            var pi = Services.PluginInterface;
            var collectionCall = pi.GetIpcSubscriber<int,
                (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)>("Penumbra.GetCollectionForObject.V5");
            var resolveCall = pi.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath");
            bool loaded = pi.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName.Equals("Penumbra", StringComparison.OrdinalIgnoreCase));
            bool penumbra = collectionCall.HasFunction || resolveCall.HasFunction || loaded;
            Guid collectionId = Guid.Empty;
            string collectionName = "vanilla", pbd = GamePbdPath;
            if (penumbra)
            {
                if (!collectionCall.HasFunction || !resolveCall.HasFunction)
                    return Fail("Loaded Penumbra collection/PBD IPC is unavailable", out reason);
                var collection = collectionCall.InvokeFunc(objectIndex);
                if (!collection.ObjectValid) return Fail("Penumbra object collection is unavailable", out reason);
                collectionId = collection.EffectiveCollection.Id; collectionName = collection.EffectiveCollection.Name;
                pbd = resolveCall.InvokeFunc(GamePbdPath, objectIndex);
                if (string.IsNullOrWhiteSpace(pbd)) return Fail("Penumbra returned no collection PBD path", out reason);
            }
            long modified = 0, length = 0;
            if (Path.IsPathRooted(pbd))
            {
                var info = new FileInfo(pbd);
                if (!info.Exists) return Fail("Resolved PBD file absent", out reason);
                modified = info.LastWriteTimeUtc.Ticks; length = info.Length;
                if (length <= 0 || length > FluidRaceDeformation.MaximumFileBytes)
                    return Fail("Resolved PBD file outside size bounds", out reason);
            }
            context = new Context((nint)human, objectIndex, human->RaceSexId, collectionId,
                collectionName, pbd, modified, length, penumbra);
            Status = reason = penumbra ? "Collection-resolved deformation context" : "Vanilla deformation context";
            return true;
        }
        catch (Exception ex) { return Fail("Deformation context unavailable: " + ex.GetType().Name + ": " + ex.Message, out reason); }
    }

    public bool TryCaptureSlot(Human* human, nint modelIdentity, int slot, string loadedModelPath,
        Context context, out Snapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = "";
        if (!Services.Framework.IsInFrameworkUpdateThread)
            return Fail("Deformation source capture requires Framework thread", out reason);
        if (human == null || modelIdentity == 0 || context.DrawIdentity != (nint)human || slot < 0 || slot >= human->CharacterBase.SlotCount)
            return Fail("Invalid human/model/slot identity", out reason);
        if (human->CharacterBase.GetModelType() != CharacterBase.ModelType.Human)
            return Fail("Draw object is not a Human model", out reason);
        if (string.IsNullOrWhiteSpace(loadedModelPath) || !loadedModelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return Fail("Loaded model has no explicit MDL path", out reason);

        // RaceSexId is mapped in the installed SDK. No arbitrary pointer layout is used here.
        ushort target = human->RaceSexId;
        if (target != context.TargetRace) return Fail("Human race changed during snapshot", out reason);
        if (target == 0) return Fail("Human target RaceSexId is unknown", out reason);
        try
        {
            var key = new Key(context, modelIdentity, slot, loadedModelPath);
            if (cache.TryGetValue(key, out snapshot))
            {
                Status = reason = "Cached PBD source: " + snapshot.SourceRace + " -> " + target;
                return true;
            }

            string[]? originals;
            if (TryReadExplicitGameRace(loadedModelPath, out _)) originals = new[] { loadedModelPath };
            else if (context.Penumbra)
                originals = Services.PluginInterface.GetIpcSubscriber<string, int, string[]>("Penumbra.ReverseResolveGameObjectPath")
                    .InvokeFunc(loadedModelPath, context.ObjectIndex);
            else return Fail("Vanilla slot lacks actual native game MDL path", out reason);
            if (originals == null || originals.Length == 0 || originals.Length > 128)
                return Fail("No bounded original game MDL paths for loaded resource", out reason);
            ushort donor = 0;
            string original = "";
            foreach (var path in originals)
            {
                // Reverse lookup can include disk aliases. Only legitimate game MDL paths qualify.
                if (!IsGameModelPath(path)) continue;
                if (!TryReadExplicitGameRace(path, out var race))
                    return Fail("Original game MDL path has unknown/conflicting race: " + path, out reason);
                if (donor != 0 && race != donor)
                    return Fail("Loaded resource resolves to multiple different source races", out reason);
                donor = race;
                if (original.Length == 0 || string.CompareOrdinal(path, original) < 0) original = path;
            }
            if (donor == 0) return Fail("No unambiguous original game race path", out reason);
            snapshot = new Snapshot((nint)human, modelIdentity, context.ObjectIndex, slot,
                context.CollectionId, context.CollectionName, loadedModelPath,
                original, context.ResolvedPbdPath, donor, target) { ContextStamp = context };
            if (cache.Count >= 64) cache.Clear();
            cache[key] = snapshot;
            Status = reason = $"Resolved PBD source {donor} -> {target}; {snapshot.Proof}";
            return true;
        }
        catch (Exception ex)
        {
            return Fail("Read-only Penumbra deformation metadata unavailable: " + ex.GetType().Name + ": " + ex.Message, out reason);
        }
    }

    public void Invalidate() => cache.Clear();

    private bool Fail(string message, out string reason)
    {
        Status = reason = message;
        return false;
    }

    private static bool TryReadExplicitGameRace(string? path, out ushort race)
    {
        race = 0;
        // Only actual native game paths or original game paths returned by IPC qualify.
        if (path == null || !IsGameModelPath(path)) return false;
        for (int i = 0; i + 5 < path.Length; ++i)
        {
            if ((i != 0 && path[i - 1] != '/') || char.ToLowerInvariant(path[i]) != 'c') continue;
            int value = 0;
            bool digits = true;
            for (int j = 1; j <= 4; ++j)
            {
                char c = path[i + j];
                if (c < '0' || c > '9') { digits = false; break; }
                value = value * 10 + c - '0';
            }
            // c#### path component or c####<category>#### model basename.
            if (!digits || value == 0 || (path[i + 5] != '/' &&
                (path[i + 5] < 'a' || path[i + 5] > 'z'))) continue;
            if (race != 0 && race != value) return false;
            race = (ushort)value;
        }
        return race != 0;
    }

    private static bool IsGameModelPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 2048 &&
        path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains('\\') && !path.Contains("/../", StringComparison.Ordinal) && !path.Contains("/./", StringComparison.Ordinal);
}
