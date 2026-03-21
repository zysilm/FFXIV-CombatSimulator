using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CombatSimulator;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;
    public bool ShowPlayerHpBar { get; set; } = true;

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
