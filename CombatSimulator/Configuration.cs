using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CombatSimulator;

[Serializable]
public class DeathCamPreset
{
    public string Name { get; set; } = "";
    public int BoneIndex { get; set; } = 1;
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
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;
    public bool ShowPlayerHpBar { get; set; } = true;
    public bool ShowHudPlayerHpBar { get; set; } = false;

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // NPC Defaults
    public int DefaultNpcLevel { get; set; } = 90;
    public float DefaultNpcHpMultiplier { get; set; } = 1.0f;

    // Default behavior for auto-selected NPCs (0=Dummy, 1=BasicMelee, 2=BasicRanged, 3=Boss)
    public int DefaultNpcBehaviorType { get; set; } = 1;

    // Animation Commands
    // Attack: empty = use ActionTimeline defaults; set a command (e.g., "/gsit") for custom
    public string PlayerMeleeAttackCommand { get; set; } = "";
    public string PlayerRangedAttackCommand { get; set; } = "";

    // Death: empty = use BypassEmote-style timeline (works on both player + NPC, no unlock needed)
    //        set a command (e.g., "/playdead") to use that instead (player only; NPC always uses timeline)
    public string PlayerDeathCommand { get; set; } = "";

    // Death emote ID override (0 = auto-detect "Play Dead" from Emote sheet)
    public uint DeathEmoteId { get; set; } = 0;

    // Victory: command executed when one party wins
    public string PlayerVictoryCommand { get; set; } = "";
    public string TargetVictoryCommand { get; set; } = "";

    // Glamourer: apply a preset on player death
    public bool ApplyGlamourerOnDeath { get; set; } = false;
    public string DeathGlamourerDesignId { get; set; } = "";

    // Glamourer: apply a preset on start / reset / reboot (combat ready look)
    public bool ApplyGlamourerOnReset { get; set; } = false;
    public string ResetGlamourerDesignId { get; set; } = "";

    // Shortcuts bar
    public bool ShowShortcuts { get; set; } = false;

    // Death Cam toolbar
    public bool ShowDeathCamToolbar { get; set; } = false;

    // Target Behaviors
    public bool EnableTargetApproach { get; set; } = false;
    public float TargetApproachDistance { get; set; } = 3.0f;

    // Aggro propagation: nearby BattleNpcs auto-added as targets when one is engaged
    public bool EnableAggroPropagation { get; set; } = false;
    public float AggroPropagationRange { get; set; } = 15.0f;

    // Maximum number of active targets
    public int MaxTargets { get; set; } = 10;

    // Ragdoll physics (Experimental)
    public bool EnableRagdoll { get; set; } = false;
    public float RagdollActivationDelay { get; set; } = 1.0f;
    public float RagdollGravity { get; set; } = 9.8f;
    public float RagdollDamping { get; set; } = 0.97f;
    public float RagdollFloorOffset { get; set; } = 0.1f;  // Lower terrain by this amount to avoid joint-floor collision artifacts
    public bool RagdollSelfCollision { get; set; } = true; // Body parts collide with each other (arms vs torso, etc)

    // Force Camera Follow — overrides camera look-at to track a specific bone
    public bool EnableCameraFollow { get; set; } = false;
    public int CameraFollowBoneIndex { get; set; } = 1;

    // Hit VFX on player when taking damage (empty = disabled)
    public string HitVfxPath { get; set; } = "vfx/common/eff/dk05th_stdn0t.avfx";
    public bool EnableHitVfx { get; set; } = true;

    // Player HP Bar
    public float PlayerHpBarYOffset { get; set; } = 0.3f;
    public string CustomPlayerName { get; set; } = "";

    // Death Cam (Experimental)
    public bool EnableDeathCam { get; set; } = false;
    public int DeathCamBoneIndex { get; set; } = 1; // n_hara (waist)
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

    // Recent NPCs
    public List<uint> RecentNpcIds { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
