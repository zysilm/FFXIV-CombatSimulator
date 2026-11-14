// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Combat;

/// <summary>A question to the battle rules about one candidate target (docs/PLUGIN_ARCHITECTURE.md §4).</summary>
public readonly record struct TargetQuery(Combatant Self, uint CandidateId);

/// <summary>
/// The rule hooks on target selection (docs/PLUGIN_ARCHITECTURE.md §4, §5.1). Declared here for the
/// battle-rule parts; nothing calls them yet: today's rule weights are still applied where they were
/// (CombatCompanionManager.Aggro.cs), in the same order, until refactor stage B5 moves them.
/// </summary>
public interface ITargetingRules
{
    /// <summary>A multiplier on a candidate's weight (1 when no rule cares).</summary>
    float TargetWeight(in TargetQuery query);

    /// <summary>Removes candidates an enemy must not draw (not a weight of 0: that would change the draw).</summary>
    void FilterCandidates(SimulatedNpc enemy, List<(uint EntityId, SimulatedEntityState State, float Dps)> candidates);

    /// <summary>Whether this fighter must draw again now, whatever its hold.</summary>
    bool ForcesRedraw(Combatant fighter);
}

/// <summary>
/// Who fights whom: the state behind both sides' target draws (docs/PLUGIN_ARCHITECTURE.md §4,
/// refactor stage B2b). Every fighter's threat table, every fighter's <see cref="AggroState"/> and its
/// generator, the enemies' current targets, how many fighters are on each target, and the draw and
/// switch counts behind the <c>Targets:</c> log line. Moved unchanged out of CombatCompanionManager,
/// which owns it for now and forwards its public methods here; the draws themselves (and the
/// situation multipliers) are still the manager's.
/// </summary>
public sealed class TargetingService
{
    private readonly Func<float> now;

    // Threat table of every fighter, both sides: victim id -> (attacker id -> decaying threat). Who
    // fights whom is drawn from it.
    private readonly Dictionary<uint, Dictionary<uint, float>> threatByEnemy = new();
    private readonly Dictionary<uint, uint> enemyTargetByEnemyId = new();
    private readonly Dictionary<uint, AggroState> aggro = new();
    private readonly Dictionary<uint, (int Melee, int Ranged)> aggroCounts = new();
    private readonly List<(uint Id, float Weight)> aggroDraw = new();
    private IReadOnlyList<SimulatedNpc> aggroEnemies = Array.Empty<SimulatedNpc>();

    // How often each side drew a target, and how often the draw took a fighter off a target it could
    // have kept (a voluntary switch, the measure behind sw/min in Tests/TargetingSim), written to the
    // log every few seconds in which anyone drew.
    private int enemyDraws, enemySwitches, allyDraws, allySwitches;
    private readonly System.Diagnostics.Stopwatch targetReportClock = System.Diagnostics.Stopwatch.StartNew();

    /// <param name="now">The simulation time (CombatEngine.State.SimulationTime).</param>
    public TargetingService(Func<float> now)
    {
        this.now = now;
    }

    /// <summary>
    /// Where each fighter's own generator (the one its targets are drawn with) gets its seed, the first
    /// time that fighter is seen. Unseeded, unless a caller wants a repeatable run and gives a seeded
    /// one; fighters already seen keep the generator they have until the next reset.
    /// </summary>
    public Random SeedSource { private get; set; } = new();

    /// <summary>The battle rules' hooks on target selection. Not called yet (see <see cref="ITargetingRules"/>).</summary>
    public ITargetingRules? Rules { get; set; }

    // ---- Threat ----

    /// <summary>A hit from an enemy on one of the player's side: the victim's threat table and its "hit by someone new".</summary>
    public void RegisterHit(SimulatedEntityState source, SimulatedEntityState target, int damage)
    {
        if (source.IsPlayer || source.IsCompanion || damage <= 0)
            return;
        AddThreat(target.EntityId, source.EntityId, damage, source.IsTank);
    }

    public void AddThreat(uint enemyId, uint sourceId, int damage, bool isTank)
    {
        if (enemyId == 0 || sourceId == 0 || damage <= 0)
            return;
        NoteHit(enemyId, sourceId);

        if (!threatByEnemy.TryGetValue(enemyId, out var table))
            threatByEnemy[enemyId] = table = new Dictionary<uint, float>();
        table[sourceId] = table.GetValueOrDefault(sourceId) + EnemyMovementMath.HitThreat(damage, isTank);
    }

    public void DecayThreat(float deltaTime)
    {
        if (threatByEnemy.Count == 0)
            return;

        foreach (var table in threatByEnemy.Values)
        {
            foreach (var key in table.Keys.ToList())
                table[key] = EnemyMovementMath.DecayThreat(table[key], deltaTime);
        }
    }

    // ---- Each fighter's draw state ----

    public AggroState AggroOf(uint id)
    {
        if (!aggro.TryGetValue(id, out var state))
            aggro[id] = state = new AggroState { Rng = new Random(SeedSource.Next()) };
        return state;
    }

    private void NoteHit(uint victimId, uint sourceId)
    {
        var state = AggroOf(victimId);
        var now = this.now();
        AggroModel.NoteHit(state, sourceId, now);
    }

    // ---- The enemies' current targets ----

    public bool TryGetEnemyTarget(uint enemyId, out uint targetId)
        => enemyTargetByEnemyId.TryGetValue(enemyId, out targetId);

    /// <summary>The enemy's current target, or 0.</summary>
    public uint EnemyTargetOf(uint enemyId) => enemyTargetByEnemyId.GetValueOrDefault(enemyId);

    public void SetEnemyTarget(uint enemyId, uint targetId) => enemyTargetByEnemyId[enemyId] = targetId;

    public void ForceEnemyTarget(uint enemyId, uint targetId)
    {
        if (targetId == 0)
            enemyTargetByEnemyId.Remove(enemyId);
        else
            enemyTargetByEnemyId[enemyId] = targetId;
    }

    /// <summary>How many enemies have this one as their target.</summary>
    public int AttackersOn(uint entityId)
    {
        var n = 0;
        foreach (var target in enemyTargetByEnemyId.Values)
            if (target == entityId)
                n++;
        return n;
    }

    // ---- Crowding: how many fighters are on each target ----

    /// <summary>The enemies counted by <see cref="CountAggro"/>: the list the companion manager ticked with.</summary>
    public void TrackEnemies(IReadOnlyList<SimulatedNpc> enemies) => aggroEnemies = enemies;

    /// <summary>How many fighters, melee and ranged, are on each target now (both sides).</summary>
    public void CountAggro(IReadOnlyList<CombatCompanion> companions)
    {
        aggroCounts.Clear();
        foreach (var enemy in aggroEnemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || !enemyTargetByEnemyId.TryGetValue(enemy.SimulatedEntityId, out var target))
                continue;
            Count(target, AggroModel.IsRangedStyle(enemy.Behavior.AutoAttackStyle));
        }
        foreach (var companion in companions)
            if (companion.IsSpawned && companion.State.IsAlive && companion.CurrentTargetId != 0)
                Count(companion.CurrentTargetId, AggroModel.IsRangedStyle(companion.Behavior.AutoAttackStyle));

        void Count(uint target, bool ranged)
        {
            var (melee, rangedCount) = aggroCounts.GetValueOrDefault(target);
            aggroCounts[target] = ranged ? (melee, rangedCount + 1) : (melee + 1, rangedCount);
        }
    }

    // ---- The draw ----

    /// <summary>The weight shared by both sides (AggroModel.Weight); the situation multiplier is the caller's.</summary>
    public float Weight(
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

    /// <summary>Starts a draw: clears the weights of the last one.</summary>
    public void BeginDraw() => aggroDraw.Clear();

    public void AddCandidate(uint id, float weight) => aggroDraw.Add((id, weight));

    public uint Draw(AggroState state) => AggroModel.Draw(state, aggroDraw);

    public void Hold(AggroState state, uint target) => AggroModel.Hold(state, target, now());

    // ---- Counts for the log ----

    public void NoteEnemyDraw(bool switched)
    {
        enemyDraws++;
        if (switched)
            enemySwitches++;
    }

    public void NoteAllyDraw(bool switched)
    {
        allyDraws++;
        if (switched)
            allySwitches++;
    }

    /// <summary>The <c>Targets:</c> line, every 5 s in which anyone drew.</summary>
    public void ReportIfDue(IPluginLog log, IReadOnlyList<SimulatedNpc> enemies, IReadOnlyList<CombatCompanion> companions)
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

    // ---- Reset ----

    /// <summary>Forgets every target, threat table and draw state (combat reset, despawn).</summary>
    public void Reset()
    {
        enemyTargetByEnemyId.Clear();
        threatByEnemy.Clear();
        aggro.Clear();
        aggroCounts.Clear();
    }
}
