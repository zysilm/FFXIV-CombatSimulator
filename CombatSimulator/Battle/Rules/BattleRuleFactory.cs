// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Which parts a battle runs, from the war settings (docs/PLUGIN_ARCHITECTURE.md §5.4), and the parts
/// themselves, in the one order every hook is asked in.
/// </summary>
public static class BattleRuleFactory
{
    public const string Base = "base";
    public const string Rescue = "rescue";
    public const string Lord = "lord";
    public const string Hostage = "hostage";
    public const string Guard = "guard";
    public const string Seizure = "seizure";

    /// <summary>
    /// The parts for these settings. Retreat (the window's checkbox, the profile's <c>CorpseSeizure</c>)
    /// brings both the guard of the lord's body and the seizure.
    /// </summary>
    public static IReadOnlyList<string> PartsFor(IBattleRuleSettings s) => s.FallenRule switch
    {
        WarFallenRule.SpringAndAutumn => [Base, Rescue],
        WarFallenRule.WarringStates => [Base, Lord, .. s.Hostages ? [Hostage] : (string[])[], .. s.Retreat ? [Guard, Seizure] : (string[])[]],
        _ => [Base],
    };

    /// <summary>Whether <see cref="PartsFor"/> has this part (without building the list).</summary>
    public static bool Composes(string id, WarFallenRule fallenRule, bool hostages, bool retreat) => id switch
    {
        Base => true,
        Rescue => fallenRule == WarFallenRule.SpringAndAutumn,
        Lord => fallenRule == WarFallenRule.WarringStates,
        Hostage => fallenRule == WarFallenRule.WarringStates && hostages,
        Guard or Seizure => fallenRule == WarFallenRule.WarringStates && retreat,
        _ => false,
    };

    /// <summary>
    /// Every part, in the order the hooks are asked: the rescue's tick runs before the lord's and the guard's,
    /// as <c>TickRescues</c> ran before <c>TickLord</c>.
    /// </summary>
    /// <remarks>
    /// Invariant: an ally raising or freeing a friend (<see cref="SpringAutumnRescueRule"/>) and the one praying
    /// over the lord's body (<see cref="LordGuardRule"/>) are never the same ally, nor both present on any ally
    /// at once. So two orders here do not matter, though they differ from the old code: <c>TakesOverAlly</c>
    /// asks the rescue before the guard (the old <c>UpdateCompanion</c> asked the prayer first), and the status
    /// word "Raising" comes before "Praying" (the old one checked the prayer first). Why it holds: the rule
    /// changes only between frames (the window), and in the tick pass, in this order:
    /// Warring States to Spring and Autumn, the rescue's first tick may assign a rescuer, but the guard is
    /// still live that frame and <see cref="BattleRuleSet.Tends"/> excludes the one praying; the guard's
    /// <see cref="IBattleRule.OnDeactivated"/> then releases the prayer later in the same pass.
    /// Spring and Autumn to Warring States, the rescue's <see cref="IBattleRule.OnDeactivated"/> cancels every
    /// rescue before the guard's tick can choose a prayer. Within either rule only one of the two is composed,
    /// and a part holds no task state outside its ticks (see <see cref="BattleRuleSet"/>).
    /// </remarks>
    public static IBattleRule[] CreateAll()
    {
        var guard = new LordGuardRule();
        return
        [
            new BaseRule(),
            new SpringAutumnRescueRule(),
            new WarringStatesLordRule(),
            new HostageRule(),
            guard,
            new CorpseSeizureRule(guard),
        ];
    }
}
