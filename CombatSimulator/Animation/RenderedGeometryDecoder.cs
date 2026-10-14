// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Lumina.Data.Parsing;

namespace CombatSimulator.Animation;

internal readonly record struct RenderedGeometryInfluence(int BoneIndex, float Weight, Vector3 WorldContribution = default);
internal readonly record struct RenderedGeometryVertex(Vector3 WorldPosition, RenderedGeometryInfluence[] Influences, int SourceSlot);
internal sealed class RenderedGeometrySnapshot
{
    public RenderedGeometryVertex[] Vertices = Array.Empty<RenderedGeometryVertex>();
    public int[] TriangleIndices = Array.Empty<int>();
    public string[] Diagnostics = Array.Empty<string>();
}

/// <summary>Managed decoder: visible topology, active shape replacements and up to eight influences.
/// No native references survive capture. Invalid vertices remove their triangles rather than
/// silently substituting an undeformed reference vertex.</summary>
internal static class RenderedGeometryDecoder
{
    internal const int MaximumSourceFaces = 100000;
    internal const int MaximumSourceVertices = MaximumSourceFaces * 3;
    internal static string StringAt(byte[] strings, uint start)
    {
        if (start >= strings.Length) return string.Empty;
        int end = (int)start;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)start, end - (int)start);
    }

    internal static int Append(ModelGeometryData data, int lodIndex, int meshIndex, int slot,
        uint attributes, bool[] activeShapes, int[] boneMap, Matrix4x4[] skin,
        Matrix4x4[]? raceDeform, Matrix4x4 world,
        List<RenderedGeometryVertex> vertices, List<int> triangles, int[]? influenceOwners = null)
    {
        if ((uint)meshIndex >= data.Meshes.Length || meshIndex >= data.VertexDeclarations.Length ||
            data.FileHeader.IndexOffset == null || data.FileHeader.VertexOffset == null ||
            lodIndex >= data.FileHeader.IndexOffset.Length || lodIndex >= data.FileHeader.VertexOffset.Length) return 0;
        var mesh = data.Meshes[meshIndex];
        if (mesh.VertexCount == 0 || mesh.IndexCount > 1200000) return 0;
        var replacements = new Dictionary<uint, ushort>();
        for (int shapeIndex = 0; shapeIndex < Math.Min(activeShapes.Length, data.Shapes.Length); shapeIndex++)
        {
            if (!activeShapes[shapeIndex]) continue;
            var shape = data.Shapes[shapeIndex];
            if (shape.ShapeMeshStartIndex == null || shape.ShapeMeshCount == null ||
                lodIndex >= shape.ShapeMeshStartIndex.Length || lodIndex >= shape.ShapeMeshCount.Length) continue;
            int end = Math.Min(data.ShapeMeshes.Length, shape.ShapeMeshStartIndex[lodIndex] + shape.ShapeMeshCount[lodIndex]);
            for (int si = shape.ShapeMeshStartIndex[lodIndex]; si < end; si++)
            {
                var sm = data.ShapeMeshes[si];
                if (sm.MeshIndexOffset != mesh.StartIndex) continue;
                if ((ulong)sm.ShapeValueOffset + sm.ShapeValueCount > (ulong)data.ShapeValues.Length) return 0;
                for (uint j = 0; j < sm.ShapeValueCount; j++)
                {
                    var replacement = data.ShapeValues[sm.ShapeValueOffset + j];
                    if (replacement.BaseIndicesIndex >= mesh.IndexCount || replacement.ReplacingVertexIndex >= mesh.VertexCount) return 0;
                    replacements[replacement.BaseIndicesIndex] = replacement.ReplacingVertexIndex;
                }
            }
        }

        var localToOutput = new Dictionary<int, int>();
        int before = triangles.Count;
        int Resolve(uint relativeIndex)
        {
            ulong at = (ulong)data.FileHeader.IndexOffset[lodIndex] + ((ulong)mesh.StartIndex + relativeIndex) * 2;
            if (relativeIndex >= mesh.IndexCount || at + 2 > (ulong)data.Data.Length) return -1;
            int vertexIndex = replacements.TryGetValue(relativeIndex, out var replacement) ? replacement :
                BinaryPrimitives.ReadUInt16LittleEndian(data.Data.AsSpan((int)at, 2));
            if (vertexIndex >= mesh.VertexCount) return -1;
            if (localToOutput.TryGetValue(vertexIndex, out int existing)) return existing;
            int result = -1;
            if (ReadVertex(data, lodIndex, meshIndex, vertexIndex, boneMap, skin, raceDeform, world, slot, influenceOwners, out var vertex))
            {
                if (vertices.Count >= MaximumSourceVertices) throw new System.IO.InvalidDataException("Rendered geometry exceeded 300000 source vertices; exact geometry was not decimated.");
                result = vertices.Count;
                vertices.Add(vertex);
            }
            localToOutput[vertexIndex] = result;
            return result;
        }
        void AddRange(uint start, uint count)
        {
            if (start < mesh.StartIndex || (ulong)start + count > (ulong)mesh.StartIndex + mesh.IndexCount) return;
            for (uint i = 0; i + 2 < count; i += 3)
            {
                int a = Resolve(start - mesh.StartIndex + i), b = Resolve(start - mesh.StartIndex + i + 1), c = Resolve(start - mesh.StartIndex + i + 2);
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || c == a) continue;
                if (Vector3.Cross(vertices[b].WorldPosition - vertices[a].WorldPosition, vertices[c].WorldPosition - vertices[a].WorldPosition).LengthSquared() < 1e-16f) continue;
                if (triangles.Count / 3 >= MaximumSourceFaces) throw new System.IO.InvalidDataException("Rendered geometry exceeded 100000 source faces; exact geometry was not decimated.");
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
        }
        if (mesh.SubMeshCount == 0) AddRange(mesh.StartIndex, mesh.IndexCount);
        else
        {
            if ((uint)mesh.SubMeshIndex + mesh.SubMeshCount > data.Submeshes.Length) return 0;
            for (int si = mesh.SubMeshIndex; si < mesh.SubMeshIndex + mesh.SubMeshCount; si++)
            {
                var sub = data.Submeshes[si];
                if ((sub.AttributeIndexMask & attributes) == sub.AttributeIndexMask) AddRange(sub.IndexOffset, sub.IndexCount);
            }
        }
        return (triangles.Count - before) / 3;
    }

    private static bool ReadVertex(ModelGeometryData data, int lod, int mi, int vi, int[] map, Matrix4x4[] skin,
        Matrix4x4[]? deformation, Matrix4x4 world, int slot, int[]? influenceOwners, out RenderedGeometryVertex vertex)
    {
        vertex = default;
        var mesh = data.Meshes[mi];
        Span<float> weights = stackalloc float[8]; weights.Clear();
        Span<byte> indices = stackalloc byte[8]; indices.Clear();
        Vector3 position = default;
        bool hasPosition = false, hasWeights = false, hasIndices = false;
        foreach (var e in data.VertexDeclarations[mi].VertexElements)
        {
            if (e.Usage > 2) continue;
            if (e.Stream >= mesh.VertexBufferOffset.Length || e.Stream >= mesh.VertexBufferStride.Length) return false;
            int size = e.Type switch { 2 => 12, 3 => 16, 5 or 8 => 4, 14 or 17 => 8, _ => 0 };
            ulong offset = (ulong)data.FileHeader.VertexOffset[lod] + mesh.VertexBufferOffset[e.Stream] +
                           (ulong)vi * mesh.VertexBufferStride[e.Stream] + e.Offset;
            if (size == 0 || offset + (uint)size > (ulong)data.Data.Length) return false;
            var span = data.Data.AsSpan((int)offset, size);
            if (e.Usage == 0)
            {
                if (e.Type is 2 or 3) position = new(F32(span, 0), F32(span, 4), F32(span, 8));
                else if (e.Type == 14) position = new(F16(span, 0), F16(span, 2), F16(span, 4));
                else return false;
                hasPosition = Finite(position);
            }
            else if (e.Usage == 2)
            {
                if (e.Type is not (5 or 17)) return false;
                span.CopyTo(indices); hasIndices = true;
            }
            else
            {
                if (e.Type is 5 or 8 or 17)
                    for (int i = 0; i < size; i++) weights[i] = span[i] / 255f;
                else if (e.Type == 14)
                    for (int i = 0; i < 4; i++) weights[i] = F16(span, i * 2);
                else if (e.Type == 3)
                    for (int i = 0; i < 4; i++) weights[i] = F32(span, i * 4);
                else return false;
                hasWeights = true;
            }
        }
        if (!hasPosition || !hasWeights || !hasIndices) return false;
        float total = 0;
        var influences = new List<RenderedGeometryInfluence>(8);
        for (int i = 0; i < 8; i++)
        {
            if (!float.IsFinite(weights[i]) || weights[i] < 0) return false;
            if (weights[i] == 0) continue;
            if (indices[i] >= map.Length || map[indices[i]] < 0 || map[indices[i]] >= skin.Length) return false;
            total += weights[i];
        }
        if (total < 1e-6f) return false;
        // PBD is a vertex pre-deformation stage; animation skinning follows it.
        if (deformation != null)
        {
            Vector3 deformed = default;
            for (int i = 0; i < 8; i++)
                if (weights[i] > 0) deformed += Vector3.Transform(position, deformation[map[indices[i]]]) * (weights[i] / total);
            position = deformed;
        }
        Vector3 posed = default;
        for (int i = 0; i < 8; i++)
        {
            if (weights[i] <= 0) continue;
            int bone = map[indices[i]]; float weight = weights[i] / total;
            var contribution = Vector3.Transform(position, skin[bone]);
            posed += contribution * weight;
            int owner = influenceOwners == null ? bone : bone < influenceOwners.Length ? influenceOwners[bone] : -1;
            if (owner < 0) return false;
            influences.Add(new(owner, weight, Vector3.Transform(contribution, world)));
        }
        var worldPosition = Vector3.Transform(posed, world);
        if (!Finite(worldPosition)) return false;
        vertex = new(worldPosition, influences.ToArray(), slot);
        return true;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float F32(ReadOnlySpan<byte> bytes, int at) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]));
    private static float F16(ReadOnlySpan<byte> bytes, int at) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]));
}
