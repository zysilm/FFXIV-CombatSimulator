// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Warring States with Hostages: a lethal blow on the player's side leaves the victim at 1 HP and takes
/// it captive (docs/WAR_GENERATOR.md). Its hooks are the engine's (<see cref="IBattleRule.OnLethalBlow"/>,
/// <see cref="IBattleRule.DescribeOutcome"/>): until stage B6 the capture is still decided in
/// <c>CombatEngine</c> (<c>BattleRules.TakesCaptivesOnLethalBlow</c>) and the "ROUTED" wording in
/// <c>Plugin.War.cs</c>, so this part only holds its place in the composition. That an enemy executes a
/// captive only with nobody else left to fight is the allies' controller's for every captive, whichever
/// rule took it.
/// </summary>
public sealed class HostageRule : BattleRule
{
    public override string Id => BattleRuleFactory.Hostage;
}
