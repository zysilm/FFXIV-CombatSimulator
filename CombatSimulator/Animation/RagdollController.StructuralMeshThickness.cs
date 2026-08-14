// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // Structural mesh fitting is deliberately much narrower than soft-tissue fitting. It may
    // replace transverse flesh thickness, but it must never become a second skeleton builder.
    // In particular, this result contains no center, axis, segment length, parent or joint data.
    private readonly struct StructuralMeshThicknessFit
    {
        public readonly float RadiusX;
        public readonly float RadiusZ;
        public readonly bool UseBox;
        public readonly int SampleCount;

        public StructuralMeshThicknessFit(float radiusX, float radiusZ, bool useBox, int sampleCount)
        {
            RadiusX = radiusX;
            RadiusZ = radiusZ;
            UseBox = useBox;
            SampleCount = sampleCount;
        }
    }

    private readonly record struct StructuralMeshTarget(
        string Bone,
        string Child,
        bool UseBox,
        int MinimumSamples);

    private static readonly StructuralMeshTarget[] StructuralMeshTargets =
    {
        new("j_kosi",    "j_sebo_a", true,  30),
        new("j_sebo_a",  "j_sebo_b", true,  30),
        new("j_sebo_b",  "j_sebo_c", true,  30),
        new("j_sebo_c",  "j_kubi",   true,  30),
        new("j_ude_a_l", "j_ude_b_l", false, 18),
        new("j_ude_a_r", "j_ude_b_r", false, 18),
        new("j_ude_b_l", "j_te_l",    false, 18),
        new("j_ude_b_r", "j_te_r",    false, 18),
        new("j_asi_a_l", "j_asi_b_l", false, 24),
        new("j_asi_a_r", "j_asi_b_r", false, 24),
        new("j_asi_b_l", "j_asi_c_l", false, 24),
        new("j_asi_b_r", "j_asi_c_r", false, 24),
    };

    private Dictionary<string, StructuralMeshThicknessFit> BuildStructuralMeshThicknessFits(
        SkeletonAccess skel,
        IReadOnlyDictionary<string, RagdollBoneDef> definitions,
        IReadOnlyDictionary<string, int> activeBoneIndices)
    {
        var result = new Dictionary<string, StructuralMeshThicknessFit>(StringComparer.Ordinal);
        if (skel.CharBase == null || !TryBuildReferenceModelTransforms(skel, out var referenceModel))
        {
            log.Warning("Ragdoll mesh thickness: reference skeleton unavailable; using configured volumes.");
            return result;
        }

        var targetsByIndex = new Dictionary<int, StructuralMeshTarget>();
        foreach (var target in StructuralMeshTargets)
        {
            if (!definitions.ContainsKey(target.Bone) ||
                !activeBoneIndices.TryGetValue(target.Bone, out var boneIndex) ||
                boneIndex < 0 || boneIndex >= referenceModel.Length)
                continue;
            targetsByIndex[boneIndex] = target;
        }
        if (targetsByIndex.Count == 0)
            return result;

        var referenceHeight = EstimateStructuralReferenceHeight(skel, referenceModel);

        var samples = new Dictionary<int, List<SoftTissueMeshSample>>();
        var loadedBodyModels = 0;
        var sampledMeshes = 0;
        var slotCount = Math.Clamp(skel.CharBase->SlotCount, 0, 32);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var renderModel = skel.CharBase->Models == null ? null : skel.CharBase->Models[slot];
            if (renderModel == null || renderModel->ModelResourceHandle == null)
                continue;

            var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
            var modelPath = resourceHandle->FileName.ToString();
            if (!IsBodyModelPath(modelPath))
                continue;
            if (!TryLoadMeshCollisionMdlData("Ragdoll mesh thickness", slot, modelPath, out var mdl) ||
                !TrySelectMdlLod(mdl, out var lodIndex, out var lod))
                continue;

            loadedBodyModels++;
            var meshIndices = new HashSet<int>();
            AddAnimatedMeshRange(mdl, lod.MeshIndex, lod.MeshCount, meshIndices);
            if (mdl.ExtraLodEnabled && lodIndex < mdl.ExtraLods.Length)
            {
                var extra = mdl.ExtraLods[lodIndex];
                AddAnimatedMeshRange(mdl, extra.GlassMeshIndex, extra.GlassMeshCount, meshIndices);
                AddAnimatedMeshRange(mdl, extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount, meshIndices);
                AddAnimatedMeshRange(mdl, extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount, meshIndices);
            }

            foreach (var meshIndex in meshIndices)
                if (CollectStructuralMeshSamples(mdl, lodIndex, meshIndex, skel, targetsByIndex, samples))
                    sampledMeshes++;
        }

        foreach (var (boneIndex, target) in targetsByIndex)
        {
            string? rejectionReason = null;
            if (!samples.TryGetValue(boneIndex, out var boneSamples) ||
                !activeBoneIndices.TryGetValue(target.Child, out var childIndex) ||
                childIndex < 0 || childIndex >= referenceModel.Length ||
                !TryFitStructuralMeshThickness(
                    target, referenceHeight, boneIndex, childIndex, referenceModel, boneSamples, out var fit,
                    out rejectionReason))
            {
                if (config.RagdollVerboseLog)
                    log.Info($"Ragdoll mesh thickness '{target.Bone}': configured fallback ({rejectionReason ?? "no direct mesh samples"}).");
                continue;
            }

            result[target.Bone] = fit;
        }

        ValidateStructuralMeshPair(result, "j_ude_a_l", "j_ude_a_r");
        ValidateStructuralMeshPair(result, "j_ude_b_l", "j_ude_b_r");
        ValidateStructuralMeshPair(result, "j_asi_a_l", "j_asi_a_r");
        ValidateStructuralMeshPair(result, "j_asi_b_l", "j_asi_b_r");

        log.Info($"Ragdoll mesh thickness: accepted {result.Count}/{targetsByIndex.Count} structural segment(s) " +
                 $"from {loadedBodyModels} body model(s), {sampledMeshes} mesh(es); topology and segment geometry unchanged.");
        return result;
    }

    private float EstimateStructuralReferenceHeight(SkeletonAccess skel, Matrix4x4[] referenceModel)
    {
        var head = boneService.ResolveBoneIndex(skel, "j_kao");
        var leftFoot = boneService.ResolveBoneIndex(skel, "j_asi_e_l");
        var rightFoot = boneService.ResolveBoneIndex(skel, "j_asi_e_r");
        if (head >= 0 && head < referenceModel.Length &&
            leftFoot >= 0 && leftFoot < referenceModel.Length &&
            rightFoot >= 0 && rightFoot < referenceModel.Length)
        {
            var feet = (referenceModel[leftFoot].Translation + referenceModel[rightFoot].Translation) * 0.5f;
            var boneHeight = Vector3.Distance(referenceModel[head].Translation, feet);
            if (float.IsFinite(boneHeight) && boneHeight is >= 0.80f and <= 3.0f)
                return boneHeight + 0.12f;
        }

        // Used only as a corruption guard when the canonical endpoints are absent. It does not
        // define a collision volume; every accepted dimension still comes from mesh samples.
        return 1.60f;
    }

    private static bool TryBuildReferenceModelTransforms(
        SkeletonAccess skel,
        out Matrix4x4[] referenceModel)
    {
        referenceModel = Array.Empty<Matrix4x4>();
        if (skel.HavokSkeleton == null || skel.HavokSkeleton->ReferencePose.Data == null)
            return false;

        var count = Math.Min(skel.BoneCount,
            Math.Min(skel.ParentCount, skel.HavokSkeleton->ReferencePose.Length));
        if (count <= 0)
            return false;

        referenceModel = new Matrix4x4[count];
        for (var i = 0; i < count; i++)
        {
            var local = QsToMatrix(skel.HavokSkeleton->ReferencePose.Data[i]);
            var parent = skel.HavokSkeleton->ParentIndices[i];
            referenceModel[i] = parent >= 0 && parent < i
                ? local * referenceModel[parent]
                : local;
        }
        return true;
    }

    private bool CollectStructuralMeshSamples(
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        SkeletonAccess skel,
        IReadOnlyDictionary<int, StructuralMeshTarget> targets,
        Dictionary<int, List<SoftTissueMeshSample>> samples)
    {
        if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length ||
            meshIndex >= mdl.VertexDeclarations.Length ||
            mdl.FileHeader.VertexOffset == null || lodIndex < 0 ||
            lodIndex >= mdl.FileHeader.VertexOffset.Length)
            return false;

        var mesh = mdl.Meshes[meshIndex];
        if (mesh.VertexCount == 0)
            return false;

        var localToHavok = BuildMdlMeshBoneMap(mdl, mesh, skel);
        if (localToHavok.Length == 0)
            return false;

        var collected = false;
        for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            var vertex = ReadMdlCollisionVertex(
                mdl.Data, mdl.FileHeader.VertexOffset[lodIndex], mesh,
                mdl.VertexDeclarations[meshIndex], vertexIndex);
            if (vertex.Position == null || vertex.BlendWeights == null || vertex.BlendIndices == null)
                continue;

            var p = vertex.Position.Value;
            var bindPosition = new Vector3(p.X, p.Y, p.Z);
            if (!IsFinite(bindPosition))
                continue;

            var influenceCount = Math.Min(4, vertex.BlendIndices.Length);
            for (var influence = 0; influence < influenceCount; influence++)
            {
                var weight = GetBlendWeight(vertex.BlendWeights.Value, influence);
                if (weight <= 0f)
                    continue;
                var localIndex = vertex.BlendIndices[influence];
                if (localIndex >= localToHavok.Length)
                    continue;
                var havokIndex = localToHavok[localIndex];
                if (!targets.ContainsKey(havokIndex))
                    continue;

                if (!samples.TryGetValue(havokIndex, out var boneSamples))
                {
                    boneSamples = new List<SoftTissueMeshSample>();
                    samples.Add(havokIndex, boneSamples);
                }
                boneSamples.Add(new SoftTissueMeshSample(bindPosition, weight));
                collected = true;
            }
        }
        return collected;
    }

    private bool TryFitStructuralMeshThickness(
        StructuralMeshTarget target,
        float referenceHeight,
        int boneIndex,
        int childIndex,
        Matrix4x4[] referenceModel,
        List<SoftTissueMeshSample> samples,
        out StructuralMeshThicknessFit fit,
        out string? rejectionReason)
    {
        fit = default;
        rejectionReason = null;
        if (samples.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {samples.Count} weighted samples";
            return false;
        }

        var maximumWeight = 0f;
        foreach (var sample in samples)
            maximumWeight = MathF.Max(maximumWeight, sample.Weight);
        var minimumWeight = MathF.Max(0.12f, maximumWeight * 0.30f);

        var boneOrigin = referenceModel[boneIndex].Translation;
        var childOrigin = referenceModel[childIndex].Translation;
        var segment = childOrigin - boneOrigin;
        var segmentLength = segment.Length();
        if (!target.UseBox && segmentLength < 0.04f)
        {
            rejectionReason = $"reference segment too short ({segmentLength:F4}m)";
            return false;
        }

        Vector3 axis;
        if (segmentLength >= 0.01f)
            axis = segment / segmentLength;
        else
            axis = NormalizeOrFallback(Vector3.TransformNormal(Vector3.UnitY, referenceModel[boneIndex]), Vector3.UnitY);

        if (!Matrix4x4.Decompose(referenceModel[boneIndex], out _, out var boneRotation, out _))
            boneRotation = Quaternion.Identity;
        var sectionRotation = CreateCapsuleRotation(axis, boneRotation);
        var inverseSectionRotation = Quaternion.Inverse(sectionRotation);

        var filtered = new List<(Vector3 Local, float Weight)>(samples.Count);
        var longitudinalMin = float.MaxValue;
        var longitudinalMax = float.MinValue;
        foreach (var sample in samples)
        {
            if (sample.Weight < minimumWeight)
                continue;
            var delta = sample.Position - boneOrigin;
            var along = Vector3.Dot(delta, axis);
            if (segmentLength >= 0.01f)
            {
                var t = along / segmentLength;
                var minimumT = target.UseBox ? -0.10f : 0.08f;
                var maximumT = target.UseBox ? 1.05f : 0.92f;
                if (t < minimumT || t > maximumT)
                    continue;
                longitudinalMin = MathF.Min(longitudinalMin, t);
                longitudinalMax = MathF.Max(longitudinalMax, t);
            }
            var local = Vector3.Transform(delta, inverseSectionRotation);
            filtered.Add((local, sample.Weight));
        }

        if (filtered.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {filtered.Count} meaningful central samples";
            return false;
        }
        if (!target.UseBox && longitudinalMax - longitudinalMin < 0.35f)
        {
            rejectionReason = $"insufficient segment coverage ({longitudinalMax - longitudinalMin:F2})";
            return false;
        }

        // First discard isolated seam/corruption vertices using a radial envelope, then measure a
        // symmetric cross-section around the unchanged skeleton axis. There is intentionally no
        // centroid calculation here: a measured center must never move a structural body.
        var rawRadial = new List<WeightedScalar>(filtered.Count);
        foreach (var sample in filtered)
            rawRadial.Add(new WeightedScalar(
                MathF.Sqrt(sample.Local.X * sample.Local.X + sample.Local.Z * sample.Local.Z),
                sample.Weight));
        var envelope = WeightedPercentile(rawRadial, 0.97f);
        if (!float.IsFinite(envelope) || envelope <= 0.005f)
        {
            rejectionReason = "invalid radial envelope";
            return false;
        }

        var xValues = new List<WeightedScalar>(filtered.Count);
        var zValues = new List<WeightedScalar>(filtered.Count);
        var radialValues = new List<WeightedScalar>(filtered.Count);
        foreach (var sample in filtered)
        {
            var radial = MathF.Sqrt(sample.Local.X * sample.Local.X + sample.Local.Z * sample.Local.Z);
            if (radial > envelope)
                continue;
            xValues.Add(new WeightedScalar(MathF.Abs(sample.Local.X), sample.Weight));
            zValues.Add(new WeightedScalar(MathF.Abs(sample.Local.Z), sample.Weight));
            radialValues.Add(new WeightedScalar(radial, sample.Weight));
        }
        if (radialValues.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {radialValues.Count} inlier samples";
            return false;
        }

        float radiusX;
        float radiusZ;
        if (target.UseBox)
        {
            radiusX = WeightedPercentile(xValues, 0.90f) * 0.98f;
            radiusZ = WeightedPercentile(zValues, 0.90f) * 0.98f;
        }
        else
        {
            var radius = WeightedPercentile(radialValues, 0.90f) * 0.98f;
            radiusX = radius;
            radiusZ = radius;
        }

        if (!ValidateStructuralMeshDimensions(
                target, referenceHeight, segmentLength, radiusX, radiusZ, out rejectionReason))
            return false;

        fit = new StructuralMeshThicknessFit(radiusX, radiusZ, target.UseBox, radialValues.Count);
        if (config.RagdollVerboseLog)
        {
            log.Info($"Ragdoll mesh thickness '{target.Bone}': accepted samples={fit.SampleCount}, " +
                     $"x={fit.RadiusX:F4}, z={fit.RadiusZ:F4}, segment={segmentLength:F4}, " +
                     $"referenceHeight={referenceHeight:F3}.");
        }
        return true;
    }

    private static bool ValidateStructuralMeshDimensions(
        StructuralMeshTarget target,
        float referenceHeight,
        float segmentLength,
        float radiusX,
        float radiusZ,
        out string? reason)
    {
        reason = null;
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusZ) || radiusX <= 0f || radiusZ <= 0f)
        {
            reason = "non-finite thickness";
            return false;
        }

        if (target.UseBox)
        {
            var minimumX = referenceHeight * 0.035f;
            var maximumX = referenceHeight * 0.115f;
            var minimumZ = referenceHeight * 0.030f;
            var maximumZ = referenceHeight * 0.095f;
            if (radiusX < minimumX || radiusZ < minimumZ ||
                radiusX > maximumX || radiusZ > maximumZ)
            {
                reason = $"torso thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4}; " +
                         $"x={minimumX:F4}-{maximumX:F4}, z={minimumZ:F4}-{maximumZ:F4})";
                return false;
            }
            var aspect = radiusX / radiusZ;
            if (aspect < 0.50f || aspect > 2.0f)
            {
                reason = $"torso aspect ratio {aspect:F2} is implausible";
                return false;
            }
            return true;
        }

        var radius = (radiusX + radiusZ) * 0.5f;
        float minimumRatio;
        float maximumRatio;
        float minimumStatureRatio;
        if (target.Bone.StartsWith("j_asi_a_", StringComparison.Ordinal))
        {
            minimumRatio = 0.10f;
            maximumRatio = 0.32f;
            minimumStatureRatio = 0.020f;
        }
        else if (target.Bone.StartsWith("j_asi_b_", StringComparison.Ordinal))
        {
            minimumRatio = 0.07f;
            maximumRatio = 0.26f;
            minimumStatureRatio = 0.015f;
        }
        else
        {
            minimumRatio = 0.06f;
            maximumRatio = 0.27f;
            minimumStatureRatio = 0.011f;
        }

        var minimum = MathF.Max(referenceHeight * minimumStatureRatio, segmentLength * minimumRatio);
        var maximum = MathF.Min(referenceHeight * 0.055f, segmentLength * maximumRatio);
        if (maximum <= minimum || radius < minimum || radius > maximum)
        {
            reason = $"limb radius {radius:F4} outside safety envelope {minimum:F4}-{maximum:F4}";
            return false;
        }
        return true;
    }

    private void ValidateStructuralMeshPair(
        Dictionary<string, StructuralMeshThicknessFit> fits,
        string left,
        string right)
    {
        if (!fits.TryGetValue(left, out var leftFit) || !fits.TryGetValue(right, out var rightFit))
        {
            // A one-sided result is more likely a missing/modified mesh partition than anatomy.
            fits.Remove(left);
            fits.Remove(right);
            return;
        }

        var leftRadius = (leftFit.RadiusX + leftFit.RadiusZ) * 0.5f;
        var rightRadius = (rightFit.RadiusX + rightFit.RadiusZ) * 0.5f;
        var ratio = MathF.Max(leftRadius, rightRadius) / MathF.Max(0.001f, MathF.Min(leftRadius, rightRadius));
        if (ratio <= 1.22f)
            return;

        fits.Remove(left);
        fits.Remove(right);
        log.Warning($"Ragdoll mesh thickness: rejected asymmetric pair '{left}'/'{right}' (ratio={ratio:F2}); configured volumes retained.");
    }
}
