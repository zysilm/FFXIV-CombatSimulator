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
    // Structural mesh fitting owns collision geometry, but never skeleton topology or joint
    // anchors. The body may be offset from its bone: joints stay on the bone pivots while contact,
    // center of mass and inertia follow the weighted character surface.
    private readonly struct StructuralMeshThicknessFit
    {
        public readonly float RadiusX;
        public readonly float RadiusZ;
        public readonly float HalfLength;
        public readonly Vector3 CenterBoneLocal;
        public readonly Vector3 AxisBoneLocal;
        public readonly bool UseBox;
        public readonly int SampleCount;

        public StructuralMeshThicknessFit(
            float radiusX,
            float radiusZ,
            float halfLength,
            Vector3 centerBoneLocal,
            Vector3 axisBoneLocal,
            bool useBox,
            int sampleCount)
        {
            RadiusX = radiusX;
            RadiusZ = radiusZ;
            HalfLength = halfLength;
            CenterBoneLocal = centerBoneLocal;
            AxisBoneLocal = axisBoneLocal;
            UseBox = useBox;
            SampleCount = sampleCount;
        }
    }

    private readonly record struct StructuralMeshTarget(
        string Bone,
        string Child,
        bool UseBox,
        int MinimumSamples,
        string? AdditionalSampleBone = null);

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
        new("j_te_l",    "j_naka_a_l", true,  12),
        new("j_te_r",    "j_naka_a_r", true,  12),
        new("j_asi_a_l", "j_asi_b_l", false, 24),
        new("j_asi_a_r", "j_asi_b_r", false, 24),
        // j_asi_c is a real second knee-region body now (see LogLegBoneStructureDiagnostics), not
        // a skinning helper — fit it as its own segment instead of folding it into j_asi_b's.
        new("j_asi_b_l", "j_asi_c_l", false, 18),
        new("j_asi_b_r", "j_asi_c_r", false, 18),
        new("j_asi_c_l", "j_asi_d_l", false, 24),
        new("j_asi_c_r", "j_asi_d_r", false, 24),
        new("j_asi_d_l", "j_asi_e_l", true,  14),
        new("j_asi_d_r", "j_asi_e_r", true,  14),
    };

    private Dictionary<string, StructuralMeshThicknessFit> BuildStructuralMeshThicknessFits(
        SkeletonAccess skel,
        IReadOnlyDictionary<string, RagdollBoneDef> definitions,
        IReadOnlyDictionary<string, int> activeBoneIndices)
    {
        var result = new Dictionary<string, StructuralMeshThicknessFit>(StringComparer.Ordinal);
        if (skel.CharBase == null || !TryBuildReferenceModelTransforms(skel, out var referenceModel))
        {
            log.Warning("Ragdoll mesh geometry: reference skeleton unavailable; using built-in volumes.");
            return result;
        }

        var targetsByIndex = new Dictionary<int, StructuralMeshTarget>();
        var sampleOwnerByIndex = new Dictionary<int, int>();
        foreach (var target in StructuralMeshTargets)
        {
            if (!definitions.ContainsKey(target.Bone) ||
                !activeBoneIndices.TryGetValue(target.Bone, out var boneIndex) ||
                boneIndex < 0 || boneIndex >= referenceModel.Length)
                continue;
            targetsByIndex[boneIndex] = target;
            sampleOwnerByIndex[boneIndex] = boneIndex;
            if (target.AdditionalSampleBone != null)
            {
                var additionalIndex = boneService.ResolveBoneIndex(skel, target.AdditionalSampleBone);
                if (additionalIndex >= 0 && additionalIndex < referenceModel.Length)
                    sampleOwnerByIndex[additionalIndex] = boneIndex;
            }
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
                if (CollectStructuralMeshSamples(mdl, lodIndex, meshIndex, skel, sampleOwnerByIndex, samples))
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
                    log.Info($"Ragdoll mesh geometry '{target.Bone}': built-in fallback ({rejectionReason ?? "no direct mesh samples"}).");
                continue;
            }

            result[target.Bone] = fit;
        }

        ValidateStructuralMeshPair(result, "j_ude_a_l", "j_ude_a_r");
        ValidateStructuralMeshPair(result, "j_ude_b_l", "j_ude_b_r");
        ValidateStructuralMeshPair(result, "j_te_l", "j_te_r");
        ValidateStructuralMeshPair(result, "j_asi_a_l", "j_asi_a_r");
        ValidateStructuralMeshPair(result, "j_asi_b_l", "j_asi_b_r");
        ValidateStructuralMeshPair(result, "j_asi_d_l", "j_asi_d_r");

        log.Info($"Ragdoll mesh geometry: accepted {result.Count}/{targetsByIndex.Count} structural segment(s) " +
                 $"from {loadedBodyModels} body model(s), {sampledMeshes} mesh(es); anatomical joint topology unchanged.");
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
        IReadOnlyDictionary<int, int> sampleOwnerByIndex,
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
                if (!sampleOwnerByIndex.TryGetValue(havokIndex, out var ownerIndex))
                    continue;

                if (!samples.TryGetValue(ownerIndex, out var boneSamples))
                {
                    boneSamples = new List<SoftTissueMeshSample>();
                    samples.Add(ownerIndex, boneSamples);
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

        // First discard isolated seam/corruption vertices using a broad radial envelope around the
        // bone, then find the robust cross-section center. The collider may be off the skeleton axis;
        // joint anchors remain at bone pivots, while contact and inertia follow this measured center.
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

        var signedXValues = new List<WeightedScalar>(filtered.Count);
        var signedZValues = new List<WeightedScalar>(filtered.Count);
        var longitudinalValues = new List<WeightedScalar>(filtered.Count);
        foreach (var sample in filtered)
        {
            var radial = MathF.Sqrt(sample.Local.X * sample.Local.X + sample.Local.Z * sample.Local.Z);
            if (radial > envelope)
                continue;
            signedXValues.Add(new WeightedScalar(sample.Local.X, sample.Weight));
            signedZValues.Add(new WeightedScalar(sample.Local.Z, sample.Weight));
            longitudinalValues.Add(new WeightedScalar(sample.Local.Y, sample.Weight));
        }
        if (signedXValues.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {signedXValues.Count} inlier samples";
            return false;
        }

        var centerX = WeightedPercentile(signedXValues, 0.50f);
        var centerZ = WeightedPercentile(signedZValues, 0.50f);
        var xValues = new List<WeightedScalar>(signedXValues.Count);
        var zValues = new List<WeightedScalar>(signedXValues.Count);
        var radialValues = new List<WeightedScalar>(signedXValues.Count);
        foreach (var sample in filtered)
        {
            var radialFromBone = MathF.Sqrt(
                sample.Local.X * sample.Local.X + sample.Local.Z * sample.Local.Z);
            if (radialFromBone > envelope)
                continue;
            var dx = sample.Local.X - centerX;
            var dz = sample.Local.Z - centerZ;
            xValues.Add(new WeightedScalar(MathF.Abs(dx), sample.Weight));
            zValues.Add(new WeightedScalar(MathF.Abs(dz), sample.Weight));
            radialValues.Add(new WeightedScalar(MathF.Sqrt(dx * dx + dz * dz), sample.Weight));
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

        var longitudinalLow = WeightedPercentile(longitudinalValues, 0.06f);
        var longitudinalHigh = WeightedPercentile(longitudinalValues, 0.94f);
        var longitudinalSpan = longitudinalHigh - longitudinalLow;
        if (!float.IsFinite(longitudinalSpan) || longitudinalSpan < referenceHeight * 0.015f)
        {
            rejectionReason = $"invalid longitudinal mesh span ({longitudinalSpan:F4}m)";
            return false;
        }

        var centerSection = new Vector3(
            centerX,
            (longitudinalLow + longitudinalHigh) * 0.5f,
            centerZ);
        var radiusForLength = MathF.Max(radiusX, radiusZ);
        var halfLength = target.UseBox
            ? longitudinalSpan * 0.5f
            : MathF.Max(referenceHeight * 0.008f, longitudinalSpan * 0.5f - radiusForLength);
        if (!float.IsFinite(halfLength) || halfLength > referenceHeight * 0.22f)
        {
            rejectionReason = $"invalid mesh half length ({halfLength:F4}m)";
            return false;
        }

        var centerModelOffset = Vector3.Transform(centerSection, sectionRotation);
        var inverseBoneRotation = Quaternion.Inverse(boneRotation);
        var centerBoneLocal = Vector3.Transform(centerModelOffset, inverseBoneRotation);
        var axisBoneLocal = NormalizeOrFallback(Vector3.Transform(axis, inverseBoneRotation), Vector3.UnitY);

        fit = new StructuralMeshThicknessFit(
            radiusX, radiusZ, halfLength, centerBoneLocal, axisBoneLocal,
            target.UseBox, radialValues.Count);
        if (config.RagdollVerboseLog)
        {
            log.Info($"Ragdoll mesh geometry '{target.Bone}': accepted samples={fit.SampleCount}, " +
                     $"x={fit.RadiusX:F4}, z={fit.RadiusZ:F4}, half={fit.HalfLength:F4}, " +
                     $"center=({fit.CenterBoneLocal.X:F3},{fit.CenterBoneLocal.Y:F3},{fit.CenterBoneLocal.Z:F3}), segment={segmentLength:F4}, " +
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

        if (target.Bone.StartsWith("j_asi_d_", StringComparison.Ordinal))
        {
            // A foot is box-like but must not use the torso box envelope. X/Z are the two
            // transverse dimensions around the unchanged ankle-to-toe axis; accept either local
            // ordering because reference bone rolls differ between races and skeleton variants.
            var smaller = MathF.Min(radiusX, radiusZ);
            var larger = MathF.Max(radiusX, radiusZ);
            var minimumSmall = referenceHeight * 0.006f;
            var maximumSmall = referenceHeight * 0.030f;
            var minimumLarge = referenceHeight * 0.012f;
            var maximumLarge = referenceHeight * 0.060f;
            if (smaller < minimumSmall || smaller > maximumSmall ||
                larger < minimumLarge || larger > maximumLarge || larger / smaller > 4f)
            {
                reason = $"foot thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4}; " +
                         $"small={minimumSmall:F4}-{maximumSmall:F4}, large={minimumLarge:F4}-{maximumLarge:F4})";
                return false;
            }
            return true;
        }

        if (target.Bone.StartsWith("j_te_", StringComparison.Ordinal))
        {
            var smaller = MathF.Min(radiusX, radiusZ);
            var larger = MathF.Max(radiusX, radiusZ);
            var minimumSmall = referenceHeight * 0.0035f;
            var maximumSmall = referenceHeight * 0.020f;
            var minimumLarge = referenceHeight * 0.009f;
            var maximumLarge = referenceHeight * 0.045f;
            if (smaller < minimumSmall || smaller > maximumSmall ||
                larger < minimumLarge || larger > maximumLarge || larger / smaller > 5f)
            {
                reason = $"hand thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4}; " +
                         $"small={minimumSmall:F4}-{maximumSmall:F4}, large={minimumLarge:F4}-{maximumLarge:F4})";
                return false;
            }
            return true;
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

        // j_asi_b is now a short (~6cm) proximal stub, not a long limb segment — its radius is
        // wider than it is long, so scaling the envelope BY segmentLength (as the long-limb
        // branch below does) produces a maximum smaller than the stature-based minimum and
        // rejects every measurement. Use a pure stature ratio instead, the same way the foot/hand
        // branches above do for their own short segments.
        if (target.Bone.StartsWith("j_asi_b_", StringComparison.Ordinal))
        {
            var minimumKnee = referenceHeight * 0.018f;
            var maximumKnee = referenceHeight * 0.038f;
            if (radius < minimumKnee || radius > maximumKnee)
            {
                reason = $"upper-knee radius {radius:F4} outside stature envelope {minimumKnee:F4}-{maximumKnee:F4}";
                return false;
            }
            return true;
        }

        float minimumRatio;
        float maximumRatio;
        float minimumStatureRatio;
        if (target.Bone.StartsWith("j_asi_a_", StringComparison.Ordinal))
        {
            minimumRatio = 0.10f;
            maximumRatio = 0.32f;
            minimumStatureRatio = 0.020f;
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
        log.Warning($"Ragdoll mesh geometry: rejected asymmetric pair '{left}'/'{right}' (ratio={ratio:F2}); built-in volumes retained.");
    }
}
