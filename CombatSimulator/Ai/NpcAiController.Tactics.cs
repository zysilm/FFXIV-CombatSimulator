// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
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

        // Staggered engagement: whether this melee enemy holds one of its target's attacker
        // places, and the outer-ring spot it waits on otherwise.
        public bool Admitted;
        public uint AdmittedTargetId;
        public Vector3 WaitSpot;
        public Vector3 WaitAnchor;
        public bool HasWaitSpot;
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

    // Staggered engagement: at most this many melee enemies fight one target at once (the same cap
    // CombatCompanionManager's pressure scoring aims for); the rest wait on a ring this far outside
    // their hold range and step in, closest first, when a place frees.
    private const int MeleeAttackersPerTarget = 3;
    private const float WaitRingExtra = 3.5f;
    private const float WaitSpotRefreshDistance = 2.0f;
    private const float WaitSpotArriveDistance = 0.4f;
    private const float EngageDelayJitterMax = 1.2f;

    /// <summary>Random extra wind-up when an enemy first engages, so a pack does not start in lockstep.</summary>
    private void ApplyEngageJitter(SimulatedNpc npc)
    {
        if (config.TacticsStagger)
            npc.EngageDelayTimer = MathF.Max(npc.EngageDelayTimer, Random.Shared.NextSingle() * EngageDelayJitterMax);
    }

    /// <summary>
    /// Once per frame, before party approach: decide which melee enemies hold an attacker place on
    /// their target. Places already held are kept (no reshuffling); free places go to the waiters
    /// closest to the target.
    /// </summary>
    private void UpdateMeleeAdmission(
        IReadOnlyList<SimulatedNpc> approachNpcs,
        IReadOnlyDictionary<uint, (SimulatedEntityState State, Vector3 Position)> targetByNpcId)
    {
        if (!config.TacticsStagger)
            return;

        var byTarget = new Dictionary<uint, List<(SimulatedNpc Npc, EnemyTacticsState Tactics, float Dist)>>();
        foreach (var npc in approachNpcs)
        {
            var tactics = Tactics(npc);
            if (!npc.IsEngaged || !HasMeleePositioningIntent(npc) ||
                !targetByNpcId.TryGetValue(npc.SimulatedEntityId, out var target) || target.State == null || !target.State.IsAlive)
            {
                tactics.Admitted = false;
                continue;
            }

            var targetId = target.State.EntityId;
            if (tactics.AdmittedTargetId != targetId)
            {
                tactics.Admitted = false;
                tactics.HasWaitSpot = false;
            }
            tactics.AdmittedTargetId = targetId;

            if (!byTarget.TryGetValue(targetId, out var list))
                byTarget[targetId] = list = new();
            list.Add((npc, tactics, FlatDistance(GetNpcPosition(npc), target.Position)));
        }

        foreach (var list in byTarget.Values)
        {
            var admitted = 0;
            foreach (var entry in list)
                if (entry.Tactics.Admitted)
                    admitted++;

            // A place can be over-subscribed after a retarget; drop the farthest extras.
            if (admitted > MeleeAttackersPerTarget)
            {
                foreach (var entry in list.Where(e => e.Tactics.Admitted).OrderByDescending(e => e.Dist).Take(admitted - MeleeAttackersPerTarget))
                    entry.Tactics.Admitted = false;
                admitted = MeleeAttackersPerTarget;
            }

            foreach (var entry in list.Where(e => !e.Tactics.Admitted).OrderBy(e => e.Dist))
            {
                if (admitted >= MeleeAttackersPerTarget)
                    break;
                entry.Tactics.Admitted = true;
                entry.Tactics.HasWaitSpot = false;
                admitted++;
            }
        }
    }

    /// <summary>
    /// A melee enemy without an attacker place walks to (and stands on) a spot on the outer ring,
    /// facing its target. Returns true when it handled this enemy's movement for the frame.
    /// </summary>
    private bool TryTickMeleeWait(
        SimulatedNpc npc,
        GameObject* gameObj,
        Vector3 npcPos,
        Vector3 targetPos,
        float holdRange,
        float bodyExtra,
        float deltaTime,
        TerrainHeightCache? terrainCache)
    {
        if (!config.TacticsStagger || !HasMeleePositioningIntent(npc))
            return false;

        var tactics = Tactics(npc);
        if (tactics.Admitted)
            return false;

        var ringRadius = holdRange + bodyExtra + WaitRingExtra;
        if (!tactics.HasWaitSpot || FlatDistance(tactics.WaitAnchor, targetPos) > WaitSpotRefreshDistance)
        {
            tactics.WaitSpot = PickWaitSpot(npc.SimulatedEntityId, npcPos, targetPos, ringRadius);
            tactics.WaitAnchor = targetPos;
            tactics.HasWaitSpot = true;
        }

        var to = tactics.WaitSpot - npcPos;
        to.Y = 0;
        var remaining = to.Length();
        if (remaining <= WaitSpotArriveDistance)
        {
            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, targetPos, deltaTime);
            return true;
        }

        var speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        var step = MathF.Min(remaining, speed * deltaTime);
        MoveTacticsStep(npc, gameObj, npcPos, npcPos + to / remaining * step, deltaTime, terrainCache);
        ForceRotateToward(npc, tactics.WaitSpot, deltaTime);
        return true;
    }

    private Vector3 PickWaitSpot(uint selfId, Vector3 npcPos, Vector3 targetPos, float ringRadius)
    {
        var flat = npcPos - targetPos;
        flat.Y = 0;
        var bearing = flat.LengthSquared() < 0.01f ? 0f : MathF.Atan2(flat.X, flat.Z);
        Vector3 At(float a) => new(targetPos.X + MathF.Sin(a) * ringRadius, npcPos.Y, targetPos.Z + MathF.Cos(a) * ringRadius);

        const float stepRad = 20f * MathF.PI / 180f;
        for (var i = 0; i <= 8; i++)
        {
            foreach (var sign in i == 0 ? new[] { 0f } : new[] { 1f, -1f })
            {
                var spot = At(bearing + sign * i * stepRad);
                if (IsSpotClear(selfId, spot, StrafeClearance * 1.5f) && IsWaitSpotFree(selfId, spot))
                    return spot;
            }
        }

        return At(bearing);
    }

    private bool IsWaitSpotFree(uint selfId, Vector3 spot)
    {
        foreach (var (id, other) in tacticsStates)
        {
            if (id != selfId && other.HasWaitSpot && !other.Admitted && FlatDistance(other.WaitSpot, spot) < StrafeClearance * 1.5f)
                return false;
        }
        return true;
    }

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
