// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using Lumina.Data.Files;
using Lumina.Data.Parsing;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>Managed geometry metadata shared by normal Lumina loading and the existing raw MDL parser bridge.</summary>
internal sealed class FluidModelData
{
    public byte[] Data = Array.Empty<byte>();
    public MdlStructs.ModelFileHeader FileHeader;
    public MdlStructs.VertexDeclarationStruct[] VertexDeclarations = Array.Empty<MdlStructs.VertexDeclarationStruct>();
    public MdlStructs.LodStruct[] Lods = Array.Empty<MdlStructs.LodStruct>();
    public MdlStructs.MeshStruct[] Meshes = Array.Empty<MdlStructs.MeshStruct>();
    public MdlStructs.BoneTableStruct[] BoneTables = Array.Empty<MdlStructs.BoneTableStruct>();
    public uint[] BoneNameOffsets = Array.Empty<uint>();
    public byte[] Strings = Array.Empty<byte>();
    public MdlStructs.SubmeshStruct[] Submeshes = Array.Empty<MdlStructs.SubmeshStruct>();

    public static FluidModelData FromMdlFile(MdlFile mdl) => new()
    {
        Data = mdl.Data ?? Array.Empty<byte>(),
        FileHeader = mdl.FileHeader,
        VertexDeclarations = mdl.VertexDeclarations ?? Array.Empty<MdlStructs.VertexDeclarationStruct>(),
        Lods = mdl.Lods ?? Array.Empty<MdlStructs.LodStruct>(),
        Meshes = mdl.Meshes ?? Array.Empty<MdlStructs.MeshStruct>(),
        BoneTables = mdl.BoneTables ?? Array.Empty<MdlStructs.BoneTableStruct>(),
        BoneNameOffsets = mdl.BoneNameOffsets ?? Array.Empty<uint>(),
        Strings = mdl.Strings ?? Array.Empty<byte>(),
        Submeshes = mdl.Submeshes ?? Array.Empty<MdlStructs.SubmeshStruct>(),
    };
}
