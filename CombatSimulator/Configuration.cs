using System;
using System.Collections.Generic;
using CombatSimulator.Dev;
using Dalamud.Configuration;
using Dalamud.Plugin;

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
    public int JointType { get; set; } // 0=Ball, 1=Hinge
    public float TwistMinAngle { get; set; }
    public float TwistMaxAngle { get; set; }
    public string? Description { get; set; }      // human-readable label for UI
    // Soft body spring settings (for breast/jiggle bones)
    public bool SoftBody { get; set; }             // use soft springs + AngularServo instead of rigid + AngularMotor
    public float SoftSpringFreq { get; set; } = 6f;    // BallSocket spring frequency (Hz), lower = bouncier
    public float SoftSpringDamp { get; set; } = 0.4f;  // BallSocket damping ratio, lower = more oscillation
    public float SoftServoFreq { get; set; } = 4f;     // AngularServo spring frequency (Hz), controls return speed
    public float SoftServoDamp { get; set; } = 0.35f;  // AngularServo damping ratio, controls bounce on return
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
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public float SidebarWidth { get; set; } = 130f;
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
    public uint TargetVictoryEmoteId { get; set; } = 0; // 0 = none, otherwise Emote sheet RowId

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
    public bool EnableNpcTargetPlayer { get; set; } = true;
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
    public int RagdollSolverIterations { get; set; } = 8;
    public bool RagdollSelfCollision { get; set; } = true; // Body parts collide with each other (arms vs torso, etc)
    public float RagdollFriction { get; set; } = 1.0f; // Surface friction (0=ice, 1=grippy). Lower = limbs slide more realistically.
    // Weapon drop physics
    public bool RagdollWeaponDrop { get; set; } = true; // Weapon detaches and falls on death (uses battle/dead instead of play-dead)
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

    // Dev (Experimental) — hidden behind easter egg
    public bool RagdollVerboseLog { get; set; } = false;
    public bool RagdollFollowPosition { get; set; } = false; // Update GameObject.Position to follow ragdoll root (prevents model unload on long falls)
    public bool EnableVictorySequence { get; set; } = false;
    public List<VictorySequenceStage> VictorySequenceStages { get; set; } = new();
    public List<VictoryCinematicPreset> VictoryCinematicPresets { get; set; } = new();
    public bool RagdollNpcCollision { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    public bool RagdollNpcSettleCollision { get; set; } = true;

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

    // Skill VFX on combat actions (cast circles, impact particles)
    public bool EnableSkillVfx { get; set; } = false;

    // Hit VFX on player when taking damage (empty = disabled)
    public string HitVfxPath { get; set; } = "vfx/common/eff/dk05th_stdn0t.avfx";
    public bool EnableHitVfx { get; set; } = true;

    // Player HP Bar
    public float PlayerHpBarYOffset { get; set; } = 0.3f;
    public string CustomPlayerName { get; set; } = "";

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
