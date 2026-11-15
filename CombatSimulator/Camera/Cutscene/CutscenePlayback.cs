// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>A camera pose: eye, look-at point, up vector and VERTICAL field of view in radians.</summary>
public readonly record struct CutPose(Vector3 Eye, Vector3 Target, Vector3 Up, float VerticalFov)
{
    public bool IsFinite =>
        float.IsFinite(Eye.X) && float.IsFinite(Eye.Y) && float.IsFinite(Eye.Z) &&
        float.IsFinite(Target.X) && float.IsFinite(Target.Y) && float.IsFinite(Target.Z) &&
        float.IsFinite(Up.X) && float.IsFinite(Up.Y) && float.IsFinite(Up.Z) &&
        float.IsFinite(VerticalFov) && Vector3.DistanceSquared(Eye, Target) > 1e-8f && Up.LengthSquared() > 1e-8f;

    public Vector3 Forward => Vector3.Normalize(Target - Eye);

    /// <summary>Blend two poses (w = 0 → a, 1 → b): positions and FOV linearly, the up vector normalised.</summary>
    public static CutPose Lerp(in CutPose a, in CutPose b, float w)
    {
        w = Math.Clamp(w, 0f, 1f);
        var up = Vector3.Lerp(a.Up, b.Up, w);
        if (up.LengthSquared() < 1e-8f) up = Vector3.UnitY;
        return new CutPose(Vector3.Lerp(a.Eye, b.Eye, w), Vector3.Lerp(a.Target, b.Target, w), Vector3.Normalize(up),
            a.VerticalFov + (b.VerticalFov - a.VerticalFov) * w);
    }

    /// <summary>Smoothstep easing for blend weights.</summary>
    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// The two readings of the cutscene lens that are not yet calibrated in game (docs/CUTSCENE_SHOTS.md §5). Each is one
/// constant so it can be flipped after the passive probe (<c>/combatsim cutprobe</c>) has measured a real cutscene.
/// </summary>
public static class CutsceneLens
{
    /// <summary>c116 is the field of view in degrees; read as the VERTICAL angle (assumed). False reads it as horizontal
    /// and converts with the screen's aspect ratio.</summary>
    public const bool FovIsVertical = true;

    /// <summary>c117 is read as roll in degrees about the view axis (assumed); the sign of that rotation.</summary>
    public const float RollSign = 1f;

    /// <summary>The vertical FOV in radians for a c116 value in degrees, on a screen of the given aspect (width / height).</summary>
    public static float VerticalFov(float c116Degrees, float aspect)
    {
        var rad = c116Degrees * MathF.PI / 180f;
        if (FovIsVertical || !(aspect > 0f) || !float.IsFinite(aspect)) return rad;
        return 2f * MathF.Atan(MathF.Tan(rad / 2f) / aspect);
    }
}

/// <summary>
/// Samples a curated segment at a time in seconds: the segment's shots back to back at 30 timeline frames per second,
/// each sampled continuously from its curves (cubic Hermite, <see cref="CurveChannel.Eval"/>), cutting from one shot to
/// the next exactly where its span ends. Game-free; the caller supplies the anchor frames of the attach slots.
/// </summary>
public static class CutscenePlayback
{
    public const float Fps = 30f;

    public readonly record struct Sample(CutPose Pose, float FovDegrees, float RollDegrees, int SpanIndex, int ShotIndex, float LocalFrame);

    public static Sample Evaluate(LoadedCutsceneShot shot, float seconds, Func<Attach, Matrix4x4> anchor, float aspect = 16f / 9f)
    {
        var frames = Math.Clamp(seconds * Fps, 0f, MathF.Max(0f, shot.Frames - 0.001f));
        var acc = 0f;
        for (var i = 0; i < shot.Spans.Count; i++)
        {
            var (span, s) = shot.Spans[i];
            var len = span.To - span.From;
            var last = i == shot.Spans.Count - 1;
            if (frames < acc + len || last)
            {
                var timelineFrame = span.From + MathF.Min(frames - acc, len - 0.001f);
                var local = timelineFrame - s.Start;
                var e = s.Evaluate(local, anchor);
                var roll = s.Channel(9, 117, 255)?.Eval(local) ?? 0f;
                var up = ApplyRoll(e.Eye, e.Aim, e.Up, roll * CutsceneLens.RollSign);
                var pose = new CutPose(e.Eye, e.Aim, up, CutsceneLens.VerticalFov(e.Fov, aspect));
                return new Sample(pose, e.Fov, roll, i, span.Shot, local);
            }
            acc += len;
        }
        throw new InvalidOperationException("segment without shots");
    }

    /// <summary>Rotate the up vector about the view axis by <paramref name="degrees"/> (after removing its part along
    /// the view axis). A degenerate input returns world up.</summary>
    public static Vector3 ApplyRoll(Vector3 eye, Vector3 aim, Vector3 up, float degrees)
    {
        var f = aim - eye;
        if (f.LengthSquared() < 1e-8f) return Vector3.UnitY;
        f = Vector3.Normalize(f);
        var u = up - Vector3.Dot(up, f) * f;
        if (u.LengthSquared() < 1e-8f)
        {
            u = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, f) * f;
            if (u.LengthSquared() < 1e-8f) return Vector3.UnitY;
        }
        u = Vector3.Normalize(u);
        if (MathF.Abs(degrees) < 1e-4f) return u;
        var q = Quaternion.CreateFromAxisAngle(f, degrees * MathF.PI / 180f);
        return Vector3.Normalize(Vector3.Transform(u, q));
    }

    /// <summary>Keep the eye above the floor under the followed actor (a cut composed for a standing person, replayed
    /// on a body lying on the ground, would otherwise put the lens into the ground).</summary>
    public static CutPose ClampEye(in CutPose pose, float floorY, float margin)
    {
        if (!float.IsFinite(floorY) || pose.Eye.Y >= floorY + margin) return pose;
        return pose with { Eye = new Vector3(pose.Eye.X, floorY + margin, pose.Eye.Z) };
    }
}
