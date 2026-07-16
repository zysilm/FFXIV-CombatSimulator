using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Animation;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.GamePad;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CombatSimulator;

[Serializable]
public class RagdollBoneConfig
{
    public string Name { get; set; } = "";
    public string? SkeletonParent { get; set; }  // real skeleton parent (for computing physics parent chain)
    public bool Enabled { get; set; } = true;     // whether this bone participates in physics
    public float CapsuleRadius { get; set; }
    public float CapsuleHalfLength { get; set; }
    public float Mass { get; set; }
    public float SwingLimit { get; set; }
    public float? SwingMinLimit { get; set; } // hinge-only lower bound; null means migrate/default
    public float? HingeRestAngle { get; set; } // hinge-only passive rest target; null disables/defaults
    public float? HingeRestSpringFreq { get; set; }
    public float? HingeRestMaxForce { get; set; }
    public int JointType { get; set; } // 0=Ball, 1=Hinge
    public float TwistMinAngle { get; set; }
    public float TwistMaxAngle { get; set; }
    public int AnatomicalRole { get; set; } // 0=Generic, see RagdollController.AnatomicalRole
    public int ColliderShape { get; set; } // 0=Capsule, 1=Box
    public float BoxHalfExtentX { get; set; }
    public float BoxHalfExtentY { get; set; }
    public float BoxHalfExtentZ { get; set; }
    public string? Description { get; set; }      // human-readable label for UI
    // Soft body spring settings (for breast/jiggle bones)
    public bool SoftBody { get; set; }             // use soft springs + AngularServo instead of rigid + AngularMotor
    public float SoftSpringFreq { get; set; } = 6f;    // BallSocket spring frequency (Hz), lower = bouncier
    public float SoftSpringDamp { get; set; } = 0.4f;  // BallSocket damping ratio, lower = more oscillation
    public float SoftServoFreq { get; set; } = 4f;     // AngularServo spring frequency (Hz), controls return speed
    public float SoftServoDamp { get; set; } = 0.35f;  // AngularServo damping ratio, controls bounce on return
}

[Serializable]
public class RagdollBoneProfile
{
    public string Name { get; set; } = "";
    public List<RagdollBoneConfig> Bones { get; set; } = new();
}

[Serializable]
public class GuidedCollapseSettings
{
    public bool Enabled { get; set; } = false;
    public int Mode { get; set; } = 1; // 0=Relaxation, 1=KneePowerLoss
    public GuidedCollapseRelaxationSettings Relaxation { get; set; } = new();
    public GuidedCollapseKneePowerLossSettings KneePowerLoss { get; set; } = new();

    public void ResetDefaults()
    {
        var defaults = new GuidedCollapseSettings();
        Enabled = defaults.Enabled;
        Mode = defaults.Mode;
        Relaxation = defaults.Relaxation;
        KneePowerLoss = defaults.KneePowerLoss;
    }
}

[Serializable]
public class GuidedCollapseRelaxationSettings
{
    public int Archetype { get; set; } = 1;        // 0=StiffHold, 1=UniformCollapse
    public float Strength { get; set; } = 14f;
    public float Hold { get; set; } = 0.3f;
    public float Fade { get; set; } = 0.9f;
    public float HingeSoften { get; set; } = 0.25f;
    public int Direction { get; set; } = 1;        // 0=None,1=Random,2=Forward,3=Backward,4=Sideways
    public float Impulse { get; set; } = 2.0f;
    // Eccentric braking: after the hold fades, joints keep a velocity-resisting brake (the
    // muscle "pays out" under load) instead of going slack instantly, so the body sinks →
    // gets braked → sinks rather than free-falling to limp. BrakeStrength = residual torque
    // ceiling as a fraction of full (0 = old instant-limp behavior, ~0.3 = noticeable brake);
    // BrakeFade = seconds the brake decays over after the hold is gone.
    public float BrakeStrength { get; set; } = 0.3f;
    public float BrakeFade { get; set; } = 0.7f;
}

[Serializable]
public class GuidedCollapseKneePowerLossSettings
{
    public float EntryStrength { get; set; } = 0.65f;
    public float KneeYield { get; set; } = 0.55f;
    public float FootGrip { get; set; } = 0.65f;
    public float ForwardCommitment { get; set; } = 0.55f;
    public float ReleaseTiming { get; set; } = 0.55f;
    public bool EntryConditioningEnabled { get; set; } = true;
    public float EntryStanceThreshold { get; set; } = 0.28f;
    public float EntryReadyStance { get; set; } = 0.30f;
    public float EntryReadyKneeAngle { get; set; } = 10f;
    public float EntryMinDuration { get; set; } = 0.24f;
    public float EntryMaxDuration { get; set; } = 0.42f;
    public float EntryTargetStanceStart { get; set; } = 0.34f;
    public float EntryTargetStanceEnd { get; set; } = 0.50f;
    public float EntryPelvisDownStart { get; set; } = 0.32f;
    public float EntryPelvisDownEnd { get; set; } = 0.60f;
    public float KneeFlexDegrees { get; set; } = 34f;
    // Knee-flex torques act on the lower-leg inertia, which Tier D's anthropometric masses
    // cut to ~half (shin 3->1.8, calf 1->0.18 kg). Scaled down ~x0.55 so the knee buckles at
    // the same rate instead of over-driving on the now-lighter leg (was 82 / 42).
    public float KneeBuckleFlexForce { get; set; } = 46f;
    public float KneeTorsoFlexForce { get; set; } = 24f;
    // Foot supports are positional pins that anchor the WHOLE body's pivot over the planted
    // foot; body mass is unchanged (~70 kg), so these stay as-is (lowering them slips the foot).
    public float BuckleFootSupportForce { get; set; } = 1100f;
    public float TorsoFootSupportForce { get; set; } = 650f;
    public bool FootProxyEnabled { get; set; } = true;
    public float FootProxyForwardOffset { get; set; } = 0.10f;
    public float FootProxyDownOffset { get; set; } = 0.035f;
    public float FootProxyGroundClearance { get; set; } = 0.018f;
    // Pelvis-drive torques act on the trunk, which Tier D made HEAVIER (pelvis 8->9.9,
    // mid-spine 5->7.7 kg). Scaled up ~x1.24 so the torso still pitches forward in step with
    // the buckling legs instead of lagging (was 420 / 220).
    public float BucklePelvisForce { get; set; } = 520f;
    public float TorsoPelvisForce { get; set; } = 275f;
    public float ChestPitchDegrees { get; set; } = 41f;
    public bool UseSemanticControls { get; set; } = false;
    public float BuckleMinDuration { get; set; } = 0.24f;
    public float BuckleTimeout { get; set; } = 0.95f;
    public float BucklePelvisDropToTorso { get; set; } = 0.30f;
    public float BuckleKneeAngleToTorso { get; set; } = 22f;
    public float TorsoMinDuration { get; set; } = 0.55f;
    public float TorsoTimeout { get; set; } = 0.90f;
}

[Serializable]
public class DeathCamPreset
{
    public string Name { get; set; } = "";
    public string BoneName { get; set; } = "n_hara";
    public float DirH { get; set; } = 0;
    public float DirV { get; set; } = 0;
    public float Distance { get; set; } = 5.0f;
    public float FoV { get; set; } = 0.78f;
    public float HeightOffset { get; set; } = 0f;
    public float SideOffset { get; set; } = 0f;
    public float Tilt { get; set; } = 0f;
    public bool DisableCollision { get; set; } = true;
    public float TransitionDuration { get; set; } = 1.5f;
}

[Serializable]
public class RecentNpcEntry
{
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
}

public enum RagdollNpcCollisionMode
{
    BoneCapsule = 0,
    ConvexHull = 1,
    Mesh = 2,
    AnimatedMesh = 3,
}

[Serializable]
public partial class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Preserve private/dev-only config fields when this object is loaded and saved by a
    // public build that does not compile the experimental partial Configuration.
    [JsonExtensionData]
    public IDictionary<string, JToken>? PrivateConfigFields { get; set; }

    public bool ActionGuardDefaultMigratedToR2 { get; set; } = false;
    public bool ActionGuardVfxDefaultMigratedToHeavySwing { get; set; } = false;
    public bool ActionBasicAttackDefaultMigratedToSouth { get; set; } = false;
    public bool ActionMeleeRangeDefaultMigratedTo5 { get; set; } = false;
    public bool ActionAttackShapeDefaultsMigrated20260630 { get; set; } = false;
    public bool EnemyDismembermentDefaultsMigrated20260630 { get; set; } = false;
    public bool FightingSpacingDefaultsMigrated20260703 { get; set; } = false;
    public bool ThinnerProfileTuningMigrated20260704 { get; set; } = false;
    public bool KneeHingeStiffnessMigrated20260704 { get; set; } = false;
    public bool AnatomicalDefaultsOffMigrated20260704 { get; set; } = false;
    public bool AnatomicalHingeSignFixMigrated { get; set; } = false;
    public bool NpcCollisionMeshDefaultMigrated { get; set; } = false;
    public bool NpcCollisionModeMigrated20260705 { get; set; } = false;
    public bool RagdollShinBoxHalfYMigrated20260716 { get; set; } = false;
    public bool RagdollDefaultProfileTuningMigrated20260716 { get; set; } = false;
    public bool NpcCollisionConvexHullDefaultMigrated20260716 { get; set; } = false;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public float SidebarWidth { get; set; } = 130f;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;
    public bool ShowPlayerHpBar { get; set; } = true;
    public bool ShowHudPlayerHpBar { get; set; } = false;
    public bool AnonymousMode { get; set; } = false;

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // Party companions: client-side clones of visible players that fight
    // simulated enemies as friendly actors.
    public bool EnableCombatCompanions { get; set; } = true;
    public int CombatCompanionMaxCount { get; set; } = 10;
    public bool SensePartyMembers { get; set; } = true;
    // When true, resetting combat keeps existing companions (revived + healed)
    // instead of despawning them. Stopping the simulation always despawns.
    public bool KeepCompanionsOnReset { get; set; } = true;
    public float PartyCommandRange { get; set; } = 8.0f;
    public float PartyCommandRangeRandomness { get; set; } = 0.2f;
    public float PartyMeleeAttackRange { get; set; } = 1.5f;
    public float PartyRangedAttackRange { get; set; } = 9.5f;
    public bool PartyCompanionDeathRagdoll { get; set; } = true;
    public bool PartyEnemyDeathRagdoll { get; set; } = true; // Legacy: merged into EnableNpcDeathRagdoll.

    // NPC Defaults
    public int DefaultNpcLevel { get; set; } = 90;
    public float DefaultNpcHpMultiplier { get; set; } = 1.0f;
    public float DefaultNpcHeightOffset { get; set; } = 0f; // Y offset added to spawn position

    // Default behavior for auto-selected NPCs (0=Dummy, 1=BasicMelee, 2=BasicRanged, 3=Boss)
    public int DefaultNpcBehaviorType { get; set; } = 1;

    // Glamourer: apply a preset on player death
    public bool ApplyGlamourerOnDeath { get; set; } = false;
    public string DeathGlamourerDesignId { get; set; } = "";

    // Glamourer: apply a preset on start / reset / reboot (combat ready look)
    public bool ApplyGlamourerOnReset { get; set; } = false;
    public string ResetGlamourerDesignId { get; set; } = "";

    // Glamourer: equipment-only design applied to party companion clones on spawn.
    // "" = None — the clone keeps its own (inherited) equipment. Also the effective
    // behavior when Glamourer is not installed.
    public string PartyCompanionGlamourerDesignId { get; set; } = "";

    // Armor Detachment: compact floating control UI, opened from Professional Mode > Effects.
    public bool ShowArmorDetachmentControls { get; set; } = false;

    // Armor Detachment: visually detach configured player gear slots on KO. Monster-hit detachment is
    // a dev-only extension and is additionally runtime-gated by the dev unlock state.
    public bool KoStripEnabled { get; set; } = false;
    public bool KoStripOnHitEnabled { get; set; } = false;
    public bool KoStripSyncWithRagdoll { get; set; } = true;
    public bool KoStripHead { get; set; } = false;
    public bool KoStripBody { get; set; } = true;
    public bool KoStripHands { get; set; } = true;
    public bool KoStripLegs { get; set; } = true;
    public bool KoStripFeet { get; set; } = true;
    public bool KoStripEars { get; set; } = false;
    public bool KoStripNeck { get; set; } = false;
    public bool KoStripWrists { get; set; } = false;
    public bool KoStripRFinger { get; set; } = false;
    public bool KoStripLFinger { get; set; } = false;

    // Physically drop hats / accessories (separate models, not fused with skin) as falling rigid
    // bodies instead of just hiding them. Head + accessory slots. Default off.
    public bool KoStripPhysicsDrop { get; set; } = false;

    // Physically drop supported clothing (Body / Legs) as falling shells. Still includes the body skin
    // baked into those equipment models, so it remains opt-in. Default off.
    public bool KoStripPhysicsDropClothing { get; set; } = false;

    // Garment polish layered on top of clothing physics drop: short visual body follow, body/ground
    // friction damping, and delayed cloth collapse. Default off.
    public bool KoStripAdvancedClothPhysics { get; set; } = false;

    // Experimental: drive the upper garment (Body slot) with a ring-tube physics model instead of the
    // chain-of-boxes rig. The tube wraps the corpse capsules, so the shirt slides down off the body
    // instead of folding. Host ragdoll only; falls back to the chain rig when unavailable. Default off.
    public bool KoStripGarmentTubeModel { get; set; } = false;

    // Draw the garment tube's ring bodies as a wireframe overlay (tuning aid). Not saved-critical.
    public bool KoStripGarmentTubeDebugDraw { get; set; } = false;

    // Give a coat's j_sk_* columns real bodies hanging off the hem ring, instead of riding it rigidly.
    // They then swing and fold, and — since the rig already collides with the body — drape over the legs
    // and pool on the ground rather than passing through them. Costs roughly one body and one joint per
    // skirt bone (~18 on a typical coat); only reachable when the tube model is on, which already brings
    // a rig of its own.
    public bool KoStripGarmentSkirtPhysics { get; set; } = true;
    public float KoStripSkirtSegmentMass { get; set; } = 0.06f;
    // Radians a segment may swing away from its parent. Too tight reads as a board; too loose lets a
    // panel fold back through the leg it is hanging on.
    public float KoStripSkirtSwingLimit { get; set; } = 0.9f;
    // How much of that swing is allowed at birth. The rig relaxes from this to the full range over the
    // first second, so a skirt does not burst out of its rest shape on the frame it is created.
    public float KoStripSkirtInitialSwing { get; set; } = 0.3f;

    // Pieces still attached to the body travel with it when the body is moved as a whole. Nothing but
    // contact holds them on, so without this they are simply left where they were. Pieces that have
    // already come away stay put; which is which is decided by whether the piece still wraps the bones
    // it was built around, not by how near the body it happens to lie.
    public bool KoStripGarmentFollowsBody { get; set; } = true;

    // Friction the tube uses against the corpse (higher = clings/slides slower). Defaults match the
    // values the tube shipped with (Math.Clamp(RagdollFriction, 0.45, 0.9) at RagdollFriction=1.0).
    public const float KoStripGarmentTubeBodyFrictionDefault = 0.9f;
    public float KoStripGarmentTubeBodyFriction { get; set; } = KoStripGarmentTubeBodyFrictionDefault;

    // Friction the tube uses against the ground once it slides off the body. Default matches the
    // value the tube shipped with (MathF.Max(RagdollFriction, 3.5) at RagdollFriction=1.0).
    public const float KoStripGarmentTubeGroundFrictionDefault = 3.5f;
    public float KoStripGarmentTubeGroundFriction { get; set; } = KoStripGarmentTubeGroundFrictionDefault;

    // How long the tube stays visually bound to the body pose before handoff to physics. Default matches
    // the value the tube shipped with (ClothHoldMinFrames = 18 frames @ 60fps).
    public const float KoStripGarmentTubeHoldSecondsDefault = 0.3f;
    public float KoStripGarmentTubeHoldSeconds { get; set; } = KoStripGarmentTubeHoldSecondsDefault;

    // Manual fallback duration (seconds) for the "still attached" visual hold with Advanced clothing
    // settle on. Auto mode is the default path; this only applies when Auto cloth hold is disabled.
    public const float KoStripClothHoldSecondsDefault = 0.3f;
    public float KoStripClothHoldSeconds { get; set; } = KoStripClothHoldSecondsDefault;

    // Auto cloth hold: release the garment on an event (body settled, or slid down to the floor)
    // rather than the fixed KoStripClothHoldSeconds timer.
    public bool KoStripClothHoldAuto { get; set; } = true;

    // Auto-hold feel: 0 Quick, 1 Natural, 2 Clingy, 3 Slide-to-floor, 4 Visual-only. Default Slide-to-floor.
    public int KoStripClothHoldPreset { get; set; } = 3;

    // Visual-only preset tuning: how far (metres) and how fast (m/s) the garment slides down the body
    // before it freezes and stays visual. Only used by the Visual-only preset — Slide-to-floor keeps its
    // own fixed 0.8m / 0.20 m/s behaviour. Raise the distance if the garment stops short of the ground
    // in a standing KO; raise the speed if the slide looks too slow.
    public const float KoStripClothVisualOnlySlideDistanceDefault = 0.8f;
    public float KoStripClothVisualOnlySlideDistance { get; set; } = KoStripClothVisualOnlySlideDistanceDefault;
    public const float KoStripClothVisualOnlySlideSpeedDefault = 0.07f;
    public float KoStripClothVisualOnlySlideSpeed { get; set; } = KoStripClothVisualOnlySlideSpeedDefault;

    // Per-slot "collapse on drop" toggles for the physics-drop pieces. When a slot is enabled the
    // dropped piece deflates/flattens like cloth; when disabled it keeps its full rigid shape (better
    // for armor / rigid gear). Indexed via GearKeepModelSlot (0 Head,1 Body,2 Hands,3 Legs,4 Feet,
    // 5 Ears,6 Neck,7 Wrists,8 RFinger,9 LFinger).
    public bool KoStripCollapseHead { get; set; } = true;
    public bool KoStripCollapseBody { get; set; } = true;
    public bool KoStripCollapseHands { get; set; } = true;
    public bool KoStripCollapseLegs { get; set; } = true;
    public bool KoStripCollapseFeet { get; set; } = true;
    public bool KoStripCollapseEars { get; set; } = false;
    public bool KoStripCollapseNeck { get; set; } = false;
    public bool KoStripCollapseWrists { get; set; } = false;
    public bool KoStripCollapseRFinger { get; set; } = false;
    public bool KoStripCollapseLFinger { get; set; } = false;

    /// <summary>Whether the dropped piece for the given GearKeepModelSlot should collapse/deflate.
    /// Unknown slots default to collapsing (matches the historic all-gear-deflates behavior).</summary>
    public bool IsKoStripCollapseEnabled(int gearKeepModelSlot) => gearKeepModelSlot switch
    {
        0 => KoStripCollapseHead,
        1 => KoStripCollapseBody,
        2 => KoStripCollapseHands,
        3 => KoStripCollapseLegs,
        4 => KoStripCollapseFeet,
        5 => KoStripCollapseEars,
        6 => KoStripCollapseNeck,
        7 => KoStripCollapseWrists,
        8 => KoStripCollapseRFinger,
        9 => KoStripCollapseLFinger,
        _ => true,
    };

    /// <summary>Restore the default collapse mask: clothing collapses, accessories stay rigid.</summary>
    public void ResetKoStripCollapseDefaults()
    {
        KoStripCollapseHead = true;
        KoStripCollapseBody = true;
        KoStripCollapseHands = true;
        KoStripCollapseLegs = true;
        KoStripCollapseFeet = true;
        KoStripCollapseEars = false;
        KoStripCollapseNeck = false;
        KoStripCollapseWrists = false;
        KoStripCollapseRFinger = false;
        KoStripCollapseLFinger = false;
    }

    // Shortcuts bar
    public bool ShowShortcuts { get; set; } = false;
    public bool ShowProfessionalWindow { get; set; } = false;
    public bool ShowFastCombatToolbar { get; set; } = false;
    public bool ShowDefeatRevivePopup { get; set; } = true;
    public string FastCombatRecipeName { get; set; } = "";
    public int FastCombatLevel { get; set; } = 90;

    // Death Cam toolbar
    public bool ShowDeathCamToolbar { get; set; } = false;

    // Target Formation
    public bool EnableNpcTargetPlayer { get; set; } = true;
    public bool EnableTargetApproach { get; set; } = true;
    public bool UseSoloTargetFormationWhenNoCompanions { get; set; } = false;
    public float TargetApproachDistance { get; set; } = 1.5f;
    public bool UseVNavmeshTargetApproach { get; set; } = true;

    // Map enemies: real BattleNpc objects can join the mixed battle through
    // sensing or first attack.
    public bool EnableMapEnemySensing { get; set; } = false;
    public bool EnableMapPlayerEnemySensing { get; set; } = false;
    public float MapEnemySenseRange { get; set; } = 10.0f;
    public int MapEnemyMaxCount { get; set; } = 10;

    // Legacy aggro propagation. Kept for config compatibility; no longer shown
    // in the main GUI once Map Enemies owns world-enemy joins.
    public bool EnableAggroPropagation { get; set; } = false;
    public float AggroPropagationRange { get; set; } = 15.0f;

    // Maximum number of active targets
    public int MaxTargets { get; set; } = 10;

    // Custom in-simulation targeting: during an active simulation the plugin
    // takes over the game's target keybinds (confirm acquires, cancel releases,
    // next/prev cycle) and the player only attacks the locked target.
    public bool EnableCustomTargeting { get; set; } = true;

    // Auto-counter: while custom targeting is active and the player has no locked
    // target, being hit by an enemy auto-locks that attacker. Pressing cancel
    // suppresses it (no auto-lock even when hit) until the player confirm-locks again.
    public bool EnableAutoCounter { get; set; } = true;

    // Combat-link arcs: blue arc = player → locked target, red arcs = every enemy
    // currently attacking the player. Drawn above the head with a flowing animation
    // from attacker toward the one being attacked.
    public bool ShowCombatLinkArcs { get; set; } = true;
    public bool ShowLockMarker { get; set; } = true;
    public float CombatLinkHeightOffset { get; set; } = 0.3f; // extra Y above the head anchor
    public float CombatLinkAlpha { get; set; } = 0.55f;       // base stroke opacity
    public float CombatLinkThickness { get; set; } = 4.0f;    // core stroke thickness (px)
    public float CombatLinkFlowSpeed { get; set; } = 0.6f;    // flow cycles per second

    // Ragdoll physics (Experimental)
    public bool EnableRagdoll { get; set; } = false;
    public float RagdollActivationDelay { get; set; } = 1.0f;
    // Extend terrain detection: also build ground collision patches under nearby
    // enemies (not just the death spot) so a victory-sequence grab that drags the
    // body onto an enemy doesn't fall through the floor on release. Costs extra
    // raycasts/triangles at activation, so default off.
    public bool ExtendTerrainDetection { get; set; } = false;
    // NPC death ragdoll
    public bool EnableNpcDeathRagdoll { get; set; } = true;
    public float NpcRagdollActivationDelay { get; set; } = 0.5f;
    public int MaxNpcRagdolls { get; set; } = 5;
    public float RagdollGravity { get; set; } = 9.8f;
    public float RagdollDamping { get; set; } = 0.97f;
    public int RagdollSolverIterations { get; set; } = 8;
    // Velocity-solve substeps per fixed timestep. 1 = legacy behavior. Raising this
    // re-solves the constraints at a finer sub-step, which is BEPU's recommended lever
    // for making a STIFF limit wall (see RagdollLimitSpringFrequency) well-conditioned
    // instead of pumping energy. Costs ~linearly in performance. 8 = the validated default
    // that keeps stiff joints/limits from rubber-banding. Takes effect on next ragdoll activation.
    public int RagdollSolverSubsteps { get; set; } = 8;
    // Spring frequency (Hz) of the joint LIMIT walls (swing cones + twist ranges), not
    // the positional joints. Higher = firmer wall so joints don't blow past their range
    // under momentum (e.g. shoulders/waist over-rotating); too high relative to the
    // 60Hz step can over-drive the solver into jitter. 60 = long-standing soft wall,
    // 120 = very firm (needs substeps to stay stable), 90 = balanced default. Takes
    // effect on next ragdoll activation.
    public float RagdollLimitSpringFrequency { get; set; } = 90f;
    // Soft-edge swing limits (ball joints): replace the hard 90Hz wall on swing cones and
    // the Tier-C directional limits with a low-frequency, overdamped spring, so a joint
    // sliding toward its range edge decelerates and settles instead of pinning at the
    // extreme angle (the visual cause of splayed "spread-eagle" corpse poses freezing at
    // their limits). Hinges (knee/elbow) keep the hard wall — a soft knee reads as
    // hyperextension. Takes effect on next ragdoll activation.
    public bool RagdollSoftLimits { get; set; } = true;
    public float RagdollSoftLimitFrequency { get; set; } = 12f;
    public float RagdollSoftLimitDamping { get; set; } = 4f;
    // Spring frequency (Hz) of the POSITIONAL joints (the BallSocket/Weld that hold bones
    // together at the joint), as opposed to the limit walls above. Higher = bones separate
    // less under large impulses ("rubber-band" stretch); too high relative to the step needs
    // more substeps to stay stable. 30 = long-standing default. Takes effect on next
    // ragdoll activation.
    public float RagdollJointSpringFrequency { get; set; } = 30f;
    // Positional joint stiffness for the FOOT specifically (calf->foot BallSocket). The foot
    // takes the hardest ground-impact impulses, so it rubber-bands first; give it a firmer
    // spring than the body default. 60 = firm; falls back to RagdollJointSpringFrequency when
    // set to 0. Takes effect on next ragdoll activation.
    public float RagdollFootJointSpringFrequency { get; set; } = 60f;
    // Animation-driven handoff: when the ragdoll activates after the death-animation delay,
    // seed each physics body with the velocity the animation was carrying (finite-difference
    // of the last animated frame) instead of starting at zero. Removes the "freeze" hitch
    // when an in-motion death animation hands off to physics, and gives the topple its initial
    // momentum. Disable to restore the old zero-velocity handoff.
    public bool RagdollCarryAnimationVelocity { get; set; } = true;
    // Scales the carried handoff velocity (1 = exact animation speed). Lower if a fast death
    // animation throws the corpse too hard at handoff.
    public float RagdollHandoffVelocityScale { get; set; } = 1.0f;
    // Relaxation collapse also drives a whole-body center-of-mass topple (the body loses
    // balance over its support base and falls like an inverted pendulum), fused with the
    // muscle-failure brake, instead of only a one-shot directional shove. Off = legacy single
    // impulse.
    // Stage a hard landing instead of merely simulating it: a brief freeze, one heavy heave with a whip
    // on the limbs, and a camera shake. Physics alone cannot make a corpse read heavy — and restitution
    // is the wrong lever, since a real one gives the decaying patter of a beach ball. The weight comes
    // from the freeze frame and the camera, which is exactly how a fighting game does it.
    public bool RagdollImpactWeight { get; set; } = true;

    // Make the body itself read heavy, as opposed to the landing: fall harder than true gravity, and
    // give the limbs the rotational inertia a real one has rather than the little a thin capsule
    // implies. Kept separate from the landing staging above because unlike that, this changes
    // trajectories — how far a kicked corpse flies, and how fast it tumbles on the way.
    public bool RagdollHeavyBody { get; set; } = true;

    public bool RagdollRelaxationTopple { get; set; } = true;
    // Collapse asymmetry: real people never collapse symmetrically. Pick a random lead side
    // each death — its leg buckles first and the whole-body topple leans + twists toward it —
    // instead of a flat, mirror-image fall. 0 = perfectly symmetric (robotic), ~0.35 = natural
    // lopsided collapse, 1 = strongly one-sided. Applies to the Relaxation topple/spike.
    public float RagdollCollapseAsymmetry { get; set; } = 0.35f;
    // Staged muscle failure: instead of every joint fading on one shared curve, let the muscle
    // groups fail in sequence — legs give first, the trunk holds a beat longer, the arms trail
    // last. More biological than a uniform limp. Off = single shared fade curve.
    public bool RagdollStagedFailure { get; set; } = true;
    // Momentum-steered topple: bias the fall direction toward the body's actual horizontal
    // motion at the handoff (carried from the death animation), so a moving corpse falls the way
    // it was going rather than a fixed preset. 0 = ignore momentum (use Topple direction only),
    // ~0.5 = blend, 1 = fall purely along momentum when it is moving.
    public float RagdollToppleMomentumBias { get; set; } = 0.5f;
    // Anatomical joint-frame builder for hinge axes and ball-joint twist references.
    // Keep the switch so unusual skeletons can fall back to the legacy frame builder.
    public bool RagdollExperimentalJointFrames { get; set; } = true;
    // Knee/elbow planar hinge: constrain the bend to the sagittal plane with a soft
    // AngularHinge (BallSocket = position, AngularHinge = plane, SwingLimit = range).
    // Without it the knee/elbow is a swing CONE and can fold sideways — the biggest
    // "ragdoll, not a body" tell. Soft spring + substeps avoid the freeze the old stiff
    // full-Hinge hit. Frequency is the plane stiffness (Hz): too low = still bends
    // sideways under load, too high relative to the 60 Hz step = jitter/freeze. Takes
    // effect on next ragdoll activation.
    public bool RagdollKneeElbowPlanarHinge { get; set; } = true;
    // 18 Hz was calibrated before SolverSubsteps existed; with 8 substeps the solver
    // holds far stiffer angular constraints without freezing, and a soft planar hinge
    // is exactly what lets impacts wobble/tunnel the knee sideways and in twist.
    public float RagdollKneeHingeFrequency { get; set; } = 50f;
    public bool RagdollSelfCollision { get; set; } = true; // Body parts collide with each other (arms vs torso, etc)
    public float RagdollFriction { get; set; } = 1.0f; // Surface friction (0=ice, 1=grippy). Lower = limbs slide more realistically.

    // Tier D — Anthropometric segment masses. When on, each physics bone's mass is
    // resolved as (Winter Table 3.1 body-mass fraction) x RagdollBodyMass instead of the
    // hand-picked per-bone Mass values. Fixes wrong inertia (thigh too heavy, trunk not
    // pelvis-heavy). Cloth/weapon/breast bones keep their tiny existing masses.
    // Default OFF after field testing: only useful in specific setups.
    public bool RagdollAnthropometricMass { get; set; } = false;
    public float RagdollBodyMass { get; set; } = 70f; // Total body mass (kg) anthropometric fractions scale against.
    // Tier B — Anatomy-fixed knee/elbow hinge. A knee folds the shin backward and an elbow folds the
    // forearm forward, relative to the character: facts about the body, not about the pose it died in.
    // Both the hinge axis and the fold direction are taken from the character's facing, replacing
    // Cross(thighDir, shinDir) — degenerate for a near-straight limb — and a fold sign that was read off
    // whichever way the limb happened to be leaning (perpendicular on a straight leg, so it fell out of
    // floating-point noise, one knee sideways and the other forward).
    //
    // This shipped OFF because it fixed the axis and then deliberately took its SIGN from the very axis
    // it was replacing, which bent every knee forward. The idea was right; the sign was miswired. On by
    // default now that it derives both.
    public bool RagdollAnatomicalHingeAxis { get; set; } = true;
    // Tier C — Asymmetric swing-twist range of motion. When on, joints draw their axial
    // twist range (all joints) and the knee/elbow flexion/hyperextension bounds from a
    // clinical/ISB anatomical ROM table instead of the hand-set per-bone twist values and
    // the symmetric fold-stop. Blocks knee/elbow backward hyperextension (the most visible
    // anatomical violation) and gives each joint a correct asymmetric axial range. The
    // ball-joint (hip/shoulder) asymmetric SWING ellipse is deferred to Tier A. Takes
    // effect on next ragdoll activation.
    // Default OFF after field testing: only useful in specific setups. The twist
    // governors, hemisphere locks, and profile-table limits stay active regardless.
    public bool RagdollAnatomicalRom { get; set; } = false;

    // Dismemberment POC: while the player ragdoll is active, collapse each selected limb's bone
    // subtree to ~0 scale so it vanishes from the body. Multi-select: stores the root bone name of
    // every severed part (e.g. "j_kao", "j_ude_b_l"). Empty = none. The separate rolling limb prop
    // comes later; this is the "hide" half.
    public List<string> DismemberPocBones { get; set; } = new();
    // When on, each hidden limb also spawns a clone that shows ONLY that limb and tumbles away (the
    // "rolls away" half). Off = just hide on the body. POC: local player only.
    public bool EnableDismemberRollaway { get; set; } = true;
    public bool EnableEnemyDismemberment { get; set; } = true;
    public int EnemyHumanoidDismembermentCount { get; set; } = 0;
    public float EnemyMonsterDismembermentBonePercent { get; set; } = 100.0f;

    // Death collapse — physics-driven guided collapse on death (relaxation family + directed
    // knee power-loss). Config lives in GuidedCollapse; see DEATH_COLLAPSE_RESEARCH.md.
    public GuidedCollapseSettings GuidedCollapse { get; set; } = new();
    // Weapon drop physics — runs as part of ragdoll; weapon detaches and falls on death
    public float WeaponDropGravity { get; set; } = 9.8f;
    public float WeaponDropDamping { get; set; } = 0.99f;
    public float WeaponDropAngularDamping { get; set; } = 0.85f; // much stronger than linear: kills spin fast so capsule stops rolling
    public float WeaponDropMass { get; set; } = 1.5f;
    public float WeaponDropRadius { get; set; } = 0.025f;
    public float WeaponDropHalfLength { get; set; } = 0.4f;
    public float WeaponDropBounce { get; set; } = 1.5f; // Bepu MaximumRecoveryVelocity — higher = bouncier
    public float WeaponDropFriction { get; set; } = 0.6f;
    public int WeaponDropSolverIterations { get; set; } = 4;
    // Hair physics
    public bool RagdollHairPhysics { get; set; } = false;
    public float RagdollHairGravityStrength { get; set; } = 0.5f;
    public float RagdollHairDamping { get; set; } = 0.92f;
    public float RagdollHairStiffness { get; set; } = 0.1f;
    // Hair physics — BEPU rig mode: real jointed rigid-body strands (reuses the garment tube rig:
    // BallSocket + relaxing SwingLimit + damping AngularMotor + fading pose-guide servo), anchored to
    // the head ragdoll body and colliding with the corpse + ground. When false, the legacy pendulum
    // simulator (fields above) is used instead. Works for any hairstyle — the rig is built from the
    // hair partial-skeleton bone tree, so it is name-/style-agnostic (mod hairstyles included).
    public bool RagdollHairRigMode { get; set; } = false;
    public float RagdollHairRigSegmentMass { get; set; } = 0.02f;        // per-segment mass (very light)
    public float RagdollHairRigThickness { get; set; } = 0.008f;         // strand box half-thickness (m)
    public float RagdollHairRigSwingLimit { get; set; } = 0.6f;          // per-joint swing ROM (radians)
    public float RagdollHairRigInitialSwingFactor { get; set; } = 0.28f; // spawn ROM fraction (holds style, relaxes to full)
    public float RagdollHairRigPoseGuideForce { get; set; } = 4f;        // servo force holding the style at spawn, fades out
    public float RagdollHairRigSettleSeconds { get; set; } = 1.0f;       // time to relax ROM to full + fade the pose guide
    // Ragdoll debug overlay — renders capsules and joint limits in 3D
    public bool RagdollDebugOverlay { get; set; } = false;
    // Ragdoll bone configs (Advanced) — per-bone physics parameters
    // Empty = use built-in defaults from RagdollController.DefaultBoneDefs
    public List<RagdollBoneConfig> RagdollBoneConfigs { get; set; } = new();

    // Saved ragdoll bone profiles (snapshots of the per-bone advanced configs)
    public List<RagdollBoneProfile> RagdollBoneProfiles { get; set; } = new();

    // Dev (Experimental) — hidden behind easter egg
    public bool RagdollVerboseLog { get; set; } = false;
    public bool RagdollFollowPosition { get; set; } = false; // Follow ragdoll root to keep the flung corpse from being culled/unloaded on long falls. Local player moves render-only (DrawObject.Position); NPC phantoms move full position.
    public bool RagdollLiftUndergroundBonesOnStart { get; set; } = false;
    public bool DevCompanionAppearanceVariant { get; set; } = false;
    public bool DevPartyApproachDebugLog { get; set; } = false;
    public bool RagdollNpcCollision { get; set; } = true;
    public bool RagdollNpcCollisionAutoSize { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    public bool RagdollNpcCollisionConvexHull { get; set; } = false;
    // Convex hull: one hull built from the activation-pose bone positions. It is an approximation
    // of the creature's shape, but a cheap one — the mesh (skinned) snapshot it replaces as the
    // default is far more faithful and far more expensive, enough to stutter badly on larger
    // creatures and on machines that were coping until then. Fidelity is still one dropdown away.
    public RagdollNpcCollisionMode RagdollNpcCollisionMode { get; set; } = RagdollNpcCollisionMode.ConvexHull;
    public bool RagdollNpcSettleCollision { get; set; } = true;

    // Auto-engage: NPC enemy targets attack the player automatically on
    // simulation start / reset / reboot without the player attacking first.
    public bool EnableNpcAutoEngage { get; set; } = false;
    public float NpcAutoEngageDelay { get; set; } = 2.0f;

    // Active Camera — camera tracks a bone with free orbital control
    public bool ShowActiveCamToolbar { get; set; } = false;
    public bool EnableActiveCamera { get; set; } = false;
    public string ActiveCameraBoneName { get; set; } = "j_kubi";
    public float ActiveCameraHeightOffset { get; set; } = 0f;
    public float ActiveCameraSideOffset { get; set; } = 0f;
    public float ActiveCameraVerticalAngle { get; set; } = 0f;
    public bool ActiveCameraLockVertical { get; set; } = false;
    public bool ActiveCameraDisableCollision { get; set; } = false;
    public float ActiveCameraMinZoomDistance { get; set; } = 1.0f;
    public bool ActiveCameraPreventFade { get; set; } = false;

    // Legacy combined skill VFX toggle. Migrated to the split toggles below.
    public bool EnableSkillVfx { get; set; } = false;
    public bool EnableCharacterVfx { get; set; } = true;
    public bool EnableTargetVfx { get; set; } = true;

    // Hit VFX on player when taking damage (empty = disabled)
    public string HitVfxPath { get; set; } = "vfx/common/eff/dk05th_stdn0t.avfx";
    public bool EnableHitVfx { get; set; } = true;

    // HP Bar bone tracking
    public float PlayerHpBarYOffset { get; set; } = 0.3f;
    public float PlayerHpBarXOffset { get; set; } = 0f;
    public float EnemyHpBarYOffset { get; set; } = 0.3f;
    public bool HpBarOcclusion { get; set; } = true;
    public string CustomPlayerName { get; set; } = "";

    // Player HP bar label customization
    public bool ShowSimLabel { get; set; } = true;
    public string SimLabelText { get; set; } = "Sim";
    public bool ShowDeadLabel { get; set; } = true;
    public string DeadLabelText { get; set; } = "DEAD";
    public bool ShowDefeatedText { get; set; } = true;
    public string DefeatedText { get; set; } = "DEFEATED";

    // Death Cam (Experimental)
    public bool EnableDeathCam { get; set; } = false;
    public string DeathCamBoneName { get; set; } = "n_hara";
    public float DeathCamTransitionDuration { get; set; } = 1.5f;
    public float DeathCamAnchorDirH { get; set; } = 0;
    public float DeathCamAnchorDirV { get; set; } = 0;
    public float DeathCamAnchorDistance { get; set; } = 5.0f;
    public float DeathCamFoV { get; set; } = 0.78f;
    public float DeathCamHeightOffset { get; set; } = 0f;
    public float DeathCamSideOffset { get; set; } = 0f;
    public float DeathCamTilt { get; set; } = 0f;
    public bool DeathCamDisableCollision { get; set; } = true;
    public bool DeathCamAnchorSet { get; set; } = false;

    // Death Cam Presets
    public List<DeathCamPreset> DeathCamPresets { get; set; } = new();

    // Spawn Enemy defaults
    public int SpawnDirection { get; set; } = 0;       // 0=Front, 1=Behind, 2=Left, 3=Right
    public float SpawnDistance { get; set; } = 5.0f;    // yalms from player
    // When true, virtual enemies spawn directly in front of the player using the character's real
    // in-game facing (GameObject.Rotation, NOT any ragdoll/visual facing), instead of a random
    // direction around the player. Also governs regenerate/refresh on combat reset. Default off.
    public bool SpawnInFront { get; set; } = false;

    // Recent NPCs (for spawn enemy UI)
    public List<uint> RecentNpcIds { get; set; } = new();
    public List<RecentNpcEntry> RecentNpcEntries { get; set; } = new();

    // -- Action Mode --------------------------------------------------------
    // A toggleable real-time action layer that replaces the tab-target / GCD
    // interaction. Player attacks/guard come from hotbar actions remapped by
    // actionId (the game resolves keyboard/gamepad for us); enemy attacks
    // telegraph from a wind-up snapshot, then resolve by hitbox at the active frame. When OFF,
    // every seam falls back to the original simulation behavior.
    public bool ActionMode { get; set; } = true;

    // Fighting Mode (Experimental): 1v1 side-view combat layer. It owns player
    // action routing, constrains the player/enemy pair onto a 2D lane, and drives
    // a fighting-game camera. Mutually exclusive with Action Mode.
    public bool FightingMode { get; set; } = false;
    public float FightingModeLaneHalfWidth { get; set; } = 0.35f;
    public float FightingModeMinSeparation { get; set; } = 0.45f;
    public float FightingModeMaxSeparation { get; set; } = 1.0f;
    // Camera defaults are field-tuned in-game values.
    public float FightingModeCameraMargin { get; set; } = 0.75f;
    public float FightingModeCameraMinDistance { get; set; } = 2.31f;
    public float FightingModeCameraMaxDistance { get; set; } = 3.5f;
    public float FightingModeCameraSmoothing { get; set; } = 10.0f;
    public float FightingModeCameraHeight { get; set; } = 1.18f;
    public float FightingModeCameraVerticalAngle { get; set; } = 0.31f;
    public bool FightingModeTranslateCam { get; set; } = true;
    public float FightingModeTranslateDuration { get; set; } = 3.0f;
    public float FightingModeTranslateDistance { get; set; } = 1.22f;
    public string FightingModeTranslateBoneName { get; set; } = "j_kosi";
    public float FightingModeTranslateHeightOffset { get; set; } = 0.44f;
    public float FightingModeTranslateSideOffset { get; set; } = 0.0f;
    public bool FightingModeTranslateLockHorizontal { get; set; } = false;
    public float FightingModeTranslateHorizontalAngle { get; set; } = 0.0f;
    public bool FightingModeTranslateLockVertical { get; set; } = false;
    public float FightingModeTranslateVerticalAngle { get; set; } = 0.31f;
    // Which side of the lane the 2D camera sits on (0 = right of the engage axis, 1 = left).
    public int FightingModeCameraSide { get; set; } = 0;
    // Double-jump vault: a second press of the game's own JUMP bind mid-air carries the player
    // over the enemy. Fighting Mode has no key config of its own — jump inherits the game bind,
    // and the basic attack / guard reuse the Action Mode binds.
    public float FightingModeVaultDuration { get; set; } = 0.45f;
    // Fighting Mode weapon-contact hit detection: the main-hand weapon segment must
    // sweep through the enemy hurtbox capsule during the swing's active window.
    public float FightingModeWeaponLength { get; set; } = 1.2f;       // fallback/single-bone length
    public float FightingModeWeaponLengthScale { get; set; } = 1.1f;  // pad on skeleton-derived length
    public int FightingModeWeaponAxis { get; set; } = 1;              // 0=X 1=Y 2=Z (single-bone weapons)
    public float FightingModeWeaponRadius { get; set; } = 0.15f;
    public float FightingModeHurtboxHeight { get; set; } = 2.0f;
    public float FightingModeHurtboxRadiusScale { get; set; } = 1.0f;
    public float FightingModeAttackActiveStartPct { get; set; } = 0.25f;
    public float FightingModeAttackActiveEndPct { get; set; } = 0.70f;
    public bool FightingModeDebugDraw { get; set; } = false;
    // Fighting Mode 1v1 enemy AI (replaces NpcAiController for the engaged fighter):
    // spacing band + approach/retreat + telegraphed attacks + hitstun pushback.
    // Retreats slower than it approaches so the player can actually corner it.
    public float FightingAiMoveSpeed { get; set; } = 2.5f;
    public float FightingAiRetreatSpeedScale { get; set; } = 0.5f;
    public float FightingAiRangeMin { get; set; } = 0.9f;
    public float FightingAiRangeMax { get; set; } = 2.2f;
    public float FightingAiAttackCooldown { get; set; } = 1.1f;
    public float FightingAiAttackCooldownJitter { get; set; } = 0.7f;
    // Fighting-game move variety: combo strings, dash-ins, and side-switching hops.
    public float FightingAiComboChance { get; set; } = 0.35f;
    public float FightingAiComboGap { get; set; } = 0.18f;
    public int FightingAiMaxComboHits { get; set; } = 3;
    public float FightingAiDashChance { get; set; } = 0.35f;
    public float FightingAiDashSpeedScale { get; set; } = 2.2f;
    public float FightingAiJumpOverChance { get; set; } = 0.15f;
    public float FightingAiJumpDuration { get; set; } = 0.55f;
    public float FightingAiJumpHeight { get; set; } = 1.6f;
    public float FightingAiJumpInAttackChance { get; set; } = 0.5f;
    public float FightingAiRetreatChance { get; set; } = 0.35f;
    public float FightingAiRetreatDuration { get; set; } = 0.8f;
    public float FightingAiRecoverTime { get; set; } = 0.6f;
    public float FightingAiHitstunDuration { get; set; } = 0.35f;
    public float FightingAiHitstunPushback { get; set; } = 1.5f;
    // Fighting Mode KO camera: after the player's defeat, when the dev-controlled
    // monster is followed, frame the corpse↔monster midpoint and zoom with separation.
    public float FightingModeKoCameraMargin { get; set; } = 0.8f;
    public float FightingModeKoCameraBase { get; set; } = 3.0f;
    public float FightingModeKoCameraMinDistance { get; set; } = 3.0f;
    public float FightingModeKoCameraMaxDistance { get; set; } = 14.0f;
    public float FightingModeKoCameraHeight { get; set; } = 0.8f;
    public bool FightingModeKoLockAngle { get; set; } = true;

    // Input map: put these actions on the hotbar; a press is interpreted as the
    // mapped role instead of firing the real action. 0 = unmapped.
    public uint ActionAttackId { get; set; } = 0;
    public uint ActionGuardId { get; set; } = 0;
    public uint ActionSkill1Id { get; set; } = 0;
    public uint ActionSkill2Id { get; set; } = 0;
    public int ActionGuardKey { get; set; } = 17; // SeVirtualKey.Control. Independent of job/action availability.
    public GamepadButtons ActionGuardGamepadButton { get; set; } = GamepadButtons.R2;
    // Basic attack: gamepad South/Cross (Xbox A) + the Q key, no hotbar action needed.
    public GamepadButtons ActionBasicAttackGamepadButton { get; set; } = GamepadButtons.South;
    public int ActionBasicAttackKey { get; set; } = 0x51; // SeVirtualKey.Q

    // Telegraph / windup tuning
    public float MinTelegraphWindup { get; set; } = 0.4f;  // floor so even instant attacks are readable

    // Action-Mode attack pace: enemies AND companions initiate attacks this many
    // times faster (auto-attack delay + skill cooldowns divided by this). Makes the
    // fight feel action-paced instead of the slow tab-target ~3s cadence.
    public float ActionEnemyAttackSpeed { get; set; } = 1.6f;

    // Guard tuning
    public float GuardActiveWindow { get; set; } = 0.22f;  // perfect-guard reaction window (early tolerance)
    public float GuardLateTolerance { get; set; } = 0.15f; // grace after the strike closes (late tolerance)
    public float ChainGuardWindow { get; set; } = 0.4f;    // after a block, keep guard open this long to absorb the next attack (chain guard)
    public int GuardMaxChain { get; set; } = 3;            // max attacks ONE guard press can chain-block before the chain ends and you must re-guard (0 = unlimited). Stops one press tanking an entire swarm.
    public float GuardRecovery { get; set; } = 0.35f;      // lockout after a guard CHAIN ends
    public float GuardCooldown { get; set; } = 0.15f;      // min time between guard attempts
    public ushort GuardTimelineId { get; set; } = 0;       // 0 = auto/fallback
    public string GuardSuccessVfxPath { get; set; } = "vfx/ws/wax_heavyswing/eff/wax_heavy1t0h.avfx";
    public string EnemyTelegraphVfxPath { get; set; } = "vfx/common/eff/cmhit_fire1t.avfx";

    // Player light-combo + hitbox tuning
    public float LightComboWindow { get; set; } = 0.6f;    // time to chain the next swing
    public float LightSwingInterval { get; set; } = 0.4f;  // min time between swings (cadence)
    public float PlayerHitboxRange { get; set; } = 5f;     // melee basic-attack reach (yalms), measured to the target's SURFACE
    public float PlayerHitboxAngleDeg { get; set; } = 100f; // melee selection cone full angle
    // Edge-to-edge reach: the target's hitbox radius x this is added to the melee select range, so a
    // large enemy is hittable from its surface (not its centre) while a small enemy is unchanged. This
    // is how FFXIV itself measures melee range. 0 = old centre-to-centre, 1 = exact surface, >1 = lenient.
    public float MeleeTargetHitboxReach { get; set; } = 1.0f;

    // Impact feedback on a confirmed player attack: brief target hitstop + a
    // camera punch + an optional hit spark. All gated below.
    public bool EnableHitFeedback { get; set; } = true;
    // Delay (seconds) between the attack input and the impact feedback firing. The swing has ~0.5s of
    // windup before the weapon visually CONNECTS, so the hitstop/camera/spark must wait or they fire
    // at swing start and feel disconnected. ~0.45 lines up with the contact frame; tune to taste.
    public float HitFeedbackDelay { get; set; } = 0.45f;
    // Hitstop: freeze the struck target's animation for this many ms on impact. The single
    // biggest contributor to impact feel. Keep SHORT (40-90 ms) or it reads as lag. 0 = off. Only the
    // target is frozen (never the player), and the freeze is skipped/auto-released if it dies.
    public float HitstopMs { get; set; } = 81.5f;
    // Camera punch: a brief decaying screen shake on impact (yalms of camera offset). Small = weighty,
    // large = nauseating. Layered on top of Active/Fight cam; suppressed during the death cam. 0 = off.
    public float HitCameraShake { get; set; } = 0.1f;
    public float HitCameraShakeDuration { get; set; } = 0.2f; // seconds the shake decays over
    // Spawn a spark VFX on the struck target (reuses HitVfxPath). Off by default: ActorVfxCreate on
    // modified/spawned actors can be fragile, so opt in once you've confirmed it's stable in your setup.
    public bool EnableHitSparkVfx { get; set; } = true;
    public int LightAttackPotency { get; set; } = 120;
    // Soft-target selection: ranged basic attack / ranged skills use a longer, wider selection cone
    // and pick the smallest-angle enemy (not the nearest).
    public float RangedBasicRange { get; set; } = 25f;
    public float RangedSelectAngleDeg { get; set; } = 160f;

    // Telegraph overlay
    public bool ShowTelegraphs { get; set; } = false;
    public float TelegraphAlpha { get; set; } = 0.45f;
    public float TelegraphThickness { get; set; } = 3.0f;

    // Action Mode windup: the strike resolves this long after the telegraph appears, so it's
    // the time the player has to react, and the duration of the osu-style approach circle.
    public float ActionWindupSeconds { get; set; } = 0.39f;

    // Play a wind-up swing on the enemy when the telegraph starts so the windup has body
    // language (instead of the enemy standing idle until the strike). NOTE: the engine couples
    // the impact sound + hit-reaction to a swing, so the strike still swings — this produces a
    // second swing. Off = enemy idle during the windup, single clean swing at the strike.
    public bool ActionEnemyWindupSwing { get; set; } = true;

    // osu-style parry-timing circle drawn on the player. A steady inner ring + an outer
    // ring that shrinks onto it over the windup; aligned = the guard window.
    public bool OsuCircleEnabled { get; set; } = true;
    public float OsuAnchorHeight { get; set; } = 1.1f;     // anchor height above feet (yalms; chest≈1.1)
    public float OsuInnerRadius { get; set; } = 0.18f;     // inner ring radius in world units (~1.5× head)
    public float OsuOuterStartScale { get; set; } = 3.2f;  // outer ring starts at inner × this

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
        MigrateSplitVfxToggles();
        MigrateSkirtParentChains();
        MigrateRagdollProfileMetadata();
        MigrateAnatomicalHinges();
        MigrateRagdollShinBoxHalfY();
        MigrateDefaultProfileTuning();
        MigrateActionGuardDefaultButton();
        MigrateActionGuardVfxDefault();
        MigrateActionBasicAttackDefaultButton();
        MigrateActionMeleeRangeDefault();
        MigrateActionAttackShapeDefaults();
        MigrateEnemyDismembermentDefaults();
        MigrateFightingSpacingDefaults();
        MigrateThinnerProfileTuning();
        MigrateKneeHingeStiffness();
        MigrateAnatomicalDefaultsOff();
        MigrateAnatomicalHingeSignFix(); // must run AFTER the off-migration, whose verdict it lifts
        MigrateNpcCollisionMode();
        MigrateNpcCollisionMeshDefault(); // after the mode migration above, whose old default it lifts
        MigrateNpcCollisionConvexHullDefault(); // last of the three: it overrides both verdicts above
        MigrateGuidedCollapse();
        MigrateDynamicCameraPivotBone();
        RenameLegacyBoneProfiles();
        SeedBuiltInBoneProfiles();
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    /// <summary>
    /// Force every existing config onto the convex-hull NPC collision shape. The mesh (skinned)
    /// shape that used to be the default is accurate but expensive enough to stutter badly, and
    /// a corrected default alone would never reach anyone who already has the old value saved —
    /// which is everyone who has run the plugin before. One-shot: the dropdown is theirs again
    /// afterwards, including switching straight back to mesh if they want the fidelity.
    /// </summary>
    private void MigrateNpcCollisionConvexHullDefault()
    {
        if (NpcCollisionConvexHullDefaultMigrated20260716)
            return;

        RagdollNpcCollisionMode = RagdollNpcCollisionMode.ConvexHull;
        RagdollNpcCollisionConvexHull = true;
        NpcCollisionConvexHullDefaultMigrated20260716 = true;
        Save();
    }

    /// <summary>
    /// Options the Ragdoll page owns but does not name after itself.
    /// </summary>
    private static readonly HashSet<string> RagdollPageExtraOptions = new(StringComparer.Ordinal)
    {
        nameof(ExtendTerrainDetection),
        nameof(EnableNpcDeathRagdoll),
        nameof(NpcRagdollActivationDelay),
        nameof(MaxNpcRagdolls),
        nameof(PartyCompanionDeathRagdoll),
        nameof(PartyEnemyDeathRagdoll),
    };

    /// <summary>
    /// Named like the page, but not the page's to reset.
    /// </summary>
    private static readonly HashSet<string> RagdollPageResetExclusions = new(StringComparer.Ordinal)
    {
        // The master switch. Resetting it would turn the whole feature off and take the page with it,
        // which reads as the button having broken something rather than having restored it.
        nameof(EnableRagdoll),

        // Data, not options. The per-bone table has its own reset on the Advanced page, and the saved
        // profiles are the user's own work — a defaults button has no business destroying either.
        nameof(RagdollBoneConfigs),
        nameof(RagdollBoneProfiles),

        // Dev-only switches. They live on the hidden panel, not this page.
        nameof(RagdollVerboseLog),
        nameof(RagdollFollowPosition),
        nameof(RagdollLiftUndergroundBonesOnStart),
    };

    private static bool IsRagdollPageOption(string name)
    {
        if (RagdollPageResetExclusions.Contains(name)) return false;

        return name.StartsWith("Ragdoll", StringComparison.Ordinal)
            || name.StartsWith("WeaponDrop", StringComparison.Ordinal)
            || RagdollPageExtraOptions.Contains(name);
    }

    /// <summary>
    /// Restore everything on the Ragdoll page — the ragdoll options themselves, guided collapse, NPC
    /// collision and its settle pass — to defaults.
    ///
    /// Done by reflection against a fresh Configuration rather than by a hand-written list of
    /// assignments, because a hand-written list is exactly what this replaces. That one had drifted
    /// until most of the page was missing from it: every option added since had to remember to enrol
    /// itself, and they stopped. A field is now restored by virtue of being named after the page it
    /// belongs to, and the only things that need naming are the exceptions.
    /// </summary>
    public void ResetRagdollPageDefaults()
    {
        var defaults = new Configuration();

        foreach (var property in typeof(Configuration).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.GetIndexParameters().Length > 0) continue;
            if (!IsRagdollPageOption(property.Name)) continue;

            property.SetValue(this, property.GetValue(defaults));
        }

        ResetGuidedCollapseDefaults();
        Save();
    }

    public void ResetGuidedCollapseDefaults(bool preserveEnabled = false)
    {
        var enabled = GuidedCollapse.Enabled;
        GuidedCollapse.ResetDefaults();
        if (preserveEnabled)
            GuidedCollapse.Enabled = enabled;
    }

    public void ResetFightingModeDefaults(bool preserveEnabled = true)
    {
        var enabled = FightingMode;
        var defaults = new Configuration();

        FightingMode = preserveEnabled ? enabled : defaults.FightingMode;
        FightingModeLaneHalfWidth = defaults.FightingModeLaneHalfWidth;
        FightingModeMinSeparation = defaults.FightingModeMinSeparation;
        FightingModeMaxSeparation = defaults.FightingModeMaxSeparation;
        FightingModeCameraMargin = defaults.FightingModeCameraMargin;
        FightingModeCameraMinDistance = defaults.FightingModeCameraMinDistance;
        FightingModeCameraMaxDistance = defaults.FightingModeCameraMaxDistance;
        FightingModeCameraSmoothing = defaults.FightingModeCameraSmoothing;
        FightingModeCameraHeight = defaults.FightingModeCameraHeight;
        FightingModeCameraVerticalAngle = defaults.FightingModeCameraVerticalAngle;
        FightingModeTranslateCam = defaults.FightingModeTranslateCam;
        FightingModeTranslateDuration = defaults.FightingModeTranslateDuration;
        FightingModeTranslateDistance = defaults.FightingModeTranslateDistance;
        FightingModeTranslateBoneName = defaults.FightingModeTranslateBoneName;
        FightingModeTranslateHeightOffset = defaults.FightingModeTranslateHeightOffset;
        FightingModeTranslateSideOffset = defaults.FightingModeTranslateSideOffset;
        FightingModeTranslateLockHorizontal = defaults.FightingModeTranslateLockHorizontal;
        FightingModeTranslateHorizontalAngle = defaults.FightingModeTranslateHorizontalAngle;
        FightingModeTranslateLockVertical = defaults.FightingModeTranslateLockVertical;
        FightingModeTranslateVerticalAngle = defaults.FightingModeTranslateVerticalAngle;
        FightingModeWeaponLength = defaults.FightingModeWeaponLength;
        FightingModeWeaponLengthScale = defaults.FightingModeWeaponLengthScale;
        FightingModeWeaponAxis = defaults.FightingModeWeaponAxis;
        FightingModeWeaponRadius = defaults.FightingModeWeaponRadius;
        FightingModeHurtboxHeight = defaults.FightingModeHurtboxHeight;
        FightingModeHurtboxRadiusScale = defaults.FightingModeHurtboxRadiusScale;
        FightingModeAttackActiveStartPct = defaults.FightingModeAttackActiveStartPct;
        FightingModeAttackActiveEndPct = defaults.FightingModeAttackActiveEndPct;
        FightingModeDebugDraw = defaults.FightingModeDebugDraw;
        FightingModeCameraSide = defaults.FightingModeCameraSide;
        FightingModeVaultDuration = defaults.FightingModeVaultDuration;
        FightingAiMoveSpeed = defaults.FightingAiMoveSpeed;
        FightingAiRetreatSpeedScale = defaults.FightingAiRetreatSpeedScale;
        FightingAiRangeMin = defaults.FightingAiRangeMin;
        FightingAiRangeMax = defaults.FightingAiRangeMax;
        FightingAiAttackCooldown = defaults.FightingAiAttackCooldown;
        FightingAiAttackCooldownJitter = defaults.FightingAiAttackCooldownJitter;
        FightingAiComboChance = defaults.FightingAiComboChance;
        FightingAiComboGap = defaults.FightingAiComboGap;
        FightingAiMaxComboHits = defaults.FightingAiMaxComboHits;
        FightingAiDashChance = defaults.FightingAiDashChance;
        FightingAiDashSpeedScale = defaults.FightingAiDashSpeedScale;
        FightingAiJumpOverChance = defaults.FightingAiJumpOverChance;
        FightingAiJumpDuration = defaults.FightingAiJumpDuration;
        FightingAiJumpHeight = defaults.FightingAiJumpHeight;
        FightingAiJumpInAttackChance = defaults.FightingAiJumpInAttackChance;
        FightingAiRetreatChance = defaults.FightingAiRetreatChance;
        FightingAiRetreatDuration = defaults.FightingAiRetreatDuration;
        FightingAiRecoverTime = defaults.FightingAiRecoverTime;
        FightingAiHitstunDuration = defaults.FightingAiHitstunDuration;
        FightingAiHitstunPushback = defaults.FightingAiHitstunPushback;
        FightingModeKoCameraMargin = defaults.FightingModeKoCameraMargin;
        FightingModeKoCameraBase = defaults.FightingModeKoCameraBase;
        FightingModeKoCameraMinDistance = defaults.FightingModeKoCameraMinDistance;
        FightingModeKoCameraMaxDistance = defaults.FightingModeKoCameraMaxDistance;
        FightingModeKoCameraHeight = defaults.FightingModeKoCameraHeight;
        FightingModeKoLockAngle = defaults.FightingModeKoLockAngle;
    }

    public void ResetActionModeDefaults(bool preserveEnabled = true)
    {
        var enabled = ActionMode;
        var defaults = new Configuration();

        ActionMode = preserveEnabled ? enabled : defaults.ActionMode;
        ActionAttackId = defaults.ActionAttackId;
        ActionGuardId = defaults.ActionGuardId;
        ActionSkill1Id = defaults.ActionSkill1Id;
        ActionSkill2Id = defaults.ActionSkill2Id;
        ActionGuardKey = defaults.ActionGuardKey;
        ActionGuardGamepadButton = defaults.ActionGuardGamepadButton;
        ActionBasicAttackGamepadButton = defaults.ActionBasicAttackGamepadButton;
        ActionBasicAttackKey = defaults.ActionBasicAttackKey;

        MinTelegraphWindup = defaults.MinTelegraphWindup;
        ActionEnemyAttackSpeed = defaults.ActionEnemyAttackSpeed;

        GuardActiveWindow = defaults.GuardActiveWindow;
        GuardLateTolerance = defaults.GuardLateTolerance;
        ChainGuardWindow = defaults.ChainGuardWindow;
        GuardMaxChain = defaults.GuardMaxChain;
        GuardRecovery = defaults.GuardRecovery;
        GuardCooldown = defaults.GuardCooldown;
        GuardTimelineId = defaults.GuardTimelineId;
        GuardSuccessVfxPath = defaults.GuardSuccessVfxPath;
        EnemyTelegraphVfxPath = defaults.EnemyTelegraphVfxPath;

        LightComboWindow = defaults.LightComboWindow;
        LightSwingInterval = defaults.LightSwingInterval;
        PlayerHitboxRange = defaults.PlayerHitboxRange;
        PlayerHitboxAngleDeg = defaults.PlayerHitboxAngleDeg;
        LightAttackPotency = defaults.LightAttackPotency;

        ShowTelegraphs = defaults.ShowTelegraphs;
        TelegraphAlpha = defaults.TelegraphAlpha;
        TelegraphThickness = defaults.TelegraphThickness;
        ActionWindupSeconds = defaults.ActionWindupSeconds;
        ActionEnemyWindupSwing = defaults.ActionEnemyWindupSwing;

        OsuCircleEnabled = defaults.OsuCircleEnabled;
        OsuAnchorHeight = defaults.OsuAnchorHeight;
        OsuInnerRadius = defaults.OsuInnerRadius;
        OsuOuterStartScale = defaults.OsuOuterStartScale;
    }

    private void MigrateActionGuardDefaultButton()
    {
        if (ActionGuardDefaultMigratedToR2)
            return;

        if (ActionGuardGamepadButton == GamepadButtons.R1)
            ActionGuardGamepadButton = GamepadButtons.R2;

        ActionGuardDefaultMigratedToR2 = true;
        Save();
    }

    private void MigrateActionGuardVfxDefault()
    {
        if (ActionGuardVfxDefaultMigratedToHeavySwing)
            return;

        if (GuardSuccessVfxPath == "vfx/common/eff/dk05th_stdn0t.avfx")
            GuardSuccessVfxPath = "vfx/ws/wax_heavyswing/eff/wax_heavy1t0h.avfx";

        ActionGuardVfxDefaultMigratedToHeavySwing = true;
        Save();
    }

    private void MigrateActionBasicAttackDefaultButton()
    {
        if (ActionBasicAttackDefaultMigratedToSouth)
            return;

        if (ActionBasicAttackGamepadButton == GamepadButtons.East)
            ActionBasicAttackGamepadButton = GamepadButtons.South;

        ActionBasicAttackDefaultMigratedToSouth = true;
        Save();
    }

    private void MigrateActionMeleeRangeDefault()
    {
        if (ActionMeleeRangeDefaultMigratedTo5)
            return;

        if (MathF.Abs(PlayerHitboxRange - 4f) < 0.001f)
            PlayerHitboxRange = 5f;

        ActionMeleeRangeDefaultMigratedTo5 = true;
        Save();
    }

    private void MigrateActionAttackShapeDefaults()
    {
        if (ActionAttackShapeDefaultsMigrated20260630)
            return;

        if (MathF.Abs(PlayerHitboxRange - 4f) < 0.001f)
            PlayerHitboxRange = 5f;
        if (MathF.Abs(PlayerHitboxAngleDeg - 90f) < 0.001f)
            PlayerHitboxAngleDeg = 100f;
        if (MathF.Abs(RangedBasicRange - 22f) < 0.001f)
            RangedBasicRange = 25f;

        ActionAttackShapeDefaultsMigrated20260630 = true;
        Save();
    }

    private void MigrateEnemyDismembermentDefaults()
    {
        if (EnemyDismembermentDefaultsMigrated20260630)
            return;

        if (!EnableEnemyDismemberment &&
            EnemyHumanoidDismembermentCount == 3 &&
            MathF.Abs(EnemyMonsterDismembermentBonePercent - 50.0f) < 0.001f)
        {
            EnableEnemyDismemberment = true;
            EnemyHumanoidDismembermentCount = 0;
            EnemyMonsterDismembermentBonePercent = 100.0f;
        }

        EnemyDismembermentDefaultsMigrated20260630 = true;
        Save();
    }

    // Field testing showed the first fighting-AI spacing defaults made the enemy
    // unreachable (band too wide, retreat at full speed). Move configs still on the
    // old defaults to the retuned ones.
    private void MigrateFightingSpacingDefaults()
    {
        if (FightingSpacingDefaultsMigrated20260703)
            return;

        if (MathF.Abs(FightingAiMoveSpeed - 3.0f) < 0.001f)
            FightingAiMoveSpeed = 2.5f;
        if (MathF.Abs(FightingAiRangeMin - 1.8f) < 0.001f)
            FightingAiRangeMin = 0.9f;
        if (MathF.Abs(FightingAiRangeMax - 3.2f) < 0.001f)
            FightingAiRangeMax = 2.2f;
        if (MathF.Abs(FightingAiAttackCooldown - 2.5f) < 0.001f)
            FightingAiAttackCooldown = 1.1f;
        if (MathF.Abs(FightingAiAttackCooldownJitter - 1.0f) < 0.001f)
            FightingAiAttackCooldownJitter = 0.7f;
        if (MathF.Abs(FightingModeCameraMaxDistance - 3.0f) < 0.001f)
            FightingModeCameraMaxDistance = 3.5f;

        FightingSpacingDefaultsMigrated20260703 = true;
        Save();
    }

    // Knee hinge stack stiffened (see RagdollKneeHingeFrequency comment): 18 Hz predates
    // the substep solver and lets impacts wobble/tunnel the knee.
    private void MigrateKneeHingeStiffness()
    {
        if (KneeHingeStiffnessMigrated20260704)
            return;

        if (MathF.Abs(RagdollKneeHingeFrequency - 18f) < 0.001f)
            RagdollKneeHingeFrequency = 50f;

        KneeHingeStiffnessMigrated20260704 = true;
        Save();
    }

    // Field-tuned bone values promoted into the built-in Thinner Volumes profiles
    // (tighter hip cone/twist, tighter clavicle twist, slimmer upper arms, stronger
    // knee rest bias). The seeder only adds MISSING profiles, so saved copies in
    // existing configs must be patched here — targeted per-field so any other user
    // divergence in those profiles is preserved.
    private void MigrateThinnerProfileTuning()
    {
        if (ThinnerProfileTuningMigrated20260704)
            return;

        var changed = false;
        foreach (var profile in RagdollBoneProfiles)
        {
            if (profile?.Name != "Thinner Volumes I" && profile?.Name != "Thinner Volumes II")
                continue;
            if (profile.Bones == null)
                continue;

            foreach (var bone in profile.Bones)
            {
                switch (bone.Name)
                {
                    case "j_asi_a_l":
                    case "j_asi_a_r":
                        bone.SwingLimit = 0.95f;
                        bone.TwistMinAngle = -0.15f;
                        bone.TwistMaxAngle = 0.15f;
                        changed = true;
                        break;
                    case "j_sako_l":
                    case "j_sako_r":
                        bone.TwistMinAngle = -0.15f;
                        bone.TwistMaxAngle = 0.15f;
                        changed = true;
                        break;
                    case "j_ude_a_l":
                    case "j_ude_a_r":
                        bone.CapsuleRadius = 0.02f;
                        bone.BoxHalfExtentZ = 0.024f;
                        changed = true;
                        break;
                    case "j_asi_b_l":
                    case "j_asi_b_r":
                        bone.HingeRestSpringFreq = 3.5f;
                        bone.HingeRestMaxForce = 50f;
                        changed = true;
                        break;
                }
            }
        }

        ThinnerProfileTuningMigrated20260704 = true;
        if (changed)
            Save();
    }

    // Field testing verdict: the anatomical hinge axis, anatomical ROM, and
    // anthropometric mass experiments are only useful in specific setups — flip
    // configs still carrying the old ON defaults to OFF. (They stay available in
    // the GUI for those specific cases.)
    private void MigrateAnatomicalDefaultsOff()
    {
        if (AnatomicalDefaultsOffMigrated20260704)
            return;

        RagdollAnatomicalHingeAxis = false;
        RagdollAnatomicalRom = false;
        RagdollAnthropometricMass = false;

        AnatomicalDefaultsOffMigrated20260704 = true;
        Save();
    }

    /// <summary>
    /// The anatomical hinge was turned off — here and in every config that had already run — because it
    /// bent knees forward. That turned out not to be the idea's fault: it derived the hinge AXIS from
    /// anatomy and then took the axis's SIGN from the very pose-derived axis it was replacing, and on a
    /// straight leg that sign points the fold forward. Both are derived from anatomy now, so the verdict
    /// no longer holds and the migration that recorded it has to be lifted — otherwise the fix reaches
    /// nobody who ever ran the old build.
    ///
    /// Only the hinge is restored. The other two the old migration turned off are untouched: nothing has
    /// changed about them.
    /// </summary>
    private void MigrateAnatomicalHingeSignFix()
    {
        if (AnatomicalHingeSignFixMigrated)
            return;

        RagdollAnatomicalHingeAxis = true;
        AnatomicalHingeSignFixMigrated = true;
        Save();
    }

    private void MigrateNpcCollisionMode()
    {
        if (NpcCollisionModeMigrated20260705)
            return;

        RagdollNpcCollisionMode = RagdollNpcCollisionConvexHull
            ? RagdollNpcCollisionMode.ConvexHull
            : RagdollNpcCollisionMode.BoneCapsule;
        NpcCollisionModeMigrated20260705 = true;
        Save();
    }

    /// <summary>
    /// The skinned mesh is the default now: a corpse falling against a creature should meet the shape
    /// the player can see, not a stack of capsules standing in for it.
    ///
    /// Only configs still sitting on the OLD default are moved. Anyone who went and picked a mode
    /// deliberately picked it, and a change of default is no reason to overrule them.
    /// </summary>
    private void MigrateNpcCollisionMeshDefault()
    {
        if (NpcCollisionMeshDefaultMigrated)
            return;

        if (RagdollNpcCollisionMode == RagdollNpcCollisionMode.BoneCapsule)
            RagdollNpcCollisionMode = RagdollNpcCollisionMode.Mesh;

        NpcCollisionMeshDefaultMigrated = true;
        Save();
    }

    private void MigrateGuidedCollapse()
    {
        GuidedCollapse ??= new GuidedCollapseSettings();
        GuidedCollapse.Relaxation ??= new GuidedCollapseRelaxationSettings();
        GuidedCollapse.KneePowerLoss ??= new GuidedCollapseKneePowerLossSettings();
    }

    private void MigrateSplitVfxToggles()
    {
        if (!EnableSkillVfx)
            return;

        var changed = !EnableCharacterVfx || !EnableTargetVfx;
        EnableCharacterVfx = true;
        EnableTargetVfx = true;
        EnableSkillVfx = false;

        if (changed)
            Save();
    }

    /// <summary>
    /// Rewrites legacy skirt-bone parent references in the live RagdollBoneConfigs
    /// list and all saved RagdollBoneProfiles to use the chained per-radial-slot
    /// parenting (B-tier under matching A, C-tier under matching B) introduced
    /// when the spine-anchored layout was replaced. Saves only if something
    /// actually changed so this is cheap on every load.
    /// </summary>
    private void MigrateSkirtParentChains()
    {
        bool changed = false;
        if (MigrateBoneList(RagdollBoneConfigs)) changed = true;
        foreach (var profile in RagdollBoneProfiles)
            if (MigrateBoneList(profile.Bones)) changed = true;
        if (changed) Save();
    }

    private static bool MigrateBoneList(List<RagdollBoneConfig> bones)
    {
        bool changed = false;
        foreach (var bone in bones)
        {
            var remapped = RemapLegacySkirtParent(bone.Name, bone.SkeletonParent);
            if (remapped != bone.SkeletonParent)
            {
                bone.SkeletonParent = remapped;
                changed = true;
            }
        }
        return changed;
    }

    private static string? RemapLegacySkirtParent(string boneName, string? oldParent)
    {
        // Bones named j_sk_<pos>_<tier>_<side>. Tier 'b' under j_sebo_b and tier 'c'
        // under j_sebo_c are the legacy flat-to-spine parents we replaced.
        if (oldParent == null) return null;
        if (!boneName.StartsWith("j_sk_")) return oldParent;
        var parts = boneName.Split('_');
        if (parts.Length != 5) return oldParent;
        string pos = parts[2];
        string tier = parts[3];
        string side = parts[4];
        if (tier == "b" && oldParent == "j_sebo_b") return $"j_sk_{pos}_a_{side}";
        if (tier == "c" && oldParent == "j_sebo_c") return $"j_sk_{pos}_b_{side}";
        return oldParent;
    }

    private void MigrateRagdollProfileMetadata()
    {
        bool changed = false;
        if (MigrateRagdollBoneMetadata(RagdollBoneConfigs)) changed = true;
        foreach (var profile in RagdollBoneProfiles)
            if (MigrateRagdollBoneMetadata(profile.Bones)) changed = true;
        if (changed) Save();
    }

    private static bool MigrateRagdollBoneMetadata(List<RagdollBoneConfig> bones)
    {
        bool changed = false;
        foreach (var bone in bones)
        {
            var role = bone.AnatomicalRole;
            var shape = bone.ColliderShape;
            var bx = bone.BoxHalfExtentX;
            var by = bone.BoxHalfExtentY;
            var bz = bone.BoxHalfExtentZ;
            var swingMin = bone.SwingMinLimit;
            var restAngle = bone.HingeRestAngle;
            var restFreq = bone.HingeRestSpringFreq;
            var restForce = bone.HingeRestMaxForce;
            RagdollController.FillProfileDefaults(bone);
            if (role != bone.AnatomicalRole ||
                shape != bone.ColliderShape ||
                bx != bone.BoxHalfExtentX ||
                by != bone.BoxHalfExtentY ||
                bz != bone.BoxHalfExtentZ ||
                swingMin != bone.SwingMinLimit ||
                restAngle != bone.HingeRestAngle ||
                restFreq != bone.HingeRestSpringFreq ||
                restForce != bone.HingeRestMaxForce)
                changed = true;
        }
        return changed;
    }

    private void MigrateAnatomicalHinges()
    {
        bool changed = false;
        if (MigrateAnatomicalHingeList(RagdollBoneConfigs)) changed = true;
        foreach (var profile in RagdollBoneProfiles)
            if (MigrateAnatomicalHingeList(profile.Bones)) changed = true;
        if (changed) Save();
    }

    /// <summary>
    /// Promote the shorter knee/shin collision box into every saved profile and the live
    /// advanced-bone list. Built-in profiles are copied into user config on first install,
    /// so changing only the embedded JSON would leave every existing installation on 0.11.
    /// </summary>
    private void MigrateRagdollShinBoxHalfY()
    {
        if (RagdollShinBoxHalfYMigrated20260716)
            return;

        SetRagdollShinBoxHalfY(RagdollBoneConfigs);
        foreach (var profile in RagdollBoneProfiles)
            if (profile?.Bones != null)
                SetRagdollShinBoxHalfY(profile.Bones);

        RagdollShinBoxHalfYMigrated20260716 = true;
        // Save even if no profiles exist yet, so the one-shot migration is recorded. A new
        // install is seeded later in Initialize from the already-updated embedded profiles.
        Save();
    }

    private static bool SetRagdollShinBoxHalfY(List<RagdollBoneConfig> bones)
    {
        var changed = false;
        foreach (var bone in bones)
        {
            if (bone.Name is not ("j_asi_b_l" or "j_asi_b_r") ||
                MathF.Abs(bone.BoxHalfExtentY - 0.035f) < 0.0001f)
                continue;

            bone.BoxHalfExtentY = 0.035f;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Promote the current field-tuned Default profile into installations that still carry
    /// the previous built-in values. Match each old value exactly so a user's own Default
    /// profile remains untouched wherever it has already diverged.
    /// </summary>
    private void MigrateDefaultProfileTuning()
    {
        if (RagdollDefaultProfileTuningMigrated20260716)
            return;

        var profile = RagdollBoneProfiles.FirstOrDefault(p =>
            p?.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) == true);
        if (profile?.Bones != null)
        {
            foreach (var bone in profile.Bones)
            {
                if (bone.Name is "j_sako_l" or "j_sako_r")
                {
                    if (MathF.Abs(bone.TwistMinAngle - (-0.25f)) < 0.0001f)
                    {
                        bone.TwistMinAngle = -0.15f;
                    }
                    if (MathF.Abs(bone.TwistMaxAngle - 0.25f) < 0.0001f)
                    {
                        bone.TwistMaxAngle = 0.15f;
                    }
                }
                else if (bone.Name is "j_asi_b_l" or "j_asi_b_r")
                {
                    if (bone.HingeRestSpringFreq is { } frequency &&
                        MathF.Abs(frequency - 1.2f) < 0.0001f)
                    {
                        bone.HingeRestSpringFreq = 3.5f;
                    }
                    if (bone.HingeRestMaxForce is { } force &&
                        MathF.Abs(force - 10f) < 0.0001f)
                    {
                        bone.HingeRestMaxForce = 50f;
                    }
                }
            }
        }

        RagdollDefaultProfileTuningMigrated20260716 = true;
        // Record the one-shot even on a fresh install; its profile is seeded immediately
        // afterwards from the already-updated embedded resource.
        Save();
    }

    private static bool MigrateAnatomicalHingeList(List<RagdollBoneConfig> bones)
    {
        bool changed = false;
        foreach (var bone in bones)
        {
            var role = (RagdollController.AnatomicalRole)bone.AnatomicalRole;
            var isLowerLimb = bone.Name.StartsWith("j_asi_b_", StringComparison.Ordinal);
            var isForearm = bone.Name.StartsWith("j_ude_b_", StringComparison.Ordinal);
            var isUpperArm = bone.Name.StartsWith("j_ude_a_", StringComparison.Ordinal);
            var isClavicle = bone.Name.StartsWith("j_sako_", StringComparison.Ordinal);
            if ((role == RagdollController.AnatomicalRole.Knee || role == RagdollController.AnatomicalRole.Elbow ||
                 isLowerLimb || isForearm) &&
                bone.JointType != (int)RagdollController.JointType.Hinge)
            {
                bone.JointType = (int)RagdollController.JointType.Hinge;
                changed = true;
            }

            var minSwingFloor = isLowerLimb || role == RagdollController.AnatomicalRole.Knee
                ? 0.75f
                : isForearm || role == RagdollController.AnatomicalRole.Elbow
                    ? 0.45f
                    : 0f;
            if (minSwingFloor > 0 && (bone.SwingMinLimit == null || bone.SwingMinLimit < minSwingFloor))
            {
                bone.SwingMinLimit = minSwingFloor;
                changed = true;
            }

            if (isLowerLimb || isForearm)
            {
                if (bone.ColliderShape != (int)RagdollController.RagdollColliderShape.Box)
                {
                    bone.ColliderShape = (int)RagdollController.RagdollColliderShape.Box;
                    changed = true;
                }

                var minX = isLowerLimb ? 0.042f : 0.030f;
                var minY = isLowerLimb ? 0.035f : 0.060f;
                var minZ = isLowerLimb ? 0.030f : 0.022f;
                if (bone.BoxHalfExtentX < minX) { bone.BoxHalfExtentX = minX; changed = true; }
                if (bone.BoxHalfExtentY < minY) { bone.BoxHalfExtentY = minY; changed = true; }
                if (bone.BoxHalfExtentZ < minZ) { bone.BoxHalfExtentZ = minZ; changed = true; }
            }

            if (isForearm || role == RagdollController.AnatomicalRole.Elbow)
            {
                if (bone.TwistMinAngle > -1.25f) { bone.TwistMinAngle = -1.25f; changed = true; }
                if (bone.TwistMaxAngle < 1.25f) { bone.TwistMaxAngle = 1.25f; changed = true; }
                if (bone.HingeRestAngle == null || MathF.Abs(bone.HingeRestAngle.Value - MathF.PI / 2) < 0.01f) { bone.HingeRestAngle = 0f; changed = true; }
                if (bone.HingeRestSpringFreq == null || bone.HingeRestSpringFreq <= 0f || bone.HingeRestSpringFreq >= 2.0f) { bone.HingeRestSpringFreq = 1.5f; changed = true; }
                if (bone.HingeRestMaxForce == null || bone.HingeRestMaxForce <= 0f || bone.HingeRestMaxForce >= 8.0f) { bone.HingeRestMaxForce = 6.0f; changed = true; }
            }

            if (isLowerLimb || role == RagdollController.AnatomicalRole.Knee)
            {
                if (bone.HingeRestAngle == null || MathF.Abs(bone.HingeRestAngle.Value - MathF.PI / 2) < 0.01f) { bone.HingeRestAngle = 0f; changed = true; }
                if (bone.HingeRestSpringFreq == null || bone.HingeRestSpringFreq <= 0f) { bone.HingeRestSpringFreq = 1.2f; changed = true; }
                if (bone.HingeRestMaxForce == null || bone.HingeRestMaxForce <= 0f) { bone.HingeRestMaxForce = 10.0f; changed = true; }
            }

            if (isClavicle)
            {
                if (bone.SwingLimit < 0.35f) { bone.SwingLimit = 0.35f; changed = true; }
                if (bone.TwistMinAngle < -0.25f || bone.TwistMinAngle > -0.05f) { bone.TwistMinAngle = -0.25f; changed = true; }
                if (bone.TwistMaxAngle > 0.25f || bone.TwistMaxAngle < 0.05f) { bone.TwistMaxAngle = 0.25f; changed = true; }
            }

            if (isUpperArm)
            {
                if (bone.SwingLimit > 1.35f) { bone.SwingLimit = 1.35f; changed = true; }
                if (bone.TwistMinAngle < -0.65f || bone.TwistMinAngle > -0.20f) { bone.TwistMinAngle = -0.65f; changed = true; }
                if (bone.TwistMaxAngle > 0.65f || bone.TwistMaxAngle < 0.20f) { bone.TwistMaxAngle = 0.65f; changed = true; }
                if (bone.ColliderShape != (int)RagdollController.RagdollColliderShape.Box)
                {
                    bone.ColliderShape = (int)RagdollController.RagdollColliderShape.Box;
                    changed = true;
                }

                if (bone.BoxHalfExtentX < 0.032f) { bone.BoxHalfExtentX = 0.032f; changed = true; }
                if (bone.BoxHalfExtentY < 0.075f) { bone.BoxHalfExtentY = 0.075f; changed = true; }
                if (bone.BoxHalfExtentZ < 0.024f) { bone.BoxHalfExtentZ = 0.024f; changed = true; }
            }
        }
        return changed;
    }

    private static readonly Dictionary<string, string> LegacyBoneProfileNameMap = new()
    {
        { "Thickness",     "Default"            },
        { "Flatter",       "Thinner Volumes I"  },
        { "Complete Flat", "Thinner Volumes II" },
    };

    private void RenameLegacyBoneProfiles()
    {
        bool changed = false;
        foreach (var profile in RagdollBoneProfiles)
        {
            if (LegacyBoneProfileNameMap.TryGetValue(profile.Name, out var newName))
            {
                profile.Name = newName;
                changed = true;
            }
        }
        if (changed) Save();
    }

    private void SeedBuiltInBoneProfiles()
    {
        List<RagdollBoneProfile>? builtIns;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("CombatSimulator.Resources.BuiltInBoneProfiles.json");
            if (stream == null) return;
            using var reader = new System.IO.StreamReader(stream);
            var json = reader.ReadToEnd();
            builtIns = System.Text.Json.JsonSerializer.Deserialize<List<RagdollBoneProfile>>(json);
        }
        catch
        {
            return;
        }
        if (builtIns == null) return;

        bool changed = false;
        foreach (var seed in builtIns)
        {
            if (RagdollBoneProfiles.Any(p =>
                    p.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            RagdollBoneProfiles.Add(seed);
            changed = true;
        }
        if (changed) Save();
    }
}
