// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Every battle: the fallen stay down. Its words on the HP bars are what the engine's mechanics left
/// (the dead, the executed, the captive, whatever rule took them) and the player's; the other parts
/// add theirs for the living allies, and with none "Fighting" stands.
/// </summary>
public sealed class BaseRule : BattleRule
{
    public override string Id => BattleRuleFactory.Base;

    public override string? StatusWord(BattleContext ctx, SimulatedEntityState who)
    {
        if (!who.IsAlive)
            return who.IsPlayer && ctx.Allies.LordCorpseCaptured ? "Corpse captured"
                : who.Executed ? "Executed"
                : "Killed in battle";
        if (who.IsCaptive)
            return "Captive";
        if (who.IsPlayer)
            return "Fighting";
        return null;
    }
}
