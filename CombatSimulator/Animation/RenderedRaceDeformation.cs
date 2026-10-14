// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace CombatSimulator.Animation;

/// <summary>Bounded read-only human.pbd reader. File-layout reference:
/// https://github.com/Ottermandias/Penumbra.GameData/blob/main/Files/PbdFile.cs
/// and Data/RacialDeformer.cs. Independent implementation; no experimental module dependency.
/// Extended PBD and runtime native deformer edits are deliberately not advertised as captured.</summary>
internal sealed class RenderedRaceDeformation
{
    private readonly record struct Entry(ushort Race, int Node, Dictionary<string, Matrix4x4> Matrices);
    private readonly Entry[] entries;
    private readonly (int Parent, int Entry)[] nodes;

    internal RenderedRaceDeformation(byte[] bytes)
    {
        int ReadInt(int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
        int ReadShort(int at) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2));
        int count = ReadInt(0);
        if (count is < 1 or > 256 || bytes.Length < 4 + count * 20) throw new InvalidDataException("Invalid PBD tables.");
        entries = new Entry[count]; nodes = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * 12;
            ushort race = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
            int node = ReadShort(at + 2), offset = ReadInt(at + 4);
            if (node < 0 || node >= count) throw new InvalidDataException("Invalid PBD node.");
            var matrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            if (offset != 0)
            {
                int bones = ReadInt(offset);
                if (bones is < 0 or > 4096) throw new InvalidDataException("Invalid PBD bone count.");
                int matrixStart = checked(offset + 4 + ((bones * 2 + 3) & ~3));
                if (matrixStart < 0 || (long)matrixStart + bones * 48 > bytes.Length) throw new InvalidDataException("Truncated PBD matrices.");
                for (int b = 0; b < bones; b++)
                {
                    int name = checked(offset + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4 + b * 2, 2)));
                    if ((uint)name >= bytes.Length) throw new InvalidDataException("Invalid PBD name.");
                    int end = Array.IndexOf(bytes, (byte)0, name);
                    if (end < 0 || end - name > 256) throw new InvalidDataException("Unterminated PBD bone name.");
                    int mat = matrixStart + b * 48;
                    float F(int j) => BitConverter.Int32BitsToSingle(ReadInt(mat + j * 4));
                    var matrix = new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0,
                        F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
                    if (!float.IsFinite(matrix.GetDeterminant())) throw new InvalidDataException("Non-finite PBD matrix.");
                    matrices.Add(Encoding.UTF8.GetString(bytes, name, end - name), matrix);
                }
            }
            entries[i] = new(race, node, matrices);
            int tree = 4 + count * 12 + i * 8;
            nodes[i] = (ReadShort(tree), ReadShort(tree + 6));
        }
    }

    internal bool TryBuild(ushort sourceRace, ushort targetRace, string[] boneNames, int[] parents, out Matrix4x4[] matrices)
    {
        matrices = new Matrix4x4[boneNames.Length];
        Array.Fill(matrices, Matrix4x4.Identity);
        if (sourceRace == targetRace) return true;
        var chain = new List<Dictionary<string, Matrix4x4>>();
        int current = Array.FindIndex(entries, e => e.Race == targetRace);
        var visited = new HashSet<int>();
        while (current >= 0 && current < entries.Length && visited.Add(current))
        {
            var entry = entries[current];
            if (entry.Race == sourceRace)
            {
                // Match the PBD deformer composition: start with the target's bone map,
                // append ancestor entries only for bones present in that map. In row-vector
                // notation Append is right multiplication. Resolve missing skin bones through
                // their skeleton ancestors after composing the complete per-bone map.
                var combined = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
                if (chain.Count > 0)
                    foreach (var pair in chain[0]) combined.Add(pair.Key, pair.Value);
                var keys = new List<string>(combined.Keys);
                for (int step = 1; step < chain.Count; step++)
                    foreach (string key in keys)
                        if (chain[step].TryGetValue(key, out var next)) combined[key] *= next;
                for (int bone = 0; bone < matrices.Length; bone++)
                {
                    int ancestor = bone;
                    var seen = new HashSet<int>();
                    while (ancestor >= 0 && ancestor < boneNames.Length && seen.Add(ancestor))
                    {
                        if (combined.TryGetValue(boneNames[ancestor], out var transform)) { matrices[bone] = transform; break; }
                        ancestor = ancestor < parents.Length ? parents[ancestor] : -1;
                    }
                }
                return true;
            }
            chain.Add(entry.Matrices);
            int parent = nodes[entry.Node].Parent;
            if (parent < 0 || parent >= nodes.Length) break;
            current = nodes[parent].Entry;
        }
        return false;
    }
}
