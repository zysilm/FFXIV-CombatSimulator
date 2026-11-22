// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>What the automatic death shots ask of the world. The plugin answers with the game's collision rays
/// (BGCollisionModule) and its water lookup; the offline check answers from a ragdoll recording.</summary>
public interface IDeathShotWorld
{
    /// <summary>Height of the ground straight under <paramref name="from"/> (the solver starts about 1.5 m above
    /// the eye, so a camera that sank into a slope still finds the surface above it). Null: nothing there.</summary>
    float? GroundBelow(Vector3 from);

    /// <summary>Height of a water surface around <paramref name="from"/>, or null.</summary>
    float? WaterBelow(Vector3 from);

    /// <summary>Whether walls or terrain block the segment, and how far along it.</summary>
    bool Blocked(Vector3 from, Vector3 to, out float distance);
}

/// <summary>Angles and critically damped springs for the death shots. Pure.</summary>
public static class DeathShotMath
{
    public const float Deg = MathF.PI / 180f;

    public static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    public static float AngleDelta(float from, float to) => WrapAngle(to - from);

    /// <summary>Azimuth of a horizontal direction in the camera convention shared with
    /// <see cref="DynamicCameraSolver"/>: (sin ψ, cos ψ) in xz.</summary>
    public static float Azimuth(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    public static Vector3 Horizontal(float azimuth) => new(MathF.Sin(azimuth), 0f, MathF.Cos(azimuth));

    /// <summary>Unit direction from the look-at point to the eye for an azimuth and an elevation (positive = the
    /// eye is above the look-at point, looking down).</summary>
    public static Vector3 OrbitDirection(float azimuth, float elevation)
    {
        var c = MathF.Cos(elevation);
        return new Vector3(MathF.Sin(azimuth) * c, MathF.Sin(elevation), MathF.Cos(azimuth) * c);
    }

    public static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// One step of a critically damped spring toward <paramref name="target"/> (exact for a constant target over
    /// the step), with the speed kept under <paramref name="maxSpeed"/>. <paramref name="smoothTime"/> is the
    /// time the value needs to cover most of a step change (the spring's natural frequency is 2 / smoothTime).
    /// </summary>
    public static float Spring(float current, float target, ref float velocity, float smoothTime, float maxSpeed, float dt)
    {
        if (dt <= 0f) return current;
        smoothTime = MathF.Max(1e-3f, smoothTime);
        var omega = 2f / smoothTime;
        var x = omega * dt;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        // The speed cap, as a cap on how far ahead the spring may be asked to reach (Unity's SmoothDamp form).
        var goal = target;
        var maxChange = maxSpeed * smoothTime;
        var change = Math.Clamp(current - target, -maxChange, maxChange);
        target = current - change;
        var temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        var result = target + (change + temp) * exp;
        // Never past the goal.
        if ((goal - current > 0f) == (result > goal))
        {
            result = goal;
            velocity = 0f;
        }
        return result;
    }

    public static Vector3 Spring(Vector3 current, Vector3 target, ref Vector3 velocity, float smoothTime, float maxSpeed, float dt)
    {
        if (dt <= 0f) return current;
        smoothTime = MathF.Max(1e-3f, smoothTime);
        var omega = 2f / smoothTime;
        var x = omega * dt;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        var change = current - target;
        var maxChange = maxSpeed * smoothTime;
        var length = change.Length();
        if (length > maxChange && length > 1e-9f) change *= maxChange / length;
        var temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        var result = current - change + (change + temp) * exp;
        var speed = velocity.Length();
        if (speed > maxSpeed) velocity *= maxSpeed / speed;
        return result;
    }
}

/// <summary>
/// "The body has come to rest": every point slower than <see cref="Speed"/> for <see cref="Hold"/> seconds. Once
/// settled it stays so until a point has moved more than <see cref="MovedAgain"/> from where it rested (grabbed,
/// kicked), which starts the count again.
/// </summary>
public sealed class SettleDetector
{
    public const float Speed = 0.05f;
    public const float Hold = 0.8f;
    public const float MovedAgain = 0.25f;

    private readonly float speedLimit, holdSeconds;
    private bool has;
    private BodyPoints last, rest;
    private float quiet;

    public SettleDetector(float speed = Speed, float hold = Hold)
    {
        speedLimit = speed;
        holdSeconds = hold;
    }

    public bool Settled { get; private set; }

    public void Reset()
    {
        has = false;
        quiet = 0f;
        Settled = false;
    }

    /// <summary>Feeds one frame. Returns whether the body counts as settled now.</summary>
    public bool Update(in BodyPoints points, float dt)
    {
        if (!points.IsFinite) return Settled;
        if (!has)
        {
            has = true;
            last = points;
            return false;
        }
        if (Settled)
        {
            if (points.MaxDistance(rest) > MovedAgain)
            {
                Settled = false;
                quiet = 0f;
            }
        }
        else if (dt > 1e-5f)
        {
            var speed = points.MaxDistance(last) / dt;
            quiet = speed < speedLimit ? quiet + dt : 0f;
            if (quiet >= holdSeconds)
            {
                Settled = true;
                rest = points;
            }
        }
        last = points;
        return Settled;
    }
}
