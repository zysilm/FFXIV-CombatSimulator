// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Replay.Model;

// Every file in Replay/Model is game-free by rule (docs/DEATH_REPLAY.md §2): Tests/DeathReplay links them as they are.

/// <summary>
/// One camera shot of the death replay, saved in the configuration. It is stored RELATIVE to the body at death
/// (<see cref="ReplayFrame"/>): the eye's offset from where the player stood when they died, in the frame turned by
/// the player's facing then, and the view's direction as an azimuth from that facing and a pitch. The same shot
/// therefore shows the same picture of every death, wherever it happens and whichever way the player faced.
/// </summary>
public sealed class DeathReplayShot
{
    /// <summary>The eye in the frame of the body at death, metres: +Z is the way the player faced, +Y is up, +X is
    /// the facing turned a quarter to the left (the frame is the world's turned by the facing about +Y).</summary>
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
    /// <summary>The view's azimuth minus the facing at death, radians (0: looking the way the player faced).</summary>
    public float Yaw { get; set; }
    /// <summary>The view's pitch, radians (positive: looking up).</summary>
    public float Pitch { get; set; }
    /// <summary>The vertical field of view the shot renders with, radians.</summary>
    public float Fov { get; set; } = ReplayMath.DefaultFov;

    public Vector3 Offset => new(OffsetX, OffsetY, OffsetZ);

    public DeathReplayShot Clone() => new()
    {
        OffsetX = OffsetX, OffsetY = OffsetY, OffsetZ = OffsetZ, Yaw = Yaw, Pitch = Pitch, Fov = Fov,
    };
}

/// <summary>The frame shots are stored in: the player's position when they died and their facing then (the game's
/// object rotation, the azimuth convention atan2(x, z)). Outside a death: where the player stands now.</summary>
public readonly record struct ReplayFrame(Vector3 Origin, float Facing)
{
    public Vector3 ToWorld(Vector3 local) => Origin + ReplayMath.RotateY(local, Facing);
    public Vector3 ToLocal(Vector3 world) => ReplayMath.RotateY(world - Origin, -Facing);
}

/// <summary>A view: the eye, the unit direction it looks along, the vertical field of view (radians).</summary>
public readonly record struct ReplayView(Vector3 Eye, Vector3 Forward, float Fov)
{
    /// <summary>The point handed to the game as the look-at point (it only needs a direction; 5 m ahead).</summary>
    public Vector3 LookAt => Eye + Forward * LookDistance;
    public const float LookDistance = 5f;
    public bool IsFinite =>
        float.IsFinite(Eye.X) && float.IsFinite(Eye.Y) && float.IsFinite(Eye.Z) &&
        float.IsFinite(Forward.X) && float.IsFinite(Forward.Y) && float.IsFinite(Forward.Z) && float.IsFinite(Fov) &&
        Forward.LengthSquared() > 0.5f;
}

/// <summary>Angles, the relative-shot conversion and the free camera's flying, all game-free.</summary>
public static class ReplayMath
{
    public const float Deg = MathF.PI / 180f;
    /// <summary>The free camera never looks straight up or down (the game needs a horizontal direction).</summary>
    public const float MaxPitch = 88f * Deg;
    public const float MinFov = 10f * Deg, MaxFov = 120f * Deg, DefaultFov = 45f * Deg;

    public static float WrapAngle(float a)
    {
        if (!float.IsFinite(a)) return 0f;
        a %= 2f * MathF.PI;
        if (a > MathF.PI) a -= 2f * MathF.PI;
        else if (a <= -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    /// <summary>Azimuth of a direction in the game's convention: atan2(x, z) (0 = +Z).</summary>
    public static float Azimuth(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    /// <summary>The unit direction with this azimuth and pitch (positive pitch = up).</summary>
    public static Vector3 Forward(float azimuth, float pitch)
    {
        var c = MathF.Cos(pitch);
        return new Vector3(c * MathF.Sin(azimuth), MathF.Sin(pitch), c * MathF.Cos(azimuth));
    }

    public static float PitchOf(Vector3 forward)
    {
        var l = forward.Length();
        return l > 1e-6f ? MathF.Asin(Math.Clamp(forward.Y / l, -1f, 1f)) : 0f;
    }

    /// <summary>Turns a vector about +Y by <paramref name="angle"/> in the azimuth convention: +Z goes to the
    /// direction of azimuth <paramref name="angle"/> (sin, 0, cos).</summary>
    public static Vector3 RotateY(Vector3 v, float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    /// <summary>A view, stored relative to <paramref name="frame"/>.</summary>
    public static DeathReplayShot ShotFromView(in ReplayFrame frame, in ReplayView view)
    {
        var local = frame.ToLocal(view.Eye);
        return new DeathReplayShot
        {
            OffsetX = local.X, OffsetY = local.Y, OffsetZ = local.Z,
            Yaw = WrapAngle(Azimuth(view.Forward) - frame.Facing),
            Pitch = Math.Clamp(PitchOf(view.Forward), -MaxPitch, MaxPitch),
            Fov = Math.Clamp(float.IsFinite(view.Fov) ? view.Fov : DefaultFov, MinFov, MaxFov),
        };
    }

    /// <summary>A stored shot, placed in <paramref name="frame"/>.</summary>
    public static ReplayView ViewFromShot(in ReplayFrame frame, DeathReplayShot shot)
    {
        var eye = frame.ToWorld(shot.Offset);
        var forward = Forward(frame.Facing + shot.Yaw, Math.Clamp(shot.Pitch, -MaxPitch, MaxPitch));
        return new ReplayView(eye, forward, Math.Clamp(float.IsFinite(shot.Fov) ? shot.Fov : DefaultFov, MinFov, MaxFov));
    }

    /// <summary>The screen-right horizontal direction of a view looking along <paramref name="forward"/> (the world
    /// is right-handed with +Y up: right = forward × up, flattened).</summary>
    public static Vector3 Right(Vector3 forward)
    {
        var r = new Vector3(-forward.Z, 0f, forward.X);
        var l = r.Length();
        return l > 1e-5f ? r / l : Vector3.UnitX;
    }

    /// <summary>
    /// One step of the free camera: <paramref name="forwardInput"/> flies along the view (pitch included),
    /// <paramref name="strafeInput"/> to the screen's right, <paramref name="upInput"/> straight up; inputs are -1..1
    /// (several at once are normalised so a diagonal is not faster), at <paramref name="speed"/> m/s.
    /// </summary>
    public static Vector3 FlyStep(Vector3 eye, Vector3 forward, float forwardInput, float strafeInput, float upInput, float speed, float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f || !float.IsFinite(speed)) return eye;
        var move = forward * forwardInput + Right(forward) * strafeInput + Vector3.UnitY * upInput;
        var l = move.Length();
        if (l < 1e-5f || !float.IsFinite(l)) return eye;
        if (l > 1f) move /= l;
        return eye + move * (speed * dt);
    }
}

/// <summary>Which shot is on the screen at a moment of the replay: the replay's length divided equally among the
/// shots, in their order; on a loop the shots cycle with the replay.</summary>
public static class ShotSchedule
{
    /// <summary>Each shot's share of the replay.</summary>
    public static float SegmentLength(float duration, int count) =>
        count > 0 && float.IsFinite(duration) && duration > 0f ? duration / count : 0f;

    /// <summary><paramref name="t"/> brought into [0, duration) (a looping replay's position in its pass).</summary>
    public static float Wrap(float t, float duration)
    {
        if (!float.IsFinite(t) || !float.IsFinite(duration) || duration <= 0f) return 0f;
        var w = t % duration;
        if (w < 0f) w += duration;
        return w >= duration ? 0f : w;
    }

    /// <summary>
    /// The shot at time <paramref name="t"/> from the start of the replay, or -1 with no shots. On a loop
    /// <paramref name="t"/> counts across the passes (the shots keep cycling in order); without one it is clamped to
    /// the pass and the last shot holds at its end.
    /// </summary>
    public static int IndexAt(float t, float duration, int count, bool loop)
    {
        if (count <= 0) return -1;
        if (!float.IsFinite(duration) || duration <= 0f) return 0;
        var inPass = loop ? Wrap(t, duration) : Math.Clamp(float.IsFinite(t) ? t : 0f, 0f, duration);
        var index = (int)MathF.Floor(inPass / SegmentLength(duration, count));
        return Math.Clamp(index, 0, count - 1);
    }
}
