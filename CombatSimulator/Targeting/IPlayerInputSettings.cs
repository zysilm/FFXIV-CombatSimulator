// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Targeting;

/// <summary>
/// What the player's actions and target lock read on their way into the simulation (docs/PLUGIN_ARCHITECTURE.md
/// §7): the target lock and the auto-counter (<see cref="PlayerTargetController"/>), and the UseAction hook
/// (<c>Safety.UseActionHook</c>, which also registers a map enemy the player attacks at the chosen strength and
/// level). Read-only; implemented by <c>Configuration</c>, so the stored JSON does not change.
/// </summary>
public interface IPlayerInputSettings
{
    bool ActionMode { get; }
    bool FightingMode { get; }
    bool EnableCustomTargeting { get; }
    bool EnableAutoCounter { get; }
    int EnemyStrength { get; }
    int EnemyLevel { get; }
}
