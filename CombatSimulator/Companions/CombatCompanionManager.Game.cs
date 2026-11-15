// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Animation;
using CombatSimulator.Battle.Rules;
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
        ICombatAiSettings config,
        CombatEngine combatEngine,
        CombatRoster roster,
        TargetingService targeting,
        BattleRuleSet ruleParts,
        ICorpseQuery corpses,
        Func<uint, bool> isSourceEnemy,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        ITargetManager targetManager,
        IPluginLog log)
        : this(
            objectTable,
            clientState,
            config,
            combatEngine,
            roster,
            targeting,
            ruleParts,
            corpses,
            isSourceEnemy,
            new GameCombatWorld(objectTable, targetManager, animationController, movementBlockHook, null),
            new GameAttackSink(combatEngine, null),
            log)
    {
    }
}
