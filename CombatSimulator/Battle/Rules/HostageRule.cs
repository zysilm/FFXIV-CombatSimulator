// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Combat;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Warring States with Hostages: a lethal blow on the player's side leaves the victim at 1 HP and takes it
/// captive (docs/WAR_GENERATOR.md), whoever struck it and whether or not it was aimed at them. The capture
/// itself (1 HP, the captive's state and kneel) is the engine's. The executed captives' "ROUTED" is
/// <see cref="WarringStatesLordRule"/>'s, as the lord's fall without Retreat takes captives too. That an
/// enemy executes a captive only with nobody else left to fight is the allies' controller's for every
/// captive, whichever rule took it.
/// </summary>
public sealed class HostageRule : BattleRule
{
    public override string Id => BattleRuleFactory.Hostage;

    public override LethalBlow OnLethalBlow(BattleContext ctx, SimulatedEntityState victim, Combatant by, bool primary)
        => LethalBlow.Capture;
}
