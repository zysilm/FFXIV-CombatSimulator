using System;
using System.Collections.Generic;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Npcs;

/// <summary>
/// Builds a simulated enemy's behavior straight from its weapon — no manual action-type
/// selection. A humanoid whose main-hand weapon resolves to a job (<see cref="NpcWeaponClassifier"/>)
/// casts that job's real damage kit (<see cref="JobActionKitProvider"/>) at the sim enemy level;
/// monsters and NPC-only weapons resolve to no job and auto-attack only. Training dummies never
/// attack. This replaces the old template-per-behavior-type system (BasicMelee/Ranged/Boss with
/// hardcoded fake skills).
/// </summary>
public class NpcActionProfileProvider
{
    private const int PhysicalRangedAutoPotency = 82;
    private const uint TrainingDummyBNpcBaseId = 541;

    private readonly JobActionKitProvider jobKits;
    private readonly Configuration config;
    private readonly IPluginLog log;

    public NpcActionProfileProvider(JobActionKitProvider jobKits, Configuration config, IPluginLog log)
    {
        this.jobKits = jobKits;
        this.config = config;
        this.log = log;
    }

    /// <summary>
    /// Derive an enemy's behavior. <paramref name="jobId"/> is the ClassJob the weapon resolved to
    /// (0 = monster / NPC-only weapon → auto-attack only). <paramref name="weaponStyle"/> sets the
    /// auto-attack motion; <paramref name="level"/> caps the job kit.
    /// </summary>
    public NpcBehavior Create(string npcName, uint jobId, NpcAttackStyle weaponStyle, int level, uint bNpcBaseId = 0)
    {
        // Training dummies never attack.
        if (bNpcBaseId == TrainingDummyBNpcBaseId ||
            npcName.Contains("dummy", StringComparison.OrdinalIgnoreCase))
            return NpcBehavior.Create(NpcBehaviorType.TrainingDummy);

        var behavior = CreateBaseBehavior(weaponStyle);

        // Humanoid with a resolved job → its real damage kit; everything else auto-attacks only.
        if (jobId != 0)
        {
            var kit = jobKits.BuildKit(jobId, level, Math.Max(1, config.EnemyMaxRealSkills));
            if (kit.Count > 0)
            {
                behavior.Skills = new List<NpcSkill>(kit);
                log.Info($"Enemy '{npcName}' → job {jobId}, {kit.Count} real skills (style={weaponStyle}, lvl≤{level}).");
            }
            else
            {
                log.Info($"Enemy '{npcName}' job {jobId} yielded no kit at level {level}; auto-attack only.");
            }
        }

        return behavior;
    }

    // Auto-attack base stats from the weapon style. Casters auto-attack in melee (only their spells
    // are ranged); physical ranged (bow/gun) auto-attacks at range. Skills come from the job kit.
    private static NpcBehavior CreateBaseBehavior(NpcAttackStyle weaponStyle)
    {
        var behavior = new NpcBehavior(); // melee defaults (delay 3, range 3, potency 110, auto id 7)
        switch (weaponStyle)
        {
            case NpcAttackStyle.Ranged:
                behavior.AutoAttackStyle = NpcAttackStyle.Ranged;
                behavior.AutoAttackActionId = 97;
                behavior.AutoAttackRange = 25f;
                behavior.AutoAttackPotency = PhysicalRangedAutoPotency;
                behavior.MoveSpeed = 5f;
                break;
            case NpcAttackStyle.Magic:
                behavior.AutoAttackStyle = NpcAttackStyle.Magic;
                behavior.AutoAttackActionId = 7; // caster melee auto-attack
                behavior.MoveSpeed = 5f;
                break;
            // Melee / Auto → NpcBehavior defaults
        }
        return behavior;
    }
}
