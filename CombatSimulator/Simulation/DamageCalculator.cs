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
    private readonly CombatStatProvider statProvider;

    public DamageCalculator() : this(new CombatStatProvider())
    {
    }

    public DamageCalculator(CombatStatProvider statProvider)
    {
        this.statProvider = statProvider;
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
        var potency = ResolvePotency(actionData, isCombo);
        var result = CalculateDirectDamage(source, potency, actionData.DamageType, enableCrit, enableDh, applyVariance: true);
        if (result.Damage <= 0)
            return result;

        var damage = result.Damage;
        damage = ApplyLevelCorrection(damage, source.Level, target.Level);
        damage = ApplyStatusDamageModifiers(damage, source, target);
        damage = (int)MathF.Floor(damage * MathF.Max(0f, damageMultiplier));

        result.Damage = Math.Max(1, damage);
        return result;
    }

    public DamageResult CalculateNpcAutoAttack(SimulatedEntityState npc, SimulatedEntityState target, int potency = 110)
    {
        var raw = CalculateDirectDamage(npc, potency, SimDamageType.Physical, enableCrit: false, enableDh: false, applyVariance: false);
        var mitigated = ApplyDamageTaken(raw.Damage, target, SimDamageType.Physical);

        raw.Damage = Math.Max(1, ApplyLevelCorrection(mitigated, npc.Level, target.Level));
        raw.DamageType = SimDamageType.Physical;
        return raw;
    }

    private DamageResult CalculateDirectDamage(
        SimulatedEntityState source,
        int potency,
        SimDamageType damageType,
        bool enableCrit,
        bool enableDh,
        bool applyVariance)
    {
        var result = new DamageResult { DamageType = damageType };
        if (potency <= 0)
            return result;

        var level = Math.Clamp(source.Level > 0 ? source.Level : 90, 1, 300);
        var mods = statProvider.GetLevelModifiers(level);

        var attackStat = ResolveAttackStat(source, damageType, mods);
        var fAtk = FAttackPower(attackStat, mods.Main, source.IsTank);
        var fDet = FDetermination(ResolveStat(source.Determination, mods.Main), mods);
        var fTnc = source.IsTank ? FTenacity(ResolveStat(source.Tenacity, mods.Sub), mods) : 1000;
        var fWd = FWeaponDamage(source, damageType, mods);
        var traitPct = source.DamageTraitPct > 0 ? source.DamageTraitPct : 100;

        var damage = (long)potency * fAtk;
        damage = damage * fDet / 1000;
        damage /= 100;
        damage = damage * fTnc / 1000;
        damage = damage * fWd / 100;
        damage = damage * traitPct / 100;

        var critStat = ResolveStat(source.CriticalHit, mods.Sub);
        var critRate = Math.Clamp(FCritRate(critStat, mods), 0, 1000);
        var critTerm = 1000;
        if (enableCrit && rng.Next(1000) < critRate)
        {
            result.IsCritical = true;
            critTerm = FCritStrength(critStat, mods);
        }

        var dhStat = ResolveStat(source.DirectHit, mods.Sub);
        var dhRate = Math.Clamp(FDirectHitRate(dhStat, mods), 0, 1000);
        var dhTerm = 100;
        if (enableDh && rng.Next(1000) < dhRate)
        {
            result.IsDirectHit = true;
            dhTerm = 125;
        }

        damage = damage * critTerm / 1000;
        damage = damage * dhTerm / 100;

        if (applyVariance)
            damage = damage * rng.Next(95, 106) / 100;

        result.Damage = Math.Max(0, (int)Math.Min(int.MaxValue, damage));
        return result;
    }

    private int ApplyDamageTaken(int rawDamage, SimulatedEntityState target, SimDamageType damageType)
    {
        if (rawDamage <= 0)
            return 0;

        var level = Math.Clamp(target.Level > 0 ? target.Level : 90, 1, 300);
        var mods = statProvider.GetLevelModifiers(level);
        var defense = damageType == SimDamageType.Magical
            ? ResolveStat(target.MagicDefense, mods.Sub)
            : ResolveStat(target.Defense, mods.Sub);

        var fDefense = FDefense(defense, mods);
        var damage = (long)rawDamage * Math.Clamp(100 - fDefense, 1, 100) / 100;

        var fTnc = target.IsTank ? FTenacity(ResolveStat(target.Tenacity, mods.Sub), mods) : 1000;
        damage = damage * (2000 - fTnc) / 1000;
        damage = damage * rng.Next(95, 106) / 100;

        return Math.Max(1, (int)Math.Min(int.MaxValue, damage));
    }

    private int FWeaponDamage(SimulatedEntityState source, SimDamageType damageType, LevelModifiers mods)
    {
        var weaponDamage = damageType == SimDamageType.Magical && source.MagicWeaponDamage > 0
            ? source.MagicWeaponDamage
            : source.WeaponDamage;
        if (weaponDamage <= 0)
            weaponDamage = EstimateWeaponDamage(source.Level);

        var jobMod = statProvider.GetPrimaryJobModifier(source.ClassJobId, damageType);
        return (int)Math.Floor(mods.Main * jobMod / 1000.0) + weaponDamage;
    }

    private static int FAttackPower(int attackPower, int mainMod, bool isTank)
    {
        var coeff = isTank ? 115 : 165;
        return (int)Math.Floor(coeff * (attackPower - mainMod) / (double)mainMod) + 100;
    }

    private static int FDetermination(int det, LevelModifiers mods)
        => (int)Math.Floor(140.0 * (det - mods.Main) / mods.Div + 1000);

    private static int FTenacity(int tenacity, LevelModifiers mods)
        => (int)Math.Floor(100.0 * (tenacity - mods.Sub) / mods.Div + 1000);

    private static int FCritRate(int crit, LevelModifiers mods)
        => (int)Math.Floor(200.0 * (crit - mods.Sub) / mods.Div + 50);

    private static int FCritStrength(int crit, LevelModifiers mods)
        => (int)Math.Floor(200.0 * (crit - mods.Sub) / mods.Div) + 1400;

    private static int FDirectHitRate(int directHit, LevelModifiers mods)
        => (int)Math.Floor(550.0 * (directHit - mods.Sub) / mods.Div);

    private static int FDefense(int defense, LevelModifiers mods)
        => (int)Math.Floor(15.0 * defense / mods.Div);

    private static int ResolvePotency(ActionData actionData, bool isCombo)
        => isCombo && actionData.ComboPotency > 0 ? actionData.ComboPotency : actionData.Potency;

    private static int ResolveAttackStat(SimulatedEntityState source, SimDamageType damageType, LevelModifiers mods)
    {
        if (damageType == SimDamageType.Magical && source.AttackMagicPotency > 0)
            return source.AttackMagicPotency;
        if (damageType != SimDamageType.Magical && source.AttackPower > 0)
            return source.AttackPower;
        return source.MainStat > 0 ? source.MainStat : mods.Main + 100;
    }

    private static int ResolveStat(int value, int fallback) => value > 0 ? value : fallback;

    private static int EstimateWeaponDamage(int level)
        => level switch
        {
            >= 100 => 132,
            >= 90 => 120,
            >= 80 => 104,
            >= 70 => 92,
            >= 60 => 75,
            >= 50 => 59,
            _ => Math.Max(5, 10 + level),
        };

    private static int ApplyLevelCorrection(int damage, int sourceLevel, int targetLevel)
    {
        if (damage <= 0 || sourceLevel <= 0 || targetLevel <= 0 || sourceLevel >= targetLevel)
            return damage;

        var penaltyPct = Math.Clamp((targetLevel - sourceLevel) * 2.5f, 0f, 90f);
        return (int)MathF.Floor(damage * (100f - penaltyPct) / 100f);
    }

    private static int ApplyStatusDamageModifiers(int damage, SimulatedEntityState source, SimulatedEntityState target)
    {
        var result = damage;
        foreach (var status in source.StatusEffects)
        {
            if (status.StatusId == 1000001)
                result = (int)MathF.Floor(result * 1.1f);
        }

        foreach (var status in target.StatusEffects)
        {
            if (status.StatusId == 1000002)
                result = (int)MathF.Floor(result * 1.1f);
        }

        return result;
    }
}
