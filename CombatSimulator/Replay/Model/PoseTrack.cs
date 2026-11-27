// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Replay.Model;

/// <summary>Where a character was drawn on one sample: its skeleton's world transform (what its model-space pose is
/// relative to) and its draw object's position and rotation.</summary>
public struct RootPose
{
    public Vector3 SkeletonPosition;
    public Quaternion SkeletonRotation;
    public Vector3 SkeletonScale;
    public Vector3 DrawPosition;
    public Quaternion DrawRotation;

    public static RootPose Lerp(in RootPose a, in RootPose b, float t) => new()
    {
        SkeletonPosition = Vector3.Lerp(a.SkeletonPosition, b.SkeletonPosition, t),
        SkeletonRotation = PoseQuant.Nlerp(a.SkeletonRotation, b.SkeletonRotation, t),
        SkeletonScale = Vector3.Lerp(a.SkeletonScale, b.SkeletonScale, t),
        DrawPosition = Vector3.Lerp(a.DrawPosition, b.DrawPosition, t),
        DrawRotation = PoseQuant.Nlerp(a.DrawRotation, b.DrawRotation, t),
    };

    /// <summary>A model-space bone position of this sample in the world.</summary>
    public readonly Vector3 ModelToWorld(Vector3 model) =>
        SkeletonPosition + Vector3.Transform(model * SkeletonScale, SkeletonRotation);
}

/// <summary>
/// The compact form of one bone on one sample: 7 shorts (14 bytes). The model-space position as three half floats
/// (model space spans a few metres: half precision is 1 mm at 1–2 m, 4 mm at 4–8 m), the rotation as four
/// signed 16-bit components (an error of about 3e-5 per component, re-normalised on reading).
/// </summary>
public static class PoseQuant
{
    public const int ShortsPerBone = 7;
    public const int BytesPerBone = ShortsPerBone * sizeof(short);

    public static void Pack(Span<short> dst, Vector3 position, Quaternion rotation)
    {
        dst[0] = HalfBits(position.X);
        dst[1] = HalfBits(position.Y);
        dst[2] = HalfBits(position.Z);
        var l = rotation.Length();
        if (!(l > 1e-6f) || !float.IsFinite(l)) rotation = Quaternion.Identity;
        else rotation = Quaternion.Multiply(rotation, 1f / l);
        dst[3] = Snorm(rotation.X);
        dst[4] = Snorm(rotation.Y);
        dst[5] = Snorm(rotation.Z);
        dst[6] = Snorm(rotation.W);
    }

    public static void Unpack(ReadOnlySpan<short> src, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(FromHalf(src[0]), FromHalf(src[1]), FromHalf(src[2]));
        rotation = new Quaternion(src[3] / 32767f, src[4] / 32767f, src[5] / 32767f, src[6] / 32767f);
        var l = rotation.Length();
        rotation = l > 1e-6f ? Quaternion.Multiply(rotation, 1f / l) : Quaternion.Identity;
    }

    /// <summary>Normalised linear interpolation on the shorter arc (at 30 samples a second the difference from a
    /// slerp is far below what can be seen).</summary>
    public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
    {
        if (Quaternion.Dot(a, b) < 0f) b = -b;
        var q = new Quaternion(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        var l = q.Length();
        return l > 1e-6f ? Quaternion.Multiply(q, 1f / l) : a;
    }

    private static short HalfBits(float v)
    {
        if (!float.IsFinite(v)) v = 0f;
        return unchecked((short)BitConverter.HalfToUInt16Bits((Half)v));
    }

    private static float FromHalf(short bits) => (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)bits));

    private static short Snorm(float v) => (short)MathF.Round(Math.Clamp(v, -1f, 1f) * 32767f);
}

/// <summary>
/// The recorded poses of one character: a ring of samples (oldest overwritten when full), each a time, a
/// <see cref="RootPose"/> and every bone of the main skeleton in model space (<see cref="PoseQuant"/>).
/// Preallocated: appending never allocates.
/// </summary>
public sealed class PoseTrack
{
    private readonly double[] times;
    private readonly RootPose[] roots;
    private readonly short[] bones;
    private int start;

    public PoseTrack(int capacity, int boneCount)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));
        Capacity = capacity;
        BoneCount = boneCount;
        times = new double[capacity];
        roots = new RootPose[capacity];
        bones = new short[capacity * boneCount * PoseQuant.ShortsPerBone];
    }

    public int Capacity { get; }
    public int BoneCount { get; }
    /// <summary>Samples held now (at most <see cref="Capacity"/>).</summary>
    public int Count { get; private set; }

    /// <summary>The memory the track holds, bytes (the sample arrays).</summary>
    public long Bytes => (long)bones.Length * sizeof(short) + (long)times.Length * sizeof(double) +
                         (long)roots.Length * (17 * sizeof(float));

    public static long BytesFor(int capacity, int boneCount) =>
        (long)capacity * boneCount * PoseQuant.BytesPerBone + (long)capacity * (sizeof(double) + 17 * sizeof(float));

    public double OldestTime => Count > 0 ? times[start] : double.NaN;
    public double NewestTime => Count > 0 ? times[Slot(Count - 1)] : double.NaN;

    public void Clear()
    {
        start = 0;
        Count = 0;
    }

    private int Slot(int logical) => (start + logical) % Capacity;

    /// <summary>The time of the sample <paramref name="logical"/> places after the oldest.</summary>
    public double TimeAt(int logical) => times[Slot(logical)];

    /// <summary>
    /// Starts a new sample at <paramref name="time"/> (which must be later than the newest) and returns its slot, to
    /// fill with <see cref="BoneSlot"/>; the oldest sample is dropped when the ring is full. -1 (nothing changed) for a
    /// time that is not later than the newest, or not finite.
    /// </summary>
    public int Append(double time, in RootPose root)
    {
        if (!double.IsFinite(time) || Count > 0 && time <= NewestTime) return -1;
        int slot;
        if (Count < Capacity)
        {
            slot = Slot(Count);
            Count++;
        }
        else
        {
            slot = start;
            start = (start + 1) % Capacity;
        }
        times[slot] = time;
        roots[slot] = root;
        return slot;
    }

    /// <summary>The bones of a sample's slot: <see cref="BoneCount"/> × <see cref="PoseQuant.ShortsPerBone"/>.</summary>
    public Span<short> BoneSlot(int slot) =>
        bones.AsSpan(slot * BoneCount * PoseQuant.ShortsPerBone, BoneCount * PoseQuant.ShortsPerBone);

    /// <summary>
    /// The two samples around <paramref name="time"/> and how far between them it is (0 = the first). Before the
    /// oldest sample the oldest holds, after the newest the newest holds. False with no samples.
    /// </summary>
    public bool Locate(double time, out int slotA, out int slotB, out float alpha)
    {
        slotA = slotB = -1;
        alpha = 0f;
        if (Count == 0) return false;
        if (Count == 1 || !(time > OldestTime))
        {
            slotA = slotB = Slot(0);
            return true;
        }
        if (time >= NewestTime)
        {
            slotA = slotB = Slot(Count - 1);
            return true;
        }
        // The last sample not later than the time (binary search over the logical order).
        int lo = 0, hi = Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (TimeAt(mid) <= time) lo = mid;
            else hi = mid;
        }
        slotA = Slot(lo);
        slotB = Slot(hi);
        var span = times[slotB] - times[slotA];
        alpha = span > 1e-9 ? (float)Math.Clamp((time - times[slotA]) / span, 0.0, 1.0) : 0f;
        return true;
    }

    /// <summary>The pose at <paramref name="time"/>, interpolated between the samples around it (positions
    /// linearly, rotations on the shorter arc). The spans must hold <see cref="BoneCount"/> entries. False with no
    /// samples.</summary>
    public bool Sample(double time, Span<Vector3> positions, Span<Quaternion> rotations, out RootPose root)
    {
        root = default;
        if (!Locate(time, out var a, out var b, out var t)) return false;
        var sa = BoneSlot(a);
        var sb = BoneSlot(b);
        var n = BoneCount;
        if (a == b || t <= 0f)
        {
            root = roots[a];
            for (var i = 0; i < n; i++)
                PoseQuant.Unpack(sa.Slice(i * PoseQuant.ShortsPerBone, PoseQuant.ShortsPerBone), out positions[i], out rotations[i]);
            return true;
        }
        root = RootPose.Lerp(roots[a], roots[b], t);
        for (var i = 0; i < n; i++)
        {
            PoseQuant.Unpack(sa.Slice(i * PoseQuant.ShortsPerBone, PoseQuant.ShortsPerBone), out var pa, out var qa);
            PoseQuant.Unpack(sb.Slice(i * PoseQuant.ShortsPerBone, PoseQuant.ShortsPerBone), out var pb, out var qb);
            positions[i] = Vector3.Lerp(pa, pb, t);
            rotations[i] = PoseQuant.Nlerp(qa, qb, t);
        }
        return true;
    }

    /// <summary>Only the root at <paramref name="time"/> (interpolated like <see cref="Sample"/>).</summary>
    public bool SampleRoot(double time, out RootPose root)
    {
        root = default;
        if (!Locate(time, out var a, out var b, out var t)) return false;
        root = a == b || t <= 0f ? roots[a] : RootPose.Lerp(roots[a], roots[b], t);
        return true;
    }
}

/// <summary>Which stretch of the recording a replay plays.</summary>
public static class ReplayWindow
{
    /// <summary>
    /// The last <paramref name="seconds"/> up to <paramref name="end"/> (clamped to the newest sample), clipped at the
    /// oldest sample: what is there of it. False when the recording holds less than <paramref name="minimum"/>
    /// seconds of it.
    /// </summary>
    public static bool For(double oldest, double newest, double end, double seconds, double minimum, out double start, out double stop)
    {
        start = stop = double.NaN;
        if (!double.IsFinite(oldest) || !double.IsFinite(newest) || newest < oldest || !double.IsFinite(seconds) || seconds <= 0)
            return false;
        stop = double.IsFinite(end) ? Math.Clamp(end, oldest, newest) : newest;
        start = Math.Max(oldest, stop - seconds);
        return stop - start >= minimum;
    }
}
