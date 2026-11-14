// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Animation;

/// <summary>When, after death, ragdoll physics takes the body over from the death animation.</summary>
public enum RagdollTriggerMode
{
    /// <summary>A set number of seconds after death.</summary>
    FixedDelay,
    /// <summary>On the last frame of the death animation that can still be caught before it ends.</summary>
    AnimationLastFrame,
    /// <summary>When nine tenths of the death animation have played.</summary>
    AnimationNinetyPercent,
    /// <summary>While the body is falling: when its middle drops fast, or at the fastest point of a slow fall.</summary>
    FallingFastest,
    /// <summary>Once the body has come down and stopped falling.</summary>
    Landed,
    /// <summary>Once the death animation has moved the body and the body has all but stopped moving.</summary>
    Still,
}

/// <summary>The animation that currently dominates a character's pose.</summary>
/// <param name="Id">Identifies the animation; changes when another one takes over.</param>
/// <param name="Time">Seconds played, within <paramref name="Duration"/>.</param>
public readonly record struct RagdollAnimationSample(nint Id, float Time, float Duration);

/// <summary>
/// Decides the moment ragdoll physics starts. Fed once a frame with what the death animation is doing
/// and how high the body's middle is; says when to start. Pure: no game access.
/// </summary>
public sealed class RagdollTrigger
{
    public static readonly string[] Labels =
    {
        "After a fixed delay",
        "One frame before the death animation ends",
        "At 90% of the death animation",
        "When the body falls fastest",
        "When the body has landed",
        "When the body has stopped moving",
    };

    /// <summary>However the moment is chosen, physics starts by this many seconds after death.</summary>
    public const float MaximumWait = 30f;

    // The game's animations are authored at 30 frames a second.
    private const float AnimationFrame = 1f / 30f;
    // An animation is only timed if it was seen from near its start: the looping stance the
    // character was in when it died is still dominant for a moment, and where it happens to be in
    // its loop says nothing about the death.
    private const float SeenFromStartSeconds = 0.5f;
    private const float SeenFromStartShare = 0.25f;
    // Dropping this fast, the body is in free fall and physics should have it.
    private const float FastFall = 1.2f;           // m/s
    // A slower fall counts once it has at least this speed, and has peaked when it loses 30% of it.
    private const float SlowFall = 0.4f;           // m/s
    private const float PeakPassedShare = 0.7f;
    // Landed: the middle has come down by a quarter of its height and is hardly moving.
    private const float LandedDropShare = 0.25f;
    private const float RestingFall = 0.15f;       // m/s
    // Still: the body has moved at least this fast since death (the stance it died in hardly
    // moves, and is not the death having played out), and has then moved no faster than the
    // still speed for the still time: longer than a pause in the middle of a death.
    private const float MovingSpeed = 0.3f;        // m/s
    private const float StillSpeed = 0.06f;        // m/s
    private const float StillSeconds = 0.3f;
    // With nothing to read, wait this long before giving up on reading it.
    private const float UnreadableGrace = 1f;

    private readonly RagdollTriggerMode mode;
    private readonly float fixedDelay;

    private nint trackedAnimation;
    private float trackedTime, trackedDuration;
    private bool trackedFromStart, animationSeen;

    private bool heightSeen;
    private float startHeight, previousHeight, fallSpeed, fastestFall;
    private int restingFrames;

    private bool motionSeen, hasMoved;
    private float stillFor;

    public RagdollTrigger(RagdollTriggerMode mode, float fixedDelay)
    {
        this.mode = Enum.IsDefined(mode) ? mode : RagdollTriggerMode.FixedDelay;
        this.fixedDelay = Math.Clamp(fixedDelay, 0f, MaximumWait);
    }

    public static RagdollTriggerMode ClampMode(int mode)
        => (RagdollTriggerMode)Math.Clamp(mode, 0, Labels.Length - 1);

    /// <summary>True when the moment is found from the animation or the motion rather than from the clock.</summary>
    public bool Automatic => mode != RagdollTriggerMode.FixedDelay;

    /// <summary>Call once a frame until it returns true.</summary>
    /// <param name="elapsed">Seconds since death, including this frame.</param>
    /// <param name="animation">The dominant animation, or null when it cannot be read.</param>
    /// <param name="middleHeight">Height of the body's middle above its own ground, in metres, or null.</param>
    /// <param name="motion">How fast the body is moving within itself, metres a second (see <see cref="BodyMotion"/>), or null.</param>
    public bool Update(float dt, float elapsed, RagdollAnimationSample? animation, float? middleHeight, float? motion = null)
    {
        if (elapsed >= MaximumWait)
            return true;
        if (mode == RagdollTriggerMode.FixedDelay)
            return elapsed >= fixedDelay;

        var animationEnded = TrackAnimation(animation);
        TrackHeight(dt, middleHeight);
        TrackMotion(dt, motion);

        switch (mode)
        {
            case RagdollTriggerMode.AnimationLastFrame:
                // The frame after this one would be too late if less than a frame of animation is left by then.
                if (trackedFromStart && trackedDuration - trackedTime <= AnimationFrame + MathF.Max(0f, dt))
                    return true;
                break;
            case RagdollTriggerMode.AnimationNinetyPercent:
                if (trackedFromStart && trackedTime >= 0.9f * trackedDuration)
                    return true;
                break;
            case RagdollTriggerMode.FallingFastest:
                if (heightSeen && (fallSpeed >= FastFall ||
                                   (fastestFall >= SlowFall && fallSpeed <= PeakPassedShare * fastestFall)))
                    return true;
                break;
            case RagdollTriggerMode.Landed:
                if (heightSeen && startHeight - previousHeight >= LandedDropShare * startHeight && restingFrames >= 2)
                    return true;
                break;
            case RagdollTriggerMode.Still:
                if (hasMoved && stillFor >= StillSeconds)
                    return true;
                break;
        }

        // Whatever the mode waits for, the death animation running out ends the wait: a body that
        // dies sitting never falls, and an animation cut short never reaches its last frame.
        if (animationEnded)
            return true;

        // Nothing to go by at all: fall back on the clock.
        var blind = mode switch
        {
            RagdollTriggerMode.AnimationLastFrame or RagdollTriggerMode.AnimationNinetyPercent => !animationSeen,
            RagdollTriggerMode.Still => !motionSeen && !animationSeen,
            _ => !heightSeen && !animationSeen,
        };
        return blind && elapsed >= MathF.Max(fixedDelay, UnreadableGrace);
    }

    /// <summary>Follows the dominant animation; true when one that was followed from its start has run out.</summary>
    private bool TrackAnimation(RagdollAnimationSample? sample)
    {
        if (sample is not { } animation || !(animation.Duration > 0f) || !float.IsFinite(animation.Time))
            return false;
        animationSeen = true;

        var ended = false;
        if (animation.Id != trackedAnimation)
        {
            // Another animation took over. If the one before had all but played out, it has ended.
            ended = trackedFromStart && trackedTime >= 0.85f * trackedDuration;
            trackedAnimation = animation.Id;
            trackedFromStart = animation.Time <= MathF.Max(SeenFromStartSeconds, SeenFromStartShare * animation.Duration);
        }
        else if (animation.Time < trackedTime - AnimationFrame)
        {
            // It wrapped round: a loop, now seen from its start.
            ended = trackedFromStart;
            trackedFromStart = true;
        }
        trackedTime = animation.Time;
        trackedDuration = animation.Duration;
        return ended;
    }

    /// <summary>
    /// How fast a body is moving within itself, for <see cref="Update"/>: given how far each of its
    /// bones has moved since the frame before, in its own space, the speed nine tenths of them
    /// are under. So a body counts as moving while a limb of it moves, and as still while only
    /// a few small bones do (cloth and hair settling).
    /// </summary>
    /// <param name="moved">Metres each bone moved; reordered by the call.</param>
    public static float? BodyMotion(Span<float> moved, float dt)
    {
        if (moved.Length == 0 || dt <= 1e-5f) return null;
        moved.Sort();
        var speed = moved[Math.Min(moved.Length - 1, (int)(0.9f * moved.Length))] / dt;
        return float.IsFinite(speed) ? speed : null;
    }

    private void TrackMotion(float dt, float? motion)
    {
        if (motion is not { } speed || !float.IsFinite(speed))
            return;
        motionSeen = true;
        hasMoved |= speed >= MovingSpeed;
        stillFor = speed <= StillSpeed ? stillFor + MathF.Max(0f, dt) : 0f;
    }

    private void TrackHeight(float dt, float? middleHeight)
    {
        if (middleHeight is not { } height || !float.IsFinite(height))
            return;
        if (!heightSeen)
        {
            heightSeen = true;
            startHeight = previousHeight = height;
            return;
        }
        if (dt <= 1e-5f)
            return;

        // Smoothed over a couple of frames: one frame's difference is mostly animation noise.
        var speed = (previousHeight - height) / dt;
        fallSpeed += (speed - fallSpeed) * 0.5f;
        fastestFall = MathF.Max(fastestFall, fallSpeed);
        restingFrames = MathF.Abs(fallSpeed) < RestingFall ? restingFrames + 1 : 0;
        previousHeight = height;
    }
}
