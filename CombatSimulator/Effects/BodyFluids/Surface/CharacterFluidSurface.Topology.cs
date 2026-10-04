// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using Lumina.Data.Files;
using Lumina.Data.Parsing;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private void BuildTopology(SkeletonAccess ns)
    {
        var points = new List<Vertex>();
        var triangles = new List<Face>();
        int omitted = 0, loaded = 0;
        for (int slot = 0; slot < Math.Clamp(ns.CharBase->SlotCount, 0, MaxSlots); slot++)
        {
            var model = ns.CharBase->Models == null ? null : ns.CharBase->Models[slot];
            if (model == null || model->ModelResourceHandle == null)
            {
                Services.Log.Info($"Body fluid topology generation={Generation} slot={slot} path='<unavailable>' faces=0 validVerts=0 reason=no render model/resource handle");
                continue;
            }
            string path = model->ModelResourceHandle->FileName.ToString();
            if (path.StartsWith('|')) { int end = path.IndexOf('|', 1); if (end >= 0) path = path[(end + 1)..]; }
            var diagnostic = new SlotDiagnostic();
            int before = triangles.Count;
            string reason = "supported";
            try
            {
                // Diagnose before rejecting; keep conservative shape handling unchanged.
                if (model->EnabledShapeKeyIndexMask != 0) { omitted++; reason = "active shape mask"; continue; }
                FluidModelData? mdl;
                if (Path.IsPathRooted(path))
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length > 64 * 1024 * 1024) { omitted++; reason = "no file or file over 64 MiB"; continue; }
                    mdl = LoadModel(path, true, out diagnostic.Parser);
                }
                else mdl = LoadModel(path, false, out diagnostic.Parser);
                if (mdl == null || mdl.Lods.Length == 0 || mdl.Data.Length == 0) { omitted++; reason = "no model data / unsupported parser"; continue; }
                var lod = mdl.Lods[0];
                for (int mi = lod.MeshIndex; mi < Math.Min(mdl.Meshes.Length, lod.MeshIndex + lod.MeshCount); mi++)
                    ReadMesh(mdl, mi, model->EnabledAttributeIndexMask, ns, points, triangles, diagnostic);
                if (triangles.Count > before) loaded++; else { omitted++; reason = "no supported faces"; }
            }
            catch (Exception e)
            {
                omitted++;
                reason = $"exception {e.GetType().Name}: {e.Message}";
            }
            finally
            {
                Services.Log.Info($"Body fluid topology generation={Generation} slot={slot} path='{path}' " +
                    $"shapeMask=0x{model->EnabledShapeKeyIndexMask:X8} attributeMask=0x{model->EnabledAttributeIndexMask:X8} " +
                    $"nativeShapeCount={model->ModelResourceHandle->Shapes.LongCount} " +
                    $"parser={diagnostic.Parser} faces={triangles.Count - before} validVerts={diagnostic.ValidVertices}/{diagnostic.Vertices} " +
                    $"meshes={diagnostic.Meshes} missingDeclarations={diagnostic.MissingDeclarations} invalidBoneTables={diagnostic.InvalidBoneTables} " +
                    $"capacityRejectedMeshes={diagnostic.CapacityRejectedMeshes} submeshesVisible={diagnostic.VisibleSubmeshes} " +
                    $"submeshesHidden={diagnostic.HiddenSubmeshes} badSubmeshRanges={diagnostic.BadSubmeshRanges} " +
                    $"weightedBoneRejects={diagnostic.WeightedBoneRejects} unmappedWeightedBones=[{string.Join(",", diagnostic.MissingBones)}] reason={reason}");
            }
        }
        vertices = points.ToArray(); faces = triangles.ToArray();
        currentVertices = new Vector3[vertices.Length]; previousVertices = new Vector3[vertices.Length];
        vertexFrames = new uint[vertices.Length];
        BuildAdjacency(); BuildClusters();
        Status = faces.Length == 0 ? "Mouth source only; visible surface unsupported" :
            omitted == 0 ? "CPU LOD0 surface; shader deformation unverified" : "Partial CPU surface; unsupported models omitted";
        Services.Log.Info($"Body fluids: surface generation={Generation}, models={loaded}, omitted={omitted}, triangles={faces.Length}, vertices={vertices.Length}.");
    }

    private sealed class SlotDiagnostic
    {
        public string Parser = "not loaded";
        public int Vertices, ValidVertices, Meshes, MissingDeclarations, InvalidBoneTables, CapacityRejectedMeshes;
        public int VisibleSubmeshes, HiddenSubmeshes, BadSubmeshRanges, WeightedBoneRejects;
        public readonly List<string> MissingBones = new(5);
    }

    private static FluidModelData? LoadModel(string path, bool disk, out string route)
    {
        route = "lumina";
        try
        {
            var normal = disk ? Services.DataManager.GameData.GetFileFromDisk<MdlFile>(path) :
                Services.DataManager.GameData.GetFile<MdlFile>(path);
            if (normal != null) return FluidModelData.FromMdlFile(normal);
        }
        catch (Exception) { /* Game/mod MDL versions may exceed Lumina's normal parser; use the existing raw geometry reader. */ }
        route = "raw fallback";
        byte[]? raw = disk ? File.ReadAllBytes(path) : Services.DataManager.GameData.GetFile(path)?.Data;
        if (raw == null || raw.Length > 64 * 1024 * 1024) return null;
        return RagdollController.TryReadFluidModelData(raw, out var data) ? data : null;
    }

    private void ReadMesh(FluidModelData mdl, int mi, uint attributes, SkeletonAccess ns, List<Vertex> points, List<Face> triangles, SlotDiagnostic diagnostic)
    {
        diagnostic.Meshes++;
        if (mi >= mdl.VertexDeclarations.Length) { diagnostic.MissingDeclarations++; return; }
        var mesh = mdl.Meshes[mi];
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
            map[i] = bones.ResolveBoneIndex(ns, mapNames[i]);
        }
        int baseVertex = points.Count;
        var valid = new bool[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            diagnostic.Vertices++;
            valid[i] = ReadVertex(mdl, mesh, mdl.VertexDeclarations[mi], i, map, out var point, out int missingBone);
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
        if (mesh.SubMeshCount == 0) AddIndices(mdl, mesh.StartIndex, mesh.IndexCount, baseVertex, valid, triangles);
        else
        {
            if (mesh.SubMeshIndex + mesh.SubMeshCount > mdl.Submeshes.Length) diagnostic.BadSubmeshRanges++;
            for (int s = mesh.SubMeshIndex; s < Math.Min(mdl.Submeshes.Length, mesh.SubMeshIndex + mesh.SubMeshCount); s++)
            {
                var sub = mdl.Submeshes[s];
                if ((sub.AttributeIndexMask & attributes) != sub.AttributeIndexMask) { diagnostic.HiddenSubmeshes++; continue; }
                diagnostic.VisibleSubmeshes++;
                AddIndices(mdl, sub.IndexOffset, sub.IndexCount, baseVertex, valid, triangles);
            }
        }
    }

    private static void AddIndices(FluidModelData mdl, uint indexOffset, uint count, int baseVertex, bool[] valid, List<Face> triangles)
    {
        ulong start = (ulong)mdl.FileHeader.IndexOffset[0] + (ulong)indexOffset * 2;
        if (start + (ulong)count * 2 > (ulong)mdl.Data.Length) return;
        var data = mdl.Data.AsSpan((int)start, checked((int)count * 2));
        for (int i = 0; i + 2 < count; i += 3)
        {
            int a = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2));
            int b = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((i + 1) * 2));
            int c = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((i + 2) * 2));
            if (a >= valid.Length || b >= valid.Length || c >= valid.Length || !valid[a] || !valid[b] || !valid[c] || a == b || b == c || a == c) continue;
            triangles.Add(new Face { A = baseVertex + a, B = baseVertex + b, C = baseVertex + c, N0 = -1, N1 = -1, N2 = -1 });
        }
    }

    private static bool ReadVertex(FluidModelData mdl, MdlStructs.MeshStruct mesh, MdlStructs.VertexDeclarationStruct declaration,
        int index, int[] map, out Vertex result, out int missingBone)
    {
        missingBone = -1;
        result = default;
        Vector4 position = default, weights = default;
        byte b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        bool hasPosition = false, hasWeights = false, hasIndices = false;
        foreach (var element in declaration.VertexElements)
        {
            if (element.Usage > 2 || element.Stream >= mesh.VertexBufferOffset.Length || element.Stream >= mesh.VertexBufferStride.Length) continue;
            int size = element.Type switch { 2 => 12, 3 => 16, 5 or 8 => 4, 14 => 8, _ => 0 };
            ulong start = (ulong)mdl.FileHeader.VertexOffset[0] + mesh.VertexBufferOffset[element.Stream] +
                (ulong)index * mesh.VertexBufferStride[element.Stream] + element.Offset;
            if (size == 0 || start + (uint)size > (ulong)mdl.Data.Length) return false;
            var bytes = mdl.Data.AsSpan((int)start, size);
            if (element.Usage == 2)
            {
                if (element.Type != 5) return false;
                b0 = bytes[0]; b1 = bytes[1]; b2 = bytes[2]; b3 = bytes[3]; hasIndices = true; continue;
            }
            Vector4 value;
            if (element.Type == 8) value = new(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
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
        if (i0 < 0) missingBone = b0;
        else if (i1 < 0) missingBone = b1;
        else if (i2 < 0) missingBone = b2;
        else if (i3 < 0) missingBone = b3;
        float sum = weights.X + weights.Y + weights.Z + weights.W;
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 || weights.X < 0 || weights.Y < 0 || weights.Z < 0 || weights.W < 0 ||
            !(sum > 1e-5f) || !float.IsFinite(sum)) return false;
        result = new Vertex { Position = new(position.X, position.Y, position.Z), Weights = weights / sum, B0 = i0, B1 = i1, B2 = i2, B3 = i3 };
        return true;
    }
    private static float Single(ReadOnlySpan<byte> s, int o) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o)));
    private static float Half(ReadOnlySpan<byte> s, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)));

    private void BuildAdjacency()
    {
        // Only identical vertex IDs connect. UV seams and nonmanifold edges remain boundaries.
        var edges = new Dictionary<(int, int), (int Face, int Edge)>();
        var nonmanifold = new HashSet<(int, int)>();
        for (int i = 0; i < faces.Length; i++)
        {
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
                        for (int le = 0; le < 3; le++) if (Neighbour(faces[linked], le) == other.Face) SetNeighbour(linked, le, -1);
                        SetNeighbour(other.Face, other.Edge, -1); nonmanifold.Add(key);
                    }
                    else { SetNeighbour(i, e, other.Face); SetNeighbour(other.Face, other.Edge, i); }
                }
                else edges.Add(key, (i, e));
            }
        }
    }
    private static int Neighbour(Face f, int e) => e == 0 ? f.N0 : e == 1 ? f.N1 : f.N2;
    private void SetNeighbour(int fi, int e, int value)
    { if (e == 0) faces[fi].N0 = value; else if (e == 1) faces[fi].N1 = value; else faces[fi].N2 = value; }

    private void BuildClusters()
    {
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < faces.Length; i++)
        {
            var v = vertices[faces[i].A];
            int owner = v.B0; float weight = v.Weights.X;
            if (v.Weights.Y > weight) { owner = v.B1; weight = v.Weights.Y; }
            if (v.Weights.Z > weight) { owner = v.B2; weight = v.Weights.Z; }
            if (v.Weights.W > weight) owner = v.B3;
            if (!groups.TryGetValue(owner, out var group)) groups[owner] = group = new List<int>();
            group.Add(i);
        }
        var result = new List<Cluster>();
        foreach (var group in groups.Values)
        {
            var indices = group.ToArray();
            var leaves = new List<Cluster>();
            Partition(indices, 0, indices.Length, leaves);
            var parent = CreateCluster(indices, 0, indices.Length);
            parent.Children = leaves.ToArray();
            // Parent performs coarse rejection; only bounded, spatially partitioned leaves scan triangles.
            parent.Triangles = Array.Empty<int>();
            result.Add(parent);
        }
        clusters = result.ToArray();

        void Partition(int[] indices, int offset, int count, List<Cluster> leaves)
        {
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
        Vector3 Center(int fi)
        {
            var face = faces[fi];
            return (vertices[face.A].Position + vertices[face.B].Position + vertices[face.C].Position) / 3f;
        }
        Cluster CreateCluster(int[] indices, int offset, int count)
        {
            var bounds = new Dictionary<int, InfluenceBounds>();
            for (int i = offset; i < offset + count; i++)
            {
                int fi = indices[i];
                var f = faces[fi]; Add(vertices[f.A]); Add(vertices[f.B]); Add(vertices[f.C]);
            }
            var subset = new int[count]; Array.Copy(indices, offset, subset, 0, count);
            return new Cluster { Triangles = subset, Influences = new List<InfluenceBounds>(bounds.Values).ToArray() };
            void Add(Vertex v)
            {
                AddBone(v.B0, v.Weights.X); AddBone(v.B1, v.Weights.Y); AddBone(v.B2, v.Weights.Z); AddBone(v.B3, v.Weights.W);
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
