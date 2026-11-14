// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Ai;

// Enhanced enemy tactics (Configuration.EnemyTactics). These are decisions only: where an enemy wants
// to be and how fast. The crowd on the navigation mesh does all the moving (TryCrowdMove), so a tactic
// never steps an enemy itself.
public unsafe partial class NpcAiController
{
    private sealed class EnemyTacticsState
    {
        // Circling while holding range; a ranged enemy's new firing spot after an attack.
        public float StrafeTimer;
        public bool Strafing;
        public Vector3 StrafeGoal;
        public float StrafeTimeLeft;
        public float StrafeSpeed = StrafeSpeedScale;
        public bool WasBusy;
        public float RepositionCooldown;

        // Taking turns in melee: whether this enemy holds one of its target's attacker places, and the
        // outer-ring spot it waits on otherwise.
        public bool Admitted;
        public uint AdmittedTargetId;
        public Vector3 WaitSpot;
        public Vector3 WaitAnchor;
        public bool HasWaitSpot;

        public bool Charging;

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

    // Keyed by entity id, so the state survives an enemy leaving and rejoining the crowd.
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
    private const float StrafeArriveDistance = 0.3f;
    // Do not pick a sidestep spot this close to another body.
    private const float StrafeClearance = 1.0f;

    // Ranged/caster reposition: after an attack, sometimes run to a new spot further round the ring.
    private const float RepositionChance = 0.5f;
    private const float RepositionCooldownSeconds = 8f;
    private const float RepositionArcMinDeg = 25f;
    private const float RepositionArcMaxDeg = 45f;

    // Taking turns: at most this many melee enemies fight one target at once (configurable, 3..20);
    // the rest wait on a ring this far outside their standing gap and step in, closest first, when a
    // place frees.
    private int MeleeAttackersPerTarget => Math.Clamp(config.EnemyTacticsMeleeAttackers, 3, 20);
    private const float WaitRingExtra = 3.5f;
    private const float WaitSpotRefreshDistance = 2.0f;
    private const float EngageDelayJitterMax = 1.2f;

    // Charge: an enemy far from its target closes the gap at a sprint, dropping back to the normal
    // run once it is near (hysteresis so it does not toggle at the boundary).
    private const float ChargeStartDistance = 15f;
    private const float ChargeStopDistance = 8f;
    private const float ChargeSpeedScale = 1.8f;

    private const float BaseTurnRate = 5.0f;

    /// <summary>
    /// Once per frame: every enemy that has just entered the fight wakes idle allies within its aggro
    /// range (NpcBehavior.AggroRange). Woken allies do not call further, so a call reaches one hop, not
    /// the whole map.
    /// </summary>
    private void TickCallForHelp(IReadOnlyList<SimulatedNpc> npcs)
    {
        if (!config.EnhancedEnemyTactics)
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
        => config.EnhancedEnemyTactics && tacticsStates.TryGetValue(npc.SimulatedEntityId, out var t) && t.Fleeing;

    /// <summary>
    /// Where a fighting enemy's tactics want it instead of its plain standing spot, and the speed
    /// factor for getting there; null to leave it to the plain approach. In order: running from the
    /// fight at low HP; waiting its turn on the outer ring; a sidestep or new firing spot while it holds
    /// its range.
    /// </summary>
    private Vector3? TacticsGoal(SimulatedNpc npc, Vector3 npcPos, Vector3 targetPos, float stand, float bodies,
        float deltaTime, out float speedScale)
    {
        speedScale = 1f;
        if (!config.EnhancedEnemyTactics)
            return null;

        if (FleeGoal(npc, npcPos, targetPos, deltaTime) is { } flee)
        {
            speedScale = FleeSpeedScale;
            return flee;
        }

        var tactics = Tactics(npc);
        if (IsMeleeFighter(npc) && !tactics.Admitted)
        {
            if (!tactics.HasWaitSpot || FlatDistance(tactics.WaitAnchor, targetPos) > WaitSpotRefreshDistance)
            {
                tactics.WaitSpot = PickWaitSpot(npc.SimulatedEntityId, npcPos, targetPos, stand + bodies + WaitRingExtra);
                tactics.WaitAnchor = targetPos;
                tactics.HasWaitSpot = true;
            }
            return tactics.WaitSpot;
        }

        if (npc.InReach && HoldMoveGoal(npc, tactics, npcPos, targetPos, stand + bodies, deltaTime) is { } hold)
        {
            speedScale = tactics.StrafeSpeed;
            return hold;
        }
        return null;
    }

    /// <summary>
    /// Low-HP flee: start (once per enemy, for the enemies whose stable roll says so) and give the spot
    /// it runs to, behind the nearest other living enemy (or its spawn point). Null when not fleeing.
    /// </summary>
    private Vector3? FleeGoal(SimulatedNpc npc, Vector3 npcPos, Vector3 threatPos, float deltaTime)
    {
        if (npc.AiState != NpcAiState.Combat)
            return null;

        var allies = tickNpcs;
        var tactics = Tactics(npc);
        if (!tactics.Fleeing)
        {
            var hp = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 1f;
            if (tactics.HasFled || hp > FleeHpFraction)
                return null;
            tactics.HasFled = true; // one decision per enemy, flee or not
            if (StableRoll(npc.SimulatedEntityId) >= FleeChance)
                return null;

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
                return null; // nowhere to run

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
        if (FlatDistance(goal, npcPos) <= FleeArriveDistance || tactics.FleeTimeLeft <= 0f)
        {
            // Back into the fight.
            tactics.Fleeing = false;
            return null;
        }
        return goal;
    }

    /// <summary>
    /// Holding range: occasionally a short arc around the target (a ranged enemy also shifts to a new
    /// firing spot after an attack), staying inside the standing band. Never while casting or swinging.
    /// </summary>
    private Vector3? HoldMoveGoal(SimulatedNpc npc, EnemyTacticsState tactics, Vector3 npcPos, Vector3 targetPos,
        float centres, float deltaTime)
    {
        tactics.RepositionCooldown = MathF.Max(0f, tactics.RepositionCooldown - deltaTime);
        if (npc.AiState != NpcAiState.Combat || IsNpcBusyAttacking(npc))
        {
            tactics.Strafing = false;
            tactics.WasBusy = true;
            return null;
        }

        // An attack (cast, swing, telegraph) finished since the last hold tick.
        var justAttacked = tactics.WasBusy;
        tactics.WasBusy = false;
        var maxR = centres;
        var minR = MathF.Max(npc.HitboxRadius + 0.6f, centres * 0.75f);

        if (!tactics.Strafing && justAttacked && !IsMeleeFighter(npc) && tactics.RepositionCooldown <= 0f &&
            RandomSource.NextSingle() < RepositionChance)
        {
            tactics.RepositionCooldown = RepositionCooldownSeconds;
            var arc = RepositionArcMinDeg + RandomSource.NextSingle() * (RepositionArcMaxDeg - RepositionArcMinDeg);
            TryStartHoldMove(npc, tactics, npcPos, targetPos, arc, minR, maxR, 1f);
        }

        if (!tactics.Strafing)
        {
            tactics.StrafeTimer -= deltaTime;
            if (tactics.StrafeTimer > 0f)
                return null;
            tactics.StrafeTimer = NextStrafeInterval();
            if (RandomSource.NextSingle() >= StrafeChance)
                return null;
            var arc = StrafeArcMinDeg + RandomSource.NextSingle() * (StrafeArcMaxDeg - StrafeArcMinDeg);
            if (!TryStartHoldMove(npc, tactics, npcPos, targetPos, arc, minR, maxR, StrafeSpeedScale))
                return null;
        }

        tactics.StrafeTimeLeft -= deltaTime;
        if (FlatDistance(tactics.StrafeGoal, npcPos) <= StrafeArriveDistance || tactics.StrafeTimeLeft <= 0f)
        {
            tactics.Strafing = false;
            return null;
        }
        return tactics.StrafeGoal;
    }

    /// <summary>Start an arc move around the target (random side first, other side if blocked).</summary>
    private bool TryStartHoldMove(
        SimulatedNpc npc, EnemyTacticsState tactics, Vector3 npcPos, Vector3 targetPos,
        float arcDeg, float minR, float maxR, float speedScale)
    {
        var first = RandomSource.Next(2) == 0 ? -1f : 1f;
        foreach (var sign in new[] { first, -first })
        {
            var (gx, gz) = EnemyMovementMath.CircleGoal(targetPos.X, targetPos.Z, npcPos.X, npcPos.Z, arcDeg * sign, minR, maxR);
            var candidate = new Vector3(gx, npcPos.Y, gz);
            if (IsSpotClear(npc.SimulatedEntityId, candidate, StrafeClearance))
            {
                tactics.Strafing = true;
                tactics.StrafeGoal = candidate;
                tactics.StrafeTimeLeft = StrafeMaxDuration;
                tactics.StrafeSpeed = speedScale;
                return true;
            }
        }
        return false;
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

    /// <summary>Random extra wind-up when an enemy first engages, so a pack does not start in lockstep.</summary>
    private void ApplyEngageJitter(SimulatedNpc npc)
    {
        if (config.EnhancedEnemyTactics)
            npc.EngageDelayTimer = MathF.Max(npc.EngageDelayTimer, RandomSource.NextSingle() * EngageDelayJitterMax);
    }

    /// <summary>A melee fighter: nothing in its kit reaches beyond melee.</summary>
    private static bool IsMeleeFighter(SimulatedNpc npc)
    {
        if (npc.Behavior.AutoAttackStyle == NpcAttackStyle.Ranged)
            return false;
        foreach (var skill in npc.Behavior.Skills)
            if (skill.Range > MeleeReachLimit)
                return false;
        return true;
    }

    /// <summary>
    /// Once per frame, before movement: decide which melee enemies hold an attacker place on their
    /// target. Places already held are kept (no reshuffling); free places go to the waiters closest to
    /// the target.
    /// </summary>
    private void UpdateMeleeAdmission(
        IReadOnlyList<SimulatedNpc> npcs,
        IReadOnlyDictionary<uint, (SimulatedEntityState State, Vector3 Position)> targetByNpcId)
    {
        if (!config.EnhancedEnemyTactics)
            return;

        var byTarget = new Dictionary<uint, List<(SimulatedNpc Npc, EnemyTacticsState Tactics, float Dist)>>();
        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned)
                continue;
            var tactics = Tactics(npc);
            if (!npc.IsEngaged || !npc.State.IsAlive || !IsMeleeFighter(npc) ||
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
            var admitted = list.Count(e => e.Tactics.Admitted);

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

    /// <summary>Approach speed multiplier: <see cref="ChargeSpeedScale"/> while charging, else 1.</summary>
    private float ChargeScale(SimulatedNpc npc, float gap)
    {
        if (!config.TacticsCharge)
            return 1f;

        var tactics = Tactics(npc);
        if (tactics.Charging)
            tactics.Charging = gap > ChargeStopDistance && npc.IsEngaged;
        else
            tactics.Charging = gap > ChargeStartDistance && npc.IsEngaged;
        return tactics.Charging ? ChargeSpeedScale : 1f;
    }

    /// <summary>Facing turn rate (rad/s): slower for big bodies with heavy turning enabled.</summary>
    private float NpcTurnRate(SimulatedNpc npc)
        => config.EnhancedEnemyTactics
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

    private float NextStrafeInterval()
        => StrafeIntervalMin + RandomSource.NextSingle() * (StrafeIntervalMax - StrafeIntervalMin);

    private bool IsNpcBusyAttacking(SimulatedNpc npc)
        => npc.State.IsCasting || npc.State.AnimationLock > 0f || isTelegraphBusy(npc.SimulatedEntityId);
}
