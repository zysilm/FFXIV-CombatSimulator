using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Animation;
using CombatSimulator.Dev;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.GamePad;

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
}

[Serializable]
public class GuidedCollapseKneePowerLossSettings
{
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
    public float KneeBuckleFlexForce { get; set; } = 82f;
    public float KneeTorsoFlexForce { get; set; } = 42f;
    public float BuckleFootSupportForce { get; set; } = 1100f;
    public float TorsoFootSupportForce { get; set; } = 650f;
    public float BucklePelvisForce { get; set; } = 420f;
    public float TorsoPelvisForce { get; set; } = 220f;
    public float ChestPitchDegrees { get; set; } = 41f;
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

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ActionGuardDefaultMigratedToR2 { get; set; } = false;
    public bool ActionGuardVfxDefaultMigratedToHeavySwing { get; set; } = false;
    public bool ActionBasicAttackDefaultMigratedToSouth { get; set; } = false;

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

    // Shortcuts bar
    public bool ShowShortcuts { get; set; } = false;
    public bool ShowProfessionalWindow { get; set; } = false;
    public bool ShowFastCombatToolbar { get; set; } = false;
    public bool ShowDefeatRevivePopup { get; set; } = true;
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

    // Custom in-sim targeting (综合提升): during an active simulation the plugin
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
    // instead of pumping energy. Costs ~linearly in performance. Takes effect on next
    // ragdoll activation.
    public int RagdollSolverSubsteps { get; set; } = 1;
    // Spring frequency (Hz) of the joint LIMIT walls (swing cones + twist ranges), not
    // the positional joints. Higher = firmer wall so joints don't blow past their range
    // under momentum (e.g. shoulders/waist over-rotating); too high relative to the
    // 60Hz step can over-drive the solver into jitter. 60 = long-standing soft wall,
    // 120 = very firm (needs substeps to stay stable), 90 = balanced default. Takes
    // effect on next ragdoll activation.
    public float RagdollLimitSpringFrequency { get; set; } = 90f;
    // Anatomical joint-frame builder for hinge axes and ball-joint twist references.
    // Keep the switch so unusual skeletons can fall back to the legacy frame builder.
    public bool RagdollExperimentalJointFrames { get; set; } = true;
    public bool RagdollSelfCollision { get; set; } = true; // Body parts collide with each other (arms vs torso, etc)
    public float RagdollFriction { get; set; } = 1.0f; // Surface friction (0=ice, 1=grippy). Lower = limbs slide more realistically.

    // Death collapse — physics-driven "muscle relaxation" on death (active-ragdoll relaxation
    // family). When enabled, a dying body holds its standing death-instant pose briefly
    // ("muscle tone") then fades limp via per-joint AngularServos, optionally with a directional
    // topple impulse. Layered on top of normal ragdoll activation (needs EnableRagdoll). Directed
    // postures (kneeling) are intentionally out of scope — see DEATH_COLLAPSE_RESEARCH.md.
    public bool DeathCollapseEnabled { get; set; } = false;
    public int DeathCollapseArchetype { get; set; } = 1;        // 0=StiffHold (stays rigid), 1=UniformCollapse (relax limp)
    public float DeathCollapseStrength { get; set; } = 14f;     // servo spring freq (Hz); torque ceiling scales with it
    public float DeathCollapseHold { get; set; } = 0.3f;        // seconds holding the death pose before fading
    public float DeathCollapseFade { get; set; } = 0.9f;        // seconds to fade muscle tone to zero
    public float DeathCollapseHingeSoften { get; set; } = 0.25f;// knee/elbow servo fraction (lower = limbs yield)
    public int DeathCollapseDirection { get; set; } = 1;        // 0=None,1=Random,2=Forward,3=Backward,4=Sideways
    public float DeathCollapseImpulse { get; set; } = 2.0f;     // topple nudge speed (m/s) at the chest
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
    // Ragdoll debug overlay — renders capsules and joint limits in 3D
    public bool RagdollDebugOverlay { get; set; } = false;
    // Ragdoll bone configs (Advanced) — per-bone physics parameters
    // Empty = use built-in defaults from RagdollController.DefaultBoneDefs
    public List<RagdollBoneConfig> RagdollBoneConfigs { get; set; } = new();

    // Saved ragdoll bone profiles (snapshots of the per-bone advanced configs)
    public List<RagdollBoneProfile> RagdollBoneProfiles { get; set; } = new();

    // Dev (Experimental) — hidden behind easter egg
    public bool RagdollVerboseLog { get; set; } = false;
    public bool RagdollFollowPosition { get; set; } = false; // Update GameObject.Position to follow ragdoll root (prevents model unload on long falls)
    public bool RagdollLiftUndergroundBonesOnStart { get; set; } = false;
    public bool EnableVictorySequence { get; set; } = false;
    public bool ControlGrabber { get; set; } = false;
    public float GrabberControlSpeed { get; set; } = 2.5f;
    // Custom primary (grabber) NPC: when on, an alive enemy whose name contains
    // GrabCustomPrimaryName is chosen as the cinematic/grab NPC (random among
    // matches); otherwise the last-targeted enemy is used.
    public bool GrabCustomPrimary { get; set; } = false;
    public string GrabCustomPrimaryName { get; set; } = string.Empty;
    public List<VictorySequenceStage> VictorySequenceStages { get; set; } = new();
    public List<VictorySequenceStage> VictorySequenceOtherStages { get; set; } = new();
    public List<VictoryCinematicPreset> VictoryCinematicPresets { get; set; } = new();
    public float DevNpcScale { get; set; } = 1.0f;
    public bool ShowGrabToolbar { get; set; } = false;
    public bool ShowHoldToolbar { get; set; } = false;

    // ── KO Strip (dev experimental) ──────────────────────────────────────────
    // Visually unequips selected gear slots on knockout (dramatic fighting-game
    // style). Tier 1: pure visual unequip (swap slot model to smallclothes), no
    // physics drop. Draw-object only — no server packets, restored on reset.
    public bool ShowKoStripToolbar { get; set; } = false;
    public bool KoStripEnabled { get; set; } = false;

    // ── Monster mode (dev experimental) ──────────────────────────────────────
    // On player death, spawn a controllable no-HP creature (default Bat) that flies around
    // and can punt the ground ragdoll with an adjustable impulse.
    public bool ShowMonsterToolbar { get; set; } = false;
    public bool MonsterSpawnOnDeath { get; set; } = false;
    public bool MonsterControlKiller { get; set; } = false; // on death, control the enemy that killed you (instead of spawning)
    public uint MonsterModelId { get; set; } = 38;      // BNpcBaseId — 38 = Bat
    public uint MonsterModelNameId { get; set; } = 38;  // BNpcNameId
    public float MonsterMoveSpeed { get; set; } = 6f;
    public float MonsterVerticalSpeed { get; set; } = 2.5f;
    public int MonsterAttackKey { get; set; } = 0x59; // VirtualKey.Y — adjustable normal-attack key
    public bool MonsterCameraFollowsMonster { get; set; } = true; // remembered cam target (monster vs character)
    // Strike: during an attack the monster's collider bones impart their swing velocity (× power) to
    // nearby ragdoll bodies, so the hit lands forcefully at the limb's real contact point.
    public float MonsterStrikePower { get; set; } = 0.1f;
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

    public float HoldStandingHeight { get; set; } = 0.92f;
    public string HoldAnchorBone { get; set; } = "j_kosi";
    public bool HoldNpcAttack { get; set; } = true;
    public bool HoldAllNpcsAttack { get; set; } = false;
    public float HoldApproachDistance { get; set; } = 1.0f;
    public uint HoldAttackEmoteId { get; set; } = 0; // 0 = "Atk" (melee anim), else emote RowId
    public bool HoldDetailedBoneMode { get; set; } = false;
    public bool HoldShakeEnabled { get; set; } = false;
    public float HoldShakeIntensity { get; set; } = 3.0f;
    public bool HoldBindArms { get; set; } = false;
    public float HoldArmSpread { get; set; } = 0.8f;
    public float HoldArmHeight { get; set; } = 1.2f;
    public bool HoldGrabEnabled { get; set; } = false;
    public string HoldGrabNpcBone { get; set; } = "j_te_r";
    public string HoldGrabPlayerBone { get; set; } = "j_kubi";
    public float HoldGrabForce { get; set; } = 800f;
    public float HoldGrabFreq { get; set; } = 120f;
    public bool DevNpcOcclusionHide { get; set; } = false;
    public float DevNpcOcclusionRadius { get; set; } = 1.0f;
    public bool DevCompanionAppearanceVariant { get; set; } = false;
    public bool DevPartyApproachDebugLog { get; set; } = false;
    public bool RagdollNpcCollision { get; set; } = true;
    public bool RagdollNpcCollisionAutoSize { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    public bool RagdollNpcCollisionConvexHull { get; set; } = false;
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

    // Fighting Camera (1v1): frame the player + locked target together with auto-zoom,
    // then on either death transition to the dead character's bone (active-cam follow).
    public bool ActiveCameraFightingMode { get; set; } = false;
    public float ActiveCameraFightingTransitionDuration { get; set; } = 1.0f;
    public float ActiveCameraFightingZoomMargin { get; set; } = 1.3f;
    public float ActiveCameraFightingMinDistance { get; set; } = 2.5f;
    public float ActiveCameraFightingMaxDistance { get; set; } = 15.0f;
    public float ActiveCameraFightingSmoothing { get; set; } = 8.0f;
    public float ActiveCameraFightingHeightOffset { get; set; } = 0f;
    public string ActiveCameraFightingBoneName { get; set; } = "j_sebo_b";

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

    // Recent NPCs (for spawn enemy UI)
    public List<uint> RecentNpcIds { get; set; } = new();
    public List<RecentNpcEntry> RecentNpcEntries { get; set; } = new();

    // ── Action Mode (动作模式) ──────────────────────────────────────────────
    // A toggleable real-time action layer that replaces the tab-target / GCD
    // interaction. Player attacks/guard come from hotbar actions remapped by
    // actionId (the game resolves keyboard/gamepad for us); enemy attacks
    // telegraph (起手快照) then resolve by hitbox at the active frame. When OFF,
    // every seam falls back to the original simulation behavior.
    public bool ActionMode { get; set; } = false;

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
    public float GuardRecovery { get; set; } = 0.35f;      // lockout after a guard CHAIN ends
    public float GuardCooldown { get; set; } = 0.15f;      // min time between guard attempts
    public ushort GuardTimelineId { get; set; } = 0;       // 0 = auto/fallback
    public string GuardSuccessVfxPath { get; set; } = "vfx/ws/wax_heavyswing/eff/wax_heavy1t0h.avfx";
    public string EnemyTelegraphVfxPath { get; set; } = "vfx/common/eff/cmhit_fire1t.avfx";

    // Player light-combo + hitbox tuning
    public float LightComboWindow { get; set; } = 0.6f;    // time to chain the next swing
    public float LightSwingInterval { get; set; } = 0.4f;  // min time between swings (cadence)
    public float PlayerHitboxRange { get; set; } = 4f;     // melee basic-attack reach (yalms)
    public float PlayerHitboxAngleDeg { get; set; } = 90f; // melee selection cone full angle
    public int LightAttackPotency { get; set; } = 120;
    // Soft-target selection: ranged basic attack / ranged skills use a longer, wider selection cone
    // and pick the smallest-angle enemy (not the nearest).
    public float RangedBasicRange { get; set; } = 22f;
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
        MigrateActionGuardDefaultButton();
        MigrateActionGuardVfxDefault();
        MigrateActionBasicAttackDefaultButton();
        MigrateGuidedCollapse();
        RenameLegacyBoneProfiles();
        SeedBuiltInBoneProfiles();
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    public void ResetGuidedCollapseDefaults(bool preserveEnabled = false)
    {
        var enabled = GuidedCollapse.Enabled;
        GuidedCollapse.ResetDefaults();
        if (preserveEnabled)
            GuidedCollapse.Enabled = enabled;
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

    private void MigrateGuidedCollapse()
    {
        GuidedCollapse ??= new GuidedCollapseSettings();
        GuidedCollapse.Relaxation ??= new GuidedCollapseRelaxationSettings();
        GuidedCollapse.KneePowerLoss ??= new GuidedCollapseKneePowerLossSettings();

        if (DeathCollapseEnabled && !GuidedCollapse.Enabled)
        {
            GuidedCollapse.Enabled = true;
            GuidedCollapse.Mode = 0;
            GuidedCollapse.Relaxation.Archetype = DeathCollapseArchetype;
            GuidedCollapse.Relaxation.Strength = DeathCollapseStrength;
            GuidedCollapse.Relaxation.Hold = DeathCollapseHold;
            GuidedCollapse.Relaxation.Fade = DeathCollapseFade;
            GuidedCollapse.Relaxation.HingeSoften = DeathCollapseHingeSoften;
            GuidedCollapse.Relaxation.Direction = DeathCollapseDirection;
            GuidedCollapse.Relaxation.Impulse = DeathCollapseImpulse;
            Save();
        }
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
                var minY = isLowerLimb ? 0.090f : 0.060f;
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
