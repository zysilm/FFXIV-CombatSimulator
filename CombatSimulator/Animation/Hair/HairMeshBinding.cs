// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CombatSimulator.Effects.BodyFluids.Surface;

namespace CombatSimulator.Animation.Hair;

/// <summary>Managed, visible-LOD0 weighted samples for mesh-informed hair guides.
/// No native references are retained; this is not a vertex-deformation backend.</summary>
internal sealed class HairMeshBinding
{
    public readonly Dictionary<string, Vector3[]> Samples;
    public readonly int VisibleVertices;
    public HairMeshBinding(Dictionary<string, Vector3[]> samples, int vertices) { Samples = samples; VisibleVertices = vertices; }
    private sealed class Bucket
    {
        public readonly List<Vector3> Points = new(256);
        public int Count;
        public void Add(Vector3 p)
        {
            Count++;
            if (Points.Count < 256) { Points.Add(p); return; }
            // Deterministic bounded reservoir across the entire loaded model.
            uint hash = unchecked((uint)Count * 2654435761u);
            int index = (int)(hash % (uint)Count);
            if (index < 256) Points[index] = p;
        }
    }
    public static HairMeshBinding? Decode(byte[] raw, uint attributes)
    {
        if (!RagdollController.TryReadFluidModelData(raw, out var mdl) || mdl.Lods.Length == 0 || mdl.Meshes.Length > 256) return null;
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        int visibleVertices = 0; var lod = mdl.Lods[0];
        for (int mi = lod.MeshIndex; mi < Math.Min(mdl.Meshes.Length, lod.MeshIndex + lod.MeshCount); mi++)
        {
            var mesh = mdl.Meshes[mi];
            if (mi >= mdl.VertexDeclarations.Length || mesh.BoneTableIndex >= mdl.BoneTables.Length) continue;
            var used = new bool[mesh.VertexCount];
            void Mark(uint start, uint count)
            {
                if (start < mesh.StartIndex || (ulong)start + count > (ulong)mesh.StartIndex + mesh.IndexCount) return;
                for (uint i = start; i < start + count; i++)
                {
                    ulong offset = (ulong)mdl.FileHeader.IndexOffset[0] + i * 2;
                    if (offset + 2 > (ulong)raw.Length) break;
                    int vertex = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)offset, 2));
                    if (vertex < used.Length) used[vertex] = true;
                }
            }
            if (mesh.SubMeshCount == 0) Mark(mesh.StartIndex, mesh.IndexCount);
            else
                for (int s = mesh.SubMeshIndex; s < Math.Min(mdl.Submeshes.Length, mesh.SubMeshIndex + mesh.SubMeshCount); s++)
                    if ((mdl.Submeshes[s].AttributeIndexMask & attributes) == mdl.Submeshes[s].AttributeIndexMask)
                        Mark(mdl.Submeshes[s].IndexOffset, mdl.Submeshes[s].IndexCount);
            var palette = mdl.BoneTables[mesh.BoneTableIndex].BoneIndex;
            for (int vi = 0; vi < used.Length; vi++)
            {
                if (!used[vi]) continue;
                if (!Read(mdl, mi, vi, out var position, out int localBone)) continue;
                if (localBone >= palette.Length || palette[localBone] >= mdl.BoneNameOffsets.Length) continue;
                uint nameStart = mdl.BoneNameOffsets[palette[localBone]];
                if (nameStart >= mdl.Strings.Length) continue;
                int end = (int)nameStart;
                while (end < mdl.Strings.Length && mdl.Strings[end] != 0) end++;
                string name = Encoding.UTF8.GetString(mdl.Strings, (int)nameStart, end - (int)nameStart);
                if (!buckets.TryGetValue(name, out var bucket)) buckets[name] = bucket = new();
                bucket.Add(position); visibleVertices++;
                if (visibleVertices > 200000) return null;
            }
        }
        var samples = new Dictionary<string, Vector3[]>(StringComparer.Ordinal);
        foreach (var item in buckets) samples[item.Key] = item.Value.Points.ToArray();
        return new(samples, visibleVertices);
    }
    private static bool Read(FluidModelData mdl, int mi, int vi, out Vector3 position, out int bone)
    {
        position = default; bone = -1;
        var mesh = mdl.Meshes[mi];
        Span<float> weights = stackalloc float[8]; weights.Clear();
        Span<byte> indices = stackalloc byte[8]; indices.Clear();
        bool hasPosition = false, hasWeights = false, hasIndices = false;
        foreach (var element in mdl.VertexDeclarations[mi].VertexElements)
        {
            if (element.Usage > 2 || element.Stream >= mesh.VertexBufferOffset.Length || element.Stream >= mesh.VertexBufferStride.Length) continue;
            int size = element.Type switch { 2 => 12, 3 => 16, 5 or 8 => 4, 14 or 17 => 8, _ => 0 };
            ulong offset = (ulong)mdl.FileHeader.VertexOffset[0] + mesh.VertexBufferOffset[element.Stream] + (ulong)vi * mesh.VertexBufferStride[element.Stream] + element.Offset;
            if (size == 0 || offset + (uint)size > (ulong)mdl.Data.Length) return false;
            var data = mdl.Data.AsSpan((int)offset, size);
            if (element.Usage == 0)
            {
                if (element.Type == 2 || element.Type == 3) position = new(Float(data, 0), Float(data, 4), Float(data, 8));
                else if (element.Type == 14) position = new(Half(data, 0), Half(data, 2), Half(data, 4));
                else return false;
                hasPosition = FlexibleHairSolver.Finite(position);
            }
            else if (element.Usage == 2)
            {
                if (element.Type != 5 && element.Type != 17) return false;
                data.CopyTo(indices); hasIndices = true;
            }
            else
            {
                if (element.Type == 8 || element.Type == 17)
                    for (int j = 0; j < size; j++) weights[j] = data[j] / 255f;
                else if (element.Type == 14)
                    for (int j = 0; j < 4; j++) weights[j] = Half(data, j * 2);
                else if (element.Type == 3)
                    for (int j = 0; j < 4; j++) weights[j] = Float(data, j * 4);
                else return false;
                hasWeights = true;
            }
        }
        float largest = 0;
        for (int i = 0; i < 8; i++) if (weights[i] > largest) { largest = weights[i]; bone = indices[i]; }
        return hasPosition && hasWeights && hasIndices && bone >= 0;
    }
    private static float Float(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
    private static float Half(ReadOnlySpan<byte> bytes, int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]));
}
