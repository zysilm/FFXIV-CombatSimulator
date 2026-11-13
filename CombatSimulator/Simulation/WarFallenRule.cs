// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Simulation;

/// <summary>What becomes of the player's fallen in a War Generator battle.</summary>
public enum WarFallenRule
{
    /// <summary>The fallen stay down.</summary>
    StayDown,
    /// <summary>Companions run to a fallen or captive friend and raise or free them.</summary>
    SpringAndAutumn,
    /// <summary>
    /// The player is the lord: nobody is raised; when the lord falls the retainers are taken
    /// captive, or, retreating, one prays over the body while the others guard it.
    /// </summary>
    WarringStates,
}
