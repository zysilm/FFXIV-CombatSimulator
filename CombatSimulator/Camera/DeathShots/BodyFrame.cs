// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>The bone positions the automatic death shots read (world space, metres, Y up): hips j_kosi, chest
/// j_sebo_c, head j_kao, shoulders j_sako_l/r, feet j_asi_d_l/r. Game-free: whoever calls supplies them.</summary>
public readonly record struct BodyPoints(
    Vector3 Hips, Vector3 Chest, Vector3 Head, Vector3 ShoulderL, Vector3 ShoulderR, Vector3 FootL, Vector3 FootR)
{
    public bool IsFinite =>
        Finite(Hips) && Finite(Chest) && Finite(Head) && Finite(ShoulderL) && Finite(ShoulderR) && Finite(FootL) && Finite(FootR);

    public Vector3 FeetMid => (FootL + FootR) * 0.5f;

    public BodyPoints Offset(in BodyPoints d) => new(
        Hips + d.Hips, Chest + d.Chest, Head + d.Head, ShoulderL + d.ShoulderL, ShoulderR + d.ShoulderR, FootL + d.FootL, FootR + d.FootR);

    public BodyPoints Minus(in BodyPoints o) => new(
        Hips - o.Hips, Chest - o.Chest, Head - o.Head, ShoulderL - o.ShoulderL, ShoulderR - o.ShoulderR, FootL - o.FootL, FootR - o.FootR);

    public BodyPoints Scaled(float k) => new(Hips * k, Chest * k, Head * k, ShoulderL * k, ShoulderR * k, FootL * k, FootR * k);

    public static BodyPoints Lerp(in BodyPoints a, in BodyPoints b, float t) => new(
        Vector3.Lerp(a.Hips, b.Hips, t), Vector3.Lerp(a.Chest, b.Chest, t), Vector3.Lerp(a.Head, b.Head, t),
        Vector3.Lerp(a.ShoulderL, b.ShoulderL, t), Vector3.Lerp(a.ShoulderR, b.ShoulderR, t),
        Vector3.Lerp(a.FootL, b.FootL, t), Vector3.Lerp(a.FootR, b.FootR, t));

    /// <summary>The largest distance between corresponding points.</summary>
    public float MaxDistance(in BodyPoints o) => MathF.Max(
        MathF.Max(MathF.Max(Vector3.Distance(Hips, o.Hips), Vector3.Distance(Chest, o.Chest)),
            MathF.Max(Vector3.Distance(Head, o.Head), Vector3.Distance(ShoulderL, o.ShoulderL))),
        MathF.Max(Vector3.Distance(ShoulderR, o.ShoulderR), MathF.Max(Vector3.Distance(FootL, o.FootL), Vector3.Distance(FootR, o.FootR))));

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// The body's own frame, from bone positions only (the death animation is always the same id, so nothing else
/// tells one fall from another). Torso axis a = chest − hips; lateral s = right − left shoulder, made perpendicular
/// to a; front f = a × s. With the game's skeleton (character facing +Z has its right side at −X) that cross product
/// points out of the chest: Tests/DeathShots checks it against the recorded facing of every standing first frame.
/// For a body on the ground there is also the long axis: feet → head, horizontal. It does not change when the body
/// rolls about its own axis, so a lying shot can lock its azimuth to it.
/// </summary>
public readonly struct BodyFrame
{
    public readonly bool Valid;
    public readonly Vector3 Hips, Chest, Head, FeetMid;
    /// <summary>Unit torso axis, hips → chest.</summary>
    public readonly Vector3 Axis;
    /// <summary>Unit lateral axis, left shoulder → right shoulder, perpendicular to <see cref="Axis"/>.</summary>
    public readonly Vector3 Lateral;
    /// <summary>Unit front: where the chest (and, as far as bones can tell, the face) points.</summary>
    public readonly Vector3 Front;
    /// <summary>Unit horizontal direction feet → head; the horizontal torso axis when the feet are under the head,
    /// and the horizontal front when that vanishes too (standing straight).</summary>
    public readonly Vector3 LongAxis;
    /// <summary>True when feet → head has a horizontal run worth the name (> 0.35 m): the body lies or leans.</summary>
    public readonly bool LongAxisDefined;
    /// <summary>Unit horizontal front; when the front is vertical (flat on the back or the belly), the direction
    /// the head end points, so "in front" still means something.</summary>
    public readonly Vector3 FrontHorizontal;
    /// <summary>Hips above the ground given to <see cref="From"/>, metres.</summary>
    public readonly float HipsHeight;

    private BodyFrame(in BodyPoints p, float groundY)
    {
        Hips = p.Hips;
        Chest = p.Chest;
        Head = p.Head;
        FeetMid = p.FeetMid;
        HipsHeight = p.Hips.Y - groundY;
        var a = p.Chest - p.Hips;
        var s = p.ShoulderR - p.ShoulderL;
        Valid = p.IsFinite && a.LengthSquared() > 1e-6f && s.LengthSquared() > 1e-6f;
        if (!Valid)
        {
            Axis = Vector3.UnitY;
            Lateral = -Vector3.UnitX;
            Front = Vector3.UnitZ;
            LongAxis = Vector3.UnitZ;
            FrontHorizontal = Vector3.UnitZ;
            LongAxisDefined = false;
            return;
        }
        Axis = Vector3.Normalize(a);
        s -= Axis * Vector3.Dot(s, Axis);
        if (s.LengthSquared() < 1e-8f)
        {
            // Shoulders collapsed onto the spine: any perpendicular will do, prefer a horizontal one.
            s = Vector3.Cross(Axis, MathF.Abs(Axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ);
        }
        Lateral = Vector3.Normalize(s);
        Front = Vector3.Normalize(Vector3.Cross(Axis, Lateral));

        var run = p.Head - p.FeetMid;
        run.Y = 0f;
        LongAxisDefined = run.Length() > 0.35f;
        var axisFlat = new Vector3(Axis.X, 0f, Axis.Z);
        var frontFlat = new Vector3(Front.X, 0f, Front.Z);
        LongAxis = LongAxisDefined ? Vector3.Normalize(run)
            : axisFlat.Length() > 0.2f ? Vector3.Normalize(axisFlat)
            : frontFlat.Length() > 1e-3f ? Vector3.Normalize(frontFlat) : Vector3.UnitZ;
        FrontHorizontal = frontFlat.Length() > 0.35f ? Vector3.Normalize(frontFlat) : LongAxis;
    }

    public static BodyFrame From(in BodyPoints points, float groundY) => new(points, groundY);
}
