// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>Read-only Framework snapshot of collection-resolved racial-deformation inputs.
/// The source race is inferred from original game resource paths, not observed native slot state.
/// Consumers must treat this as a diagnostic candidate until the resulting surface is verified.</summary>
public sealed unsafe class FluidRaceDeformationSource
{
    public const string GamePbdPath = "chara/xls/boneDeformer/human.pbd";

    public sealed record Snapshot(nint DrawIdentity, nint ModelIdentity, int ObjectIndex, int Slot,
        Guid CollectionId, string CollectionName, string LoadedModelPath, string OriginalModelPath,
        string ResolvedPbdPath, ushort SourceRace, ushort TargetRace)
    {
        public bool NativeSlotChainVerified => false;
        public bool RequiresDeformation => SourceRace != TargetRace;
        public string Proof => "source race inferred from Penumbra original game paths; native slot chain unobserved";
    }

    private readonly record struct Key(nint Draw, nint Model, int ObjectIndex, int Slot,
        Guid Collection, string LoadedPath, string PbdPath, ushort TargetRace);
    private readonly Dictionary<Key, Snapshot> cache = new();
    public string Status { get; private set; } = "No deformation source snapshot";

    /// <summary>Invoke only during topology/resource snapshot, never for every skin vertex or pose.
    /// Does not dereference modelIdentity, call CreateDeformer, or read undocumented native offsets.</summary>
    public bool TryCapture(Human* human, nint modelIdentity, int objectIndex, int slot,
        string loadedModelPath, out Snapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = "";
        if (!Services.Framework.IsInFrameworkUpdateThread)
            return Fail("Deformation source capture requires Framework thread", out reason);
        if (human == null || modelIdentity == 0 || objectIndex < 0 || slot < 0 || slot >= human->CharacterBase.SlotCount)
            return Fail("Invalid human/model/slot identity", out reason);
        if (human->CharacterBase.GetModelType() != CharacterBase.ModelType.Human)
            return Fail("Draw object is not a Human model", out reason);
        if (string.IsNullOrWhiteSpace(loadedModelPath) || !loadedModelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return Fail("Loaded model has no explicit MDL path", out reason);

        // RaceSexId is mapped in the installed SDK. No arbitrary pointer layout is used here.
        ushort target = human->RaceSexId;
        if (target == 0) return Fail("Human target RaceSexId is unknown", out reason);
        try
        {
            var pi = Services.PluginInterface;
            var collection = pi.GetIpcSubscriber<int,
                (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)>(
                "Penumbra.GetCollectionForObject.V5").InvokeFunc(objectIndex);
            if (!collection.ObjectValid)
                return Fail("Penumbra object collection is unavailable", out reason);
            var resolvedPbd = pi.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath")
                .InvokeFunc(GamePbdPath, objectIndex);
            if (string.IsNullOrWhiteSpace(resolvedPbd))
                return Fail("Penumbra returned no current collection PBD path", out reason);
            var key = new Key((nint)human, modelIdentity, objectIndex, slot, collection.EffectiveCollection.Id,
                loadedModelPath, resolvedPbd, target);
            if (cache.TryGetValue(key, out snapshot))
            {
                Status = reason = "Cached diagnostic PBD candidate: " + snapshot.SourceRace + " -> " + target;
                return true;
            }

            var originals = pi.GetIpcSubscriber<string, int, string[]>("Penumbra.ReverseResolveGameObjectPath")
                .InvokeFunc(loadedModelPath, objectIndex);
            if (originals == null || originals.Length == 0 || originals.Length > 128)
                return Fail("No bounded original game MDL paths for loaded resource", out reason);
            ushort donor = 0;
            string original = "";
            foreach (var path in originals)
            {
                if (!TryReadExplicitGameRace(path, out var race))
                    return Fail("Original resource path has no unambiguous game race: " + path, out reason);
                if (donor != 0 && race != donor)
                    return Fail("Loaded resource resolves to multiple different source races", out reason);
                donor = race;
                if (original.Length == 0 || string.CompareOrdinal(path, original) < 0) original = path;
            }
            snapshot = new Snapshot((nint)human, modelIdentity, objectIndex, slot,
                collection.EffectiveCollection.Id, collection.EffectiveCollection.Name, loadedModelPath,
                original, resolvedPbd, donor, target);
            if (cache.Count >= 64) cache.Clear();
            cache[key] = snapshot;
            Status = reason = $"Diagnostic PBD candidate {donor} -> {target}; {snapshot.Proof}";
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
        // Only original game paths returned by IPC qualify. Never inspect a disk mod filename.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 ||
            !path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return false;
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
}
