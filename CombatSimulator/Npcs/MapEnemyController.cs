using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Companions;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

public sealed unsafe class MapEnemyController
{
    private const float SenseInterval = 0.35f;

    private readonly IObjectTable objectTable;
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly Func<IReadOnlyList<CombatCompanion>> getCompanions;
    private readonly Action<uint, uint> forceEnemyTarget;
    private readonly Func<bool> isRecipeBattle;
    private readonly IPluginLog log;

    private MapEnemySettings? recipeSettings;
    private float senseTimer;

    public MapEnemyController(
        IObjectTable objectTable,
        Configuration config,
        NpcSelector npcSelector,
        CombatEngine combatEngine,
        Func<IReadOnlyList<CombatCompanion>> getCompanions,
        Action<uint, uint> forceEnemyTarget,
        Func<bool> isRecipeBattle,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.getCompanions = getCompanions;
        this.forceEnemyTarget = forceEnemyTarget;
        this.isRecipeBattle = isRecipeBattle;
        this.log = log;
    }

    public bool AllowsMapEnemies => CurrentSettings()?.Enabled == true;

    public void SetRecipeSettings(MapEnemySettings? settings)
    {
        recipeSettings = settings;
        senseTimer = 0;
    }

    public void ClearRecipeSettings()
    {
        recipeSettings = null;
        senseTimer = 0;
    }

    public void Tick(float deltaTime)
    {
        if (!combatEngine.IsActive)
            return;

        var settings = CurrentSettings();
        if (settings is not { Enabled: true })
            return;

        senseTimer -= deltaTime;
        if (senseTimer > 0)
            return;
        senseTimer = SenseInterval;

        SenseMapEnemies(settings);
    }

    public SimulatedNpc? TryRegisterByEntityId(ulong targetId)
    {
        if (targetId == 0 || targetId == 0xE0000000)
            return null;

        if (!combatEngine.IsActive)
            return null;

        var settings = CurrentSettings();
        if (settings is not { Enabled: true })
            return null;

        foreach (var obj in objectTable)
        {
            if (obj.EntityId != (uint)targetId)
                continue;

            var friendly = NearestFriendly(obj.Position);
            return RegisterObject(obj, settings, friendly.EntityId);
        }

        return null;
    }

    private MapEnemySettings? CurrentSettings()
    {
        if (isRecipeBattle())
            return recipeSettings;

        return new MapEnemySettings
        {
            Enabled = config.EnableMapEnemySensing,
            MaxCount = Math.Max(0, config.MapEnemyMaxCount),
            SenseRange = Math.Max(0.1f, config.MapEnemySenseRange),
            Level = Math.Clamp(config.DefaultNpcLevel, 1, 300),
            HpMultiplier = Math.Max(0.0001f, config.DefaultNpcHpMultiplier),
            BehaviorType = (NpcBehaviorType)config.DefaultNpcBehaviorType,
        };
    }

    private void SenseMapEnemies(MapEnemySettings settings)
    {
        var friendlies = FriendlyActors();
        if (friendlies.Count == 0)
            return;

        // The pool limit (and dead-enemy eviction when full) is enforced inside
        // RegisterObject, so we scan every in-range candidate here.
        foreach (var obj in objectTable)
        {
            if (!IsValidMapEnemyCandidate(obj))
                continue;

            if (npcSelector.GetSelectedNpc(obj.EntityId) != null)
                continue;

            var nearest = NearestFriendly(obj.Position, friendlies);
            if (nearest.EntityId == 0 || nearest.Distance > settings.SenseRange)
                continue;

            RegisterObject(obj, settings, nearest.EntityId);
        }
    }

    private SimulatedNpc? RegisterObject(IGameObject obj, MapEnemySettings settings, uint initialTargetId)
    {
        if (!IsValidMapEnemyCandidate(obj))
            return null;

        // Already in the battle: hand back the existing entry, never evict for it.
        var existing = npcSelector.GetSelectedNpc(obj.EntityId);
        if (existing != null)
            return existing;

        // Pool full: drop the earliest dead map enemy to make room. If every map
        // enemy is still alive there is no room, so the newcomer is refused.
        if (settings.MaxCount > 0 && npcSelector.MapEnemyCount >= settings.MaxCount)
        {
            if (!combatEngine.TryEvictDeadMapEnemy())
                return null;
        }

        var (npc, error) = npcSelector.RegisterMapEnemy(
            obj,
            Math.Clamp(settings.Level, 1, 300),
            Math.Max(0.0001f, settings.HpMultiplier),
            settings.BehaviorType,
            Math.Max(0, settings.MaxCount),
            ignoreGlobalMaxTargets: true);

        if (npc == null)
        {
            if (!string.IsNullOrWhiteSpace(error))
                log.Debug($"Map enemy register skipped for 0x{obj.EntityId:X}: {error}");
            return null;
        }

        combatEngine.RegisterNpcEntity(npc);
        npc.AiState = NpcAiState.Engaging;
        npc.EngageDelayTimer = NpcAiController.PlayerTriggeredEngageDelay;
        NpcAiController.StaggerTimers(npc);

        if (initialTargetId != 0)
            forceEnemyTarget(npc.SimulatedEntityId, initialTargetId);

        combatEngine.AddLogEntry($"{npc.Name} joins the fight!", CombatLogType.Info);
        return npc;
    }

    private bool IsValidMapEnemyCandidate(IGameObject obj)
    {
        if (obj.Address == nint.Zero)
            return false;

        if (obj is IPlayerCharacter)
            return false;

        if ((byte)obj.ObjectKind != (byte)ObjectKind.BattleNpc)
            return false;

        return true;
    }

    private List<FriendlyActor> FriendlyActors()
    {
        var result = new List<FriendlyActor>();
        var player = objectTable.LocalPlayer;
        if (player != null && combatEngine.State.PlayerState.IsAlive)
            result.Add(new FriendlyActor(player.EntityId, player.Position, 0));

        foreach (var companion in getCompanions())
        {
            if (!companion.IsSpawned || !companion.State.IsAlive || companion.BattleChara == null)
                continue;

            var obj = (GameObject*)companion.BattleChara;
            result.Add(new FriendlyActor(companion.SimulatedEntityId, (Vector3)obj->Position, 0));
        }

        return result;
    }

    private FriendlyActor NearestFriendly(Vector3 position)
        => NearestFriendly(position, FriendlyActors());

    private static FriendlyActor NearestFriendly(Vector3 position, IReadOnlyList<FriendlyActor> friendlies)
    {
        var best = new FriendlyActor(0, Vector3.Zero, float.MaxValue);
        foreach (var friendly in friendlies)
        {
            var dist = Vector3.Distance(position, friendly.Position);
            if (dist < best.Distance ||
                (MathF.Abs(dist - best.Distance) < 0.001f && friendly.EntityId < best.EntityId))
                best = friendly with { Distance = dist };
        }

        return best;
    }

    private readonly record struct FriendlyActor(uint EntityId, Vector3 Position, float Distance);
}
