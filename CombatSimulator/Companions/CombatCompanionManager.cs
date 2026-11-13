// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Companions;

public unsafe partial class CombatCompanionManager : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly Configuration config;
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly ITargetManager targetManager;
    private readonly IPluginLog log;

    private readonly List<CombatCompanion> companions = new();
    private readonly List<PendingSpawn> pendingSpawns = new();
    private readonly ConcurrentQueue<CompanionSpawnSource> spawnQueue = new();
    private readonly HashSet<int> allocatedIndices = new();
    private readonly Dictionary<uint, uint> enemyTargetByEnemyId = new();
    // Enhanced tactics threat table: enemy id -> (attacker id -> decaying threat).
    private readonly Dictionary<uint, Dictionary<uint, float>> threatByEnemy = new();
    private const float ThreatScoreWeight = 150f;
    // Wider than EnemyTargetSwitchThreshold so threat ties do not ping-pong an enemy between two
    // attackers; together with EnemyTargetCurrentBonus this is the aggro hysteresis.
    private const float ThreatSwitchThreshold = 60f;
    private uint nextEntityId = 0xF1000001;


    // Floor for plugin-synthesised entity IDs. Spawned virtual enemies start at
    // 0xF0000001 and companion clones at 0xF1000001; no real player's EntityId
    // falls in this range. Used to keep our own spawned actors out of the
    // "sense visible players" scan (otherwise a humanoid enemy that ends up as a
    // Pc, or an existing companion clone, would be re-cloned into the party).
    private const uint SimulatedEntityIdFloor = 0xF0000000;

    private float playerRecentDamage;
    private float playerRecentDps;
    private uint selectedFocusTargetId;
    private uint lastFocusTargetId;
    private float senseTimer;
    private bool combatAnchorActive;
    private Vector3 combatAnchorPosition;
    private float combatAnchorRotation;

    /// <summary>Hard cap on simultaneous companions. The game's object table runs
    /// out of slots before this in practice, which is handled gracefully at spawn.</summary>
    public const int MaxCompanionCap = 50;

    private const int MaxPendingFrames = 120;
    private const float TargetRange = 3.0f;
    private const float RecentDpsWindowSeconds = 8.0f;
    private const float RetargetDpsLead = 1.20f;
    private const float FollowDistance = 3.0f;
    private const float FollowStopDistance = 0.6f;
    private const float CombatAnchorRelocateDistance = 10.0f;
    private const float SenseInterval = 1.0f;
    private const float TargetSelectInterval = 0.25f;
    private const float SelectedTargetBonus = 130f;
    private const float LastPlayerTargetBonus = 95f;
    private const float AttackingPlayerBonus = 120f;
    private const float PreviousTargetBonus = 35f;
    private const float LowHpFinishBonus = 35f;
    private const float AssignmentPenalty = 95f;
    private const float PlayerFocusOvercapPenalty = 160f;
    private const float DistancePenaltyPerYalm = 1.0f;
    private const float TargetSwitchThreshold = 20f;
    private const int MaxPlayerFocusAssistants = 2;
    private const float EnemyTargetAssignmentPenalty = 80f;
    private const float EnemyTargetCurrentBonus = 50f;
    private const float EnemyTargetDpsWeight = 0.25f;
    private const float EnemyTargetSwitchThreshold = 25f;
    private const int CompanionMeleePressureCap = 2;
    private const int CompanionRangedPressureCap = 3;
    private const int CompanionFocusPressureBonus = 1;
    private const int EnemyMeleePressureCap = 3;
    private const int EnemyRangedPressureCap = 2;
    private const float TargetPressureOverCapPenalty = 260f;
    private const int TargetGraphMaxPreferredDepth = 2;
    private const float TargetGraphChainDepthPenalty = 75f;
    private const float TargetGraphMutualPairBonus = 45f;
    private const float TargetGraphPeelForPlayerBonus = 55f;
    private const float TargetGraphBreakLongChainBonus = 30f;
    private const float ReachabilitySoftPenaltyWeight = 55f;
    private const float ReachabilityHardPenalty = 220f;
    private const float ReachabilityRecoveryDistance = 0.75f;
    private static readonly DrawDataContainer.EquipmentSlot[] AppearanceEquipmentSlots =
    {
        DrawDataContainer.EquipmentSlot.Head,
        DrawDataContainer.EquipmentSlot.Body,
        DrawDataContainer.EquipmentSlot.Hands,
        DrawDataContainer.EquipmentSlot.Legs,
        DrawDataContainer.EquipmentSlot.Feet,
        DrawDataContainer.EquipmentSlot.Ears,
        DrawDataContainer.EquipmentSlot.Neck,
        DrawDataContainer.EquipmentSlot.Wrists,
        DrawDataContainer.EquipmentSlot.RFinger,
        DrawDataContainer.EquipmentSlot.LFinger,
    };

    public IReadOnlyList<CombatCompanion> Companions => companions;

    /// <summary>Where the living party stands: the player and every living companion.</summary>
    public void CollectPartyPositions(List<Vector3> into)
    {
        var player = objectTable.LocalPlayer;
        if (player != null && combatEngine.State.PlayerState.IsAlive)
            into.Add(player.Position);
        if (!config.EnableCombatCompanions)
            return;
        foreach (var companion in companions)
            if (companion.IsSpawned && companion.State.IsAlive && companion.BattleChara != null)
                into.Add((Vector3)((GameObject*)companion.BattleChara)->Position);
    }
    public Func<bool>? SuspendCombat { get; set; }
    public int PendingCount => pendingSpawns.Count + spawnQueue.Count;
    public bool HasActiveCompanions => config.EnableCombatCompanions && companions.Any(c => c.IsSpawned && c.State.IsAlive);
    public bool HasLivingCompanions => config.EnableCombatCompanions && companions.Any(c => c.IsSpawned && c.State.IsAlive);

    public Action<CombatCompanion>? OnCompanionSpawnComplete { get; set; }
    public Action<nint>? OnCompanionDeathRagdoll { get; set; }
    public Action<string>? OnSpawnError { get; set; }
    public Func<uint, bool>? IsSourceEnemy { get; set; }
    /// <summary>Optional world-space surface height for walking over active corpses.</summary>
    public Func<nint, Vector3, float?>? CorpseSupportHeightProvider { private get; set; }

    public CombatCompanionManager(
        IObjectTable objectTable,
        IClientState clientState,
        Configuration config,
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        ITargetManager targetManager,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.config = config;
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.targetManager = targetManager;
        this.log = log;
    }

    /// <summary>
    /// Take over a creature the NPC spawner made as an ally (any model, not only a humanoid): from
    /// here on it is a companion like the cloned ones, fighting with its own kit and with the HP
    /// its strength and level gave it. The actor and its slot become this manager's.
    /// </summary>
    public bool AdoptNpc(SimulatedNpc npc, NpcSpawnRequest request)
    {
        if (npc.BattleChara == null)
            return false;

        var obj = (GameObject*)npc.BattleChara;
        if (companions.Count + PendingCount >= MaxCompanionCap)
        {
            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr != null && npc.ObjectIndex >= 0)
            {
                obj->DisableDraw();
                clientObjMgr->DeleteObjectByIndex((ushort)npc.ObjectIndex, 0);
            }
            OnSpawnError?.Invoke($"Cannot add {npc.Name}: the party is full ({MaxCompanionCap}).");
            return false;
        }

        var entityId = nextEntityId++;
        obj->EntityId = entityId;
        allocatedIndices.Add(npc.ObjectIndex);

        // A Normal ally at the player's level is about as tough as the player; its strength scales its
        // HP as it does an enemy's. Its hits are everyone's: the same skill does the same damage.
        combatEngine.EnsurePlayerInitialized();
        var playerHp = Math.Max(1, combatEngine.State.PlayerState.MaxHp);
        var state = npc.State;
        var scale = CombatBudgetModel.SecondsToKill(state.Tier) / CombatBudgetModel.SecondsToKill(EnemyTier.Normal)
                    * MathF.Max(0.0001f, state.LevelFactor);
        state.EntityId = entityId;
        state.IsPlayer = false;
        state.IsCompanion = true;
        state.MaxHp = (int)Math.Clamp(playerHp * (double)scale, 1, int.MaxValue);
        state.CurrentHp = state.MaxHp;

        var companion = new CombatCompanion
        {
            SimulatedEntityId = entityId,
            ObjectIndex = npc.ObjectIndex,
            SourceEntityId = 0,
            Name = npc.Name,
            BattleChara = npc.BattleChara,
            GameObjectRef = npc.GameObjectRef,
            SpawnPosition = npc.SpawnPosition,
            Behavior = npc.Behavior,
            IsRanged = npc.IsRanged,
            IsSpawned = true,
            AutoAttackTimer = Random.Shared.NextSingle() * MathF.Max(0.1f, npc.Behavior.AutoAttackDelay),
            State = state,
            IsCatalogNpc = true,
            CatalogRequest = request,
        };
        foreach (var skill in companion.Behavior.Skills)
            skill.CooldownRemaining = Random.Shared.NextSingle() * skill.Cooldown;

        companions.Add(companion);
        combatEngine.RegisterCompanionEntity(companion);
        log.Info($"Ally '{companion.Name}' joined the party at index {companion.ObjectIndex}, entityId={entityId:X}.");
        return true;
    }

    /// <summary>Remove one companion from the world.</summary>
    public void DespawnCompanion(CombatCompanion companion)
    {
        if (!companions.Contains(companion))
            return;
        Despawn(companion);
    }

    public int SpawnFromVisiblePlayers(int maxToQueue = int.MaxValue)
    {
        if (!config.EnableCombatCompanions)
            return 0;

        var max = Math.Clamp(config.CombatCompanionMaxCount, 0, MaxCompanionCap);
        var availableSlots = Math.Max(0, max - companions.Count - PendingCount);
        availableSlots = Math.Min(availableSlots, Math.Max(0, maxToQueue));
        if (availableSlots == 0)
            return 0;

        var queued = 0;
        var seenSources = BuildSeenSourceSet();
        foreach (var obj in objectTable)
        {
            if (queued >= availableSlots)
                break;
            if (obj is not IPlayerCharacter player)
                continue;
            if (objectTable.LocalPlayer != null && player.EntityId == objectTable.LocalPlayer.EntityId)
                continue;
            if (player.EntityId >= SimulatedEntityIdFloor)
                continue;
            if (player.Address == nint.Zero)
                continue;
            if (IsSourceEnemy?.Invoke(player.EntityId) == true)
                continue;
            if (ShouldLeaveVisiblePlayerForEnemySide(player.EntityId))
                continue;
            if (!seenSources.Add(player.EntityId))
                continue;

            spawnQueue.Enqueue(CompanionSpawnSource.FromObject(player));
            queued++;
        }

        return queued;
    }

    public int SpawnSelfCharacters(int count, bool randomizeAppearance, bool ignoreConfiguredMax = false)
    {
        if (!config.EnableCombatCompanions)
            return 0;

        var max = ignoreConfiguredMax
            ? MaxCompanionCap
            : Math.Clamp(config.CombatCompanionMaxCount, 0, MaxCompanionCap);
        var availableSlots = Math.Max(0, max - companions.Count - PendingCount);
        var toQueue = Math.Min(Math.Max(0, count), availableSlots);
        var queued = 0;
        for (var i = 0; i < toQueue; i++)
        {
            if (!SpawnSelfCharacter(randomizeAppearance, ignoreConfiguredMax))
                break;
            queued++;
        }
        return queued;
    }

    public bool SpawnSelfCharacter(bool randomizeAppearance = false, bool ignoreConfiguredMax = false)
    {
        if (!config.EnableCombatCompanions)
            return false;

        var max = ignoreConfiguredMax
            ? MaxCompanionCap
            : Math.Clamp(config.CombatCompanionMaxCount, 0, MaxCompanionCap);
        if (companions.Count + PendingCount >= max)
            return false;

        var player = objectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return false;

        // Intentionally no source-dedupe here: the player can be cloned many times
        // (each click adds one self-clone) up to the max count / available game slots.
        spawnQueue.Enqueue(CompanionSpawnSource.FromObject(player, randomizeAppearance));
        return true;
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> enemies)
    {
        while (spawnQueue.TryDequeue(out var source))
            ProcessSpawnRequest(source);

        TickPendingSpawns();

        if (!config.EnableCombatCompanions)
            return;

        TickSensing(deltaTime);
        DecayThreat(deltaTime);

        var assignedTargets = new Dictionary<uint, int>();
        var companionPressure = new Dictionary<PressureKey, int>();
        TickCombatAnchor(enemies);
        UpdateFocusTarget(enemies);

        var now = combatEngine.State.SimulationTime;
        var liveCompanions = companions.ToList();

        for (var i = 0; i < liveCompanions.Count; i++)
        {
            var companion = liveCompanions[i];
            TickRecentDps(companion, deltaTime);

            // Target (re)selection is the heavy per-companion work — it scores every enemy,
            // each with a reachability probe. Throttle it to a few times a second, but
            // reselect immediately when the companion has no target or its target just
            // died, otherwise it would briefly retreat before re-engaging.
            var hasLiveTarget = companion.CurrentTargetId != 0 &&
                HasLiveEnemy(enemies, companion.CurrentTargetId);
            if (!hasLiveTarget || now >= companion.NextTargetSelectAt)
            {
                AssignCompanionTarget(companion, enemies, assignedTargets, companionPressure);
                // Stagger by entity id so companions don't all reselect on the same frame.
                companion.NextTargetSelectAt = now + TargetSelectInterval + (companion.SimulatedEntityId % 8) * 0.02f;
            }
            else
            {
                // Still register the held target into the spread accumulators so the
                // companions that DO reselect this frame see accurate crowding/pressure.
                assignedTargets[companion.CurrentTargetId] = assignedTargets.GetValueOrDefault(companion.CurrentTargetId) + 1;
                AddPressure(companionPressure, companion.CurrentTargetId, IsRangedStyle(companion.Behavior.AutoAttackStyle));
            }
        }

        var enemyTargets = new Dictionary<uint, uint>();
        var enemyPressure = new Dictionary<PressureKey, int>();
        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive)
                continue;
            // A sentry that has not noticed the party presses on nobody.
            if (enemy.IsSentry && enemy.AiState == Ai.NpcAiState.Idle)
                continue;

            var target = SelectEnemyTargetInternal(enemy, enemyPressure);
            if (target != null)
            {
                enemyTargets[enemy.SimulatedEntityId] = target.EntityId;
                AddPressure(enemyPressure, target.EntityId, IsRangedStyle(enemy.Behavior.AutoAttackStyle));
            }
        }

        TickRescues(deltaTime);
        TickLord();
        for (var i = 0; i < liveCompanions.Count; i++)
            TickCompanion(liveCompanions[i], deltaTime, enemies, i, liveCompanions.Count);

        TickPlayerRecentDps(deltaTime);
        ReportActivity(enemies);
    }

    public void RegisterDamage(uint companionEntityId, int damage)
    {
        var companion = companions.FirstOrDefault(c => c.SimulatedEntityId == companionEntityId);
        if (companion == null)
            return;

        companion.RecentDamage += Math.Max(0, damage);
        companion.RecentDps = Math.Max(companion.RecentDps, companion.RecentDamage / RecentDpsWindowSeconds);
    }

    public void RegisterPlayerDamage(int damage)
    {
        playerRecentDamage += Math.Max(0, damage);
        playerRecentDps = Math.Max(playerRecentDps, playerRecentDamage / RecentDpsWindowSeconds);
    }

    public void RegisterPlayerDamage(uint targetId, int damage)
    {
        RegisterPlayerDamage(damage);
        AddThreat(targetId, combatEngine.State.PlayerState.EntityId, damage, combatEngine.State.PlayerState.IsTank);
    }

    private void AddThreat(uint enemyId, uint sourceId, int damage, bool isTank)
    {
        if (!config.EnhancedEnemyTactics || enemyId == 0 || sourceId == 0 || damage <= 0)
            return;

        if (!threatByEnemy.TryGetValue(enemyId, out var table))
            threatByEnemy[enemyId] = table = new Dictionary<uint, float>();
        table[sourceId] = table.GetValueOrDefault(sourceId) + EnemyMovementMath.HitThreat(damage, isTank);
    }

    private void DecayThreat(float deltaTime)
    {
        if (threatByEnemy.Count == 0)
            return;

        foreach (var table in threatByEnemy.Values)
        {
            foreach (var key in table.Keys.ToList())
                table[key] = EnemyMovementMath.DecayThreat(table[key], deltaTime);
        }
    }

    // Threat term for the enemy target score: the candidate's share of the top threat on this enemy.
    private float ThreatScore(uint enemyId, uint candidateId)
    {
        if (!threatByEnemy.TryGetValue(enemyId, out var table) || table.Count == 0)
            return 0f;

        var max = 0f;
        foreach (var value in table.Values)
            max = MathF.Max(max, value);
        return EnemyMovementMath.ThreatShare(table.GetValueOrDefault(candidateId), max) * ThreatScoreWeight;
    }

    /// <summary>
    /// The enemy's current target as decided by <see cref="Tick"/>, which scores with the shared
    /// pressure map. Callers outside that pass (AI, look-at, engine) only read the decision: rescoring
    /// here without pressure disagreed with Tick and flipped the target every frame. A fresh
    /// selection happens only when there is no live decision yet (new enemy, target died).
    /// </summary>
    public SimulatedEntityState? SelectEnemyTarget(SimulatedNpc enemy)
    {
        if (config.EnableCombatCompanions &&
            enemyTargetByEnemyId.TryGetValue(enemy.SimulatedEntityId, out var currentId) &&
            ResolveTargetState(currentId) is { IsAlive: true } current)
            return current;

        return SelectEnemyTargetInternal(enemy, null);
    }

    private SimulatedEntityState? ResolveTargetState(uint entityId)
    {
        var player = combatEngine.State.PlayerState;
        if (entityId == player.EntityId)
            return player;

        foreach (var companion in companions)
        {
            if (companion.SimulatedEntityId == entityId)
                return companion.IsSpawned ? companion.State : null;
        }

        return null;
    }

    private SimulatedEntityState? SelectEnemyTargetInternal(
        SimulatedNpc enemy,
        Dictionary<PressureKey, int>? pressureMap = null)
    {
        if (!config.EnableCombatCompanions)
            return combatEngine.State.PlayerState.IsAlive ? combatEngine.State.PlayerState : null;

        var livingCompanions = companions
            .Where(c => c.IsSpawned && c.State.IsAlive)
            .ToList();
        var hasLivingCompanions = livingCompanions.Count > 0;
        if (!hasLivingCompanions)
            return combatEngine.State.PlayerState.IsAlive ? combatEngine.State.PlayerState : null;

        var candidates = new List<(uint EntityId, SimulatedEntityState State, float Dps)>();
        if (combatEngine.State.PlayerState.IsAlive)
            candidates.Add((combatEngine.State.PlayerState.EntityId, combatEngine.State.PlayerState, playerRecentDps));

        foreach (var companion in livingCompanions)
            candidates.Add((companion.SimulatedEntityId, companion.State, companion.RecentDps));

        candidates.RemoveAll(c => !c.State.IsAlive);
        if (candidates.Count == 0)
            return null;
        // A captive is executed only by an enemy with nobody else left to fight.
        if (candidates.Exists(c => !c.State.IsCaptive))
            candidates.RemoveAll(c => c.State.IsCaptive);

        // Going for the lord's body: whoever stands over it, not whoever the spreading would pick.
        if (LordSeizerTarget(enemy) is { } holder)
        {
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = holder.SimulatedEntityId;
            return holder.State;
        }

        var validTargetIds = candidates.Select(c => c.EntityId).ToHashSet();
        foreach (var kv in enemyTargetByEnemyId.ToList())
        {
            if (!validTargetIds.Contains(kv.Value))
                enemyTargetByEnemyId.Remove(kv.Key);
        }

        if (!enemyTargetByEnemyId.TryGetValue(enemy.SimulatedEntityId, out var currentId))
        {
            var best = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, 0, candidates, pressureMap, enemy.Behavior.AutoAttackStyle);
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = best.EntityId;
            return best.State;
        }

        var current = candidates.FirstOrDefault(c => c.EntityId == currentId);
        if (current.State == null)
        {
            var best = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, 0, candidates, pressureMap, enemy.Behavior.AutoAttackStyle);
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = best.EntityId;
            return best.State;
        }

        var balancedBest = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, current.EntityId, candidates, pressureMap, enemy.Behavior.AutoAttackStyle);
        var currentScore = ScoreEnemyTargetCandidate(enemy.SimulatedEntityId, current.EntityId, current, pressureMap, enemy.Behavior.AutoAttackStyle);
        var bestScore = ScoreEnemyTargetCandidate(enemy.SimulatedEntityId, current.EntityId, balancedBest, pressureMap, enemy.Behavior.AutoAttackStyle);
        var switchThreshold = config.EnhancedEnemyTactics ? ThreatSwitchThreshold : EnemyTargetSwitchThreshold;
        if (balancedBest.EntityId != current.EntityId && bestScore > currentScore + switchThreshold)
        {
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = balancedBest.EntityId;
            return balancedBest.State;
        }

        return current.State;
    }

    private (uint EntityId, SimulatedEntityState State, float Dps) SelectBalancedEnemyTarget(
        uint enemyId,
        uint currentId,
        IReadOnlyList<(uint EntityId, SimulatedEntityState State, float Dps)> candidates,
        IReadOnlyDictionary<PressureKey, int>? pressureMap,
        NpcAttackStyle attackStyle)
        => candidates
            .OrderByDescending(c => ScoreEnemyTargetCandidate(enemyId, currentId, c, pressureMap, attackStyle))
            .ThenBy(c => StableTargetTieBreak(enemyId, c.EntityId))
            .First();

    private float ScoreEnemyTargetCandidate(
        uint enemyId,
        uint currentId,
        (uint EntityId, SimulatedEntityState State, float Dps) candidate,
        IReadOnlyDictionary<PressureKey, int>? pressureMap,
        NpcAttackStyle attackStyle)
    {
        var assignedCount = enemyTargetByEnemyId
            .Where(kv => kv.Key != enemyId)
            .Count(kv => kv.Value == candidate.EntityId);

        // Enhanced tactics: aggro follows threat (damage, tanks weighted up) instead of raw DPS.
        var score = config.EnhancedEnemyTactics
            ? ThreatScore(enemyId, candidate.EntityId)
            : candidate.Dps * EnemyTargetDpsWeight;
        score -= assignedCount * EnemyTargetAssignmentPenalty;
        if (candidate.EntityId == currentId)
            score += EnemyTargetCurrentBonus;
        if (pressureMap != null)
            score -= TargetPressurePenalty(
                pressureMap,
                candidate.EntityId,
                IsRangedStyle(attackStyle),
                EnemyMeleePressureCap,
                EnemyRangedPressureCap);
        score += TargetGraphScore(enemyId, candidate.EntityId);
        return score;
    }

    private static uint StableTargetTieBreak(uint enemyId, uint targetId)
        => (enemyId * 1103515245u + targetId * 2654435761u) & 0xFFFFu;

    private static bool IsRangedStyle(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic;

    private static int PressureCount(IReadOnlyDictionary<PressureKey, int> map, uint targetId, bool ranged)
        => map.GetValueOrDefault(new PressureKey(targetId, ranged));

    private static void AddPressure(Dictionary<PressureKey, int> map, uint targetId, bool ranged)
    {
        var key = new PressureKey(targetId, ranged);
        map[key] = map.GetValueOrDefault(key) + 1;
    }

    private static float TargetPressurePenalty(
        IReadOnlyDictionary<PressureKey, int> map,
        uint targetId,
        bool ranged,
        int meleeCap,
        int rangedCap,
        int extraCap = 0)
    {
        var cap = (ranged ? rangedCap : meleeCap) + extraCap;
        var count = PressureCount(map, targetId, ranged);
        return count >= cap ? (count - cap + 1) * TargetPressureOverCapPenalty : 0f;
    }

    private uint GraphTargetId(uint actorId, uint overrideActorId = 0, uint overrideTargetId = 0)
    {
        if (actorId == overrideActorId)
            return overrideTargetId;

        var companion = companions.FirstOrDefault(c => c.SimulatedEntityId == actorId);
        if (companion is { IsSpawned: true } && companion.State.IsAlive)
            return companion.CurrentTargetId;

        return enemyTargetByEnemyId.GetValueOrDefault(actorId);
    }

    private int TargetChainDepth(uint fromId, uint toId)
    {
        var playerId = combatEngine.State.PlayerState.EntityId;
        var depth = 1;
        var seen = new HashSet<uint> { fromId };
        var current = toId;

        while (current != 0 && !seen.Contains(current))
        {
            seen.Add(current);
            var next = GraphTargetId(current, fromId, toId);
            if (next == 0 || next == playerId)
                break;

            depth++;
            current = next;
        }

        return depth;
    }

    private float TargetGraphScore(uint actorId, uint candidateId)
    {
        var score = 0f;
        var depth = TargetChainDepth(actorId, candidateId);
        if (depth > TargetGraphMaxPreferredDepth)
            score -= (depth - TargetGraphMaxPreferredDepth) * TargetGraphChainDepthPenalty;

        if (GraphTargetId(candidateId) == actorId)
            score += TargetGraphMutualPairBonus;

        if (enemyTargetByEnemyId.ContainsKey(candidateId) &&
            GraphTargetId(candidateId) == combatEngine.State.PlayerState.EntityId)
            score += TargetGraphPeelForPlayerBonus;

        var current = GraphTargetId(actorId);
        if (current != 0)
        {
            var currentDepth = TargetChainDepth(actorId, current);
            if (currentDepth > depth)
                score += Math.Min(2, currentDepth - depth) * TargetGraphBreakLongChainBonus;
        }

        return score;
    }

    public CombatCompanion? GetCompanion(uint entityId)
        => companions.FirstOrDefault(c => c.SimulatedEntityId == entityId);

    public nint? ResolveAddress(uint entityId)
        => GetCompanion(entityId)?.Address;

    public uint GetEnemyTargetId(uint enemyId)
        => enemyTargetByEnemyId.GetValueOrDefault(enemyId);

    public void ForceEnemyTarget(uint enemyId, uint targetId)
    {
        if (targetId == 0)
            enemyTargetByEnemyId.Remove(enemyId);
        else
            enemyTargetByEnemyId[enemyId] = targetId;
    }

    /// <summary>
    /// Combat reset while keeping companions: revive + heal each one and clear its
    /// combat state so it is battle-ready again, without despawning/re-cloning.
    /// </summary>
    public void ResetForCombatReset()
    {
        enemyTargetByEnemyId.Clear();
        threatByEnemy.Clear();
        playerRecentDamage = 0;
        playerRecentDps = 0;
        selectedFocusTargetId = 0;
        lastFocusTargetId = 0;

        ClearRescues();
        ResetLord();
        foreach (var companion in companions)
        {
            companion.State.CurrentHp = companion.State.MaxHp;
            companion.State.CurrentMp = companion.State.MaxMp;
            companion.State.IsCaptive = false;
            companion.State.Executed = false;
            companion.State.IncomingDamageScale = 1f;
            companion.State.AnimationLock = 0;
            companion.DeathAnimationPlayed = false;
            companion.CaptiveEmotePlayed = false;
            companion.EquipmentVariantApplied = false;
            companion.AutoAttackTimer = 0;
            ClearCompanionTargetState(companion);
            companion.RecentDamage = 0;
            companion.RecentDps = 0;

            if (companion.BattleChara == null)
                continue;

            try
            {
                animationController.ResetDeathAnimation(companion.BattleChara);
                RestoreCompanionEquipment(companion);
                var character = (Character*)companion.BattleChara;
                character->Mode = CharacterModes.Normal;
                character->ModeParam = 0;
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Failed to revive companion '{companion.Name}' on reset.");
            }
        }
    }

    public void DespawnAll()
    {
        while (spawnQueue.TryDequeue(out _)) { }

        var clientObjMgr = ClientObjectManager.Instance();
        foreach (var pending in pendingSpawns)
        {
            if (clientObjMgr != null && pending.Companion.ObjectIndex >= 0)
                clientObjMgr->DeleteObjectByIndex((ushort)pending.Companion.ObjectIndex, 0);
        }
        pendingSpawns.Clear();

        foreach (var companion in companions.ToList())
            Despawn(companion);

        allocatedIndices.Clear();
        rescues.Clear();
        prayerId = 0;
        prayerAtPost = false;
        lordFallHandled = false;
        enemyTargetByEnemyId.Clear();
        threatByEnemy.Clear();
        playerRecentDamage = 0;
        playerRecentDps = 0;
        selectedFocusTargetId = 0;
        lastFocusTargetId = 0;
        combatAnchorActive = false;
    }

    private void TickSensing(float deltaTime)
    {
        if (!config.SensePartyMembers)
            return;
        // Only sense/auto-clone party members during an active battle. Otherwise
        // companions would keep spawning while idle in the world or after Stop.
        if (!combatEngine.IsActive)
            return;

        senseTimer = Math.Max(0, senseTimer - deltaTime);
        if (senseTimer > 0)
            return;

        senseTimer = SenseInterval;
        FillFromVisibleSources();
    }

    private int FillFromVisibleSources()
    {
        var max = Math.Clamp(config.CombatCompanionMaxCount, 0, MaxCompanionCap);
        var availableSlots = Math.Max(0, max - companions.Count - PendingCount);
        if (availableSlots == 0)
            return 0;

        var queued = 0;
        var seenSources = BuildSeenSourceSet();

        foreach (var obj in objectTable)
        {
            if (queued >= availableSlots)
                break;
            if (obj is not IPlayerCharacter player)
                continue;
            if (objectTable.LocalPlayer != null && player.EntityId == objectTable.LocalPlayer.EntityId)
                continue;
            if (player.EntityId >= SimulatedEntityIdFloor)
                continue;
            if (IsSourceEnemy?.Invoke(player.EntityId) == true)
                continue;
            if (ShouldLeaveVisiblePlayerForEnemySide(player.EntityId))
                continue;
            if (player.Address == nint.Zero || !seenSources.Add(player.EntityId))
                continue;

            spawnQueue.Enqueue(CompanionSpawnSource.FromObject(player));
            queued++;
        }

        return queued;
    }

    private HashSet<uint> BuildSeenSourceSet()
    {
        var seen = new HashSet<uint>(companions.Select(c => c.SourceEntityId));
        foreach (var pending in pendingSpawns)
            seen.Add(pending.Companion.SourceEntityId);
        foreach (var queued in spawnQueue)
            seen.Add(queued.EntityId);
        return seen;
    }

    public bool HasSourceEntity(uint sourceEntityId) => BuildSeenSourceSet().Contains(sourceEntityId);

    private bool ShouldLeaveVisiblePlayerForEnemySide(uint sourceEntityId)
    {
        if (!config.EnableMapPlayerEnemySensing)
            return false;
        if (!config.SensePartyMembers)
            return false;

        return Random.Shared.Next(2) == 0;
    }

    private void ProcessSpawnRequest(CompanionSpawnSource sourceInfo)
    {
        try
        {
            if (sourceInfo.Address == nint.Zero)
                return;

            var source = (Character*)sourceInfo.Address;
            var sourceRace = ((byte*)&source->DrawData.CustomizeData)[0];
            if (sourceRace == 0)
            {
                OnSpawnError?.Invoke($"Cannot clone {sourceInfo.Name}: source is not a humanoid model.");
                return;
            }

            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr == null)
            {
                OnSpawnError?.Invoke("ClientObjectManager is null. Are you logged in?");
                return;
            }

            var hint = Core.ClientActorSlotAllocator.FindFreeAscending(clientObjMgr, 100);
            if (hint == uint.MaxValue)
            {
                OnSpawnError?.Invoke("CreateBattleCharacter failed - no available slot.");
                return;
            }
            var createResult = clientObjMgr->CreateBattleCharacter(hint);
            if (createResult == 0xFFFFFFFF)
            {
                OnSpawnError?.Invoke("CreateBattleCharacter failed - no available slot.");
                return;
            }

            var index = (int)createResult;
            if (index < 0 || index >= Core.ClientActorSlotAllocator.SharedSlotLimit)
            {
                if (index >= 0 && index < Core.ClientActorSlotAllocator.TotalSlotCount)
                    clientObjMgr->DeleteObjectByIndex((ushort)index, 0);
                OnSpawnError?.Invoke("CreateBattleCharacter returned an actor slot reserved by another system.");
                return;
            }

            var obj = clientObjMgr->GetObjectByIndex((ushort)index);
            if (obj == null)
            {
                clientObjMgr->DeleteObjectByIndex((ushort)index, 0);
                OnSpawnError?.Invoke($"Object null at index {index} after creation.");
                return;
            }
            allocatedIndices.Add(index);

            var chara = (BattleChara*)obj;
            var character = (Character*)chara;

            obj->ObjectKind = ObjectKind.Pc;
            obj->SubKind = 0;
            obj->TargetableStatus = 0;
            obj->RenderFlags = VisibilityFlags.None;

            var spawnPos = CalculateSpawnPosition(companions.Count + pendingSpawns.Count);
            spawnPos = SnapToTerrain(spawnPos) ?? spawnPos;
            obj->Position = spawnPos;
            obj->Rotation = CalculateSpawnRotation(spawnPos);

            var name = sourceInfo.Name;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            for (int j = 0; j < 64; j++)
                obj->Name[j] = j < nameBytes.Length && j < 63 ? nameBytes[j] : (byte)0;

            const CharacterSetupContainer.CopyFlags flags =
                CharacterSetupContainer.CopyFlags.ClassJob |
                CharacterSetupContainer.CopyFlags.WeaponHiding;
            character->CharacterSetup.CopyFromCharacter(source, flags);
            character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);
            if (sourceInfo.RandomizeAppearance)
                RandomizeHairAndFace(character);
            character->SetMode(CharacterModes.Normal, 0);

            IGameObject? gameObjectRef = null;
            try { gameObjectRef = objectTable.CreateObjectReference((nint)obj); }
            catch (Exception ex) { log.Warning(ex, "CreateObjectReference failed for companion clone."); }

            var entityId = nextEntityId++;
            obj->EntityId = entityId;

            var classJobId = sourceInfo.ClassJobId;
            var weaponStyle = NpcWeaponClassifier.DetectFromCharacter(character, log, name);
            var behavior = CreateCompanionBehavior(weaponStyle);

            // A companion is "another me": HP, offense, and defenses all mirror
            // the local player's real combat stats. Only appearance, job, and
            // attack style stay per-source. EnsurePlayerInitialized covers the
            // case where companions are spawned before the simulation starts.
            combatEngine.EnsurePlayerInitialized();
            var pp = combatEngine.State.PlayerState;

            var companion = new CombatCompanion
            {
                SimulatedEntityId = entityId,
                ObjectIndex = index,
                SourceEntityId = sourceInfo.EntityId,
                Name = name,
                BattleChara = chara,
                GameObjectRef = gameObjectRef,
                SpawnPosition = spawnPos,
                Behavior = behavior,
                IsRanged = weaponStyle == NpcAttackStyle.Ranged,
                RandomizedLook = sourceInfo.RandomizeAppearance,
                IsSpawned = false,
                AutoAttackTimer = Random.Shared.NextSingle() * behavior.AutoAttackDelay,
                State = new SimulatedEntityState
                {
                    EntityId = entityId,
                    Name = name,
                    IsPlayer = false,
                    IsCompanion = true,
                    Level = pp.Level,
                    MaxHp = Math.Max(1, pp.MaxHp),
                    CurrentHp = Math.Max(1, pp.MaxHp),
                    MaxMp = pp.MaxMp > 0 ? pp.MaxMp : 10000,
                    CurrentMp = pp.MaxMp > 0 ? pp.MaxMp : 10000,
                    ClassJobId = classJobId,
                    IsTank = IsTankJob(classJobId),
                },
            };
            pendingSpawns.Add(new PendingSpawn { Companion = companion });
            log.Info($"Companion clone '{name}' created at index {index}, entityId={entityId:X}. Pending draw...");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Exception in companion spawn.");
            OnSpawnError?.Invoke($"Companion spawn failed: {ex.Message}");
        }
    }

    // Randomize only hair style, hair color, and face on a freshly cloned
    // character, leaving race/gender/tribe and the rest of the appearance as
    // copied from the source. Written before EnableDraw so the redraw picks up
    // the new customize bytes.
    private void RandomizeHairAndFace(Character* character)
    {
        try
        {
            var customize = (byte*)&character->DrawData.CustomizeData;
            var race = customize[0x00];
            var tribe = customize[0x04];
            var gender = customize[0x01];

            // Face: 1-4 is valid for every race/tribe/gender.
            customize[0x05] = (byte)Random.Shared.Next(1, 5);
            // Hair color: index into the race's color palette.
            customize[0x0A] = (byte)Random.Shared.Next(1, 193);

            // Hair style must be one actually valid for this race/tribe/gender,
            // otherwise the model renders bald. If we can't resolve a valid set,
            // leave the source's (already valid) hairstyle untouched.
            var hairStyle = PickRandomHairStyle(race, tribe, gender);
            if (hairStyle.HasValue)
                customize[0x06] = hairStyle.Value;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to randomize companion hair/face appearance.");
        }
    }

    private byte? PickRandomHairStyle(byte race, byte tribe, byte gender)
    {
        try
        {
            var sheet = Core.Services.DataManager.GetExcelSheet<Resources.Sheets.HairMakeTypeSheet>();
            if (sheet == null)
                return null;

            var styles = new List<byte>();
            foreach (var entry in sheet)
            {
                if (entry.Race.RowId != race || entry.Tribe.RowId != tribe || entry.Gender != gender)
                    continue;

                foreach (var styleRef in entry.HairStyles)
                {
                    if (!styleRef.IsValid)
                        continue;

                    var featureId = styleRef.Value.FeatureID;
                    if (featureId is > 0 and <= byte.MaxValue)
                        styles.Add((byte)featureId);
                }
            }

            if (styles.Count == 0)
                return null;

            return styles[Random.Shared.Next(styles.Count)];
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to resolve valid hairstyles; keeping cloned hairstyle.");
            return null;
        }
    }

    private void TickPendingSpawns()
    {
        for (int i = pendingSpawns.Count - 1; i >= 0; i--)
        {
            var pending = pendingSpawns[i];
            var companion = pending.Companion;
            pending.FramesWaited++;

            try
            {
                if (companion.BattleChara == null)
                {
                    pendingSpawns.RemoveAt(i);
                    continue;
                }

                if (companion.BattleChara->IsReadyToDraw() || pending.FramesWaited >= MaxPendingFrames)
                {
                    companion.BattleChara->EnableDraw();
                    CaptureCompanionEquipment(companion);
                    var obj = (GameObject*)companion.BattleChara;
                    obj->TargetableStatus = ObjectTargetableFlags.IsTargetable;
                    companion.IsSpawned = true;
                    companions.Add(companion);
                    pendingSpawns.RemoveAt(i);
                    OnCompanionSpawnComplete?.Invoke(companion);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Error while enabling companion '{companion.Name}'.");
                pendingSpawns.RemoveAt(i);
            }
        }
    }

    private void TickCompanion(
        CombatCompanion companion,
        float deltaTime,
        IReadOnlyList<SimulatedNpc> enemies,
        int companionIndex,
        int companionCount)
    {
        if (SuspendCombat?.Invoke() == true || !companion.IsSpawned || companion.BattleChara == null)
            return;

        TickCompanionActionTimers(companion, deltaTime);

        if (!companion.State.IsAlive)
        {
            ClearCompanionActionState(companion);
            ClearCompanionTargetState(companion);
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.Dead);
            if (!companion.DeathAnimationPlayed)
            {
                ApplyCompanionDefeatAppearance(companion);
                // (An executed captive drops from its kneel straight into the ragdoll.)
                if (!companion.State.Executed)
                    animationController.PlayDeathAnimation(ToSimulatedNpcView(companion));
                if (config.PartyCompanionDeathRagdoll && companion.Address != nint.Zero)
                    OnCompanionDeathRagdoll?.Invoke(companion.Address);
                companion.DeathAnimationPlayed = true;
                combatEngine.TriggerEnemyVictoryIfPartyDefeated();
            }
            return;
        }

        // MP comes back as it does for enemies: steadily, and with each auto-attack.
        companion.State.RegenerateMp(CombatBudgetModel.EnemyMpRegenPerSecond, deltaTime);

        if (TickCaptive(companion) || TickPrayer(companion, deltaTime) || TickRescuer(companion, deltaTime))
        {
            NoteActivity(ActivityOther);
            return;
        }

        // Only guarding the lord's body holds an ally to one place; otherwise it fights wherever the
        // fight goes.
        if (GuardingLord && !IsWithinCommandRange(companion))
        {
            NoteActivity(ActivityReturning);
            ClearCompanionActionState(companion);
            ClearCompanionTargetState(companion);
            EnterCompanionState(companion, CompanionAiState.ReturningToCommandRange, deltaTime);
            MoveToCommandRange(companion, deltaTime, companionIndex, companionCount);
            return;
        }

        var target = GetCurrentCompanionTarget(companion, enemies);
        if (target == null || target.BattleChara == null)
        {
            NoteActivity(ActivityNoTarget);
            ClearCompanionActionState(companion);
            ClearCompanionTargetState(companion);
            EnterCompanionState(companion, CompanionAiState.ReturningToCommandRange, deltaTime);
            MoveToCommandRange(companion, deltaTime, companionIndex, companionCount);
            return;
        }

        // Out of its standing spot: the crowd walks it in. It attacks whenever the target is within
        // reach, as enemies do, walking or not: in a crush it may never reach its spot.
        var walking = CrowdApproach(companion, target, deltaTime);
        var dist = CrowdGap(companion, target);
        if (walking && dist > CrowdReachNow(companion))
        {
            NoteActivity(ActivityWalkingIn);
            return;
        }
        if (!walking)
        {
            var targetPos = (Vector3)((GameObject*)target.BattleChara)->Position;
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.CombatReady);
            RotateToward(companion, targetPos, deltaTime);
        }
        NoteActivity(ActivityFighting);

        if (companion.State.AnimationLock > 0)
        {
            EnterCompanionState(companion, CompanionAiState.ActionLocked);
            return;
        }

        foreach (var skill in companion.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (skill.CooldownRemaining > 0)
                continue;
            if (dist > SkillReach(companion, skill))
                continue;
            // A skill costs MP as it does an enemy: a share of the companion's own pool. Out of MP,
            // it auto-attacks until it has earned enough back.
            var mpCost = CombatBudgetModel.EnemySkillMpCost(combatEngine.GetActionData(skill.ActionId)?.MpCost ?? 0, companion.State.MaxMp);
            if (companion.State.CurrentMp < mpCost)
                continue;

            var result = combatEngine.ProcessCompanionAction(companion, target,
                skill.ActionId, skill.Potency, skill.AttackStyle, skill.Radius);
            if (result.Success)
            {
                companion.State.CurrentMp = Math.Max(0, companion.State.CurrentMp - mpCost);
                RegisterDamage(companion.SimulatedEntityId, result.Damage);
                AddThreat(target.SimulatedEntityId, companion.SimulatedEntityId, result.Damage, companion.State.IsTank);
                skill.CooldownRemaining = skill.Cooldown / ActionPace();
                companion.State.AnimationLock = 0.6f;
            }
            return;
        }

        companion.AutoAttackTimer -= deltaTime;
        if (companion.AutoAttackTimer <= 0)
        {
            // A caster's auto-attack is a melee swing, as an enemy's: held until it is in melee.
            if (companion.Behavior.AutoAttackStyle == NpcAttackStyle.Magic && dist > CrowdMagicAutoReach)
                return;
            companion.AutoAttackTimer = companion.Behavior.AutoAttackDelay / ActionPace();
            var result = combatEngine.ProcessCompanionAction(
                companion, target,
                companion.Behavior.AutoAttackActionId,
                companion.Behavior.AutoAttackPotency,
                companion.Behavior.AutoAttackStyle);
            if (result.Success)
            {
                companion.State.CurrentMp = Math.Min(companion.State.MaxMp,
                    companion.State.CurrentMp + CombatBudgetModel.EnemyAutoAttackMpRestore);
                RegisterDamage(companion.SimulatedEntityId, result.Damage);
                AddThreat(target.SimulatedEntityId, companion.SimulatedEntityId, result.Damage, companion.State.IsTank);
                companion.State.AnimationLock = 0.6f;
            }
        }
    }

    // Companions keep pace with Action Mode's faster combat (same multiplier as enemies).
    private float ActionPace() => config.ActionMode ? Math.Max(0.1f, config.ActionEnemyAttackSpeed) : 1f;

    private static void TickCompanionActionTimers(CombatCompanion companion, float deltaTime)
    {
        companion.State.AnimationLock = Math.Max(0, companion.State.AnimationLock - deltaTime);
        foreach (var skill in companion.Behavior.Skills)
            skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - deltaTime);
    }

    private static void ClearCompanionActionState(CombatCompanion companion)
    {
        companion.State.IsCasting = false;
        companion.State.CastActionId = 0;
        companion.State.CastTimeElapsed = 0;
        companion.State.CastTimeTotal = 0;
        companion.State.CastTargetId = 0;
        companion.State.AnimationLock = 0;
    }

    private void ClearCompanionTargetState(CombatCompanion companion)
    {
        companion.CurrentTargetId = 0;
    }

    private void AssignCompanionTarget(
        CombatCompanion companion,
        IReadOnlyList<SimulatedNpc> enemies,
        Dictionary<uint, int> assignedTargets,
        Dictionary<PressureKey, int> pressureMap)
    {
        if (!companion.IsSpawned || !companion.State.IsAlive || companion.BattleChara == null)
            return;

        if (GuardingLord && !IsWithinCommandRange(companion))
        {
            ClearCompanionTargetState(companion);
            return;
        }

        var target = SelectCompanionTarget(companion, enemies, assignedTargets, pressureMap);
        if (target == null)
        {
            ClearCompanionTargetState(companion);
            return;
        }

        companion.CurrentTargetId = target?.SimulatedEntityId ?? 0;
        if (target != null)
        {
            assignedTargets[target.SimulatedEntityId] = assignedTargets.GetValueOrDefault(target.SimulatedEntityId) + 1;
            AddPressure(pressureMap, target.SimulatedEntityId, IsRangedStyle(companion.Behavior.AutoAttackStyle));
        }
    }

    private SimulatedNpc? GetCurrentCompanionTarget(CombatCompanion companion, IReadOnlyList<SimulatedNpc> enemies)
        => companion.CurrentTargetId == 0
            ? null
            : enemies.FirstOrDefault(e =>
                e.SimulatedEntityId == companion.CurrentTargetId &&
                e.IsSpawned &&
                e.State.IsAlive &&
                e.BattleChara != null);

    private bool IsWithinCommandRange(CombatCompanion companion)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || companion.BattleChara == null)
            return false;

        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var delta = current - CommandAnchor(player.Position);
        delta.Y = 0;
        return delta.Length() <= CommandLimit(companion.SimulatedEntityId);
    }

    /// <summary>
    /// What the allies keep close to: the lord's body while they guard it, otherwise where the fight
    /// began (or the player).
    /// </summary>
    private Vector3 CommandAnchor(Vector3 playerPosition)
        => GuardingLord ? LordBodyAt(playerPosition) : !combatAnchorActive ? playerPosition : combatAnchorPosition;

    /// <summary>
    /// How far from the command anchor this ally may go: the party range (the guard range around the
    /// lord's body), a little less for some.
    /// </summary>
    private float CommandLimit(uint actorId)
    {
        var jitter = StableUnit(actorId ^ 0xC001u);
        var factor = 1.0f - Math.Clamp(config.PartyCommandRangeRandomness, 0, 0.8f) * jitter;
        return MathF.Max(1.0f, (GuardingLord ? LordGuardRange : config.PartyCommandRange) * factor);
    }

    private static float StableUnit(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216.0f;
    }

    private void MoveToCommandRange(
        CombatCompanion companion,
        float deltaTime,
        int companionIndex,
        int companionCount)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || companion.BattleChara == null)
            return;

        // Its place in the formation behind the anchor (the player, or where the fight began); guarding
        // the lord, its place in the ring round the body.
        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var anchor = CommandAnchor(player.Position);
        var anchorRotation = combatAnchorActive && !GuardingLord ? combatAnchorRotation : player.Rotation;
        var goal = GuardingLord
            ? GuardRingPosition(anchor, companionIndex, companionCount)
            : CalculateFollowPosition(anchor, anchorRotation, companionIndex, companionCount);
        if (FlatDistance(current, goal) > FollowStopDistance)
        {
            MoveToward(companion, goal, deltaTime);
            return;
        }
        StopMove(companion);
        EnterCompanionState(companion, companion.CurrentTargetId == 0 ? CompanionAiState.Idle : CompanionAiState.CombatReady);
        RotateToward(companion, player.Position, deltaTime);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    private SimulatedNpc? SelectCompanionTarget(
        CombatCompanion companion,
        IReadOnlyList<SimulatedNpc> enemies,
        Dictionary<uint, int> assignedTargets,
        IReadOnlyDictionary<PressureKey, int> pressureMap)
    {
        // (A sentry that has not noticed the party is left alone: it may be far off on the map.)
        var aliveEnemies = enemies
            .Where(e => e.IsSpawned && e.State.IsAlive && e.BattleChara != null &&
                        !(e.IsSentry && e.AiState == Ai.NpcAiState.Idle))
            .ToList();
        if (aliveEnemies.Count == 0)
            return null;

        var playerId = combatEngine.State.PlayerState.EntityId;
        var current = companion.CurrentTargetId == 0
            ? null
            : aliveEnemies.FirstOrDefault(e => e.SimulatedEntityId == companion.CurrentTargetId);

        var scoredTargets = aliveEnemies
            .Select(enemy => (Enemy: enemy, Score: ScoreCompanionTarget(
                companion, enemy, aliveEnemies, assignedTargets, playerId, pressureMap)))
            .OrderByDescending(t => t.Score)
            .ToList();

        var best = scoredTargets[0];
        if (current != null)
        {
            var currentScore = scoredTargets.First(t => t.Enemy.SimulatedEntityId == current.SimulatedEntityId).Score;
            if (best.Enemy.SimulatedEntityId != current.SimulatedEntityId &&
                best.Score < currentScore + TargetSwitchThreshold)
            {
                best = (current, currentScore);
            }
        }

        assignedTargets[best.Enemy.SimulatedEntityId] =
            assignedTargets.GetValueOrDefault(best.Enemy.SimulatedEntityId) + 1;
        return best.Enemy;
    }

    private float ScoreCompanionTarget(
        CombatCompanion companion,
        SimulatedNpc enemy,
        IReadOnlyList<SimulatedNpc> enemies,
        IReadOnlyDictionary<uint, int> assignedTargets,
        uint playerId,
        IReadOnlyDictionary<PressureKey, int> pressureMap)
    {
        var score = 0f;
        var enemyId = enemy.SimulatedEntityId;
        var assignedCount = assignedTargets.GetValueOrDefault(enemyId);
        var isSelectedTarget = selectedFocusTargetId != 0 && enemyId == selectedFocusTargetId;
        var isLastFocusTarget = lastFocusTargetId != 0 && enemyId == lastFocusTargetId;
        var isPlayerFocusTarget = isSelectedTarget || isLastFocusTarget;

        if (isSelectedTarget)
            score += SelectedTargetBonus;
        if (isLastFocusTarget && !isSelectedTarget)
            score += LastPlayerTargetBonus;
        if (GetEnemyTargetId(enemyId) == playerId)
            score += AttackingPlayerBonus;
        if (companion.CurrentTargetId == enemyId)
        {
            score += PreviousTargetBonus;
        }

        var hpPercent = enemy.State.MaxHp > 0
            ? (float)enemy.State.CurrentHp / enemy.State.MaxHp
            : 1f;
        score += (1f - Math.Clamp(hpPercent, 0f, 1f)) * LowHpFinishBonus;

        var focusFreeAssignments = isPlayerFocusTarget ? MaxPlayerFocusAssistants : 0;
        var penalizedAssignments = Math.Max(0, assignedCount - focusFreeAssignments);
        score -= penalizedAssignments * AssignmentPenalty;
        if (isPlayerFocusTarget && assignedCount >= MaxPlayerFocusAssistants)
            score -= (assignedCount - MaxPlayerFocusAssistants + 1) * PlayerFocusOvercapPenalty;

        score -= TargetPressurePenalty(
            pressureMap,
            enemyId,
            IsRangedStyle(companion.Behavior.AutoAttackStyle),
            CompanionMeleePressureCap,
            CompanionRangedPressureCap,
            isPlayerFocusTarget ? CompanionFocusPressureBonus : 0);
        score += TargetGraphScore(companion.SimulatedEntityId, enemyId);
        score += ReachabilityScore(companion, enemy, enemies);

        var distance = DistanceToPlayer(enemy);
        if (!float.IsInfinity(distance) && !float.IsNaN(distance))
            score -= distance * DistancePenaltyPerYalm;
        score += LordGuardScore(enemy);

        return score;
    }

    private float DistanceToPlayer(SimulatedNpc enemy)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || enemy.BattleChara == null)
            return float.MaxValue;

        var enemyPos = (Vector3)((GameObject*)enemy.BattleChara)->Position;
        return Vector3.Distance(player.Position, enemyPos);
    }

    private float ReachabilityScore(CombatCompanion companion, SimulatedNpc enemy, IReadOnlyList<SimulatedNpc> enemies)
    {
        var reachability = ComputeCompanionReachability(companion, enemy);
        if (reachability.Reachable)
            return 0f;

        return -ReachabilityHardPenalty - reachability.Overflow * ReachabilitySoftPenaltyWeight;
    }

    private static bool HasLiveEnemy(IReadOnlyList<SimulatedNpc> enemies, uint entityId)
    {
        for (var i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i];
            if (e.SimulatedEntityId == entityId && e.IsSpawned && e.State.IsAlive && e.BattleChara != null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether this ally can stand within reach of the enemy and still be inside its command range,
    /// and by how much it would overshoot.
    /// </summary>
    private (bool Reachable, float Overflow) ComputeCompanionReachability(
        CombatCompanion companion,
        SimulatedNpc enemy)
    {
        var player = objectTable.LocalPlayer;
        if (!GuardingLord || player == null || companion.BattleChara == null || enemy.BattleChara == null)
            return (true, 0f);

        var enemyPos = (Vector3)((GameObject*)enemy.BattleChara)->Position;
        var anchor = CommandAnchor(player.Position);
        var standOff = CrowdReachNow(companion) + HitboxOf((GameObject*)companion.BattleChara) + enemy.HitboxRadius;
        var overflow = FlatDistance(enemyPos, anchor) - standOff - CommandLimit(companion.SimulatedEntityId);
        return (overflow <= ReachabilityRecoveryDistance, MathF.Max(0f, overflow));
    }

    private void UpdateFocusTarget(IReadOnlyList<SimulatedNpc> enemies)
    {
        var selectedTargetId = ResolveSelectedEnemyTargetId(enemies);
        if (selectedTargetId == selectedFocusTargetId)
            return;

        lastFocusTargetId = selectedFocusTargetId;
        selectedFocusTargetId = selectedTargetId;
    }

    private uint ResolveSelectedEnemyTargetId(IReadOnlyList<SimulatedNpc> enemies)
    {
        var selected = targetManager.Target;
        if (selected == null)
            return 0;

        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;

            var obj = (GameObject*)enemy.BattleChara;
            if (selected.EntityId == enemy.SimulatedEntityId || selected.EntityId == obj->EntityId || selected.Address == (nint)obj)
                return enemy.SimulatedEntityId;
        }

        return 0;
    }

    /// <summary>
    /// Walk this ally toward a point with the crowd on the navigation mesh. Off the mesh (no mesh yet,
    /// or none near it) it stands where it is.
    /// </summary>
    private void MoveToward(CombatCompanion companion, Vector3 targetPos, float deltaTime)
    {
        if (TryCrowdMove(companion, targetPos, deltaTime))
            return;
        StopCompanionAtMoveTarget(companion, targetPos, deltaTime);
    }

    private void StopCompanionAtMoveTarget(CombatCompanion companion, Vector3 faceTarget, float deltaTime)
    {
        StopMove(companion);
        EnterCompanionState(
            companion,
            companion.CurrentTargetId == 0 ? CompanionAiState.Idle : CompanionAiState.CombatReady);
        RotateToward(companion, faceTarget, deltaTime);
    }

    private void TickCombatAnchor(IReadOnlyList<SimulatedNpc> enemies)
    {
        var player = objectTable.LocalPlayer;
        var hasCombat = player != null &&
                        companions.Any(c => c.IsSpawned && c.State.IsAlive) &&
                        enemies.Any(e => e.IsSpawned && e.State.IsAlive);
        if (!hasCombat)
        {
            combatAnchorActive = false;
            return;
        }

        if (!combatAnchorActive)
        {
            combatAnchorPosition = player!.Position;
            combatAnchorRotation = player.Rotation;
            combatAnchorActive = true;
            return;
        }

        var delta = player!.Position - combatAnchorPosition;
        delta.Y = 0;
        if (delta.Length() >= CombatAnchorRelocateDistance)
        {
            combatAnchorPosition = player.Position;
            combatAnchorRotation = player.Rotation;
        }
    }

    private static Vector3 CalculateFollowPosition(Vector3 playerPos, float playerRot, int index, int count)
    {
        var clampedCount = Math.Max(1, count);
        var row = index / 4;
        var slot = index % 4;
        var spacing = 1.6f;
        var lateral = (slot - Math.Min(3, clampedCount - 1) / 2.0f) * spacing;
        var backward = FollowDistance + row * 1.5f;

        var back = new Vector3(MathF.Sin(playerRot + MathF.PI), 0, MathF.Cos(playerRot + MathF.PI));
        var right = new Vector3(MathF.Sin(playerRot - MathF.PI / 2f), 0, MathF.Cos(playerRot - MathF.PI / 2f));
        return playerPos + back * backward + right * lateral;
    }

    private Vector3? SnapToTerrain(Vector3 point)
    {
        try
        {
            if (BGCollisionModule.RaycastMaterialFilter(
                    point + new Vector3(0, 10f, 0),
                    new Vector3(0, -1, 0),
                    out var hit,
                    80f))
            {
                return point with { Y = hit.Point.Y };
            }
        }
        catch (Exception ex)
        {
            log.Verbose($"Companion terrain snap failed: {ex.Message}");
        }

        return null;
    }

    private void StopMove(CombatCompanion companion)
    {
        // On the mesh it holds its place in the crowd (and is nudged aside by those passing).
        if (TryCrowdMove(companion, null, 0f))
            return;
        StopMoveAnim(companion);
    }

    private void StopMoveAnim(CombatCompanion companion)
    {
        if (companion.BattleChara != null)
            ActorVisualStateController.ClearMovement((Character*)companion.BattleChara, companion.VisualState);
    }

    private void CaptureCompanionEquipment(CombatCompanion companion)
    {
        if (companion.BattleChara == null)
            return;

        var character = (Character*)companion.BattleChara;
        companion.OriginalEquipment = new ulong[AppearanceEquipmentSlots.Length];
        for (var i = 0; i < AppearanceEquipmentSlots.Length; i++)
            companion.OriginalEquipment[i] = character->DrawData.Equipment(AppearanceEquipmentSlots[i]).Value;
    }

    private void ApplyCompanionDefeatAppearance(CombatCompanion companion)
    {
        if (!config.DevCompanionAppearanceVariant ||
            companion.IsCatalogNpc ||
            companion.BattleChara == null ||
            companion.EquipmentVariantApplied)
        {
            return;
        }

        if (companion.OriginalEquipment == null)
            CaptureCompanionEquipment(companion);

        var character = (Character*)companion.BattleChara;
        foreach (var slot in AppearanceEquipmentSlots)
            LoadCompanionEquipmentSlot(character, slot, 0);

        companion.EquipmentVariantApplied = true;
    }

    private void RestoreCompanionEquipment(CombatCompanion companion)
    {
        if (companion.BattleChara == null || companion.OriginalEquipment == null)
            return;

        var character = (Character*)companion.BattleChara;
        var count = Math.Min(AppearanceEquipmentSlots.Length, companion.OriginalEquipment.Length);
        for (var i = 0; i < count; i++)
            LoadCompanionEquipmentSlot(character, AppearanceEquipmentSlots[i], companion.OriginalEquipment[i]);
    }

    private static void LoadCompanionEquipmentSlot(Character* character, DrawDataContainer.EquipmentSlot slot, ulong value)
    {
        var model = new EquipmentModelId { Value = value };
        character->DrawData.Equipment(slot).Value = value;
        character->DrawData.LoadEquipment(slot, &model, true);
    }

    private void EnterCompanionState(CombatCompanion companion, CompanionAiState state, float deltaTime = 0)
    {
        companion.AiState = state;
        if (companion.BattleChara == null)
            return;

        var character = (Character*)companion.BattleChara;
        switch (state)
        {
            case CompanionAiState.ReturningToCommandRange:
            case CompanionAiState.MovingToTarget:
                ActorVisualStateController.ApplyMoving(character, companion.VisualState, deltaTime);
                break;
            case CompanionAiState.ActionLocked:
                ActorVisualStateController.ApplyActionLocked(character, companion.VisualState);
                break;
            case CompanionAiState.Dead:
                ActorVisualStateController.ApplyDead(character, companion.VisualState);
                break;
            default:
                ActorVisualStateController.ApplyCombatIdle(character, companion.VisualState);
                break;
        }
    }

    private void RotateToward(CombatCompanion companion, Vector3 targetPos, float deltaTime)
    {
        var obj = (GameObject*)companion.BattleChara;
        var pos = (Vector3)obj->Position;
        var dir = targetPos - pos;
        if (dir.LengthSquared() < 0.001f)
            return;

        var targetRot = MathF.Atan2(dir.X, dir.Z);
        var currentRot = obj->Rotation;
        var diff = targetRot - currentRot;
        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotStep = 6.0f * deltaTime;
        var nextRot = MathF.Abs(diff) < rotStep ? targetRot : currentRot + MathF.Sign(diff) * rotStep;
        movementBlockHook.SetApproachRotation(obj, nextRot);
    }

    private void TickRecentDps(CombatCompanion companion, float deltaTime)
    {
        if (companion.RecentDamage <= 0)
        {
            companion.RecentDps = 0;
            return;
        }

        var decayPerSecond = companion.RecentDamage / RecentDpsWindowSeconds;
        companion.RecentDamage = Math.Max(0, companion.RecentDamage - decayPerSecond * deltaTime);
        companion.RecentDps = companion.RecentDamage / RecentDpsWindowSeconds;
    }

    private void TickPlayerRecentDps(float deltaTime)
    {
        if (playerRecentDamage <= 0)
        {
            playerRecentDps = 0;
            return;
        }

        var decayPerSecond = playerRecentDamage / RecentDpsWindowSeconds;
        playerRecentDamage = Math.Max(0, playerRecentDamage - decayPerSecond * deltaTime);
        playerRecentDps = playerRecentDamage / RecentDpsWindowSeconds;
    }

    private void Despawn(CombatCompanion companion)
    {
        try
        {
            movementBlockHook.RemoveApproachNpc(companion.Address);
            // Only touch game memory while the session is alive — on game close Dispose runs after
            // the game freed these objects, so DisableDraw/DeleteObjectByIndex would crash.
            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr != null && companion.ObjectIndex >= 0 && Core.Services.ObjectTable.LocalPlayer != null)
            {
                if (companion.BattleChara != null)
                    ((GameObject*)companion.BattleChara)->DisableDraw();
                clientObjMgr->DeleteObjectByIndex((ushort)companion.ObjectIndex, 0);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to despawn companion '{companion.Name}'.");
        }

        combatEngine.UnregisterNpcEntity(companion.SimulatedEntityId);
        allocatedIndices.Remove(companion.ObjectIndex);
        companions.Remove(companion);
    }

    private Vector3 CalculateSpawnPosition(int index)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return Vector3.Zero;

        var angle = player.Rotation + MathF.PI + (index - 1) * 0.7f;
        var offset = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * 3.0f;
        return player.Position + offset;
    }

    private float CalculateSpawnRotation(Vector3 spawnPos)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return 0;
        var dir = player.Position - spawnPos;
        return MathF.Atan2(dir.X, dir.Z);
    }

    private static NpcBehavior CreateCompanionBehavior(NpcAttackStyle style)
    {
        var type = style == NpcAttackStyle.Ranged || style == NpcAttackStyle.Magic
            ? NpcBehaviorType.BasicRanged
            : NpcBehaviorType.BasicMelee;
        var behavior = NpcBehavior.Create(type);
        behavior.AutoAttackStyle = style == NpcAttackStyle.Auto ? behavior.AutoAttackStyle : style;
        if (style == NpcAttackStyle.Ranged || style == NpcAttackStyle.Magic)
            behavior.AutoAttackRange = 25.0f;
        else
            behavior.AutoAttackRange = 1.0f;
        return behavior;
    }

    private SimulatedNpc ToSimulatedNpcView(CombatCompanion companion) => new()
    {
        SimulatedEntityId = companion.SimulatedEntityId,
        ObjectIndex = companion.ObjectIndex,
        Name = companion.Name,
        BattleChara = companion.BattleChara,
        GameObjectRef = companion.GameObjectRef,
        State = companion.State,
        Behavior = companion.Behavior,
        IsSpawned = companion.IsSpawned,
        IsClientControlled = true,
        IsRanged = companion.IsRanged,
    };

    private static bool IsTankJob(uint classJobId)
        => classJobId is 19 or 21 or 32 or 37;

    public void Dispose() => DespawnAll();

    private class PendingSpawn
    {
        public CombatCompanion Companion { get; set; } = null!;
        public int FramesWaited { get; set; }
    }

    private readonly record struct CompanionSpawnSource(
        uint EntityId,
        nint Address,
        string Name,
        uint ClassJobId,
        bool RandomizeAppearance)
    {
        public static CompanionSpawnSource FromObject(IGameObject obj, bool randomizeAppearance = false)
        {
            var classJobId = obj is IPlayerCharacter player ? player.ClassJob.RowId : 0;
            return new CompanionSpawnSource(
                obj.EntityId,
                obj.Address,
                obj.Name.TextValue,
                classJobId,
                randomizeAppearance);
        }
    }

    private readonly record struct PressureKey(uint TargetId, bool Ranged);
}
