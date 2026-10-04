// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>
/// Immutable managed human.pbd metadata and pre-skinning, bone-name-weighted deformation.
/// The caller supplies verified source/target resource identities; no model-path inference,
/// game pointers, rig mutation, ancestor-bone guesses or inverse/unrelated race conversion.
/// Independently implemented from binary layout and affine-transform facts documented by:
/// https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Formats/Files/PbdFile.cs
/// https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Utils/MeshBuilder.cs
/// Reference implementation code is not included or adapted into this MPL implementation.
/// </summary>
internal sealed class FluidRaceDeformation
{
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    public const int MaximumEntries = 1024, MaximumBonesPerStep = 1024;
    public const int MaximumTotalBones = 131072, MaximumChainSteps = 64;
    public const int MaximumBoneNameBytes = 128, MaximumInfluences = 8;
    private const ushort NullLink = ushort.MaxValue;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Header[] headers;
    private readonly Link[] links;
    private readonly Dictionary<ushort, int> raceHeaders;
    private readonly Dictionary<int, Dictionary<string, Matrix4x4>> matricesByOffset;
    public int EntryCount => headers.Length;
    public int MatrixCount { get; }

    private readonly record struct Header(ushort RaceId, ushort LinkIndex, int Offset);
    private readonly record struct Link(ushort Parent, ushort FirstChild, ushort NextSibling, ushort HeaderIndex);
    private readonly record struct Payload(int Offset, int BoneCount, int MatrixStart, int End);

    private FluidRaceDeformation(Header[] headers, Link[] links, Dictionary<ushort, int> races,
        Dictionary<int, Dictionary<string, Matrix4x4>> matrices, int matrixCount)
    {
        this.headers = headers; this.links = links; raceHeaders = races;
        matricesByOffset = matrices; MatrixCount = matrixCount;
    }

    /// <summary>
    /// Parse a stable private snapshot. Count/offset arithmetic uses file-range checks before
    /// slicing, and every float, UTF-8 name and link is validated before publication.
    /// Zero deformer offsets are allowed as metadata, but cannot supply a non-identity step.
    /// </summary>
    public static bool TryParse(byte[]? data, out FluidRaceDeformation? result, out string error)
    {
        result = null; error = string.Empty;
        if (data == null || data.Length < 4 || data.Length > MaximumFileBytes)
            return Reject("PBD file length outside supported bounds", out error);
        var bytes = (byte[])data.Clone();
        var span = bytes.AsSpan();
        int count = I32(span, 0);
        if (count <= 0 || count > MaximumEntries)
            return Reject("PBD header count outside supported bounds", out error);
        int tableEnd = 4 + count * 20;
        if (!Range(span.Length, 4, count * 20))
            return Reject("PBD header/link tables are truncated", out error);
        var headers = new Header[count]; var links = new Link[count];
        var races = new Dictionary<ushort, int>(count);
        var payloads = new Dictionary<int, Payload>(count);
        int totalBones = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * 12;
            var header = new Header(U16(span, at), U16(span, at + 2), I32(span, at + 4));
            if (header.RaceId == 0 || !races.TryAdd(header.RaceId, i) || header.LinkIndex >= count ||
                !float.IsFinite(F32(span, at + 8)))
                return Reject("PBD header has duplicate/invalid race, link or nonfinite metadata", out error);
            headers[i] = header;
            if (header.Offset == 0 || payloads.ContainsKey(header.Offset)) continue;
            if (header.Offset < tableEnd || (header.Offset & 3) != 0 || !Range(span.Length, header.Offset, 4))
                return Reject("PBD deformer offset overlaps metadata, is unaligned or out of range", out error);
            int bones = I32(span, header.Offset);
            if (bones < 0 || bones > MaximumBonesPerStep || totalBones > MaximumTotalBones - bones)
                return Reject("PBD bone count outside supported bounds", out error);
            int namesLength = bones * 2;
            int matrixStart = header.Offset + 4 + namesLength + (namesLength & 3);
            int matrixLength = bones * 48;
            if (!Range(span.Length, header.Offset + 4, namesLength + (namesLength & 3)) ||
                !Range(span.Length, matrixStart, matrixLength))
                return Reject("PBD name offsets or 12-float matrices are truncated", out error);
            payloads.Add(header.Offset, new Payload(header.Offset, bones, matrixStart, matrixStart + matrixLength));
            totalBones += bones;
        }
        var ordered = new Payload[payloads.Count]; payloads.Values.CopyTo(ordered, 0);
        Array.Sort(ordered, static (a, b) => a.Offset.CompareTo(b.Offset));
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Offset < ordered[i - 1].End)
                return Reject("PBD deformer numeric payloads overlap", out error);
        for (int i = 0; i < count; i++)
        {
            int at = 4 + count * 12 + i * 8;
            var link = new Link(U16(span, at), U16(span, at + 2), U16(span, at + 4), U16(span, at + 6));
            if (!LinkIndex(link.Parent, count) || !LinkIndex(link.FirstChild, count) ||
                !LinkIndex(link.NextSibling, count) || link.HeaderIndex >= count)
                return Reject("PBD parent/child/sibling/header link outside table", out error);
            links[i] = link;
        }
        if (!ValidateHierarchy(headers, links, out error)) return false;
        var matrices = new Dictionary<int, Dictionary<string, Matrix4x4>>(payloads.Count);
        Span<float> values = stackalloc float[12];
        foreach (var payload in ordered)
        {
            var bones = new Dictionary<string, Matrix4x4>(payload.BoneCount, StringComparer.Ordinal);
            for (int i = 0; i < payload.BoneCount; i++)
            {
                // Unsigned, block-relative byte offsets; names may share a separate string
                // pool, so do not assume the next deformer offset terminates every string.
                int nameAt = payload.Offset + U16(span, payload.Offset + 4 + i * 2);
                if (!TryReadName(span, nameAt, tableEnd, ordered, out var name))
                    return Reject("PBD bone name is invalid, unterminated or overlaps numeric metadata", out error);
                int at = payload.MatrixStart + i * 48;
                for (int f = 0; f < values.Length; f++)
                {
                    values[f] = F32(span, at + f * 4);
                    if (!float.IsFinite(values[f])) return Reject("PBD matrix contains nonfinite values", out error);
                }
                // On-disk rows describe x', y', z' as affine dot products. Numerics
                // multiplies row vectors, so transpose the linear block and put its
                // three translations in M41/M42/M43; no handedness/axis guess is applied.
                var matrix = new Matrix4x4(values[0], values[4], values[8], 0,
                    values[1], values[5], values[9], 0, values[2], values[6], values[10], 0,
                    values[3], values[7], values[11], 1);
                if (!bones.TryAdd(name, matrix)) return Reject("PBD deformer has duplicate bone names", out error);
            }
            matrices.Add(payload.Offset, bones);
        }
        result = new FluidRaceDeformation(headers, links, races, matrices, totalBones);
        return true;
    }

    /// <summary>Only a forward descendant path proven by target's parent links is supported.</summary>
    public bool TryGetChain(ushort sourceId, ushort targetId, out Chain? chain, out string error)
    {
        chain = null; error = string.Empty;
        if (!raceHeaders.TryGetValue(sourceId, out int source) || !raceHeaders.TryGetValue(targetId, out int target))
            return Reject("PBD source or target race identity is absent", out error);
        Span<int> reverse = stackalloc int[MaximumChainSteps];
        int length = 0, current = target;
        while (current != source)
        {
            if (length >= reverse.Length) return Reject("PBD chain exceeds supported depth", out error);
            var header = headers[current];
            if (header.Offset == 0 || !matricesByOffset.ContainsKey(header.Offset))
                return Reject("PBD forward chain lacks a deformer payload", out error);
            reverse[length++] = current;
            ushort parent = links[header.LinkIndex].Parent;
            if (parent == NullLink) return Reject("PBD target does not descend from source; inverse/sibling conversion unsupported", out error);
            current = links[parent].HeaderIndex;
        }
        var steps = new int[length];
        for (int i = 0; i < length; i++) steps[i] = reverse[length - 1 - i];
        chain = new Chain(this, sourceId, targetId, steps);
        return true;
    }

    internal sealed class Chain
    {
        private readonly FluidRaceDeformation owner;
        private readonly int[] steps;
        public ushort SourceId { get; }
        public ushort TargetId { get; }
        public int StepCount => steps.Length;
        internal Chain(FluidRaceDeformation owner, ushort source, ushort target, int[] steps)
        { this.owner = owner; SourceId = source; TargetId = target; this.steps = steps; }

        public bool TryGetStepRaceIds(int step, out ushort source, out ushort target)
        {
            source = target = 0;
            if ((uint)step >= (uint)steps.Length) return false;
            target = owner.headers[steps[step]].RaceId;
            source = step == 0 ? SourceId : owner.headers[steps[step - 1]].RaceId;
            return true;
        }

        public bool TryGetBoneMatrix(int step, string boneName, out Matrix4x4 matrix)
        {
            matrix = default;
            return (uint)step < (uint)steps.Length && !string.IsNullOrEmpty(boneName) &&
                owner.matricesByOffset[owner.headers[steps[step]].Offset].TryGetValue(boneName, out matrix);
        }

        /// <summary>
        /// Allocation-free success path; each step blends affine transforms of the current
        /// position before advancing to the next step. Input influences must be normalized
        /// within 2% quantization tolerance; their positive weights are normalized once.
        /// Missing positive-weight bones fail the whole operation. No identity/parent fallback.
        /// An identity race chain accepts a finite position even with no influences.
        /// </summary>
        public bool TryDeform(Vector3 position, ReadOnlySpan<string> boneNames, ReadOnlySpan<float> weights,
            out Vector3 deformed, out string error)
        {
            deformed = position; error = string.Empty;
            if (!Finite(position)) return Reject("PBD input position is nonfinite", out error);
            if (steps.Length == 0) return true;
            if (weights.Length == 0 || weights.Length > MaximumInfluences || boneNames.Length != weights.Length)
                return Reject("PBD influence arrays must contain one to eight matched entries", out error);
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if (!float.IsFinite(weights[i]) || weights[i] < 0 || weights[i] > 1 ||
                    weights[i] > 0 && string.IsNullOrEmpty(boneNames[i]))
                    return Reject("PBD influence weight/name is invalid", out error);
                sum += weights[i];
            }
            if (sum <= 0 || Math.Abs(sum - 1) > 0.02)
                return Reject("PBD influence sum is not normalized within quantization tolerance", out error);
            var current = position;
            for (int step = 0; step < steps.Length; step++)
            {
                double x = 0, y = 0, z = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (weights[i] == 0) continue;
                    if (!TryGetBoneMatrix(step, boneNames[i], out var matrix))
                        return Reject($"PBD step {step} lacks positive-weight bone '{boneNames[i]}'", out error);
                    var sample = Vector3.Transform(current, matrix);
                    if (!Finite(sample)) return Reject("PBD affine transform produced nonfinite position", out error);
                    var weight = weights[i] / sum;
                    x += sample.X * weight; y += sample.Y * weight; z += sample.Z * weight;
                }
                current = new Vector3((float)x, (float)y, (float)z);
                if (!Finite(current)) return Reject("PBD weighted step produced nonfinite position", out error);
            }
            deformed = current;
            return true;
        }
    }

    private static bool ValidateHierarchy(Header[] headers, Link[] links, out string error)
    {
        error = string.Empty;
        int count = links.Length;
        for (int i = 0; i < count; i++)
        {
            if (links[headers[i].LinkIndex].HeaderIndex != i || headers[links[i].HeaderIndex].LinkIndex != i)
                return Reject("PBD header/link mapping is not reciprocal", out error);
            if (links[i].NextSibling != NullLink && links[links[i].NextSibling].Parent != links[i].Parent)
                return Reject("PBD sibling has a different parent", out error);
        }
        var marks = new byte[count];
        for (int pass = 0; pass < 2; pass++)
        {
            Array.Clear(marks);
            for (int start = 0; start < count; start++)
            {
                int current = start;
                while (current != NullLink && marks[current] == 0)
                {
                    marks[current] = 1;
                    current = pass == 0 ? links[current].Parent : links[current].NextSibling;
                }
                if (current != NullLink && marks[current] == 1)
                    return Reject("PBD parent or sibling graph contains a cycle", out error);
                current = start;
                while (current != NullLink && marks[current] == 1)
                {
                    marks[current] = 2;
                    current = pass == 0 ? links[current].Parent : links[current].NextSibling;
                }
            }
        }
        var childOwners = new bool[count];
        for (int parent = 0; parent < count; parent++)
        {
            int child = links[parent].FirstChild;
            while (child != NullLink)
            {
                if (links[child].Parent != parent || childOwners[child])
                    return Reject("PBD child list is inconsistent or duplicated", out error);
                childOwners[child] = true; child = links[child].NextSibling;
            }
            int depth = 0, current = parent;
            while (links[current].Parent != NullLink)
            {
                if (++depth > MaximumChainSteps) return Reject("PBD parent depth outside supported bounds", out error);
                current = links[current].Parent;
            }
        }
        for (int i = 0; i < count; i++)
            if (links[i].Parent != NullLink && !childOwners[i])
                return Reject("PBD parent references an unlisted child", out error);
        return true;
    }

    private static bool TryReadName(ReadOnlySpan<byte> data, int at, int tableEnd, Payload[] payloads, out string name)
    {
        name = string.Empty;
        if (at < tableEnd || at >= data.Length) return false;
        // Find the first numeric block strictly after this address. Checking once rather
        // than once per byte keeps name validation bounded by O(log(blocks) + nameBytes).
        int low = 0, high = payloads.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (payloads[middle].Offset <= at) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && at < payloads[low - 1].End) return false;
        int limit = Math.Min(data.Length, at + MaximumBoneNameBytes + 1);
        if (low < payloads.Length) limit = Math.Min(limit, payloads[low].Offset);
        for (int cursor = at; cursor < limit; cursor++)
        {
            if (data[cursor] == 0)
            {
                int length = cursor - at;
                if (length == 0) return false;
                try { name = StrictUtf8.GetString(data.Slice(at, length)); }
                catch (DecoderFallbackException) { return false; }
                foreach (char character in name) if (char.IsControl(character)) return false;
                return true;
            }
        }
        return false;
    }

    private static bool Range(int length, int offset, int size) => offset >= 0 && size >= 0 && offset <= length - size;
    private static bool LinkIndex(ushort index, int count) => index == NullLink || index < count;
    private static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2));
    private static int I32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4));
    private static float F32(ReadOnlySpan<byte> bytes, int at) => BitConverter.Int32BitsToSingle(I32(bytes, at));
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Reject(string reason, out string error) { error = reason; return false; }
}
