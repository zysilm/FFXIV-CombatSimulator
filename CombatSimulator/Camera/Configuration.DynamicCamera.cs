namespace CombatSimulator;

/// <summary>
/// Dynamic Camera settings. Kept in their own partial (and their own GUI page) so the
/// three camera features — Active Cam, Death Cam, Dynamic Cam — stay independently
/// tunable instead of collapsing into one wall of sliders.
/// </summary>
public partial class Configuration
{
    // ---- Master ----

    public bool EnableDynamicCamera { get; set; } = false;
    public bool ShowDynamicCamToolbar { get; set; } = false;
    public bool DynCamDebugOverlay { get; set; } = false;

    // ---- Combat framing (God of War-style over-the-shoulder) ----

    public bool DynCamCombatFraming { get; set; } = true;

    /// <summary>Bone the combat pivot orbits at range. Zooming in blends the aim up toward
    /// the head on its own, so this only sets where the shot sits when pulled back.</summary>
    public string DynCamPivotBoneName { get; set; } = "j_sebo_c";

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

    public float DynCamHeightOffset { get; set; } = 0.1f;
    public float DynCamPivotSmoothing { get; set; } = 6.0f;
    public float DynCamDistanceSmoothing { get; set; } = 3.0f;

    /// <summary>Seconds of hands-off after the player touches the camera before the
    /// framing resumes adapting. Never a lock — only a pause on OUR adjustments.</summary>
    public float DynCamInputHold { get; set; } = 2.0f;

    // ---- Death framing (prone-photographer shot) ----

    public bool DynCamDeathFraming { get; set; } = true;

    /// <summary>How much of the corpse must stay in frame: 1.0 = head to feet,
    /// 0.5 ≈ half the body, 0.25 = head and chest only.</summary>
    public float DynCamDeathBodyVisibility { get; set; } = 1.0f;

    /// <summary>Where the body sits on screen, in NDC (−1 = bottom edge, 0 = centre). The
    /// camera HEIGHT is derived from this each frame — placing the body low is the whole
    /// fallen-hero look, and solving the height from it beats hand-tuning a height that only
    /// works for one angle. Testing bore this out: a manually-dialled height left the body
    /// dead-centre and the shot feeling like a portrait, not a knockdown.</summary>
    public float DynCamDeathBodyBand { get; set; } = -0.45f;

    /// <summary>Camera angle in radians. POSITIVE tips the lens down (the photographer
    /// raises up on their elbows); NEGATIVE tips it up (flat on the ground, looking up past
    /// the body at the killer standing over it). Tilting further down needs more height and
    /// distance to still hold the killer in frame — the fit does that for you.</summary>
    public float DynCamDeathAngle { get; set; } = -0.05f;

    /// <summary>How close the shot wants to sit to the body, in yalms. The fit only backs
    /// away from this when the killer will not otherwise fit.</summary>
    public float DynCamDeathCloseUpDistance { get; set; } = 1.5f;

    public float DynCamDeathTranslateDuration { get; set; } = 2.0f;

    public bool DynCamDeathDisableCollision { get; set; } = true;

    // The safe margin, lens band, give-up distance and zoom headroom used to be sliders here.
    // They are internal solver bounds now (constants on DynamicCameraController): in testing
    // they either did nothing a player could see or, worse, could be mis-set in ways that
    // silently broke the shot (an inverted lens range pinned the whole composition to a
    // super-wide 1.2 rad and shrank everyone). What the shot LOOKS like is fully expressed by
    // the sliders that remain — body coverage, body position, angle, close-up, duration.

    /// <summary>One-shot: the first build framed combat on the mid spine, which put the aim at
    /// the waist once you zoomed in. The shot now blends up toward the head as it closes, and
    /// the resting anchor moved to the chest — but a config saved by that build has the old
    /// bone persisted, so the corrected default would never reach it.</summary>
    public bool MigratedDynCamPivotBone { get; set; }

    private void MigrateDynamicCameraPivotBone()
    {
        if (MigratedDynCamPivotBone)
            return;

        MigratedDynCamPivotBone = true;
        if (DynCamPivotBoneName == "j_sebo_b")
            DynCamPivotBoneName = "j_sebo_c";
        Save();
    }

    /// <summary>Restore every Dynamic Camera option to its shipped default, leaving the master
    /// switch alone (resetting it would switch the feature off and take the page with it,
    /// which reads as a break rather than a restore).</summary>
    public void ResetDynamicCameraDefaults()
    {
        var d = new Configuration();

        DynCamCombatFraming = d.DynCamCombatFraming;
        DynCamPivotBoneName = d.DynCamPivotBoneName;
        DynCamSubjectScreenShare = d.DynCamSubjectScreenShare;
        DynCamShoulderScreenFrac = d.DynCamShoulderScreenFrac;
        DynCamShoulderSide = d.DynCamShoulderSide;
        DynCamCrowdingRelief = d.DynCamCrowdingRelief;
        DynCamHeightOffset = d.DynCamHeightOffset;
        DynCamPivotSmoothing = d.DynCamPivotSmoothing;
        DynCamDistanceSmoothing = d.DynCamDistanceSmoothing;
        DynCamInputHold = d.DynCamInputHold;

        DynCamDeathFraming = d.DynCamDeathFraming;
        DynCamDeathBodyVisibility = d.DynCamDeathBodyVisibility;
        DynCamDeathBodyBand = d.DynCamDeathBodyBand;
        DynCamDeathAngle = d.DynCamDeathAngle;
        DynCamDeathCloseUpDistance = d.DynCamDeathCloseUpDistance;
        DynCamDeathTranslateDuration = d.DynCamDeathTranslateDuration;
        DynCamDeathDisableCollision = d.DynCamDeathDisableCollision;

        DynCamDebugOverlay = d.DynCamDebugOverlay;

        Save();
    }
}
