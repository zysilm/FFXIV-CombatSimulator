// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>
/// The death-shot dropdown (Auto and six shots in one camera language: close, low, narrow lens, three-quarter, slow),
/// and the two framings of a body that is held up. The numbers are what is saved: the shots removed on 2026-11-22
/// (Face 2, Along the body 3, Overhead 4, Ground profile 5) keep theirs unused, so a saved choice of one of them is
/// not read as something else (<see cref="DeathShotProfiles.IsChoice"/>: it becomes Auto).
/// </summary>
public enum DeathShotProfile
{
    Auto = 0,
    /// <summary>Hips to chest from the front / head side, from below.</summary>
    LowThreeQuarter = 1,
    /// <summary>The face, from in front of it, low.</summary>
    LowFace = 10,
    /// <summary>From low by the hips, up along the torso to the face.</summary>
    HipsToFace = 11,
    /// <summary>Hips to chest from the feet end, to a side.</summary>
    LowFeetSide = 12,
    /// <summary>Hips to chest from the back side.</summary>
    LowBehind = 13,
    /// <summary>The whole body, running diagonally across the frame.</summary>
    LowFullBody = 14,
    /// <summary>A held-up body, upper body, camera level with it (not in the dropdown: "When grabbed").</summary>
    GrabbedLevel = 20,
    /// <summary>A held-up body, upper body, from slightly above.</summary>
    GrabbedAbove = 21,
}

/// <summary>The "Side preference" dropdown: which of a shot's sides the camera takes when the shot has more than one.</summary>
public enum DeathShotSide
{
    /// <summary>The side nearest the camera as it is.</summary>
    Nearest = 0,
    /// <summary>The side the face points to.</summary>
    FaceSide = 1,
    /// <summary>The side the back of the head is on.</summary>
    BehindHead = 2,
    /// <summary>The body's own left.</summary>
    BodyLeft = 3,
    /// <summary>The body's own right.</summary>
    BodyRight = 4,
}

/// <summary>The "When grabbed" dropdown.</summary>
public enum DeathShotGrabbedFraming
{
    Level = 0,
    SlightlyAbove = 1,
}

public static class DeathShotProfiles
{
    /// <summary>The dropdown, in order.</summary>
    public static readonly DeathShotProfile[] Choices =
    {
        DeathShotProfile.Auto, DeathShotProfile.LowThreeQuarter, DeathShotProfile.LowFace, DeathShotProfile.HipsToFace,
        DeathShotProfile.LowFeetSide, DeathShotProfile.LowBehind, DeathShotProfile.LowFullBody,
    };

    public static readonly string[] ChoiceLabels =
    {
        "Auto", "Low three-quarter", "Low on the face", "Hips up to the face", "Low from the feet", "Low from behind", "Low full body",
    };

    public static readonly string[] GrabbedLabels = { "Level", "Slightly above" };

    public static readonly string[] SideLabels = { "Nearest", "Face side", "Behind the head", "Body's left", "Body's right" };

    /// <summary>Whether a saved value is one of the dropdown's choices (a removed shot, or anything else, is not).</summary>
    public static bool IsChoice(DeathShotProfile profile) => Array.IndexOf(Choices, profile) >= 0;

    public static string Label(DeathShotProfile profile)
    {
        var i = Array.IndexOf(Choices, profile);
        return i >= 0 ? ChoiceLabels[i] : profile switch
        {
            DeathShotProfile.GrabbedLevel => "Grabbed, level",
            DeathShotProfile.GrabbedAbove => "Grabbed, slightly above",
            _ => profile.ToString(),
        };
    }
}

public enum ShotSubject
{
    /// <summary>Hips and chest (the old Death Cam's subject).</summary>
    HipsToChest,
    Head,
    /// <summary>Chest and head: the upper body.</summary>
    ChestToHead,
    /// <summary>Every body point given, head top to feet.</summary>
    WholeBody,
}

/// <summary>
/// One shot = subject + size + side rule + pitch. Lengths are for the reference body (a midlander); the solver
/// scales them by the body's size. Angles in radians. Every shot is a free orbit about its subject (azimuth,
/// elevation, distance) and pushes in slowly once the body rests.
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
    /// <summary>Elevation of the eye about the look-at point: positive = above it, looking down. (Hips up to the
    /// face and Low on the face take theirs from the body instead: see the side rules in the solver.)</summary>
    float Elevation,
    float MinDistance,
    float MaxDistance,
    /// <summary>The eye sits below the subject looking up: with "Allow below ground" it may go under the ground.</summary>
    bool Low = true,
    /// <summary>The side in use is kept (not looked for again) while it is within this of a side the shot's rule asks
    /// for: 75° for a shot of the body, less for a shot that is about the face.</summary>
    float KeepWithin = 75f * MathF.PI / 180f)
{
    public const float Deg = MathF.PI / 180f;

    /// <summary>Hips-to-chest, medium close on a narrow lens, three-quarter front, from below looking up: the
    /// three surviving shots of the old Death Cam (39–46° off the front, 19–26° up, 1.0–1.35 m, FoV 0.57–0.65).
    /// On a body on the ground the eye cannot go below it, so the same shot ends at ground level.</summary>
    public static readonly ShotRecipe LowThreeQuarter = new(DeathShotProfile.LowThreeQuarter, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: -22f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f);

    /// <summary>The face from in front of it, 35° to a side, low: 1.0 m on the same lens.</summary>
    public static readonly ShotRecipe LowFace = new(DeathShotProfile.LowFace, ShotSubject.Head,
        Fov: 0.60f, FrameHeight: 0.62f, SafeFrame: 0.85f, Elevation: -15f * Deg,
        MinDistance: 0.7f, MaxDistance: 3f, KeepWithin: 25f * Deg);

    /// <summary>From low by the hips up along the torso: chest and head, the face behind a foreshortened torso.</summary>
    public static readonly ShotRecipe HipsToFace = new(DeathShotProfile.HipsToFace, ShotSubject.ChestToHead,
        Fov: 0.62f, FrameHeight: 0.70f, SafeFrame: 0.85f, Elevation: 0f,
        MinDistance: 0.8f, MaxDistance: 4f, KeepWithin: 50f * Deg);

    /// <summary>Low three-quarter from the other end: hips to chest from the feet, to a side.</summary>
    public static readonly ShotRecipe LowFeetSide = new(DeathShotProfile.LowFeetSide, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.90f, SafeFrame: 0.85f, Elevation: -22f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f);

    /// <summary>Hips to chest from the back side: the old Death Cam's "anchor A" (129° off the facing, 1.35 m,
    /// looking up 25°, FoV 0.65).</summary>
    public static readonly ShotRecipe LowBehind = new(DeathShotProfile.LowBehind, ShotSubject.HipsToChest,
        Fov: 0.65f, FrameHeight: 0.91f, SafeFrame: 0.85f, Elevation: -25f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f);

    /// <summary>The whole body, head to feet, in the language of Low three-quarter: low, narrow lens, from the head
    /// end 50° to a side, so the body runs diagonally across the frame (not the axial view from the soles).</summary>
    public static readonly ShotRecipe LowFullBody = new(DeathShotProfile.LowFullBody, ShotSubject.WholeBody,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.85f, Elevation: -12f * Deg,
        MinDistance: 1.2f, MaxDistance: 9f);

    /// <summary>A held-up body: chest to head, three-quarter front, camera level with it.</summary>
    public static readonly ShotRecipe GrabbedLevel = new(DeathShotProfile.GrabbedLevel, ShotSubject.ChestToHead,
        Fov: 0.62f, FrameHeight: 0.95f, SafeFrame: 0.80f, Elevation: 0f,
        MinDistance: 1.0f, MaxDistance: 4f, Low: false);

    /// <summary>A held-up body: chest to head, three-quarter front, looking down 17°.</summary>
    public static readonly ShotRecipe GrabbedAbove = new(DeathShotProfile.GrabbedAbove, ShotSubject.ChestToHead,
        Fov: 0.62f, FrameHeight: 0.95f, SafeFrame: 0.80f, Elevation: 17f * Deg,
        MinDistance: 1.0f, MaxDistance: 4f, Low: false);

    public static ShotRecipe Of(DeathShotProfile profile) => profile switch
    {
        DeathShotProfile.LowFace => LowFace,
        DeathShotProfile.HipsToFace => HipsToFace,
        DeathShotProfile.LowFeetSide => LowFeetSide,
        DeathShotProfile.LowBehind => LowBehind,
        DeathShotProfile.LowFullBody => LowFullBody,
        DeathShotProfile.GrabbedLevel => GrabbedLevel,
        DeathShotProfile.GrabbedAbove => GrabbedAbove,
        _ => LowThreeQuarter,
    };

    public static ShotRecipe Of(DeathShotGrabbedFraming framing) =>
        framing == DeathShotGrabbedFraming.SlightlyAbove ? GrabbedAbove : GrabbedLevel;

    private static readonly ShotRecipe[] AutoUpright = { LowThreeQuarter };
    private static readonly ShotRecipe[] AutoSide = { LowThreeQuarter, LowFace, LowFeetSide, LowBehind };
    private static readonly ShotRecipe[] AutoSupine = { LowThreeQuarter, LowFace, HipsToFace, LowFeetSide };
    private static readonly ShotRecipe[] AutoProne = { LowFeetSide, LowBehind, LowFullBody, LowThreeQuarter };
    private static readonly ShotRecipe[] SpareUpright = { LowBehind, LowFullBody };
    private static readonly ShotRecipe[] SpareLying = { LowFullBody, LowBehind, LowFeetSide };
    private static readonly ShotRecipe[] SpareProne = { LowFullBody };

    /// <summary>What Auto may use for a pose (it takes the one whose side is nearest the camera and not blocked, so
    /// the camera has little way to go): standing, kneeling, sitting → Low three-quarter; on a side → Low
    /// three-quarter, Low on the face, Low from the feet or Low from behind; on the back → Low three-quarter, Low on
    /// the face, Hips up to the face or Low from the feet; on the belly → Low from the feet, Low from behind, Low
    /// full body or Low three-quarter, never a shot of the face.</summary>
    public static IReadOnlyList<ShotRecipe> ForAuto(PoseClass pose) => pose switch
    {
        PoseClass.Supine => AutoSupine,
        PoseClass.Side => AutoSide,
        PoseClass.Prone => AutoProne,
        _ => AutoUpright,
    };

    /// <summary>What Auto falls back on when every side of <see cref="ForAuto"/> is blocked.</summary>
    public static IReadOnlyList<ShotRecipe> AutoSpare(PoseClass pose) => pose switch
    {
        PoseClass.Supine or PoseClass.Side => SpareLying,
        PoseClass.Prone => SpareProne,
        _ => SpareUpright,
    };

    public const float ThreeQuarterAngle = 42.5f * Deg;
    /// <summary>Low on the face: how far to a side of the face's own direction.</summary>
    public const float FaceOffAngle = 35f * Deg;
    /// <summary>Low from behind: 129° off the facing (51° off straight behind).</summary>
    public const float BehindAngle = 129f * Deg;
    /// <summary>Low from behind on a body flat on its back or belly: off the body's line, from behind the head.</summary>
    public const float BehindHeadAngle = 20f * Deg;
    /// <summary>Low full body: off the long axis, from the head end.</summary>
    public const float FullBodyAngle = 50f * Deg;
    /// <summary>Hips up to the face: the eye direction is the torso axis reversed (toward the hips), tipped this far
    /// toward the body's front and this far to a side.</summary>
    public const float HipsTip = 20f * Deg, HipsSide = 20f * Deg;
    public const float PushInAmount = 0.12f;
    public const float PushInSeconds = 8f;
}
