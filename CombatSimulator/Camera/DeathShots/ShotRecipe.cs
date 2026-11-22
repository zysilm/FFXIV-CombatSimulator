// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>
/// The death-shot dropdown (three shots, told apart by subject, distance and height only: the horizontal direction is
/// the player's), and the two framings of a body that is held up. The numbers are what is saved. The values no longer
/// in the dropdown keep theirs unused, so a saved choice of one of them is not read as something else: Auto 0 (dropped
/// with the automatic side choice: there is nothing left for it to decide), Face 2, Along the body 3, Overhead 4,
/// Ground profile 5, and the three that differed from the others only by direction: Hips up to the face 11, Low from
/// the feet 12, Low from behind 13 (<see cref="DeathShotProfiles.Migrate"/>).
/// </summary>
public enum DeathShotProfile
{
    /// <summary>Hips to chest, close, from below looking up (the former "Low three-quarter").</summary>
    LowClose = 1,
    /// <summary>The head, close, low.</summary>
    LowFace = 10,
    /// <summary>The whole body, farther, low.</summary>
    LowFullBody = 14,
    /// <summary>A held-up body, head to mid-thigh, camera level with it (not in the dropdown: "When grabbed").</summary>
    GrabbedLevel = 20,
    /// <summary>A held-up body, head to mid-thigh, from slightly above.</summary>
    GrabbedAbove = 21,
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
        DeathShotProfile.LowClose, DeathShotProfile.LowFace, DeathShotProfile.LowFullBody,
    };

    public static readonly string[] ChoiceLabels = { "Low close", "Low on the face", "Low full body" };

    public static readonly string[] GrabbedLabels = { "Level", "Slightly above" };

    /// <summary>Whether a saved value is one of the dropdown's choices (a removed shot, or anything else, is not).</summary>
    public static bool IsChoice(DeathShotProfile profile) => Array.IndexOf(Choices, profile) >= 0;

    /// <summary>
    /// A saved value → a choice of the dropdown. The removed shots go to the one with their subject: Hips up to the
    /// face (11, chest and head) → Low on the face; Low from the feet (12) and Low from behind (13), both hips to
    /// chest → Low close; Auto (0) and everything else → Low close.
    /// </summary>
    public static DeathShotProfile Migrate(DeathShotProfile saved) => IsChoice(saved) ? saved
        : (int)saved == 11 ? DeathShotProfile.LowFace
        : DeathShotProfile.LowClose;

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
    /// <summary>The top of the head down to the middle of the thighs: a body that is held up.</summary>
    HeadToThigh,
    /// <summary>Every body point given, head top to feet.</summary>
    WholeBody,
}

/// <summary>
/// One shot = subject + size + height. Lengths are for the reference body (a midlander); the solver scales them by
/// the body's size. Angles in radians. Every shot is a free orbit about its subject: the azimuth is the player's, the
/// elevation and the distance are the shot's; it pushes in slowly once the body rests.
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
    /// <summary>Elevation of the eye about the look-at point: positive = above it, looking down.</summary>
    float Elevation,
    float MinDistance,
    float MaxDistance,
    /// <summary>The eye sits below the subject looking up: with "Allow below ground" it may go under the ground.</summary>
    bool Low = true)
{
    public const float Deg = MathF.PI / 180f;

    /// <summary>Hips to chest, medium close on a narrow lens, from below looking up 22°: the three surviving shots of
    /// the old Death Cam (19–26° up, 1.0–1.35 m, FoV 0.57–0.65). On a body on the ground the eye cannot go below it,
    /// so the same shot ends at ground level (unless "Allow below ground").</summary>
    public static readonly ShotRecipe LowClose = new(DeathShotProfile.LowClose, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: -22f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f);

    /// <summary>The head, low (looking up 15°): 1.0 m on the same lens.</summary>
    public static readonly ShotRecipe LowFace = new(DeathShotProfile.LowFace, ShotSubject.Head,
        Fov: 0.60f, FrameHeight: 0.62f, SafeFrame: 0.85f, Elevation: -15f * Deg,
        MinDistance: 0.7f, MaxDistance: 3f);

    /// <summary>The whole body, head to feet, low (looking up 12°) on the same narrow lens, as far as it takes to
    /// keep all of it in the frame from where the player has put the camera.</summary>
    public static readonly ShotRecipe LowFullBody = new(DeathShotProfile.LowFullBody, ShotSubject.WholeBody,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.85f, Elevation: -12f * Deg,
        MinDistance: 1.2f, MaxDistance: 9f);

    /// <summary>A held-up body: the top of the head to mid-thigh, the aim point between the two (lower chest /
    /// belly), camera level with it. The distance is what that extent takes, so the head has the frame's ordinary
    /// margin above it (<see cref="SafeFrame"/>: 7% of the frame's height) and no more.</summary>
    public static readonly ShotRecipe GrabbedLevel = new(DeathShotProfile.GrabbedLevel, ShotSubject.HeadToThigh,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.86f, Elevation: 0f,
        MinDistance: 1.2f, MaxDistance: 5f, Low: false);

    /// <summary>The same, looking down 17°.</summary>
    public static readonly ShotRecipe GrabbedAbove = new(DeathShotProfile.GrabbedAbove, ShotSubject.HeadToThigh,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.86f, Elevation: 17f * Deg,
        MinDistance: 1.2f, MaxDistance: 5f, Low: false);

    public static ShotRecipe Of(DeathShotProfile profile) => profile switch
    {
        DeathShotProfile.LowFace => LowFace,
        DeathShotProfile.LowFullBody => LowFullBody,
        DeathShotProfile.GrabbedLevel => GrabbedLevel,
        DeathShotProfile.GrabbedAbove => GrabbedAbove,
        _ => LowClose,
    };

    public static ShotRecipe Of(DeathShotGrabbedFraming framing) =>
        framing == DeathShotGrabbedFraming.SlightlyAbove ? GrabbedAbove : GrabbedLevel;

    public const float PushInAmount = 0.12f;
    public const float PushInSeconds = 8f;
}
