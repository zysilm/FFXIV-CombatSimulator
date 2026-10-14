// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Linq;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private readonly record struct RenderedModelStamp(int Slot, nint Model, nint Resource, nint ModelData, uint Attributes, uint Shapes);
    private readonly List<RenderedModelStamp> renderedGeometryStamps = new();
    private nint renderedGeometryCharBase, renderedGeometrySkeleton, renderedGeometryPose, renderedGeometryHavok;
    private Vector3 renderedGeometryScale;
    private int renderedGeometrySlotCount;
    private ushort renderedGeometryRace;
    private byte[] renderedGeometryCustomize = Array.Empty<byte>();
    private bool renderedGeometryPenumbra;
    private readonly Dictionary<string, string> renderedGeometryResolvedPaths = new(StringComparer.Ordinal);
    private long renderedGeometryNextResolveCheck;

    private void ClearRenderedGeometryCapture()
    {
        renderedGeometryStamps.Clear();
        renderedGeometryCharBase = renderedGeometrySkeleton = renderedGeometryPose = renderedGeometryHavok = 0;
        renderedGeometryScale = default; renderedGeometrySlotCount = -1;
        renderedGeometryRace = 0; renderedGeometryCustomize = Array.Empty<byte>();
        renderedGeometryResolvedPaths.Clear(); renderedGeometryNextResolveCheck = 0;
        renderedPartialStamps.Clear(); renderedPartialCount = 0;
    }

    private bool RenderedGeometrySourcesMatch(SkeletonAccess skel)
    {
        var cb = skel.CharBase;
        if (cb == null || renderedGeometryCharBase != (nint)cb || renderedGeometrySkeleton != (nint)cb->Skeleton ||
            renderedGeometryPose != (nint)skel.Pose || renderedGeometryHavok != (nint)skel.HavokSkeleton ||
            renderedGeometrySlotCount != cb->SlotCount || renderedGeometryScale != GetCharacterScale(targetCharacterAddress, skel)) return false;
        if (!RenderedPartialSourcesMatch(skel)) return false;
        if (renderedGeometryCustomize.Length != 0)
        {
            var human = (Human*)cb;
            if (human->RaceSexId != renderedGeometryRace || !new ReadOnlySpan<byte>(&human->Customize, 26).SequenceEqual(renderedGeometryCustomize)) return false;
        }
        long now = Environment.TickCount64;
        if (now >= renderedGeometryNextResolveCheck)
        {
            renderedGeometryNextResolveCheck = now + 500;
            if (IsRenderedPenumbraLoaded() != renderedGeometryPenumbra) return false;
            foreach (var resolved in renderedGeometryResolvedPaths)
                if (!TryResolveRenderedActorPath(resolved.Key, out var current) || current != resolved.Value) return false;
        }
        foreach (var stamp in renderedGeometryStamps)
        {
            var model = cb->Models == null ? null : cb->Models[stamp.Slot];
            if ((nint)model != stamp.Model) return false;
            if (model == null) continue;
            var resource = model->ModelResourceHandle;
            if ((nint)resource != stamp.Resource || (resource == null ? 0 : (nint)resource->ModelData) != stamp.ModelData ||
                model->EnabledAttributeIndexMask != stamp.Attributes || model->EnabledShapeKeyIndexMask != stamp.Shapes) return false;
        }
        return true;
    }

    /// <summary>Framework/render-thread only. Snapshot currently loaded visible models and pose;
    /// returned arrays own all data. It captures CPU geometry, not shader/GPU displacement.</summary>
    private RenderedGeometrySnapshot BuildRenderedGeometrySnapshot(SkeletonAccess skel)
    {
        var result = new RenderedGeometrySnapshot();
        var diagnostics = new List<string>();
        var vertices = new List<RenderedGeometryVertex>();
        var triangles = new List<int>();
        var cb = skel.CharBase;
        ClearRenderedGeometryCapture();
        if (cb == null || cb->Skeleton == null || cb->Models == null || !TryBuildSkinDeltas(skel, out var skin))
        {
            result.Diagnostics = new[] { "Current model pose or render skeleton unavailable." };
            return result;
        }
        var transform = cb->Skeleton->Transform;
        var scale = GetCharacterScale(targetCharacterAddress, skel);
        var rotation = Quaternion.Normalize(new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W));
        var world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
                    Matrix4x4.CreateTranslation(transform.Position.X, transform.Position.Y, transform.Position.Z);
        BuildRenderedSkinPalette(skel, diagnostics, ref skin, out var names, out var parents, out var owners, out var boneByName);
        bool human = cb->GetModelType() == CharacterBase.ModelType.Human;
        renderedGeometryCharBase = (nint)cb; renderedGeometrySkeleton = (nint)cb->Skeleton;
        renderedGeometryPose = (nint)skel.Pose; renderedGeometryHavok = (nint)skel.HavokSkeleton;
        renderedGeometryScale = scale; renderedGeometrySlotCount = cb->SlotCount;
        ushort targetRace = human ? ((Human*)cb)->RaceSexId : (ushort)0;
        renderedGeometryRace = targetRace;
        renderedGeometryCustomize = human ? new ReadOnlySpan<byte>(&((Human*)cb)->Customize, 26).ToArray() : Array.Empty<byte>();
        renderedGeometryPenumbra = IsRenderedPenumbraLoaded();
        RenderedRaceDeformation? pbd = null;
        string pbdRoute = "unavailable";
        bool triedPbd = false;
        byte* logicalBuffer = stackalloc byte[CharacterBase.PathBufferSize];
        diagnostics.Add($"CPU loaded-model capture: LOD0, ModelPose, actor scale={scale}; GPU material displacement and opaque native post-deformer edits are not captured.");
        diagnostics.Add("Unmapped or ambiguously named skeleton influences are omitted; primary and connected partial poses are captured.");
        for (int slot = 0; slot < Math.Clamp(cb->SlotCount, 0, 32); slot++)
        {
            // Hair/accessories have independent physical rigs. Keep anatomy and the visible
            // equipment covering it, including mod replacements of these rendered slots.
            if (human && slot is not (0 or 1 or 2 or 3 or 4 or 11)) continue;
            var model = cb->Models[slot];
            var handle = model == null ? null : model->ModelResourceHandle;
            renderedGeometryStamps.Add(new(slot, (nint)model, (nint)handle, handle == null ? 0 : (nint)handle->ModelData,
                model == null ? 0 : model->EnabledAttributeIndexMask, model == null ? 0 : model->EnabledShapeKeyIndexMask));
            if (model == null || model->ModelResourceHandle == null) continue;
            var resource = (ResourceHandle*)model->ModelResourceHandle;
            string path = Encoding.UTF8.GetString(resource->FileName.AsSpan());
            int slotVertexStart = vertices.Count, slotIndexStart = triangles.Count;
            try
            {
                if (!TryLoadRenderedModel(resource, path, out var data, out var route))
                { diagnostics.Add($"slot={slot} path='{path}': model bytes unavailable or unsupported; omitted."); continue; }
                if (data.Lods.Length == 0) continue;
                Matrix4x4[]? raceDeform = null;
                string deformation = "same-race/no PBD";
                // ResolveMdlPath yields the logical cXXXX code even when Penumbra's physical
                // replacement filename contains no race identifier.
                string logicalPath = cb->ResolveMdlPath(logicalBuffer, CharacterBase.PathBufferSize, (uint)slot).ToString();
                ushort sourceRace = ReadRenderedModelRace(logicalPath);
                if (renderedGeometryPenumbra && targetCharacterAddress != 0)
                {
                    try
                    {
                        var reverse = Services.PluginInterface.GetIpcSubscriber<string, int, string[]>("Penumbra.ReverseResolveGameObjectPath")
                            .InvokeFunc(path, ((GameObject*)targetCharacterAddress)->ObjectIndex);
                        var candidateRaces = reverse.Select(ReadRenderedModelRace).Where(r => r != 0).Distinct().ToArray();
                        if (candidateRaces.Length == 1) sourceRace = candidateRaces[0];
                        else if (candidateRaces.Length > 1 && !reverse.Contains(logicalPath, StringComparer.OrdinalIgnoreCase)) sourceRace = 0;
                    }
                    catch { diagnostics.Add($"slot={slot}: Penumbra reverse path unavailable; using native logical donor path '{logicalPath}'."); }
                }
                if (human && sourceRace != 0 && sourceRace != targetRace)
                {
                    if (!triedPbd)
                    {
                        triedPbd = true;
                        var rawPbd = TryLoadRenderedPbd(out pbdRoute);
                        if (rawPbd != null) pbd = new RenderedRaceDeformation(rawPbd);
                    }
                    if (pbd == null || !pbd.TryBuild(sourceRace, targetRace, names, parents, out raceDeform))
                    { diagnostics.Add($"slot={slot} path='{path}': unsupported PBD route c{sourceRace:D4}->c{targetRace:D4}; omitted instead of using undeformed geometry."); continue; }
                    deformation = $"PBD c{sourceRace:D4}->c{targetRace:D4} source={pbdRoute}; CPU reconstruction, native post-deformer edits unverified";
                }
                else if (human && sourceRace == 0)
                { diagnostics.Add($"slot={slot} path='{path}': authored race unresolved; omitted instead of assuming undeformed geometry."); continue; }

                var shapes = new bool[data.Shapes.Length];
                for (int si = 0; si < shapes.Length; si++)
                {
                    string name = RenderedGeometryDecoder.StringAt(data.Strings, data.Shapes[si].StringOffset);
                    int renderIndex = string.IsNullOrEmpty(name) ? -1 : model->GetShapeIndex(name);
                    shapes[si] = renderIndex is >= 0 and < 32 && (model->EnabledShapeKeyIndexMask & (1u << renderIndex)) != 0;
                }
                var lod = data.Lods[0];
                int before = triangles.Count;
                var meshIndices = new SortedSet<int>();
                void AddRange(int start, int count)
                { for (int mi = start; mi < Math.Min(data.Meshes.Length, start + count); mi++) meshIndices.Add(mi); }
                AddRange(lod.MeshIndex, lod.MeshCount);
                if (data.ExtraLodEnabled && data.ExtraLods.Length > 0)
                {
                    var extra = data.ExtraLods[0];
                    AddRange(extra.GlassMeshIndex, extra.GlassMeshCount);
                    AddRange(extra.MaterialChangeMeshIndex, extra.MaterialChangeMeshCount);
                    AddRange(extra.CrestChangeMeshIndex, extra.CrestChangeMeshCount);
                }
                foreach (int mi in meshIndices)
                {
                    var mesh = data.Meshes[mi];
                    if (mesh.BoneTableIndex >= data.BoneTables.Length) continue;
                    var palette = data.BoneTables[mesh.BoneTableIndex].BoneIndex;
                    var map = new int[palette.Length]; Array.Fill(map, -1);
                    for (int bi = 0; bi < palette.Length; bi++)
                        if (palette[bi] < data.BoneNameOffsets.Length)
                            map[bi] = boneByName.GetValueOrDefault(RenderedGeometryDecoder.StringAt(data.Strings, data.BoneNameOffsets[palette[bi]]), -1);
                    RenderedGeometryDecoder.Append(data, 0, mi, slot, model->EnabledAttributeIndexMask,
                        shapes, map, skin, raceDeform, world, vertices, triangles, owners);
                }
                diagnostics.Add($"slot={slot} path='{path}' logical='{logicalPath}' source={route} triangles={(triangles.Count - before) / 3} attrs=0x{model->EnabledAttributeIndexMask:X8} shapes=0x{model->EnabledShapeKeyIndexMask:X8} deformation={deformation}");
            }
            catch (Exception ex)
            {
                vertices.RemoveRange(slotVertexStart, vertices.Count - slotVertexStart);
                triangles.RemoveRange(slotIndexStart, triangles.Count - slotIndexStart);
                diagnostics.Add($"slot={slot} path='{path}': {ex.GetType().Name}: {ex.Message}; omitted.");
            }
        }
        result.Vertices = vertices.ToArray(); result.TriangleIndices = triangles.ToArray(); result.Diagnostics = diagnostics.ToArray();
        return result;
    }

    private static ushort ReadRenderedModelRace(string path)
    {
        // Logical game paths contain /human/cXXXX/. Do not mistake arbitrary mod filenames
        // containing c1234 for a reliable authored skeleton identifier.
        int start = path.IndexOf("/human/c", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = path.IndexOf("/model/c", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && start + 12 <= path.Length && ushort.TryParse(path.AsSpan(start + 8, 4), out var race) ? race : (ushort)0;
    }

    private static bool IsRenderedPenumbraLoaded() => Services.PluginInterface.InstalledPlugins.Any(p =>
        p.IsLoaded && string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase));

    private bool TryResolveRenderedActorPath(string logical, out string resolved)
    {
        resolved = logical;
        if (!IsRenderedPenumbraLoaded()) return true;
        if (targetCharacterAddress == 0) return false;
        try
        {
            resolved = Services.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath")
                .InvokeFunc(logical, ((GameObject*)targetCharacterAddress)->ObjectIndex);
            return !string.IsNullOrEmpty(resolved);
        }
        catch { return false; }
    }

    private byte[]? TryLoadRenderedPbd(out string route)
    {
        const string path = "chara/xls/boneDeformer/human.pbd";
        route = "actor collection unresolved";
        if (!TryResolveRenderedActorPath(path, out var resolved)) return null;
        renderedGeometryResolvedPaths[path] = resolved;
        route = $"actor collection '{resolved}'";
        if (resolved.StartsWith('|')) { int end = resolved.IndexOf('|', 1); if (end < 0) return null; resolved = resolved[(end + 1)..]; }
        if (!Path.IsPathRooted(resolved)) return TryGetRawFileData(resolved);
        var file = new FileInfo(resolved);
        return file.Exists && file.Length is > 0 and <= 16777216 ? File.ReadAllBytes(resolved) : null;
    }

    private static bool TryLoadRenderedModel(ResourceHandle* resource, string path, out ModelGeometryData data, out string route)
    {
        data = new(); route = "unavailable";
        // Some resource versions retain the full MDL; others expose metadata only. Only accept
        // a full validated file. Never dereference guessed GPU buffer or deformer layouts.
        var memory = resource->GetDataSpan();
        if (memory.Length is > 0 and <= 67108864 && TryReadModelGeometryData(memory.ToArray(), out data))
        { route = "loaded resource"; return true; }
        if (path.StartsWith('|'))
        {
            int delimiter = path.IndexOf('|', 1);
            if (delimiter < 0) return false;
            path = path[(delimiter + 1)..];
        }
        byte[]? raw;
        if (Path.IsPathRooted(path))
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 67108864) return false;
            raw = File.ReadAllBytes(path); route = "loaded path on disk";
        }
        else { raw = TryGetRawFileData(path); route = "loaded path in SqPack"; }
        return raw != null && TryReadModelGeometryData(raw, out data);
    }
}
