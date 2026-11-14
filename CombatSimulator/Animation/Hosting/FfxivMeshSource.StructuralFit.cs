// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using StructuralFitResult = CombatSimulator.Animation.RagdollController.StructuralFitResult;

namespace CombatSimulator.Animation.Hosting;

// Reading the models a hull fit needs, and the fit itself on a worker (moved here from
// RagdollController.StructuralMeshFit.cs in stage A2b, unchanged). One appearance is fitted once and
// shared by every ragdoll that wears it.
internal sealed unsafe partial class FfxivMeshSource
{
    // What a worker needs of one drawn model, taken on the framework thread.
    private sealed record StructuralFitModel(ModelGeometryData Data, uint Attributes, bool[] Shapes, ushort SourceRace);

    private static readonly ConcurrentDictionary<string, Task<StructuralFitResult>> StructuralFitCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RenderedRaceDeformation?> StructuralFitDeformers = new(StringComparer.Ordinal);
    private const int StructuralFitCacheLimit = 48;
    // Equipment slots that carry the surface of the torso and legs: body, hands, legs, feet.
    private static readonly int[] StructuralFitSlots = { 1, 2, 3, 4 };
    private static readonly HashSet<string> StructuralFitBones = new(StructuralMeshFitter.Targets.Select(t => t.Bone), StringComparer.Ordinal);

    private Task<StructuralFitResult>? StartStructuralMeshFit(nint actor, SkeletonAccess skel)
    {
        var cb = skel.CharBase;
        if (cb == null || cb->Models == null || skel.HavokSkeleton == null ||
            cb->GetModelType() != CharacterBase.ModelType.Human || !RagdollController.ReadCharacterSurfaceIdentity(actors, actor).IsHumanoid)
            return null;

        var targetRace = ((Human*)cb)->RaceSexId;
        var slotCount = Math.Clamp(cb->SlotCount, 0, 32);
        var penumbra = IsRenderedPenumbraLoaded();

        // The appearance: which files are drawn, with which parts and shape keys switched on.
        var key = new StringBuilder(512);
        key.Append(targetRace).Append('|').Append(skel.BoneCount).Append('|').Append(penumbra ? 'p' : 'n');
        var drawn = new List<int>();
        foreach (var slot in StructuralFitSlots)
        {
            if (slot >= slotCount) continue;
            var model = cb->Models[slot];
            if (model == null || model->ModelResourceHandle == null) continue;
            var resource = (ResourceHandle*)model->ModelResourceHandle;
            key.Append('|').Append(slot).Append(':').Append(Encoding.UTF8.GetString(resource->FileName.AsSpan()))
                .Append(':').Append(model->EnabledAttributeIndexMask).Append(':').Append(model->EnabledShapeKeyIndexMask);
            drawn.Add(slot);
        }
        if (drawn.Count == 0)
            return null;

        var cacheKey = key.ToString();
        if (StructuralFitCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!TryBuildReferenceModelTransforms(skel, out var referenceModel))
            return null;
        var boneNames = new string[referenceModel.Length];
        var boneParents = new int[referenceModel.Length];
        for (var i = 0; i < referenceModel.Length; i++)
        {
            boneNames[i] = skel.HavokSkeleton->Bones[i].Name.String ?? string.Empty;
            boneParents[i] = skel.HavokSkeleton->ParentIndices[i];
        }

        var models = new List<StructuralFitModel>(drawn.Count);
        var notes = new List<string>();
        RenderedRaceDeformation? deformer = null;
        var deformerLoaded = false;
        byte* logicalBuffer = stackalloc byte[CharacterBase.PathBufferSize];
        foreach (var slot in drawn)
        {
            var model = cb->Models[slot];
            var resource = (ResourceHandle*)model->ModelResourceHandle;
            var path = Encoding.UTF8.GetString(resource->FileName.AsSpan());
            if (!TryLoadRenderedModel(resource, path, out var data, out _) || data.Lods.Length == 0)
            {
                notes.Add($"slot {slot} unreadable");
                continue;
            }

            // Which race the model was made for decides whether the race deformer reshapes it.
            // ResolveMdlPath yields the logical cXXXX code even when a mod's file name has none.
            var logicalPath = cb->ResolveMdlPath(logicalBuffer, CharacterBase.PathBufferSize, (uint)slot).ToString();
            var sourceRace = ReadRenderedModelRace(logicalPath);
            if (penumbra && actor != 0)
            {
                try
                {
                    var reverse = Services.PluginInterface.GetIpcSubscriber<string, int, string[]>("Penumbra.ReverseResolveGameObjectPath")
                        .InvokeFunc(path, ((GameObject*)actor)->ObjectIndex);
                    var candidates = reverse.Select(ReadRenderedModelRace).Where(r => r != 0).Distinct().ToArray();
                    if (candidates.Length == 1) sourceRace = candidates[0];
                    else if (candidates.Length > 1 && !reverse.Contains(logicalPath, StringComparer.OrdinalIgnoreCase)) sourceRace = 0;
                }
                catch { /* the native logical path stands */ }
            }
            if (sourceRace == 0)
            {
                notes.Add($"slot {slot} authored race unknown");
                continue;
            }
            if (sourceRace != targetRace && !deformerLoaded)
            {
                deformerLoaded = true;
                deformer = LoadStructuralFitDeformer(actor);
            }
            if (sourceRace != targetRace && deformer == null)
            {
                notes.Add($"slot {slot} race deformer unavailable");
                continue;
            }

            var shapes = new bool[data.Shapes.Length];
            for (var i = 0; i < shapes.Length; i++)
            {
                var name = RenderedGeometryDecoder.StringAt(data.Strings, data.Shapes[i].StringOffset);
                var renderIndex = string.IsNullOrEmpty(name) ? -1 : model->GetShapeIndex(name);
                shapes[i] = renderIndex is >= 0 and < 32 && (model->EnabledShapeKeyIndexMask & (1u << renderIndex)) != 0;
            }
            models.Add(new StructuralFitModel(data, model->EnabledAttributeIndexMask, shapes, sourceRace));
        }
        if (models.Count == 0)
            return null;

        if (StructuralFitCache.Count >= StructuralFitCacheLimit)
            StructuralFitCache.Clear();
        var worker = scheduler.Run(() => ComputeStructuralMeshFits(models, deformer, targetRace, boneNames, boneParents, referenceModel, notes));
        StructuralFitCache[cacheKey] = worker;
        return worker;
    }

    /// <summary>Worker thread: managed data only.</summary>
    private static StructuralFitResult ComputeStructuralMeshFits(List<StructuralFitModel> models, RenderedRaceDeformation? deformer,
        ushort targetRace, string[] boneNames, int[] boneParents, Matrix4x4[] referenceModel, List<string> notes)
    {
        var fits = new Dictionary<string, StructuralMeshFitter.Fit>(StringComparer.Ordinal);
        try
        {
            var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < boneNames.Length; i++)
                boneIndex.TryAdd(boneNames[i], i);

            var samples = new Dictionary<string, List<StructuralMeshFitter.Sample>>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                var sourceRace = model.SourceRace;
                StructuralMeshSampler.Collect(model.Data, model.Attributes, model.Shapes,
                    sourceRace == targetRace || deformer == null
                        ? null
                        : names => StructuralMeshSampler.RaceDeformation(deformer, sourceRace, targetRace, names, boneNames, boneParents),
                    StructuralFitBones, samples);
            }

            Vector3? At(string name) => boneIndex.TryGetValue(name, out var i) ? referenceModel[i].Translation : null;
            var referenceHeight = StructuralMeshFitter.ReferenceHeight(At("j_kao"), At("j_asi_e_l"), At("j_asi_e_r"));
            Vector3? trunkAxis = At("j_kosi") is { } pelvis && At("j_kubi") is { } neck ? neck - pelvis : null;
            foreach (var target in StructuralMeshFitter.Targets)
            {
                if (!boneIndex.TryGetValue(target.Bone, out var bone) || !boneIndex.TryGetValue(target.Child, out var child) ||
                    !samples.TryGetValue(target.Bone, out var boneSamples))
                {
                    notes.Add($"{target.Bone}: no surface");
                    continue;
                }
                if (StructuralMeshFitter.TryFit(target, referenceHeight, referenceModel[bone], referenceModel[child].Translation,
                        boneSamples, trunkAxis, out var fit, out var reason))
                    fits[target.Bone] = fit;
                else
                    notes.Add($"{target.Bone}: {reason}");
            }
            foreach (var (left, right) in StructuralMeshFitter.Pairs)
                if (StructuralMeshFitter.ValidatePair(fits, left, right) is { } pairReason)
                    notes.Add(pairReason);
        }
        catch (Exception ex)
        {
            fits.Clear();
            notes.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        var summary = $"fitted {fits.Count}/{StructuralMeshFitter.Targets.Length} segment(s) from {models.Count} model(s)" +
                      (notes.Count > 0 ? $"; built-in volumes kept where not fitted ({string.Join("; ", notes)})" : string.Empty);
        return new StructuralFitResult(fits, summary);
    }

    /// <summary>The race deformer the character's collection resolves to, read once per file.</summary>
    private RenderedRaceDeformation? LoadStructuralFitDeformer(nint actor)
    {
        const string path = "chara/xls/boneDeformer/human.pbd";
        if (!TryResolveRenderedActorPath(actor, path, out var resolved))
            return null;
        return StructuralFitDeformers.GetOrAdd(resolved, static file =>
        {
            try
            {
                if (file.StartsWith('|'))
                {
                    var end = file.IndexOf('|', 1);
                    if (end < 0) return null;
                    file = file[(end + 1)..];
                }
                byte[]? bytes;
                if (Path.IsPathRooted(file))
                {
                    var info = new FileInfo(file);
                    bytes = info.Exists && info.Length is > 0 and <= 16777216 ? File.ReadAllBytes(file) : null;
                }
                else bytes = TryGetRawFileData(file);
                return bytes == null ? null : new RenderedRaceDeformation(bytes);
            }
            catch
            {
                return null;
            }
        });
    }

    private static bool TryBuildReferenceModelTransforms(
        SkeletonAccess skel,
        out Matrix4x4[] referenceModel)
    {
        if (skel.HavokSkeleton == null)
        {
            referenceModel = Array.Empty<Matrix4x4>();
            return false;
        }
        return RagdollController.TryBuildReferenceModelTransforms(new FfxivSkeletonPose(0, skel), out referenceModel);
    }
}
