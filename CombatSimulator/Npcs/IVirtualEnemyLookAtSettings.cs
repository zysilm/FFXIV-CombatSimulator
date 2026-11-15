// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Npcs;

/// <summary>What the virtual enemies' look-at reads (<see cref="VirtualEnemyLookAtController"/>). Read-only; implemented by <c>Configuration</c>.</summary>
public interface IVirtualEnemyLookAtSettings
{
    bool VirtualEnemyLookAtTarget { get; }
    string VirtualEnemyLookAtBone { get; }
}
