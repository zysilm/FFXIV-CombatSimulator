// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using CombatSimulator.Effects.BodyFluids.Surface;
using Lumina.Data.Parsing;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>
    /// Read-only metadata bridge to the existing validated raw MDL parser. Does not
    /// create a ragdoll, change its shapes, or alter any existing parsing path.
    /// Surface flow additionally needs submesh visibility, which collision fitting omits.
    /// </summary>
    internal static bool TryReadFluidModelData(byte[] bytes, out FluidModelData data)
    {
        data = new FluidModelData();
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
            data = new FluidModelData {
                Data = parsed.Data, FileHeader = parsed.FileHeader,
                Lods = parsed.Lods, Meshes = parsed.Meshes, VertexDeclarations = parsed.VertexDeclarations,
                BoneTables = parsed.BoneTables, BoneNameOffsets = parsed.BoneNameOffsets,
                Strings = parsed.Strings, Submeshes = submeshes,
            };
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or IndexOutOfRangeException)
        { return false; }
    }
}
