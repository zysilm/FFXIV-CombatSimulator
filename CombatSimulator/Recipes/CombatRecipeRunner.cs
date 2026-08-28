// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Recipes;

/// <summary>
/// Runs combat recipes independently of the UI. MainWindow and the encounter director share this
/// single path so scripted encounters remain behavior-identical to manually started recipes.
/// </summary>
public sealed class CombatRecipeRunner
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly NpcSpawner npcSpawner;
    private readonly CombatCompanionManager companionManager;
    private readonly CombatEngine combatEngine;
    private readonly MapEnemyController mapEnemyController;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly UseActionHook useActionHook;
    private readonly CombatRecipeBook recipeBook;
    private NpcCatalog? npcCatalog;

    public CombatRecipeRunner(
        Configuration config,
        NpcSelector npcSelector,
        NpcSpawner npcSpawner,
        CombatCompanionManager companionManager,
        CombatEngine combatEngine,
        MapEnemyController mapEnemyController,
        IDataManager dataManager,
        IChatGui chatGui,
        UseActionHook useActionHook,
        IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.npcSpawner = npcSpawner;
        this.companionManager = companionManager;
        this.combatEngine = combatEngine;
        this.mapEnemyController = mapEnemyController;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.useActionHook = useActionHook;
        this.log = log;
        recipeBook = new CombatRecipeBook(log);
    }

    public IReadOnlyList<CombatRecipe> Recipes => recipeBook.Recipes;

    public bool TryStart(string recipeName)
    {
        foreach (var recipe in Recipes)
        {
            if (!string.Equals(recipe.Name, recipeName, StringComparison.OrdinalIgnoreCase))
                continue;
            return Start(recipe);
        }

        chatGui.PrintError($"[CombatSim] Encounter recipe not found: {recipeName}.");
        log.Warning($"Combat recipe '{recipeName}' was requested but is not loaded.");
        return false;
    }

    public bool Start(CombatRecipe recipe)
    {
        if (!useActionHook.IsHealthy)
        {
            chatGui.PrintError(
                "[CombatSim] Cannot start: UseAction hook is not healthy. Actions could reach the server.");
            return false;
        }

        Stop(print: false);

        if (config.FightingMode)
        {
            config.ActionMode = false;
            config.EnableCombatCompanions = false;
            config.SensePartyMembers = false;
            config.EnableMapPlayerEnemySensing = false;
            npcSpawner.SpawnModeActive = false;
            mapEnemyController.ClearRecipeSettings();
            combatEngine.StartSimulation();
            chatGui.Print("[CombatSim] Fighting Mode started. Attack one enemy to begin a 1v1.");
            return true;
        }

        config.RecipeNpcCollisionOverride = recipe.NpcCollision;
        config.EnableCombatCompanions = true;
        config.SensePartyMembers = recipe.Companions.Exists(c => c.Type == CompanionRecipeType.VisiblePlayers);
        config.EnableMapPlayerEnemySensing = recipe.MapEnemies.Exists(g => g.Enabled && g.IncludePlayers);
        config.CombatCompanionMaxCount = Math.Min(
            CombatCompanionManager.MaxCompanionCap, TotalRequestedCompanions(recipe));

        npcSpawner.SpawnModeActive = true;
        combatEngine.StartSimulation();
        var mapEnemySettings = BuildMapEnemySettings(recipe);
        mapEnemyController.SetRecipeSettings(mapEnemySettings);

        var queuedCompanions = 0;
        foreach (var group in recipe.Companions)
        {
            var count = Math.Max(0, group.Count);
            queuedCompanions += group.Type switch
            {
                CompanionRecipeType.VisiblePlayers => companionManager.SpawnFromVisiblePlayers(count),
                CompanionRecipeType.Self => companionManager.SpawnSelfCharacters(
                    count, randomizeAppearance: false, ignoreConfiguredMax: true),
                CompanionRecipeType.SelfRandomized => companionManager.SpawnSelfCharacters(
                    count, randomizeAppearance: true, ignoreConfiguredMax: true),
                _ => 0,
            };
        }

        var queuedEnemies = QueueEnemies(recipe.Enemies);

        var mapEnemyText = mapEnemySettings != null
            ? $", up to {mapEnemySettings.MaxCount} map enemy/enemies"
            : "";
        chatGui.Print(
            $"[CombatSim] Started recipe '{recipe.Name}' ({queuedCompanions} companion(s), " +
            $"{queuedEnemies} enemy/enemies queued{mapEnemyText}).");
        return true;
    }

    public int QueueEnemies(IReadOnlyList<CombatRecipeEnemyGroup> groups)
    {
        var queuedEnemies = 0;
        npcCatalog ??= new NpcCatalog(dataManager, log);
        foreach (var group in groups)
        {
            var entry = ResolveEnemy(group);
            if (entry == null)
            {
                chatGui.PrintError(
                    $"[CombatSim] Encounter enemy not found: {group.Name} ({group.Type}:{group.Id}).");
                continue;
            }

            var remainingCapacity = Math.Max(0, npcSpawner.MaxNpcs - npcSpawner.TotalCount);
            var count = Math.Min(Math.Clamp(group.Count, 0, npcSpawner.MaxNpcs), remainingCapacity);
            for (var i = 0; i < count; i++)
            {
                npcSpawner.QueueSpawn(new NpcSpawnRequest
                {
                    BNpcBaseId = entry.Type == NpcCatalogType.BNpc ? entry.Id : 0,
                    BNpcNameId = entry.BNpcNameId,
                    ENpcBaseId = entry.Type is NpcCatalogType.ENpc or NpcCatalogType.Human ? entry.Id : 0,
                    Level = Math.Clamp(config.FastCombatLevel, 1, 300),
                    HpMultiplier = Math.Max(0.0001f, group.HpMultiplier),
                });
                queuedEnemies++;
            }
        }

        return queuedEnemies;
    }

    public void Reset(CombatRecipe? fallbackRecipe = null)
    {
        if (!combatEngine.IsActive)
        {
            if (fallbackRecipe != null)
                Start(fallbackRecipe);
            return;
        }

        var keepCompanionsOnReset = config.KeepCompanionsOnReset;
        config.KeepCompanionsOnReset = true;
        combatEngine.ResetState();
        config.KeepCompanionsOnReset = keepCompanionsOnReset;
        chatGui.Print("[CombatSim] Fast combat reset.");
    }

    public void Stop(bool print = true)
    {
        combatEngine.StopSimulation();
        foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
            npcSelector.UnregisterSpawnedNpc(npc);
        npcSpawner.DespawnAll();
        companionManager.DespawnAll();
        npcSpawner.SpawnModeActive = false;
        mapEnemyController.ClearRecipeSettings();
        config.RecipeNpcCollisionOverride = null;

        if (print)
            chatGui.Print("[CombatSim] Fast combat stopped.");
    }

    private MapEnemySettings? BuildMapEnemySettings(CombatRecipe recipe)
    {
        foreach (var group in recipe.MapEnemies)
        {
            if (!group.Enabled || group.MaxCount <= 0)
                continue;

            return new MapEnemySettings
            {
                Enabled = true,
                IncludeBattleNpcs = group.IncludeBattleNpcs,
                IncludePlayers = group.IncludePlayers,
                MaxCount = Math.Max(0, group.MaxCount),
                SenseRange = Math.Max(0.1f, group.SenseRange),
                Level = Math.Clamp(config.FastCombatLevel, 1, 300),
                HpMultiplier = Math.Max(0.0001f, group.HpMultiplier),
            };
        }

        return null;
    }

    private NpcCatalogEntry? ResolveEnemy(CombatRecipeEnemyGroup group)
    {
        npcCatalog ??= new NpcCatalog(dataManager, log);
        if (group.Id != 0)
        {
            var byId = npcCatalog.FindById(group.Type, group.Id)
                ?? (group.Type == NpcCatalogType.Human
                    ? npcCatalog.FindById(NpcCatalogType.ENpc, group.Id)
                    : null)
                ?? (group.Type == NpcCatalogType.ENpc
                    ? npcCatalog.FindById(NpcCatalogType.Human, group.Id)
                    : null);
            if (byId != null)
                return byId;
        }

        return !string.IsNullOrWhiteSpace(group.Name)
            ? npcCatalog.FindByNameOccurrence(group.Name, group.Type, group.Occurrence)
              ?? npcCatalog.FindByNameOccurrence(group.Name, null, group.Occurrence)
            : null;
    }

    private static int TotalRequestedCompanions(CombatRecipe recipe)
    {
        var total = 0;
        foreach (var group in recipe.Companions)
            total += Math.Max(0, group.Count);
        return Math.Max(1, total);
    }
}
