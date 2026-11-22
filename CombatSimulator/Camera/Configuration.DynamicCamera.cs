// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>
/// Dynamic Camera settings. Kept in their own partial so its combat framing and death shot
/// options remain separate from the always-on Active Camera controls.
/// </summary>
public partial class Configuration
{
    // ---- Master ----

    public bool EnableDynamicCamera { get; set; } = false;

    // ---- Combat framing (God of War-style over-the-shoulder) ----

    public bool DynCamCombatFraming { get; set; } = true;

    /// <summary>Combat-only adaptive pullback that keeps the full physical volume of engaged
    /// enemies inside the view. Disabled returns smoothly to the ordinary combat distance.</summary>
    public bool DynCamEnemyVolumeFitting { get; set; } = true;

    /// <summary>Bone the combat pivot orbits at range. Zooming in blends the aim up toward
    /// the head on its own, so this only sets where the shot sits when pulled back.</summary>
    [Newtonsoft.Json.JsonIgnore] public string DynCamPivotBoneName => "j_sebo_c";

    /// <summary>Fraction of screen HEIGHT the player's body should occupy. Drives the
    /// target orbit distance.</summary>
    public float DynCamSubjectScreenShare { get; set; } = 0.55f;

    /// <summary>Over-the-shoulder offset, as a fraction of screen WIDTH. A screen fraction
    /// rather than a world distance: the world offset needed to shift the character a given
    /// amount on screen scales with camera distance, so a fixed yalm offset that looks right
    /// at range shoves the character out of frame the moment you zoom in.</summary>
    public float DynCamShoulderScreenFrac { get; set; } = 0.15f;

    /// <summary>0 = auto (opposite the focused enemy), 1 = always left, 2 = always right.</summary>
    public int DynCamShoulderSide { get; set; } = 0;

    /// <summary>How hard to pull back when several enemies span a wide arc. 0 disables.
    /// This is the fix for the "claustrophobic in a crowd" complaint the real thing has.</summary>
    public float DynCamCrowdingRelief { get; set; } = 0.5f;

    [Newtonsoft.Json.JsonIgnore] public float DynCamHeightOffset => 0.1f;
    public float DynCamPivotSmoothing { get; set; } = 6.0f;
    public float DynCamDistanceSmoothing { get; set; } = 3.0f;

    /// <summary>Seconds of hands-off after the player touches the camera before the
    /// framing resumes adapting. Never a lock — only a pause on OUR adjustments.</summary>
    [Newtonsoft.Json.JsonIgnore] public float DynCamInputHold => 2.0f;

    /// <summary>The combat zoom the player last settled on, as a multiple of the framing
    /// distance. Learned from the scroll wheel and persisted so a new fight (or re-enabling
    /// the feature) starts at the zoom they chose, not at ours. Combat framing only.</summary>
    public float DynCamCombatZoomMemory { get; set; } = 1.0f;

    /// <summary>The camera pitch (game DirV) the player last held in combat, persisted so it
    /// survives a death and is restored when combat framing resumes — the same memory the
    /// combat zoom already has, now for the angle.</summary>
    public float DynCamCombatPitchMemory { get; set; } = 0f;
    public bool DynCamCombatPitchMemoryValid { get; set; } = false;

    // ---- Death: the automatic death shot (docs/DEATH_SHOTS.md) ----

    /// <summary>The Directed camera directs deaths too ("Directs": Combat and death / Death only).</summary>
    public bool DynCamDeathFraming { get; set; } = true;

    /// <summary>The profile of the automatic death shot; Auto picks one by the body's pose. The old death framing's
    /// own settings (coverage, band, angle, ignore-killer) are retired: everyone starts on Auto.</summary>
    public Camera.DeathShots.DeathShotProfile DeathShotProfile { get; set; } = Camera.DeathShots.DeathShotProfile.Auto;

    /// <summary>"Allow below ground": Low three-quarter (and Face, where it sits slightly low) keep their from-below
    /// placement on a body on the ground, with the eye under it looking up through the one-sided ground — the look
    /// of the old Death Cam with camera collision off. Off: the eye never goes under the ground.</summary>
    public bool DeathShotBelowGround { get; set; } = false;

    /// <summary>Restore every Dynamic Camera option to its shipped default, leaving the master
    /// switch alone (resetting it would switch the feature off and take the page with it,
    /// which reads as a break rather than a restore).</summary>
    public void ResetDynamicCameraDefaults()
    {
        var d = new Configuration();

        DynCamCombatFraming = d.DynCamCombatFraming;
        DynCamEnemyVolumeFitting = d.DynCamEnemyVolumeFitting;
        DynCamSubjectScreenShare = d.DynCamSubjectScreenShare;
        DynCamShoulderScreenFrac = d.DynCamShoulderScreenFrac;
        DynCamShoulderSide = d.DynCamShoulderSide;
        DynCamCrowdingRelief = d.DynCamCrowdingRelief;
        DynCamPivotSmoothing = d.DynCamPivotSmoothing;
        DynCamDistanceSmoothing = d.DynCamDistanceSmoothing;
        DynCamCombatZoomMemory = d.DynCamCombatZoomMemory;
        DynCamCombatPitchMemory = d.DynCamCombatPitchMemory;
        DynCamCombatPitchMemoryValid = d.DynCamCombatPitchMemoryValid;

        DynCamDeathFraming = d.DynCamDeathFraming;
        DeathShotProfile = d.DeathShotProfile;
        DeathShotBelowGround = d.DeathShotBelowGround;

        Save();
    }
}
