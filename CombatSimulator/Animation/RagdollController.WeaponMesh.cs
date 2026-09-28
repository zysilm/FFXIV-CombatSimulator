using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using BepuPhysics.Collidables;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    public WeaponMeshGeometry? TryBuildWeaponMesh(DrawObject* draw)
    {
        if (!config.WeaponDropMeshCollision || draw == null) return null;
        var result = BuildWeaponMesh(draw);
        if (result == null) log.Warning("Weapon mesh: geometry unavailable or unsupported; using simple collision for this drop.");
        return result;
    }

    private WeaponMeshGeometry? BuildWeaponMesh(DrawObject* draw)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var skel = boneService.TryGetSkeletonFromCharBase((CharacterBase*)draw);
            if (skel == null || !TryBuildSkinDeltas(skel.Value, out var deltas)) return null;
            var ns = skel.Value; var cb = ns.CharBase; var scale = GetSkeletonScale(ns);
            var triangles = new List<Triangle>();
            for (var slot = 0; slot < Math.Clamp(cb->SlotCount, 0, 32); slot++)
            {
                var model = cb->Models == null ? null : cb->Models[slot];
                if (model == null) continue;
                if (model->ModelResourceHandle == null) return null;
                var path = model->ModelResourceHandle->FileName.ToString();
                if (path.StartsWith('|')) { var end = path.IndexOf('|', 1); if (end >= 0) path = path[(end + 1)..]; }
                MeshCollisionMdlData mdl;
                if (Path.IsPathRooted(path))
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length > 64 * 1024 * 1024) return null;
                    if (!TryParseRawMdlCollisionData(File.ReadAllBytes(path), out mdl, out _)) return null;
                }
                else if (!TryLoadMeshCollisionMdlData("Weapon", slot, path, out mdl)) return null;
                if (!TrySelectMdlLod(mdl, out var lodIndex, out var lod)) return null;
                var indices = new HashSet<int>(); AddAnimatedMeshRange(mdl, lod.MeshIndex, lod.MeshCount, indices);
                if (mdl.ExtraLodEnabled && lodIndex < mdl.ExtraLods.Length)
                {
                    var e = mdl.ExtraLods[lodIndex]; AddAnimatedMeshRange(mdl, e.GlassMeshIndex, e.GlassMeshCount, indices);
                    AddAnimatedMeshRange(mdl, e.MaterialChangeMeshIndex, e.MaterialChangeMeshCount, indices);
                    AddAnimatedMeshRange(mdl, e.CrestChangeMeshIndex, e.CrestChangeMeshCount, indices);
                }
                foreach (var index in indices)
                {
                    if (index >= mdl.VertexDeclarations.Length) return null;
                    var mesh = mdl.Meshes[index];
                    if (triangles.Count + mesh.IndexCount / 3 > 120000) return null;
                    var map = BuildMdlMeshBoneMap(mdl, mesh, ns);
                    var vertices = new Vector3[mesh.VertexCount];
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var vertex = ReadMdlCollisionVertex(mdl.Data, mdl.FileHeader.VertexOffset[lodIndex], mesh, mdl.VertexDeclarations[index], i);
                        if (vertex.Position == null) return null;
                        // Never substitute reference-space geometry for an unmapped weighted vertex.
                        if (vertex.BlendWeights is { } weights && vertex.BlendIndices is { } bones)
                            for (var k = 0; k < Math.Min(4, bones.Length); k++)
                                if (GetBlendWeight(weights, k) > 0 && (bones[k] >= map.Length || map[bones[k]] < 0 || map[bones[k]] >= deltas.Length)) return null;
                        vertices[i] = SkinVertex(vertex, map, deltas) * scale;
                    }
                    var start = (ulong)mdl.FileHeader.IndexOffset[lodIndex] + (ulong)mesh.StartIndex * 2;
                    if (!TryGetIntRange(start, mesh.IndexCount * 2, mdl.Data.Length, out var offset, out var length)) return null;
                    var data = mdl.Data.AsSpan(offset, length);
                    for (var i = 0; i + 2 < mesh.IndexCount; i += 3)
                    {
                        var a = ReadUInt16(data, i * 2); var b = ReadUInt16(data, (i + 1) * 2); var c = ReadUInt16(data, (i + 2) * 2);
                        if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) return null;
                        triangles.Add(new Triangle(vertices[a], vertices[b], vertices[c]));
                    }
                }
            }
            var fit = WeaponMeshGeometry.Fit(triangles);
            log.Info($"Weapon mesh: {triangles.Count} triangles, {fit?.Parts.Length ?? 0} convex hulls, scale={scale}, center={fit?.Center}, full extent={fit?.Half * 2}, {clock.Elapsed.TotalMilliseconds:F1} ms");
            return fit;
        }
        catch (Exception ex) { log.Warning(ex, "Weapon mesh unavailable; using simple collision"); return null; }
    }
}
