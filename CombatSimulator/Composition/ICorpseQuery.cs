// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace CombatSimulator.Composition;

/// <summary>
/// What the fight asks about bodies on the ground (docs/PLUGIN_ARCHITECTURE.md §6.5), answered by
/// <see cref="CorpseModule"/> from the ragdolls. Given to the allies, the enemies' feet and the battle
/// rules in place of the plugin's methods they were handed before.
/// </summary>
internal interface ICorpseQuery
{
    /// <summary>Where a fallen actor's body lies: its ragdoll's pelvis, or null without one.</summary>
    Vector3? BodyPositionOf(nint address);

    /// <summary>Where the lord's (the player's) body lies once its ragdoll is up, else null.</summary>
    Vector3? LordBodyPosition();

    /// <summary>The highest low corpse surface under a foot about to step at <paramref name="position"/>, or null.</summary>
    float? ResolveCorpseTraversalHeight(nint actorAddress, Vector3 position);
}
