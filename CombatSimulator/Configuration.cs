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
    // Tier B — Anatomy-fixed knee/elbow hinge axis. When on, the hinge axis is derived
    // from the skeleton medial-lateral (character RIGHT) axis projected perpendicular to
    // the bone, instead of Cross(thighDir, shinDir) which is degenerate for a near-straight
    // limb and produced an asymmetric (one knee sideways, one forward) axis at death.
    // Default OFF after field testing: only useful in specific setups.
    public bool RagdollAnatomicalHingeAxis { get; set; } = false;
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
    // Double-jump vault: a second jump press mid-air carries the player over the enemy.
    public int FightingModeVaultKey { get; set; } = 0x20;   // Space
    public float FightingModeVaultDuration { get; set; } = 0.45f;
    // Fighting Mode guard: reuses the shared PlayerGuardController timing windows
    // (GuardActiveWindow / chain / recovery) with its own key binding.
    public int FightingModeGuardKey { get; set; } = 17;      // Ctrl
    public GamepadButtons FightingModeGuardGamepadButton { get; set; } = GamepadButtons.East;
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

    // Hit feedback ("game feel" / 打击感) on a confirmed player attack: brief target hitstop + a
    // camera punch + an optional hit spark. All gated below.
    public bool EnableHitFeedback { get; set; } = true;
    // Delay (seconds) between the attack input and the impact feedback firing. The swing has ~0.5s of
    // windup before the weapon visually CONNECTS, so the hitstop/camera/spark must wait or they fire
    // at swing start and feel disconnected. ~0.45 lines up with the contact frame; tune to taste.
    public float HitFeedbackDelay { get; set; } = 0.45f;
    // Hitstop: freeze the struck target's animation for this many ms on impact ("刀刀到肉"). The single
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
        FightingModeGuardKey = defaults.FightingModeGuardKey;
        FightingModeGuardGamepadButton = defaults.FightingModeGuardGamepadButton;
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
        FightingModeVaultKey = defaults.FightingModeVaultKey;
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
