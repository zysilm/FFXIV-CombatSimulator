// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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

/// <summary>
/// Turns an attack into damage. All the numbers come from <see cref="CombatBudgetModel"/>; this class
/// only decides which side of it applies (player, companion or enemy) and rolls the dice.
/// </summary>
public class DamageCalculator
{
    private const int DefaultAutoAttackPotency = 110;
    private readonly Random rng;

    /// <summary>Whether Action Mode is on. It changes the player's reference output and the enemy threat.</summary>
    public Func<bool> IsActionMode { get; set; } = () => false;

    /// <summary>The player's maximum HP, which sizes everyone's damage (0: use the attacker's own).</summary>
    public Func<float> PlayerMaxHp { get; set; } = () => 0f;

    public DamageCalculator() : this(new Random())
    {
    }

    public DamageCalculator(Random rng)
    {
        this.rng = rng;
    }

    /// <summary>A player's or companion's skill against an enemy.</summary>
    public DamageResult Calculate(
        SimulatedEntityState source,
        SimulatedEntityState target,
        ActionData actionData,
        bool isCombo = false,
        bool enableCrit = true,
        bool enableDh = true,
        float damageMultiplier = 1.0f)
    {
        var potency = isCombo && actionData.ComboPotency > 0 ? actionData.ComboPotency : actionData.Potency;
        return Deal(source, target, potency, actionData.DamageType, enableCrit, enableDh, damageMultiplier);
    }

    /// <summary>
    /// An attack given as a bare potency: every enemy attack (auto-attack or skill), and the auto-attacks
    /// of the player and of companions.
    /// </summary>
    public DamageResult CalculateNpcAutoAttack(
        SimulatedEntityState source,
        SimulatedEntityState target,
        int potency = DefaultAutoAttackPotency,
        SimDamageType damageType = SimDamageType.Physical)
        => Deal(source, target, potency, damageType, enableCrit: false, enableDh: false, damageMultiplier: 1f);

    private DamageResult Deal(SimulatedEntityState source, SimulatedEntityState target, int potency,
        SimDamageType damageType, bool enableCrit, bool enableDh, float damageMultiplier)
    {
        var result = new DamageResult { DamageType = damageType };
        if (potency <= 0)
            return result;

        // One rate for everyone: the player's, sized by the player's HP, times the attacker's level worth.
        // The same skill hits as hard from an enemy or an ally as from the player.
        var actionMode = IsActionMode();
        var playerHp = source.IsPlayer ? source.MaxHp : PlayerMaxHp();
        if (playerHp <= 0f) playerHp = source.MaxHp;
        var levelFactor = source.IsPlayer ? 1f : source.LevelFactor;
        var damage = potency * CombatBudgetModel.DamagePerPotency(playerHp, actionMode, levelFactor)
                     * MathF.Max(0f, damageMultiplier)
                     * MathF.Max(0f, target.IncomingDamageScale);

        damage *= CombatBudgetModel.RollModifier(rng.NextDouble(), rng.NextDouble(), enableCrit, enableDh,
            out var isCritical, out var isDirectHit);
        result.IsCritical = isCritical;
        result.IsDirectHit = isDirectHit;

        damage *= StatusModifier(source, target);
        damage *= 0.95f + 0.10f * (float)rng.NextDouble();

        result.Damage = (int)Math.Clamp(MathF.Floor(damage), 1f, int.MaxValue);
        return result;
    }

    private static float StatusModifier(SimulatedEntityState source, SimulatedEntityState target)
    {
        var modifier = 1f;
        foreach (var status in source.StatusEffects)
            if (status.StatusId == 1000001)
                modifier *= 1.1f;
        foreach (var status in target.StatusEffects)
            if (status.StatusId == 1000002)
                modifier *= 1.1f;
        return modifier;
    }
}
