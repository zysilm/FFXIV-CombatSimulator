// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
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
    // Situation multipliers (AggroModel): focus x2, attacking the player x1.5, someone kneeling x3; the
    // battle rules add theirs (Battle/Rules: the body's holder x100 for those going for it, the seizers
    // x30 for the guards, out of a guard's reach x0.05).
    private const float FocusGain = AggroModel.FocusGain;
    private const float AttackingPlayerGain = AggroModel.AttackingPlayerGain;
    private const float ExposedGain = AggroModel.ExposedGain;

    /// <summary>
    /// Where each fighter's own generator (the one its targets are drawn with) gets its seed, the first
    /// time that fighter is seen. Unseeded, unless a caller wants a repeatable run and gives a seeded
    /// one; fighters already seen keep the generator they have until the next reset.
    /// </summary>
    public Random AggroSeedSource { set => targeting.SeedSource = value; }

    private void ReportTargetSwitches(IReadOnlyList<SimulatedNpc> enemies)
        => targeting.ReportIfDue(log, enemies, companions);

    private AggroState AggroOf(uint id) => targeting.AggroOf(id);

    /// <summary>A hit from an enemy on one of the player's side: the victim's threat table and its "hit by someone new".</summary>
    public void RegisterHit(SimulatedEntityState source, SimulatedEntityState target, int damage)
        => targeting.RegisterHit(source, target, damage);

    /// <summary>How many fighters, melee and ranged, are on each target now (both sides).</summary>
    private void CountAggro() => targeting.CountAggro(companions);

    /// <summary>The weight shared by both sides (AggroModel.Weight); the situation multiplier is the caller's.</summary>
    private float AggroWeight(
        uint selfId, Vector3 selfPos, float selfReach, bool selfRanged, uint currentTarget,
        uint candidateId, Vector3 candidatePos, float candidateHpShare, uint candidateTarget)
        => targeting.Weight(selfId, selfPos, selfReach, selfRanged, currentTarget,
            candidateId, candidatePos, candidateHpShare, candidateTarget);

    private uint DrawAggro(AggroState state) => targeting.Draw(state);

    private void HoldAggro(AggroState state, uint target) => targeting.Hold(state, target);

    /// <summary>Whether this friend is kneeling (raising someone, or praying over the lord): open to the enemy.</summary>
    private bool IsExposed(uint friendId)
    {
        if (friendId == 0)
            return false;
        return ruleParts.Exposes(friendId);
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
        var current = targeting.EnemyTargetOf(enemy.SimulatedEntityId);
        var currentLive = candidates.Exists(c => c.EntityId == current);
        // The one this enemy's task makes it cut down (the lord's body: the retainer in the way), 0: none.
        var holder = ruleParts.HolderFor(enemy);

        var due = AggroModel.IsDue(state, now, currentLive, holder != state.Holder);
        state.HitByNew = false;
        state.Holder = holder;
        if (!due)
            return candidates.Find(c => c.EntityId == current).State;

        CountAggro();
        targeting.BeginDraw();
        var selfPos = enemy.BattleChara != null ? world.PositionOf(enemy) : enemy.SpawnPosition;
        var selfRanged = IsRangedStyle(enemy.Behavior.AutoAttackStyle);
        var reach = EnemyReach(enemy);
        foreach (var (id, candidate, _) in candidates)
        {
            var w = AggroWeight(enemy.SimulatedEntityId, selfPos, reach, selfRanged, currentLive ? current : 0,
                id, combatEngine.GetSimulatedEntityPosition(candidate), HpShare(candidate), FriendTarget(candidate));
            w = ruleParts.EnemyTargetWeight(new EnemyTargetQuery(enemy, holder, id), w);
            if (IsExposed(id))
                w *= ExposedGain;
            targeting.AddCandidate(id, w);
        }
        var drawn = DrawAggro(state);
        targeting.NoteEnemyDraw(currentLive && drawn != current);
        HoldAggro(state, drawn);
        targeting.SetEnemyTarget(enemy.SimulatedEntityId, drawn);
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
        // A rule may make it let go at once (guarding the lord: a target gone out of the guard's reach).
        if (!due && ruleParts.ForcesRedraw(companion, currentEnemy!))
            due = true;
        if (!due)
            return currentEnemy;

        CountAggro();
        targeting.BeginDraw();
        var self = world.PositionOf(companion);
        var selfRanged = IsRangedStyle(companion.Behavior.AutoAttackStyle);
        var reach = CrowdReachNow(companion);
        var playerId = combatEngine.State.PlayerState.EntityId;
        ruleParts.BeginAllyDraw(companion);
        foreach (var enemy in enemies)
        {
            var enemyPos = world.PositionOf(enemy);
            var enemyTarget = targeting.EnemyTargetOf(enemy.SimulatedEntityId);
            var w = AggroWeight(companion.SimulatedEntityId, self, reach, selfRanged, currentEnemy != null ? current : 0,
                enemy.SimulatedEntityId, enemyPos, HpShare(enemy.State), enemyTarget);
            if (selectedFocusTargetId != 0 && enemy.SimulatedEntityId == selectedFocusTargetId)
                w *= FocusGain;
            if (enemyTarget == playerId && combatEngine.State.PlayerState.IsAlive)
                w *= AttackingPlayerGain;
            if (IsExposed(enemyTarget))
                w *= ExposedGain;
            // The rules' own (guarding the lord's body: the seizers, nearness to the body, out of reach).
            w = ruleParts.AllyTargetWeight(new AllyTargetQuery(companion, enemy, enemyPos), w);
            targeting.AddCandidate(enemy.SimulatedEntityId, w);
        }
        var drawn = DrawAggro(state);
        targeting.NoteAllyDraw(currentEnemy != null && drawn != current);
        HoldAggro(state, drawn);
        return enemies.Find(e => e.SimulatedEntityId == drawn);
    }
}
