// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>The death-shot dropdown: Auto picks one of the five by the body's pose.</summary>
public enum DeathShotProfile
{
    Auto,
    LowThreeQuarter,
    Face,
    AlongBody,
    Overhead,
    GroundProfile,
}

public enum ShotSubject
{
    /// <summary>Hips and chest (the old Death Cam's subject).</summary>
    HipsToChest,
    Head,
    /// <summary>Every body point given, head top to feet.</summary>
    WholeBody,
}

/// <summary>The motion built into a shot once the body has settled (not an option).</summary>
public enum ShotMotion
{
    /// <summary>8 s push-in of 12 %.</summary>
    PushIn,
    /// <summary>Slow arc of 20° at no more than 4°/s.</summary>
    Arc,
}

/// <summary>
/// One shot = subject + size + side rule + pitch + motion. Lengths are for the reference body (a midlander); the
/// solver scales them by the body's size. Angles in radians.
/// </summary>
public sealed record ShotRecipe(
    DeathShotProfile Profile,
    ShotSubject Subject,
    /// <summary>Vertical field of view.</summary>
    float Fov,
    /// <summary>Height of world the frame spans at the subject, metres; 0 = as close as keeps the subject inside
    /// <see cref="SafeFrame"/>.</summary>
    float FrameHeight,
    /// <summary>How far toward the frame edge (1 = the edge) the subject's points may reach.</summary>
    float SafeFrame,
    /// <summary>Elevation of the eye about the look-at point: positive = above it, looking down. For a shot with
    /// <see cref="EyeHeight"/> it is the fixed view pitch instead (negative = looking up), unless
    /// <see cref="AimAtSubject"/>.</summary>
    float Elevation,
    /// <summary>Above 0: the eye is pinned this far above the ground under it (ground-level shots).</summary>
    float EyeHeight,
    /// <summary>With <see cref="EyeHeight"/>: aim at the subject's centre instead of holding a fixed pitch.</summary>
    bool AimAtSubject,
    float MinDistance,
    float MaxDistance,
    ShotMotion Motion)
{
    public const float Deg = MathF.PI / 180f;

    /// <summary>Hips-to-chest, medium close on a narrow lens, three-quarter front, from below looking up: the
    /// three surviving shots of the old Death Cam (39–46° off the front, 19–26° up, 1.0–1.35 m, FoV 0.57–0.65).
    /// On a body on the ground the eye cannot go below it, so the same shot ends at ground level.</summary>
    public static readonly ShotRecipe LowThreeQuarter = new(DeathShotProfile.LowThreeQuarter, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: -22f * Deg, EyeHeight: 0f, AimAtSubject: false,
        MinDistance: 0.8f, MaxDistance: 4f, Motion: ShotMotion.PushIn);

    /// <summary>Head close-up from in front of the face, 25° off, slightly low.</summary>
    public static readonly ShotRecipe Face = new(DeathShotProfile.Face, ShotSubject.Head,
        Fov: 0.55f, FrameHeight: 0.55f, SafeFrame: 0.85f, Elevation: -8f * Deg, EyeHeight: 0f, AimAtSubject: false,
        MinDistance: 0.6f, MaxDistance: 3f, Motion: ShotMotion.PushIn);

    /// <summary>Whole body from beyond the feet, looking along it toward the head, 0.22 m above the ground.</summary>
    public static readonly ShotRecipe AlongBody = new(DeathShotProfile.AlongBody, ShotSubject.WholeBody,
        Fov: 0.70f, FrameHeight: 0f, SafeFrame: 0.80f, Elevation: 0f, EyeHeight: 0.22f, AimAtSubject: true,
        MinDistance: 0.9f, MaxDistance: 12f, Motion: ShotMotion.PushIn);

    /// <summary>Whole body from above at 78° (never straight down: the camera has no roll to orient a vertical
    /// view), across the body's long axis.</summary>
    public static readonly ShotRecipe Overhead = new(DeathShotProfile.Overhead, ShotSubject.WholeBody,
        Fov: 0.70f, FrameHeight: 0f, SafeFrame: 0.80f, Elevation: 78f * Deg, EyeHeight: 0f, AimAtSubject: false,
        MinDistance: 1.5f, MaxDistance: 12f, Motion: ShotMotion.Arc);

    /// <summary>Whole body in profile from 0.12 m above the ground, looking up 14° (the Directed camera's
    /// "Looking up").</summary>
    public static readonly ShotRecipe GroundProfile = new(DeathShotProfile.GroundProfile, ShotSubject.WholeBody,
        Fov: 0.78f, FrameHeight: 0f, SafeFrame: 0.80f, Elevation: -14f * Deg, EyeHeight: 0.12f, AimAtSubject: false,
        MinDistance: 0.9f, MaxDistance: 12f, Motion: ShotMotion.Arc);

    public static ShotRecipe Of(DeathShotProfile profile) => profile switch
    {
        DeathShotProfile.Face => Face,
        DeathShotProfile.AlongBody => AlongBody,
        DeathShotProfile.Overhead => Overhead,
        DeathShotProfile.GroundProfile => GroundProfile,
        _ => LowThreeQuarter,
    };

    private static readonly ShotRecipe[] AutoUpright = { LowThreeQuarter };
    private static readonly ShotRecipe[] AutoSupine = { Face, LowThreeQuarter };
    private static readonly ShotRecipe[] AutoProne = { AlongBody, GroundProfile };

    /// <summary>What Auto may use for a pose: standing, kneeling, sitting and lying on a side → Low three-quarter
    /// (on the front side); on the back → Face or Low three-quarter from the head side; on the belly → Along the
    /// body or Ground profile, never Face. Overhead is the fallback when those are blocked.</summary>
    public static IReadOnlyList<ShotRecipe> ForAuto(PoseClass pose) => pose switch
    {
        PoseClass.Supine => AutoSupine,
        PoseClass.Prone => AutoProne,
        _ => AutoUpright,
    };

    public const float ThreeQuarterAngle = 42.5f * Deg;
    public const float FaceOffAngle = 25f * Deg;
    public const float PushInAmount = 0.12f;
    public const float PushInSeconds = 8f;
    public const float ArcAngle = 20f * Deg;
    public const float ArcSeconds = 7.5f; // smoothstep over 7.5 s peaks at 1.5 · 20° / 7.5 s = 4°/s
}
