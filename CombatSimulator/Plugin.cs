using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Core;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator;

public sealed unsafe class CombatSimulatorPlugin : IDalamudPlugin
{
    private const string CommandName = "/combatsim";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly NpcSpawner npcSpawner;
    private readonly ActionDataProvider actionDataProvider;
    private readonly NpcActionProfileProvider npcActionProfileProvider;
    private readonly DamageCalculator damageCalculator;
    private readonly PartyEngagePlanner partyEngagePlanner;
    private readonly TerrainHeightService terrainHeightService;
    private readonly GlamourerIpc glamourerIpc;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly AnimationController animationController;
    private readonly BoneTransformService boneTransformService;
    private readonly RagdollController ragdollController;
    private readonly WeaponDropController weaponDropController;
    private readonly DismembermentController dismembermentController;
    private readonly CombatEngine combatEngine;
    private readonly CombatCompanionManager companionManager;
    private readonly NpcAiController npcAiController;
    private readonly PlayerTargetController playerTargetController;
    private readonly MapEnemyController mapEnemyController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly UseActionHook useActionHook;
    private readonly DeathCamController deathCamController;
    private readonly ActiveCameraController activeCameraController;
    private readonly Dev.VictorySequenceController victorySequenceController;
    private readonly Dev.BoneHoldTestModeController executionModeController;
    private readonly Dev.KoStripController koStripController;
    private readonly Dev.MonsterModeController monsterModeController;
    private readonly HookSafetyChecker hookSafetyChecker;

    // Action Mode (动作模式): real-time combat layer wired through narrow seams.
    private readonly CombatSimulator.ActionCombat.ActionComboSink actionComboSink;
    private readonly CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter;
    private readonly CombatSimulator.ActionCombat.TelegraphSystem telegraphSystem;
    private readonly CombatSimulator.ActionCombat.ActionModeController actionModeController;
    private readonly CombatSimulator.ActionCombat.HitFeedbackController hitFeedbackController;

    // NPC ragdoll controllers (multiple concurrent, persist until sim stop/reset/zone change)
    private readonly Dictionary<nint, RagdollController> npcRagdolls = new();

    // Dev: NPCs hidden by occlusion check (need to restore visibility on cleanup)
    private readonly HashSet<nint> occlusionHiddenNpcs = new();
    private readonly MainWindow mainWindow;
    private readonly HpBarOverlay hpBarOverlay;
    private readonly CombatLogWindow combatLogWindow;
    private readonly RagdollDebugOverlay ragdollDebugOverlay;
    private readonly CombatLinkOverlay combatLinkOverlay;
    private readonly TelegraphOverlay telegraphOverlay;
    private readonly OsuParryOverlay osuParryOverlay;
    private readonly ReticleOverlay reticleOverlay;
    private bool hookSafetyScanned;
    private bool wasLoggedIn;

    public CombatSimulatorPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        ICondition condition,
        IGamepadState gamepadState,
        IKeyState keyState,
        ITargetManager targetManager,
        ISigScanner sigScanner,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.framework = framework;
        this.chatGui = chatGui;
        this.log = log;

        // Store in static services for easy access
        Services.Init(pluginInterface, clientState, objectTable, framework,
            gameInterop, dataManager, gameGui, chatGui, condition, log);

        // Configuration
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // Simulation
        actionDataProvider = new ActionDataProvider(dataManager, log, config);
        NpcWeaponClassifier.Initialize(dataManager, log);
        npcActionProfileProvider = new NpcActionProfileProvider(actionDataProvider, log);
        damageCalculator = new DamageCalculator(new CombatStatProvider(dataManager, log));
        glamourerIpc = new GlamourerIpc(pluginInterface, log);
        vnavmeshIpc = new VNavmeshIpc(pluginInterface, log);
        partyEngagePlanner = new PartyEngagePlanner(vnavmeshIpc);
        terrainHeightService = new TerrainHeightService();
        movementBlockHook = new MovementBlockHook(gameInterop, clientState, log);
        animationController = new AnimationController(log, clientState, dataManager, gameInterop, sigScanner, config);
        boneTransformService = new BoneTransformService(gameInterop, sigScanner, log);
        npcSelector = new NpcSelector(objectTable, targetManager, config, npcActionProfileProvider, log);
        npcSpawner = new NpcSpawner(objectTable, dataManager, clientState, config, npcActionProfileProvider, log);
        ragdollController = new RagdollController(boneTransformService, npcSelector, movementBlockHook, config, log, GetPartyCollisionAddresses);
        weaponDropController = new WeaponDropController(boneTransformService, config, log);
        dismembermentController = new DismembermentController(boneTransformService, glamourerIpc, animationController, objectTable, config, log);
        koStripController = new Dev.KoStripController(config, glamourerIpc, log);
        deathCamController = new DeathCamController(gameInterop, clientState, sigScanner, config, log);
        activeCameraController = new ActiveCameraController(gameInterop, clientState, sigScanner, config, log);
        monsterModeController = new Dev.MonsterModeController(keyState, gamepadState, framework, ragdollController, animationController, boneTransformService, movementBlockHook, activeCameraController, vnavmeshIpc, dismembermentController, glamourerIpc, config, log);
        victorySequenceController = new Dev.VictorySequenceController(
            boneTransformService, animationController.EmotePlayer,
            movementBlockHook, ragdollController, vnavmeshIpc, clientState, targetManager, config, log);
        combatEngine = new CombatEngine(
            actionDataProvider, damageCalculator, animationController,
            glamourerIpc, movementBlockHook, ragdollController,
            config, npcSelector, clientState, log, deathCamController,
            victorySequenceController);
        // Action Mode wiring. Seams: ActionComboSink (player input), CombatModeRouter
        // (enemy attack executor), TelegraphSystem (windup→hitbox). All gate on
        // config.ActionMode; off ⇒ behavior-equivalent simulation paths.
        actionComboSink = new CombatSimulator.ActionCombat.ActionComboSink(config);
        var playerGuardController = new CombatSimulator.ActionCombat.PlayerGuardController(
            animationController,
            config,
            () => combatEngine.State.PlayerState.IsAlive,
            log);
        playerGuardController.OnChainGuardResolved += combatEngine.RestorePlayerGuardMp;
        telegraphSystem = new CombatSimulator.ActionCombat.TelegraphSystem(
            combatEngine, animationController, config, () => playerGuardController.IsGuardActive, playerGuardController.NotifyPerfectGuard, log);
        var playerHitboxResolver = new CombatSimulator.ActionCombat.PlayerHitboxResolver(combatEngine, npcSelector, config);
        hitFeedbackController = new CombatSimulator.ActionCombat.HitFeedbackController(
            config, npcSelector.GetSelectedNpc, animationController, log);
        deathCamController.HitShakeProvider = () => hitFeedbackController.CurrentShakeOffset;
        actionModeController = new CombatSimulator.ActionCombat.ActionModeController(
            config, actionComboSink, playerHitboxResolver, playerGuardController,
            telegraphSystem, combatEngine, animationController, hitFeedbackController, gamepadState, log);
        combatModeRouter = new CombatSimulator.ActionCombat.CombatModeRouter(
            config,
            new CombatSimulator.ActionCombat.InstantAttackExecutor(combatEngine),
            new CombatSimulator.ActionCombat.TelegraphedAttackExecutor(telegraphSystem, config));
        // Clear any live telegraphs when a combat session ends.
        combatEngine.OnSimulationReset += telegraphSystem.Clear;
        // Release any outstanding hitstop freezes when combat ends so no enemy is left frozen.
        combatEngine.OnSimulationReset += hitFeedbackController.Clear;
        // Super-armor enemies during their telegraph windup (no flinch from player hits).
        combatEngine.IsTargetSuperArmored = telegraphSystem.IsWindingUp;

        executionModeController = new Dev.BoneHoldTestModeController(
            boneTransformService, animationController.EmotePlayer, ragdollController, animationController,
            movementBlockHook, vnavmeshIpc, combatEngine, log);
        companionManager = new CombatCompanionManager(
            objectTable, clientState, config, combatEngine, animationController,
            movementBlockHook, vnavmeshIpc, targetManager, partyEngagePlanner, terrainHeightService, log);
        combatEngine.ResolveNpcTarget = companionManager.SelectEnemyTarget;
        combatEngine.ResolveExternalEntityAddress = companionManager.ResolveAddress;
        combatEngine.HasLivingCompanions = () => companionManager.HasLivingCompanions;
        combatEngine.OnPlayerDamageDealt = companionManager.RegisterPlayerDamage;
        combatEngine.OnPlayerDamageDealtToTarget = companionManager.RegisterPlayerDamage;
        npcAiController = new NpcAiController(
            combatEngine, animationController, movementBlockHook, vnavmeshIpc,
            clientState, config, partyEngagePlanner, terrainHeightService, log,
            combatModeRouter,
            addr => victorySequenceController.ControlsNpc(addr) || monsterModeController.ControlsNpc(addr));

        // Custom in-sim target lock system (综合提升). Takes over the game's target
        // keybinds during simulation; the engine reads the locked target for
        // auto-attack and UseActionHook routes manual actions to it.
        playerTargetController = new PlayerTargetController(
            gameInterop, combatEngine, npcSelector, config, objectTable, gameGui, log);
        combatEngine.GetLockedTargetId = () => playerTargetController.LockedTargetEntityId;
        combatEngine.OnPlayerHitByNpc = playerTargetController.NotifyPlayerHitBy;

        // Fighting camera: frame the player + locked 1v1 target; suppress Death Cam when it owns the camera.
        activeCameraController.GetFightingTargetAddress = () =>
        {
            var t = playerTargetController.LockedTarget;
            if (t != null && t.IsSpawned && t.State.IsAlive && t.Address != nint.Zero)
                return t.Address;
            return null;
        };
        combatEngine.SuppressDeathCam = () => activeCameraController.IsFightingEngaged;

        mapEnemyController = new MapEnemyController(
            objectTable,
            config,
            npcSelector,
            combatEngine,
            () => companionManager.Companions,
            companionManager.HasSourceEntity,
            companionManager.ForceEnemyTarget,
            () => npcSpawner.SpawnModeActive,
            log);
        companionManager.IsSourceEnemy = entityId => npcSelector.GetSelectedNpc(entityId) != null;

        companionManager.OnCompanionSpawnComplete = companion =>
        {
            combatEngine.RegisterCompanionEntity(companion);
            ApplyCompanionGlamourer(companion);
            log.Info($"Spawned companion '{companion.Name}' registered as friendly combatant.");
        };
        companionManager.OnCompanionDeathRagdoll = OnCompanionDeathRagdoll;
        companionManager.OnSpawnError = msg =>
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

        // Re-register alive spawned NPCs on sim start (they're removed from
        // npcSelector by DeselectAll during StopSimulation). For real
        // first-time starts this just re-populates the selector so
        // NpcAiController can tick the existing spawns.
        combatEngine.OnSimulationStarted += () =>
        {
            foreach (var npc in npcSpawner.SpawnedNpcs)
            {
                if (npc.IsSpawned && npc.BattleChara != null)
                    npcSelector.RegisterSpawnedNpc(npc, ignoreMaxTargets: npcSpawner.SpawnModeActive);
            }
        };

        // On sim stop or reset: despawn every virtual enemy and queue a
        // fresh spawn with the same params at a NEW randomized position
        // around the player (regenerate, not revive-in-place).
        // Reason — humanoid clone state gets left in a broken blend-lock
        // state by the cinema cleanup (see RegenerateAll doc), and a
        // fresh CopyFromCharacter clone is the only known fix.
        //
        // ResetState doesn't call DeselectAll so the selector still holds
        // references to NPCs we're about to despawn. Explicitly unregister
        // them first to avoid dangling pointers.
        combatEngine.OnSimulationReset += () =>
        {
            DeactivateAllNpcRagdolls();
            weaponDropController.RemoveAll();
            dismembermentController.RemoveAll();
            koStripController.Reset();
            monsterModeController.Despawn();
            activeCameraController.ResetFightingCamera();

            // Keep companions across a combat *reset* (IsActive stays true) when the
            // option is set — revive/heal them instead of despawning. Stopping the
            // simulation (IsActive false) always despawns.
            if (config.KeepCompanionsOnReset && combatEngine.State.IsActive)
                companionManager.ResetForCombatReset();
            else
                companionManager.DespawnAll();

            if (npcSpawner.SpawnedNpcs.Count == 0) return;

            log.Info($"Sim reset — despawning and re-queuing {npcSpawner.SpawnedNpcs.Count} virtual enemies.");

            foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
                npcSelector.UnregisterSpawnedNpc(npc);

            npcSpawner.RegenerateAll();
        };

        // Weapon drop is co-triggered with ragdoll (same delay), so writes survive while animation is frozen
        combatEngine.OnNpcDeathRagdoll = OnNpcDeathRagdoll;
        combatEngine.OnPlayerDeath = addr =>
        {
            weaponDropController.SpawnFor(addr, config.RagdollActivationDelay);
            if (config.EnableDismemberRollaway && config.DismemberPocBones is { Count: > 0 })
            {
                // Capture the player's LIVE Glamourer state once (null if Glamourer absent) and spawn one
                // severed-limb clone per selected part.
                var playerIdx = objectTable.LocalPlayer?.ObjectIndex ?? 0;
                var glam = glamourerIpc.GetStateBase64((int)playerIdx);
                log.Info($"Dismember: glamour capture playerIdx={playerIdx} len={(glam?.Length ?? -1)}");
                foreach (var bone in config.DismemberPocBones)
                    dismembermentController.SpawnFor(addr, bone, config.RagdollActivationDelay, glam);
            }
            koStripController.StripOnKo(addr);
            if (config.MonsterControlKiller)
            {
                var killer = FindNpcByAddress(combatEngine.LastPlayerKillerAddress);
                if (killer != null) monsterModeController.ControlKiller(killer);
                else log.Info("MonsterMode: no valid killer to control.");
            }
            else if (config.MonsterSpawnOnDeath)
            {
                monsterModeController.Spawn();
            }
            activeCameraController.NotifyCombatantDeath(addr, isPlayer: true);
        };

        // Hook safety checker — register native functions we CALL (not hook) that other plugins may hook.
        // We check for JMP detours at each address to detect third-party hooks.
        hookSafetyChecker = new HookSafetyChecker(pluginInterface, log);
        // expectedFirstByte from each function's signature pattern (fallback if JMP pattern is unrecognized)
        hookSafetyChecker.Register("ActorVfxCreate",
            "Spawns VFX on actors. Hooked by VFXEditor, RotationSolver.",
            animationController.ActorVfxCreateAddress, expectedFirstByte: 0x40); // sig: "40 53 55 56 57..."
        hookSafetyChecker.Register("ActorVfxRemove",
            "Removes VFX from actors. Hooked by VFX-related plugins.",
            animationController.ActorVfxRemoveAddress); // resolved via pointer chase — no expected byte
        hookSafetyChecker.Register("ActionEffectHandler.Receive",
            "Processes combat effects (damage, flytext, animations). Hooked by ACT, combat log plugins.",
            (nint)FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler.MemberFunctionPointers.Receive);

        // Safety — enable hooks immediately; they gate on internal state
        useActionHook = new UseActionHook(gameInterop, combatEngine, npcSelector, npcSpawner, config, clientState, log, playerTargetController, mapEnemyController, actionComboSink);
        useActionHook.Enable();
        movementBlockHook.Enable();

        // Do not auto-enable Active Camera on plugin/game startup. The camera and
        // skeleton stack can still be settling during login; users can enable it
        // manually once in-world.
        if (config.EnableActiveCamera)
        {
            config.EnableActiveCamera = false;
            config.Save();
        }

        // GUI
        mainWindow = new MainWindow(config, npcSelector, npcSpawner, companionManager, combatEngine, mapEnemyController, glamourerIpc, vnavmeshIpc, animationController, ragdollController, dismembermentController, deathCamController, activeCameraController, hookSafetyChecker, clientState, dataManager, chatGui, log);
        hpBarOverlay = new HpBarOverlay(npcSelector, companionManager, combatEngine, boneTransformService, gameGui, clientState, config);
        combatLogWindow = new CombatLogWindow(combatEngine);
        ragdollDebugOverlay = new RagdollDebugOverlay(ragdollController, mainWindow, config, gameGui, clientState);
        combatLinkOverlay = new CombatLinkOverlay(npcSelector, playerTargetController, combatEngine, boneTransformService, gameGui, config);
        telegraphOverlay = new TelegraphOverlay(telegraphSystem, gameGui, config);
        osuParryOverlay = new OsuParryOverlay(telegraphSystem, gameGui, config, playerGuardController);
        reticleOverlay = new ReticleOverlay(playerHitboxResolver, combatEngine, gameGui, config);

        // Register
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Combat Simulator.\n" +
                          "/combatsim professional - Open Professional Mode settings\n" +
                          "/combatsim start - Start combat simulation\n" +
                          "/combatsim stop - Stop combat simulation\n" +
                          "/combatsim reset - Reset combat state\n" +
                          "/combatsim recover - Force reset the local player's animation state\n" +
                          "/combatsim export-actions - Export player action data to Actions.json",
        });

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        log.Info("Combat Simulator loaded. [virtual-enemies: humanoid clone-from-player bootstrap]");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        commandManager.RemoveHandler(CommandName);

        // Game-state cleanup touches live game objects (despawn, restore animations/visibility).
        // On game CLOSE, Dispose runs after the game has freed those objects, so doing it would
        // dereference freed memory and crash. Only do it while a session is alive (plugin unload
        // while playing); on close the game frees everything itself.
        if (Core.Services.ObjectTable.LocalPlayer != null)
        {
            RestoreOcclusionHiddenNpcs();
            DeactivateAllNpcRagdolls();
            npcSpawner.SpawnModeActive = false;
            companionManager.DespawnAll();
            npcSpawner.DespawnAll();
            combatEngine.StopSimulation();
            npcSelector.DeselectAll();
        }

        // Resource disposal (BEPU sims, hooks, event handlers) — always safe; any game-memory
        // access inside is itself guarded.
        companionManager.Dispose();
        npcSpawner.Dispose();
        useActionHook.Dispose();
        playerTargetController.Dispose();
        movementBlockHook.Dispose();
        combatLogWindow.Dispose();
        hpBarOverlay.Dispose();
        mainWindow.Dispose();
        npcAiController.Dispose();
        combatEngine.Dispose();
        victorySequenceController.Dispose();
        executionModeController.Dispose();
        koStripController.Dispose();
        monsterModeController.Dispose();
        ragdollController.Dispose();
        weaponDropController.Dispose();
        dismembermentController.Dispose();
        boneTransformService.Dispose();
        deathCamController.Dispose();
        activeCameraController.Dispose();
        animationController.Dispose();
        npcSelector.Dispose();

        log.Info("Combat Simulator unloaded.");
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            config.ShowMainWindow = !config.ShowMainWindow;
            config.Save();
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "start":
                if (!useActionHook.IsHealthy)
                {
                    chatGui.PrintError("[CombatSim] Cannot start: UseAction hook is not healthy. Actions would reach the server.");
                    break;
                }
                combatEngine.StartSimulation();
                chatGui.Print("[CombatSim] Combat simulation started.");
                break;

            case "stop":
                combatEngine.StopSimulation();
                chatGui.Print("[CombatSim] Combat simulation stopped.");
                break;

            case "reset":
                combatEngine.ResetState();
                chatGui.Print("[CombatSim] Combat state reset.");
                break;

            case "recover":
                ragdollController.Deactivate();
                animationController.RecoverPlayerAnimationState();
                chatGui.Print("[CombatSim] Player animation state recovered.");
                break;

            case "professional":
            case "pro":
            case "settings":
                config.ShowProfessionalWindow = true;
                config.Save();
                break;

            case "export-actions":
            case "exportactions":
                ExportActionDatabase();
                break;

            default:
                config.ShowMainWindow = !config.ShowMainWindow;
                config.Save();
                break;
        }
    }

    private void ExportActionDatabase()
    {
        var exporter = new Simulation.ActionDatabaseExporter(Core.Services.DataManager, log);
        var result = exporter.Export();
        if (result.Success)
        {
            actionDataProvider.ReloadDatabase();
            chatGui.Print($"[CombatSim] Exported {result.Count} player damage actions to {result.Path}");
            if (!string.IsNullOrEmpty(result.AttackTypeSummary))
                chatGui.Print($"[CombatSim] AttackTypes: {result.AttackTypeSummary}");
        }
        else
        {
            chatGui.PrintError($"[CombatSim] Action export failed: {result.Error}");
        }
    }

    private void OnDraw()
    {
        // No plugin UI off-world (title / character select) — prevents acting on
        // a preview character (e.g. clicking Reset there crashes the game).
        if (!clientState.IsLoggedIn)
            return;

        if (config.ShowShortcuts)
            mainWindow.DrawShortcutsBar();

        if (config.ShowFastCombatToolbar)
            mainWindow.DrawFastCombatToolbar();

        if (config.ShowDeathCamToolbar)
            mainWindow.DrawDeathCamToolbar();

        if (config.ShowActiveCamToolbar)
            mainWindow.DrawActiveCamToolbar();

        if (config.ShowGrabToolbar)
            mainWindow.DrawGrabToolbar(victorySequenceController);

        if (config.ShowHoldToolbar)
            mainWindow.DrawHoldToolbar(executionModeController);

        if (config.ShowKoStripToolbar)
            mainWindow.DrawKoStripToolbar(koStripController);

        if (config.ShowMonsterToolbar)
            mainWindow.DrawMonsterToolbar(monsterModeController);

        if (config.ShowMainWindow)
            mainWindow.Draw();

        if (config.ShowProfessionalWindow)
            mainWindow.DrawProfessional();

        mainWindow.DrawDefeatRevivePopup();

        if (combatEngine.IsActive)
        {
            if (config.ShowEnemyHpBar || config.ShowPlayerHpBar || config.ShowHudPlayerHpBar)
                hpBarOverlay.Draw();

            if (config.ShowCombatLog)
                combatLogWindow.Draw();

            if (config.ShowCombatLinkArcs || config.ShowLockMarker)
                combatLinkOverlay.Draw();

            if (config.ActionMode && config.ShowTelegraphs)
                telegraphOverlay.Draw();

            osuParryOverlay.Draw();
            reticleOverlay.Draw();
        }

        ragdollDebugOverlay.Draw();

        if (!combatEngine.IsActive)
        {
            // Restore native HP bar when sim is not active
            hpBarOverlay.RestoreNativeHpBar();
        }
    }

    private void OnOpenMainUi()
    {
        config.ShowMainWindow = true;
        config.Save();
    }

    private void OnOpenConfig()
    {
        config.ShowProfessionalWindow = true;
        config.Save();
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
            // The plugin only operates in-world. On the title / character-select
            // screens the visible character is just a preview model — cloning or
            // spawning against it (companion sensing, etc.) crashes the game — so
            // run nothing until logged in, and tear down on logout.
            var loggedIn = clientState.IsLoggedIn;
            if (wasLoggedIn && !loggedIn)
                HandleLoggedOut();
            wasLoggedIn = loggedIn;
            if (!loggedIn)
                return;

            // Real wall-clock delta for all per-frame simulation. The framework fires
            // once per rendered frame at whatever framerate the game runs; using a fixed
            // 1/60 made every AI behavior (movement, attack cadence, casts, cooldowns,
            // DPS) run fast above 60fps and slow below it. Clamp out loading/hitch gaps.
            var deltaTime = Math.Clamp((float)fw.UpdateDelta.TotalSeconds, 0f, 0.1f);

            // Scan for hook conflicts once after all plugins have loaded
            if (!hookSafetyScanned)
            {
                hookSafetyScanned = true;
                hookSafetyChecker.Scan();
            }

            // Validate selected NPCs still exist
            npcSelector.Tick();

            // Process queued spawns and pending draw enables (works outside combat)
            npcSpawner.Tick();
            companionManager.Tick(deltaTime, npcSelector.SelectedNpcs);
            ProcessPendingGlamourerApplies();

            animationController.Tick(deltaTime);

            // Camera controllers run independently of combat
            deathCamController.Tick(deltaTime);
            activeCameraController.Tick(deltaTime);

            // Target lock upkeep (drops dead/stale locks, clears when inactive).
            // Runs every frame so the lock is cleared the moment the sim stops.
            playerTargetController.Tick(deltaTime);

            // Action Mode loop (player combo/guard + enemy telegraphs). Runs every
            // frame so guard windows and toggle-off cleanup are handled even
            // when the sim is not active; gates internally on config.ActionMode.
            actionModeController.Tick(deltaTime);

            // Hit feedback (camera punch decay + hitstop restore). Runs every frame.
            hitFeedbackController.Tick(deltaTime);

            if (!combatEngine.IsActive)
                return;

            // Safety: verify UseAction hook is still healthy.
            // If the hook is gone, actions would pass through to the server unintercepted.
            if (!useActionHook.IsHealthy)
            {
                log.Error("UseAction hook is no longer healthy — emergency stopping simulation to prevent server packets.");
                chatGui.PrintError("[CombatSim] SAFETY: UseAction hook failed. Simulation stopped to prevent server communication.");
                combatEngine.StopSimulation();
                return;
            }

            mapEnemyController.Tick(deltaTime);
            combatEngine.Tick(deltaTime);
            npcAiController.Tick(deltaTime, npcSelector.SelectedNpcs);
            victorySequenceController.Tick(deltaTime);
            executionModeController.Tick(deltaTime, npcSelector.SelectedNpcs);

            // Dev: apply NPC scale override via DrawObject transform
            if (config.DevNpcScale != 1.0f)
            {
                var s = config.DevNpcScale;
                foreach (var npc in npcSelector.SelectedNpcs)
                {
                    if (npc.BattleChara == null) continue;
                    var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                    if (gameObj->DrawObject == null) continue;
                    gameObj->DrawObject->Scale = new Vector3(s, s, s);
                    gameObj->DrawObject->NotifyTransformChanged();
                }
            }

            // Dev: hide NPCs that block the active camera's view of the player
            TickNpcOcclusionHide();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in framework update, stopping simulation.");
            combatEngine.StopSimulation();
        }
    }

    private SimulatedNpc? FindNpcByAddress(nint address)
    {
        if (address == nint.Zero) return null;
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address == address && npc.BattleChara != null) return npc;
        return null;
    }

    private void OnNpcDeathRagdoll(nint address)
    {
        // Fighting camera death transition must run regardless of ragdoll settings.
        activeCameraController.NotifyCombatantDeath(address, isPlayer: false);

        // Note: KO strip is intentionally player-only — NPC draw objects vary too much
        // (non-humanoid, partial gear) to strip reliably, so it's not applied here.

        if (!config.EnableRagdoll || !config.EnableNpcDeathRagdoll)
        {
            return;
        }

        // Weapon drop is part of ragdoll — same activation delay.
        weaponDropController.SpawnFor(address, config.NpcRagdollActivationDelay);

        if (npcRagdolls.ContainsKey(address)) return;

        log.Info($"NPC death ragdoll: activating for 0x{address:X}");

        var partyMode = config.EnableCombatCompanions;
        var controller = partyMode
            ? new RagdollController(boneTransformService, npcSelector, movementBlockHook, config, log, GetPartyCollisionAddresses)
            : new RagdollController(boneTransformService, config, log);

        IReadOnlyList<string> bones = Array.Empty<string>();
        if (config.EnableEnemyDismemberment)
        {
            bones = dismembermentController.SelectRandomEnemyBones(
                address,
                config.EnemyHumanoidDismembermentCount,
                config.EnemyMonsterDismembermentBonePercent);
        }

        controller.SetDismemberedBones(bones);
        if (bones.Count > 0)
        {
            foreach (var bone in bones)
                dismembermentController.SpawnFor(address, bone, config.NpcRagdollActivationDelay, null);
            log.Info($"NPC death ragdoll: selected {bones.Count} visual parts for 0x{address:X}");
        }

        controller.Activate(address, config.NpcRagdollActivationDelay);
        npcRagdolls[address] = controller;
    }

    private sealed class GlamourerApplyJob
    {
        public Companions.CombatCompanion Companion = null!;
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
    private void ApplyCompanionGlamourer(Companions.CombatCompanion companion)
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

    private void ProcessPendingGlamourerApplies()
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

    private void OnCompanionDeathRagdoll(nint address)
    {
        if (!config.EnableRagdoll || !config.PartyCompanionDeathRagdoll) return;

        // Weapon drop is part of ragdoll — same activation delay.
        weaponDropController.SpawnFor(address, config.RagdollActivationDelay);

        if (npcRagdolls.ContainsKey(address)) return;

        log.Info($"Companion death ragdoll: activating for 0x{address:X}");

        var controller = new RagdollController(boneTransformService, npcSelector, movementBlockHook, config, log, GetPartyCollisionAddresses);
        controller.SetDismemberedBones(Array.Empty<string>());
        controller.Activate(address, config.RagdollActivationDelay);
        npcRagdolls[address] = controller;
    }

    /// <summary>
    /// Collision actors for a party-mode ragdoll: the local player plus every spawned
    /// companion. Enemies are supplied separately via the NpcSelector, and each ragdoll
    /// skips its own body, so this makes every ragdoll collide with every living
    /// character (party↔party, party↔enemy, enemy↔party, and the player).
    /// </summary>
    private IReadOnlyList<nint> GetPartyCollisionAddresses()
    {
        var list = new List<nint>();

        var player = Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address != nint.Zero)
        {
            list.Add(player.Address);
            if (config.RagdollNpcCollision)
            {
                AddMountCollisionAddresses(list);
            }
        }

        foreach (var companion in companionManager.Companions)
        {
            if (companion.IsSpawned && companion.Address != nint.Zero)
                list.Add(companion.Address);
        }

        return list;
    }

    private void AddMountCollisionAddresses(List<nint> list)
    {
        foreach (var obj in Services.ObjectTable)
        {
            if (obj == null || obj.Address == nint.Zero)
                continue;
            if (obj.ObjectKind != ObjectKind.Mount)
                continue;

            list.Add(obj.Address);
        }
    }

    private void RemoveNpcRagdoll(nint address)
    {
        if (npcRagdolls.TryGetValue(address, out var controller))
        {
            controller.Dispose();
            npcRagdolls.Remove(address);
        }
    }

    private void DeactivateAllNpcRagdolls()
    {
        foreach (var controller in npcRagdolls.Values)
            controller.Dispose();
        npcRagdolls.Clear();
    }

    private void TickNpcOcclusionHide()
    {
        if (!config.DevNpcOcclusionHide || !config.EnableActiveCamera)
        {
            // Feature disabled — restore any hidden NPCs
            RestoreOcclusionHiddenNpcs();
            return;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
        {
            RestoreOcclusionHiddenNpcs();
            return;
        }

        var cp = camMgr->Camera->LastPosition;
        var la = camMgr->Camera->LastLookAtVector;
        var camPos = new Vector3(cp.X, cp.Y, cp.Z);
        var lookAt = new Vector3(la.X, la.Y, la.Z);
        var camToTarget = lookAt - camPos;
        var lineLen = camToTarget.Length();
        if (lineLen < 0.01f) return;
        var lineDir = camToTarget / lineLen;

        var cinematicAddr = victorySequenceController.CinematicNpcAddress;
        var threshold = config.DevNpcOcclusionRadius;

        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            var addr = npc.Address;

            // Never hide the cinematic (grabbing) NPC
            if (addr == cinematicAddr) continue;

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            if (gameObj->DrawObject == null) continue;

            // Check multiple heights along the NPC body (feet, knees, chest, head)
            var baseX = gameObj->Position.X;
            var baseY = gameObj->Position.Y;
            var baseZ = gameObj->Position.Z;
            bool blocking = false;

            for (float yOff = 0.0f; yOff <= 1.8f; yOff += 0.45f)
            {
                var samplePos = new Vector3(baseX, baseY + yOff, baseZ);
                var camToSample = samplePos - camPos;
                var t = Vector3.Dot(camToSample, lineDir);

                if (t < 0f || t > lineLen)
                    continue;

                var closestPoint = camPos + lineDir * t;
                var perpDist = Vector3.Distance(samplePos, closestPoint);

                if (perpDist < threshold)
                {
                    blocking = true;
                    break;
                }
            }

            if (blocking)
            {
                // Blocking — hide
                if (occlusionHiddenNpcs.Add(addr))
                    gameObj->DrawObject->IsVisible = false;
            }
            else
            {
                // Not blocking — restore if hidden
                if (occlusionHiddenNpcs.Remove(addr))
                    gameObj->DrawObject->IsVisible = true;
            }
        }
    }

    private void RestoreOcclusionHiddenNpcs()
    {
        if (occlusionHiddenNpcs.Count == 0) return;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            if (!occlusionHiddenNpcs.Contains(npc.Address)) continue;
            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            if (gameObj->DrawObject != null)
                gameObj->DrawObject->IsVisible = true;
        }
        occlusionHiddenNpcs.Clear();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        // Despawn client-spawned NPCs first (they don't survive zone changes)
        npcSpawner.SpawnModeActive = false;
        if (npcSpawner.SpawnedNpcs.Count > 0)
        {
            npcSpawner.DespawnAll();
            log.Info("Despawned all client NPCs on zone change.");
        }

        DeactivateAllNpcRagdolls();
        weaponDropController.RemoveAll();
        dismembermentController.RemoveAll();
        koStripController.Reset();
        monsterModeController.Despawn();

        if (combatEngine.IsActive)
        {
            log.Info($"Territory changed to {territoryId} — auto-stopping combat simulation.");
            chatGui.Print("[CombatSim] Zone changed. Combat simulation stopped.");
            combatEngine.StopSimulation();
        }
    }

    /// <summary>
    /// Tear down all world-bound state when the player logs out (returns to the
    /// title / character-select screen). Wrapped defensively because the game is
    /// already destroying the world at this point.
    /// </summary>
    private void HandleLoggedOut()
    {
        try
        {
            npcSpawner.SpawnModeActive = false;
            DeactivateAllNpcRagdolls();
            weaponDropController.RemoveAll();
            dismembermentController.RemoveAll();
            koStripController.Reset();
            monsterModeController.Despawn();
            npcSpawner.DespawnAll();
            companionManager.DespawnAll();
            if (combatEngine.IsActive)
                combatEngine.StopSimulation();
            log.Info("Logged out — combat simulation state cleared.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error cleaning up combat state on logout.");
        }
    }
}
