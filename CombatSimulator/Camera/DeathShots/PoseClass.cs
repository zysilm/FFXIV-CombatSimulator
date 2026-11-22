// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>How the body is posed, as far as a camera cares.</summary>
public enum PoseClass
{
    /// <summary>Torso upright, hips at standing height.</summary>
    Standing,
    /// <summary>Torso upright, hips low: kneeling, sitting, slumped against something.</summary>
    Kneeling,
    /// <summary>Lying on the back (front up).</summary>
    Supine,
    /// <summary>Lying on the belly (front down).</summary>
    Prone,
    /// <summary>Lying on a side (front horizontal).</summary>
    Side,
}

/// <summary>
/// Classifies a <see cref="BodyFrame"/> with enter/exit thresholds and a minimum dwell, so a body that hovers on a
/// boundary (a ragdoll rocking on its shoulder) does not flip the class every frame.
/// Upright: torso axis within 45° of vertical to enter, lost beyond 60°. Upright splits by hips height: standing
/// above 0.60 m, kneeling/sitting/slumped below, however low (boundary ±0.05 m by current class; heights scale
/// with the body, see <see cref="Scale"/>). Lying splits by where the front points: up beyond 0.5 = supine, down beyond 0.5 = prone,
/// between = side (boundaries ±0.12 by current class). A class is kept at least <see cref="MinDwell"/> seconds.
/// </summary>
public sealed class PoseClassifier
{
    public const float MinDwell = 0.75f;
    public const float UprightEnter = 0.7071f; // cos 45°
    public const float UprightExit = 0.5f;     // cos 60°
    public const float StandingHips = 0.60f;
    public const float HipsBand = 0.05f;
    public const float FrontUp = 0.5f;
    public const float FrontBand = 0.12f;

    private bool has;
    private float dwell;

    public PoseClass Current { get; private set; } = PoseClass.Standing;

    /// <summary>Body size relative to the reference body (1): the hips thresholds are multiplied by it, so a small
    /// body standing with its hips at 0.45 m still stands. <see cref="DeathShotSolver"/> sets it from the spine length.</summary>
    public float Scale { get; set; } = 1f;

    public void Reset()
    {
        has = false;
        dwell = 0f;
        Current = PoseClass.Standing;
    }

    /// <summary>The class a frame has with no history (no hysteresis, no dwell).</summary>
    public static PoseClass Raw(in BodyFrame f, float scale = 1f) => Decide(f, null, scale);

    public PoseClass Update(in BodyFrame frame, float dt)
    {
        if (!frame.Valid) return Current;
        if (!has)
        {
            has = true;
            dwell = 0f;
            Current = Decide(frame, null, Scale);
            return Current;
        }
        dwell += MathF.Max(0f, dt);
        var next = Decide(frame, Current, Scale);
        if (next != Current && dwell >= MinDwell)
        {
            Current = next;
            dwell = 0f;
        }
        return Current;
    }

    private static PoseClass Decide(in BodyFrame f, PoseClass? current, float scale)
    {
        var wasUpright = current is PoseClass.Standing or PoseClass.Kneeling;
        var upright = current == null ? f.Axis.Y >= 0.6f : wasUpright ? f.Axis.Y >= UprightExit : f.Axis.Y >= UprightEnter;
        if (upright)
        {
            var line = StandingHips * scale;
            if (current == PoseClass.Standing) line -= HipsBand * scale;
            else if (current == PoseClass.Kneeling) line += HipsBand * scale;
            return f.HipsHeight >= line ? PoseClass.Standing : PoseClass.Kneeling;
        }
        var up = f.Front.Y;
        var supineLine = FrontUp + (current == PoseClass.Supine ? -FrontBand : current is PoseClass.Side or PoseClass.Prone ? FrontBand : 0f);
        var proneLine = -FrontUp + (current == PoseClass.Prone ? FrontBand : current is PoseClass.Side or PoseClass.Supine ? -FrontBand : 0f);
        if (up >= supineLine) return PoseClass.Supine;
        if (up <= proneLine) return PoseClass.Prone;
        return PoseClass.Side;
    }

    public static bool IsLying(PoseClass c) => c is PoseClass.Supine or PoseClass.Prone or PoseClass.Side;
}
