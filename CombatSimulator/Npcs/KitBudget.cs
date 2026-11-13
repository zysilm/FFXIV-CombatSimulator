// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Simulation;

namespace CombatSimulator.Npcs;

/// <summary>
/// Measures the output an enemy's or companion's kit sustains and records it on the entity, so its
/// damage can be budgeted against it (see <see cref="CombatBudgetModel"/>). Measured once per kit: the
/// signature changes when skills are added, removed or re-priced.
/// </summary>
public static class KitBudget
{
    /// <param name="playerPrice">MP price of an action for a player, or 0 when unknown.</param>
    /// <param name="usesMp">Whether skills are paid for in MP (enemies and companions both do).</param>
    /// <remarks>
    /// An enemy's kit is played out for as long as its tier is meant to live, because the MP pool it
    /// opens with makes its first seconds stronger than its later ones; set the tier before measuring.
    /// </remarks>
    public static void Measure(SimulatedEntityState state, NpcBehavior behavior, Func<uint, int> playerPrice, bool usesMp)
    {
        var signature = Signature(state, behavior, usesMp);
        if (state.KitSignature == signature && state.KitOutputClassic > 0f)
            return;

        Span<KitSkill> skills = behavior.Skills.Count <= 32
            ? stackalloc KitSkill[behavior.Skills.Count]
            : new KitSkill[behavior.Skills.Count];
        for (var i = 0; i < behavior.Skills.Count; i++)
        {
            var skill = behavior.Skills[i];
            var cost = usesMp ? CombatBudgetModel.EnemySkillMpCost(playerPrice(skill.ActionId), state.MaxMp) : 0;
            // A generated skill with an area gets its real cast back as a dodgeable windup (at most 5 s);
            // one without stays instant. Mirrors NpcAiController.EffectiveCastTime.
            var cast = skill.CastTime > 0f ? skill.CastTime
                : skill.Radius > 0f ? MathF.Min(MathF.Max(0f, skill.NativeCastTime), 5f) : 0f;
            skills[i] = new KitSkill(skill.Potency, skill.Cooldown, cost, skill.HpThreshold < 1f, cast);
        }

        // An enemy over the life its tier is meant to have; a companion over its own window.
        var seconds = usesMp && !state.IsCompanion ? CombatBudgetModel.SecondsToKill(state.Tier) : CombatBudgetModel.CompanionKitWindow;
        state.KitOutputClassic = CombatBudgetModel.KitOutput(behavior.AutoAttackPotency, behavior.AutoAttackDelay,
            skills, 1f, usesMp, state.MaxMp, seconds, castsBlock: true);
        state.KitOutputAction = CombatBudgetModel.KitOutput(behavior.AutoAttackPotency, behavior.AutoAttackDelay,
            skills, CombatBudgetModel.ActionModePace, usesMp, state.MaxMp, seconds, castsBlock: false);
        state.KitSignature = signature;
    }

    private static int Signature(SimulatedEntityState state, NpcBehavior behavior, bool usesMp)
    {
        var hash = new HashCode();
        hash.Add(usesMp);
        hash.Add((int)state.Tier);
        hash.Add(state.MaxMp);
        hash.Add(behavior.AutoAttackPotency);
        hash.Add(behavior.AutoAttackDelay);
        foreach (var skill in behavior.Skills)
        {
            hash.Add(skill.ActionId);
            hash.Add(skill.Potency);
            hash.Add(skill.Cooldown);
            hash.Add(skill.HpThreshold);
            hash.Add(skill.CastTime);
            hash.Add(skill.NativeCastTime);
            hash.Add(skill.Radius);
        }
        var value = hash.ToHashCode();
        return value == 0 ? 1 : value;
    }
}
