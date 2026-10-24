// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Ai;

// Enhanced enemy tactics (Configuration.EnemyTactics). Kept apart from the core controller so the
// opt-in behaviours are easy to find and every entry point is gated on its own config switch.
public unsafe partial class NpcAiController
{
    private sealed class EnemyTacticsState
    {
        // Circling while holding range.
        public float StrafeTimer;
        public bool Strafing;
        public Vector3 StrafeGoal;
        public float StrafeTimeLeft;
    }

    // Keyed by entity id, not address, and outside approachPaths: that state is dropped whenever
    // vnavmesh is off or the approach stops, and these timers must survive it.
    private readonly Dictionary<uint, EnemyTacticsState> tacticsStates = new();

    // Circling: every few seconds an enemy holding range may sidestep part-way around its target.
    private const float StrafeIntervalMin = 2.5f;
    private const float StrafeIntervalMax = 5.0f;
    private const float StrafeChance = 0.5f;
    private const float StrafeArcMinDeg = 30f;
    private const float StrafeArcMaxDeg = 60f;
    // A sidestep is a walk, not a run.
    private const float StrafeSpeedScale = 0.55f;
    private const float StrafeMaxDuration = 2.0f;
    private const float StrafeArriveDistance = 0.15f;
    // Do not pick a sidestep spot this close to another body.
    private const float StrafeClearance = 1.0f;

    private EnemyTacticsState Tactics(SimulatedNpc npc)
    {
        if (!tacticsStates.TryGetValue(npc.SimulatedEntityId, out var state))
        {
            state = new EnemyTacticsState { StrafeTimer = NextStrafeInterval() };
            tacticsStates[npc.SimulatedEntityId] = state;
        }
        return state;
    }

    private static float NextStrafeInterval()
        => StrafeIntervalMin + Random.Shared.NextSingle() * (StrafeIntervalMax - StrafeIntervalMin);

    private bool IsNpcBusyAttacking(SimulatedNpc npc)
        => npc.State.IsCasting || npc.State.AnimationLock > 0f || isTelegraphBusy(npc.SimulatedEntityId);

    /// <summary>
    /// Holding range: occasionally walk a short arc around the target, staying inside the attack
    /// band, so a held enemy is not a statue. Never while casting/swinging. Returns true when it
    /// moved the enemy this frame (the caller then skips its stand-still handling).
    /// </summary>
    private bool TryTickHoldStrafe(
        SimulatedNpc npc,
        GameObject* gameObj,
        Vector3 npcPos,
        Vector3 targetPos,
        float holdRange,
        float bodyExtra,
        float deltaTime,
        TerrainHeightCache? terrainCache)
    {
        if (!config.TacticsStrafe)
            return false;

        var tactics = Tactics(npc);
        if (npc.AiState != NpcAiState.Combat || IsNpcBusyAttacking(npc))
        {
            tactics.Strafing = false;
            return false;
        }

        if (!tactics.Strafing)
        {
            tactics.StrafeTimer -= deltaTime;
            if (tactics.StrafeTimer > 0f)
                return false;

            tactics.StrafeTimer = NextStrafeInterval();
            if (Random.Shared.NextSingle() >= StrafeChance)
                return false;

            var arc = StrafeArcMinDeg + Random.Shared.NextSingle() * (StrafeArcMaxDeg - StrafeArcMinDeg);
            if (Random.Shared.Next(2) == 0)
                arc = -arc;
            var maxR = holdRange + bodyExtra;
            var minR = MathF.Max(0.6f, holdRange * 0.75f) + bodyExtra;

            Vector3? goal = null;
            foreach (var sign in new[] { 1f, -1f })
            {
                var (gx, gz) = EnemyMovementMath.CircleGoal(targetPos.X, targetPos.Z, npcPos.X, npcPos.Z, arc * sign, minR, maxR);
                var candidate = new Vector3(gx, npcPos.Y, gz);
                if (IsSpotClear(npc.SimulatedEntityId, candidate, StrafeClearance))
                {
                    goal = candidate;
                    break;
                }
            }

            if (goal == null)
                return false;

            tactics.Strafing = true;
            tactics.StrafeGoal = goal.Value;
            tactics.StrafeTimeLeft = StrafeMaxDuration;
        }

        tactics.StrafeTimeLeft -= deltaTime;
        var to = tactics.StrafeGoal - npcPos;
        to.Y = 0;
        var remaining = to.Length();
        if (remaining <= StrafeArriveDistance || tactics.StrafeTimeLeft <= 0f)
        {
            tactics.Strafing = false;
            return false;
        }

        var speed = (npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f) * StrafeSpeedScale;
        var step = MathF.Min(remaining, speed * deltaTime);
        var next = npcPos + to / remaining * step;
        MoveTacticsStep(npc, gameObj, npcPos, next, deltaTime, terrainCache);
        ForceRotateToward(npc, npcPos + to, deltaTime);
        return true;
    }

    /// <summary>True when no other living body (or the player) stands within <paramref name="clearance"/>.</summary>
    private bool IsSpotClear(uint selfId, Vector3 spot, float clearance)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && combatEngine.State.PlayerState.IsAlive && FlatDistance(player.Position, spot) < clearance)
            return false;

        foreach (var entity in combatEngine.State.Entities.Values)
        {
            if (!entity.IsAlive || entity.EntityId == selfId)
                continue;
            var position = combatEngine.GetSimulatedEntityPosition(entity);
            if (position != Vector3.Zero && FlatDistance(position, spot) < clearance)
                return false;
        }

        return true;
    }

    /// <summary>One grounded movement step for a tactics move (same height rules as the approach).</summary>
    private void MoveTacticsStep(
        SimulatedNpc npc,
        GameObject* gameObj,
        Vector3 npcPos,
        Vector3 next,
        float deltaTime,
        TerrainHeightCache? terrainCache)
    {
        if (terrainCache != null && TryGetOrCreateApproachPathState(npc, out var pathState))
        {
            next = CorrectMovingRootHeight(npc.Address, next, terrainCache, pathState, deltaTime,
                preserveInitialClearance: false, horizontalStep: FlatDistance(npcPos, next));
        }
        else if (vnavmeshIpc.CanPathfind)
        {
            var floor = vnavmeshIpc.PointOnFloor(next, false, 3f);
            if (floor.HasValue)
                next.Y = floor.Value.Y + config.DefaultNpcHeightOffset;
        }
        else
        {
            next.Y = npcPos.Y;
        }

        movementBlockHook.AddApproachNpc(npc.Address);
        StartApproachMoveAnim(npc, deltaTime);
        movementBlockHook.SetApproachPosition(gameObj, next.X, next.Y, next.Z);
    }
}
