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
        public float StrafeSpeed = StrafeSpeedScale;

        // Ranged reposition after an attack / backstep out of a crowd.
        public bool WasBusy;
        public float RepositionCooldown;
        public float BackstepCooldown;

        // Staggered engagement: whether this melee enemy holds one of its target's attacker
        // places, and the outer-ring spot it waits on otherwise.
        public bool Admitted;
        public uint AdmittedTargetId;
        public Vector3 WaitSpot;
        public Vector3 WaitAnchor;
        public bool HasWaitSpot;

        // Low-HP flee / call for help.
        public bool CalledForHelp;
        public bool HasFled;
        public bool Fleeing;
        public float FleeTimeLeft;
        public uint FleeAllyId;
        public Vector3 FleeFallback;
    }

    // Below this HP fraction some enemies (a stable per-enemy roll) break off once and run to an ally.
    private const float FleeHpFraction = 0.2f;
    private const float FleeChance = 0.35f;
    private const float FleeMaxDuration = 4.0f;
    // Slower than the approach run, so a pursuer can catch it.
    private const float FleeSpeedScale = 0.8f;
    private const float FleeArriveDistance = 1.0f;
    private const float FleeBehindAllyDistance = 1.5f;
    private const float FleeMinAllyDistance = 3.0f;
    private const float FleeMaxAllyDistance = 30.0f;
    // Used when an enemy's authored AggroRange is 0 (most of them).
    private const float DefaultHelpRadius = 10f;

    /// <summary>
    /// Once per frame: every enemy that has just entered the fight wakes idle allies within its aggro
    /// range (NpcBehavior.AggroRange, previously unread). Woken allies do not call further, so a call
    /// reaches one hop, not the whole map.
    /// </summary>
    private void TickCallForHelp(IReadOnlyList<SimulatedNpc> npcs)
    {
        if (!config.TacticsFlee)
            return;

        foreach (var caller in npcs)
        {
            if (!caller.IsSpawned || !caller.State.IsAlive || !caller.IsEngaged)
                continue;

            var tactics = Tactics(caller);
            if (tactics.CalledForHelp)
                continue;
            tactics.CalledForHelp = true;

            var radius = caller.Behavior.AggroRange > 0f ? caller.Behavior.AggroRange : DefaultHelpRadius;
            var callerPos = GetNpcPosition(caller);
            foreach (var ally in npcs)
            {
                if (ally == caller || !ally.IsSpawned || !ally.State.IsAlive || ally.AiState != NpcAiState.Idle)
                    continue;
                if (FlatDistance(GetNpcPosition(ally), callerPos) > radius)
                    continue;

                Tactics(ally).CalledForHelp = true;
                EngageNpc(ally);
            }
        }
    }

    /// <summary>True while an enemy is running from the fight; TickCombat holds its attacks.</summary>
    private bool IsTacticsFleeing(SimulatedNpc npc)
        => config.TacticsFlee && tacticsStates.TryGetValue(npc.SimulatedEntityId, out var t) && t.Fleeing;

    /// <summary>
    /// Low-HP flee: start (once per enemy, for the enemies whose stable roll says so) and drive the run
    /// toward the nearest other living enemy, or its spawn point. Returns true when it moved the enemy
    /// this frame; the approach pass then skips its own movement.
    /// </summary>
    private bool TryTickFlee(
        SimulatedNpc npc,
        GameObject* gameObj,
        Vector3 npcPos,
        Vector3 threatPos,
        IReadOnlyList<SimulatedNpc> allies,
        float deltaTime,
        TerrainHeightCache? terrainCache)
    {
        if (!config.TacticsFlee || npc.AiState != NpcAiState.Combat)
            return false;

        var tactics = Tactics(npc);
        if (!tactics.Fleeing)
        {
            var hp = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 1f;
            if (tactics.HasFled || hp > FleeHpFraction)
                return false;
            tactics.HasFled = true; // one decision per enemy, flee or not
            if (StableRoll(npc.SimulatedEntityId) >= FleeChance)
                return false;

            SimulatedNpc? best = null;
            var bestDist = float.MaxValue;
            foreach (var ally in allies)
            {
                if (ally == npc || !ally.IsSpawned || !ally.State.IsAlive || ally.BattleChara == null)
                    continue;
                if (tacticsStates.TryGetValue(ally.SimulatedEntityId, out var allyTactics) && allyTactics.Fleeing)
                    continue;
                var d = FlatDistance(GetNpcPosition(ally), npcPos);
                if (d < FleeMinAllyDistance || d > FleeMaxAllyDistance || d >= bestDist)
                    continue;
                best = ally;
                bestDist = d;
            }

            if (best == null && FlatDistance(npc.SpawnPosition, npcPos) < FleeMinAllyDistance)
                return false; // nowhere to run

            tactics.Fleeing = true;
            tactics.Admitted = false; // free its melee place for a waiter
            tactics.FleeTimeLeft = FleeMaxDuration;
            tactics.FleeAllyId = best?.SimulatedEntityId ?? 0;
            tactics.FleeFallback = npc.SpawnPosition;
            ClearNpcActionState(npc);
            combatEngine.AddLogEntry($"{npc.Name} flees!", CombatLogType.Info);
        }

        var goal = tactics.FleeFallback;
        if (tactics.FleeAllyId != 0)
        {
            var ally = allies.FirstOrDefault(a => a.SimulatedEntityId == tactics.FleeAllyId && a.State.IsAlive && a.BattleChara != null);
            if (ally != null)
            {
                var allyPos = GetNpcPosition(ally);
                var (gx, gz) = EnemyMovementMath.FleeGoal(threatPos.X, threatPos.Z, allyPos.X, allyPos.Z, FleeBehindAllyDistance);
                goal = new Vector3(gx, allyPos.Y, gz);
            }
        }

        tactics.FleeTimeLeft -= deltaTime;
        var to = goal - npcPos;
        to.Y = 0;
        var remaining = to.Length();
        if (remaining <= FleeArriveDistance || tactics.FleeTimeLeft <= 0f)
        {
            // Back into the fight; the next approach tick walks it in again.
            tactics.Fleeing = false;
            return false;
        }

        var speed = (npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f) * FleeSpeedScale;
        var step = MathF.Min(remaining, speed * deltaTime);
        MoveTacticsStep(npc, gameObj, npcPos, npcPos + to / remaining * step, deltaTime, terrainCache);
        ForceRotateToward(npc, npcPos + to, deltaTime);
        return true;
    }

    /// <summary>Deterministic per-enemy value in [0, 1) so the same enemy always makes the same choice.</summary>
    private static float StableRoll(uint id)
    {
        var x = id ^ 0xF1EEu;
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216.0f;
    }

    // Keyed by entity id, not address, and outside approachPaths: that state is dropped whenever
    // vnavmesh is off or the approach stops, and these timers must survive it.
    private readonly Dictionary<uint, EnemyTacticsState> tacticsStates = new();
    // The enemy list of the Tick in progress (flee looks for allies in it).
    private IReadOnlyList<SimulatedNpc> tickNpcs = Array.Empty<SimulatedNpc>();

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

    // Ranged/caster reposition: after an attack, sometimes run to a new spot further round the ring.
    private const float RepositionChance = 0.5f;
    private const float RepositionCooldownSeconds = 8f;
    private const float RepositionArcMinDeg = 25f;
    private const float RepositionArcMaxDeg = 45f;
    // Backstep: bodies closer than this overlap (two ~0.5y radii); step this far away at most.
    private const float CrowdedDistance = 0.8f;
    private const float BackstepDistance = 0.7f;
    private const float BackstepCooldownSeconds = 1.5f;

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

    private const float BaseTurnRate = 5.0f;

    /// <summary>Facing turn rate (rad/s): slower for big bodies with heavy turning enabled.</summary>
    private float NpcTurnRate(SimulatedNpc npc)
        => config.TacticsHeavyTurn
            ? EnemyMovementMath.TurnRate(BaseTurnRate, npc.HitboxRadius)
            : BaseTurnRate;

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
        if (!config.TacticsStrafe && !config.TacticsReposition)
            return false;

        var tactics = Tactics(npc);
        tactics.RepositionCooldown = MathF.Max(0f, tactics.RepositionCooldown - deltaTime);
        tactics.BackstepCooldown = MathF.Max(0f, tactics.BackstepCooldown - deltaTime);
        if (npc.AiState != NpcAiState.Combat || IsNpcBusyAttacking(npc))
        {
            tactics.Strafing = false;
            tactics.WasBusy = true;
            return false;
        }

        // An attack (cast, swing, telegraph) finished since the last hold tick.
        var justAttacked = tactics.WasBusy;
        tactics.WasBusy = false;
        var maxR = holdRange + bodyExtra;
        var minR = MathF.Max(0.6f, holdRange * 0.75f) + bodyExtra;

        if (!tactics.Strafing && config.TacticsReposition)
        {
            if (justAttacked && !HasMeleePositioningIntent(npc) && tactics.RepositionCooldown <= 0f &&
                Random.Shared.NextSingle() < RepositionChance)
            {
                // Ranged/caster: shift to a new firing spot after an attack, as players do.
                tactics.RepositionCooldown = RepositionCooldownSeconds;
                var arc = RepositionArcMinDeg + Random.Shared.NextSingle() * (RepositionArcMaxDeg - RepositionArcMinDeg);
                TryStartHoldMove(npc, tactics, npcPos, targetPos, arc, minR, maxR, 1f);
            }
            else if (tactics.BackstepCooldown <= 0f &&
                     TryFindCrowdingBody(npc.SimulatedEntityId, npcPos, out var crowder))
            {
                // Standing inside another body: one short step straight away from it, kept in band.
                tactics.BackstepCooldown = BackstepCooldownSeconds;
                var away = npcPos - crowder;
                away.Y = 0;
                away = away.LengthSquared() > 0.0001f ? Vector3.Normalize(away) : new Vector3(MathF.Sin(npc.SimulatedEntityId), 0, MathF.Cos(npc.SimulatedEntityId));
                var stepTo = npcPos + away * BackstepDistance;
                var (bx, bz) = EnemyMovementMath.CircleGoal(targetPos.X, targetPos.Z, stepTo.X, stepTo.Z, 0f, minR, maxR);
                StartHoldMove(tactics, new Vector3(bx, npcPos.Y, bz), StrafeSpeedScale);
            }
        }

        if (!tactics.Strafing)
        {
            if (!config.TacticsStrafe)
                return false;

            tactics.StrafeTimer -= deltaTime;
            if (tactics.StrafeTimer > 0f)
                return false;

            tactics.StrafeTimer = NextStrafeInterval();
            if (Random.Shared.NextSingle() >= StrafeChance)
                return false;

            var arc = StrafeArcMinDeg + Random.Shared.NextSingle() * (StrafeArcMaxDeg - StrafeArcMinDeg);
            if (!TryStartHoldMove(npc, tactics, npcPos, targetPos, arc, minR, maxR, StrafeSpeedScale))
                return false;
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

        var speed = (npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f) * tactics.StrafeSpeed;
        var step = MathF.Min(remaining, speed * deltaTime);
        var next = npcPos + to / remaining * step;
        MoveTacticsStep(npc, gameObj, npcPos, next, deltaTime, terrainCache);
        ForceRotateToward(npc, npcPos + to, deltaTime);
        return true;
    }

    /// <summary>Start an arc move around the target (random side first, other side if blocked).</summary>
    private bool TryStartHoldMove(
        SimulatedNpc npc, EnemyTacticsState tactics, Vector3 npcPos, Vector3 targetPos,
        float arcDeg, float minR, float maxR, float speedScale)
    {
        var first = Random.Shared.Next(2) == 0 ? -1f : 1f;
        foreach (var sign in new[] { first, -first })
        {
            var (gx, gz) = EnemyMovementMath.CircleGoal(targetPos.X, targetPos.Z, npcPos.X, npcPos.Z, arcDeg * sign, minR, maxR);
            var candidate = new Vector3(gx, npcPos.Y, gz);
            if (IsSpotClear(npc.SimulatedEntityId, candidate, StrafeClearance))
            {
                StartHoldMove(tactics, candidate, speedScale);
                return true;
            }
        }
        return false;
    }

    private static void StartHoldMove(EnemyTacticsState tactics, Vector3 goal, float speedScale)
    {
        tactics.Strafing = true;
        tactics.StrafeGoal = goal;
        tactics.StrafeTimeLeft = StrafeMaxDuration;
        tactics.StrafeSpeed = speedScale;
    }

    /// <summary>Nearest other body overlapping this one (closer than <see cref="CrowdedDistance"/>).</summary>
    private bool TryFindCrowdingBody(uint selfId, Vector3 npcPos, out Vector3 crowder)
    {
        crowder = default;
        var best = CrowdedDistance;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && combatEngine.State.PlayerState.IsAlive)
        {
            var d = FlatDistance(player.Position, npcPos);
            if (d < best) { best = d; crowder = player.Position; }
        }

        foreach (var entity in combatEngine.State.Entities.Values)
        {
            if (!entity.IsAlive || entity.EntityId == selfId)
                continue;
            var position = combatEngine.GetSimulatedEntityPosition(entity);
            if (position == Vector3.Zero)
                continue;
            var d = FlatDistance(position, npcPos);
            if (d < best) { best = d; crowder = position; }
        }

        return best < CrowdedDistance;
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
