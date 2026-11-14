// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Combat;

/// <summary>
/// Where a combatant's AI is, both sides (refactor stage B9: the enemies' <c>NpcAiState</c> and the
/// allies' <c>CompanionAiState</c> merged, docs/PLUGIN_ARCHITECTURE.md §2.2). Each side uses the states
/// it always used, by the same names, so anything that prints a state prints the same word as before:
/// <list type="bullet">
/// <item>both: <see cref="Idle"/>, <see cref="Dead"/>;</item>
/// <item>enemies (<c>NpcAiController</c>): <see cref="Engaging"/>, <see cref="Combat"/>, <see cref="Chasing"/>,
/// <see cref="Resetting"/>;</item>
/// <item>allies (<c>CombatCompanionManager</c>): <see cref="ReturningToCommandRange"/>, <see cref="MovingToTarget"/>,
/// <see cref="CombatReady"/>, <see cref="ActionLocked"/>, <see cref="Captive"/>.</item>
/// </list>
/// The numeric values are not the old ones (the two enums gave <c>Dead</c> different numbers); nothing
/// stores or compares them as numbers.
/// </summary>
public enum CombatantAiState
{
    Idle,
    /// <summary>Enemy: drawn into the fight, waiting out its engage delay.</summary>
    Engaging,
    /// <summary>Enemy: fighting.</summary>
    Combat,
    /// <summary>Enemy: set by older code paths; the next turn turns it back into <see cref="Combat"/>.</summary>
    Chasing,
    Dead,
    /// <summary>Enemy: being put back (full HP, then idle).</summary>
    Resetting,
    /// <summary>Ally: walking back to its place by the command anchor (also: walking with no target).</summary>
    ReturningToCommandRange,
    /// <summary>Ally: walking in on its target.</summary>
    MovingToTarget,
    /// <summary>Ally: standing within reach of its target, or at its place with a target.</summary>
    CombatReady,
    /// <summary>Ally: in its animation lock.</summary>
    ActionLocked,
    /// <summary>Ally: held at 1 HP, kneeling, until executed or freed (War Generator hostages).</summary>
    Captive,
}
