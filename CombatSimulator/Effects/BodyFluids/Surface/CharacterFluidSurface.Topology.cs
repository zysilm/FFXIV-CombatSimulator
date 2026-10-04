// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using Lumina.Data.Parsing;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private void BuildTopology(SkeletonAccess ns)
    {
        CancelTopologyMetadata();
        var snapshotTimer = Stopwatch.StartNew();
        topologyDiagnostics.Clear(); diagnosticSeed = -1;
        awaitedModelLoads.Clear(); topologyAwaitingModels = false;
        awaitedPbdLoads.Clear(); awaitedDeformedMeshes.Clear();
        deformationDeferred = false;
        builtFaceOnlyScope = FaceOnlyCapture;
        builtRaceDeformation = ApplyRaceDeformation;
        DeformationStatus = ApplyRaceDeformation ? "Resolved PBD initialization; native slot chain unobserved" : "Raw diagnostic CPU surface; PBD correction disabled";
        var contextForBuild = deformationContext;
#if DEV_EXPERIMENTAL
        if (!ApplyRaceDeformation)
            deformationSource.TryCaptureContext((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)ns.CharBase,
                ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor)->ObjectIndex,
                out contextForBuild, out _);
#endif
        var points = new List<Vertex>();
        var triangles = new List<Face>();
        int omitted = 0, loaded = 0;
        int deformedSlots = 0, rawUnknownSlots = 0, deformedRejected = 0, deformedVertices = 0;
        string firstDeformationFailure = "";
        for (int slot = 0; slot < Math.Clamp(ns.CharBase->SlotCount, 0, MaxSlots); slot++)
        {
            if (FaceOnlyCapture && slot != 11) continue;
            var model = ns.CharBase->Models == null ? null : ns.CharBase->Models[slot];
            if (model == null || model->ModelResourceHandle == null)
            {
                Services.Log.Info($"Body fluid topology generation={Generation} slot={slot} path='<unavailable>' faces=0 validVerts=0 reason=no render model/resource handle");
                continue;
            }
            // Resource paths are UTF-8 (including Penumbra disk paths). StdString.ToString()
            // uses Windows ACP and corrupts non-ASCII filenames on non-UTF-8 system locales.
            var nativeFileName = model->ModelResourceHandle->FileName;
            if (nativeFileName.Length == 0 || nativeFileName.Length > 4096 || nativeFileName.LongCount > 4096)
            {
                omitted++;
                topologyDiagnostics.Add($"slot={slot} unsupported native model path length={nativeFileName.Length}");
                continue;
            }
            string path = Encoding.UTF8.GetString(nativeFileName.AsSpan());
            if (path.StartsWith('|')) { int end = path.IndexOf('|', 1); if (end >= 0) path = path[(end + 1)..]; }
            FluidRaceDeformationSource.Snapshot? deformation = null;
            string deformationReason = deformationContextFailure;
            bool captureDeformation = ApplyRaceDeformation;
#if DEV_EXPERIMENTAL
            captureDeformation = true;
#endif
            bool deformationCaptured = captureDeformation && contextForBuild != null && deformationSource.TryCaptureSlot(
                (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)ns.CharBase,
                (nint)model, slot, path, contextForBuild, out deformation, out deformationReason);
#if DEV_EXPERIMENTAL
            // Preserve the live metadata proof; enabling correction still does not prove native slot state.
            if (deformationCaptured && deformation != null)
                Services.Log.Info($"Body fluid PBD candidate slot={slot} source={deformation.SourceRace} target={deformation.TargetRace} " +
                    $"original='{deformation.OriginalModelPath}' pbd='{deformation.ResolvedPbdPath}' collection={deformation.CollectionId} " +
                    $"nativeSlotVerified={deformation.NativeSlotChainVerified}; diagnosticApply={ApplyRaceDeformation}");
            else
                Services.Log.Info($"Body fluid PBD metadata unavailable slot={slot}: {deformationReason}; " +
                    (ApplyRaceDeformation ? "slot omitted from collision" : "raw diagnostic geometry only"));
#endif
            var diagnostic = new SlotDiagnostic();
            int before = triangles.Count;
            string reason = "supported";
            try
            {
                FluidRaceDeformation.Chain? chain = null;
                bool pbdPending = false;
                diagnostic.Deformation = ApplyRaceDeformation ? "omitted/unknown: " + deformationReason : "raw/uncorrected: diagnostic only";
                if (ApplyRaceDeformation && !deformationCaptured)
                { omitted++; reason = "racial deformation metadata unsupported: " + deformationReason; continue; }
                if (ApplyRaceDeformation && deformationCaptured && deformation != null)
                {
                    chain = AcquireDeformation(deformation, out pbdPending, out diagnostic.Deformation);
                    diagnostic.DeformationApplied = chain != null;
                    DeformationStatus = diagnostic.Deformation;
                }
                var mdl = AcquireModel(path, Path.IsPathRooted(path), (nint)model->ModelResourceHandle,
                    (nint)model->ModelResourceHandle->ModelData, out diagnostic.Parser, out bool pending);
                if (pending || pbdPending) { topologyAwaitingModels = true; reason = "managed model/PBD decode pending"; continue; }
                if (ApplyRaceDeformation && deformationCaptured && deformation?.RequiresDeformation == true && chain == null)
                { omitted++; reason = "resolved PBD unsupported: " + diagnostic.Deformation; continue; }
                if (mdl == null || mdl.Lods.Length == 0 || mdl.Data.Length == 0) { omitted++; reason = "no model data / unsupported parser"; continue; }
                var lod = mdl.Lods[0];
                var activeShapes = new bool[mdl.Shapes.Length];
                uint resolvedMask = 0;
                for (int si = 0; si < mdl.Shapes.Length; si++)
                {
                    var name = ReadString(mdl.Strings, mdl.Shapes[si].StringOffset);
                    int nativeIndex = name.Length == 0 ? -1 : model->GetShapeIndex(name);
                    topologyDiagnostics.Add($"slot={slot} shape={si} name='{name}' nativeIndex={nativeIndex} " +
                        $"lod0MeshStart={(mdl.Shapes[si].ShapeMeshStartIndex?.Length > 0 ? mdl.Shapes[si].ShapeMeshStartIndex[0] : -1)} " +
                        $"lod0MeshCount={(mdl.Shapes[si].ShapeMeshCount?.Length > 0 ? mdl.Shapes[si].ShapeMeshCount[0] : -1)}");
                    if (nativeIndex < 0 || nativeIndex >= 32) continue;
                    uint bit = 1u << nativeIndex;
                    resolvedMask |= bit;
                    activeShapes[si] = (model->EnabledShapeKeyIndexMask & bit) != 0;
                }
                if ((model->EnabledShapeKeyIndexMask & ~resolvedMask) != 0)
                { omitted++; reason = "active native shape has no managed name mapping"; continue; }
                for (int mi = lod.MeshIndex; mi < Math.Min(mdl.Meshes.Length, lod.MeshIndex + lod.MeshCount); mi++)
                    ReadMesh(mdl, slot, mi, model->EnabledAttributeIndexMask, activeShapes, ns, points, triangles, diagnostic, chain);
                if (diagnostic.DeformationPending) { topologyAwaitingModels = true; reason = "managed PBD vertex snapshot pending"; continue; }
                if (triangles.Count > before) loaded++; else { omitted++; reason = "no supported faces"; }
            }
            catch (Exception e)
            {
                omitted++;
                reason = $"exception {e.GetType().Name}: {e.Message}";
            }
            finally
            {
                if (ApplyRaceDeformation)
                {
                    if (!deformationCaptured) rawUnknownSlots++;
                    if (diagnostic.DeformationApplied && triangles.Count > before) deformedSlots++;
                    if (diagnostic.DeformationApplied) deformedVertices += diagnostic.ValidVertices;
                    deformedRejected += diagnostic.DeformationRejected;
                    if (firstDeformationFailure.Length == 0 && diagnostic.DeformationRejectReason.Length > 0)
                        firstDeformationFailure = diagnostic.DeformationRejectReason;
                    else if (firstDeformationFailure.Length == 0 && (reason.StartsWith("resolved PBD unsupported", StringComparison.Ordinal) ||
                        reason.StartsWith("racial deformation metadata unsupported", StringComparison.Ordinal)))
                        firstDeformationFailure = diagnostic.Deformation;
                }
                topologyDiagnostics.Add($"slot={slot} path='{path}' shapeMask=0x{model->EnabledShapeKeyIndexMask:X8} attributes=0x{model->EnabledAttributeIndexMask:X8} faces={triangles.Count - before} parser={diagnostic.Parser} pbd='{diagnostic.Deformation}' pbdRejectedVerts={diagnostic.DeformationRejected} pbdFirstReject='{diagnostic.DeformationRejectReason}' reason={reason}");
                Services.Log.Info($"Body fluid topology generation={Generation} slot={slot} path='{path}' " +
                    $"shapeMask=0x{model->EnabledShapeKeyIndexMask:X8} attributeMask=0x{model->EnabledAttributeIndexMask:X8} " +
                    $"nativeShapeCount={model->ModelResourceHandle->Shapes.LongCount} " +
                    $"parser={diagnostic.Parser} faces={triangles.Count - before} validVerts={diagnostic.ValidVertices}/{diagnostic.Vertices} " +
                    $"meshes={diagnostic.Meshes} missingDeclarations={diagnostic.MissingDeclarations} invalidBoneTables={diagnostic.InvalidBoneTables} " +
                    $"capacityRejectedMeshes={diagnostic.CapacityRejectedMeshes} submeshesVisible={diagnostic.VisibleSubmeshes} " +
                    $"submeshesHidden={diagnostic.HiddenSubmeshes} badSubmeshRanges={diagnostic.BadSubmeshRanges} " +
                    $"weightedBoneRejects={diagnostic.WeightedBoneRejects} unmappedWeightedBones=[{string.Join(",", diagnostic.MissingBones)}] " +
                    $"pbd='{diagnostic.Deformation}' pbdRejectedVerts={diagnostic.DeformationRejected} pbdFirstReject='{diagnostic.DeformationRejectReason}' reason={reason}");
            }
        }
        if (ApplyRaceDeformation)
            DeformationStatus = $"Resolved PBD: deformedSlots={deformedSlots}, correctedVerts={deformedVertices}, rejectedVerts={deformedRejected}, " +
                $"unknownOmittedSlots={rawUnknownSlots}, pending={topologyAwaitingModels}, firstFailure='{firstDeformationFailure}'; native slot chain unobserved";
        // A full request is an atomic batch. Never expose a partially decoded actor to collision queries.
        if (topologyAwaitingModels)
        {
            vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
            currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
            triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
            diagnosticFaceTriangles = Array.Empty<int>(); lipCandidateTriangles = Array.Empty<int>();
            Status = "Managed model/PBD/vertex batch pending; emission disabled";
            LastTopologySnapshotMilliseconds = snapshotTimer.Elapsed.TotalMilliseconds;
            return;
        }
        QueueTopologyMetadata(points.ToArray(), triangles.ToArray(), loaded, omitted);
        LastTopologySnapshotMilliseconds = snapshotTimer.Elapsed.TotalMilliseconds;
    }

    private void FinishTopologyMetadata(int loaded, int omitted)
    {
        var facialTriangles = new List<int>();
        for (int i = 0; i < faces.Length; i++)
        {
            var v = vertices[faces[i].A];
            if ((v.Weights.X > 0 && IsFacialBone(v.B0)) || (v.Weights.Y > 0 && IsFacialBone(v.B1)) ||
                (v.Weights.Z > 0 && IsFacialBone(v.B2)) || (v.Weights.W > 0 && IsFacialBone(v.B3)) ||
                (v.ExtraWeights.X > 0 && IsFacialBone(v.B4)) || (v.ExtraWeights.Y > 0 && IsFacialBone(v.B5)) ||
                (v.ExtraWeights.Z > 0 && IsFacialBone(v.B6)) || (v.ExtraWeights.W > 0 && IsFacialBone(v.B7))) facialTriangles.Add(i);
        }
        diagnosticFaceTriangles = facialTriangles.ToArray();
        diagnosticSeed = facialTriangles.Count > 0 ? facialTriangles[0] : -1;
        BuildLipCandidates();
        Status = topologyAwaitingModels ? "Managed face decode pending; emission disabled" : faces.Length == 0 ? "Visible surface unsupported; emission disabled" :
            omitted == 0 ? "CPU LOD0 surface; shader deformation unverified" : "Partial CPU surface; unsupported models omitted";
        Services.Log.Info($"Body fluids: surface generation={Generation}, models={loaded}, omitted={omitted}, triangles={faces.Length}, vertices={vertices.Length}.");
    }

    private sealed class SlotDiagnostic
    {
        public string Parser = "not loaded";
        public string Deformation = "raw/uncorrected", DeformationRejectReason = "";
        public int DeformationRejected;
        public bool DeformationPending, DeformationApplied;
        public int Vertices, ValidVertices, Meshes, MissingDeclarations, InvalidBoneTables, CapacityRejectedMeshes;
        public int VisibleSubmeshes, HiddenSubmeshes, BadSubmeshRanges, WeightedBoneRejects;
        public readonly List<string> MissingBones = new(5);
    }

    private void ReadMesh(FluidModelData mdl, int slot, int mi, uint attributes, bool[] activeShapes, SkeletonAccess ns, List<Vertex> points, List<Face> triangles, SlotDiagnostic diagnostic, FluidRaceDeformation.Chain? deformation = null)
    {
        diagnostic.Meshes++;
        if (mi >= mdl.VertexDeclarations.Length) { diagnostic.MissingDeclarations++; return; }
        var mesh = mdl.Meshes[mi];
        if (!ResolveShapeIndices(mdl, mesh, activeShapes, out var replacements, out var shapeReason))
        {
            topologyDiagnostics.Add($"slot={slot} mesh={mi} unsupported={shapeReason}");
            Services.Log.Info($"Body fluid mesh slot={slot} mesh={mi} unsupported: {shapeReason}");
            return;
        }
        if (mesh.BoneTableIndex == 255 || mesh.BoneTableIndex >= mdl.BoneTables.Length) { diagnostic.InvalidBoneTables++; return; }
        if (points.Count + mesh.VertexCount > 80000 || triangles.Count + mesh.IndexCount / 3 > 100000) { diagnostic.CapacityRejectedMeshes++; return; }
        var table = mdl.BoneTables[mesh.BoneTableIndex].BoneIndex;
        var map = new int[table.Length]; Array.Fill(map, -1);
        var mapNames = new string[table.Length];
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] >= mdl.BoneNameOffsets.Length) continue;
            uint start = mdl.BoneNameOffsets[table[i]];
            if (start >= mdl.Strings.Length) continue;
            int end = (int)start;
            while (end < mdl.Strings.Length && mdl.Strings[end] != 0) end++;
            mapNames[i] = Encoding.UTF8.GetString(mdl.Strings, (int)start, end - (int)start);
            map[i] = ResolveSurfaceBone(mapNames[i]);
        }
        int baseVertex = points.Count;
        int baseTriangle = triangles.Count;
        var valid = new bool[mesh.VertexCount];
        DeformedMeshResult? deformed = null;
        if (deformation != null)
        {
            deformed = AcquireDeformedMesh(mdl, mi, map, mapNames, deformation, out bool pending, out var deformReason);
            if (pending) { diagnostic.DeformationPending = true; return; }
            if (deformed == null) { diagnostic.DeformationRejectReason = deformReason; return; }
            diagnostic.DeformationRejected += deformed.Rejected;
            if (diagnostic.DeformationRejectReason.Length == 0) diagnostic.DeformationRejectReason = deformed.Reason;
        }
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            diagnostic.Vertices++;
            Vertex point;
            int missingBone = -1;
            if (deformed != null) { point = deformed.Vertices[i]; valid[i] = deformed.Valid[i]; }
            else valid[i] = ReadVertex(mdl, mesh, mdl.VertexDeclarations[mi], i, map, out point, out missingBone);
            if (valid[i]) diagnostic.ValidVertices++;
            if (missingBone >= 0)
            {
                diagnostic.WeightedBoneRejects++;
                if (diagnostic.MissingBones.Count < 5)
                {
                    string name = missingBone < mapNames.Length ? mapNames[missingBone] ?? $"missing-name:{missingBone}" : $"local-index-out-of-range:{missingBone}";
                    if (!diagnostic.MissingBones.Contains(name)) diagnostic.MissingBones.Add(name);
                }
            }
            points.Add(point);
        }
        if (mesh.SubMeshCount == 0) AddIndices(mdl, mesh, slot, mi, mesh.StartIndex, mesh.IndexCount, replacements, baseVertex, valid, triangles);
        else
        {
            if (mesh.SubMeshIndex + mesh.SubMeshCount > mdl.Submeshes.Length) diagnostic.BadSubmeshRanges++;
            for (int s = mesh.SubMeshIndex; s < Math.Min(mdl.Submeshes.Length, mesh.SubMeshIndex + mesh.SubMeshCount); s++)
            {
                var sub = mdl.Submeshes[s];
                if ((sub.AttributeIndexMask & attributes) != sub.AttributeIndexMask) { diagnostic.HiddenSubmeshes++; continue; }
                diagnostic.VisibleSubmeshes++;
                if (sub.IndexOffset < mesh.StartIndex || (ulong)sub.IndexOffset + sub.IndexCount > (ulong)mesh.StartIndex + mesh.IndexCount)
                { diagnostic.BadSubmeshRanges++; continue; }
                AddIndices(mdl, mesh, slot, mi, sub.IndexOffset, sub.IndexCount, replacements, baseVertex, valid, triangles);
            }
        }
        topologyDiagnostics.Add($"slot={slot} mesh={mi} boneTable={mesh.BoneTableIndex} vertexCount={mesh.VertexCount} resolvedReplacements={replacements.Count} " +
            $"triangleStart={baseTriangle} triangleCount={triangles.Count - baseTriangle} meshIndexStart={mesh.StartIndex} indexCount={mesh.IndexCount}");
    }

    private static void AddIndices(FluidModelData mdl, MdlStructs.MeshStruct mesh, int slot, int mi, uint indexOffset, uint count,
        Dictionary<uint, ushort> replacements, int baseVertex, bool[] valid, List<Face> triangles)
    {
        ulong start = (ulong)mdl.FileHeader.IndexOffset[0] + (ulong)indexOffset * 2;
        if (start + (ulong)count * 2 > (ulong)mdl.Data.Length) return;
        var data = mdl.Data.AsSpan((int)start, checked((int)count * 2));
        for (int i = 0; i + 2 < count; i += 3)
        {
            int a = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2));
            int b = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((i + 1) * 2));
            int c = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((i + 2) * 2));
            uint entry = indexOffset - mesh.StartIndex + (uint)i;
            if (replacements.TryGetValue(entry, out var ra)) a = ra;
            if (replacements.TryGetValue(entry + 1, out var rb)) b = rb;
            if (replacements.TryGetValue(entry + 2, out var rc)) c = rc;
            if (a >= valid.Length || b >= valid.Length || c >= valid.Length || !valid[a] || !valid[b] || !valid[c] || a == b || b == c || a == c) continue;
            triangles.Add(new Face { A = baseVertex + a, B = baseVertex + b, C = baseVertex + c, N0 = -1, N1 = -1, N2 = -1,
                Slot = slot, Mesh = mi, IndexEntry = entry, V0 = a, V1 = b, V2 = c });
        }
    }

    private static string ReadString(byte[] strings, uint start)
    {
        if (start >= strings.Length) return string.Empty;
        int end = (int)start;
        while (end < strings.Length && strings[end] != 0) end++;
        return end == strings.Length ? string.Empty : Encoding.UTF8.GetString(strings, (int)start, end - (int)start);
    }

    // Shapes replace index entries. Overlapping, different replacements have unknown native priority;
    // reject only that mesh, rather than choosing a mask-bit order or dropping the entire face slot.
    private static bool ResolveShapeIndices(FluidModelData mdl, MdlStructs.MeshStruct mesh, bool[] active,
        out Dictionary<uint, ushort> replacements, out string reason)
    {
        replacements = new(); reason = string.Empty;
        for (int si = 0; si < active.Length; si++)
        {
            if (!active[si]) continue;
            var shape = mdl.Shapes[si];
            if (shape.ShapeMeshStartIndex == null || shape.ShapeMeshCount == null || shape.ShapeMeshStartIndex.Length < 1 || shape.ShapeMeshCount.Length < 1)
            { reason = "invalid shape LOD range"; return false; }
            int start = shape.ShapeMeshStartIndex[0], count = shape.ShapeMeshCount[0];
            if (start + count > mdl.ShapeMeshes.Length) { reason = "shape mesh range outside metadata"; return false; }
            for (int sm = start; sm < start + count; sm++)
            {
                var item = mdl.ShapeMeshes[sm];
                if (item.MeshIndexOffset != mesh.StartIndex) continue;
                if ((ulong)item.ShapeValueOffset + item.ShapeValueCount > (ulong)mdl.ShapeValues.Length)
                { reason = "shape value range outside metadata"; return false; }
                for (uint vi = item.ShapeValueOffset; vi < item.ShapeValueOffset + item.ShapeValueCount; vi++)
                {
                    var value = mdl.ShapeValues[vi];
                    if (value.BaseIndicesIndex >= mesh.IndexCount || value.ReplacingVertexIndex >= mesh.VertexCount)
                    { reason = "shape index/replacement outside mesh"; return false; }
                    if (replacements.TryGetValue(value.BaseIndicesIndex, out var old) && old != value.ReplacingVertexIndex)
                    { reason = "conflicting active shape replacements; priority unverified"; return false; }
                    replacements[value.BaseIndicesIndex] = value.ReplacingVertexIndex;
                }
            }
        }
        return true;
    }

    private static bool ReadVertex(FluidModelData mdl, MdlStructs.MeshStruct mesh, MdlStructs.VertexDeclarationStruct declaration,
        int index, int[] map, out Vertex result, out int missingBone)
    {
        missingBone = -1;
        result = default;
        Vector4 position = default, weights = default, extraWeights = default;
        byte b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
        bool hasPosition = false, hasWeights = false, hasIndices = false;
        foreach (var element in declaration.VertexElements)
        {
            if (element.Usage > 2 || element.Stream >= mesh.VertexBufferOffset.Length || element.Stream >= mesh.VertexBufferStride.Length) continue;
            int size = element.Type switch { 2 => 12, 3 => 16, 5 or 8 => 4, 14 or 17 => 8, _ => 0 };
            ulong start = (ulong)mdl.FileHeader.VertexOffset[0] + mesh.VertexBufferOffset[element.Stream] +
                (ulong)index * mesh.VertexBufferStride[element.Stream] + element.Offset;
            if (size == 0 || start + (uint)size > (ulong)mdl.Data.Length) return false;
            var bytes = mdl.Data.AsSpan((int)start, size);
            if (element.Usage == 2)
            {
                if (element.Type != 5 && element.Type != 17) return false;
                b0 = bytes[0]; b1 = bytes[1]; b2 = bytes[2]; b3 = bytes[3];
                if (element.Type == 17) { b4 = bytes[4]; b5 = bytes[5]; b6 = bytes[6]; b7 = bytes[7]; }
                hasIndices = true; continue;
            }
            Vector4 value;
            // Dawntrail blend usage type 17 is eight interleaved BYTE influences, not four ushort indices.
            // Keeping both byte vectors in matching storage order preserves all eight weights exactly.
            if (element.Type == 17 && element.Usage == 1)
            {
                value = new(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
                extraWeights = new(bytes[4] / 255f, bytes[5] / 255f, bytes[6] / 255f, bytes[7] / 255f);
            }
            else if (element.Type == 8) value = new(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
            else if (element.Type == 14) value = new(Half(bytes, 0), Half(bytes, 2), Half(bytes, 4), Half(bytes, 6));
            else if (element.Type == 2 || element.Type == 3)
                value = new(Single(bytes, 0), Single(bytes, 4), Single(bytes, 8), element.Type == 3 ? Single(bytes, 12) : 1);
            else return false;
            if (element.Usage == 0) { position = value; hasPosition = true; }
            else { weights = value; hasWeights = true; }
        }
        if (!hasPosition || !hasWeights || !hasIndices || !Finite(new(position.X, position.Y, position.Z))) return false;
        int Map(byte b, float weight) => weight <= 0 ? 0 : b < map.Length ? map[b] : -1;
        int i0 = Map(b0, weights.X), i1 = Map(b1, weights.Y), i2 = Map(b2, weights.Z), i3 = Map(b3, weights.W);
        int i4 = Map(b4, extraWeights.X), i5 = Map(b5, extraWeights.Y), i6 = Map(b6, extraWeights.Z), i7 = Map(b7, extraWeights.W);
        if (i0 < 0) missingBone = b0;
        else if (i1 < 0) missingBone = b1;
        else if (i2 < 0) missingBone = b2;
        else if (i3 < 0) missingBone = b3;
        else if (i4 < 0) missingBone = b4;
        else if (i5 < 0) missingBone = b5;
        else if (i6 < 0) missingBone = b6;
        else if (i7 < 0) missingBone = b7;
        float sum = weights.X + weights.Y + weights.Z + weights.W + extraWeights.X + extraWeights.Y + extraWeights.Z + extraWeights.W;
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0 || i6 < 0 || i7 < 0 || weights.X < 0 || weights.Y < 0 || weights.Z < 0 || weights.W < 0 ||
            !(sum > 1e-5f) || !float.IsFinite(sum)) return false;
        result = new Vertex { Position = new(position.X, position.Y, position.Z), Weights = weights / sum, ExtraWeights = extraWeights / sum,
            B0 = i0, B1 = i1, B2 = i2, B3 = i3, B4 = i4, B5 = i5, B6 = i6, B7 = i7 };
        return true;
    }
    private static float Single(ReadOnlySpan<byte> s, int o) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o)));
    private static float Half(ReadOnlySpan<byte> s, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)));

    private static void BuildAdjacency(Face[] faces, CancellationToken cancellation)
    {
        // Only identical vertex IDs connect. UV seams and nonmanifold edges remain boundaries.
        var edges = new Dictionary<(int, int), (int Face, int Edge)>();
        var nonmanifold = new HashSet<(int, int)>();
        for (int i = 0; i < faces.Length; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            for (int e = 0; e < 3; e++)
            {
                var face = faces[i];
                int a = e == 0 ? face.B : e == 1 ? face.C : face.A;
                int b = e == 0 ? face.C : e == 1 ? face.A : face.B;
                var key = a < b ? (a, b) : (b, a);
                if (nonmanifold.Contains(key)) continue;
                if (edges.TryGetValue(key, out var other))
                {
                    if (Neighbour(faces[other.Face], other.Edge) >= 0)
                    {
                        int linked = Neighbour(faces[other.Face], other.Edge);
                        for (int le = 0; le < 3; le++) if (Neighbour(faces[linked], le) == other.Face) SetNeighbour(faces, linked, le, -1);
                        SetNeighbour(faces, other.Face, other.Edge, -1); nonmanifold.Add(key);
                    }
                    else { SetNeighbour(faces, i, e, other.Face); SetNeighbour(faces, other.Face, other.Edge, i); }
                }
                else edges.Add(key, (i, e));
            }
        }
    }
    private static int Neighbour(Face f, int e) => e == 0 ? f.N0 : e == 1 ? f.N1 : f.N2;
    private static void SetNeighbour(Face[] faces, int fi, int e, int value)
    { if (e == 0) faces[fi].N0 = value; else if (e == 1) faces[fi].N1 = value; else faces[fi].N2 = value; }

    private static Cluster[] BuildClusters(Vertex[] vertices, Face[] faces, CancellationToken cancellation)
    {
        var groups = new Dictionary<int, List<int>>();
        var centers = new Vector3[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var face = faces[i];
            centers[i] = (vertices[face.A].Position + vertices[face.B].Position + vertices[face.C].Position) / 3f;
            var v = vertices[face.A];
            int owner = v.B0; float weight = v.Weights.X;
            if (v.Weights.Y > weight) { owner = v.B1; weight = v.Weights.Y; }
            if (v.Weights.Z > weight) { owner = v.B2; weight = v.Weights.Z; }
            if (v.Weights.W > weight) { owner = v.B3; weight = v.Weights.W; }
            // Dawntrail BYTE8 has eight influences. Ownership only partitions candidates;
            // each cluster still bounds every positive influence on all three face vertices.
            if (v.ExtraWeights.X > weight) { owner = v.B4; weight = v.ExtraWeights.X; }
            if (v.ExtraWeights.Y > weight) { owner = v.B5; weight = v.ExtraWeights.Y; }
            if (v.ExtraWeights.Z > weight) { owner = v.B6; weight = v.ExtraWeights.Z; }
            if (v.ExtraWeights.W > weight) owner = v.B7;
            if (!groups.TryGetValue(owner, out var group)) groups[owner] = group = new List<int>();
            group.Add(i);
        }
        var result = new List<Cluster>();
        foreach (var group in groups.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var indices = group.ToArray();
            var leaves = new List<Cluster>();
            Partition(indices, 0, indices.Length, leaves);
            var parent = CreateCluster(indices, 0, indices.Length);
            parent.Children = leaves.ToArray();
            // Parent performs coarse rejection; only bounded, spatially partitioned leaves scan triangles.
            parent.Triangles = Array.Empty<int>();
            result.Add(parent);
        }
        return result.ToArray();

        void Partition(int[] indices, int offset, int count, List<Cluster> leaves)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count <= 128) { leaves.Add(CreateCluster(indices, offset, count)); return; }
            var minimum = new Vector3(float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity);
            for (int i = offset; i < offset + count; i++)
            {
                var center = Center(indices[i]);
                minimum = Vector3.Min(minimum, center); maximum = Vector3.Max(maximum, center);
            }
            var size = maximum - minimum;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(indices, offset, count, Comparer<int>.Create((a, b) => Component(Center(a), axis).CompareTo(Component(Center(b), axis))));
            int half = count / 2;
            Partition(indices, offset, half, leaves); Partition(indices, offset + half, count - half, leaves);
        }
        Vector3 Center(int fi) => centers[fi];
        Cluster CreateCluster(int[] indices, int offset, int count)
        {
            var bounds = new Dictionary<int, InfluenceBounds>();
            for (int i = offset; i < offset + count; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                int fi = indices[i];
                var f = faces[fi]; Add(vertices[f.A]); Add(vertices[f.B]); Add(vertices[f.C]);
            }
            var subset = new int[count]; Array.Copy(indices, offset, subset, 0, count);
            return new Cluster { Triangles = subset, Influences = new List<InfluenceBounds>(bounds.Values).ToArray() };
            void Add(Vertex v)
            {
                AddBone(v.B0, v.Weights.X); AddBone(v.B1, v.Weights.Y); AddBone(v.B2, v.Weights.Z); AddBone(v.B3, v.Weights.W);
                AddBone(v.B4, v.ExtraWeights.X); AddBone(v.B5, v.ExtraWeights.Y); AddBone(v.B6, v.ExtraWeights.Z); AddBone(v.B7, v.ExtraWeights.W);
                void AddBone(int b, float w)
                {
                    if (w <= 0) return;
                    if (!bounds.TryGetValue(b, out var bound)) bound = new InfluenceBounds { Bone = b, Minimum = v.Position, Maximum = v.Position };
                    else { bound.Minimum = Vector3.Min(bound.Minimum, v.Position); bound.Maximum = Vector3.Max(bound.Maximum, v.Position); }
                    bounds[b] = bound;
                }
            }
        }
    }

    private void UpdateBounds(Cluster cluster)
    {
        cluster.BoundsFrame = frame;
        cluster.Minimum = cluster.PreviousMinimum = new Vector3(float.PositiveInfinity);
        cluster.Maximum = cluster.PreviousMaximum = new Vector3(float.NegativeInfinity);
        foreach (var bound in cluster.Influences)
        {
            Expand(skinWorld[bound.Bone], bound, ref cluster.Minimum, ref cluster.Maximum);
            Expand(previousSkinWorld[bound.Bone], bound, ref cluster.PreviousMinimum, ref cluster.PreviousMaximum);
        }
    }
    private static void Expand(Matrix4x4 matrix, InfluenceBounds bound, ref Vector3 minimum, ref Vector3 maximum)
    {
        var center = Vector3.Transform((bound.Minimum + bound.Maximum) * 0.5f, matrix);
        var e = (bound.Maximum - bound.Minimum) * 0.5f;
        var extent = new Vector3(Math.Abs(matrix.M11) * e.X + Math.Abs(matrix.M21) * e.Y + Math.Abs(matrix.M31) * e.Z,
            Math.Abs(matrix.M12) * e.X + Math.Abs(matrix.M22) * e.Y + Math.Abs(matrix.M32) * e.Z,
            Math.Abs(matrix.M13) * e.X + Math.Abs(matrix.M23) * e.Y + Math.Abs(matrix.M33) * e.Z);
        minimum = Vector3.Min(minimum, center - extent); maximum = Vector3.Max(maximum, center + extent);
    }
}
