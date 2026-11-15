// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Animation;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Spectators;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// Who is on the field: action data and the enemies' kits, the selected and spawned enemies, the
/// allies, spectators and the map's own enemies, and the allies' Glamourer queue
/// (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed unsafe class ActorsModule
{
    private readonly PluginModules m;

    private ActionDataProvider actionDataProvider = null!;
    private NpcActionProfileProvider npcActionProfileProvider = null!;
    private NpcSelector npcSelector = null!;
    private NpcSpawner npcSpawner = null!;
    private Effects.NpcScaleController npcScaleController = null!;
    private CombatCompanionManager companionManager = null!;
    private SpectatorController spectatorController = null!;
    private MapEnemyController mapEnemyController = null!;
    private EnemyControlController enemyControlController = null!;
    private CombatRoster combatRoster = null!;

    public ActorsModule(PluginModules modules) => m = modules;

    public ActionDataProvider ActionData => actionDataProvider;
    public NpcActionProfileProvider ActionProfiles => npcActionProfileProvider;
    public NpcSelector Selector => npcSelector;
    public NpcSpawner Spawner => npcSpawner;
    public Effects.NpcScaleController Scale => npcScaleController;
    public CombatCompanionManager Companions => companionManager;
    public SpectatorController Spectators => spectatorController;
    public MapEnemyController MapEnemies => mapEnemyController;
    /// <summary>Enemy Control: taking over one enemy after a lost battle (possession, movement, the grab).</summary>
    public EnemyControlController EnemyControl => enemyControlController;
    public CombatRoster Roster => combatRoster;

    private IActorsModuleSettings config => m.Config;
    private TargetingService targeting => m.Combat.Targeting;
    private BattleRuleSet ruleParts => m.Combat.RuleParts;
    private IPluginLog log => m.Log;
    private AnimationController animationController => m.World.Animation;
    private MovementBlockHook movementBlockHook => m.World.MovementBlock;
    private VNavmeshIpc vnavmeshIpc => m.World.VNavmesh;
    private GlamourerIpc glamourerIpc => m.World.Glamourer;
    private CombatEngine combatEngine => m.Combat.Engine;
    private CorpseModule corpse => m.Corpse;
    private WeaponDropController weaponDropController => m.Corpse.WeaponDrop;

    /// <summary>Action data, the weapon classifier, the body-type animation fallback and the enemies' kits.</summary>
    public void CreateActionData(IDalamudPluginInterface pluginInterface, IDataManager dataManager)
    {
        actionDataProvider = new ActionDataProvider(dataManager, log, m.Config);
        NpcWeaponClassifier.Initialize(dataManager, log);
        Animation.BodyTypeAnimationFallback.Initialize(pluginInterface, dataManager, log);
        var jobActionKitProvider = new JobActionKitProvider(actionDataProvider, dataManager, log);
        npcActionProfileProvider = new NpcActionProfileProvider(jobActionKitProvider, m.Config, log);
    }

    /// <summary>Monsters' kits come from their model's actions, which animation knows.</summary>
    public void AttachMonsterKits(IDataManager dataManager)
    {
        npcActionProfileProvider.MonsterKits = new MonsterActionKitProvider(
            animationController.NpcModelActions, actionDataProvider, dataManager, log);
    }

    /// <summary>
    /// The selected enemies, the spawner and enemy scaling; and the roster of the player's side (the allies'
    /// list, which the engine is given by constructor and the allies' controller keeps).
    /// </summary>
    public void CreateRoster(IObjectTable objectTable, IDataManager dataManager, IClientState clientState)
    {
        npcSelector = new NpcSelector(objectTable, m.Config, npcActionProfileProvider, log);
        npcSpawner = new NpcSpawner(objectTable, dataManager, clientState, m.Config, npcActionProfileProvider, movementBlockHook, log);
        npcScaleController = new Effects.NpcScaleController(m.Config, npcSelector, objectTable);
        combatRoster = new CombatRoster(config);
    }

    /// <summary>
    /// Enemy Control (taking over one enemy after a lost battle), with the camera coordinator it submits its
    /// follow framing to.
    /// </summary>
    public void CreateEnemyControl(IKeyState keyState, IGamepadState gamepadState, IFramework framework)
    {
        enemyControlController = new EnemyControlController(
            keyState, gamepadState, framework, m.Corpse.PlayerRagdoll, animationController, m.World.BoneTransforms,
            movementBlockHook, m.Camera.Active, vnavmeshIpc,
            m.Config, log);
        enemyControlController.SetCameraCoordinator(m.Camera.Coordinator);
    }

    public void CreateCompanions(IObjectTable objectTable, IClientState clientState, ITargetManager targetManager)
    {
        companionManager = new CombatCompanionManager(
            objectTable, clientState, config, combatEngine, combatRoster, targeting, ruleParts,
            corpse, entityId => npcSelector.GetSelectedNpc(entityId) != null, animationController,
            movementBlockHook, targetManager, log);
    }

    public void CreateSpectators(IClientState clientState)
    {
        spectatorController = new SpectatorController(
            npcSelector, movementBlockHook, animationController.EmotePlayer,
            vnavmeshIpc, clientState, m.Config,
            () => combatEngine.State.PlayerState.IsAlive || companionManager.HasLivingCompanions,
            log);
    }

    public void CreateMapEnemies(IObjectTable objectTable)
    {
        mapEnemyController = new MapEnemyController(
            objectTable,
            m.Config,
            npcSelector,
            combatEngine,
            () => companionManager.Companions,
            companionManager.HasSourceEntity,
            companionManager.ForceEnemyTarget,
            () => npcSpawner.SpawnModeActive,
            log);
    }

    /// <summary>What the allies' manager and the spawner report back: spawned, died, revived, failed.</summary>
    public void WireActorCallbacks(IChatGui chatGui)
    {
        // The allies' notifications are CombatEvents slots (stage B9).
        combatEngine.Events.AllySpawned = companion =>
        {
            combatEngine.RegisterCompanionEntity(companion);
            ApplyCompanionGlamourer(companion);
            log.Info($"Spawned companion '{companion.Name}' registered as friendly combatant.");
        };
        combatEngine.Events.AllyDied = corpse.OnCompanionDeathRagdoll;
        combatEngine.Events.AllyRevived = address =>
        {
            corpse.RemoveNpcRagdoll(address);
            weaponDropController.RemoveFor(address);
            // The revived ally has its outfit back; its armour can come off again.
            corpse.ArmorDetachment.Forget(address);
        };
        combatEngine.Events.AllySpawnError = msg =>
        {
            chatGui.PrintError($"[CombatSim] Party error: {msg}");
        };

        // Wire NpcSpawner callbacks
        npcSpawner.OnNpcSpawnComplete = (npc) =>
        {
            npcSelector.RegisterSpawnedNpc(npc, ignoreMaxTargets: npcSpawner.SpawnModeActive);
            combatEngine.RegisterNpcEntity(npc);
            log.Info($"Spawned NPC '{npc.Name}' registered as combat target.");
        };
        npcSpawner.OnSpawnError = (msg) =>
        {
            chatGui.PrintError($"[CombatSim] Spawn error: {msg}");
        };
    }

    public SimulatedNpc? FindNpcByAddress(nint address)
    {
        if (address == nint.Zero) return null;
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address == address && npc.BattleChara != null) return npc;
        foreach (var npc in npcSpawner.SpawnedNpcs)
            if (npc.Address == address && npc.BattleChara != null) return npc;
        return null;
    }

    private sealed class GlamourerApplyJob
    {
        public CombatCompanion Companion = null!;
        public Guid DesignId;
        public int FramesUntilNext;
        public int AttemptsLeft;
    }

    private readonly List<GlamourerApplyJob> pendingGlamourerApplies = new();

    // Queue the configured Glamourer equipment design for a freshly spawned companion
    // clone. Equipment-only, so the clone keeps its own/randomized customize. A blank
    // setting (None) or a missing/uninstalled Glamourer is a no-op — the clone then
    // just wears its inherited equipment. We defer + retry (rather than apply inline)
    // because the companion is still building its draw object at spawn-complete.
    private void ApplyCompanionGlamourer(CombatCompanion companion)
    {
        if (string.IsNullOrEmpty(config.PartyCompanionGlamourerDesignId))
            return;
        if (!Guid.TryParse(config.PartyCompanionGlamourerDesignId, out var designId))
            return;

        pendingGlamourerApplies.Add(new GlamourerApplyJob
        {
            Companion = companion,
            DesignId = designId,
            FramesUntilNext = 1,
            AttemptsLeft = 8,
        });
    }

    public void ProcessPendingGlamourerApplies()
    {
        for (var i = pendingGlamourerApplies.Count - 1; i >= 0; i--)
        {
            var job = pendingGlamourerApplies[i];

            // Drop if the companion despawned before we could apply.
            if (!job.Companion.IsSpawned || job.Companion.BattleChara == null)
            {
                pendingGlamourerApplies.RemoveAt(i);
                continue;
            }

            if (--job.FramesUntilNext > 0)
                continue;

            // Glamourer addresses actors by their GLOBAL object-table index, not the
            // ClientObjectManager index we spawned with (companion.ObjectIndex), so use
            // the live game object's index — otherwise Glamourer reports ActorNotFound.
            var tableIndex = job.Companion.GameObjectRef?.ObjectIndex ?? ushort.MaxValue;
            if (tableIndex == ushort.MaxValue)
            {
                if (--job.AttemptsLeft <= 0)
                    pendingGlamourerApplies.RemoveAt(i);
                else
                    job.FramesUntilNext = 5;
                continue;
            }

            var ok = glamourerIpc.ApplyDesignToObject(job.DesignId, tableIndex, equipmentOnly: true);
            job.AttemptsLeft--;
            if (ok || job.AttemptsLeft <= 0)
                pendingGlamourerApplies.RemoveAt(i);
            else
                job.FramesUntilNext = 5; // retry in ~5 frames until the draw object resolves
        }
    }
}
