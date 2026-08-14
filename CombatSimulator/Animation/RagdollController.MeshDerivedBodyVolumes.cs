// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using CombatSimulator.Animation.SurfaceProfiles;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private const int StructuralMeshMinimumSamples = 10;
    private const float StructuralMeshMinimumOwnerWeight = 0.15f;
    private const float StructuralMeshSkinMargin = 0.004f;

    private enum MeshDerivedVolumeKind
    {
        Capsule,
        Box,
    }

    /// <summary>
    /// One immutable, model-space structural volume.  Geometry is measured from the rendered
    /// flesh mesh, while orientation is constrained by the skeleton so a death pose cannot rotate
    /// a thigh proxy sideways merely because PCA found a wider cross-section than longitudinal
    /// extent.  The center offset is measured from the owning bone origin.
    /// </summary>
    private readonly struct MeshDerivedBodyVolume
    {
        public readonly string Name;
        public readonly MeshDerivedVolumeKind Kind;
        public readonly Vector3 CenterModelOffset;
        public readonly Vector3 AxisModel;
        public readonly float Radius;
        public readonly float HalfLength;
        public readonly Vector3 BoxHalfExtents;
        public readonly int SampleCount;

        public MeshDerivedBodyVolume(
            string name,
            MeshDerivedVolumeKind kind,
            Vector3 centerModelOffset,
            Vector3 axisModel,
            float radius,
            float halfLength,
            Vector3 boxHalfExtents,
            int sampleCount)
        {
            Name = name;
            Kind = kind;
            CenterModelOffset = centerModelOffset;
            AxisModel = axisModel;
            Radius = radius;
            HalfLength = halfLength;
            BoxHalfExtents = boxHalfExtents;
            SampleCount = sampleCount;
        }
    }

    private sealed class MeshDerivedBodyVolumeRig
    {
        private readonly Dictionary<string, MeshDerivedBodyVolume> byName;

        public MeshDerivedBodyVolumeRig(Dictionary<string, MeshDerivedBodyVolume> volumes)
        {
            byName = new Dictionary<string, MeshDerivedBodyVolume>(volumes, StringComparer.Ordinal);
        }

        public int Count => byName.Count;

        public bool TryGet(string name, out MeshDerivedBodyVolume volume)
            => byName.TryGetValue(name, out volume);
    }

    /// <summary>
    /// Build the structural proxy rig from the current body/face mesh snapshot.  Skin influences
    /// owned by helper bones are folded into their nearest simulated structural ancestor.  This is
    /// important for modded bodies: breasts, abdomen helpers, glute helpers and face bones would
    /// otherwise leave holes in the core torso/head clouds.
    /// </summary>
    private MeshDerivedBodyVolumeRig BuildMeshDerivedBodyVolumeRig(
        SkeletonAccess skel,
        IReadOnlyList<RagdollBoneDef> defs,
        IReadOnlyDictionary<string, int> nameToIndex)
    {
        var volumes = new Dictionary<string, MeshDerivedBodyVolume>(StringComparer.Ordinal);
        if (skel.CharBase == null || !TryBuildSkinDeltas(skel, out var skinDeltas))
        {
            log.Warning("Mesh-derived body volumes: skin transforms unavailable; using Ragdoll Advanced fallbacks.");
            return new MeshDerivedBodyVolumeRig(volumes);
        }

        var targetDefs = new Dictionary<int, RagdollBoneDef>();
        foreach (var def in defs)
        {
            if (def.SoftBody || def.AnatomicalRole is AnatomicalRole.Cloth or AnatomicalRole.Weapon ||
                !nameToIndex.TryGetValue(def.Name, out var boneIndex))
                continue;
            targetDefs[boneIndex] = def;
        }
        if (targetDefs.Count == 0)
            return new MeshDerivedBodyVolumeRig(volumes);

        var ownerByBone = BuildStructuralOwnerMap(skel, targetDefs);
        var samples = new Dictionary<int, List<SoftTissueMeshSample>>();
        var loadedModels = 0;
        var sampledMeshes = 0;
        var slotCount = Math.Clamp(skel.CharBase->SlotCount, 0, 32);

        for (var slot = 0; slot < slotCount; slot++)
        {
            var renderModel = skel.CharBase->Models == null ? null : skel.CharBase->Models[slot];
            if (renderModel == null || renderModel->ModelResourceHandle == null)
                continue;

            var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
            var modelPath = resourceHandle->FileName.ToString();
            if (!IsStructuralFleshModelPath(modelPath))
                continue;

            if (!TryLoadMeshCollisionMdlData("BodyVolume", slot, modelPath, out var mdl) ||
                !TrySelectMdlLod(mdl, out var lodIndex, out var lod))
                continue;

            loadedModels++;
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
                if (CollectStructuralMeshSamples(
                        mdl, lodIndex, meshIndex, skel, skinDeltas, ownerByBone, samples))
                    sampledMeshes++;
            }
        }

        foreach (var (boneIndex, def) in targetDefs)
        {
            if (!samples.TryGetValue(boneIndex, out var boneSamples) ||
                !TryFitStructuralBodyVolume(skel, boneIndex, def, boneSamples, out var volume))
                continue;
            volumes[def.Name] = volume;
        }

        log.Info($"Mesh-derived body volumes: fitted {volumes.Count}/{targetDefs.Count} structural bone(s) " +
                 $"from {loadedModels} flesh model(s), {sampledMeshes} mesh(es); " +
                 $"{targetDefs.Count - volumes.Count} use Ragdoll Advanced fallback geometry.");
        return new MeshDerivedBodyVolumeRig(volumes);
    }

    private static int[] BuildStructuralOwnerMap(
        SkeletonAccess skel,
        IReadOnlyDictionary<int, RagdollBoneDef> targetDefs)
    {
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        var ownerByBone = new int[count];
        Array.Fill(ownerByBone, -1);
        for (var bone = 0; bone < count; bone++)
        {
            var cursor = bone;
            while (cursor >= 0 && cursor < count)
            {
                if (targetDefs.ContainsKey(cursor))
                {
                    ownerByBone[bone] = cursor;
                    break;
                }
                cursor = skel.HavokSkeleton->ParentIndices[cursor];
            }
        }
        return ownerByBone;
    }

    private static bool IsStructuralFleshModelPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) ||
            !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalized = modelPath.Replace('\\', '/');
        return normalized.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase);
    }

    private bool CollectStructuralMeshSamples(
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        SkeletonAccess skel,
        Matrix4x4[] skinDeltas,
        int[] ownerByBone,
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
        Span<int> owners = stackalloc int[4];
        Span<float> ownerWeights = stackalloc float[4];
        for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            var vertex = ReadMdlCollisionVertex(
                mdl.Data,
                mdl.FileHeader.VertexOffset[lodIndex],
                mesh,
                mdl.VertexDeclarations[meshIndex],
                vertexIndex);
            if (vertex.Position == null || vertex.BlendWeights == null || vertex.BlendIndices == null)
                continue;

            owners.Fill(-1);
            ownerWeights.Clear();
            var ownerCount = 0;
            var influenceCount = Math.Min(4, vertex.BlendIndices.Length);
            for (var influence = 0; influence < influenceCount; influence++)
            {
                var weight = GetBlendWeight(vertex.BlendWeights.Value, influence);
                if (weight <= 0f)
                    continue;
                var localBone = vertex.BlendIndices[influence];
                if (localBone >= localToHavok.Length)
                    continue;
                var havokBone = localToHavok[localBone];
                if (havokBone < 0 || havokBone >= ownerByBone.Length)
                    continue;
                var owner = ownerByBone[havokBone];
                if (owner < 0)
                    continue;

                var slot = -1;
                for (var i = 0; i < ownerCount; i++)
                {
                    if (owners[i] == owner)
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot < 0)
                {
                    slot = ownerCount++;
                    owners[slot] = owner;
                }
                ownerWeights[slot] += weight;
            }

            var bestOwner = -1;
            var bestWeight = 0f;
            for (var i = 0; i < ownerCount; i++)
            {
                if (ownerWeights[i] <= bestWeight)
                    continue;
                bestOwner = owners[i];
                bestWeight = ownerWeights[i];
            }
            if (bestOwner < 0 || bestWeight < StructuralMeshMinimumOwnerWeight)
                continue;

            var position = SkinVertex(vertex, localToHavok, skinDeltas);
            if (!IsFinite(position))
                continue;
            if (!samples.TryGetValue(bestOwner, out var boneSamples))
            {
                boneSamples = new List<SoftTissueMeshSample>();
                samples.Add(bestOwner, boneSamples);
            }
            boneSamples.Add(new SoftTissueMeshSample(position, bestWeight));
            collected = true;
        }

        return collected;
    }

    private bool TryFitStructuralBodyVolume(
        SkeletonAccess skel,
        int boneIndex,
        RagdollBoneDef def,
        List<SoftTissueMeshSample> samples,
        out MeshDerivedBodyVolume volume)
    {
        volume = default;
        if (samples.Count < StructuralMeshMinimumSamples ||
            boneIndex < 0 || boneIndex >= skel.BoneCount)
            return false;

        ref var bonePose = ref skel.Pose->ModelPose.Data[boneIndex];
        var bonePosition = new Vector3(
            bonePose.Translation.X, bonePose.Translation.Y, bonePose.Translation.Z);
        var boneRotation = Quaternion.Normalize(new Quaternion(
            bonePose.Rotation.X, bonePose.Rotation.Y, bonePose.Rotation.Z, bonePose.Rotation.W));
        var axis = NormalizeOrFallback(ResolveSkeletonAxis(skel, boneIndex), Vector3.UnitY);
        var fitRotation = CreateCapsuleRotation(axis, boneRotation);
        var inverseFitRotation = Quaternion.Inverse(fitRotation);

        var localSamples = new List<(Vector3 Position, float Weight)>(samples.Count);
        var xs = new List<WeightedScalar>(samples.Count);
        var ys = new List<WeightedScalar>(samples.Count);
        var zs = new List<WeightedScalar>(samples.Count);
        foreach (var sample in samples)
        {
            var local = Vector3.Transform(sample.Position - bonePosition, inverseFitRotation);
            if (!IsFinite(local))
                continue;
            var weight = MathF.Max(0.001f, sample.Weight);
            localSamples.Add((local, weight));
            xs.Add(new WeightedScalar(local.X, weight));
            ys.Add(new WeightedScalar(local.Y, weight));
            zs.Add(new WeightedScalar(local.Z, weight));
        }
        if (localSamples.Count < StructuralMeshMinimumSamples)
            return false;

        var xMin = WeightedPercentile(xs, 0.03f);
        var xMax = WeightedPercentile(xs, 0.97f);
        var yMin = WeightedPercentile(ys, 0.02f);
        var yMax = WeightedPercentile(ys, 0.98f);
        var zMin = WeightedPercentile(zs, 0.03f);
        var zMax = WeightedPercentile(zs, 0.97f);
        if (xMax <= xMin || yMax <= yMin || zMax <= zMin)
            return false;

        var localCenter = new Vector3(
            (xMin + xMax) * 0.5f,
            (yMin + yMax) * 0.5f,
            (zMin + zMax) * 0.5f);
        var segmentLength = ResolveStructuralSegmentLength(skel, boneIndex);
        var kind = StructuralVolumeKind(def);

        if (kind == MeshDerivedVolumeKind.Box)
        {
            var extents = new Vector3(
                (xMax - xMin) * 0.5f + StructuralMeshSkinMargin,
                (yMax - yMin) * 0.5f + StructuralMeshSkinMargin,
                (zMax - zMin) * 0.5f + StructuralMeshSkinMargin);
            // Adjacent ownership regions meet at a skinning blend, not at a hard anatomical cut.
            // Give the simplified proxies a small axial overlap so the continuous flesh surface
            // does not turn into a staircase with gaps at hips, spine slices, wrists or ankles.
            extents.Y += Math.Clamp(segmentLength * 0.06f, 0.003f, 0.015f);
            extents = Vector3.Max(extents, StructuralMinimumBoxExtents(def, segmentLength));
            if (!IsFinite(extents) || extents.X > 0.45f || extents.Y > 0.45f || extents.Z > 0.45f)
                return false;

            volume = new MeshDerivedBodyVolume(
                def.Name,
                kind,
                Vector3.Transform(localCenter, fitRotation),
                axis,
                MathF.Min(extents.X, extents.Z),
                extents.Y,
                extents,
                localSamples.Count);
            return true;
        }

        var radial = new List<WeightedScalar>(localSamples.Count);
        foreach (var sample in localSamples)
        {
            var dx = sample.Position.X - localCenter.X;
            var dz = sample.Position.Z - localCenter.Z;
            radial.Add(new WeightedScalar(MathF.Sqrt(dx * dx + dz * dz), sample.Weight));
        }
        var radius = WeightedPercentile(radial, 0.96f) + StructuralMeshSkinMargin;
        radius = MathF.Max(radius, StructuralMinimumCapsuleRadius(def, segmentLength));
        var overlap = Math.Clamp(segmentLength * 0.06f, 0.003f, 0.015f);
        var outerHalfExtent = (yMax - yMin) * 0.5f + overlap;
        var halfLength = MathF.Max(0.003f, outerHalfExtent - radius);
        if (!float.IsFinite(radius) || !float.IsFinite(halfLength) ||
            radius > 0.25f || halfLength > 0.40f)
            return false;

        volume = new MeshDerivedBodyVolume(
            def.Name,
            kind,
            Vector3.Transform(localCenter, fitRotation),
            axis,
            radius,
            halfLength,
            Vector3.Zero,
            localSamples.Count);
        return true;
    }

    private static MeshDerivedVolumeKind StructuralVolumeKind(RagdollBoneDef def)
    {
        if (def.Name == "j_kubi")
            return MeshDerivedVolumeKind.Capsule;
        return def.AnatomicalRole is AnatomicalRole.Pelvis or AnatomicalRole.Spine or
            AnatomicalRole.Head or AnatomicalRole.Hand or AnatomicalRole.Foot
            ? MeshDerivedVolumeKind.Box
            : MeshDerivedVolumeKind.Capsule;
    }

    private static float ResolveStructuralSegmentLength(SkeletonAccess skel, int boneIndex)
    {
        if (boneIndex < 0 || boneIndex >= skel.BoneCount)
            return 0.05f;
        ref var pose = ref skel.Pose->ModelPose.Data[boneIndex];
        var position = new Vector3(pose.Translation.X, pose.Translation.Y, pose.Translation.Z);
        var best = 0f;
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        for (var child = 0; child < count; child++)
        {
            if (skel.HavokSkeleton->ParentIndices[child] != boneIndex)
                continue;
            ref var childPose = ref skel.Pose->ModelPose.Data[child];
            var childPosition = new Vector3(
                childPose.Translation.X, childPose.Translation.Y, childPose.Translation.Z);
            best = MathF.Max(best, Vector3.Distance(position, childPosition));
        }
        if (best > 0.005f)
            return best;

        var parent = boneIndex < skel.ParentCount
            ? skel.HavokSkeleton->ParentIndices[boneIndex]
            : -1;
        if (parent >= 0 && parent < skel.BoneCount)
        {
            ref var parentPose = ref skel.Pose->ModelPose.Data[parent];
            var parentPosition = new Vector3(
                parentPose.Translation.X, parentPose.Translation.Y, parentPose.Translation.Z);
            best = Vector3.Distance(position, parentPosition);
        }
        return MathF.Max(0.02f, best);
    }

    private static float StructuralMinimumCapsuleRadius(RagdollBoneDef def, float segmentLength)
    {
        var factor = def.AnatomicalRole switch
        {
            AnatomicalRole.Hip => 0.24f,
            AnatomicalRole.Knee => 0.19f,
            AnatomicalRole.Shoulder => 0.18f,
            AnatomicalRole.Elbow => 0.15f,
            _ => 0.12f,
        };
        return Math.Clamp(segmentLength * factor, 0.006f, 0.08f);
    }

    private static Vector3 StructuralMinimumBoxExtents(RagdollBoneDef def, float segmentLength)
    {
        return def.AnatomicalRole switch
        {
            AnatomicalRole.Pelvis => new Vector3(
                MathF.Max(0.045f, segmentLength * 0.45f),
                MathF.Max(0.025f, segmentLength * 0.25f),
                MathF.Max(0.035f, segmentLength * 0.35f)),
            AnatomicalRole.Spine => new Vector3(
                MathF.Max(0.040f, segmentLength * 0.40f),
                MathF.Max(0.022f, segmentLength * 0.25f),
                MathF.Max(0.030f, segmentLength * 0.30f)),
            AnatomicalRole.Head => new Vector3(0.045f, 0.050f, 0.045f),
            AnatomicalRole.Hand => new Vector3(0.018f, 0.025f, 0.012f),
            AnatomicalRole.Foot => new Vector3(0.022f, 0.035f, 0.014f),
            _ => new Vector3(0.010f),
        };
    }

    /// <summary>
    /// Keep the visual mesh from silently redefining rotational temperament.  A narrow measured
    /// cloud is valid collision geometry, but using its raw tiny inertia makes the same limb react
    /// much faster than the joint/mass model was tuned for.  Clamp each principal inertia to a
    /// conservative solid-segment estimate derived from measured length and stabilized thickness.
    /// </summary>
    private static BodyInertia StabilizeMeshDerivedInertia(
        BodyInertia inertia,
        float mass,
        MeshDerivedBodyVolume volume,
        float shapeScale)
    {
        mass = MathF.Max(0.01f, mass);
        var radius = volume.Kind == MeshDerivedVolumeKind.Box
            ? MathF.Min(volume.BoxHalfExtents.X, volume.BoxHalfExtents.Z) * shapeScale
            : volume.Radius * shapeScale;
        var outerLength = volume.Kind == MeshDerivedVolumeKind.Box
            ? volume.BoxHalfExtents.Y * shapeScale * 2f
            : (volume.HalfLength + volume.Radius) * shapeScale * 2f;
        radius = MathF.Max(0.004f, radius);
        outerLength = MathF.Max(radius * 2f, outerLength);

        var minimumLongAxisInertia = MathF.Max(1e-6f, mass * radius * radius * 0.35f);
        var minimumTransverseInertia = MathF.Max(
            1e-6f,
            mass * (outerLength * outerLength + 3f * radius * radius) / 18f);
        inertia.InverseInertiaTensor.XX = MathF.Min(
            inertia.InverseInertiaTensor.XX, 1f / minimumTransverseInertia);
        inertia.InverseInertiaTensor.YY = MathF.Min(
            inertia.InverseInertiaTensor.YY, 1f / minimumLongAxisInertia);
        inertia.InverseInertiaTensor.ZZ = MathF.Min(
            inertia.InverseInertiaTensor.ZZ, 1f / minimumTransverseInertia);
        return inertia;
    }

    private static CharacterSurfaceProfile CreateMeshDerivedSurfaceProfile()
        => new()
        {
            Id = "mesh-derived-body-volumes",
            Name = "Mesh-Derived Body Volumes",
            Gender = byte.MaxValue,
            MinHeightScale = 1f,
            MaxHeightScale = 1f,
            Margins = new CharacterSurfaceMargins
            {
                Physics = 1f,
                Traversal = 1f,
                Grab = 1f,
                Ground = 1f,
            },
        };

    private static CharacterSurfaceMeshSeed CreateMeshDerivedSurfaceSeed(
        RagdollBone bone,
        string role)
    {
        return bone.ColliderShape == RagdollColliderShape.Box
            ? CreateBoxSurfaceSeed(bone, role)
            : CreateCapsuleSurfaceSeed(bone, role);
    }

    private static CharacterSurfaceMeshSeed CreateBoxSurfaceSeed(RagdollBone bone, string role)
    {
        var e = bone.BoxHalfExtents;
        var c = bone.ShapeCenterOffset;
        var vertices = new[]
        {
            c + new Vector3(-e.X, -e.Y, -e.Z),
            c + new Vector3( e.X, -e.Y, -e.Z),
            c + new Vector3( e.X,  e.Y, -e.Z),
            c + new Vector3(-e.X,  e.Y, -e.Z),
            c + new Vector3(-e.X, -e.Y,  e.Z),
            c + new Vector3( e.X, -e.Y,  e.Z),
            c + new Vector3( e.X,  e.Y,  e.Z),
            c + new Vector3(-e.X,  e.Y,  e.Z),
        };
        var indices = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5,
        };
        return new CharacterSurfaceMeshSeed(bone.Name, role, vertices, indices);
    }

    private static CharacterSurfaceMeshSeed CreateCapsuleSurfaceSeed(RagdollBone bone, string role)
    {
        const int radialSegments = 12;
        const int hemisphereSteps = 3;
        var rings = new List<(float Y, float Radius)>(hemisphereSteps * 2 + 1);
        for (var step = 0; step <= hemisphereSteps; step++)
        {
            var angle = -MathF.PI * 0.5f + step * (MathF.PI * 0.5f / hemisphereSteps);
            rings.Add((
                -bone.CapsuleHalfLength + MathF.Sin(angle) * bone.CapsuleRadius,
                MathF.Cos(angle) * bone.CapsuleRadius));
        }
        for (var step = 1; step <= hemisphereSteps; step++)
        {
            var angle = step * (MathF.PI * 0.5f / hemisphereSteps);
            rings.Add((
                bone.CapsuleHalfLength + MathF.Sin(angle) * bone.CapsuleRadius,
                MathF.Cos(angle) * bone.CapsuleRadius));
        }

        var vertices = new Vector3[rings.Count * radialSegments];
        for (var ring = 0; ring < rings.Count; ring++)
        {
            for (var side = 0; side < radialSegments; side++)
            {
                var angle = side * MathF.Tau / radialSegments;
                vertices[ring * radialSegments + side] = bone.ShapeCenterOffset + new Vector3(
                    MathF.Cos(angle) * rings[ring].Radius,
                    rings[ring].Y,
                    MathF.Sin(angle) * rings[ring].Radius);
            }
        }

        var indices = new int[(rings.Count - 1) * radialSegments * 6];
        var cursor = 0;
        for (var ring = 0; ring < rings.Count - 1; ring++)
        {
            var lower = ring * radialSegments;
            var upper = (ring + 1) * radialSegments;
            for (var side = 0; side < radialSegments; side++)
            {
                var next = (side + 1) % radialSegments;
                indices[cursor++] = lower + side;
                indices[cursor++] = upper + side;
                indices[cursor++] = upper + next;
                indices[cursor++] = lower + side;
                indices[cursor++] = upper + next;
                indices[cursor++] = lower + next;
            }
        }
        return new CharacterSurfaceMeshSeed(bone.Name, role, vertices, indices);
    }
}
