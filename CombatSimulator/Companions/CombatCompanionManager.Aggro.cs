// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Companions;

/// <summary>
/// Who fights whom, for both sides, by chance: each fighter draws its target at random, weighted by
/// threat and the situation, and keeps it a few seconds; it draws again only when something happens
/// (its target falls, its time is up, someone new hits it, the lord's focus or the body's holder
/// changes). The same weights for allies and enemies. Tuned and checked offline in Tests/TargetingSim
/// (WeightedCalm, with the guards' weight on seizers raised to x30: 100 seeds, field battle as before,
/// guarding the lord's body 45% won against 39% for the old fixed choice, 3.7 target switches a
/// minute against 20.9).
/// </summary>
public unsafe partial class CombatCompanionManager
{
    // How long a drawn target is kept, and the shortest hold before being hit by someone new may
    // trigger a new draw; "someone new" is anyone who had not hit it in this window.
    private const float AggroDwellMin = 4f;
    private const float AggroDwellMax = 8f;
    private const float AggroHitHold = 2.5f;
    private const float NewAttackerWindow = 5f;

    // w = (floor + gain * share^2) / crowding / distance * finish * mutual * sticky * situation.
    private const float ThreatFloor = 0.25f;
    private const float ThreatGain = 1.5f;
    // Crowding: others of my kind (melee or ranged) on it count fully, the other kind half.
    private const float CrowdK = 4f;
    private const float AggroDistanceScale = 10f;
    private const float FinishGain = 0.5f;
    private const float MutualGain = 1.5f;
    private const float StickyGain = 6f;
    private const float FocusGain = 2f;
    private const float AttackingPlayerGain = 1.5f;
    // Enemies going for the lord's body on whoever holds it.
    private const float TaskGain = 100f;
    // Guards of the body on the enemies going for it.
    private const float GuardSeizerGain = 30f;
    // Someone kneeling to raise or pray (x4 damage, not fighting back): an opening for the enemy, and
    // a friend to cover for the allies (on the enemies hitting it).
    private const float ExposedGain = 3f;
    // An enemy a guard cannot reach without leaving the body.
    private const float UnreachableGain = 0.05f;

    private sealed class AggroState
    {
        public uint Target;
        public float HeldSince;
        public float DwellUntil;
        public bool HitByNew;
        public uint Focus;
        public uint Holder;
        public Random Rng = null!;
        public readonly Dictionary<uint, float> LastHitBy = new();
    }

    private readonly Dictionary<uint, AggroState> aggro = new();
    private readonly Dictionary<uint, (int Melee, int Ranged)> aggroCounts = new();
    private readonly List<(uint Id, float Weight)> aggroDraw = new();
    private IReadOnlyList<SimulatedNpc> aggroEnemies = Array.Empty<SimulatedNpc>();
    private readonly Random aggroSeeds = new();

    private AggroState AggroOf(uint id)
    {
        if (!aggro.TryGetValue(id, out var state))
            aggro[id] = state = new AggroState { Rng = new Random(aggroSeeds.Next()) };
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
        if (now - state.LastHitBy.GetValueOrDefault(sourceId, float.NegativeInfinity) > NewAttackerWindow && state.Target != sourceId)
            state.HitByNew = true;
        state.LastHitBy[sourceId] = now;
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

    /// <summary>The weight shared by both sides; the situation multiplier is the caller's.</summary>
    private float AggroWeight(
        uint selfId, Vector3 selfPos, float selfReach, bool selfRanged, uint currentTarget,
        uint candidateId, Vector3 candidatePos, float candidateHpShare, uint candidateTarget)
    {
        var share = 0f;
        if (threatByEnemy.TryGetValue(selfId, out var table) && table.Count > 0)
        {
            var max = 0f;
            foreach (var v in table.Values)
                max = MathF.Max(max, v);
            share = EnemyMovementMath.ThreatShare(table.GetValueOrDefault(candidateId), max);
        }
        var w = ThreatFloor + ThreatGain * share * share;

        var (melee, ranged) = aggroCounts.GetValueOrDefault(candidateId);
        var same = (selfRanged ? ranged : melee) - (currentTarget == candidateId ? 1 : 0);
        var other = selfRanged ? melee : ranged;
        var crowd = MathF.Max(0f, same) + 0.5f * other;
        w /= 1f + crowd * crowd / (CrowdK * CrowdK);

        w /= 1f + MathF.Max(0f, FlatDistance(selfPos, candidatePos) - selfReach) / AggroDistanceScale;
        w *= 1f + FinishGain * (1f - Math.Clamp(candidateHpShare, 0f, 1f));
        if (candidateTarget == selfId)
            w *= MutualGain;
        if (candidateId == currentTarget)
            w *= StickyGain;
        return w;
    }

    private uint DrawAggro(AggroState state)
    {
        var total = 0.0;
        foreach (var (_, w) in aggroDraw)
            total += w;
        if (aggroDraw.Count == 0)
            return 0;
        var r = state.Rng.NextDouble() * total;
        foreach (var (id, w) in aggroDraw)
        {
            r -= w;
            if (r <= 0)
                return id;
        }
        return aggroDraw[^1].Id;
    }

    private void HoldAggro(AggroState state, uint target)
    {
        var now = combatEngine.State.SimulationTime;
        state.Target = target;
        state.HeldSince = now;
        state.DwellUntil = now + AggroDwellMin + (float)state.Rng.NextDouble() * (AggroDwellMax - AggroDwellMin);
    }

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

    private static float HpShare(SimulatedEntityState state)
        => state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 1f;

    private static float EnemyReach(SimulatedNpc enemy)
        => enemy.Behavior.AutoAttackStyle == NpcAttackStyle.Ranged ? enemy.Behavior.AutoAttackRange : MathF.Max(3f, enemy.Behavior.AutoAttackRange);

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

        var due = !currentLive || now >= state.DwellUntil ||
                  (state.HitByNew && now - state.HeldSince >= AggroHitHold) ||
                  holder != state.Holder;
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

        var due = currentEnemy == null || now >= state.DwellUntil ||
                  (state.HitByNew && now - state.HeldSince >= AggroHitHold) ||
                  selectedFocusTargetId != state.Focus;
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
        HoldAggro(state, drawn);
        return enemies.Find(e => e.SimulatedEntityId == drawn);
    }

    private void ClearAggro()
    {
        aggro.Clear();
        aggroCounts.Clear();
    }
}
