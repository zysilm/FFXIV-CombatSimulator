// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>Duel camera settings (docs/DUEL_CAMERA.md): the mode itself, its one framing option, and the automatic
/// death shot's options (docs/DEATH_SHOTS.md), which runs in this mode only.</summary>
public partial class Configuration
{
    /// <summary>Camera mode "Duel": a two-shot of the player and one opponent while a simulation runs. Exclusive with
    /// Follow bone (<see cref="EnableActiveCamera"/>) and Directed (<see cref="EnableDynamicCamera"/>); the Camera
    /// tab's mode dropdown keeps at most one of the three on.</summary>
    public bool EnableDuelCamera { get; set; } = false;

    /// <summary>How close the two-shot sits.</summary>
    public Camera.Duel.DuelFraming DuelCameraFraming { get; set; } = Camera.Duel.DuelFraming.Standard;

    /// <summary>"Duel death shot": the profile of the automatic death shot; Auto picks one by the body's pose.
    /// (Named apart from the Directed camera's own "Death shot" option, which is its death framing's angle.)</summary>
    public Camera.DeathShots.DeathShotProfile DuelDeathShot { get; set; } = Camera.DeathShots.DeathShotProfile.Auto;

    /// <summary>"Allow below ground": the low shots keep their from-below placement on a body on the ground, with the
    /// eye under it looking up through the one-sided ground — the look of the old Death Cam with camera collision
    /// off. Off: the eye never goes under the ground.</summary>
    public bool DuelDeathShotBelowGround { get; set; } = false;

    /// <summary>"When grabbed": how a body that is held up (Enemy Control's grab, a corpse being carried off) is
    /// framed.</summary>
    public Camera.DeathShots.DeathShotGrabbedFraming DuelDeathShotGrabbed { get; set; } = Camera.DeathShots.DeathShotGrabbedFraming.Level;

    /// <summary>"Side preference": which of a shot's sides the camera takes when the shot has more than one — the
    /// nearest to the camera (as before), the side the face points to, the side behind the head, or the body's own
    /// left or right.</summary>
    public Camera.DeathShots.DeathShotSide DuelDeathShotSide { get; set; } = Camera.DeathShots.DeathShotSide.Nearest;
}
