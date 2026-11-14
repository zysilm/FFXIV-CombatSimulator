// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Battle;
using CombatSimulator.Combat;

namespace CombatSimulator;

/// <summary>
/// The read-only settings views a module is given instead of the whole Configuration
/// (docs/PLUGIN_ARCHITECTURE.md §7). Declarations only: every member is an existing property, and the
/// few named differently are explicit (non-public) implementations, which the JSON serializer and the
/// reflection-based "reset to defaults" do not see.
/// </summary>
public partial class Configuration : ICombatAiSettings, IBattleRuleSettings, ITargetingSettings
{
    Simulation.WarFallenRule IBattleRuleSettings.FallenRule => (Simulation.WarFallenRule)WarFallenRule;
    bool IBattleRuleSettings.Hostages => WarCaptives;
    bool IBattleRuleSettings.Retreat => WarRetreat;
    uint IBattleRuleSettings.CaptiveEmoteId => WarCaptiveEmoteId;
}
