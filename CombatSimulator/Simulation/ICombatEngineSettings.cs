// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Simulation;

/// <summary>
/// The settings the combat engine reads (docs/PLUGIN_ARCHITECTURE.md §7): the damage formula's switches, the
/// player's light attack, custom targeting, the ragdoll and its severed parts on the player's death, the
/// captive's emote, and the battle rules it builds (<see cref="Battle.IBattleRuleSettings"/>). Read-only;
/// implemented by <c>Configuration</c> with its own properties, so the stored JSON does not change.
/// </summary>
public interface ICombatEngineSettings : Battle.IBattleRuleSettings
{
    bool ActionMode { get; }
    float DamageMultiplier { get; }
    bool EnableCriticalHits { get; }
    bool EnableDirectHits { get; }
    int LightAttackPotency { get; }
    bool EnableCustomTargeting { get; }
    /// <summary>The parts severed from the player's body when it falls (the stored list itself).</summary>
    List<string> DismemberPocBones { get; }
    uint WarCaptiveEmoteId { get; }
}
