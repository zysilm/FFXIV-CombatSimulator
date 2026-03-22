using System;

namespace CombatSimulator.Simulation;

public class DamageResult
{
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public SimDamageType DamageType { get; set; }
}

public class DamageCalculator
{
    private readonly Random rng = new();

    // Level mod table: (level) -> (base main, base sub, divisor)
    private static readonly (int baseMain, int baseSub, int div)[] LevelMods = new (int, int, int)[101];

    static DamageCalculator()
    {
        // Populate key levels (interpolate for others)
        SetLevelMod(1, 20, 56, 56);
        SetLevelMod(50, 202, 341, 341);
        SetLevelMod(60, 218, 354, 600);
        SetLevelMod(70, 292, 364, 900);
        SetLevelMod(80, 340, 380, 1900);
        SetLevelMod(90, 390, 400, 1900);
        SetLevelMod(100, 440, 420, 2780);

        // Interpolate missing levels
        InterpolateLevels();
    }

    private static void SetLevelMod(int level, int baseMain, int baseSub, int div)
    {
        if (level is >= 0 and <= 100)
            LevelMods[level] = (baseMain, baseSub, div);
    }

    private static void InterpolateLevels()
    {
        // Key level breakpoints
        int[] keys = { 1, 50, 60, 70, 80, 90, 100 };

        for (int i = 0; i < keys.Length - 1; i++)
        {
            int fromLvl = keys[i];
            int toLvl = keys[i + 1];
            var from = LevelMods[fromLvl];
            var to = LevelMods[toLvl];
            int range = toLvl - fromLvl;

            for (int lvl = fromLvl + 1; lvl < toLvl; lvl++)
            {
                float t = (float)(lvl - fromLvl) / range;
                LevelMods[lvl] = (
                    (int)(from.baseMain + (to.baseMain - from.baseMain) * t),
                    (int)(from.baseSub + (to.baseSub - from.baseSub) * t),
                    (int)(from.div + (to.div - from.div) * t)
                );
            }
        }
    }

    public DamageResult Calculate(
        SimulatedEntityState source,
        SimulatedEntityState target,
        ActionData actionData,
        bool isCombo = false,
        bool enableCrit = true,
        bool enableDh = true,
        float damageMultiplier = 1.0f)
    {
        var result = new DamageResult { DamageType = actionData.DamageType };

        int potency = isCombo && actionData.ComboPotency > 0
            ? actionData.ComboPotency
            : actionData.Potency;

        if (potency <= 0)
        {
            result.Damage = 0;
            return result;
        }

        int level = Math.Clamp(source.Level, 1, 100);
        var mod = LevelMods[level];

        // f(MainStat)
        int mainStat = source.MainStat > 0 ? source.MainStat : mod.baseMain + 100;
        float fMain = (float)Math.Floor(195.0 * (mainStat - mod.baseMain) / mod.div + 100) / 100f;

        // f(Determination)
        int det = source.Determination > 0 ? source.Determination : mod.baseSub;
        float fDet = (float)Math.Floor(140.0 * (det - mod.baseMain) / mod.div + 1000) / 1000f;

        // Base damage
        float baseDamage = potency * fMain * fDet;

        // Defense mitigation
        int defense = actionData.DamageType == SimDamageType.Magical
            ? target.MagicDefense
            : target.Defense;
        if (defense <= 0)
            defense = mod.div / 10; // Default defense based on level

        float defMitigation = (float)Math.Floor(15.0 * mod.div / defense + 85) / 100f;
        defMitigation = Math.Clamp(defMitigation, 0.1f, 1.5f);

        float damage = baseDamage * defMitigation;

        // Critical hit
        int critStat = source.CriticalHit > 0 ? source.CriticalHit : mod.baseSub;
        float critRate = (float)Math.Floor(200.0 * (critStat - mod.baseSub) / mod.div + 50) / 1000f;
        float critMult = (float)Math.Floor(200.0 * (critStat - mod.baseSub) / mod.div + 1400) / 1000f;
        critRate = Math.Clamp(critRate, 0.05f, 1.0f);

        if (enableCrit && rng.NextDouble() < critRate)
        {
            result.IsCritical = true;
            damage *= critMult;
        }

        // Direct hit
        int dhStat = source.DirectHit > 0 ? source.DirectHit : mod.baseSub;
        float dhRate = (float)Math.Floor(550.0 * (dhStat - mod.baseSub) / mod.div) / 1000f;
        dhRate = Math.Clamp(dhRate, 0.0f, 1.0f);

        if (enableDh && rng.NextDouble() < dhRate)
        {
            result.IsDirectHit = true;
            damage *= 1.25f;
        }

        // RNG variance +-5%
        float variance = 0.95f + (float)rng.NextDouble() * 0.10f;
        damage *= variance;

        // Damage multiplier (user config)
        damage *= damageMultiplier;

        // Apply status effect modifiers
        foreach (var status in source.StatusEffects)
        {
            if (status.StatusId == 1000001) // Damage Up placeholder
                damage *= 1.1f;
        }

        foreach (var status in target.StatusEffects)
        {
            if (status.StatusId == 1000002) // Vulnerability placeholder
                damage *= 1.1f;
        }

        result.Damage = Math.Max(1, (int)Math.Floor(damage));
        return result;
    }

    public DamageResult CalculateNpcAutoAttack(SimulatedEntityState npc, SimulatedEntityState target, int potency = 110)
    {
        int npcLevel = npc.Level;
        int targetLevel = target.Level > 0 ? target.Level : 90;

        // Base damage scales quadratically with NPC level for meaningful numbers
        float baseDamage = potency * (1.0f + npcLevel * 0.1f + npcLevel * npcLevel * 0.002f);

        // Level difference multiplier: each level the NPC is above the target
        // increases damage significantly (and below reduces it)
        int levelDiff = npcLevel - targetLevel;
        float levelMultiplier = 1.0f + levelDiff * 0.08f;
        if (levelDiff > 0)
            levelMultiplier += levelDiff * levelDiff * 0.005f; // Extra scaling when NPC is higher
        levelMultiplier = Math.Max(0.2f, levelMultiplier);

        // Target defense mitigation (softened so defense doesn't negate everything)
        int defense = target.Defense > 0 ? target.Defense : 500;
        float mitigation = 2000f / (2000f + defense);

        float variance = 0.90f + (float)rng.NextDouble() * 0.20f;
        float damage = baseDamage * levelMultiplier * mitigation * variance;

        return new DamageResult
        {
            Damage = Math.Max(1, (int)Math.Floor(damage)),
            DamageType = SimDamageType.Physical,
        };
    }
}
