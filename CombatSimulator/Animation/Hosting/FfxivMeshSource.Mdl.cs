// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using BepuPhysics.Collidables;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using HkQsTransform = FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf;
using Lumina.Data.Files;
using Lumina.Data.Parsing;

namespace CombatSimulator.Animation.Hosting;

// Raw model files (moved here from RagdollController.cs in stage A2b, unchanged): the validated MDL parser, the
// skinned-triangle reader of other characters' mesh colliders, and the helpers the other model readers share.
internal sealed unsafe partial class FfxivMeshSource
{
    // The game-reading half of the controller's BuildMeshCollision: the character's skinned models as one
    // triangle soup. The controller warns and builds the shape; exceptions outside a single slot reach its catch.
    private const int MeshCollisionMaxTriangles = 8000;
    private const int MeshCollisionPreferredLod = 0; // LOD0: matches the on-screen silhouette exactly (Low LOD shrank/coarsened the collision hull).

    public bool TryCollectActorTriangles(nint address, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes)
    {
        FfxivMeshSource.verboseLog = verboseLog;
        loadedModels = 0;
        processedMeshes = 0;
        var ns = FfxivSkeletonPose.Of(skeleton);
        if (!TryBuildSkinDeltas(ns, out var skinDeltas))
            return false;

        var slotCount = Math.Clamp(ns.CharBase->SlotCount, 0, 32);
        for (int slot = 0; slot < slotCount && triangles.Count < MeshCollisionMaxTriangles; slot++)
        {
            var renderModel = ns.CharBase->Models == null ? null : ns.CharBase->Models[slot];
            if (renderModel == null || renderModel->ModelResourceHandle == null)
                continue;

            var resourceHandle = (ResourceHandle*)renderModel->ModelResourceHandle;
            var modelPath = resourceHandle->FileName.ToString();
            if (string.IsNullOrWhiteSpace(modelPath) ||
                !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var mdl = Services.DataManager.GameData.GetFile<MdlFile>(modelPath);
                if (mdl == null)
                {
                    log.Warning($"RagdollController: {label} mesh collision slot {slot} '{modelPath}' returned null MdlFile");
                    continue;
                }

                loadedModels++;
                var beforeMeshes = processedMeshes;
                var beforeTriangles = triangles.Count;
                processedMeshes += AppendSkinnedMdlTriangles(modelPath, MeshCollisionMdlData.FromMdlFile(mdl), ns, skinDeltas, triangles);
                if (verboseLog)
                {
                    log.Info($"RagdollController: {label} mesh slot {slot} '{modelPath}' geometry-only mdl: meshes={processedMeshes - beforeMeshes}, triangles={triangles.Count - beforeTriangles}");
                }
            }
            catch (Exception ex)
            {
                var rawData = TryGetRawFileData(modelPath);
                string? rawParseError = null;
                if (rawData != null &&
                    TryParseRawMdlCollisionData(rawData, out var rawMdl, out rawParseError))
                {
                    loadedModels++;
                    var beforeMeshes = processedMeshes;
                    var beforeTriangles = triangles.Count;
                    processedMeshes += AppendSkinnedMdlTriangles(modelPath, rawMdl, ns, skinDeltas, triangles);
                    log.Warning($"RagdollController: {label} mesh collision mdl parser failed for slot {slot} '{modelPath}' ({ex.GetType().Name}: {ex.Message}), raw geometry fallback used: meshes={processedMeshes - beforeMeshes}, triangles={triangles.Count - beforeTriangles}");
                }
                else
                {
                    var rawLength = rawData?.Length ?? -1;
                    log.Warning(ex, $"RagdollController: {label} mesh collision failed to load mdl slot {slot} '{modelPath}' (rawBytes={(rawLength >= 0 ? rawLength.ToString() : "unavailable")}, rawFallback={rawParseError ?? "unavailable"})");
                }
            }
        }
        return true;
    }

    private bool TryLoadMeshCollisionMdlData(string label, int slot, string modelPath, out MeshCollisionMdlData mdl)
    {
        mdl = new MeshCollisionMdlData();
        try
        {
            var luminaMdl = Services.DataManager.GameData.GetFile<MdlFile>(modelPath);
            if (luminaMdl == null)
            {
                log.Warning($"RagdollController: {label} mesh collision slot {slot} '{modelPath}' returned null MdlFile");
                return false;
            }

            mdl = MeshCollisionMdlData.FromMdlFile(luminaMdl);
            return true;
        }
        catch (Exception ex)
        {
            var rawData = TryGetRawFileData(modelPath);
            string? rawParseError = null;
            if (rawData != null && TryParseRawMdlCollisionData(rawData, out var rawMdl, out rawParseError))
            {
                mdl = rawMdl;
                log.Warning($"RagdollController: {label} mesh collision mdl parser failed for slot {slot} '{modelPath}' ({ex.GetType().Name}: {ex.Message}), raw geometry fallback used");
                return true;
            }

            var rawLength = rawData?.Length ?? -1;
            log.Warning(ex, $"RagdollController: {label} mesh collision failed to load mdl slot {slot} '{modelPath}' (rawBytes={(rawLength >= 0 ? rawLength.ToString() : "unavailable")}, rawFallback={rawParseError ?? "unavailable"})");
            return false;
        }
    }

    private static void AddAnimatedMeshRange(MeshCollisionMdlData mdl, ushort meshStart, ushort meshCount, HashSet<int> meshIndices)
    {
        var meshEnd = Math.Min(mdl.Meshes.Length, meshStart + meshCount);
        for (int meshIndex = meshStart; meshIndex < meshEnd; meshIndex++)
            meshIndices.Add(meshIndex);
    }

    /// <summary>
    /// The scale a character is drawn with, read straight from the game: for the model readers here and for the
    /// weapon-drop simulation (stage A5). RagdollController.GetCharacterScale reads the same through the host.
    /// </summary>
    internal static Vector3 GetCharacterScale(nint address, SkeletonAccess ns)
    {
        if (address != nint.Zero)
        {
            var gameObject = (GameObject*)address;
            if (gameObject->DrawObject != null)
            {
                var s = gameObject->DrawObject->Scale;
                var drawScale = new Vector3(s.X, s.Y, s.Z);
                if (IsFinite(drawScale) && MathF.Abs(drawScale.X) >= 1e-6f && MathF.Abs(drawScale.Y) >= 1e-6f && MathF.Abs(drawScale.Z) >= 1e-6f)
                    return drawScale;
            }
        }
        if (ns.CharBase == null || ns.CharBase->Skeleton == null)
            return Vector3.One;
        var t = ns.CharBase->Skeleton->Transform.Scale;
        var scale = new Vector3(t.X, t.Y, t.Z);
        if (!IsFinite(scale) || MathF.Abs(scale.X) < 1e-6f || MathF.Abs(scale.Y) < 1e-6f || MathF.Abs(scale.Z) < 1e-6f)
            return Vector3.One;
        return scale;
    }

    private bool TryBuildSkinDeltas(SkeletonAccess ns, out Matrix4x4[] skinDeltas)
    {
        skinDeltas = Array.Empty<Matrix4x4>();
        if (ns.HavokSkeleton == null || ns.Pose == null ||
            ns.HavokSkeleton->ReferencePose.Data == null ||
            ns.Pose->ModelPose.Data == null)
            return false;

        var boneCount = Math.Min(ns.BoneCount, Math.Min(ns.ParentCount,
            Math.Min(ns.HavokSkeleton->ReferencePose.Length, ns.Pose->ModelPose.Length)));
        if (boneCount <= 0)
            return false;

        var refModel = new Matrix4x4[boneCount];
        skinDeltas = new Matrix4x4[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            var refLocal = QsToMatrix(ns.HavokSkeleton->ReferencePose.Data[i]);
            var parent = ns.HavokSkeleton->ParentIndices[i];
            refModel[i] = parent >= 0 && parent < i
                ? refLocal * refModel[parent]
                : refLocal;
        }

        for (int i = 0; i < boneCount; i++)
        {
            var curModel = QsToMatrix(ns.Pose->ModelPose.Data[i]);
            if (!Matrix4x4.Invert(refModel[i], out var invRef))
                invRef = Matrix4x4.Identity;
            skinDeltas[i] = invRef * curModel;
        }

        return true;
    }

    private sealed class MeshCollisionMdlData
    {
        public byte[] Data = Array.Empty<byte>();
        public MdlStructs.ModelFileHeader FileHeader;
        public MdlStructs.VertexDeclarationStruct[] VertexDeclarations = Array.Empty<MdlStructs.VertexDeclarationStruct>();
        public MdlStructs.LodStruct[] Lods = Array.Empty<MdlStructs.LodStruct>();
        public MdlStructs.ExtraLodStruct[] ExtraLods = Array.Empty<MdlStructs.ExtraLodStruct>();
        public MdlStructs.MeshStruct[] Meshes = Array.Empty<MdlStructs.MeshStruct>();
        public uint[] BoneNameOffsets = Array.Empty<uint>();
        public MdlStructs.BoneTableStruct[] BoneTables = Array.Empty<MdlStructs.BoneTableStruct>();
        public byte[] Strings = Array.Empty<byte>();
        public bool ExtraLodEnabled;

        public static MeshCollisionMdlData FromMdlFile(MdlFile mdl) => new()
        {
            Data = mdl.Data ?? Array.Empty<byte>(),
            FileHeader = mdl.FileHeader,
            VertexDeclarations = mdl.VertexDeclarations ?? Array.Empty<MdlStructs.VertexDeclarationStruct>(),
            Lods = mdl.Lods ?? Array.Empty<MdlStructs.LodStruct>(),
            ExtraLods = mdl.ExtraLods ?? Array.Empty<MdlStructs.ExtraLodStruct>(),
            Meshes = mdl.Meshes ?? Array.Empty<MdlStructs.MeshStruct>(),
            BoneNameOffsets = mdl.BoneNameOffsets ?? Array.Empty<uint>(),
            BoneTables = mdl.BoneTables ?? Array.Empty<MdlStructs.BoneTableStruct>(),
            Strings = mdl.Strings ?? Array.Empty<byte>(),
            ExtraLodEnabled = mdl.ModelHeader.ExtraLodEnabled,
        };
    }

    private int AppendSkinnedMdlTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (!TrySelectMdlLod(mdl, out var lodIndex, out var lod))
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has no usable LOD");
            return 0;
        }

        if (mdl.Data.Length == 0 || mdl.Meshes.Length == 0 || mdl.VertexDeclarations.Length == 0 ||
            mdl.BoneTables.Length == 0 || mdl.BoneNameOffsets.Length == 0 || mdl.Strings.Length == 0)
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has incomplete MdlFile data");
            return 0;
        }
        if (mdl.FileHeader.VertexOffset == null || mdl.FileHeader.VertexBufferSize == null ||
            mdl.FileHeader.IndexOffset == null || mdl.FileHeader.IndexBufferSize == null ||
            lodIndex >= mdl.FileHeader.VertexOffset.Length || lodIndex >= mdl.FileHeader.VertexBufferSize.Length ||
            lodIndex >= mdl.FileHeader.IndexOffset.Length || lodIndex >= mdl.FileHeader.IndexBufferSize.Length)
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' has incomplete LOD{lodIndex} buffer header");
            return 0;
        }

        if (!TryGetIntRange(mdl.FileHeader.VertexOffset[lodIndex], mdl.FileHeader.VertexBufferSize[lodIndex], mdl.Data.Length, out _, out _) ||
            !TryGetIntRange(mdl.FileHeader.IndexOffset[lodIndex], mdl.FileHeader.IndexBufferSize[lodIndex], mdl.Data.Length, out _, out _))
        {
            log.Warning($"RagdollController: mesh collision '{modelPath}' LOD{lodIndex} buffer ranges are outside file data");
            return 0;
        }

        var processedMeshIndices = new HashSet<int>();
        var processedMeshes = 0;
        processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, lod.MeshIndex, lod.MeshCount,
            ns, skinDeltas, triangles, processedMeshIndices);

        if (mdl.ExtraLodEnabled && lodIndex < mdl.ExtraLods.Length)
        {
            var extra = mdl.ExtraLods[lodIndex];
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.GlassMeshIndex, extra.GlassMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
            processedMeshes += AppendSkinnedMdlMeshRange(modelPath, mdl, lodIndex, extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount,
                ns, skinDeltas, triangles, processedMeshIndices);
        }

        return processedMeshes;
    }

    private int AppendSkinnedMdlMeshRange(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        ushort meshStart,
        ushort meshCount,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles,
        HashSet<int> processedMeshIndices)
    {
        if (meshCount == 0)
            return 0;

        var meshEnd = Math.Min(mdl.Meshes.Length, meshStart + meshCount);
        var processedMeshes = 0;
        for (int meshIndex = meshStart; meshIndex < meshEnd && triangles.Count < MeshCollisionMaxTriangles; meshIndex++)
        {
            if (!processedMeshIndices.Add(meshIndex))
                continue;
            if (AppendSkinnedMdlMeshTriangles(modelPath, mdl, lodIndex, meshIndex, ns, skinDeltas, triangles))
                processedMeshes++;
        }

        return processedMeshes;
    }

    private bool AppendSkinnedMdlMeshTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        SkeletonAccess ns,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length || meshIndex >= mdl.VertexDeclarations.Length)
            return false;

        var mesh = mdl.Meshes[meshIndex];
        if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
            return false;

        var localToHavok = BuildMdlMeshBoneMap(mdl, mesh, ns);
        return AppendSkinnedMdlMeshTriangles(modelPath, mdl, lodIndex, meshIndex, localToHavok, skinDeltas, triangles);
    }

    private bool AppendSkinnedMdlMeshTriangles(
        string modelPath,
        MeshCollisionMdlData mdl,
        int lodIndex,
        int meshIndex,
        int[] localToHavok,
        Matrix4x4[] skinDeltas,
        List<Triangle> triangles)
    {
        if (meshIndex < 0 || meshIndex >= mdl.Meshes.Length || meshIndex >= mdl.VertexDeclarations.Length)
            return false;

        var mesh = mdl.Meshes[meshIndex];
        if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
            return false;

        var skinnedVertices = new Vector3[mesh.VertexCount];
        for (int i = 0; i < skinnedVertices.Length; i++)
        {
            var vertex = ReadMdlCollisionVertex(mdl.Data, mdl.FileHeader.VertexOffset[lodIndex],
                mesh, mdl.VertexDeclarations[meshIndex], i);
            skinnedVertices[i] = SkinVertex(vertex, localToHavok, skinDeltas);
        }

        var indexByteOffset = (ulong)mdl.FileHeader.IndexOffset[lodIndex] + ((ulong)mesh.StartIndex * 2UL);
        var indexByteLength = (ulong)mesh.IndexCount * 2UL;
        if (!TryGetIntRange(indexByteOffset, indexByteLength, mdl.Data.Length, out var indexStart, out _))
        {
            if (verboseLog)
                log.Warning($"RagdollController: mesh collision '{modelPath}' mesh {meshIndex} index range outside file data");
            return false;
        }

        var indexData = mdl.Data.AsSpan(indexStart, checked((int)indexByteLength));
        for (int i = 0; i + 2 < mesh.IndexCount && triangles.Count < MeshCollisionMaxTriangles; i += 3)
        {
            var ia = ReadUInt16(indexData, i * 2);
            var ib = ReadUInt16(indexData, (i + 1) * 2);
            var ic = ReadUInt16(indexData, (i + 2) * 2);
            if (ia >= skinnedVertices.Length || ib >= skinnedVertices.Length || ic >= skinnedVertices.Length)
                continue;

            var a = skinnedVertices[ia];
            var b = skinnedVertices[ib];
            var c = skinnedVertices[ic];
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                continue;
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-8f)
                continue;

            // Reverse the model's triangle winding. BEPU meshes are one-sided (a body only
            // collides with a triangle's front face). FFXIV model geometry is authored in a
            // left-handed space, so feeding its raw winding into BEPU's right-handed normal
            // convention points every face INWARD — the ragdoll then falls straight through
            // the mount's outer shell and catches on the interior. Swapping b/c flips the
            // normals outward so the corpse rests on the surface.
            triangles.Add(new Triangle(a, c, b));
        }

        return true;
    }

    private int[] BuildMdlMeshBoneMap(MeshCollisionMdlData mdl, MdlStructs.MeshStruct mesh, SkeletonAccess ns)
    {
        if (mesh.BoneTableIndex == 255 || mesh.BoneTableIndex >= mdl.BoneTables.Length)
            return Array.Empty<int>();

        var boneTable = mdl.BoneTables[mesh.BoneTableIndex].BoneIndex;
        if (boneTable == null || boneTable.Length == 0)
            return Array.Empty<int>();

        var result = new int[boneTable.Length];
        Array.Fill(result, -1);

        var resolved = 0;
        string? firstUnresolved = null;
        for (int i = 0; i < boneTable.Length; i++)
        {
            var globalBoneIndex = boneTable[i];
            if (globalBoneIndex >= mdl.BoneNameOffsets.Length)
                continue;

            var boneName = ReadNullTerminatedString(mdl.Strings, mdl.BoneNameOffsets[globalBoneIndex]);
            if (string.IsNullOrEmpty(boneName))
                continue;

            result[i] = ResolveBoneIndex(ns, boneName);
            if (result[i] >= 0)
                resolved++;
            else
                firstUnresolved ??= boneName;
        }

        // When no bone name resolves against the target skeleton, every vertex weighted to this
        // mesh falls back to its bind pose in SkinVertex — the collision hull freezes in the
        // reference pose instead of the animated one. Surface that: it's the prime suspect for a
        // mesh whose shape doesn't match the on-screen (posed) model.
        if (resolved == 0)
        {
            // Decisive dump: is the bone table / bone-name-offset section drifted, or is the
            // string base off? Show the raw global bone indices, the offsets they map to, and
            // the strings actually read there, plus the section sizes.
            var probe = new System.Text.StringBuilder();
            var probeCount = Math.Min(6, boneTable.Length);
            for (int i = 0; i < probeCount; i++)
            {
                var gi = boneTable[i];
                var off = gi < mdl.BoneNameOffsets.Length ? (long)mdl.BoneNameOffsets[gi] : -1;
                var name = off >= 0 ? ReadNullTerminatedString(mdl.Strings, (uint)off) : "<oob>";
                probe.Append($" [{i}]gi={gi}->off={off}'{name}'");
            }
            log.Warning($"RagdollController: mesh collision bone map resolved 0/{boneTable.Length} bones (e.g. '{firstUnresolved}'); btIdx={mesh.BoneTableIndex}, boneNameOffsets={mdl.BoneNameOffsets.Length}, stringsLen={mdl.Strings.Length};{probe}");
        }
        else if (verboseLog)
            log.Info($"RagdollController: mesh collision bone map resolved {resolved}/{boneTable.Length} bones");

        return result;
    }

    private struct MeshCollisionVertex
    {
        public Vector4? Position;
        public Vector4? BlendWeights;
        public byte[]? BlendIndices;
    }

    private static MeshCollisionVertex ReadMdlCollisionVertex(
        byte[] data,
        uint lodVertexOffset,
        MdlStructs.MeshStruct mesh,
        MdlStructs.VertexDeclarationStruct declaration,
        int vertexIndex)
    {
        var vertex = new MeshCollisionVertex();
        foreach (var element in declaration.VertexElements)
        {
            var usage = (CollisionVertexUsage)element.Usage;
            if (usage != CollisionVertexUsage.Position &&
                usage != CollisionVertexUsage.BlendWeights &&
                usage != CollisionVertexUsage.BlendIndices)
                continue;

            if (element.Stream >= mesh.VertexBufferOffset.Length ||
                element.Stream >= mesh.VertexBufferStride.Length)
                continue;

            var stride = mesh.VertexBufferStride[element.Stream];
            if (stride == 0)
                continue;

            var elementSize = GetVertexElementSize(element.Type);
            if (elementSize <= 0)
                continue;

            var elementOffset = (ulong)lodVertexOffset +
                                mesh.VertexBufferOffset[element.Stream] +
                                ((ulong)vertexIndex * stride) +
                                element.Offset;
            if (!TryGetIntRange(elementOffset, (uint)elementSize, data.Length, out var start, out var length))
                continue;

            var bytes = data.AsSpan(start, length);
            switch (usage)
            {
                case CollisionVertexUsage.Position:
                    if (TryReadVertexVector(bytes, element.Type, out var position))
                        vertex.Position = position;
                    break;
                case CollisionVertexUsage.BlendWeights:
                    if (TryReadVertexVector(bytes, element.Type, out var weights))
                        vertex.BlendWeights = weights;
                    break;
                case CollisionVertexUsage.BlendIndices:
                    if (element.Type == (byte)CollisionVertexType.UInt && bytes.Length >= 4)
                        vertex.BlendIndices = new[] { bytes[0], bytes[1], bytes[2], bytes[3] };
                    break;
            }
        }

        return vertex;
    }

    private static Vector3 SkinVertex(MeshCollisionVertex vertex, int[] localToHavok, Matrix4x4[] skinDeltas)
    {
        if (vertex.Position == null)
            return Vector3.Zero;

        var p4 = vertex.Position.Value;
        var bindPos = new Vector3(p4.X, p4.Y, p4.Z);
        if (vertex.BlendWeights == null || vertex.BlendIndices == null || localToHavok.Length == 0)
            return bindPos;

        var weights = vertex.BlendWeights.Value;
        var blendIndices = vertex.BlendIndices;
        var skinned = Vector3.Zero;
        var weightSum = 0f;
        var influenceCount = Math.Min(4, blendIndices.Length);
        for (int i = 0; i < influenceCount; i++)
        {
            var weight = GetBlendWeight(weights, i);
            if (weight <= 0f)
                continue;

            var localBoneIndex = blendIndices[i];
            if (localBoneIndex >= localToHavok.Length)
                continue;

            var havokIndex = localToHavok[localBoneIndex];
            if (havokIndex < 0 || havokIndex >= skinDeltas.Length)
                continue;

            skinned += Vector3.Transform(bindPos, skinDeltas[havokIndex]) * weight;
            weightSum += weight;
        }

        return weightSum > 1e-5f ? skinned / weightSum : bindPos;
    }

    private static float GetBlendWeight(Vector4 weights, int index) => index switch
    {
        0 => weights.X,
        1 => weights.Y,
        2 => weights.Z,
        3 => weights.W,
        _ => 0f,
    };

    private enum CollisionVertexType : byte
    {
        Single3 = 2,
        Single4 = 3,
        UInt = 5,
        ByteFloat4 = 8,
        Half2 = 13,
        Half4 = 14,
    }

    private enum CollisionVertexUsage : byte
    {
        Position = 0,
        BlendWeights = 1,
        BlendIndices = 2,
    }

    private static int GetVertexElementSize(byte type) => (CollisionVertexType)type switch
    {
        CollisionVertexType.Single3 => 12,
        CollisionVertexType.Single4 => 16,
        CollisionVertexType.UInt => 4,
        CollisionVertexType.ByteFloat4 => 4,
        CollisionVertexType.Half2 => 4,
        CollisionVertexType.Half4 => 8,
        _ => 0,
    };

    private static bool TryReadVertexVector(ReadOnlySpan<byte> bytes, byte type, out Vector4 value)
    {
        value = default;
        switch ((CollisionVertexType)type)
        {
            case CollisionVertexType.Single3:
                if (bytes.Length < 12) return false;
                value = new Vector4(ReadSingle(bytes, 0), ReadSingle(bytes, 4), ReadSingle(bytes, 8), 1f);
                return true;
            case CollisionVertexType.Single4:
                if (bytes.Length < 16) return false;
                value = new Vector4(ReadSingle(bytes, 0), ReadSingle(bytes, 4), ReadSingle(bytes, 8), ReadSingle(bytes, 12));
                return true;
            case CollisionVertexType.ByteFloat4:
                if (bytes.Length < 4) return false;
                value = new Vector4(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
                return true;
            case CollisionVertexType.Half2:
                if (bytes.Length < 4) return false;
                value = new Vector4(ReadHalf(bytes, 0), ReadHalf(bytes, 2), 0f, 0f);
                return true;
            case CollisionVertexType.Half4:
                if (bytes.Length < 8) return false;
                value = new Vector4(ReadHalf(bytes, 0), ReadHalf(bytes, 2), ReadHalf(bytes, 4), ReadHalf(bytes, 6));
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseRawMdlCollisionData(byte[] data, out MeshCollisionMdlData mdl, out string? error)
    {
        mdl = new MeshCollisionMdlData();
        error = null;
        try
        {
            var span = data.AsSpan();
            var offset = 0;

            if (!TryReadModelFileHeader(span, ref offset, out var fileHeader))
                return FailRawMdl("file header truncated", out mdl, out error);

            if (fileHeader.VertexDeclarationCount > 512)
                return FailRawMdl($"unreasonable vertex declaration count {fileHeader.VertexDeclarationCount}", out mdl, out error);

            var vertexDeclarations = new MdlStructs.VertexDeclarationStruct[fileHeader.VertexDeclarationCount];
            for (int i = 0; i < vertexDeclarations.Length; i++)
            {
                if (!TryReadVertexDeclaration(span, ref offset, out vertexDeclarations[i]))
                    return FailRawMdl($"vertex declaration {i} truncated", out mdl, out error);
            }

            if (!TryReadUInt16(span, ref offset, out var stringCount) ||
                !TrySkip(span, ref offset, 2) ||
                !TryReadUInt32(span, ref offset, out var stringByteCount))
                return FailRawMdl("string table header truncated", out mdl, out error);

            if (stringCount > 20000 || stringByteCount > data.Length)
                return FailRawMdl($"unreasonable string table count={stringCount} bytes={stringByteCount}", out mdl, out error);
            if (!TryReadBytes(span, ref offset, checked((int)stringByteCount), out var strings))
                return FailRawMdl("string table truncated", out mdl, out error);

            if (!TryReadModelHeader(span, ref offset, out var header, out var extraLodEnabled))
                return FailRawMdl("model header truncated", out mdl, out error);
            if (header.MeshCount > 20000 || header.BoneCount > 20000 || header.BoneTableCount > 4096)
                return FailRawMdl($"unreasonable model counts meshes={header.MeshCount} bones={header.BoneCount} boneTables={header.BoneTableCount}", out mdl, out error);

            if (!TrySkip(span, ref offset, checked(header.ElementIdCount * 32)))
                return FailRawMdl("element id section truncated", out mdl, out error);

            var lods = new MdlStructs.LodStruct[3];
            for (int i = 0; i < lods.Length; i++)
            {
                if (!TryReadLod(span, ref offset, out lods[i]))
                    return FailRawMdl($"lod {i} truncated", out mdl, out error);
            }

            var extraLods = Array.Empty<MdlStructs.ExtraLodStruct>();
            if (extraLodEnabled)
            {
                extraLods = new MdlStructs.ExtraLodStruct[3];
                for (int i = 0; i < extraLods.Length; i++)
                {
                    if (!TryReadExtraLod(span, ref offset, out extraLods[i]))
                        return FailRawMdl($"extra lod {i} truncated", out mdl, out error);
                }
            }

            var meshes = new MdlStructs.MeshStruct[header.MeshCount];
            for (int i = 0; i < meshes.Length; i++)
            {
                if (!TryReadMesh(span, ref offset, out meshes[i]))
                    return FailRawMdl($"mesh {i} truncated", out mdl, out error);
            }

            if (!TrySkip(span, ref offset, checked(header.AttributeCount * 4)))
                return FailRawMdl("attribute offsets truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.TerrainShadowMeshCount * 20)))
                return FailRawMdl("terrain shadow meshes truncated", out mdl, out error);
            // SubmeshStruct is 16 bytes (uint IndexOffset + uint IndexCount + uint
            // AttributeIndexMask + ushort BoneStartIndex + ushort BoneCount), NOT 20.
            // Over-skipping by 4 bytes/submesh drifted the cursor past BoneNameOffsets into
            // the bone tables, so bone name lookups read garbage and skinning silently fell
            // back to the bind pose.
            if (!TrySkip(span, ref offset, checked(header.SubmeshCount * 16)))
                return FailRawMdl("submeshes truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.TerrainShadowSubmeshCount * 12)))
                return FailRawMdl("terrain shadow submeshes truncated", out mdl, out error);
            if (!TrySkip(span, ref offset, checked(header.MaterialCount * 4)))
                return FailRawMdl("material offsets truncated", out mdl, out error);

            var boneNameOffsets = new uint[header.BoneCount];
            for (int i = 0; i < boneNameOffsets.Length; i++)
            {
                if (!TryReadUInt32(span, ref offset, out boneNameOffsets[i]))
                    return FailRawMdl($"bone name offset {i} truncated", out mdl, out error);
            }

            var boneTables = new MdlStructs.BoneTableStruct[header.BoneTableCount];
            for (int i = 0; i < boneTables.Length; i++)
            {
                if (!TryReadBoneTable(span, ref offset, out boneTables[i]))
                    return FailRawMdl($"bone table {i} truncated", out mdl, out error);
            }

            mdl = new MeshCollisionMdlData
            {
                Data = data,
                FileHeader = fileHeader,
                VertexDeclarations = vertexDeclarations,
                Lods = lods,
                ExtraLods = extraLods,
                Meshes = meshes,
                BoneNameOffsets = boneNameOffsets,
                BoneTables = boneTables,
                Strings = strings.ToArray(),
                ExtraLodEnabled = extraLodEnabled,
            };
            return true;
        }
        catch (Exception ex)
        {
            return FailRawMdl(ex.Message, out mdl, out error);
        }
    }

    private static bool FailRawMdl(string message, out MeshCollisionMdlData mdl, out string error)
    {
        mdl = new MeshCollisionMdlData();
        error = message;
        return false;
    }

    private static bool TryReadModelFileHeader(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ModelFileHeader header)
    {
        header = default;
        if (!TryReadUInt32(data, ref offset, out header.Version) ||
            !TryReadUInt32(data, ref offset, out header.StackSize) ||
            !TryReadUInt32(data, ref offset, out header.RuntimeSize) ||
            !TryReadUInt16(data, ref offset, out header.VertexDeclarationCount) ||
            !TryReadUInt16(data, ref offset, out header.MaterialCount))
            return false;

        header.VertexOffset = new uint[3];
        header.IndexOffset = new uint[3];
        header.VertexBufferSize = new uint[3];
        header.IndexBufferSize = new uint[3];
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.VertexOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.IndexOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.VertexBufferSize[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out header.IndexBufferSize[i])) return false;
        if (!TryReadByte(data, ref offset, out header.LodCount) ||
            !TrySkip(data, ref offset, 3))
            return false;
        return true;
    }

    private static bool TryReadVertexDeclaration(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.VertexDeclarationStruct declaration)
    {
        declaration = default;
        if (!TryGetIntRange((ulong)offset, 136, data.Length, out var start, out _))
            return false;

        var elements = new List<MdlStructs.VertexElement>();
        for (int elementOffset = 0; elementOffset + 8 <= 136; elementOffset += 8)
        {
            var stream = data[start + elementOffset];
            if (stream == byte.MaxValue)
                break;
            elements.Add(new MdlStructs.VertexElement
            {
                Stream = stream,
                Offset = data[start + elementOffset + 1],
                Type = data[start + elementOffset + 2],
                Usage = data[start + elementOffset + 3],
                UsageIndex = data[start + elementOffset + 4],
            });
        }

        offset += 136;
        declaration.VertexElements = elements.ToArray();
        return true;
    }

    private static bool TryReadModelHeader(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ModelHeader header, out bool extraLodEnabled)
    {
        header = default;
        extraLodEnabled = false;
        if (!TryReadSingle(data, ref offset, out header.Radius) ||
            !TryReadUInt16(data, ref offset, out header.MeshCount) ||
            !TryReadUInt16(data, ref offset, out header.AttributeCount) ||
            !TryReadUInt16(data, ref offset, out header.SubmeshCount) ||
            !TryReadUInt16(data, ref offset, out header.MaterialCount) ||
            !TryReadUInt16(data, ref offset, out header.BoneCount) ||
            !TryReadUInt16(data, ref offset, out header.BoneTableCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeMeshCount) ||
            !TryReadUInt16(data, ref offset, out header.ShapeValueCount) ||
            !TryReadByte(data, ref offset, out header.LodCount) ||
            !TryReadByte(data, ref offset, out _) ||
            !TryReadUInt16(data, ref offset, out header.ElementIdCount) ||
            !TryReadByte(data, ref offset, out header.TerrainShadowMeshCount) ||
            !TryReadByte(data, ref offset, out var flags2) ||
            !TryReadSingle(data, ref offset, out header.ModelClipOutDistance) ||
            !TryReadSingle(data, ref offset, out header.ShadowClipOutDistance) ||
            !TryReadUInt16(data, ref offset, out header.Unknown4) ||
            !TryReadUInt16(data, ref offset, out header.TerrainShadowSubmeshCount) ||
            !TryReadByte(data, ref offset, out _) ||
            !TryReadByte(data, ref offset, out header.BGChangeMaterialIndex) ||
            !TryReadByte(data, ref offset, out header.BGCrestChangeMaterialIndex) ||
            !TryReadByte(data, ref offset, out header.Unknown6) ||
            !TryReadUInt16(data, ref offset, out header.Unknown7) ||
            !TryReadUInt16(data, ref offset, out header.Unknown8) ||
            !TryReadUInt16(data, ref offset, out header.Unknown9) ||
            !TrySkip(data, ref offset, 6))
            return false;

        extraLodEnabled = (flags2 & 0x10) != 0;
        return true;
    }

    private static bool TryReadLod(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.LodStruct lod)
    {
        lod = default;
        return TryReadUInt16(data, ref offset, out lod.MeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.MeshCount) &&
               TryReadSingle(data, ref offset, out lod.ModelLodRange) &&
               TryReadSingle(data, ref offset, out lod.TextureLodRange) &&
               TryReadUInt16(data, ref offset, out lod.WaterMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.WaterMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.ShadowMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.ShadowMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.TerrainShadowMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.TerrainShadowMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.VerticalFogMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.VerticalFogMeshCount) &&
               TryReadUInt32(data, ref offset, out lod.EdgeGeometrySize) &&
               TryReadUInt32(data, ref offset, out lod.EdgeGeometryDataOffset) &&
               TryReadUInt32(data, ref offset, out lod.PolygonCount) &&
               TryReadUInt32(data, ref offset, out lod.Unknown1) &&
               TryReadUInt32(data, ref offset, out lod.VertexBufferSize) &&
               TryReadUInt32(data, ref offset, out lod.IndexBufferSize) &&
               TryReadUInt32(data, ref offset, out lod.VertexDataOffset) &&
               TryReadUInt32(data, ref offset, out lod.IndexDataOffset);
    }

    private static bool TryReadExtraLod(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.ExtraLodStruct lod)
    {
        lod = default;
        return TryReadUInt16(data, ref offset, out lod.LightShaftMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.LightShaftMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.GlassMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.GlassMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.MaterialChangeMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.MaterialChangeMeshCount) &&
               TryReadUInt16(data, ref offset, out lod.CrestChangeMeshIndex) &&
               TryReadUInt16(data, ref offset, out lod.CrestChangeMeshCount) &&
               TrySkip(data, ref offset, 24);
    }

    private static bool TryReadMesh(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.MeshStruct mesh)
    {
        mesh = default;
        mesh.VertexBufferOffset = new uint[3];
        mesh.VertexBufferStride = new byte[3];
        if (!TryReadUInt16(data, ref offset, out mesh.VertexCount) ||
            !TrySkip(data, ref offset, 2) ||
            !TryReadUInt32(data, ref offset, out mesh.IndexCount) ||
            !TryReadUInt16(data, ref offset, out mesh.MaterialIndex) ||
            !TryReadUInt16(data, ref offset, out mesh.SubMeshIndex) ||
            !TryReadUInt16(data, ref offset, out mesh.SubMeshCount) ||
            !TryReadUInt16(data, ref offset, out mesh.BoneTableIndex) ||
            !TryReadUInt32(data, ref offset, out mesh.StartIndex))
            return false;

        for (int i = 0; i < 3; i++)
            if (!TryReadUInt32(data, ref offset, out mesh.VertexBufferOffset[i])) return false;
        for (int i = 0; i < 3; i++)
            if (!TryReadByte(data, ref offset, out mesh.VertexBufferStride[i])) return false;
        return TryReadByte(data, ref offset, out mesh.VertexStreamCount);
    }

    private static bool TryReadBoneTable(ReadOnlySpan<byte> data, ref int offset, out MdlStructs.BoneTableStruct table)
    {
        table = default;
        table.BoneIndex = new ushort[64];
        for (int i = 0; i < table.BoneIndex.Length; i++)
            if (!TryReadUInt16(data, ref offset, out table.BoneIndex[i])) return false;
        if (!TryReadByte(data, ref offset, out table.BoneCount))
            return false;
        return TrySkip(data, ref offset, 3);
    }

    private static bool TrySelectMdlLod(MeshCollisionMdlData mdl, out int lodIndex, out MdlStructs.LodStruct lod)
    {
        lodIndex = -1;
        lod = default;
        if (mdl.Lods == null || mdl.Lods.Length == 0)
            return false;

        var lodCount = Math.Min(mdl.Lods.Length, Math.Max(1, (int)mdl.FileHeader.LodCount));
        var preferred = Math.Min(MeshCollisionPreferredLod, lodCount - 1);
        for (int i = preferred; i >= 0; i--)
        {
            if (mdl.Lods[i].MeshCount == 0)
                continue;
            lodIndex = i;
            lod = mdl.Lods[i];
            return true;
        }

        for (int i = preferred + 1; i < lodCount; i++)
        {
            if (mdl.Lods[i].MeshCount == 0)
                continue;
            lodIndex = i;
            lod = mdl.Lods[i];
            return true;
        }

        return false;
    }

    private static bool TryGetIntRange(ulong offset, ulong length, int dataLength, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (offset > int.MaxValue || length > int.MaxValue)
            return false;
        var end = offset + length;
        if (end < offset || end > (ulong)dataLength)
            return false;
        start = (int)offset;
        count = (int)length;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > data.Length || count > data.Length - offset)
            return false;
        offset += count;
        return true;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> data, ref int offset, int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (count < 0 || offset < 0 || offset > data.Length || count > data.Length - offset)
            return false;
        value = data.Slice(offset, count);
        offset += count;
        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        value = 0;
        if (offset < 0 || offset >= data.Length)
            return false;
        value = data[offset++];
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 2)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 4)
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, ref int offset, out float value)
    {
        value = 0f;
        if (offset < 0 || offset > data.Length - 4)
            return false;
        value = ReadSingle(data, offset);
        offset += 4;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));

    private static float ReadHalf(ReadOnlySpan<byte> data, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)));

    private static int TryGetRawFileLength(string modelPath)
    {
        try
        {
            return Services.DataManager.GameData.GetFile(modelPath)?.Data?.Length ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static byte[]? TryGetRawFileData(string modelPath)
    {
        try
        {
            return Services.DataManager.GameData.GetFile(modelPath)?.Data;
        }
        catch
        {
            return null;
        }
    }

    private static Matrix4x4 QsToMatrix(HkQsTransform transform)
        => QsToMatrix(System.Runtime.CompilerServices.Unsafe.As<HkQsTransform, BoneQs>(ref transform));

    private static string ReadNullTerminatedString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length)
            return string.Empty;

        var start = (int)offset;
        var end = start;
        while (end < strings.Length && strings[end] != 0)
            end++;

        return Encoding.UTF8.GetString(strings, start, end - start);
    }
}
