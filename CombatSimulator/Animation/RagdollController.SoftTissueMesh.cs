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
    // A mesh-less helper bone must still have a body so its spring/servo can drive the pose, but
    // there is no honest anatomical volume to infer. Keep that fallback deliberately tiny instead
    // of resurrecting the old ancestor-distance estimator.
    private const float SoftTissueFallbackRadius = 0.006f;
    private const float SoftTissueFallbackHalfLength = 0.004f;
    private const int SoftTissueMeshMinimumSamples = 8;

    private readonly struct SoftTissueMeshFit
    {
        public readonly float Radius;
        public readonly float HalfLength;
        public readonly Vector3 CenterModelOffset;
        public readonly Vector3 AxisModel;
        public readonly int SampleCount;

        public SoftTissueMeshFit(
            float radius,
            float halfLength,
            Vector3 centerModelOffset,
            Vector3 axisModel,
            int sampleCount)
        {
            Radius = radius;
            HalfLength = halfLength;
            CenterModelOffset = centerModelOffset;
            AxisModel = axisModel;
            SampleCount = sampleCount;
        }
    }

    private readonly struct SoftTissueMeshSample
    {
        public readonly Vector3 Position;
        public readonly float Weight;

        public SoftTissueMeshSample(Vector3 position, float weight)
        {
            Position = position;
            Weight = weight;
        }
    }

    private readonly struct WeightedScalar
    {
        public readonly float Value;
        public readonly float Weight;

        public WeightedScalar(float value, float weight)
        {
            Value = value;
            Weight = weight;
        }
    }

    /// <summary>
    /// Fit one capsule per requested skeleton bone from the body mesh vertices carrying that bone's
    /// skinning weight. This reuses the same MDL parsing, bone-table mapping and current-pose
    /// skinning path as mesh collision, but preserves ownership instead of flattening everything
    /// into an anonymous triangle soup.
    /// </summary>
    private Dictionary<int, SoftTissueMeshFit> BuildSoftTissueMeshFits(
        SkeletonAccess skel,
        HashSet<int> targetBoneIndices)
    {
        var result = new Dictionary<int, SoftTissueMeshFit>();
        if (targetBoneIndices.Count == 0 || skel.CharBase == null)
            return result;

        if (!TryBuildSkinDeltas(skel, out var skinDeltas))
        {
            log.Warning("SoftTissue mesh fit: skin transforms unavailable; using conservative proxies.");
            return result;
        }

        var samples = new Dictionary<int, List<SoftTissueMeshSample>>();
        var loadedBodyModels = 0;
        var sampledMeshes = 0;
        var slotCount = Math.Clamp(skel.CharBase->SlotCount, 0, 32);

        for (int slot = 0; slot < slotCount; slot++)
        {
            var renderModel = skel.CharBase->Models == null ? null : skel.CharBase->Models[slot];
            if (renderModel == null || renderModel->ModelResourceHandle == null)
                continue;

            var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
            var modelPath = resourceHandle->FileName.ToString();
            if (!IsBodyModelPath(modelPath))
                continue;

            if (!TryLoadMeshCollisionMdlData("SoftTissue", slot, modelPath, out var mdl) ||
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
            {
                if (CollectSoftTissueMeshSamples(
                        mdl, lodIndex, meshIndex, skel, skinDeltas, targetBoneIndices, samples))
                    sampledMeshes++;
            }
        }

        foreach (var target in targetBoneIndices)
        {
            if (!samples.TryGetValue(target, out var boneSamples) ||
                !TryFitSoftTissueCapsule(skel, target, boneSamples, out var fit))
                continue;

            result[target] = fit;
        }

        if (result.Count > 0 || config.RagdollVerboseLog)
        {
            log.Info($"SoftTissue mesh fit: {result.Count}/{targetBoneIndices.Count} bone(s) fitted " +
                     $"from {loadedBodyModels} body model(s), {sampledMeshes} mesh(es).");
        }
        if (result.Count == 0 && targetBoneIndices.Count > 0)
        {
            log.Warning("SoftTissue mesh fit: no target bone had enough weighted body-mesh " +
                        "vertices; conservative proxies will be used.");
        }

        return result;
    }

    private static bool IsBodyModelPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) ||
            !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalized = modelPath.Replace('\\', '/');
        return normalized.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase);
    }

    private bool CollectSoftTissueMeshSamples(
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        SkeletonAccess skel,
        Matrix4x4[] skinDeltas,
        HashSet<int> targets,
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
        for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            var vertex = ReadMdlCollisionVertex(
                mdl.Data,
                mdl.FileHeader.VertexOffset[lodIndex],
                mesh,
                mdl.VertexDeclarations[meshIndex],
                vertexIndex);
            if (vertex.Position == null || vertex.BlendWeights == null ||
                vertex.BlendIndices == null)
                continue;

            var position = SkinVertex(vertex, localToHavok, skinDeltas);
            if (!IsFinite(position))
                continue;

            var influenceCount = Math.Min(4, vertex.BlendIndices.Length);
            for (int influence = 0; influence < influenceCount; influence++)
            {
                var weight = GetBlendWeight(vertex.BlendWeights.Value, influence);
                if (weight <= 0f)
                    continue;

                var localBoneIndex = vertex.BlendIndices[influence];
                if (localBoneIndex >= localToHavok.Length)
                    continue;

                var havokIndex = localToHavok[localBoneIndex];
                if (havokIndex < 0 || havokIndex >= skinDeltas.Length)
                    continue;

                if (!targets.Contains(havokIndex))
                    continue;

                if (!samples.TryGetValue(havokIndex, out var boneSamples))
                {
                    boneSamples = new List<SoftTissueMeshSample>();
                    samples.Add(havokIndex, boneSamples);
                }
                boneSamples.Add(new SoftTissueMeshSample(position, weight));
                collected = true;
            }
        }

        return collected;
    }

    private bool TryFitSoftTissueCapsule(
        SkeletonAccess skel,
        int boneIndex,
        List<SoftTissueMeshSample> samples,
        out SoftTissueMeshFit fit)
    {
        fit = default;
        if (samples.Count < SoftTissueMeshMinimumSamples ||
            boneIndex < 0 || boneIndex >= skel.BoneCount)
            return false;

        // A vertex with a tiny residual blend weight can be far outside the region this bone
        // actually drives. Keep the meaningful part of this bone's own weight distribution rather
        // than demanding that it beat every neighbouring bone (which would discard many helper
        // bones that are intentionally blended and never become the single dominant influence).
        var maximumWeight = 0f;
        foreach (var sample in samples)
            maximumWeight = MathF.Max(maximumWeight, sample.Weight);
        var minimumMeaningfulWeight = maximumWeight * 0.25f;
        var meaningfulSamples = samples.FindAll(sample => sample.Weight >= minimumMeaningfulWeight);
        if (meaningfulSamples.Count < SoftTissueMeshMinimumSamples)
            return false;

        // First centroid and a robust distance envelope. A corrupt vertex, hidden seam or an
        // unexpectedly shared body submesh must not define the capsule's dimensions.
        if (!TryWeightedCentroid(meaningfulSamples, null, out var initialCenter, out _))
            return false;

        var centerDistances = new List<WeightedScalar>(meaningfulSamples.Count);
        foreach (var sample in meaningfulSamples)
            centerDistances.Add(new WeightedScalar(
                Vector3.Distance(sample.Position, initialCenter), sample.Weight));
        var envelope = WeightedPercentile(centerDistances, 0.95f);
        if (!float.IsFinite(envelope) || envelope <= 0.0005f)
            return false;

        if (!TryWeightedCentroid(meaningfulSamples, envelope, out var center, out var retainedWeight))
            return false;

        var axis = PrincipalAxis(meaningfulSamples, center, envelope, skel, boneIndex);
        if (!IsFinite(axis) || axis.LengthSquared() < 1e-6f)
            return false;
        axis = Vector3.Normalize(axis);

        // Center the capsule between robust axial bounds rather than at an extreme surface-heavy
        // centroid. The radial fit is then evaluated around that final center.
        var axial = new List<WeightedScalar>(meaningfulSamples.Count);
        foreach (var sample in meaningfulSamples)
        {
            if (Vector3.Distance(sample.Position, initialCenter) > envelope)
                continue;
            axial.Add(new WeightedScalar(Vector3.Dot(sample.Position - center, axis), sample.Weight));
        }
        if (axial.Count < SoftTissueMeshMinimumSamples)
            return false;

        var axialMin = WeightedPercentile(axial, 0.05f);
        var axialMax = WeightedPercentile(axial, 0.95f);
        center += axis * ((axialMin + axialMax) * 0.5f);
        var axialExtent = MathF.Max(0f, (axialMax - axialMin) * 0.5f);

        var radial = new List<WeightedScalar>(axial.Count);
        foreach (var sample in meaningfulSamples)
        {
            if (Vector3.Distance(sample.Position, initialCenter) > envelope)
                continue;
            var delta = sample.Position - center;
            var along = Vector3.Dot(delta, axis);
            var radialVector = delta - axis * along;
            radial.Add(new WeightedScalar(radialVector.Length(), sample.Weight));
        }

        var radius = WeightedPercentile(radial, 0.95f);
        if (!float.IsFinite(radius) || radius <= 0.001f)
            return false;

        // BEPU's Capsule length excludes its hemispherical ends. Subtract the radius from the
        // measured axial half-extent so the resulting outer extent matches the vertex cloud.
        var halfLength = MathF.Max(0.002f, axialExtent - radius);

        ref var bonePose = ref skel.Pose->ModelPose.Data[boneIndex];
        var bonePosition = new Vector3(
            bonePose.Translation.X, bonePose.Translation.Y, bonePose.Translation.Z);
        var centerOffset = center - bonePosition;

        // Reject nonsensical clusters rather than clipping them into a plausible-looking but false
        // result. These limits are corruption guards far outside any humanoid soft-tissue region.
        if (radius > 0.25f || halfLength > 0.30f || centerOffset.Length() > 0.50f ||
            retainedWeight <= 0f)
            return false;

        fit = new SoftTissueMeshFit(
            MathF.Max(0.002f, radius),
            halfLength,
            centerOffset,
            axis,
            radial.Count);
        if (config.RagdollVerboseLog)
        {
            var name = skel.HavokSkeleton->Bones[boneIndex].Name.String ?? $"bone_{boneIndex}";
            log.Info($"SoftTissue mesh fit '{name}': samples={radial.Count}, " +
                     $"radius={fit.Radius:F4}, halfLength={fit.HalfLength:F4}, " +
                     $"center=({centerOffset.X:F4},{centerOffset.Y:F4},{centerOffset.Z:F4})");
        }
        return true;
    }

    private static bool TryWeightedCentroid(
        List<SoftTissueMeshSample> samples,
        float? maximumDistanceFromInitialCenter,
        out Vector3 center,
        out float totalWeight)
    {
        center = Vector3.Zero;
        totalWeight = 0f;

        Vector3 initialCenter = Vector3.Zero;
        if (maximumDistanceFromInitialCenter.HasValue)
        {
            var initialWeight = 0f;
            foreach (var sample in samples)
            {
                initialCenter += sample.Position * sample.Weight;
                initialWeight += sample.Weight;
            }
            if (initialWeight <= 1e-5f)
                return false;
            initialCenter /= initialWeight;
        }

        foreach (var sample in samples)
        {
            if (maximumDistanceFromInitialCenter.HasValue &&
                Vector3.Distance(sample.Position, initialCenter) > maximumDistanceFromInitialCenter.Value)
                continue;
            center += sample.Position * sample.Weight;
            totalWeight += sample.Weight;
        }

        if (totalWeight <= 1e-5f)
            return false;
        center /= totalWeight;
        return IsFinite(center);
    }

    private static Vector3 PrincipalAxis(
        List<SoftTissueMeshSample> samples,
        Vector3 center,
        float envelope,
        SkeletonAccess skel,
        int boneIndex)
    {
        var seed = ResolveSkeletonAxis(skel, boneIndex);
        var xx = 0f; var xy = 0f; var xz = 0f;
        var yy = 0f; var yz = 0f; var zz = 0f;
        foreach (var sample in samples)
        {
            var delta = sample.Position - center;
            if (delta.Length() > envelope)
                continue;
            var w = sample.Weight;
            xx += delta.X * delta.X * w;
            xy += delta.X * delta.Y * w;
            xz += delta.X * delta.Z * w;
            yy += delta.Y * delta.Y * w;
            yz += delta.Y * delta.Z * w;
            zz += delta.Z * delta.Z * w;
        }

        var axis = NormalizeOrFallback(seed, Vector3.UnitY);
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var next = new Vector3(
                xx * axis.X + xy * axis.Y + xz * axis.Z,
                xy * axis.X + yy * axis.Y + yz * axis.Z,
                xz * axis.X + yz * axis.Y + zz * axis.Z);
            if (next.LengthSquared() < 1e-8f)
                break;
            axis = Vector3.Normalize(next);
        }

        // PCA sign is arbitrary; keep it aligned with skeleton topology so activation is stable.
        if (Vector3.Dot(axis, seed) < 0f)
            axis = -axis;
        return axis;
    }

    private static Vector3 ResolveSkeletonAxis(SkeletonAccess skel, int boneIndex)
    {
        ref var bonePose = ref skel.Pose->ModelPose.Data[boneIndex];
        var bonePosition = new Vector3(
            bonePose.Translation.X, bonePose.Translation.Y, bonePose.Translation.Z);

        var bestChildDistance = 0f;
        var bestChildAxis = Vector3.Zero;
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int child = 0; child < count; child++)
        {
            if (skel.HavokSkeleton->ParentIndices[child] != boneIndex)
                continue;
            ref var childPose = ref skel.Pose->ModelPose.Data[child];
            var childPosition = new Vector3(
                childPose.Translation.X, childPose.Translation.Y, childPose.Translation.Z);
            var delta = childPosition - bonePosition;
            if (delta.LengthSquared() > bestChildDistance)
            {
                bestChildDistance = delta.LengthSquared();
                bestChildAxis = delta;
            }
        }
        if (bestChildDistance > 1e-6f)
            return Vector3.Normalize(bestChildAxis);

        if (boneIndex < skel.ParentCount)
        {
            var parent = skel.HavokSkeleton->ParentIndices[boneIndex];
            if (parent >= 0 && parent < skel.BoneCount)
            {
                ref var parentPose = ref skel.Pose->ModelPose.Data[parent];
                var parentPosition = new Vector3(
                    parentPose.Translation.X, parentPose.Translation.Y, parentPose.Translation.Z);
                var delta = bonePosition - parentPosition;
                if (delta.LengthSquared() > 1e-6f)
                    return Vector3.Normalize(delta);
            }
        }

        var rotation = new Quaternion(
            bonePose.Rotation.X, bonePose.Rotation.Y, bonePose.Rotation.Z, bonePose.Rotation.W);
        return Vector3.Transform(Vector3.UnitY, rotation);
    }

    private static float WeightedPercentile(List<WeightedScalar> values, float percentile)
    {
        if (values.Count == 0)
            return 0f;

        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        var totalWeight = 0f;
        foreach (var value in values)
            totalWeight += MathF.Max(0f, value.Weight);
        if (totalWeight <= 1e-6f)
            return values[values.Count / 2].Value;

        var threshold = Math.Clamp(percentile, 0f, 1f) * totalWeight;
        var accumulated = 0f;
        foreach (var value in values)
        {
            accumulated += MathF.Max(0f, value.Weight);
            if (accumulated >= threshold)
                return value.Value;
        }
        return values[^1].Value;
    }
}
