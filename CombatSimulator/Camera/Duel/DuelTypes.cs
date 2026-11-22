// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.Duel;

/// <summary>The one framing option of the duel camera: how close the two-shot sits.</summary>
public enum DuelFraming
{
    Tight,
    Standard,
    Wide,
}

/// <summary>
/// One fighter as the duel camera sees it (world space, metres, Y up): where it stands, where its head is, and how
/// wide it is. Game-free: the plugin fills it from the character's position, its head bone (or its height when it has
/// none) and its hitbox radius; the offline check from scripted trajectories.
/// </summary>
public readonly record struct DuelSubject(Vector3 Feet, Vector3 Head, float Radius)
{
    public bool IsFinite =>
        float.IsFinite(Feet.X) && float.IsFinite(Feet.Y) && float.IsFinite(Feet.Z) &&
        float.IsFinite(Head.X) && float.IsFinite(Head.Y) && float.IsFinite(Head.Z) && float.IsFinite(Radius);

    /// <summary>Feet to head, never less than a crouch.</summary>
    public float Height => Math.Clamp(Head.Y - Feet.Y, 0.3f, 30f);

    /// <summary>A point on the body, <paramref name="t"/> of the way from the feet to the head.</summary>
    public Vector3 At(float t) => Vector3.Lerp(Feet, Head, t);
}

/// <summary>The key moments the duel camera answers with a brief push-in.</summary>
public enum DuelBeat
{
    /// <summary>A heavy hit of the player's landed on the opponent.</summary>
    HitGiven,
    /// <summary>A heavy hit landed on the player.</summary>
    HitTaken,
    /// <summary>The player guarded an attack.</summary>
    Guard,
    /// <summary>A telegraphed big attack on the player is about to land.</summary>
    Threat,
    /// <summary>The opponent went down.</summary>
    Finish,
}

/// <summary>One frame of input to <see cref="DuelCameraSolver"/>.</summary>
public struct DuelInput
{
    public float Dt;
    public DuelSubject Player;
    /// <summary>The fighter the framing is locked onto; null: nobody, the camera frames the player alone.</summary>
    public DuelSubject? Opponent;
    /// <summary>Who <see cref="Opponent"/> is; a change of it is a change of opponent.</summary>
    public uint OpponentId;
    /// <summary>Picture width ÷ height.</summary>
    public float Aspect;
    public DuelFraming Framing;
    /// <summary>The player's hand this frame: how far they turned the camera left/right (radians, the sense of the
    /// eye's azimuth about what it looks at), up/down (radians, positive = the eye goes up), and wheel notches
    /// (positive = closer).</summary>
    public float YawInput, PitchInput, WheelInput;
}

/// <summary>One frame of output of <see cref="DuelCameraSolver"/>.</summary>
public struct DuelOutput
{
    public Vector3 Eye, LookAt;
    /// <summary>Vertical field of view, radians.</summary>
    public float Fov;
    /// <summary>Azimuth of the eye about the look-at point: the rendered yaw, in the convention of the game camera's
    /// own yaw (the player's movement is relative to it).</summary>
    public float Yaw;
    /// <summary>The yaw the player should move by (what the game camera's own yaw is set to): the rendered yaw,
    /// leaned towards "behind the player on the line to the opponent" (see the solver).</summary>
    public float MoveYaw;
    /// <summary>This frame does not continue the last one (first frame, or the player was teleported).</summary>
    public bool Cut;
    /// <summary>Two fighters are framed (false: the player alone).</summary>
    public bool TwoShot;
    /// <summary>Which side of the line from the player to the opponent the camera sits on (+1 / −1, in the sense of
    /// the eye's azimuth about the player).</summary>
    public int Side;
    /// <summary>The camera was told to cross the line between the fighters this frame, and why ("blocked",
    /// "hidden", "player").</summary>
    public bool SideChanged;
    public string Reason;
    /// <summary>Crossings of the line asked for so far.</summary>
    public int SideChanges;
    /// <summary>The side was picked again this frame without the camera moving (the fighters crossed over, or the
    /// opponent changed), and how often so far.</summary>
    public bool Reanchored;
    public int Reanchors;
    /// <summary>Horizontal distance between the fighters.</summary>
    public float Separation;
    /// <summary>The eye's distance from the player before the push-in and the constraints, and its angle off the
    /// line "behind the player" (signed, with the player's offset).</summary>
    public float Distance, OffAxis;
    /// <summary>0..1: how much of a push-in is applied (strength included).</summary>
    public float Beat;
    /// <summary>How much of the way to its place the eye is allowed (1 = all of it; less: something stood in the way).</summary>
    public float Pull;
    /// <summary>How far the eye was lifted to stay above the ground or water.</summary>
    public float Lift;
    /// <summary>The player's standing offsets.</summary>
    public float UserYaw, UserPitch, UserSize;
    /// <summary>How much further round on its side the eye sits because its usual place is blocked (radians).</summary>
    public float Give;
}
