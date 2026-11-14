// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Warring States: the player is the lord. When the lord falls (once, until the lord is up again) every
/// part hears it; without Retreat every retainer left is taken captive, to be executed (with Retreat the
/// guard of the lord's body takes over, <see cref="LordGuardRule"/>). Moved from
/// <c>CombatCompanionManager.Lord.cs</c> (refactor stage B5).
/// </summary>
/// <remarks>
/// The fall is noticed here, before the allies act, as the old <c>TickLord</c> noticed it; the engine
/// announcing it when the player dies is stage B6.
/// </remarks>
public sealed class WarringStatesLordRule : BattleRule
{
    private bool lordFallHandled;

    public override string Id => BattleRuleFactory.Lord;

    public override void TickBeforeAllies(BattleContext ctx, float deltaTime)
    {
        if (!ctx.Rules.LordIsDown)
        {
            lordFallHandled = false;
            return;
        }
        if (lordFallHandled)
            return;
        lordFallHandled = true;
        ctx.RuleSet.AnnounceLordDown();
    }

    public override void OnLordDown(BattleContext ctx)
    {
        if (ctx.Rules.RetainersRetreatWithTheBody)
            return;
        // No retreat: every retainer left is taken, to be executed.
        foreach (var companion in ctx.Allies.All)
            if (ctx.Allies.CanAct(companion))
                ctx.Engine.TakeCaptive(companion.State);
    }

    public override void OnDeactivated(BattleContext ctx) => lordFallHandled = false;

    public override void OnReset(BattleContext ctx) => lordFallHandled = false;

    public override void OnAlliesDismissed(BattleContext ctx) => lordFallHandled = false;
}
