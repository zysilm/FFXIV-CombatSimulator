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

public unsafe class CombatCompanionManager : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly Configuration config;
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly ITargetManager targetManager;
    private readonly PartyEngagePlanner partyEngagePlanner;
    private readonly IPluginLog log;

    private readonly List<CombatCompanion> companions = new();
    private readonly List<PendingSpawn> pendingSpawns = new();
    private readonly ConcurrentQueue<CompanionSpawnSource> spawnQueue = new();
    private readonly HashSet<int> allocatedIndices = new();
    private readonly Dictionary<nint, PathState> pathStates = new();
    private readonly Dictionary<uint, uint> enemyTargetByEnemyId = new();
    private uint nextEntityId = 0xF1000001;
    private float playerRecentDamage;
    private float playerRecentDps;
    private uint lastPlayerTargetId;
    private float senseTimer;
    private bool combatAnchorActive;
    private Vector3 combatAnchorPosition;
    private float combatAnchorRotation;

    /// <summary>Hard cap on simultaneous companions. The game's object table runs
    /// out of slots before this in practice, which is handled gracefully at spawn.</summary>
    public const int MaxCompanionCap = 50;

    private const int MaxPendingFrames = 120;
    private const float TargetRange = 3.0f;
    private const float RepathInterval = 1.0f;
    private const float RepathDistance = 1.5f;
    private const float VNavmeshFloorResnapInterval = 0.25f;
    private const float VNavmeshWaypointReachDistance = 0.5f;
    private const float VNavmeshLookaheadDistance = 1.25f;
    private const float TerrainGridStep = 0.5f;
    private const int TerrainGridMaxSize = 33;
    private const float RecentDpsWindowSeconds = 8.0f;
    private const float RetargetDpsLead = 1.20f;
    private const float FollowDistance = 3.0f;
    private const float FollowStopDistance = 0.6f;
    private const float CombatAnchorRelocateDistance = 10.0f;
    private const float MeleeAttackRangeBuffer = 0.25f;
    private const float SenseInterval = 1.0f;
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
    public int PendingCount => pendingSpawns.Count + spawnQueue.Count;
    public bool HasActiveCompanions => config.EnableCombatCompanions && companions.Any(c => c.IsSpawned && c.State.IsAlive);
    public bool HasLivingCompanions => config.EnableCombatCompanions && companions.Any(c => c.IsSpawned && c.State.IsAlive);

    public Action<CombatCompanion>? OnCompanionSpawnComplete { get; set; }
    public Action<nint>? OnCompanionDeathRagdoll { get; set; }
    public Action<string>? OnSpawnError { get; set; }

    public CombatCompanionManager(
        IObjectTable objectTable,
        IClientState clientState,
        Configuration config,
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        VNavmeshIpc vnavmeshIpc,
        ITargetManager targetManager,
        PartyEngagePlanner partyEngagePlanner,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.config = config;
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.vnavmeshIpc = vnavmeshIpc;
        this.targetManager = targetManager;
        this.partyEngagePlanner = partyEngagePlanner;
        this.log = log;
    }

    public int SpawnFromVisiblePlayers()
    {
        if (!config.EnableCombatCompanions)
            return 0;

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
            if (player.Address == nint.Zero)
                continue;
            if (!seenSources.Add(player.EntityId))
                continue;

            spawnQueue.Enqueue(CompanionSpawnSource.FromObject(player));
            queued++;
        }

        return queued;
    }

    public bool SpawnSelfCharacter()
    {
        if (!config.EnableCombatCompanions)
            return false;

        var max = Math.Clamp(config.CombatCompanionMaxCount, 0, MaxCompanionCap);
        if (companions.Count + PendingCount >= max)
            return false;

        var player = objectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return false;

        // Intentionally no source-dedupe here: the player can be cloned many times
        // (each click adds one self-clone) up to the max count / available game slots.
        spawnQueue.Enqueue(CompanionSpawnSource.FromObject(player));
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

        vnavmeshIpc.RefreshStatus();
        var terrainCache = BuildCompanionTerrainCache(enemies);
        var assignedTargets = new Dictionary<uint, int>();
        TickCombatAnchor(enemies);

        var liveCompanions = companions.ToList();
        for (var i = 0; i < liveCompanions.Count; i++)
        {
            var companion = liveCompanions[i];
            TickRecentDps(companion, deltaTime);
            AssignCompanionTarget(companion, enemies, assignedTargets);
        }

        var enemyTargets = new Dictionary<uint, uint>();
        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive)
                continue;

            var target = SelectEnemyTarget(enemy);
            if (target != null)
                enemyTargets[enemy.SimulatedEntityId] = target.EntityId;
        }

        var companionTargets = liveCompanions
            .Where(c => c.IsSpawned && c.State.IsAlive && c.CurrentTargetId != 0)
            .ToDictionary(c => c.SimulatedEntityId, c => c.CurrentTargetId);

        var player = objectTable.LocalPlayer;
        if (player != null)
        {
            var commandAnchorPos = combatAnchorActive ? combatAnchorPosition : player.Position;
            var commandAnchorRot = combatAnchorActive ? combatAnchorRotation : player.Rotation;
            partyEngagePlanner.Build(
                deltaTime,
                player.Position,
                player.Rotation,
                commandAnchorPos,
                commandAnchorRot,
                liveCompanions,
                enemies,
                companionTargets,
                enemyTargets,
                combatEngine.State.PlayerState.EntityId,
                MathF.Max(1.0f, config.PartyCommandRange),
                Math.Clamp(config.PartyCommandRangeRandomness, 0.0f, 0.8f));
        }
        else
        {
            partyEngagePlanner.Clear();
        }

        for (var i = 0; i < liveCompanions.Count; i++)
            TickCompanion(liveCompanions[i], deltaTime, enemies, i, liveCompanions.Count, terrainCache);

        TickPlayerRecentDps(deltaTime);
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
        lastPlayerTargetId = targetId;
        RegisterPlayerDamage(damage);
    }

    public SimulatedEntityState? SelectEnemyTarget(SimulatedNpc enemy)
    {
        if (!config.EnableCombatCompanions)
            return combatEngine.State.PlayerState;

        var livingCompanions = companions
            .Where(c => c.IsSpawned && c.State.IsAlive)
            .ToList();
        var hasLivingCompanions = livingCompanions.Count > 0;
        if (!hasLivingCompanions)
            return combatEngine.State.PlayerState;

        var candidates = new List<(uint EntityId, SimulatedEntityState State, float Dps)>();
        if (combatEngine.State.PlayerState.IsAlive)
            candidates.Add((combatEngine.State.PlayerState.EntityId, combatEngine.State.PlayerState, playerRecentDps));

        foreach (var companion in livingCompanions)
            candidates.Add((companion.SimulatedEntityId, companion.State, companion.RecentDps));

        candidates.RemoveAll(c => !c.State.IsAlive);
        if (candidates.Count == 0)
            return null;

        var validTargetIds = candidates.Select(c => c.EntityId).ToHashSet();
        foreach (var kv in enemyTargetByEnemyId.ToList())
        {
            if (!validTargetIds.Contains(kv.Value))
                enemyTargetByEnemyId.Remove(kv.Key);
        }

        if (!enemyTargetByEnemyId.TryGetValue(enemy.SimulatedEntityId, out var currentId))
        {
            var best = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, 0, candidates);
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = best.EntityId;
            return best.State;
        }

        var current = candidates.FirstOrDefault(c => c.EntityId == currentId);
        if (current.State == null)
        {
            var best = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, 0, candidates);
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = best.EntityId;
            return best.State;
        }

        var balancedBest = SelectBalancedEnemyTarget(enemy.SimulatedEntityId, current.EntityId, candidates);
        var currentScore = ScoreEnemyTargetCandidate(enemy.SimulatedEntityId, current.EntityId, current);
        var bestScore = ScoreEnemyTargetCandidate(enemy.SimulatedEntityId, current.EntityId, balancedBest);
        if (balancedBest.EntityId != current.EntityId && bestScore > currentScore + EnemyTargetSwitchThreshold)
        {
            enemyTargetByEnemyId[enemy.SimulatedEntityId] = balancedBest.EntityId;
            return balancedBest.State;
        }

        return current.State;
    }

    private (uint EntityId, SimulatedEntityState State, float Dps) SelectBalancedEnemyTarget(
        uint enemyId,
        uint currentId,
        IReadOnlyList<(uint EntityId, SimulatedEntityState State, float Dps)> candidates)
        => candidates
            .OrderByDescending(c => ScoreEnemyTargetCandidate(enemyId, currentId, c))
            .ThenBy(c => StableTargetTieBreak(enemyId, c.EntityId))
            .First();

    private float ScoreEnemyTargetCandidate(
        uint enemyId,
        uint currentId,
        (uint EntityId, SimulatedEntityState State, float Dps) candidate)
    {
        var assignedCount = enemyTargetByEnemyId
            .Where(kv => kv.Key != enemyId)
            .Count(kv => kv.Value == candidate.EntityId);

        var score = candidate.Dps * EnemyTargetDpsWeight;
        score -= assignedCount * EnemyTargetAssignmentPenalty;
        if (candidate.EntityId == currentId)
            score += EnemyTargetCurrentBonus;
        return score;
    }

    private static uint StableTargetTieBreak(uint enemyId, uint targetId)
        => (enemyId * 1103515245u + targetId * 2654435761u) & 0xFFFFu;

    public CombatCompanion? GetCompanion(uint entityId)
        => companions.FirstOrDefault(c => c.SimulatedEntityId == entityId);

    public nint? ResolveAddress(uint entityId)
        => GetCompanion(entityId)?.Address;

    public uint GetEnemyTargetId(uint enemyId)
        => enemyTargetByEnemyId.GetValueOrDefault(enemyId);

    /// <summary>
    /// Combat reset while keeping companions: revive + heal each one and clear its
    /// combat state so it is battle-ready again, without despawning/re-cloning.
    /// </summary>
    public void ResetForCombatReset()
    {
        enemyTargetByEnemyId.Clear();
        playerRecentDamage = 0;
        playerRecentDps = 0;

        foreach (var companion in companions)
        {
            companion.State.CurrentHp = companion.State.MaxHp;
            companion.State.AnimationLock = 0;
            companion.DeathAnimationPlayed = false;
            companion.EquipmentVariantApplied = false;
            companion.AutoAttackTimer = 0;
            companion.CurrentTargetId = 0;
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
        pathStates.Clear();
        enemyTargetByEnemyId.Clear();
        playerRecentDamage = 0;
        playerRecentDps = 0;
        combatAnchorActive = false;
    }

    private void TickSensing(float deltaTime)
    {
        if (!config.SensePartyMembers)
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

            var hint = FindFreeObjectHint();
            var createResult = clientObjMgr->CreateBattleCharacter(hint);
            if (createResult == 0xFFFFFFFF)
            {
                OnSpawnError?.Invoke("CreateBattleCharacter failed - no available slot.");
                return;
            }

            var index = (int)createResult;
            allocatedIndices.Add(index);
            var obj = clientObjMgr->GetObjectByIndex((ushort)index);
            if (obj == null)
            {
                allocatedIndices.Remove(index);
                OnSpawnError?.Invoke($"Object null at index {index} after creation.");
                return;
            }

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
            character->SetMode(CharacterModes.Normal, 0);

            IGameObject? gameObjectRef = null;
            try { gameObjectRef = objectTable.CreateObjectReference((nint)obj); }
            catch (Exception ex) { log.Warning(ex, "CreateObjectReference failed for companion clone."); }

            var entityId = nextEntityId++;
            obj->EntityId = entityId;

            var level = Math.Clamp(config.CombatCompanionLevelOverride, 1, 300);
            var maxHp = CalculateCompanionHp(level);
            var classJobId = sourceInfo.ClassJobId;
            var weaponStyle = NpcWeaponClassifier.DetectFromCharacter(character, log, name);
            var behavior = CreateCompanionBehavior(weaponStyle);

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
                IsSpawned = false,
                AutoAttackTimer = Random.Shared.NextSingle() * behavior.AutoAttackDelay,
                State = new SimulatedEntityState
                {
                    EntityId = entityId,
                    Name = name,
                    IsPlayer = false,
                    IsCompanion = true,
                    Level = level,
                    MaxHp = maxHp,
                    CurrentHp = maxHp,
                    MaxMp = 10000,
                    CurrentMp = 10000,
                    ClassJobId = classJobId,
                    IsTank = IsTankJob(classJobId),
                    MainStat = 100 + level * 10,
                    AttackPower = 100 + level * 10,
                    AttackMagicPotency = 100 + level * 10,
                    Determination = 100 + level * 8,
                    CriticalHit = 100 + level * 8,
                    DirectHit = 100 + level * 8,
                    Defense = 100 + level * 8,
                    MagicDefense = 100 + level * 8,
                    Tenacity = 100 + level * 4,
                    SkillSpeed = 100 + level * 6,
                    SpellSpeed = 100 + level * 6,
                    WeaponDamage = Math.Max(1, 20 + level),
                    MagicWeaponDamage = Math.Max(1, 20 + level),
                    WeaponDelayMs = 3000,
                    DamageTraitPct = 100,
                },
            };
            CaptureCompanionEquipment(companion);

            pendingSpawns.Add(new PendingSpawn { Companion = companion });
            log.Info($"Companion clone '{name}' created at index {index}, entityId={entityId:X}. Pending draw...");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Exception in companion spawn.");
            OnSpawnError?.Invoke($"Companion spawn failed: {ex.Message}");
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
        int companionCount,
        CompanionTerrainCache? terrainCache)
    {
        if (!companion.IsSpawned || companion.BattleChara == null)
            return;

        if (!companion.State.IsAlive)
        {
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.Dead);
            if (!companion.DeathAnimationPlayed)
            {
                ApplyCompanionDefeatAppearance(companion);
                animationController.PlayDeathAnimation(ToSimulatedNpcView(companion));
                if (config.PartyCompanionDeathRagdoll && companion.Address != nint.Zero)
                    OnCompanionDeathRagdoll?.Invoke(companion.Address);
                companion.DeathAnimationPlayed = true;
                combatEngine.TriggerEnemyVictoryIfPartyDefeated();
            }
            return;
        }

        if (!IsWithinCommandRange(companion))
        {
            companion.CurrentTargetId = 0;
            EnterCompanionState(companion, CompanionAiState.ReturningToCommandRange, deltaTime);
            MoveToCommandRange(companion, deltaTime, companionIndex, companionCount, terrainCache);
            return;
        }

        var target = GetCurrentCompanionTarget(companion, enemies);
        if (target == null || target.BattleChara == null)
        {
            companion.CurrentTargetId = 0;
            EnterCompanionState(companion, CompanionAiState.ReturningToCommandRange, deltaTime);
            MoveToCommandRange(companion, deltaTime, companionIndex, companionCount, terrainCache);
            return;
        }

        var targetObj = (GameObject*)target.BattleChara;
        var sourceObj = (GameObject*)companion.BattleChara;
        var targetPos = (Vector3)targetObj->Position;
        var sourcePos = (Vector3)sourceObj->Position;
        var dist = Vector3.Distance(sourcePos, targetPos);
        var effectiveRange = GetPartyAttackRange(companion.Behavior.AutoAttackStyle) + MeleeAttackRangeBuffer;

        if (dist > effectiveRange)
        {
            MoveByPartyPlan(companion, deltaTime, targetPos, terrainCache);
            return;
        }

        StopMove(companion);
        EnterCompanionState(companion, CompanionAiState.CombatReady);
        RotateToward(companion, targetPos, deltaTime);

        if (companion.State.AnimationLock > 0)
        {
            EnterCompanionState(companion, CompanionAiState.ActionLocked);
            companion.State.AnimationLock = Math.Max(0, companion.State.AnimationLock - deltaTime);
            return;
        }

        foreach (var skill in companion.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (skill.CooldownRemaining > 0)
            {
                skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - deltaTime);
                continue;
            }
            if (dist > GetPartySkillRange(skill.Range, skill.AttackStyle))
                continue;

            var result = combatEngine.ProcessCompanionAction(companion, target, skill.ActionId, skill.Potency, skill.AttackStyle);
            if (result.Success)
                RegisterDamage(companion.SimulatedEntityId, result.Damage);

            skill.CooldownRemaining = skill.Cooldown;
            companion.State.AnimationLock = 0.6f;
            return;
        }

        companion.AutoAttackTimer -= deltaTime;
        if (companion.AutoAttackTimer <= 0)
        {
            companion.AutoAttackTimer = companion.Behavior.AutoAttackDelay;
            var result = combatEngine.ProcessCompanionAction(
                companion, target,
                companion.Behavior.AutoAttackActionId,
                companion.Behavior.AutoAttackPotency,
                companion.Behavior.AutoAttackStyle);
            if (result.Success)
                RegisterDamage(companion.SimulatedEntityId, result.Damage);
            companion.State.AnimationLock = 0.6f;
        }
    }

    private void AssignCompanionTarget(
        CombatCompanion companion,
        IReadOnlyList<SimulatedNpc> enemies,
        Dictionary<uint, int> assignedTargets)
    {
        if (!companion.IsSpawned || !companion.State.IsAlive || companion.BattleChara == null)
            return;

        if (!IsWithinCommandRange(companion))
        {
            companion.CurrentTargetId = 0;
            return;
        }

        var target = SelectCompanionTarget(companion, enemies, assignedTargets);
        companion.CurrentTargetId = target?.SimulatedEntityId ?? 0;
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
        var commandAnchor = combatAnchorActive ? combatAnchorPosition : player.Position;
        var delta = current - commandAnchor;
        delta.Y = 0;
        var limit = partyEngagePlanner.GetCommandLimit(
            companion.SimulatedEntityId,
            MathF.Max(1.0f, config.PartyCommandRange),
            Math.Clamp(config.PartyCommandRangeRandomness, 0.0f, 0.8f));
        return delta.Length() <= limit;
    }

    private void MoveToCommandRange(
        CombatCompanion companion,
        float deltaTime,
        int companionIndex,
        int companionCount,
        CompanionTerrainCache? terrainCache)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || companion.BattleChara == null)
            return;

        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var plan = partyEngagePlanner.BuildReturnPlan(
            companion.SimulatedEntityId,
            current,
            combatAnchorActive ? combatAnchorPosition : player.Position,
            combatAnchorActive ? combatAnchorRotation : player.Rotation,
            companionIndex,
            companionCount,
            MathF.Max(1.0f, config.PartyCommandRange),
            Math.Clamp(config.PartyCommandRangeRandomness, 0.0f, 0.8f));

        MoveByPlan(companion, plan, deltaTime, terrainCache);
    }

    private void MoveByPartyPlan(
        CombatCompanion companion,
        float deltaTime,
        Vector3 fallbackFaceTarget,
        CompanionTerrainCache? terrainCache)
    {
        if (!partyEngagePlanner.TryGetPlan(companion.SimulatedEntityId, out var plan))
        {
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.CombatReady);
            return;
        }

        MoveByPlan(companion, plan, deltaTime, terrainCache);
    }

    private void MoveByPlan(
        CombatCompanion companion,
        PartyEngagePlan plan,
        float deltaTime,
        CompanionTerrainCache? terrainCache)
    {
        if (companion.BattleChara == null)
            return;

        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var distance = FlatDistance(current, plan.Goal);
        if (distance > FollowStopDistance)
        {
            MoveToward(companion, plan.Goal, deltaTime, terrainCache);
            return;
        }

        StopMove(companion);
        EnterCompanionState(
            companion,
            companion.CurrentTargetId == 0 ? CompanionAiState.Idle : CompanionAiState.CombatReady);
        if (plan.HasFaceTarget)
            RotateToward(companion, plan.FaceTarget, deltaTime);
    }

    private float GetPartyAttackRange(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic
            ? MathF.Max(1.0f, config.PartyRangedAttackRange)
            : MathF.Max(0.5f, config.PartyMeleeAttackRange);

    private float GetPartySkillRange(float skillRange, NpcAttackStyle style)
        => MathF.Min(skillRange, GetPartyAttackRange(style) + MeleeAttackRangeBuffer);

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    private SimulatedNpc? SelectCompanionTarget(
        CombatCompanion companion,
        IReadOnlyList<SimulatedNpc> enemies,
        Dictionary<uint, int> assignedTargets)
    {
        var aliveEnemies = enemies
            .Where(e => e.IsSpawned && e.State.IsAlive && e.BattleChara != null)
            .ToList();
        if (aliveEnemies.Count == 0)
            return null;

        var selectedTargetId = targetManager.Target?.EntityId ?? 0;
        var playerId = combatEngine.State.PlayerState.EntityId;
        var current = companion.CurrentTargetId == 0
            ? null
            : aliveEnemies.FirstOrDefault(e => e.SimulatedEntityId == companion.CurrentTargetId);

        var scoredTargets = aliveEnemies
            .Select(enemy => (Enemy: enemy, Score: ScoreCompanionTarget(
                companion, enemy, assignedTargets, selectedTargetId, playerId)))
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
        IReadOnlyDictionary<uint, int> assignedTargets,
        uint selectedTargetId,
        uint playerId)
    {
        var score = 0f;
        var enemyId = enemy.SimulatedEntityId;
        var assignedCount = assignedTargets.GetValueOrDefault(enemyId);
        var isSelectedTarget = selectedTargetId != 0 && enemyId == selectedTargetId;
        var isLastPlayerTarget = lastPlayerTargetId != 0 && enemyId == lastPlayerTargetId;
        var isPlayerFocusTarget = isSelectedTarget || isLastPlayerTarget;

        if (isSelectedTarget)
            score += SelectedTargetBonus;
        if (isLastPlayerTarget && !isSelectedTarget)
            score += LastPlayerTargetBonus;
        if (GetEnemyTargetId(enemyId) == playerId)
            score += AttackingPlayerBonus;
        if (companion.CurrentTargetId == enemyId)
            score += PreviousTargetBonus;

        var hpPercent = enemy.State.MaxHp > 0
            ? (float)enemy.State.CurrentHp / enemy.State.MaxHp
            : 1f;
        score += (1f - Math.Clamp(hpPercent, 0f, 1f)) * LowHpFinishBonus;

        var focusFreeAssignments = isPlayerFocusTarget ? MaxPlayerFocusAssistants : 0;
        var penalizedAssignments = Math.Max(0, assignedCount - focusFreeAssignments);
        score -= penalizedAssignments * AssignmentPenalty;
        if (isPlayerFocusTarget && assignedCount >= MaxPlayerFocusAssistants)
            score -= (assignedCount - MaxPlayerFocusAssistants + 1) * PlayerFocusOvercapPenalty;

        var distance = DistanceToPlayer(enemy);
        if (!float.IsInfinity(distance) && !float.IsNaN(distance))
            score -= distance * DistancePenaltyPerYalm;

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

    private void MoveToward(
        CombatCompanion companion,
        Vector3 targetPos,
        float deltaTime,
        CompanionTerrainCache? terrainCache)
    {
        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var moveTarget = GetPathMoveTarget(companion, current, targetPos, deltaTime);
        var dir = moveTarget - current;
        dir.Y = 0;
        if (dir.LengthSquared() < 0.01f)
        {
            StopMove(companion);
            EnterCompanionState(
                companion,
                companion.CurrentTargetId == 0 ? CompanionAiState.Idle : CompanionAiState.CombatReady);
            return;
        }

        dir = Vector3.Normalize(dir);
        var speed = companion.Behavior.MoveSpeed > 0 ? companion.Behavior.MoveSpeed : 6f;
        var remainingDist = Vector3.Distance(new Vector3(current.X, 0, current.Z), new Vector3(moveTarget.X, 0, moveTarget.Z));
        var moveDist = speed * deltaTime;
        var next = remainingDist <= moveDist
            ? moveTarget
            : current + dir * moveDist;

        RotateToward(companion, moveTarget, deltaTime);
        if (terrainCache != null && pathStates.TryGetValue(companion.Address, out var terrainState))
        {
            next = CorrectMovingRootHeight(next, terrainCache, terrainState, deltaTime);
        }
        else if (vnavmeshIpc.CanPathfind)
        {
            var floor = vnavmeshIpc.PointOnFloor(next, false, 3f);
            if (floor.HasValue)
                next.Y = floor.Value.Y;
        }
        else
        {
            next.Y = targetPos.Y;
        }

        movementBlockHook.AddApproachNpc(companion.Address);
        EnterCompanionState(
            companion,
            companion.CurrentTargetId == 0 ? CompanionAiState.ReturningToCommandRange : CompanionAiState.MovingToTarget,
            deltaTime);
        movementBlockHook.SetApproachPosition(obj, next.X, next.Y, next.Z);
    }

    private void FollowPlayer(
        CombatCompanion companion,
        float deltaTime,
        int companionIndex,
        int companionCount,
        CompanionTerrainCache? terrainCache)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || companion.BattleChara == null)
        {
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.Idle);
            return;
        }

        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var target = CalculateFollowPosition(player.Position, player.Rotation, companionIndex, companionCount);
        var distance = Vector3.Distance(current, target);
        if (distance <= FollowStopDistance)
        {
            if (terrainCache != null && pathStates.TryGetValue(companion.Address, out var stableState))
                CorrectStableRootHeight(obj, current, terrainCache, stableState, deltaTime);
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.Idle);
            RotateToward(companion, player.Position, deltaTime);
            return;
        }

        MoveToward(companion, target, deltaTime, terrainCache);
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

    private void MoveToCombatFormation(
        CombatCompanion companion,
        float deltaTime,
        int companionIndex,
        int companionCount,
        Vector3 facePos,
        CompanionTerrainCache? terrainCache)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || companion.BattleChara == null)
        {
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.Idle);
            return;
        }

        var obj = (GameObject*)companion.BattleChara;
        var current = (Vector3)obj->Position;
        var anchorPos = combatAnchorActive ? combatAnchorPosition : player.Position;
        var anchorRot = combatAnchorActive ? combatAnchorRotation : player.Rotation;
        var target = CalculateFollowPosition(anchorPos, anchorRot, companionIndex, companionCount);
        var distance = Vector3.Distance(current, target);
        if (distance <= FollowStopDistance)
        {
            if (terrainCache != null && pathStates.TryGetValue(companion.Address, out var stableState))
                CorrectStableRootHeight(obj, current, terrainCache, stableState, deltaTime);
            StopMove(companion);
            EnterCompanionState(companion, CompanionAiState.CombatReady);
            RotateToward(companion, facePos, deltaTime);
            return;
        }

        MoveToward(companion, target, deltaTime, terrainCache);
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

    private Vector3 GetPathMoveTarget(CombatCompanion companion, Vector3 current, Vector3 target, float deltaTime)
    {
        if (!vnavmeshIpc.CanPathfind)
            return target;

        if (!pathStates.TryGetValue(companion.Address, out var state))
        {
            state = new PathState();
            pathStates[companion.Address] = state;
        }

        state.RepathTimer = Math.Max(0, state.RepathTimer - deltaTime);
        if (state.PendingPath != null && state.PendingPath.IsCompleted)
        {
            try
            {
                state.Waypoints = state.PendingPath.GetAwaiter().GetResult();
                state.RequestedTarget = state.PendingTarget;
                if (!state.HasFloorYOffset)
                {
                    var floor = SnapToNavmesh(current);
                    state.FloorYOffset = floor.HasValue ? current.Y - floor.Value.Y : 0f;
                    state.HasFloorYOffset = true;
                    state.HasLastFloorY = false;
                    state.LastCorrectedWaypointIndex = -1;
                    state.FloorResnapTimer = 0;
                }
                state.WaypointIndex = SelectInitialWaypoint(state, current);
                state.LastCorrectedWaypointIndex = -1;
                state.HasLastMoveRootY = false;
            }
            catch (Exception ex)
            {
                log.Verbose($"Companion path failed: {ex.Message}");
                state.Waypoints.Clear();
            }
            state.PendingPath = null;
        }

        var shouldRepath = state.PendingPath == null &&
            (state.Waypoints.Count == 0 ||
             state.RepathTimer <= 0 ||
             Vector3.Distance(state.RequestedTarget, target) > RepathDistance);

        if (shouldRepath)
        {
            state.RepathTimer = RepathInterval;
            var from = SnapToNavmesh(current) ?? current;
            var to = SnapToNavmesh(target) ?? target;
            state.PendingTarget = to;
            state.PendingPath = vnavmeshIpc.Pathfind(from, to, 0.75f);
        }

        while (state.WaypointIndex < state.Waypoints.Count &&
               Vector3.Distance(current, ApplyFloorYOffset(state, state.Waypoints[state.WaypointIndex])) < VNavmeshWaypointReachDistance)
            state.WaypointIndex++;

        Vector3 moveTarget;
        if (state.WaypointIndex < state.Waypoints.Count)
        {
            var lookaheadIndex = SelectLookaheadWaypoint(state, current);
            state.WaypointIndex = lookaheadIndex;
            moveTarget = ApplyFloorYOffset(state, state.Waypoints[lookaheadIndex]);
        }
        else
        {
            moveTarget = target;
        }

        return CorrectMoveTargetFloor(state, moveTarget, deltaTime);
    }

    private static int SelectInitialWaypoint(PathState state, Vector3 current)
    {
        if (state.Waypoints.Count == 0)
            return 0;

        if (state.Waypoints.Count == 1)
            return FlatDistanceSquared(current, ApplyFloorYOffset(state, state.Waypoints[0])) <=
                   VNavmeshWaypointReachDistance * VNavmeshWaypointReachDistance
                ? 1
                : 0;

        var bestSegmentEnd = 1;
        var bestDistSq = float.MaxValue;
        var foundSegment = false;

        for (var i = 0; i < state.Waypoints.Count - 1; i++)
        {
            var a = ApplyFloorYOffset(state, state.Waypoints[i]);
            var b = ApplyFloorYOffset(state, state.Waypoints[i + 1]);
            a.Y = 0;
            b.Y = 0;
            var c = current;
            c.Y = 0;

            var ab = b - a;
            var lenSq = ab.LengthSquared();
            if (lenSq <= 0.0001f)
                continue;

            var t = Math.Clamp(Vector3.Dot(c - a, ab) / lenSq, 0f, 1f);
            var closest = a + ab * t;
            var distSq = Vector3.DistanceSquared(c, closest);
            if (distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            bestSegmentEnd = i + 1;
            foundSegment = true;
        }

        if (!foundSegment)
            return 0;

        while (bestSegmentEnd < state.Waypoints.Count &&
               FlatDistanceSquared(current, ApplyFloorYOffset(state, state.Waypoints[bestSegmentEnd])) <=
               VNavmeshWaypointReachDistance * VNavmeshWaypointReachDistance)
        {
            bestSegmentEnd++;
        }

        return bestSegmentEnd;
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.LengthSquared();
    }

    private static int SelectLookaheadWaypoint(PathState state, Vector3 current)
    {
        var selected = state.WaypointIndex;
        var accumulated = 0f;
        var previous = current;

        for (var i = state.WaypointIndex; i < state.Waypoints.Count; i++)
        {
            var point = ApplyFloorYOffset(state, state.Waypoints[i]);
            accumulated += Vector3.Distance(previous, point);
            selected = i;
            if (accumulated >= VNavmeshLookaheadDistance)
                break;
            previous = point;
        }

        return selected;
    }

    private static Vector3 ApplyFloorYOffset(PathState state, Vector3 point)
        => state.HasFloorYOffset ? point with { Y = point.Y + state.FloorYOffset } : point;

    private Vector3 CorrectMoveTargetFloor(PathState state, Vector3 moveTarget, float deltaTime)
    {
        if (!state.HasFloorYOffset)
            return moveTarget;

        state.FloorResnapTimer = Math.Max(0, state.FloorResnapTimer - deltaTime);
        var waypointChanged = state.LastCorrectedWaypointIndex != state.WaypointIndex;
        if (waypointChanged || !state.HasLastFloorY || state.FloorResnapTimer <= 0)
        {
            var floor = SnapToNavmesh(moveTarget);
            if (floor.HasValue)
            {
                state.LastFloorY = floor.Value.Y;
                state.HasLastFloorY = true;
                state.LastCorrectedWaypointIndex = state.WaypointIndex;
                state.FloorResnapTimer = VNavmeshFloorResnapInterval;
            }
        }

        return state.HasLastFloorY
            ? moveTarget with { Y = state.LastFloorY + state.FloorYOffset }
            : moveTarget;
    }

    private Vector3? SnapToNavmesh(Vector3 point)
    {
        try
        {
            return vnavmeshIpc.NearestPointReachable(point)
                   ?? vnavmeshIpc.PointOnFloor(point + new Vector3(0, 10f, 0));
        }
        catch (Exception ex)
        {
            log.Verbose($"Companion vnavmesh snap failed: {ex.Message}");
            return null;
        }
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

        return SnapToNavmesh(point);
    }

    private Vector3 CorrectMovingRootHeight(
        Vector3 rootPosition,
        CompanionTerrainCache terrainCache,
        PathState state,
        float deltaTime)
    {
        if (!terrainCache.TrySample(rootPosition.X, rootPosition.Z, out var terrainY))
            return rootPosition;

        if (!state.HasStableRootTerrainClearance)
        {
            state.StableRootTerrainClearance = 0f;
            state.HasStableRootTerrainClearance = true;
        }

        var desiredY = terrainY + state.StableRootTerrainClearance;
        var fromY = state.HasLastMoveRootY ? state.LastMoveRootY : rootPosition.Y;
        var maxStep = MathF.Max(0.03f, 6.0f * deltaTime);
        var deltaY = Math.Clamp(desiredY - fromY, -maxStep, maxStep);
        var y = fromY + deltaY;

        state.LastMoveRootY = y;
        state.HasLastMoveRootY = true;
        return rootPosition with { Y = y };
    }

    private void CorrectStableRootHeight(
        GameObject* gameObj,
        Vector3 rootPosition,
        CompanionTerrainCache terrainCache,
        PathState state,
        float deltaTime)
    {
        var corrected = CorrectMovingRootHeight(rootPosition, terrainCache, state, deltaTime);
        if (MathF.Abs(corrected.Y - rootPosition.Y) < 0.001f)
            return;

        movementBlockHook.SetApproachPosition(gameObj, corrected.X, corrected.Y, corrected.Z);
    }

    private void StopMove(CombatCompanion companion)
    {
        movementBlockHook.RemoveApproachNpc(companion.Address);
        StopMoveAnim(companion);
        pathStates.Remove(companion.Address);
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
            companion.BattleChara == null ||
            companion.EquipmentVariantApplied)
        {
            return;
        }

        if (companion.OriginalEquipment == null)
            CaptureCompanionEquipment(companion);

        var character = (Character*)companion.BattleChara;
        foreach (var slot in AppearanceEquipmentSlots)
            character->DrawData.Equipment(slot).Value = 0;

        companion.EquipmentVariantApplied = true;
    }

    private void RestoreCompanionEquipment(CombatCompanion companion)
    {
        if (companion.BattleChara == null || companion.OriginalEquipment == null)
            return;

        var character = (Character*)companion.BattleChara;
        var count = Math.Min(AppearanceEquipmentSlots.Length, companion.OriginalEquipment.Length);
        for (var i = 0; i < count; i++)
            character->DrawData.Equipment(AppearanceEquipmentSlots[i]).Value = companion.OriginalEquipment[i];
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

    private CompanionTerrainCache? BuildCompanionTerrainCache(IReadOnlyList<SimulatedNpc> enemies)
    {
        if (companions.Count == 0)
            return null;

        var player = objectTable.LocalPlayer;
        var hasBounds = false;
        var minX = 0f;
        var maxX = 0f;
        var minZ = 0f;
        var maxZ = 0f;
        var maxY = 0f;

        void Include(Vector3 pos, float pad)
        {
            if (!hasBounds)
            {
                minX = pos.X - pad;
                maxX = pos.X + pad;
                minZ = pos.Z - pad;
                maxZ = pos.Z + pad;
                maxY = pos.Y;
                hasBounds = true;
                return;
            }

            minX = MathF.Min(minX, pos.X - pad);
            maxX = MathF.Max(maxX, pos.X + pad);
            minZ = MathF.Min(minZ, pos.Z - pad);
            maxZ = MathF.Max(maxZ, pos.Z + pad);
            maxY = MathF.Max(maxY, pos.Y);
        }

        if (player != null)
            Include(player.Position, FollowDistance + 2f);

        foreach (var companion in companions)
        {
            if (companion.BattleChara == null)
                continue;
            Include((Vector3)((GameObject*)companion.BattleChara)->Position, 2f);
        }

        foreach (var enemy in enemies)
        {
            if (enemy.BattleChara == null || !enemy.State.IsAlive)
                continue;
            Include((Vector3)((GameObject*)enemy.BattleChara)->Position, TargetRange + 2f);
        }

        if (!hasBounds)
            return null;

        var widthWorld = MathF.Max(maxX - minX, TerrainGridStep);
        var depthWorld = MathF.Max(maxZ - minZ, TerrainGridStep);
        var step = MathF.Max(TerrainGridStep,
            MathF.Max(widthWorld, depthWorld) / MathF.Max(1, TerrainGridMaxSize - 1));
        var width = Math.Clamp((int)MathF.Ceiling(widthWorld / step) + 1, 2, TerrainGridMaxSize);
        var depth = Math.Clamp((int)MathF.Ceiling(depthWorld / step) + 1, 2, TerrainGridMaxSize);

        var heights = new float[width, depth];
        var valid = new bool[width, depth];
        var originY = maxY + 10f;
        const float rayDistance = 80f;

        for (var z = 0; z < depth; z++)
        for (var x = 0; x < width; x++)
        {
            var wx = minX + x * step;
            var wz = minZ + z * step;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(wx, originY, wz),
                    new Vector3(0, -1, 0),
                    out var hit,
                    rayDistance))
            {
                heights[x, z] = hit.Point.Y;
                valid[x, z] = true;
            }
        }

        return new CompanionTerrainCache
        {
            OriginX = minX,
            OriginZ = minZ,
            Step = step,
            Width = width,
            Depth = depth,
            Heights = heights,
            Valid = valid,
        };
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
            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr != null && companion.ObjectIndex >= 0)
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

    private uint FindFreeObjectHint()
    {
        uint hint = 100;
        while (hint < 200 && allocatedIndices.Contains((int)hint))
            hint++;
        return hint;
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

    private static int CalculateCompanionHp(int level)
        => level <= 50 ? 2000 + level * 500 :
           level <= 80 ? 30000 + level * 2000 :
           level <= 90 ? 80000 + level * 3000 :
           150000 + level * 5000;

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
        uint ClassJobId)
    {
        public static CompanionSpawnSource FromObject(IGameObject obj)
        {
            var classJobId = obj is IPlayerCharacter player ? player.ClassJob.RowId : 0;
            return new CompanionSpawnSource(
                obj.EntityId,
                obj.Address,
                obj.Name.TextValue,
                classJobId);
        }
    }

    private class PathState
    {
        public List<Vector3> Waypoints { get; set; } = new();
        public int WaypointIndex { get; set; }
        public Vector3 RequestedTarget { get; set; }
        public Vector3 PendingTarget { get; set; }
        public float RepathTimer { get; set; }
        public Task<List<Vector3>>? PendingPath { get; set; }
        public float FloorYOffset { get; set; }
        public bool HasFloorYOffset { get; set; }
        public float FloorResnapTimer { get; set; }
        public float LastFloorY { get; set; }
        public bool HasLastFloorY { get; set; }
        public int LastCorrectedWaypointIndex { get; set; } = -1;
        public float StableRootTerrainClearance { get; set; }
        public bool HasStableRootTerrainClearance { get; set; }
        public float LastMoveRootY { get; set; }
        public bool HasLastMoveRootY { get; set; }
    }

    private sealed class CompanionTerrainCache
    {
        public float OriginX { get; init; }
        public float OriginZ { get; init; }
        public float Step { get; init; }
        public int Width { get; init; }
        public int Depth { get; init; }
        public float[,] Heights { get; init; } = new float[0, 0];
        public bool[,] Valid { get; init; } = new bool[0, 0];

        public bool TrySample(float x, float z, out float y)
        {
            y = 0;
            if (Width <= 0 || Depth <= 0 || Step <= 0)
                return false;

            var gx = (x - OriginX) / Step;
            var gz = (z - OriginZ) / Step;
            var ix = (int)MathF.Floor(gx);
            var iz = (int)MathF.Floor(gz);

            if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth)
                return false;

            if (ix < Width - 1 && iz < Depth - 1)
            {
                var tx = gx - ix;
                var tz = gz - iz;
                if (Valid[ix, iz] && Valid[ix + 1, iz] && Valid[ix, iz + 1] && Valid[ix + 1, iz + 1])
                {
                    var y0 = Lerp(Heights[ix, iz], Heights[ix + 1, iz], tx);
                    var y1 = Lerp(Heights[ix, iz + 1], Heights[ix + 1, iz + 1], tx);
                    y = Lerp(y0, y1, tz);
                    return true;
                }
            }

            var bestDistSq = float.MaxValue;
            var bestY = 0f;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = ix + dx;
                var sz = iz + dz;
                if (sx < 0 || sz < 0 || sx >= Width || sz >= Depth || !Valid[sx, sz])
                    continue;

                var wx = OriginX + sx * Step;
                var wz = OriginZ + sz * Step;
                var distSq = (wx - x) * (wx - x) + (wz - z) * (wz - z);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestY = Heights[sx, sz];
                }
            }

            if (bestDistSq == float.MaxValue)
                return false;

            y = bestY;
            return true;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
