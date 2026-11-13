// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Gui;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator;

/// <summary>
/// The War Generator's outcome: the result banner, and under Warring States with Retreat, the lord's
/// body: guarded until the enemy is driven off ("body recovered"), or carried off by an enemy that
/// reaches it (Enemy Control's grab, with its current settings), which loses the battle.
/// </summary>
public sealed unsafe partial class CombatSimulatorPlugin
{
    // An enemy that stands this close to the body this long carries it off; with no retainer left
    // to stand before the body, the nearest enemy goes for it.
    private const float SeizeReach = 1.0f;
    private const float SeizeSeconds = 2f;

    private readonly WarBanner warBanner = new();
    private readonly Dictionary<uint, float> nearBodySeconds = new();
    private SimulatedNpc? bodySeizer;
    private bool bodySeized;
    private bool outcomeShown;

    private void WireWar()
    {
        var previous = combatEngine.OnOutcomeDecided;
        combatEngine.OnOutcomeDecided = playerWon =>
        {
            previous?.Invoke(playerWon);
            ShowOutcome(playerWon);
        };
        combatEngine.OnSimulationReset += ResetWar;
    }

    private void ShowOutcome(bool playerWon)
    {
        // The first word stands: a battle lost when the body was carried off is not won later.
        if (outcomeShown)
            return;
        outcomeShown = true;

        if (!playerWon)
            warBanner.Show("DEFEAT", new Vector4(0.9f, 0.25f, 0.2f, 1f));
        else if (config.WarFallenRule == (int)WarFallenRule.WarringStates && !combatEngine.State.PlayerState.IsAlive)
            warBanner.Show("BODY RECOVERED", new Vector4(0.95f, 0.8f, 0.35f, 1f));
        else
            warBanner.Show("VICTORY", new Vector4(0.95f, 0.85f, 0.3f, 1f));
    }

    private void ResetWar()
    {
        nearBodySeconds.Clear();
        if (bodySeizer != null)
            bodySeizer.SeizeGoal = null;
        bodySeizer = null;
        bodySeized = false;
        outcomeShown = false;
        warBanner.Hide();
    }

    /// <summary>Whether the lord's body is to be guarded (Warring States with Retreat, the lord down).</summary>
    private bool LordBodyAtStake => companionManager.GuardingLord && !bodySeized;

    private void TickWar(float deltaTime)
    {
        if (!LordBodyAtStake || outcomeShown)
        {
            if (bodySeizer != null)
            {
                bodySeizer.SeizeGoal = null;
                bodySeizer = null;
            }
            nearBodySeconds.Clear();
            return;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;
        var body = player.Position;

        SimulatedNpc? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.IsSpawned || !npc.IsAlive || npc.BattleChara == null)
                continue;
            var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
            var d = new Vector2(pos.X - body.X, pos.Z - body.Z).Length();
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = npc;
            }

            if (d <= SeizeReach)
            {
                var held = nearBodySeconds.GetValueOrDefault(npc.SimulatedEntityId) + deltaTime;
                nearBodySeconds[npc.SimulatedEntityId] = held;
                if (held >= SeizeSeconds || npc == bodySeizer)
                {
                    SeizeBody(npc);
                    return;
                }
            }
            else
                nearBodySeconds.Remove(npc.SimulatedEntityId);
        }

        // Nobody left to stand before the body: the nearest enemy goes to take it.
        if (!companionManager.HasRetainer)
        {
            if (bodySeizer == null || !bodySeizer.IsAlive || !bodySeizer.IsSpawned)
                bodySeizer = nearest;
            if (bodySeizer != null)
                bodySeizer.SeizeGoal = body;
        }
    }

    private void SeizeBody(SimulatedNpc npc)
    {
        bodySeized = true;
        npc.SeizeGoal = null;
        bodySeizer = null;
        combatEngine.AddLogEntry($"{npc.Name} carries off the lord's body!", CombatLogType.Death);
        companionManager.SurrenderRetainers();
        combatEngine.ForceEnemyVictory();

        // Enemy Control's grab, with its current settings, on the body.
        if (config.EnableRagdoll && ragdollController.IsActive && !enemyControlController.IsActive)
        {
            enemyControlController.ControlKiller(npc);
            if (enemyControlController.IsActive)
                enemyControlController.OnAttackInput();
        }
    }

    private void DrawWar() => warBanner.Draw();
}
