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
    // fight on. The seizing fills while any of them is within reach of the body (its own body's edge
    // within SeizeReach of it) and no retainer stands within the contest radius, and drains at half
    // that rate otherwise: a shove in the crush does not start it over. Full: the body is carried off.
    private const float SeizeReach = 1.5f;
    private const float SeizeSeconds = 2f;
    private const float SeizeDrainRate = 0.5f;
    private const float SeizerShare = 0.5f;
    private const float SeizeReportSeconds = 2f;
    // Every few seconds a seizer much further from the body than an enemy who is not one goes back to
    // the fight (and the nearer one takes its place); sentries that never noticed the fight and stand
    // further than this are not woken for it.
    private const float SeizerReviewSeconds = 3f;
    private const float SeizerKeepDistance = 10f;
    private const float SeizerWakeRange = 40f;
    private float seizerReviewTimer;
    // The grab on the seized body is tried again until it holds (it cannot while the lord's ragdoll is
    // still waiting to start, or a hand finds no bone yet).
    private const float GrabRetryInterval = 0.4f;
    private const float GrabRetrySeconds = 8f;
    private float grabRetryTimer;
    private float grabRetryLeft;

    private readonly WarBanner warBanner = new();
    private float seizeProgress;
    private float seizeReportTimer;
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
        seizeProgress = 0f;
        grabRetryLeft = 0f;
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
        {
            npc.SeizeGoal = null;
            npc.SeizeHeld = false;
        }
        bodySeizers.Clear();
        companionManager.LordSeizers.Clear();
        bodyContested = false;
    }

    private void TickWar(float deltaTime)
    {
        TickGrabRetry(deltaTime);
        if (!LordBodyAtStake || outcomeShown)
        {
            if (bodySeizers.Count > 0)
                ReleaseSeizers();
            seizeProgress = 0f;
            return;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;
        var body = LordBodyPosition() ?? player.Position;

        // Who goes for the body: those already going (while they live), then the nearest others,
        // until half the living enemies are on it.
        bodySeizers.RemoveAll(npc =>
        {
            if (npc.IsSpawned && npc.IsAlive && npc.BattleChara != null)
                return false;
            npc.SeizeGoal = null;
            npc.SeizeHeld = false;
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
            var distance = new Vector2(pos.X - body.X, pos.Z - body.Z).Length();
            if (npc.IsSentry && npc.AiState == Ai.NpcAiState.Idle && distance > SeizerWakeRange)
                continue;
            seizerCandidates.Add((npc, distance));
        }
        seizerCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        seizerReviewTimer -= deltaTime;
        if (seizerReviewTimer <= 0f)
        {
            seizerReviewTimer = SeizerReviewSeconds;
            if (seizerCandidates.Count > 0)
            {
                var keep = MathF.Max(SeizerKeepDistance, seizerCandidates[0].Distance * 2f);
                bodySeizers.RemoveAll(npc =>
                {
                    var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
                    if (new Vector2(pos.X - body.X, pos.Z - body.Z).Length() <= keep)
                        return false;
                    npc.SeizeGoal = null;
                    npc.SeizeHeld = false;
                    return true;
                });
            }
        }
        var wanted = living == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(living * SeizerShare));
        if (bodySeizers.Count < wanted)
        {
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

        // The body is held while the retainers standing over it are at least as many as the seizers
        // there: outnumber them and it can be taken, without first cutting down every last guard.
        var radius = Companions.CombatCompanionManager.LordContestRadius;
        var guardsAtBody = companionManager.RetainersAtBody(body, radius, out var guardName, out var guardDistance);
        var seizersAtBody = 0;
        foreach (var npc in bodySeizers)
        {
            var at = (Vector3)((GameObject*)npc.BattleChara)->Position;
            if (new Vector2(at.X - body.X, at.Z - body.Z).Length() - npc.HitboxRadius <= radius)
                seizersAtBody++;
        }
        var contested = guardsAtBody > 0 && guardsAtBody >= seizersAtBody;
        foreach (var npc in bodySeizers)
            npc.SeizeHeld = contested;
        if (contested != bodyContested)
        {
            bodyContested = contested;
            if (!contested && bodySeizers.Count > 0)
                combatEngine.AddLogEntry("Nobody stands over the lord's body!", CombatLogType.Death);
        }

        SimulatedNpc? closest = null;
        var closestGap = float.MaxValue;
        foreach (var npc in bodySeizers)
        {
            var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
            var gap = new Vector2(pos.X - body.X, pos.Z - body.Z).Length() - npc.HitboxRadius;
            if (gap < closestGap)
            {
                closestGap = gap;
                closest = npc;
            }
        }

        var atBody = closest != null && closestGap <= SeizeReach;
        seizeProgress = atBody && !contested
            ? seizeProgress + deltaTime
            : MathF.Max(0f, seizeProgress - deltaTime * SeizeDrainRate);
        // The body is taken in hand: not before the lord's ragdoll is there to be held.
        var bodyReady = !config.EnableRagdoll || ragdollController.IsActive;
        if (!bodyReady)
            seizeProgress = MathF.Min(seizeProgress, SeizeSeconds * 0.95f);

        seizeReportTimer -= deltaTime;
        if (seizeReportTimer <= 0f)
        {
            seizeReportTimer = SeizeReportSeconds;
            log.Info($"War: lord's body at {body:F1}; {bodySeizers.Count} going for it, the closest {(closest == null ? "none" : $"{closest.Name} {closestGap:F1} m from it")}; " +
                     $"{seizersAtBody} seizers and {guardsAtBody} retainers within {radius:0.#} m; " +
                     (contested ? $"held off, nearest retainer {guardName} {guardDistance:F1} m from it" : guardName == null ? "no retainer left" : $"not held, nearest retainer {guardName} {guardDistance:F1} m away") +
                     (bodyReady ? "" : "; body not yet fallen (ragdoll waiting)") +
                     $"; seized {seizeProgress / SeizeSeconds:P0}.");
        }

        if (closest != null && seizeProgress >= SeizeSeconds)
            SeizeBody(closest);
    }

    /// <summary>Where a fallen actor's body lies: its ragdoll's pelvis, or null without one.</summary>
    private Vector3? BodyPositionOf(nint address)
    {
        if (!config.EnableRagdoll || address == nint.Zero)
            return null;
        if (npcRagdolls.TryGetValue(address, out var npc))
            return npc.IsActive ? npc.GetBodyWorldPosition("j_kosi") : null;
        return Core.Services.ObjectTable.LocalPlayer?.Address == address ? LordBodyPosition() : null;
    }

    /// <summary>Where the lord's body lies: its ragdoll pelvis once it falls, else where the lord died.</summary>
    private Vector3? LordBodyPosition()
        => config.EnableRagdoll && ragdollController.IsActive ? ragdollController.GetBodyWorldPosition("j_kosi") : null;

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
            {
                enemyControlController.OnAttackInput();
                if (!enemyControlController.IsHoldingGrab)
                {
                    grabRetryLeft = GrabRetrySeconds;
                    grabRetryTimer = GrabRetryInterval;
                }
            }
        }
    }

    /// <summary>A grab on the seized body that did not take is tried again for a few seconds.</summary>
    private void TickGrabRetry(float deltaTime)
    {
        if (grabRetryLeft <= 0f)
            return;
        if (!enemyControlController.IsActive || enemyControlController.IsHoldingGrab)
        {
            if (enemyControlController.IsHoldingGrab)
                log.Info("War: the seizer has the lord's body in hand.");
            grabRetryLeft = 0f;
            return;
        }
        grabRetryLeft -= deltaTime;
        grabRetryTimer -= deltaTime;
        if (grabRetryTimer > 0f)
            return;
        grabRetryTimer = GrabRetryInterval;
        enemyControlController.OnAttackInput();
        if (grabRetryLeft <= 0f && !enemyControlController.IsHoldingGrab)
            log.Warning("War: the seizer could not take hold of the lord's body.");
    }

    private void DrawWar() => warBanner.Draw();
}
