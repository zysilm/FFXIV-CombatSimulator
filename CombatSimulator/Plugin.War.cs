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
    // Half the living enemies (the nearest to the body, at least one) go for the lord's body; the rest
    // fight on. One that stands at the body this long, with no retainer within the contest radius,
    // carries it off.
    private const float SeizeReach = 1.0f;
    private const float SeizeSeconds = 2f;
    private const float SeizerShare = 0.5f;

    private readonly WarBanner warBanner = new();
    private readonly Dictionary<uint, float> nearBodySeconds = new();
    private readonly List<SimulatedNpc> bodySeizers = new();
    private readonly List<(SimulatedNpc Npc, float Distance)> seizerCandidates = new();
    private bool bodyContested;
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
        ReleaseSeizers();
        bodySeized = false;
        outcomeShown = false;
        warBanner.Hide();
    }

    /// <summary>Whether the lord's body is to be guarded (Warring States with Retreat, the lord down).</summary>
    private bool LordBodyAtStake => companionManager.GuardingLord && !bodySeized;

    private void ReleaseSeizers()
    {
        foreach (var npc in bodySeizers)
            npc.SeizeGoal = null;
        bodySeizers.Clear();
        companionManager.LordSeizers.Clear();
        bodyContested = false;
    }

    private void TickWar(float deltaTime)
    {
        if (!LordBodyAtStake || outcomeShown)
        {
            if (bodySeizers.Count > 0)
                ReleaseSeizers();
            nearBodySeconds.Clear();
            return;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;
        var body = player.Position;

        // Who goes for the body: those already going (while they live), then the nearest others,
        // until half the living enemies are on it.
        bodySeizers.RemoveAll(npc =>
        {
            if (npc.IsSpawned && npc.IsAlive && npc.BattleChara != null)
                return false;
            npc.SeizeGoal = null;
            return true;
        });
        seizerCandidates.Clear();
        var living = 0;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.IsSpawned || !npc.IsAlive || npc.BattleChara == null || npc.State.IsCaptive)
                continue;
            living++;
            if (bodySeizers.Contains(npc))
                continue;
            var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
            seizerCandidates.Add((npc, new Vector2(pos.X - body.X, pos.Z - body.Z).Length()));
        }
        var wanted = living == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(living * SeizerShare));
        if (bodySeizers.Count < wanted)
        {
            seizerCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            for (var i = 0; i < seizerCandidates.Count && bodySeizers.Count < wanted; i++)
            {
                var npc = seizerCandidates[i].Npc;
                bodySeizers.Add(npc);
                // Woken if it was standing idle (a sentry): the body is what it is here for.
                npcAiController.EngageNpc(npc);
            }
        }
        companionManager.LordSeizers.Clear();
        foreach (var npc in bodySeizers)
        {
            npc.SeizeGoal = body;
            companionManager.LordSeizers.Add(npc.SimulatedEntityId);
        }

        // While a retainer stands over the body, nobody can carry it off.
        var contested = companionManager.LordBodyContested(body, Companions.CombatCompanionManager.LordContestRadius);
        if (contested != bodyContested)
        {
            bodyContested = contested;
            if (!contested && bodySeizers.Count > 0)
                combatEngine.AddLogEntry("Nobody stands over the lord's body!", CombatLogType.Death);
        }

        foreach (var npc in bodySeizers)
        {
            var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
            var d = new Vector2(pos.X - body.X, pos.Z - body.Z).Length();
            if (contested || d > SeizeReach + npc.HitboxRadius)
            {
                nearBodySeconds.Remove(npc.SimulatedEntityId);
                continue;
            }
            var held = nearBodySeconds.GetValueOrDefault(npc.SimulatedEntityId) + deltaTime;
            nearBodySeconds[npc.SimulatedEntityId] = held;
            if (held >= SeizeSeconds)
            {
                SeizeBody(npc);
                return;
            }
        }
    }

    private void SeizeBody(SimulatedNpc npc)
    {
        bodySeized = true;
        ReleaseSeizers();
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
