// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Companions;

/// <summary>
/// Who fights whom, for both sides, by chance: each fighter draws its target at random, weighted by
/// threat and the situation, and keeps it a few seconds; it draws again only when something happens
/// (its target falls, its time is up, someone new hits it, the lord's focus or the body's holder
/// changes). The same weights for allies and enemies; the weights, the draw and the hold are
/// <see cref="AggroModel"/>'s, the situation multipliers are applied here. Tuned and checked offline
/// in Tests/TargetingSim (which links AggroModel; with the guards' weight on seizers raised to x30:
/// 100 seeds, field battle as before, guarding the lord's body 45% won against 39% for the old fixed
/// choice, 3.7 target switches a minute against 20.9).
/// </summary>
public unsafe partial class CombatCompanionManager
{
    // Situation multipliers (AggroModel): focus x2, attacking the player x1.5, the body's holder x100
    // for those going for it, the seizers x30 for the guards, someone kneeling x3, out of a guard's
    // reach x0.05.
    private const float FocusGain = AggroModel.FocusGain;
    private const float AttackingPlayerGain = AggroModel.AttackingPlayerGain;
    private const float TaskGain = AggroModel.TaskGain;
    private const float GuardSeizerGain = AggroModel.GuardSeizerGain;
    private const float ExposedGain = AggroModel.ExposedGain;
    private const float UnreachableGain = AggroModel.UnreachableGain;

    private readonly Dictionary<uint, AggroState> aggro = new();
    private readonly Dictionary<uint, (int Melee, int Ranged)> aggroCounts = new();
    private readonly List<(uint Id, float Weight)> aggroDraw = new();
    private IReadOnlyList<SimulatedNpc> aggroEnemies = Array.Empty<SimulatedNpc>();

    /// <summary>
    /// Where each fighter's own generator (the one its targets are drawn with) gets its seed, the first
    /// time that fighter is seen. Unseeded, unless a caller wants a repeatable run and gives a seeded
    /// one; fighters already seen keep the generator they have until the next reset.
    /// </summary>
    public Random AggroSeedSource { private get; set; } = new();

    // How often each side drew a target, and how often the draw took a fighter off a target it could
    // have kept (a voluntary switch, the measure behind sw/min in Tests/TargetingSim), written to the
    // log every few seconds in which anyone drew.
    private int enemyDraws, enemySwitches, allyDraws, allySwitches;
    private readonly System.Diagnostics.Stopwatch targetReportClock = System.Diagnostics.Stopwatch.StartNew();

    private void ReportTargetSwitches(IReadOnlyList<SimulatedNpc> enemies)
    {
        if (targetReportClock.Elapsed.TotalSeconds < 5)
            return;
        targetReportClock.Restart();
        if (enemyDraws + allyDraws == 0)
            return;
        var fightingEnemies = 0;
        foreach (var e in enemies)
            if (e.IsSpawned && e.State.IsAlive && !(e.IsSentry && e.AiState == Ai.NpcAiState.Idle)) fightingEnemies++;
        var fightingAllies = 0;
        foreach (var c in companions)
            if (c.IsSpawned && c.State.IsAlive) fightingAllies++;
        log.Info($"Targets: enemies {enemySwitches} switches / {enemyDraws} draws, allies {allySwitches} / {allyDraws} over 5 s ({fightingEnemies} enemies, {fightingAllies} allies fighting).");
        enemyDraws = enemySwitches = allyDraws = allySwitches = 0;
    }

    private AggroState AggroOf(uint id)
    {
        if (!aggro.TryGetValue(id, out var state))
            aggro[id] = state = new AggroState { Rng = new Random(AggroSeedSource.Next()) };
        return state;
    }

    /// <summary>A hit from an enemy on one of the player's side: the victim's threat table and its "hit by someone new".</summary>
    public void RegisterHit(SimulatedEntityState source, SimulatedEntityState target, int damage)
    {
        if (source.IsPlayer || source.IsCompanion || damage <= 0)
            return;
        AddThreat(target.EntityId, source.EntityId, damage, source.IsTank);
    }

    private void NoteHit(uint victimId, uint sourceId)
    {
        var state = AggroOf(victimId);
        var now = combatEngine.State.SimulationTime;
        AggroModel.NoteHit(state, sourceId, now);
    }

    /// <summary>How many fighters, melee and ranged, are on each target now (both sides).</summary>
    private void CountAggro()
    {
        aggroCounts.Clear();
        foreach (var enemy in aggroEnemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || !enemyTargetByEnemyId.TryGetValue(enemy.SimulatedEntityId, out var target))
                continue;
            Count(target, IsRangedStyle(enemy.Behavior.AutoAttackStyle));
        }
        foreach (var companion in companions)
            if (companion.IsSpawned && companion.State.IsAlive && companion.CurrentTargetId != 0)
                Count(companion.CurrentTargetId, IsRangedStyle(companion.Behavior.AutoAttackStyle));

        void Count(uint target, bool ranged)
        {
            var (melee, rangedCount) = aggroCounts.GetValueOrDefault(target);
            aggroCounts[target] = ranged ? (melee, rangedCount + 1) : (melee + 1, rangedCount);
        }
    }

    /// <summary>The weight shared by both sides (AggroModel.Weight); the situation multiplier is the caller's.</summary>
    private float AggroWeight(
        uint selfId, Vector3 selfPos, float selfReach, bool selfRanged, uint currentTarget,
        uint candidateId, Vector3 candidatePos, float candidateHpShare, uint candidateTarget)
    {
        var share = AggroModel.ThreatShare(threatByEnemy.GetValueOrDefault(selfId), candidateId);
        var (melee, ranged) = aggroCounts.GetValueOrDefault(candidateId);
        return AggroModel.Weight(
            new AggroModel.Self(selfId, selfPos, selfReach, selfRanged, currentTarget),
            new AggroModel.Candidate(candidateId, candidatePos, candidateHpShare, candidateTarget),
            share, melee, ranged);
    }

    private uint DrawAggro(AggroState state) => AggroModel.Draw(state, aggroDraw);

    private void HoldAggro(AggroState state, uint target) => AggroModel.Hold(state, target, combatEngine.State.SimulationTime);

    /// <summary>Whether this friend is kneeling (raising someone, or praying over the lord): open to the enemy.</summary>
    private bool IsExposed(uint friendId)
    {
        if (friendId == 0)
            return false;
        if (friendId == prayerId && prayerAtPost)
            return true;
        foreach (var rescue in rescues.Values)
            if (rescue.Channelling && rescue.RescuerId == friendId)
                return true;
        return false;
    }

    /// <summary>The target of a friend of the player's side (the player's is the one they have selected).</summary>
    private uint FriendTarget(SimulatedEntityState friend)
        => friend.IsPlayer ? selectedFocusTargetId
            : GetCompanion(friend.EntityId)?.CurrentTargetId ?? 0;

    private static float HpShare(SimulatedEntityState state) => CombatantMath.HpShare(state);

    private static float EnemyReach(SimulatedNpc enemy) => AggroModel.EnemyAggroReach(enemy.Behavior);

    /// <summary>
    /// An enemy's target among the candidates (the player's side, captives already left out unless
    /// nobody else is left): kept, or drawn again on an event.
    /// </summary>
    private SimulatedEntityState? DrawEnemyTarget(
        SimulatedNpc enemy,
        List<(uint EntityId, SimulatedEntityState State, float Dps)> candidates)
    {
        var state = AggroOf(enemy.SimulatedEntityId);
        var now = combatEngine.State.SimulationTime;
        var current = enemyTargetByEnemyId.GetValueOrDefault(enemy.SimulatedEntityId);
        var currentLive = candidates.Exists(c => c.EntityId == current);
        var holder = LordSeizerTarget(enemy)?.SimulatedEntityId ?? 0;

        var due = AggroModel.IsDue(state, now, currentLive, holder != state.Holder);
        state.HitByNew = false;
        state.Holder = holder;
        if (!due)
            return candidates.Find(c => c.EntityId == current).State;

        CountAggro();
        aggroDraw.Clear();
        var selfPos = enemy.BattleChara != null ? (Vector3)((GameObject*)enemy.BattleChara)->Position : enemy.SpawnPosition;
        var selfRanged = IsRangedStyle(enemy.Behavior.AutoAttackStyle);
        var reach = EnemyReach(enemy);
        foreach (var (id, candidate, _) in candidates)
        {
            var w = AggroWeight(enemy.SimulatedEntityId, selfPos, reach, selfRanged, currentLive ? current : 0,
                id, combatEngine.GetSimulatedEntityPosition(candidate), HpShare(candidate), FriendTarget(candidate));
            if (id == holder)
                w *= TaskGain;
            if (IsExposed(id))
                w *= ExposedGain;
            aggroDraw.Add((id, w));
        }
        var drawn = DrawAggro(state);
        enemyDraws++;
        if (currentLive && drawn != current)
            enemySwitches++;
        HoldAggro(state, drawn);
        enemyTargetByEnemyId[enemy.SimulatedEntityId] = drawn;
        return candidates.Find(c => c.EntityId == drawn).State;
    }

    /// <summary>An ally's target among the living enemies that have noticed the fight: kept, or drawn again on an event.</summary>
    private SimulatedNpc? DrawCompanionTarget(CombatCompanion companion, List<SimulatedNpc> enemies)
    {
        var state = AggroOf(companion.SimulatedEntityId);
        var now = combatEngine.State.SimulationTime;
        var current = companion.CurrentTargetId;
        var currentEnemy = current == 0 ? null : enemies.Find(e => e.SimulatedEntityId == current);

        var due = AggroModel.IsDue(state, now, currentEnemy != null, selectedFocusTargetId != state.Focus);
        state.HitByNew = false;
        state.Focus = selectedFocusTargetId;
        // Guarding the lord: a target that has gone out of the guard's reach is let go at once.
        if (!due && GuardingLord && !ComputeCompanionReachability(companion, currentEnemy!).Reachable)
            due = true;
        if (!due)
            return currentEnemy;

        CountAggro();
        aggroDraw.Clear();
        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;
        var selfRanged = IsRangedStyle(companion.Behavior.AutoAttackStyle);
        var reach = CrowdReachNow(companion);
        var playerId = combatEngine.State.PlayerState.EntityId;
        var hasBody = TryGetLordBody(out var body, out _) && GuardingLord;
        foreach (var enemy in enemies)
        {
            var enemyPos = (Vector3)((GameObject*)enemy.BattleChara)->Position;
            var enemyTarget = enemyTargetByEnemyId.GetValueOrDefault(enemy.SimulatedEntityId);
            var w = AggroWeight(companion.SimulatedEntityId, self, reach, selfRanged, currentEnemy != null ? current : 0,
                enemy.SimulatedEntityId, enemyPos, HpShare(enemy.State), enemyTarget);
            if (selectedFocusTargetId != 0 && enemy.SimulatedEntityId == selectedFocusTargetId)
                w *= FocusGain;
            if (enemyTarget == playerId && combatEngine.State.PlayerState.IsAlive)
                w *= AttackingPlayerGain;
            if (IsExposed(enemyTarget))
                w *= ExposedGain;
            if (hasBody)
            {
                if (LordSeizers.Contains(enemy.SimulatedEntityId))
                    w *= GuardSeizerGain;
                w *= 1f + Math.Clamp(1f - FlatDistance(enemyPos, body) / NearBodyTargetReach, 0f, 1f);
                if (!ComputeCompanionReachability(companion, enemy).Reachable)
                    w *= UnreachableGain;
            }
            aggroDraw.Add((enemy.SimulatedEntityId, w));
        }
        var drawn = DrawAggro(state);
        allyDraws++;
        if (currentEnemy != null && drawn != current)
            allySwitches++;
        HoldAggro(state, drawn);
        return enemies.Find(e => e.SimulatedEntityId == drawn);
    }

    private void ClearAggro()
    {
        aggro.Clear();
        aggroCounts.Clear();
    }
}
