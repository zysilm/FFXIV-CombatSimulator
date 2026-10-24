// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Animation;
using CombatSimulator.Core;

namespace CombatSimulator;

public sealed unsafe partial class CombatSimulatorPlugin
{
    private VictoryCelebrationDirector? victoryCelebration;

    /// <summary>Exposed for private directors (stories) that hand actors in and out of it.</summary>
    internal VictoryCelebrationDirector? VictoryCelebration => victoryCelebration;

    private void WireVictoryCelebration()
    {
        victoryCelebration = new VictoryCelebrationDirector(animationController, Services.DataManager, log)
        {
            MaxStagger = () => config.VictoryCelebrationStagger,
            Companions = LivingCompanionActors,
            IsExternallyDriven = address => enemyControlController.ControlsNpc(address),
            // The celebration lasts exactly as long as the outcome it belongs to: a revive, a new
            // wave or stopping the simulation ends it and returns everyone to idle.
            OutcomeHolds = mood => config.EnableVictoryCelebration && combatEngine.IsActive && mood switch
            {
                CelebrationMood.EnemyVictory => !combatEngine.State.PlayerState.IsAlive && !companionManager.HasLivingCompanions,
                CelebrationMood.PartyVictory => npcSelector.SelectedNpcs.Count > 0 && npcSelector.SelectedNpcs.All(n => !n.IsAlive),
                _ => true,
            },
        };
        animationController.Victory = victoryCelebration;
        // Crowd reactions to the result; React() is a no-op unless SpectatorReactionEmotes.
        // spectatorController is constructed after this runs, hence the late lookup.
        combatEngine.OnOutcomeDecided = playerWon => spectatorController?.React(
            playerWon ? Spectators.SpectatorReaction.PlayerVictory : Spectators.SpectatorReaction.PlayerDefeat);
    }

    private IEnumerable<(nint Address, uint EntityId)> LivingCompanionActors()
    {
        var result = new List<(nint, uint)>();
        foreach (var companion in companionManager.Companions)
            if (companion.IsSpawned && companion.State.IsAlive && companion.BattleChara != null)
                result.Add((companion.Address, companion.SimulatedEntityId));
        return result;
    }
}
