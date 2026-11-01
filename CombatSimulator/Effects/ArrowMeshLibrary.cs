// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Data.Files;

namespace CombatSimulator.Effects;

/// <summary>
/// The arrow a bow actually fires, lifted out of the bow's own model. Every bow model carries its
/// arrow as a submesh tagged <c>atr_arrow</c> (shown while the string is drawn); this reads that
/// submesh and the arrow's own diffuse texture, so an arrow left in a body is the one that bow
/// shoots, in its colours. Re-framed so the point sits at the origin and the shaft runs along -Z.
/// </summary>
public sealed class ArrowMesh
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector4[] Colors = Array.Empty<Vector4>();
    public int[] Indices = Array.Empty<int>();
    /// <summary>Point to nock, metres.</summary>
    public float Length;
    public string Source = string.Empty;
    public int VertexBudget => Indices.Length;
}

public sealed unsafe class ArrowMeshLibrary
{
    private const string ArrowAttribute = "atr_arrow";
    private const string DefaultBowPath = "chara/weapon/w0601/obj/body/b0001";
    private const int DefaultBowVariant = 1;

    private readonly IPluginLog log;
    private readonly Dictionary<string, ArrowMesh?> cache = new();
    private readonly object gate = new();
    private ArrowMesh? fallback;
    private bool fallbackTried;

    public ArrowMeshLibrary(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>The arrow for a shooter's main-hand bow, or the default bow's arrow if that model
    /// has none (a modded bow, a weapon with the arrow baked elsewhere). Null only if even the
    /// default cannot be read.</summary>
    public ArrowMesh? ForShooter(Character* shooter)
    {
        if (shooter != null)
        {
            var weapon = shooter->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
            if (weapon.Id != 0)
            {
                var mesh = Get($"chara/weapon/w{weapon.Id:D4}/obj/body/b{weapon.Type:D4}", Math.Max(1, (int)weapon.Variant));
                if (mesh != null) return mesh;
            }
        }
        return Default();
    }

    public ArrowMesh? Default()
    {
        lock (gate)
        {
            if (!fallbackTried)
            {
                fallbackTried = true;
                fallback = Get(DefaultBowPath, DefaultBowVariant);
                if (fallback == null) log.Warning("Arrow impacts: the default arrow model could not be read; arrows fall back to a plain shaft.");
            }
            return fallback;
        }
    }

    private ArrowMesh? Get(string weaponFolder, int variant)
    {
        var key = $"{weaponFolder}|{variant}";
        lock (gate)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }
        ArrowMesh? mesh = null;
        try { mesh = Load(weaponFolder, variant); }
        catch (Exception ex) { log.Warning(ex, $"Arrow impacts: failed to read the arrow of {weaponFolder}."); }
        lock (gate) { cache[key] = mesh; }
        return mesh;
    }

    private ArrowMesh? Load(string weaponFolder, int variant)
    {
        // chara/weapon/w0601/obj/body/b0001 -> w0601, b0001
        var parts = weaponFolder.Split('/');
        if (parts.Length < 6) return null;
        var name = parts[2];
        var body = parts[^1];
        var mdlPath = $"{weaponFolder}/model/{name}{body}.mdl";
        var raw = Core.Services.DataManager.GameData.GetFile(mdlPath)?.Data;
        if (raw == null || !RagdollController.TryReadModelGeometryData(raw, out var mdl) || mdl.Lods.Length == 0) return null;

        // Strings: attribute names first, then bones, then materials. Attribute i is bit i.
        var attributes = new List<string>();
        var materials = new List<string>();
        var attributesDone = false;
        for (var cursor = 0; cursor < mdl.Strings.Length;)
        {
            var end = cursor;
            while (end < mdl.Strings.Length && mdl.Strings[end] != 0) end++;
            var s = Encoding.UTF8.GetString(mdl.Strings, cursor, end - cursor);
            if (!attributesDone && s.StartsWith("atr_", StringComparison.Ordinal)) attributes.Add(s);
            else attributesDone = true;
            if (s.EndsWith(".mtrl", StringComparison.Ordinal)) materials.Add(s);
            cursor = end + 1;
        }
        var arrowBit = attributes.IndexOf(ArrowAttribute);
        if (arrowBit < 0) return null;
        var mask = 1u << arrowBit;

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        var materialIndex = -1;
        var lod = mdl.Lods[0];
        for (int mi = lod.MeshIndex; mi < lod.MeshIndex + lod.MeshCount && mi < mdl.Meshes.Length; mi++)
        {
            var mesh = mdl.Meshes[mi];
            if (mi >= mdl.VertexDeclarations.Length) continue;
            var decl = mdl.VertexDeclarations[mi];
            for (int si = mesh.SubMeshIndex; si < mesh.SubMeshIndex + mesh.SubMeshCount && si < mdl.Submeshes.Length; si++)
            {
                var sub = mdl.Submeshes[si];
                if ((sub.AttributeIndexMask & mask) == 0) continue;
                if (materialIndex < 0) materialIndex = mesh.MaterialIndex;
                var local = new Dictionary<ushort, int>();
                for (uint i = 0; i + 2 < sub.IndexCount; i += 3)
                {
                    int a = Vertex(i), b = Vertex(i + 1), c = Vertex(i + 2);
                    if (a < 0 || b < 0 || c < 0) continue;
                    indices.Add(a); indices.Add(b); indices.Add(c);
                }

                int Vertex(uint i)
                {
                    ulong at = (ulong)mdl.FileHeader.IndexOffset[0] + ((ulong)sub.IndexOffset + i) * 2;
                    if (at + 2 > (ulong)mdl.Data.Length) return -1;
                    var vi = BinaryPrimitives.ReadUInt16LittleEndian(mdl.Data.AsSpan((int)at, 2));
                    if (vi >= mesh.VertexCount) return -1;
                    if (local.TryGetValue(vi, out var existing)) return existing;
                    if (!ReadVertex(mdl, mesh, decl, vi, out var p, out var n, out var uv)) { local[vi] = -1; return -1; }
                    var index = positions.Count;
                    positions.Add(p); normals.Add(n); uvs.Add(uv);
                    local[vi] = index;
                    return index;
                }
            }
        }
        if (indices.Count < 3 || positions.Count < 3) return null;

        var colors = SampleColors(weaponFolder, variant, materialIndex >= 0 && materialIndex < materials.Count ? materials[materialIndex] : null, uvs);
        return Reframe(positions, normals, colors, indices, $"{mdlPath} v{variant}");
    }

    private static bool ReadVertex(ModelGeometryData mdl, Lumina.Data.Parsing.MdlStructs.MeshStruct mesh,
        Lumina.Data.Parsing.MdlStructs.VertexDeclarationStruct decl, int vi, out Vector3 position, out Vector3 normal, out Vector2 uv)
    {
        position = default; normal = Vector3.UnitY; uv = default;
        var hasPosition = false;
        foreach (var e in decl.VertexElements)
        {
            if (e.Stream == 255 || e.Stream >= mesh.VertexBufferOffset.Length || e.Stream >= mesh.VertexBufferStride.Length) continue;
            if (e.Usage is not (0 or 3 or 4)) continue;
            int size = e.Type switch { 1 => 8, 2 => 12, 3 => 16, 13 => 4, 14 => 8, 8 => 4, _ => 0 };
            if (size == 0) continue;
            ulong offset = (ulong)mdl.FileHeader.VertexOffset[0] + mesh.VertexBufferOffset[e.Stream] +
                           (ulong)vi * mesh.VertexBufferStride[e.Stream] + e.Offset;
            if (offset + (uint)size > (ulong)mdl.Data.Length) return false;
            var span = mdl.Data.AsSpan((int)offset, size);
            Vector4 v = e.Type switch
            {
                1 => new(F32(span, 0), F32(span, 4), 0, 0),
                2 => new(F32(span, 0), F32(span, 4), F32(span, 8), 0),
                3 => new(F32(span, 0), F32(span, 4), F32(span, 8), F32(span, 12)),
                13 => new(F16(span, 0), F16(span, 2), 0, 0),
                14 => new(F16(span, 0), F16(span, 2), F16(span, 4), F16(span, 6)),
                8 => new(span[0] / 127.5f - 1f, span[1] / 127.5f - 1f, span[2] / 127.5f - 1f, 0),
                _ => default,
            };
            switch (e.Usage)
            {
                case 0: position = new(v.X, v.Y, v.Z); hasPosition = Finite(position); break;
                case 3: normal = new(v.X, v.Y, v.Z); break;
                case 4: uv = new(v.X, v.Y); break;
            }
        }
        if (!Finite(normal) || normal.LengthSquared() < 1e-6f) normal = Vector3.UnitY;
        return hasPosition;
    }

    private Vector4[] SampleColors(string weaponFolder, int variant, string? material, List<Vector2> uvs)
    {
        var colors = new Vector4[uvs.Count];
        var fallbackColor = new Vector4(0.45f, 0.38f, 0.30f, 1f);
        Array.Fill(colors, fallbackColor);
        if (material == null) return colors;
        try
        {
            var mtrlPath = $"{weaponFolder}/material/v{variant:D4}{material}";
            var mtrl = Core.Services.DataManager.GameData.GetFile<MtrlFile>(mtrlPath);
            if (mtrl == null && variant != 1)
                mtrl = Core.Services.DataManager.GameData.GetFile<MtrlFile>($"{weaponFolder}/material/v0001{material}");
            if (mtrl == null) return colors;
            string? diffuse = null, first = null;
            foreach (var t in mtrl.TextureOffsets)
            {
                int end = (int)t.Offset;
                while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
                var path = Encoding.UTF8.GetString(mtrl.Strings, (int)t.Offset, end - (int)t.Offset);
                first ??= path;
                if (path.EndsWith("_d.tex", StringComparison.Ordinal) || path.EndsWith("_base.tex", StringComparison.Ordinal)) diffuse = path;
            }
            var texPath = diffuse ?? first;
            if (texPath == null) return colors;
            var tex = Core.Services.DataManager.GameData.GetFile<TexFile>(texPath);
            if (tex == null) return colors;
            int w = tex.Header.Width, h = tex.Header.Height;
            var img = tex.ImageData;
            if (w <= 0 || h <= 0 || img.Length < w * h * 4) return colors;
            for (var i = 0; i < uvs.Count; i++)
            {
                var uv = uvs[i];
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) continue;
                var u = uv.X - MathF.Floor(uv.X);
                var v = uv.Y - MathF.Floor(uv.Y);
                var px = Math.Clamp((int)(u * w), 0, w - 1);
                var py = Math.Clamp((int)(v * h), 0, h - 1);
                var o = (py * w + px) * 4;
                colors[i] = new Vector4(img[o + 2] / 255f, img[o + 1] / 255f, img[o] / 255f, 1f);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Arrow impacts: arrow texture of {weaponFolder} unreadable; using a plain colour.");
        }
        return colors;
    }

    /// <summary>Put the point at the origin with the shaft along -Z. The shaft is the longest extent;
    /// the point is the end with the smaller spread (the fletching flares, the head does not).</summary>
    private static ArrowMesh Reframe(List<Vector3> positions, List<Vector3> normals, Vector4[] colors, List<int> indices, string source)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var extent = max - min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float Along(Vector3 p) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
        float lo = Along(min), hi = Along(max), length = hi - lo;
        // Spread is measured about each end's own centre, so a shaft that sits off the model origin
        // (it rests against the bow) does not look wider than it is.
        Vector3 Lateral(Vector3 p) { if (axis == 0) p.X = 0; else if (axis == 1) p.Y = 0; else p.Z = 0; return p; }
        Vector3 EndCentre(bool low)
        {
            var sum = Vector3.Zero; var n = 0;
            foreach (var p in positions)
            {
                var t = (Along(p) - lo) / MathF.Max(1e-6f, length);
                if (low ? t < 0.12f : t > 0.88f) { sum += Lateral(p); n++; }
            }
            return n > 0 ? sum / n : Vector3.Zero;
        }
        float EndSpread(bool low, Vector3 about)
        {
            var spread = 0f;
            foreach (var p in positions)
            {
                var t = (Along(p) - lo) / MathF.Max(1e-6f, length);
                if (low ? t < 0.12f : t > 0.88f) spread = MathF.Max(spread, (Lateral(p) - about).Length());
            }
            return spread;
        }
        var centreLo = EndCentre(true); var centreHi = EndCentre(false);
        var tipAtLow = EndSpread(true, centreLo) <= EndSpread(false, centreHi);
        // The point is turned on the shaft axis, so its cluster centre is the axis.
        var centre = tipAtLow ? centreLo : centreHi;
        var forward = Vector3.Zero;
        if (axis == 0) forward.X = tipAtLow ? -1 : 1; else if (axis == 1) forward.Y = tipAtLow ? -1 : 1; else forward.Z = tipAtLow ? -1 : 1;
        var tip = centre; if (axis == 0) tip.X = tipAtLow ? lo : hi; else if (axis == 1) tip.Y = tipAtLow ? lo : hi; else tip.Z = tipAtLow ? lo : hi;

        var u = Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(forward, u);
        var outPositions = new Vector3[positions.Count];
        var outNormals = new Vector3[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var d = positions[i] - tip;
            outPositions[i] = new Vector3(Vector3.Dot(d, u), Vector3.Dot(d, v), Vector3.Dot(d, forward));
            var n = normals[i];
            outNormals[i] = Vector3.Normalize(new Vector3(Vector3.Dot(n, u), Vector3.Dot(n, v), Vector3.Dot(n, forward)));
        }
        return new ArrowMesh
        {
            Positions = outPositions, Normals = outNormals, Colors = colors, Indices = indices.ToArray(),
            Length = length, Source = source,
        };
    }

    private static float F32(ReadOnlySpan<byte> b, int at) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[at..]));
    private static float F16(ReadOnlySpan<byte> b, int at) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b[at..]));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
