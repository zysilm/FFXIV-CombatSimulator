// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Simulation;

namespace CombatSimulator.Battle;

/// <summary>
/// The settings the battle rules read (docs/PLUGIN_ARCHITECTURE.md §7). Read-only; implemented by
/// <c>Configuration</c>, where the members named differently from its stored properties are
/// explicit interface implementations, so the stored JSON does not change.
/// </summary>
public interface IBattleRuleSettings
{
    /// <summary><c>Configuration.WarFallenRule</c> (stored as an int).</summary>
    WarFallenRule FallenRule { get; }
    /// <summary><c>Configuration.WarCaptives</c>.</summary>
    bool Hostages { get; }
    /// <summary><c>Configuration.WarRetreat</c>.</summary>
    bool Retreat { get; }
    /// <summary><c>Configuration.WarCaptiveEmoteId</c>.</summary>
    uint CaptiveEmoteId { get; }
    bool ShowResultBanner { get; }
    bool EnableRagdoll { get; }
}
