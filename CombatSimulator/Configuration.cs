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

/// <summary>When a separated part comes off (PC and enemy separation alike).</summary>
public enum SeparationTiming
{
    /// <summary>At the death itself: the part comes off and is hidden on the body while the death animation still plays.</summary>
    OnDeath = 0,
    /// <summary>The moment the body's ragdoll physics starts.</summary>
    OnRagdollActivated = 1,
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
    // Starting or resetting a battle switches the camera to the Directed camera.
    public bool DirectedCameraOnBattle { get; set; } = false;
    // Skill effects of everyone but the player: 0 all, 1 limited (those near the camera, those on the
    // player, and only a few a second), 2 none (only those on the player). The player's own always show.
    public int OthersSkillEffects { get; set; } = 1;

    // Simulation
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

    // Armor Detachment (Ragdoll tab): "Detach armor" - Off, On KO (at the knockout itself), or On ragdoll
    // activated (the moment the body's ragdoll physics starts). Replaced KoStripEnabled / KoStripOnHitEnabled
    // (2026-11-22, MigrateKoStripTrigger).
    public Dev.ArmorDetachmentTrigger KoStripTrigger { get; set; } = Dev.ArmorDetachmentTrigger.Off;
    public bool KoStripHead { get; set; } = true;
    public bool KoStripBody { get; set; } = true;
    public bool KoStripHands { get; set; } = true;
    public bool KoStripLegs { get; set; } = true;
    public bool KoStripFeet { get; set; } = false;
    public bool KoStripEars { get; set; } = false;
    public bool KoStripNeck { get; set; } = false;
    public bool KoStripWrists { get; set; } = false;
    public bool KoStripRFinger { get; set; } = false;
    public bool KoStripLFinger { get; set; } = false;

    // The same detachment, with the same settings, for allies and for enemies (humanoids wearing
    // equipment). Nothing happens while "Detach armor" is Off.
    public bool KoStripAllies { get; set; } = false;
    public bool KoStripEnemies { get; set; } = false;

    // Physically drop removed pieces instead of just hiding them. The UI sets both together; separate
    // fields remain because hats/accessories and clothing take different runtime paths.
    public bool KoStripPhysicsDrop { get; set; } = true;
    public bool KoStripPhysicsDropClothing { get; set; } = true;

    // Auto-recycle: despawn a physics-dropped piece a fixed time after it settles, rather than letting
    // it linger until reset/zone-change. Off by default — a stale dropped piece surviving into a zone
    // transition is the known crash exposure this exists to reduce.
    public bool KoStripCloneAutoExpireEnabled { get; set; } = false;
    public float KoStripCloneAutoExpireSeconds { get; set; } = 2f;

    // Body/Legs garment behaviour. Stored values keep their historic numbering:
    // 1 drop when the body settles, 5 Visual only (enhanced).
    public const int KoStripClothHoldPresetDefault = 5;
    public int KoStripClothHoldPreset { get; set; } = KoStripClothHoldPresetDefault;

    // Half off, hanging on the body (preset 5): which parts of the top / lower garment stay on the body
    // (sums of the GarmentTether.Top* / Lower* bits) and how hard the rest is pulled down (index into
    // GarmentTether.PullLabels).
    // Defaults: the top stays on at the waist, collar and both arms; the lower garment comes off
    // completely; a gentle pull (index 1 of GarmentTether.PullLabels).
    public int KoStripTopHolds { get; set; } =
        AdvancedRagdoll.Attachment.GarmentTether.TopWaist | AdvancedRagdoll.Attachment.GarmentTether.TopCollar |
        AdvancedRagdoll.Attachment.GarmentTether.TopLeftUpperArm | AdvancedRagdoll.Attachment.GarmentTether.TopLeftForearm |
        AdvancedRagdoll.Attachment.GarmentTether.TopRightUpperArm | AdvancedRagdoll.Attachment.GarmentTether.TopRightForearm;
    public int KoStripLowerHolds { get; set; } = 0;
    public int KoStripHangPull { get; set; } = 1;

    // Unselected previous implementation of preset 5 (whole-garment slide): how far the top and the lower garment slide down from their worn
    // position, in metres on a scale-one actor. The top never travels more than 0.08 m and the lower
    // garment never more than 65% of the thigh, whatever is set here.
    public float KoStripTopSlideDistance { get; set; } = 0.09f;
    public float KoStripLowerSlideDistance { get; set; } = 0.18f;

    /// <summary>
    /// Named like Armor Detachment, but not its to reset.
    /// </summary>
    private static readonly HashSet<string> ArmorDetachmentResetExclusions = new(StringComparer.Ordinal)
    {
        // The master switch. Resetting it would turn the whole feature off and take the page with
        // it, which reads as the button having broken something rather than having restored it —
        // same reasoning as EnableRagdoll on the Ragdoll page (see RagdollPageResetExclusions).
        nameof(KoStripTrigger),
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

    // Combat-link arcs: a red arc from every enemy currently attacking the player, drawn above the head
    // with a flowing animation from the attacker toward the player.
    public bool ShowCombatLinkArcs { get; set; } = true;
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
    // PC Dismemberment and Enemy Dismemberment: "Timing".
    public SeparationTiming DismemberTiming { get; set; } = SeparationTiming.OnDeath;

    // Ragdoll Trigger: "Death guidance" - what the body does when physics takes it over (player, allies and
    // enemies alike; docs/RAGDOLL_DEATH_GUIDANCE.md). Replaced the GuidedCollapse object and its one switch,
    // "Knee buckle on death" (2026-11-22, MigrateRagdollDeathGuidance).
    public RagdollDeathGuidance RagdollDeathGuidance { get; set; } = RagdollDeathGuidance.Limp;
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
    // Combat is real-time action combat: player attacks/guard come from hotbar actions remapped by
    // actionId (the game resolves keyboard/gamepad for us); enemy attacks telegraph from a wind-up
    // snapshot, then resolve by hitbox at the active frame. It is the only combat: the tab-target mode
    // it once replaced is gone (2026-11-13), and so is the experimental Fighting Mode (2026-11-15).

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
        MigrateKoStripTrigger(); // reads retired keys, so before they are purged
        MigrateRagdollDeathGuidance(); // reads a retired key, so before they are purged
        MigrateDuelDeathShot(); // reads retired keys, so before they are purged
        RemoveRetiredOptions();
        MigrateSplitVfxToggles();
        MigrateKoStripClothHoldPreset();
        MigrateActionGuardDefaultButton();
        MigrateActionBasicAttackDefaultButton();
        MigrateActionAttackShapeDefaults();
        MigrateEnemyDismembermentDefaults();
        MigrateNpcCollisionMeshDefault();
        MigrateEnemyTacticsMeleeCap();
        MigrateNpcCollisionConvexHullDefault(); // after the mesh default above: it overrides that verdict
        NormalizeNpcCollisionMode();
        RagdollDeathGuidance = RagdollDeathGuidances.Clamp(RagdollDeathGuidance);
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
        // "Detach armor" became one setting (KoStripTrigger), 2026-11-22.
        "KoStripEnabled", "KoStripOnHitEnabled",
        // "Knee buckle on death" became one choice of "Death guidance" (RagdollDeathGuidance), 2026-11-22.
        "GuidedCollapse",
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
        // Action combat is the only combat (2026-11-13). The tab-target lock that went with the old mode
        // (target keybinds, auto-counter, the locked target's marker) was unreachable since, removed 2026-11-15.
        "ActionMode", "EnableCustomTargeting", "EnableAutoCounter", "ShowLockMarker",
        // The player-only damage multiplier, removed (2026-11-15, user's decision: not used).
        "DamageMultiplier",
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
        // Enemy Control's "spawn a creature on death", removed (2026-11-15, user's decision): Enemy
        // Control only ever takes over one enemy of the fight.
        "EnemyControlSpawnOnDeath", "EnemyControlModelId", "EnemyControlModelNameId",
        // Enemy Control's swarm (other enemies following and climbing corpses), removed (2026-11-15, user's
        // decision): Enemy Control only controls its one enemy. (The arm-pose override removed with it was
        // restored on 2026-11-21 at the user's request; its keys are live again.)
        "EnemyControlSwarmEnabled", "EnemyControlSwarmFollowDistance", "EnemyControlSwarmCompactness",
        "EnemyControlSwarmClimbCorpses", "EnemyControlSwarmRepositionOnCorpse",
        "EnemyControlSwarmLandmarkChangeFrequency",
        // Armor Detachment's "Still-attached pieces follow the body", removed (2026-11-21, user's decision):
        // pieces never ride along when the body is relocated onto a grab or hold.
        "KoStripGarmentFollowsBody",
        // The automatic death shot's two settings from the days it ran in Directed mode (2026-11-22, for a few
        // hours): it runs in Duel mode only now and its settings are DuelDeathShot / DuelDeathShotBelowGround
        // (MigrateDuelDeathShot carries them over). The Directed camera's own death framing and its settings
        // (DynCamDeath*, ShowDynamicCamToolbar, DynCamDebugOverlay) are back and are NOT retired.
        "DeathShotProfile", "DeathShotBelowGround",
        // The Duel death shot's "Side preference", removed (2026-11-22, user's decision): the death shot no longer
        // chooses a side at all, the horizontal direction is the player's.
        "DuelDeathShotSide",
        // Fighting Mode (the experimental 1v1 side-view lane, its weapon hitboxes, AI and cameras), removed
        // (2026-11-15, user's decision: no longer used).
        "FightingMode", "FightingModeLaneHalfWidth", "FightingModeMinSeparation", "FightingModeMaxSeparation",
        "FightingModeCameraMargin", "FightingModeCameraMinDistance", "FightingModeCameraMaxDistance",
        "FightingModeCameraSmoothing", "FightingModeCameraHeight", "FightingModeCameraVerticalAngle",
        "FightingModeTranslateCam", "FightingModeTranslateDuration", "FightingModeTranslateDistance",
        "FightingModeTranslateBoneName", "FightingModeTranslateHeightOffset", "FightingModeTranslateSideOffset",
        "FightingModeTranslateLockHorizontal", "FightingModeTranslateHorizontalAngle",
        "FightingModeTranslateLockVertical", "FightingModeTranslateVerticalAngle", "FightingModeCameraSide",
        "FightingModeVaultDuration", "FightingModeWeaponLength", "FightingModeWeaponLengthScale",
        "FightingModeWeaponAxis", "FightingModeWeaponRadius", "FightingModeHurtboxHeight",
        "FightingModeHurtboxRadiusScale", "FightingModeAttackActiveStartPct", "FightingModeAttackActiveEndPct",
        "FightingModeDebugDraw",
        "FightingAiMoveSpeed", "FightingAiRetreatSpeedScale", "FightingAiRangeMin", "FightingAiRangeMax",
        "FightingAiAttackCooldown", "FightingAiAttackCooldownJitter", "FightingAiComboChance", "FightingAiComboGap",
        "FightingAiMaxComboHits", "FightingAiDashChance", "FightingAiDashSpeedScale", "FightingAiJumpOverChance",
        "FightingAiJumpDuration", "FightingAiJumpHeight", "FightingAiJumpInAttackChance", "FightingAiRetreatChance",
        "FightingAiRetreatDuration", "FightingAiRecoverTime", "FightingAiHitstunDuration",
        "FightingAiHitstunPushback",
        "FightingModeKoCameraMargin", "FightingModeKoCameraBase", "FightingModeKoCameraMinDistance",
        "FightingModeKoCameraMaxDistance", "FightingModeKoCameraHeight", "FightingModeKoLockAngle",
        "FightingSpacingDefaultsMigrated20260703",
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
            KoStripTopHolds = AdvancedRagdoll.Attachment.GarmentTether.HoldsFromLegacyChoice(1, (int)top);
        if (PrivateConfigFields.TryGetValue("KoStripLowerHangFrom", out var lower) && lower.Type == JTokenType.Integer)
            KoStripLowerHolds = AdvancedRagdoll.Attachment.GarmentTether.HoldsFromLegacyChoice(3, (int)lower);
    }

    // "Detach armor" was two exclusive switches (On KO; On each enemy hit). On each enemy hit is gone; a saved
    // choice of it becomes On ragdoll activated, a saved On KO stays On KO (at the knockout), Off stays Off.
    private void MigrateKoStripTrigger()
    {
        if (PrivateConfigFields == null)
            return;

        static bool Flag(IDictionary<string, JToken> fields, string name)
            => fields.TryGetValue(name, out var token) && token.Type == JTokenType.Boolean && token.Value<bool>();
        if (!PrivateConfigFields.ContainsKey("KoStripEnabled") && !PrivateConfigFields.ContainsKey("KoStripOnHitEnabled"))
            return;
        KoStripTrigger = Dev.ArmorDetachmentTriggers.FromRetiredSwitches(Flag(PrivateConfigFields, "KoStripEnabled"), Flag(PrivateConfigFields, "KoStripOnHitEnabled"));
    }

    // The automatic death shot moved to Duel mode and its settings were renamed (DeathShotProfile → DuelDeathShot,
    // DeathShotBelowGround → DuelDeathShotBelowGround). The dropdown is three shots now, told apart by subject,
    // distance and height (the horizontal direction is the player's): a saved Low three-quarter is Low close (the
    // same number); a saved shot that no longer exists becomes the one with its subject (Hips up to the face → Low
    // on the face; Low from the feet, Low from behind, Auto and the older ones → Low close:
    // DeathShotProfiles.Migrate). Whatever is saved under the new name is checked the same way.
    private void MigrateDuelDeathShot()
    {
        if (PrivateConfigFields != null)
        {
            if (PrivateConfigFields.TryGetValue("DeathShotProfile", out var profile) && profile.Type == JTokenType.Integer &&
                profile.Value<int>() == (int)Camera.DeathShots.DeathShotProfile.LowClose)
                DuelDeathShot = Camera.DeathShots.DeathShotProfile.LowClose;
            if (PrivateConfigFields.TryGetValue("DeathShotBelowGround", out var below) && below.Type == JTokenType.Boolean)
                DuelDeathShotBelowGround = below.Value<bool>();
        }
        DuelDeathShot = Camera.DeathShots.DeathShotProfiles.Migrate(DuelDeathShot);
        if (DuelDeathShotAngle is { } angle && !float.IsFinite(angle))
            DuelDeathShotAngle = null;
        if (!System.Enum.IsDefined(DuelDeathShotGrabbed))
            DuelDeathShotGrabbed = Camera.DeathShots.DeathShotGrabbedFraming.Level;
    }

    // "Guided Collapse" was an object with one switch, "Knee buckle on death". It is one choice of "Death guidance"
    // now: a saved on becomes Knee buckle, a saved off becomes Limp.
    private void MigrateRagdollDeathGuidance()
    {
        if (PrivateConfigFields == null || !PrivateConfigFields.TryGetValue("GuidedCollapse", out var saved))
            return;

        var enabled = saved is JObject guided && guided.TryGetValue("Enabled", out var token)
                      && token.Type == JTokenType.Boolean && token.Value<bool>();
        RagdollDeathGuidance = RagdollDeathGuidances.FromRetiredKneeBuckleSwitch(enabled);
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
            // The retired presets (Natural, Quick, Clingy, Slide to floor) all dropped the garment: map them
            // to "Drop when the body settles", whatever the default is.
            _ => 1,
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
            || RagdollPageExtraOptions.Contains(name)
            // Armor Detachment lives on this page too; its trigger is left alone like Enable Ragdoll.
            || IsArmorDetachmentOption(name);
    }

    /// <summary>
    /// Restore everything on the Ragdoll page — the ragdoll options themselves, death guidance, NPC
    /// collision and its settle pass, Armor Detachment's options (not its trigger) — to defaults.
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

        Save();
    }

    public void ResetActionModeDefaults()
    {
        var defaults = new Configuration();

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
