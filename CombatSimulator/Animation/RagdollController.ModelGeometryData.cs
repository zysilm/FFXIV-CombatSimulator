// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using Lumina.Data.Parsing;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>
    /// Read-only metadata bridge to the existing validated raw MDL parser. Does not
    /// create a ragdoll, change its shapes, or alter any existing parsing path.
    /// Includes submesh visibility and shape metadata for mesh consumers.
    /// </summary>
    internal static bool TryReadModelGeometryData(byte[] bytes, out ModelGeometryData data)
    {
        data = new ModelGeometryData();
        if (!TryParseRawMdlCollisionData(bytes, out var parsed, out _)) return false;
        try
        {
            var span = bytes.AsSpan();
            var offset = 0;
            if (!TryReadModelFileHeader(span, ref offset, out var fileHeader) ||
                !TrySkip(span, ref offset, checked(fileHeader.VertexDeclarationCount * 136)) ||
                !TryReadUInt16(span, ref offset, out _) || !TrySkip(span, ref offset, 2) ||
                !TryReadUInt32(span, ref offset, out var stringBytes) ||
                !TrySkip(span, ref offset, checked((int)stringBytes)) ||
                !TryReadModelHeader(span, ref offset, out var header, out var extraLods) ||
                !TrySkip(span, ref offset, checked(header.ElementIdCount * 32))) return false;
            for (var i = 0; i < 3; i++) if (!TryReadLod(span, ref offset, out _)) return false;
            if (extraLods)
                for (var i = 0; i < 3; i++) if (!TryReadExtraLod(span, ref offset, out _)) return false;
            for (var i = 0; i < header.MeshCount; i++) if (!TryReadMesh(span, ref offset, out _)) return false;
            if (!TrySkip(span, ref offset, checked(header.AttributeCount * 4)) ||
                !TrySkip(span, ref offset, checked(header.TerrainShadowMeshCount * 20))) return false;
            var submeshes = new MdlStructs.SubmeshStruct[header.SubmeshCount];
            for (var i = 0; i < submeshes.Length; i++)
            {
                if (!TryReadUInt32(span, ref offset, out var index) ||
                    !TryReadUInt32(span, ref offset, out var count) ||
                    !TryReadUInt32(span, ref offset, out var attributes) ||
                    !TryReadUInt16(span, ref offset, out var boneStart) ||
                    !TryReadUInt16(span, ref offset, out var boneCount)) return false;
                submeshes[i] = new MdlStructs.SubmeshStruct {
                    IndexOffset = index, IndexCount = count, AttributeIndexMask = attributes,
                    BoneStartIndex = boneStart, BoneCount = boneCount,
                };
            }
            if (!TrySkip(span, ref offset, checked(header.TerrainShadowSubmeshCount * 12 + header.MaterialCount * 4 + header.BoneCount * 4))) return false;
            // Dawntrail version 6 changed fixed 64-entry palettes into a header array plus
            // variable-sized ushort palettes. The collision reader remains untouched.
            var boneTables = parsed.BoneTables;
            if (fileHeader.Version == 0x01000006)
            {
                var starts = new int[header.BoneTableCount];
                var counts = new ushort[header.BoneTableCount];
                for (var i = 0; i < starts.Length; i++)
                {
                    int entry = offset;
                    if (!TryReadUInt16(span, ref offset, out var relative) || !TryReadUInt16(span, ref offset, out counts[i])) return false;
                    starts[i] = checked(entry + relative * 4);
                }
                int end = offset;
                boneTables = new MdlStructs.BoneTableStruct[starts.Length];
                for (var i = 0; i < starts.Length; i++)
                {
                    if (counts[i] > 255 || starts[i] < offset) return false;
                    int cursor = starts[i]; var indices = new ushort[counts[i]];
                    for (var b = 0; b < indices.Length; b++) if (!TryReadUInt16(span, ref cursor, out indices[b]) || indices[b] >= header.BoneCount) return false;
                    end = Math.Max(end, checked((cursor + 3) & ~3));
                    boneTables[i] = new() { BoneIndex = indices, BoneCount = (byte)counts[i] };
                }
                if (end > span.Length) return false;
                offset = end;
            }
            else
                for (var i = 0; i < header.BoneTableCount; i++) if (!TryReadBoneTable(span, ref offset, out _)) return false;
            var shapes = new MdlStructs.ShapeStruct[header.ShapeCount];
            for (var i = 0; i < shapes.Length; i++)
            {
                if (!TryReadUInt32(span, ref offset, out var name)) return false;
                var starts = new ushort[3]; var counts = new ushort[3];
                for (var l = 0; l < 3; l++) if (!TryReadUInt16(span, ref offset, out starts[l])) return false;
                for (var l = 0; l < 3; l++) if (!TryReadUInt16(span, ref offset, out counts[l])) return false;
                shapes[i] = new() { StringOffset = name, ShapeMeshStartIndex = starts, ShapeMeshCount = counts };
            }
            var shapeMeshes = new MdlStructs.ShapeMeshStruct[header.ShapeMeshCount];
            for (var i = 0; i < shapeMeshes.Length; i++)
            {
                if (!TryReadUInt32(span, ref offset, out var mesh) || !TryReadUInt32(span, ref offset, out var count) ||
                    !TryReadUInt32(span, ref offset, out var start)) return false;
                shapeMeshes[i] = new() { MeshIndexOffset = mesh, ShapeValueCount = count, ShapeValueOffset = start };
            }
            var shapeValues = new MdlStructs.ShapeValueStruct[header.ShapeValueCount];
            for (var i = 0; i < shapeValues.Length; i++)
            {
                if (!TryReadUInt16(span, ref offset, out var entry) || !TryReadUInt16(span, ref offset, out var replacement)) return false;
                shapeValues[i] = new() { BaseIndicesIndex = entry, ReplacingVertexIndex = replacement };
            }
            data = new ModelGeometryData {
                Data = parsed.Data, FileHeader = parsed.FileHeader,
                Lods = parsed.Lods, Meshes = parsed.Meshes, VertexDeclarations = parsed.VertexDeclarations,
                BoneTables = boneTables, BoneNameOffsets = parsed.BoneNameOffsets,
                Strings = parsed.Strings, Submeshes = submeshes,
                Shapes = shapes, ShapeMeshes = shapeMeshes, ShapeValues = shapeValues,
            };
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or IndexOutOfRangeException)
        { return false; }
    }
}
