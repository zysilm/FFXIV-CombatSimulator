// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>
/// Reads the surface a model contributes to the ragdoll's bodies: every visible vertex in the
/// model's reference pose, with the area it stands for and its skin weight on each fitted bone.
/// </summary>
internal static class StructuralMeshSampler
{
    /// <summary>
    /// The race deformer's matrices for a model's bones. Bones the deformer does not list take
    /// their nearest listed ancestor's, found through the character's skeleton.
    /// </summary>
    public static (bool Supported, Matrix4x4[]? Matrices) RaceDeformation(RenderedRaceDeformation deformer,
        ushort sourceRace, ushort targetRace, string[] modelBones, string[] skeletonBones, int[] skeletonParents)
    {
        if (sourceRace == targetRace)
            return (true, null);

        // The model's bones first, so their indices are the model's own; then the rest of the skeleton.
        var names = new List<string>(modelBones);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < modelBones.Length; i++)
            index.TryAdd(modelBones[i], i);
        foreach (var bone in skeletonBones)
            if (!index.ContainsKey(bone))
            {
                index[bone] = names.Count;
                names.Add(bone);
            }

        var parents = new int[names.Count];
        Array.Fill(parents, -1);
        for (var i = 0; i < skeletonBones.Length && i < skeletonParents.Length; i++)
        {
            var parent = skeletonParents[i];
            if (parent >= 0 && parent < skeletonBones.Length)
                parents[index[skeletonBones[i]]] = index[skeletonBones[parent]];
        }

        if (!deformer.TryBuild(sourceRace, targetRace, names.ToArray(), parents, out var matrices))
            return (false, null);
        return (true, matrices);
    }

    /// <param name="attributes">The model's enabled attribute mask; hidden sub-meshes are skipped.</param>
    /// <param name="activeShapes">Which of the model's shape keys are applied.</param>
    /// <param name="raceDeformation">
    /// Given the model's bone names, the matrices that reshape it for the character's race, or null
    /// when it is worn by the race it was made for. Returning false omits the model.
    /// </param>
    /// <returns>Number of samples added.</returns>
    public static int Collect(ModelGeometryData data, uint attributes, bool[] activeShapes,
        Func<string[], (bool Supported, Matrix4x4[]? Matrices)>? raceDeformation,
        IReadOnlySet<string> targetBones, Dictionary<string, List<StructuralMeshFitter.Sample>> samples)
    {
        if (data.Lods.Length == 0)
            return 0;

        var names = new string[data.BoneNameOffsets.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = RenderedGeometryDecoder.StringAt(data.Strings, data.BoneNameOffsets[i]);

        Matrix4x4[]? deformation = null;
        if (raceDeformation != null)
        {
            var (supported, matrices) = raceDeformation(names);
            if (!supported)
                return 0;
            deformation = matrices;
        }

        // The reference pose: every bone's skin matrix is the identity, and the model stays in its
        // own space. Bones the skeleton does not have still resolve, so no vertex is lost to them.
        var identity = new Matrix4x4[Math.Max(1, names.Length)];
        Array.Fill(identity, Matrix4x4.Identity);

        var meshes = new SortedSet<int>();
        void AddRange(int start, int count)
        {
            for (var mesh = start; mesh < Math.Min(data.Meshes.Length, start + count); mesh++)
                meshes.Add(mesh);
        }
        var lod = data.Lods[0];
        AddRange(lod.MeshIndex, lod.MeshCount);
        if (data.ExtraLodEnabled && data.ExtraLods.Length > 0)
        {
            var extra = data.ExtraLods[0];
            AddRange(extra.GlassMeshIndex, extra.GlassMeshCount);
            AddRange(extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount);
            AddRange(extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount);
        }

        var vertices = new List<RenderedGeometryVertex>();
        var triangles = new List<int>();
        foreach (var meshIndex in meshes)
        {
            var mesh = data.Meshes[meshIndex];
            if (mesh.BoneTableIndex >= data.BoneTables.Length)
                continue;
            var palette = data.BoneTables[mesh.BoneTableIndex].BoneIndex;
            var map = new int[palette.Length];
            for (var i = 0; i < palette.Length; i++)
                map[i] = palette[i] < names.Length ? palette[i] : -1;
            RenderedGeometryDecoder.Append(data, 0, meshIndex, 0, attributes, activeShapes, map, identity,
                deformation, Matrix4x4.Identity, vertices, triangles);
        }

        // A third of every triangle's area goes to each of its corners.
        var area = new float[vertices.Count];
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var third = Vector3.Cross(vertices[b].WorldPosition - vertices[a].WorldPosition,
                vertices[c].WorldPosition - vertices[a].WorldPosition).Length() / 6f;
            area[a] += third;
            area[b] += third;
            area[c] += third;
        }

        var added = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            if (!(area[i] > 0f))
                continue;
            foreach (var influence in vertices[i].Influences)
            {
                if ((uint)influence.BoneIndex >= (uint)names.Length || !targetBones.Contains(names[influence.BoneIndex]))
                    continue;
                if (!samples.TryGetValue(names[influence.BoneIndex], out var list))
                    samples[names[influence.BoneIndex]] = list = new List<StructuralMeshFitter.Sample>();
                list.Add(new StructuralMeshFitter.Sample(vertices[i].WorldPosition, influence.Weight, area[i]));
                added++;
            }
        }
        return added;
    }
}
