// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
    // Soft body spring settings (for breast/jiggle bones). Split: stiff translation (the socket
    // only carries flesh weight, ~mm of sag), soft rotation (the jiggle swings about the pivot).
    public bool SoftBody { get; set; }             // use soft springs + AngularServo instead of rigid + AngularMotor
    public float SoftSpringFreq { get; set; } = 10f;   // BallSocket spring frequency (Hz); static sag = g/(2πf)²
    public float SoftSpringDamp { get; set; } = 1f;    // BallSocket damping ratio, critical = no translational ring
    public float SoftServoFreq { get; set; } = 3f;     // AngularServo spring frequency (Hz), the jiggle rate
    public float SoftServoDamp { get; set; } = 0.2f;   // AngularServo damping ratio, lower = more swings before settling
}

[Serializable]
public class GuidedCollapseSettings
{
    // Knee buckle on death. The knee power-loss pattern is the only guided collapse; its tuning is
    // fixed at the GuidedCollapseKneePowerLossSettings defaults.
    public bool Enabled { get; set; } = false;
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
    public float BuckleMinDuration { get; set; } = 0.24f;
    public float BuckleTimeout { get; set; } = 0.95f;
    public float BucklePelvisDropToTorso { get; set; } = 0.30f;
    public float BuckleKneeAngleToTorso { get; set; } = 22f;
    public float TorsoMinDuration { get; set; } = 0.55f;
    public float TorsoTimeout { get; set; } = 0.90f;
}

[Serializable]
public class RecentNpcEntry
{
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
    public uint ModelCharaId { get; set; }
    public Npcs.NpcCatalogType Type { get; set; } = Npcs.NpcCatalogType.BNpc;
    public Npcs.NpcCatalogSource Source { get; set; } = Npcs.NpcCatalogSource.BNpcBase;
}

public enum RagdollNpcCollisionMode
{
    BoneCapsule = 0,
    Mesh = 2,
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
    public bool ActionBasicAttackDefaultMigratedToSouth { get; set; } = false;
    public bool ActionAttackShapeDefaultsMigrated20260630 { get; set; } = false;
    public bool EnemyDismembermentDefaultsMigrated20260630 { get; set; } = false;
    public bool FightingSpacingDefaultsMigrated20260703 { get; set; } = false;
    public bool NpcCollisionMeshDefaultMigrated { get; set; } = false;
    public bool NpcCollisionConvexHullDefaultMigrated20260716 { get; set; } = false;
    public bool RagdollLegModelRebuildMigrated20260816 { get; set; } = false;
    public bool EnemyTacticsMeleeCapMigrated20261102 { get; set; } = false;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public float SidebarWidth { get; set; } = 130f;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;
    public bool ShowDamageNumbers { get; set; } = true;
    public bool ShowPlayerHpBar { get; set; } = true;
    public bool ShowHudPlayerHpBar { get; set; } = false;
    public bool AnonymousMode { get; set; } = false;
    // Names and numbers on the bars of enemies and allies too (the player's own bar always has them).
    // Anonymous Mode hides every name and number, whatever this says.
    public bool ShowAllBarLabels { get; set; } = false;
    // The battle's result across the screen (and why), kept until the battle is reset.
    public bool ShowResultBanner { get; set; } = true;

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // Party companions: client-side clones of visible players that fight
    // simulated enemies as friendly actors.
    public bool EnableCombatCompanions { get; set; } = true;
    public int CombatCompanionMaxCount { get; set; } = 10;
    public bool SensePartyMembers { get; set; } = true;
    // Party formation distances (yalms). Fixed; they also set how close enemies hold to their target
    // (NpcAiController), so changing them moves the whole fight.
    [JsonIgnore] public float PartyCommandRange => 10f;
    [JsonIgnore] public float PartyCommandRangeRandomness => 0.15f;
    [JsonIgnore] public float PartyMeleeAttackRange => 1.2f;
    [JsonIgnore] public float PartyRangedAttackRange => 10.5f;
    public bool PartyCompanionDeathRagdoll { get; set; } = true;

    // NPC Defaults
    // How strong enemies are: index into CombatBudgetModel.StrengthChoices. 0 reads it from the enemy's
    // own data (ordinary, notorious, boss); 1-5 force Weak, Normal, Strong, Elite, Boss.
    // Applies to every enemy: spawned, map-sensed, auto-selected and those a recipe starts with.
    public int EnemyStrength { get; set; }
    // Enemy level, 1-100; 0 follows the player's level. Only the difference to the player matters.
    public int EnemyLevel { get; set; }
    public float DefaultNpcHeightOffset { get; set; } = 0f; // Y offset added to spawn position

    // Humanoid enemies (weapon resolves to a job) cast their real job kit: the strongest N damage
    // actions at the sim enemy level. Monsters / NPC-only weapons resolve to no job → auto-attack only.
    public int EnemyMaxRealSkills { get; set; } = 4;


    // Glamourer: equipment-only design applied to party companion clones on spawn.
    // "" = None — the clone keeps its own (inherited) equipment. Also the effective
    // behavior when Glamourer is not installed.
    public string PartyCompanionGlamourerDesignId { get; set; } = "";

    // Armor Detachment (Ragdoll tab). "Detach armor" is one dropdown over these two exclusive switches.
    public bool KoStripEnabled { get; set; } = false;
    public bool KoStripOnHitEnabled { get; set; } = false;
    public bool KoStripHead { get; set; } = true;
    public bool KoStripBody { get; set; } = true;
    public bool KoStripHands { get; set; } = false;
    public bool KoStripLegs { get; set; } = true;
    public bool KoStripFeet { get; set; } = false;
    public bool KoStripEars { get; set; } = false;
    public bool KoStripNeck { get; set; } = false;
    public bool KoStripWrists { get; set; } = false;
    public bool KoStripRFinger { get; set; } = false;
    public bool KoStripLFinger { get; set; } = false;

    // Physically drop removed pieces instead of just hiding them. The UI sets both together; separate
    // fields remain because hats/accessories and clothing take different runtime paths.
    public bool KoStripPhysicsDrop { get; set; } = true;
    public bool KoStripPhysicsDropClothing { get; set; } = true;

    // Auto-recycle: despawn a physics-dropped piece a fixed time after it settles, rather than letting
    // it linger until reset/zone-change. Off by default — a stale dropped piece surviving into a zone
    // transition is the known crash exposure this exists to reduce.
    public bool KoStripCloneAutoExpireEnabled { get; set; } = false;
    public float KoStripCloneAutoExpireSeconds { get; set; } = 2f;

    // Pieces still attached to the body travel with it when the body is moved as a whole. Pieces that
    // have already come away stay put; which is which is decided by whether the piece still wraps the
    // bones it was built around, not by how near the body it happens to lie.
    public bool KoStripGarmentFollowsBody { get; set; } = true;

    // Body/Legs garment behaviour. Stored values keep their historic numbering:
    // 1 drop when the body settles, 5 Visual only (enhanced).
    public const int KoStripClothHoldPresetDefault = 1;
    public int KoStripClothHoldPreset { get; set; } = KoStripClothHoldPresetDefault;

    // Half off, hanging on the body (preset 5): which parts of the top / lower garment stay on the body
    // (sums of the GarmentTether.Top* / Lower* bits) and how hard the rest is pulled down (index into
    // GarmentTether.PullLabels).
    public int KoStripTopHolds { get; set; } = Animation.Attachment.GarmentTether.DefaultTopHold;
    public int KoStripLowerHolds { get; set; } = Animation.Attachment.GarmentTether.DefaultLowerHold;
    public int KoStripHangPull { get; set; } = Animation.Attachment.GarmentTether.DefaultPull;

    // Unselected previous implementation of preset 5 (whole-garment slide): how far the top and the lower garment slide down from their worn
    // position, in metres on a scale-one actor. The top never travels more than 0.08 m and the lower
    // garment never more than 65% of the thigh, whatever is set here.
    public float KoStripTopSlideDistance { get; set; } = 0.09f;
    public float KoStripLowerSlideDistance { get; set; } = 0.13f;

    /// <summary>
    /// Named like Armor Detachment, but not its to reset.
    /// </summary>
    private static readonly HashSet<string> ArmorDetachmentResetExclusions = new(StringComparer.Ordinal)
    {
        // The master switch. Resetting it would turn the whole feature off and take the page with
        // it, which reads as the button having broken something rather than having restored it —
        // same reasoning as EnableRagdoll on the Ragdoll page (see RagdollPageResetExclusions).
        nameof(KoStripEnabled),
        nameof(KoStripOnHitEnabled),
    };

    private static bool IsArmorDetachmentOption(string name)
    {
        if (ArmorDetachmentResetExclusions.Contains(name)) return false;
        return name.StartsWith("KoStrip", StringComparison.Ordinal);
    }

    /// <summary>
    /// Restore every Armor Detachment option (slots, physics drop, garment tube, cloth hold, and the
    /// per-slot collapse mask) to defaults, by reflection against a fresh Configuration — same
    /// approach and reasoning as ResetRagdollPageDefaults.
    /// </summary>
    public void ResetArmorDetachmentDefaults()
    {
        var defaults = new Configuration();

        foreach (var property in typeof(Configuration).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.GetIndexParameters().Length > 0) continue;
            if (!IsArmorDetachmentOption(property.Name)) continue;

            property.SetValue(this, property.GetValue(defaults));
        }

        Save();
    }

    // Shortcuts bar
    public bool ShowShortcuts { get; set; } = false;
    public bool ShowProfessionalWindow { get; set; } = false;
    public bool ShowFastCombatToolbar { get; set; } = false;
    public bool ShowDefeatRevivePopup { get; set; } = true;
    public string FastCombatRecipeName { get; set; } = "";

    // Target Formation
    public bool EnableNpcTargetPlayer { get; set; } = true;
    // Explicit gameplay range override. Visual NPC Scale still supplies the model-relative melee
    // baseline; this multiplier lets users restore or enlarge reach for very small actors.
    public float NpcAttackRangeScale { get; set; } = 1.0f;

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
    public bool EnableRagdoll { get; set; } = true;
    public float RagdollActivationDelay { get; set; } = 1.0f;
    // When physics takes over from the death animation: index into RagdollTrigger.Labels. 0 waits
    // RagdollActivationDelay seconds; the others find the moment from the animation or the fall.
    public int RagdollTriggerMode { get; set; } = 5; // When the body has stopped moving
    // Extend terrain detection: also build ground collision patches under nearby
    // enemies (not just the death spot) so a victory-sequence grab that drags the
    // body onto an enemy doesn't fall through the floor on release. Costs extra
    // raycasts/triangles at activation, so default off.
    public bool ExtendTerrainDetection { get; set; } = false;
    // Build the ragdoll's ground from the game's own collision triangles instead of a 1 m grid of heights
    // sampled from them. Follows steps, edges and props exactly; costs more when a patch is built.
    public bool RagdollExactGroundCollision { get; set; } = true;
    // Bodies in water get buoyancy and drag (WaterBuoyancy) and float on the game's water surfaces.
    public bool RagdollWaterBuoyancy { get; set; } = true;
    // Which liquid the water behaves as: index into WaterBuoyancy.LiquidLabels (0 water, 1 dense liquid).
    public int RagdollWaterLiquid { get; set; } = 0;
    // NPC death ragdoll
    public bool EnableNpcDeathRagdoll { get; set; } = true;
    // Off: other characters' corpses (enemies and allies) take the light physics budget and their
    // dropped weapons land on simple body shapes. On: the same detail as the player's own.
    public bool NpcRagdollFineDetail { get; set; } = false;
    // Read by the private module; the ragdoll's gravity is fixed (RagdollTuning.Gravity).
    [JsonIgnore] public float RagdollGravity => RagdollTuning.Gravity;
    // Directional/asymmetric range-of-motion limits (Tier C). Default off after field testing and no
    // longer in the UI; the code stays pending in-game validation of the swing-ROM split fix.
    public bool RagdollAnatomicalRom { get; set; } = false;

    // Dismemberment POC: while the player ragdoll is active, collapse each selected limb's bone
    // subtree to ~0 scale so it vanishes from the body. Multi-select: stores the root bone name of
    // every severed part (e.g. "j_kao", "j_ude_b_l"). Empty = none. The separate rolling limb prop
    // comes later; this is the "hide" half.
    public List<string> DismemberPocBones { get; set; } = new();
    // When on, each hidden limb also spawns a clone that shows ONLY that limb and tumbles away (the
    // "rolls away" half). Off = just hide on the body. POC: local player only.
    public bool EnableDismemberRollaway { get; set; } = false;
    public bool EnableEnemyDismemberment { get; set; } = false;
    public int EnemyHumanoidDismembermentCount { get; set; } = 0;
    public float EnemyMonsterDismembermentBonePercent { get; set; } = 100.0f;

    // Death collapse — physics-driven guided collapse on death (relaxation family + directed
    // knee power-loss). Config lives in GuidedCollapse; see DEATH_COLLAPSE_RESEARCH.md.
    public GuidedCollapseSettings GuidedCollapse { get; set; } = new();
    // Weapon drop physics — runs as part of ragdoll; weapon detaches and falls on death
    public bool WeaponDropMeshCollision { get; set; } = false;
    public bool HidePlayerLeftWeapon { get; set; }
    public bool HidePlayerRightWeapon { get; set; }
    // Independent flexible guides drive the existing hair bones. Contacts are one-way.
    public bool RagdollHairPhysics { get; set; } = false;
    // Hair contacts the cached rendered body surface (smoothed); off (default) = approximate body capsules.
    public bool RagdollHairBodyMesh { get; set; } = false;
    // Soft tissue — mod-skeleton jiggle bones. Bones whose names match one of the
    // comma-separated prefixes (Rue/YAS/IVCS-lineage skeletons) are auto-registered as
    // SoftBody jiggle bodies at ragdoll activation. Vanilla skeletons have no matching
    // bones, so this is inert without a body mod installed.
    public bool RagdollSoftTissueModBones { get; set; } = true;
    // Internal prefix source (not exposed in the UI — the coverage dropdown picks the bones).
    public string RagdollSoftTissueBonePrefixes { get; set; } = "iv_, ya_";
    // Bone coverage: 0 = Standard (prefix-matched mod-skeleton bones except fingers and toes),
    // 1 = All bones (EVERY skeleton bone not already a rig body, vanilla included — experimental),
    // 2 = All bones except digits (same as 1, minus fingers and toes — see
    // RagdollController.IsDigitBone).
    // Squash & stretch follows this scope automatically (it applies to every SoftBody body).
    public int RagdollSoftTissueScope { get; set; } = 0;
    // Soft-tissue-vs-ground contact. Off by default (extra contact pairs cost solver time,
    // and ground pushback fights the anchor springs). Soft-vs-body stays off regardless:
    // flesh capsules live inside the torso volume, so body contact is permanent
    // interpenetration by construction.
    public bool RagdollSoftTissueCollision { get; set; } = false;
    // Soft tissue — squash & stretch (EXPERIMENTAL). Writes per-bone scale into the
    // skeleton pose so soft bones visibly compress on impact and flatten against the
    // ground at rest. Off by default until the scale write path is field-verified.
    public bool RagdollSquashStretch { get; set; } = false;
    public float RagdollSquashIntensity { get; set; } = 0.5f; // 0..1, scales max compression
    // Ragdoll debug overlay — renders capsules and joint limits in 3D
    public bool RagdollDebugOverlay { get; set; } = false;
    // Dev (Experimental) — hidden behind easter egg
    public bool RagdollVerboseLog { get; set; } = false;
    public bool DevCompanionAppearanceVariant { get; set; } = false;
    public bool DevPartyApproachDebugLog { get; set; } = false;
    public bool RagdollNpcCollision { get; set; } = true;
    public bool RagdollNpcCollisionAutoSize { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    // Fast = the per-bone capsule proxies that already exist for the ragdoll, nothing built at
    // activation. Accurate = a skinned snapshot of the rendered mesh, more faithful but built at
    // activation (may hitch).
    public RagdollNpcCollisionMode RagdollNpcCollisionMode { get; set; } = RagdollNpcCollisionMode.BoneCapsule;

    /// <summary>
    /// Combat-recipe override for <see cref="RagdollNpcCollision"/>, live only while that recipe is
    /// running. Null = the user's own setting stands.
    ///
    /// An override rather than a write to the setting itself, so a recipe that turns NPC collision off
    /// for its own sake (none of the built-in ones do any more) does not silently redefine what the
    /// user chose globally — nor leave it redefined after the fight.
    /// </summary>
    [JsonIgnore]
    public bool? RecipeNpcCollisionOverride { get; set; }

    /// <summary>Whether NPC collision is actually in force right now. Read this in the simulation;
    /// the settings UI stays bound to <see cref="RagdollNpcCollision"/> so it keeps showing what the
    /// user picked rather than what a recipe is doing to it.</summary>
    [JsonIgnore]
    public bool NpcCollisionActive => RecipeNpcCollisionOverride ?? RagdollNpcCollision;

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

    // Hostile-NPC action pacing. Every enemy paces itself off its own skill cooldowns, but the
    // COST of a cast — VFX on the caster and on each target, an action-timeline restart, flytext —
    // all lands on one screen, so visible density rises with the enemy count while nothing was
    // holding the total down. This caps skill STARTS per second across every hostile NPC at once.
    // An enemy that can't get a slot doesn't idle: its cooldown is unspent, so it goes the moment
    // one frees, and it auto-attacks meanwhile. 0 = uncapped (the old behaviour).
    // Player and companions are never throttled — our own side is what the player is watching.
    public float NpcSkillRateLimit { get; set; } = 5f;
    // Skip action VFX for hostile casters further than this from the CAMERA (yalms). Their damage,
    // flytext and animation still play; they just stop contributing to the light show at a distance
    // where the effect is a few pixels anyway. 0 = never skip.
    [JsonIgnore] public float NpcSkillVfxMaxDistance => 30f;

    // Hit VFX on player when taking damage (empty = disabled)
    [JsonIgnore] public string HitVfxPath => "vfx/ws/wax_heavyswing/eff/wax_heavy1t0h.avfx";
    public bool EnableHitVfx { get; set; } = true;

    // HP Bar bone tracking
    public float PlayerHpBarYOffset { get; set; } = 0.3f;
    public float PlayerHpBarXOffset { get; set; } = 0f;
    public float EnemyHpBarYOffset { get; set; } = 0.3f;
    public bool HpBarOcclusion { get; set; } = true;
    public string CustomPlayerName { get; set; } = "";

    // Player HP bar label customization
    public string SimLabelText { get; set; } = "Sim";
    public string DeadLabelText { get; set; } = "DEAD";
    public string DeathNameAlias { get; set; } = "";

    // Spawn Enemy defaults
    public int SpawnDirection { get; set; } = 0;       // 0=Front, 1=Behind, 2=Left, 3=Right
    public float SpawnDistance { get; set; } = 5.0f;    // yalms from player
    // War Generator. Placement: null = not chosen yet, taken from SpawnInFront (0 around, 1 in front, 2 map).
    public int? SpawnPlacementMode { get; set; }
    public bool SpawnAsAlly { get; set; } = false;
    public bool SpawnPatrol { get; set; } = false;
    public int SpawnCount { get; set; } = 1;
    public int AllyStrength { get; set; } = 0;          // as EnemyStrength, for allies from the NPC catalog
    public int AllyLevel { get; set; } = 0;
    public float MapSpawnRadius { get; set; } = 60.0f;
    // War Generator rules for the player's side. See docs/WAR_GENERATOR.md.
    public bool WarCaptives { get; set; } = false;
    public uint WarCaptiveEmoteId { get; set; } = 19; // Kneel
    public int WarFallenRule { get; set; } = 0;        // WarFallenRule
    public bool WarRetreat { get; set; } = false;  // yalms from player, opponents placed on the map
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
    public uint ActionGuardId { get; set; } = 0;
    public uint ActionSkill1Id { get; set; } = 0;
    public uint ActionSkill2Id { get; set; } = 0;
    public int ActionGuardKey { get; set; } = 17; // SeVirtualKey.Control. Independent of job/action availability.
    public GamepadButtons ActionGuardGamepadButton { get; set; } = GamepadButtons.R2;
    // Basic attack: gamepad South/Cross (Xbox A) + the Q key, no hotbar action needed.
    public GamepadButtons ActionBasicAttackGamepadButton { get; set; } = GamepadButtons.South;
    public int ActionBasicAttackKey { get; set; } = 0x51; // SeVirtualKey.Q

    // Action Mode difficulty: 0 Easy, 1 Normal, 2 Hard. One choice sets the enemy windup and pace and
    // the guard timing below together; Normal is the tuning the mode shipped with.
    public const int ActionDifficultyDefault = 1;
    public int ActionDifficulty { get; set; } = ActionDifficultyDefault;
    private int ActionDifficultyIndex => Math.Clamp(ActionDifficulty, 0, 2);

    // Action-Mode attack pace: enemies AND companions initiate attacks this many
    // times faster (auto-attack delay + skill cooldowns divided by this).
    [JsonIgnore] public float ActionEnemyAttackSpeed => new[] { 1.2f, 1.6f, 2.0f }[ActionDifficultyIndex];

    // Guard timing (from the difficulty).
    [JsonIgnore] public float GuardActiveWindow => new[] { 0.3f, 0.22f, 0.15f }[ActionDifficultyIndex];  // early tolerance
    [JsonIgnore] public float GuardLateTolerance => new[] { 0.2f, 0.15f, 0.08f }[ActionDifficultyIndex]; // grace after the strike
    [JsonIgnore] public float ChainGuardWindow => new[] { 0.5f, 0.4f, 0.25f }[ActionDifficultyIndex];    // chain guard window
    [JsonIgnore] public int GuardMaxChain => new[] { 5, 3, 2 }[ActionDifficultyIndex];                  // blocks per press

    // Fixed Action Mode tuning.
    [JsonIgnore] public float GuardRecovery => 0.35f;      // lockout after a guard CHAIN ends
    [JsonIgnore] public float GuardCooldown => 0.15f;      // min time between guard attempts
    public ushort GuardTimelineId { get; set; } = 0;       // 0 = auto/fallback
    [JsonIgnore] public string GuardSuccessVfxPath => "vfx/ws/wax_heavyswing/eff/wax_heavy1t0h.avfx";
    [JsonIgnore] public string EnemyTelegraphVfxPath => "vfx/common/eff/cmhit_fire1t.avfx";
    [JsonIgnore] public float LightComboWindow => 0.6f;    // time to chain the next swing
    [JsonIgnore] public float LightSwingInterval => 0.4f;  // min time between swings (cadence)
    [JsonIgnore] public float PlayerHitboxRange => 5f;     // melee basic-attack reach (yalms), measured to the target's SURFACE
    [JsonIgnore] public float PlayerHitboxAngleDeg => 100f; // melee selection cone full angle
    // Edge-to-edge reach: the target's hitbox radius is added to the melee select range, so a large
    // enemy is hittable from its surface (not its centre). This is how FFXIV itself measures melee range.
    [JsonIgnore] public float MeleeTargetHitboxReach => 1.0f;

    // Impact feedback on a confirmed player attack: brief target hitstop + a
    // camera punch + an optional hit spark. All gated below.
    public bool EnableHitFeedback { get; set; } = true;
    // Delay (seconds) between the attack input and the impact feedback firing. The swing has ~0.5s of
    // windup before the weapon visually CONNECTS, so the hitstop/camera/spark must wait or they fire
    // at swing start and feel disconnected. ~0.45 lines up with the contact frame; tune to taste.
    [JsonIgnore] public float HitFeedbackDelay => 0.38f;
    // Hitstop: freeze the struck target's animation for this many ms on impact. The single
    // biggest contributor to impact feel. Keep SHORT (40-90 ms) or it reads as lag. 0 = off. Only the
    // target is frozen (never the player), and the freeze is skipped/auto-released if it dies.
    public float HitstopMs { get; set; } = 81.53f;
    // Camera punch: a brief decaying screen shake on impact (yalms of camera offset). Small = weighty,
    // large = nauseating. Layered on top of the final camera pose. 0 = off.
    public float HitCameraShake { get; set; } = 0.1f;
    public float HitCameraShakeDuration { get; set; } = 0.2f; // seconds the shake decays over
    // Spawn a spark VFX on the struck target (reuses HitVfxPath). Off by default: ActorVfxCreate on
    // modified/spawned actors can be fragile, so opt in once you've confirmed it's stable in your setup.
    public bool EnableHitSparkVfx { get; set; } = true;
    // Effects > NPC Scale. This is the final uniform DrawObject scale, matching the original
    // experimental control. It normalizes authored BNpc sizes instead of multiplying them (for
    // example, 0.99 does not leave a 3.5-scale boss at 3.465). Disable the effect to restore the
    // authored size; an enabled value of exactly 1.0 means an absolute final scale of 1.0.
    public bool EnableNpcScale { get; set; } = true;
    public float NpcScale { get; set; } = 0.99f;
    [JsonIgnore] public int LightAttackPotency => 120;
    // Soft-target selection: ranged basic attack / ranged skills use a longer, wider selection cone
    // and pick the smallest-angle enemy (not the nearest).
    public float RangedBasicRange { get; set; } = 25f;
    public float RangedSelectAngleDeg { get; set; } = 160f;

    // Telegraph overlay
    public bool ShowTelegraphs { get; set; } = false;
    [JsonIgnore] public float TelegraphAlpha => 0.45f;
    public float TelegraphThickness { get; set; } = 3.0f;

    // Action Mode windup: the strike resolves this long after the telegraph appears, so it's
    // the time the player has to react, and the duration of the osu-style approach circle.
    [JsonIgnore] public float ActionWindupSeconds => new[] { 0.6f, 0.39f, 0.3f }[ActionDifficultyIndex];

    // Play a wind-up swing on the enemy when the telegraph starts so the windup has body
    // language (instead of the enemy standing idle until the strike). NOTE: the engine couples
    // the impact sound + hit-reaction to a swing, so the strike still swings — this produces a
    // second swing. Off = enemy idle during the windup, single clean swing at the strike.
    [JsonIgnore] public bool ActionEnemyWindupSwing => true;

    // osu-style parry-timing circle drawn on the player. A steady inner ring + an outer
    // ring that shrinks onto it over the windup; aligned = the guard window.
    public bool OsuCircleEnabled { get; set; } = true;
    [JsonIgnore] public float OsuAnchorHeight => 1.1f;     // anchor height above feet (yalms; chest≈1.1)
    [JsonIgnore] public float OsuInnerRadius => 0.18f;     // inner ring radius in world units (~1.5× head)
    [JsonIgnore] public float OsuOuterStartScale => 3.2f;  // outer ring starts at inner × this

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
        MigrateHpBarLabelSwitches(); // before the purge below: it reads the retired switches
        MigrateKoStripHangChoices(); // reads retired keys, so before they are purged
        RemoveRetiredOptions();
        MigrateSplitVfxToggles();
        MigrateKoStripClothHoldPreset();
        MigrateActionGuardDefaultButton();
        MigrateActionBasicAttackDefaultButton();
        MigrateActionAttackShapeDefaults();
        MigrateEnemyDismembermentDefaults();
        MigrateFightingSpacingDefaults();
        MigrateNpcCollisionMeshDefault();
        MigrateEnemyTacticsMeleeCap();
        MigrateNpcCollisionConvexHullDefault(); // after the mesh default above: it overrides that verdict
        NormalizeNpcCollisionMode();
        GuidedCollapse ??= new GuidedCollapseSettings();
        MigrateRagdollLegModelRebuild();
    }

    // Options that no longer exist. [JsonExtensionData] would otherwise keep their saved values in the
    // file forever and hand them back to any future property that reuses the name.
    private static readonly string[] RetiredOptionNames =
    {
        "ShowDefeatedText", "DefeatedText",
        // Armor Detachment, simplified 2026-11.
        "ShowArmorDetachmentControls", "KoStripSyncWithRagdoll", "KoStripAdvancedClothPhysics",
        "KoStripGarmentTubeModel", "KoStripGarmentTubeDebugDraw", "KoStripGarmentSkirtPhysics",
        "KoStripSkirtSegmentMass", "KoStripSkirtSwingLimit", "KoStripSkirtInitialSwing",
        "KoStripGarmentTubeBodyFriction", "KoStripGarmentTubeGroundFriction", "KoStripGarmentTubeHoldSeconds",
        "KoStripClothHoldSeconds", "KoStripClothHoldAuto", "KoStripAttachmentBody", "KoStripAttachmentLegs",
        "KoStripAttachmentOverrides", "KoStripAttachmentDebugDraw", "KoStripClothVisualOnlySlideDistance",
        "KoStripClothVisualOnlySlideSpeed", "KoStripCollapseHead", "KoStripCollapseBody", "KoStripCollapseHands",
        "KoStripCollapseLegs", "KoStripCollapseFeet", "KoStripCollapseEars", "KoStripCollapseNeck",
        "KoStripCollapseWrists", "KoStripCollapseRFinger", "KoStripCollapseLFinger",
        "KoStripHandsFeetRigidDefaultMigrated20260719",
        "KoStripTopHangFrom", "KoStripLowerHangFrom",
        // Enemy numbers reworked 2026-11: levels now follow the player by default, strength is detected.
        "DefaultNpcLevel", "FastCombatLevel", "DefaultEnemyTier", "FastCombatEnemyTier", "DefaultNpcHpMultiplier",
        "RecipeEnemyStrength", "RecipeEnemyLevel",
        // Glamourer presets on death / reset, removed 2026-11.
        "ApplyGlamourerOnDeath", "DeathGlamourerDesignId", "ApplyGlamourerOnReset", "ResetGlamourerDesignId",
        "KoStripDelay", "KoStripDelaySeconds", "KoStripLHand", "KoStripRHand", "KoStripLFoot", "KoStripRFoot",
        // Ragdoll pages, simplified 2026-11 (values fixed in RagdollTuning or the ON side of a switch).
        "PartyEnemyDeathRagdoll", "NpcRagdollActivationDelay", "MaxNpcRagdolls", "RagdollGravity", "RagdollDamping",
        "RagdollSolverIterations", "RagdollSolverSubsteps", "NpcRagdollSolverIterations", "NpcRagdollSolverSubsteps",
        "RagdollLimitSpringFrequency", "RagdollSoftLimits", "RagdollSoftLimitFrequency", "RagdollSoftLimitDamping",
        "RagdollJointSpringFrequency", "RagdollFootJointSpringFrequency", "RagdollCarryAnimationVelocity",
        "RagdollHandoffVelocityScale", "RagdollImpactWeight", "RagdollHeavyBody", "RagdollRelaxationTopple",
        "RagdollCollapseAsymmetry", "RagdollStagedFailure", "RagdollToppleMomentumBias", "RagdollSelfCollision",
        "RagdollFriction", "RagdollAnthropometricMass", "RagdollBodyMass", "RagdollAnatomicalHingeRestBias",
        "WeaponDropGravity", "WeaponDropDamping", "WeaponDropAngularDamping", "WeaponDropMass", "WeaponDropRadius",
        "WeaponDropHalfLength", "WeaponDropBounce", "WeaponDropFriction", "WeaponDropSolverIterations",
        "RagdollHairBendCompliance", "RagdollHairDamping", "RagdollHairSwingDamping", "RagdollHairCollision",
        "RagdollHairRigSegmentMass", "RagdollHairRigThickness", "RagdollHairRigSwingLimit",
        "RagdollHairRigInitialSwingFactor", "RagdollHairRigPoseGuideForce", "RagdollHairRigSettleSeconds",
        "RagdollGroundPenetrationLift", "RagdollBoneConfigs", "RagdollBoneProfiles", "RagdollNpcCorpseTraversal",
        "RagdollCharacterSurfaceProfiles", "RagdollNpcCollisionConvexHull", "RagdollMeshParallelPhysics",
        "ThinnerProfileTuningMigrated20260704", "AnatomicalDefaultsOffMigrated20260704",
        "NpcCollisionModeMigrated20260705", "RagdollShinBoxHalfYMigrated20260716",
        "RagdollDefaultProfileTuningMigrated20260716", "RagdollSoftBodyTuningMigrated20260719",
        "RagdollFingerPhysicsMigrated20260810", "RagdollNpcCorpseTraversalDefaultMigrated20260816",
        // Combat / Targets / Party / Spectators, simplified 2026-11.
        "ActionAttackId", "MinTelegraphWindup", "ActionEnemyAttackSpeed", "GuardActiveWindow",
        "GuardLateTolerance", "ChainGuardWindow", "GuardMaxChain", "GuardRecovery", "GuardCooldown",
        "GuardSuccessVfxPath", "EnemyTelegraphVfxPath", "LightComboWindow", "LightSwingInterval",
        "PlayerHitboxRange", "PlayerHitboxAngleDeg", "MeleeTargetHitboxReach", "LightAttackPotency",
        "TelegraphAlpha", "ActionWindupSeconds", "ActionEnemyWindupSwing", "OsuAnchorHeight",
        "OsuInnerRadius", "OsuOuterStartScale",
        "ActionGuardVfxDefaultMigratedToHeavySwing", "ActionMeleeRangeDefaultMigratedTo5",
        "EnemyTacticsPlayerSlots", "EnemyTacticsHitboxSpacing", "EnemyTacticsStrafe", "EnemyTacticsThreat",
        "EnemyTacticsStagger", "EnemyTacticsFlee", "EnemyTacticsReposition", "EnemyTacticsHeavyTurn",
        "EnemyTelegraphedCasts", "EnemyMaxCastSeconds", "EnemyKitIncludeAoe", "EnemyHpPhaseSkills",
        "KeepCompanionsOnReset", "PartyCommandRange", "PartyCommandRangeRandomness",
        "PartyMeleeAttackRange", "PartyRangedAttackRange",
        // The old movers, replaced by the crowd on the plugin's navigation mesh (2026-11-13).
        "EnableTargetApproach", "UseSoloTargetFormationWhenNoCompanions", "TargetApproachDistance",
        "UseVNavmeshTargetApproach",
        "SpectatorChatterBubbleDuration", "SpectatorChatterMaxConcurrent",
        // Effects, Enemy Control, Camera and Settings tabs, simplified 2026-11.
        "NpcSkillVfxMaxDistance", "HitVfxPath", "HitFeedbackDelay", "HitVfxDefaultMigrated20260830",
        "VictoryCelebrationStagger", "EnemyVoiceGuaranteeKills", "ArrowImpactMaxPerTarget",
        "RagdollFollowPosition", "RagdollFollowPositionDefaultMigrated20260816",
        "EnemyControlAttackEnabled", "EnemyControlAttackStrength", "EnemyControlVerticalSpeed",
        "EnemyControlSwarmClimbDelay", "EnemyControlSwarmEnhancedStompTracking",
        "EnemyControlSwarmStompCorpseMassMultiplier", "EnemyControlSwarmStompResponse",
        "DynCamPivotBoneName", "DynCamHeightOffset", "DynCamInputHold", "DynCamDeathMaximizeBody",
        "DynCamDeathCloseUpDistance", "DynCamDeathTranslateDuration", "MigratedDynCamPivotBone",
        "ShowSimLabel", "ShowDeadLabel", "ShowDeathNameAlias",
    };

    // The Show*Label switches merged into their text: an empty text now means "off". A switch that was
    // off clears its text once, so nothing that was hidden starts showing.
    private void MigrateHpBarLabelSwitches()
    {
        if (PrivateConfigFields == null)
            return;

        var changed = false;
        void Fold(string flag, Action clear)
        {
            if (PrivateConfigFields.TryGetValue(flag, out var token) && token.Type == JTokenType.Boolean && !token.Value<bool>())
            {
                clear();
                changed = true;
            }
        }
        Fold("ShowSimLabel", () => SimLabelText = "");
        Fold("ShowDeadLabel", () => DeadLabelText = "");
        Fold("ShowDeathNameAlias", () => DeathNameAlias = "");
        if (changed)
            Save();
    }

    // The hang point was briefly a single choice per garment; it is now a set of holds.
    private void MigrateKoStripHangChoices()
    {
        if (PrivateConfigFields == null)
            return;

        if (PrivateConfigFields.TryGetValue("KoStripTopHangFrom", out var top) && top.Type == JTokenType.Integer)
            KoStripTopHolds = Animation.Attachment.GarmentTether.HoldsFromLegacyChoice(1, (int)top);
        if (PrivateConfigFields.TryGetValue("KoStripLowerHangFrom", out var lower) && lower.Type == JTokenType.Integer)
            KoStripLowerHolds = Animation.Attachment.GarmentTether.HoldsFromLegacyChoice(3, (int)lower);
    }

    private void RemoveRetiredOptions()
    {
        if (PrivateConfigFields == null)
            return;

        var changed = false;
        foreach (var name in RetiredOptionNames)
            changed |= PrivateConfigFields.Remove(name);
        if (changed)
            Save();
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    // Only two garment behaviours remain. Quick and Clingy differed from Natural only in how long the
    // body had to rest, Slide to floor was dropped, and the old Visual only became Visual only (enhanced).
    private void MigrateKoStripClothHoldPreset()
    {
        var preset = KoStripClothHoldPreset switch
        {
            1 or 5 => KoStripClothHoldPreset,
            4 => 5,
            _ => KoStripClothHoldPresetDefault,
        };
        if (preset == KoStripClothHoldPreset)
            return;
        KoStripClothHoldPreset = preset;
        Save();
    }

    /// <summary>
    /// Force every existing config onto the cheap bone-capsule NPC collision shape. Both the mesh
    /// and convex-hull shapes build geometry at activation and can stutter; a corrected default
    /// alone would never reach anyone who already has the old value saved — which is everyone who
    /// has run the plugin before. One-shot: the dropdown is theirs again afterwards, including
    /// switching back to hull or mesh for the fidelity. (This flag previously forced convex hull;
    /// it now forces bone capsule, so a config that already ran it stays put — re-run it by
    /// picking the shape by hand.)
    /// </summary>
    private void MigrateNpcCollisionConvexHullDefault()
    {
        if (NpcCollisionConvexHullDefaultMigrated20260716)
            return;

        RagdollNpcCollisionMode = RagdollNpcCollisionMode.BoneCapsule;
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
        nameof(PartyCompanionDeathRagdoll),
        nameof(NpcRagdollFineDetail),
        nameof(WeaponBodyMeshContacts),
    };

    /// <summary>
    /// Named like the page, but not the page's to reset.
    /// </summary>
    private static readonly HashSet<string> RagdollPageResetExclusions = new(StringComparer.Ordinal)
    {
        // The master switch. Resetting it would turn the whole feature off and take the page with it,
        // which reads as the button having broken something rather than having restored it.
        nameof(EnableRagdoll),

        // Dev-only switch. Lives on the hidden panel, not this page.
        nameof(RagdollVerboseLog),
        // A rebuild counter, not an option: zeroing it rebuilds every live ragdoll.
        nameof(RagdollMeshCacheRevision),
    };

    private static bool IsRagdollPageOption(string name)
    {
        if (RagdollPageResetExclusions.Contains(name)) return false;
        // Every one-shot migration record, current and future: resetting one re-runs its migration on
        // next load, and the leg-model rebuild migration wipes the saved bone profiles.
        if (name.Contains("Migrated", StringComparison.Ordinal)) return false;

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

        GuidedCollapse = new GuidedCollapseSettings();
        Save();
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
        ActionGuardId = defaults.ActionGuardId;
        ActionSkill1Id = defaults.ActionSkill1Id;
        ActionSkill2Id = defaults.ActionSkill2Id;
        ActionGuardKey = defaults.ActionGuardKey;
        ActionGuardGamepadButton = defaults.ActionGuardGamepadButton;
        ActionBasicAttackGamepadButton = defaults.ActionBasicAttackGamepadButton;
        ActionBasicAttackKey = defaults.ActionBasicAttackKey;

        ActionDifficulty = defaults.ActionDifficulty;
        GuardTimelineId = defaults.GuardTimelineId;

        ShowTelegraphs = defaults.ShowTelegraphs;
        TelegraphThickness = defaults.TelegraphThickness;
        OsuCircleEnabled = defaults.OsuCircleEnabled;
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

    private void MigrateActionBasicAttackDefaultButton()
    {
        if (ActionBasicAttackDefaultMigratedToSouth)
            return;

        if (ActionBasicAttackGamepadButton == GamepadButtons.East)
            ActionBasicAttackGamepadButton = GamepadButtons.South;

        ActionBasicAttackDefaultMigratedToSouth = true;
        Save();
    }

    private void MigrateActionAttackShapeDefaults()
    {
        if (ActionAttackShapeDefaultsMigrated20260630)
            return;

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
    /// "Take turns in melee" used to cap a fight at three attackers with no way to change it, and an
    /// interim build turned it off. One-time move to the configurable cap at its default; changes
    /// made afterwards stick.
    /// </summary>
    private void MigrateEnemyTacticsMeleeCap()
    {
        if (EnemyTacticsMeleeCapMigrated20261102)
            return;

        EnemyTacticsMeleeAttackers = 8;
        EnemyTacticsMeleeCapMigrated20261102 = true;
        Save();
    }

    /// <summary>
    /// 2.x.0.0: the leg model was rebuilt — j_asi_c (previously folded into j_asi_b as an inert
    /// mesh-sampling helper) is a real second knee-region body now, j_asi_b was resized to its
    /// real short proximal length, and several joint/collision defaults changed with it (mesh-aware
    /// geometry back on, restored anatomical hinge rest bias, restored 2-hop self-collision
    /// exclusion, ankle back on the ball-cone model). Existing installs have their own saved
    /// values for all of this from before the rebuild, so a normal default-value bump would only
    /// reach fresh installs — this forces every existing config back to the shipped defaults once.
    ///
    /// </summary>
    private void MigrateRagdollLegModelRebuild()
    {
        if (RagdollLegModelRebuildMigrated20260816)
            return;

        ResetRagdollPageDefaults();

        RagdollLegModelRebuildMigrated20260816 = true;
        Save();
    }

    // ConvexHull (1) and AnimatedMesh (3) were removed. A hull approximated what capsules already
    // cover; the animated mesh becomes the static skinned snapshot it was refining.
    private void NormalizeNpcCollisionMode()
    {
        var mode = (int)RagdollNpcCollisionMode == 3 ? RagdollNpcCollisionMode.Mesh
            : RagdollNpcCollisionMode == RagdollNpcCollisionMode.Mesh ? RagdollNpcCollisionMode.Mesh
            : RagdollNpcCollisionMode.BoneCapsule;
        if (mode == RagdollNpcCollisionMode)
            return;
        RagdollNpcCollisionMode = mode;
        Save();
    }
}
