// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>
/// A dropped garment is a chain of boxes, each driving one bone of the garment's skin. A box that still
/// wraps the body keeps its worn volume; one that has come away has nothing inside it and collapses to a
/// flat panel. This class holds the pose-independent maths: is a segment off the body, how far has it
/// collapsed, and what that does to a bone, a point skinned to it and the segment's collision box.
/// </summary>
public static class GarmentFlatten
{
    /// <summary>Thickness a fully collapsed segment keeps, as a fraction of its worn depth.</summary>
    public const float CollapsedThickness = 0.2f;
    /// <summary>Seconds for a segment to collapse once it is off the body (and to refill if it returns).</summary>
    public const float CollapseSeconds = 0.35f;
    /// <summary>Slack beyond the segment's own half width before the body counts as outside it.</summary>
    public const float OffBodyMargin = 0.03f;
    /// <summary>Thinnest collision box half extent a collapsed segment is given, in metres.</summary>
    public const float MinCollapsedHalfExtent = 0.012f;

    public readonly record struct Segment(Vector3 A, Vector3 B);

    /// <summary>
    /// True when no part of the body's limb/torso axes passes through the segment any more: the segment
    /// centre is farther from every body segment than the garment segment is wide.
    /// </summary>
    public static bool IsOffBody(Vector3 center, ReadOnlySpan<Segment> body, float halfWidth)
    {
        if (body.Length == 0) return true;
        var limit = MathF.Max(0f, halfWidth) + OffBodyMargin;
        foreach (var segment in body)
            if (DistanceToSegment(center, segment) <= limit) return false;
        return true;
    }

    public static float DistanceToSegment(Vector3 point, Segment segment)
    {
        var ab = segment.B - segment.A;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-8f) return Vector3.Distance(point, segment.A);
        var t = Math.Clamp(Vector3.Dot(point - segment.A, ab) / lengthSq, 0f, 1f);
        return Vector3.Distance(point, segment.A + ab * t);
    }

    /// <summary>Move collapse progress (0 worn .. 1 flat) toward its target at a frame-rate independent rate.</summary>
    public static float Advance(float progress, bool offBody, float dt)
    {
        if (!float.IsFinite(progress)) progress = 0f;
        var step = MathF.Max(0f, dt) / CollapseSeconds;
        return Math.Clamp(progress + (offBody ? step : -step), 0f, 1f);
    }

    /// <summary>Depth scale for a collapse progress: 1 when worn, <see cref="CollapsedThickness"/> when flat.</summary>
    public static float ThicknessScale(float progress)
    {
        var t = Math.Clamp(progress, 0f, 1f);
        t = t * t * (3f - 2f * t);
        return 1f + (CollapsedThickness - 1f) * t;
    }

    /// <summary>
    /// Depth direction of a segment: perpendicular to the segment and to the body's left-right direction.
    /// Falls back to any perpendicular when the segment runs along left-right (an arm held out sideways).
    /// </summary>
    public static Vector3 DepthAxis(Vector3 segmentDirection, Vector3 lateral)
    {
        var along = SafeNormal(segmentDirection, Vector3.UnitY);
        var depth = Vector3.Cross(along, lateral);
        if (depth.LengthSquared() < 1e-4f)
            depth = Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        return SafeNormal(depth, Vector3.UnitZ);
    }

    /// <summary>
    /// Index (0 X, 1 Y, 2 Z) of the local axis of <paramref name="rotation"/> closest to a world direction.
    /// Bone scale can only act along a bone's own axes, so the collapse uses the closest one.
    /// </summary>
    public static int NearestLocalAxis(Quaternion rotation, Vector3 worldDirection, int excludeAxis = -1)
    {
        var local = Vector3.Transform(worldDirection, Quaternion.Inverse(rotation));
        var best = -1;
        var bestValue = -1f;
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis == excludeAxis) continue;
            var value = MathF.Abs(axis == 0 ? local.X : axis == 1 ? local.Y : local.Z);
            if (value > bestValue) { bestValue = value; best = axis; }
        }
        return best;
    }

    public static Vector3 LocalAxis(Quaternion rotation, int axis)
        => Vector3.Transform(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, rotation);

    /// <summary>Pull a point toward the plane through <paramref name="origin"/> with normal <paramref name="axis"/>.</summary>
    public static Vector3 FlattenPoint(Vector3 point, Vector3 origin, Vector3 axis, float scale)
        => point - axis * (Vector3.Dot(point - origin, axis) * (1f - scale));

    /// <summary>Collision box of a fully collapsed segment: only the depth extent shrinks.</summary>
    public static Vector3 CollapsedHalfExtents(Vector3 half, int depthAxis)
    {
        float Thin(float value) => MathF.Min(value, MathF.Max(MinCollapsedHalfExtent, value * CollapsedThickness));
        return depthAxis switch
        {
            0 => half with { X = Thin(half.X) },
            1 => half with { Y = Thin(half.Y) },
            _ => half with { Z = Thin(half.Z) },
        };
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-10f && float.IsFinite(value.LengthSquared()) ? Vector3.Normalize(value) : fallback;
}
