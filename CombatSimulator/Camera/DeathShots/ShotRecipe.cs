// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>
/// The death-shot dropdown (shots told apart by subject, distance, height and vertical placement only: the horizontal direction is
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
    /// <summary>Hips to chest, close, the camera level with them.</summary>
    LevelClose = 15,
    /// <summary>Hips to chest, close, from above.</summary>
    HighClose = 16,
    /// <summary>The whole body from high above (not straight down).</summary>
    HighFullBody = 17,
    /// <summary>Hips to chest, low, the body's lowest point on the frame's bottom edge.</summary>
    GroundLineClose = 18,
    /// <summary>The whole body, low, its lowest point on the frame's bottom edge, the surroundings above.</summary>
    GroundLineFullBody = 19,
    /// <summary>A held-up body, camera level with it (not in the dropdown: "When grabbed").</summary>
    GrabbedLevel = 20,
    /// <summary>A held-up body, from slightly above.</summary>
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
        DeathShotProfile.LevelClose, DeathShotProfile.HighClose, DeathShotProfile.HighFullBody,
        DeathShotProfile.GroundLineClose, DeathShotProfile.GroundLineFullBody,
    };

    public static readonly string[] ChoiceLabels =
    {
        "Low close", "Low on the face", "Low full body", "Level close", "High close", "High full body", "Ground line close", "Ground line full body",
    };

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
    /// <summary>A body that is held up: sized on chest and head, aimed so the head is near the top of the frame.</summary>
    HeldBody,
    /// <summary>Every body point given, head top to feet.</summary>
    WholeBody,
}

/// <summary>What the frame's vertical placement is solved from (the distance and the orbit are the subject's either way).</summary>
public enum ShotAnchor
{
    /// <summary>The subject's centre in the middle of the frame.</summary>
    Centre,
    /// <summary>The top of the head at <see cref="ShotRecipe.AnchorAt"/>.</summary>
    HeadTop,
    /// <summary>The subject's lowest point in the frame at <see cref="ShotRecipe.AnchorAt"/>.</summary>
    Lowest,
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
    bool Low = true,
    /// <summary>What sets the frame's vertical placement: the subject's centre in the middle, or one point at
    /// <see cref="AnchorAt"/>.</summary>
    ShotAnchor Anchor = ShotAnchor.Centre,
    /// <summary>Where the anchor sits up the frame: 0 = the bottom edge, 1 = the top edge.</summary>
    float AnchorAt = 0.5f,
    /// <summary>With <see cref="ShotAnchor.Lowest"/>: the part of the frame's height the subject may take (0 = <see cref="SafeFrame"/>).</summary>
    float AnchorSpan = 0f)
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

    /// <summary>Hips to chest at Low close's distance, the camera level with the subject. Not a low shot: "Allow below
    /// ground" does not apply.</summary>
    public static readonly ShotRecipe LevelClose = new(DeathShotProfile.LevelClose, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: 0f,
        MinDistance: 0.8f, MaxDistance: 4f, Low: false);

    /// <summary>Hips to chest at the same distance, looking down 35°.</summary>
    public static readonly ShotRecipe HighClose = new(DeathShotProfile.HighClose, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: 35f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f, Low: false);

    /// <summary>The whole body looking down 50° (not straight down: the frame keeps the world's up), as far as it
    /// takes to keep all of it in the frame.</summary>
    public static readonly ShotRecipe HighFullBody = new(DeathShotProfile.HighFullBody, ShotSubject.WholeBody,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.85f, Elevation: 50f * Deg,
        MinDistance: 1.2f, MaxDistance: 9f, Low: false);

    /// <summary>Hips to chest at Low close's distance, low (looking up 8°), the frame raised so the subject's lowest
    /// point sits on the bottom edge (4% above it) and the rest of the frame shows what is above. The eye stays above
    /// the ground ("Allow below ground" does not apply: under the ground the bottom edge would be the ground's underside).</summary>
    public static readonly ShotRecipe GroundLineClose = new(DeathShotProfile.GroundLineClose, ShotSubject.HipsToChest,
        Fov: 0.62f, FrameHeight: 0.80f, SafeFrame: 0.85f, Elevation: -8f * Deg,
        MinDistance: 0.8f, MaxDistance: 4f, Low: false, Anchor: ShotAnchor.Lowest, AnchorAt: 0.04f, AnchorSpan: 0.70f);

    /// <summary>The whole body, low (looking up 6°), its lowest point on the bottom edge (4% above it), the body in the
    /// lower 60% of the frame.</summary>
    public static readonly ShotRecipe GroundLineFullBody = new(DeathShotProfile.GroundLineFullBody, ShotSubject.WholeBody,
        Fov: 0.62f, FrameHeight: 0f, SafeFrame: 0.85f, Elevation: -6f * Deg,
        MinDistance: 1.2f, MaxDistance: 9f, Low: false, Anchor: ShotAnchor.Lowest, AnchorAt: 0.04f, AnchorSpan: 0.60f);

    /// <summary>A held-up body, camera level with it: the distance of the upper body (chest to head, frame 0.95 m
    /// high), aimed down so the top of the head sits <see cref="HeldHeadroom"/> below the frame's top edge and the
    /// frame runs on down the body as far as that distance shows.</summary>
    public static readonly ShotRecipe GrabbedLevel = new(DeathShotProfile.GrabbedLevel, ShotSubject.HeldBody,
        Fov: 0.62f, FrameHeight: 0.95f, SafeFrame: 0.80f, Elevation: 0f,
        MinDistance: 1.0f, MaxDistance: 4f, Low: false, Anchor: ShotAnchor.HeadTop, AnchorAt: 1f - HeldHeadroom);

    /// <summary>The same, looking down 17°.</summary>
    public static readonly ShotRecipe GrabbedAbove = new(DeathShotProfile.GrabbedAbove, ShotSubject.HeldBody,
        Fov: 0.62f, FrameHeight: 0.95f, SafeFrame: 0.80f, Elevation: 17f * Deg,
        MinDistance: 1.0f, MaxDistance: 4f, Low: false, Anchor: ShotAnchor.HeadTop, AnchorAt: 1f - HeldHeadroom);

    /// <summary>A held body: the room above the top of the head, as a fraction of the frame's height.</summary>
    public const float HeldHeadroom = 0.09f;

    public static ShotRecipe Of(DeathShotProfile profile) => profile switch
    {
        DeathShotProfile.LowFace => LowFace,
        DeathShotProfile.LowFullBody => LowFullBody,
        DeathShotProfile.LevelClose => LevelClose,
        DeathShotProfile.HighClose => HighClose,
        DeathShotProfile.HighFullBody => HighFullBody,
        DeathShotProfile.GroundLineClose => GroundLineClose,
        DeathShotProfile.GroundLineFullBody => GroundLineFullBody,
        DeathShotProfile.GrabbedLevel => GrabbedLevel,
        DeathShotProfile.GrabbedAbove => GrabbedAbove,
        _ => LowClose,
    };

    public static ShotRecipe Of(DeathShotGrabbedFraming framing) =>
        framing == DeathShotGrabbedFraming.SlightlyAbove ? GrabbedAbove : GrabbedLevel;

    public const float PushInAmount = 0.12f;
    public const float PushInSeconds = 8f;
}
