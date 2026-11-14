// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Combat;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Companions;

// The plugin's constructor: the game behind the manager's seams (stage B4a). Not compiled by
// Tests/CombatReplay, which builds the manager over a fake world instead.
public unsafe partial class CombatCompanionManager
{
    public CombatCompanionManager(
        IObjectTable objectTable,
        IClientState clientState,
        Configuration config,
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        ITargetManager targetManager,
        IPluginLog log)
        : this(
            objectTable,
            clientState,
            config,
            combatEngine,
            new GameCombatWorld(objectTable, targetManager, animationController, movementBlockHook, null),
            new GameAttackSink(combatEngine, null),
            log)
    {
    }
}
