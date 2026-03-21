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

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // NPC Defaults
    public int DefaultNpcLevel { get; set; } = 90;
    public float DefaultNpcHpMultiplier { get; set; } = 1.0f;

    // Safety
    public bool RequireHyperborea { get; set; } = true;

    // Animation Commands
    // Attack: empty = use ActionTimeline defaults; set a command (e.g., "/gsit") for custom
    public string PlayerMeleeAttackCommand { get; set; } = "";
    public string PlayerRangedAttackCommand { get; set; } = "";

    // Death: command executed on the player character when simulated HP reaches 0
    public string PlayerDeathCommand { get; set; } = "/playdead";

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
