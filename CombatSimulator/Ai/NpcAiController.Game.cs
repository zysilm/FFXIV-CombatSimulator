// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Animation;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Ai;

// The plugin's constructor: the game behind the controller's seams (stage B4a). Not compiled by
// Tests/CombatReplay, which builds the controller over a fake world instead.
public unsafe partial class NpcAiController
{
    public NpcAiController(
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        Configuration config,
        IPluginLog log,
        CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter,
        NpcCorpseFootIkSolver footIkSolver,
        Func<nint, bool>? isExternallyControlled = null,
        Func<uint, bool>? isTelegraphBusy = null)
        : this(
            combatEngine,
            new GameCombatWorld(Core.Services.ObjectTable, null, animationController, movementBlockHook, footIkSolver),
            new GameAttackSink(combatEngine, combatModeRouter),
            config,
            log,
            isExternallyControlled,
            isTelegraphBusy)
    {
    }
}
