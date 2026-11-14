// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Warring States with Retreat: the enemy goes for the lord's body. The seizure itself (who goes, the
/// count at the body, carrying it off) is <see cref="CorpseSeizure{TNpc}"/>, still run by the plugin
/// after the engine (<c>Plugin.TickWar</c>; the <see cref="IBattleRule.TickAfterEngine"/> hook is stage
/// B6). This part is the seizers' target choice: the one standing in their way is drawn at x100, and a
/// new one makes them draw again. Moved from <c>CombatCompanionManager.Lord.cs</c> and <c>.Aggro.cs</c>
/// (refactor stage B5).
/// </summary>
public sealed class CorpseSeizureRule : BattleRule
{
    // Only those at the body stand in a seizer's way; a retainer far off is left to the ordinary
    // choice (or a ranged seizer would stop to shoot it while the body lay open).
    private const float AtBody = LordGuardRule.LordContestRadius + 2f;

    private readonly LordGuardRule guard;

    public CorpseSeizureRule(LordGuardRule guard) => this.guard = guard;

    public override string Id => BattleRuleFactory.Seizure;

    /// <summary>
    /// The one an enemy going for the lord's body must cut down: the one praying over it (it cannot
    /// fight back), else the retainer nearest the body. 0 for any other enemy, or when no retainer
    /// is left. The ordinary target choice spreads enemies over targets, which kept a crowd of seizers
    /// off the one retainer holding the body for good.
    /// </summary>
    public override uint HolderFor(BattleContext ctx, SimulatedNpc enemy)
    {
        if (!ctx.Rules.LordBodyIsGuarded || !ctx.Allies.Seizers.Contains(enemy.SimulatedEntityId) || !guard.TryGetLordBody(ctx, out var body, out _))
            return 0;
        var prayerId = guard.PrayerId;
        if (prayerId != 0 && ctx.Allies.Get(prayerId) is { } prayer && ctx.Allies.CanAct(prayer) &&
            CombatantMath.FlatDistance(ctx.World.PositionOf(prayer), body) <= AtBody)
            return prayer.SimulatedEntityId;
        CombatCompanion? nearest = null;
        var best = AtBody;
        foreach (var companion in ctx.Allies.All)
        {
            if (!ctx.Allies.CanAct(companion))
                continue;
            var d = CombatantMath.FlatDistance(ctx.World.PositionOf(companion), body);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        return nearest?.SimulatedEntityId ?? 0;
    }

    /// <summary>The one in the way, x100.</summary>
    public override float EnemyTargetWeight(BattleContext ctx, in EnemyTargetQuery query, float weight)
        => query.CandidateId == query.Holder ? weight * AggroModel.TaskGain : weight;
}
