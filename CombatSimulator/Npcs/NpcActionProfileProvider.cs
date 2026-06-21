using System;
using System.Collections.Generic;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Npcs;

/// <summary>
/// Builds the best available local action profile for a simulated enemy.
/// Exact server-side enemy rotations are not exposed as a simple client Excel table,
/// so this provider combines known object identity, action sheet metadata, and
/// conservative role/name heuristics. Unknown enemies still fall back to the
/// existing templates.
/// </summary>
public class NpcActionProfileProvider
{
    private const int PhysicalRangedAutoPotency = 82;

    private readonly ActionDataProvider actionDataProvider;
    private readonly IPluginLog log;

    public NpcActionProfileProvider(ActionDataProvider actionDataProvider, IPluginLog log)
    {
        this.actionDataProvider = actionDataProvider;
        this.log = log;
    }

    public NpcBehavior CreateForSpawn(NpcSpawnRequest request, string npcName)
    {
        var behavior = CreateBaseBehavior(request.BehaviorType, npcName);

        if (request.IsRanged)
            ForceRanged(behavior);

        ApplyKnownProfile(behavior, npcName, request.BNpcBaseId, request.ENpcBaseId);
        EnrichFromActionData(behavior);

        return behavior;
    }

    public NpcBehavior CreateForSelectedTarget(string npcName, NpcBehaviorType fallbackType)
    {
        return CreateForSelectedTarget(npcName, fallbackType, NpcAttackStyle.Auto);
    }

    public NpcBehavior CreateForSelectedTarget(string npcName, NpcBehaviorType fallbackType, NpcAttackStyle weaponStyle)
    {
        if (weaponStyle == NpcAttackStyle.Ranged)
            return CreatePhysicalRangedBehavior();

        var behavior = CreateBaseBehavior(fallbackType, npcName);
        if (weaponStyle == NpcAttackStyle.Magic)
            ForceMagic(behavior);

        ApplyKnownProfile(behavior, npcName, 0, 0);
        EnrichFromActionData(behavior);
        return behavior;
    }

    private static NpcBehavior CreateBaseBehavior(NpcBehaviorType fallbackType, string npcName)
    {
        var lowerName = npcName.ToLowerInvariant();
        if (LooksPhysicalRanged(lowerName))
            return CreatePhysicalRangedBehavior();

        var role = GuessRole(lowerName);
        if (role != null)
            return NpcBehavior.Create(role.Value);

        return NpcBehavior.Create(fallbackType);
    }

    private static NpcBehaviorType? GuessRole(string lowerName)
    {
        if (ContainsAny(lowerName, "mage", "magus", "thaumaturge", "conjurer", "caster", "wizard", "sorcerer", "healer"))
            return NpcBehaviorType.BasicRanged;

        if (ContainsAny(lowerName, "dummy", "striking dummy"))
            return NpcBehaviorType.TrainingDummy;

        return null;
    }

    private static bool LooksPhysicalRanged(string lowerName)
    {
        return ContainsAny(lowerName, "archer", "bowman", "marksman", "sharpshooter", "gunner", "musketeer", "sniper");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ForceRanged(NpcBehavior behavior)
    {
        behavior.AutoAttackRange = Math.Max(behavior.AutoAttackRange, 25f);
        behavior.AutoAttackPotency = Math.Min(behavior.AutoAttackPotency, PhysicalRangedAutoPotency);
        behavior.AutoAttackActionId = behavior.AutoAttackActionId == 7 ? 97u : behavior.AutoAttackActionId;
        behavior.AutoAttackStyle = NpcAttackStyle.Ranged;

        foreach (var skill in behavior.Skills)
        {
            skill.Range = Math.Max(skill.Range, 20f);
            if (skill.AttackStyle == NpcAttackStyle.Auto)
                skill.AttackStyle = NpcAttackStyle.Ranged;
        }
    }

    private static void ForceMagic(NpcBehavior behavior)
    {
        behavior.AutoAttackRange = Math.Max(behavior.AutoAttackRange, 25f);
        behavior.AutoAttackPotency = Math.Min(behavior.AutoAttackPotency, 100);
        if (behavior.AutoAttackActionId == 97 || behavior.AutoAttackActionId == 98)
            behavior.AutoAttackActionId = 7;
        behavior.AutoAttackStyle = NpcAttackStyle.Magic;

        if (behavior.Skills.Count == 0 || HasPhysicalRangedActionCarriers(behavior))
        {
            behavior.Skills = CreateMagicSkillSet();
            return;
        }

        // Don't blanket-force every skill to Magic + 20y range — that turned a caster's MELEE
        // weaponskill (e.g. Heavy Swing) into a ranged cast. EnrichFromActionData (runs next)
        // classifies each skill by its real action data (magical→Magic, short physical→Melee) and
        // fills the real range, so melee skills stay melee.
    }

    private static bool HasPhysicalRangedActionCarriers(NpcBehavior behavior)
    {
        foreach (var skill in behavior.Skills)
        {
            if (skill.ActionId is 97 or 98)
                return true;
        }

        return false;
    }

    private static List<NpcSkill> CreateMagicSkillSet()
    {
        return new List<NpcSkill>
        {
            new()
            {
                Name = "Fire",
                ActionId = 141,
                Potency = 300,
                Cooldown = 8.0f,
                CastTime = 2.5f,
                Range = 25.0f,
                AttackStyle = NpcAttackStyle.Magic,
                Priority = 2,
            },
            new()
            {
                Name = "Thunder",
                ActionId = 144,
                Potency = 150,
                Cooldown = 30.0f,
                CastTime = 0f,
                Range = 25.0f,
                AttackStyle = NpcAttackStyle.Magic,
                Priority = 1,
            },
        };
    }

    private static void ApplyKnownProfile(NpcBehavior behavior, string npcName, uint bNpcBaseId, uint eNpcBaseId)
    {
        // Striking dummy / training dummies should not suddenly cast template skills.
        if (bNpcBaseId == 541 || npcName.Contains("dummy", StringComparison.OrdinalIgnoreCase))
        {
            behavior.AutoAttackDelay = float.MaxValue;
            behavior.AutoAttackRange = 0f;
            behavior.Skills.Clear();
            return;
        }

        // Humanoid archers/ranged soldiers: use real player-side ranged action
        // rows as animation/VFX carriers until a verified enemy-action table is available.
        if (LooksPhysicalRanged(npcName.ToLowerInvariant()))
            ForceRanged(behavior);
    }

    private static NpcBehavior CreatePhysicalRangedBehavior()
    {
        return new NpcBehavior
        {
            AutoAttackDelay = 3.0f,
            AutoAttackRange = 25.0f,
            AutoAttackPotency = PhysicalRangedAutoPotency,
            AutoAttackActionId = 97,
            AutoAttackStyle = NpcAttackStyle.Ranged,
            MoveSpeed = 5.0f,
            LeashDistance = 40.0f,
            Skills = new()
            {
                new NpcSkill
                {
                    Name = "Ranged Shot",
                    ActionId = 97,
                    Potency = 150,
                    Cooldown = 8.0f,
                    CastTime = 0f,
                    Range = 25.0f,
                    AttackStyle = NpcAttackStyle.Ranged,
                    Priority = 2,
                },
                new NpcSkill
                {
                    Name = "Power Shot",
                    ActionId = 98,
                    Potency = 220,
                    Cooldown = 18.0f,
                    CastTime = 1.5f,
                    Range = 25.0f,
                    AttackStyle = NpcAttackStyle.Ranged,
                    Priority = 1,
                },
            },
        };
    }

    private void EnrichFromActionData(NpcBehavior behavior)
    {
        EnrichAutoAttack(behavior);

        foreach (var skill in behavior.Skills)
        {
            var data = actionDataProvider.GetActionData(skill.ActionId);
            if (data == null)
                continue;

            if (string.IsNullOrWhiteSpace(skill.Name))
                skill.Name = data.Name;
            if (skill.Potency <= 0)
                skill.Potency = data.Potency;
            if (skill.Range <= 0)
                skill.Range = data.Range;
            if (skill.Radius <= 0)
                skill.Radius = data.Radius;
            if (skill.CastTime <= 0 && data.CastTime > 0)
                skill.CastTime = data.CastTime;
            if (skill.Cooldown <= 0)
                skill.Cooldown = Math.Max(2.5f, data.RecastTime);

            if (skill.AttackStyle == NpcAttackStyle.Auto)
            {
                skill.AttackStyle = data.DamageType == SimDamageType.Magical
                    ? NpcAttackStyle.Magic
                    : data.Range > 5f
                        ? NpcAttackStyle.Ranged
                        : NpcAttackStyle.Melee;
            }
        }
    }

    private void EnrichAutoAttack(NpcBehavior behavior)
    {
        var data = actionDataProvider.GetActionData(behavior.AutoAttackActionId);
        if (data == null)
            return;

        if (behavior.AutoAttackStyle == NpcAttackStyle.Auto)
        {
            behavior.AutoAttackStyle = data.DamageType == SimDamageType.Magical
                ? NpcAttackStyle.Magic
                : data.Range > 5f
                    ? NpcAttackStyle.Ranged
                    : NpcAttackStyle.Melee;
        }
    }
}
