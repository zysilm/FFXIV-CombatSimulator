using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Core;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
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
    private readonly DamageCalculator damageCalculator;
    private readonly ChatCommandExecutor chatCommandExecutor;
    private readonly GlamourerIpc glamourerIpc;
    private readonly AnimationController animationController;
    private readonly BoneTransformService boneTransformService;
    private readonly RagdollController ragdollController;
    private readonly CombatEngine combatEngine;
    private readonly NpcAiController npcAiController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly UseActionHook useActionHook;
    private readonly DeathCamController deathCamController;
    private readonly ActiveCameraController activeCameraController;
    private readonly Dev.VictorySequenceController victorySequenceController;
    private readonly HookSafetyChecker hookSafetyChecker;

    // Dev: NPCs hidden by occlusion check (need to restore visibility on cleanup)
    private readonly HashSet<nint> occlusionHiddenNpcs = new();
    private readonly MainWindow mainWindow;
    private readonly HpBarOverlay hpBarOverlay;
    private readonly CombatLogWindow combatLogWindow;
    private readonly RagdollDebugOverlay ragdollDebugOverlay;
    private bool hookSafetyScanned;

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
        actionDataProvider = new ActionDataProvider(dataManager, log);
        damageCalculator = new DamageCalculator();
        chatCommandExecutor = new ChatCommandExecutor(log);
        glamourerIpc = new GlamourerIpc(pluginInterface, log);
        movementBlockHook = new MovementBlockHook(gameInterop, clientState, log);
        animationController = new AnimationController(log, clientState, dataManager, sigScanner, chatCommandExecutor, config);
        boneTransformService = new BoneTransformService(gameInterop, sigScanner, log);
        npcSelector = new NpcSelector(objectTable, targetManager, config, log);
        npcSpawner = new NpcSpawner(objectTable, dataManager, log);
        ragdollController = new RagdollController(boneTransformService, npcSelector, movementBlockHook, config, log);
        deathCamController = new DeathCamController(gameInterop, clientState, sigScanner, config, log);
        activeCameraController = new ActiveCameraController(gameInterop, clientState, sigScanner, config, log);
        victorySequenceController = new Dev.VictorySequenceController(
            boneTransformService, animationController.EmotePlayer,
            movementBlockHook, ragdollController, clientState, targetManager, config, log);
        combatEngine = new CombatEngine(
            actionDataProvider, damageCalculator, animationController,
            glamourerIpc, movementBlockHook, ragdollController,
            config, npcSelector, clientState, log, deathCamController,
            victorySequenceController);
        npcAiController = new NpcAiController(combatEngine, animationController, movementBlockHook, clientState, config, log);

        // Wire NpcSpawner callbacks
        npcSpawner.OnNpcSpawnComplete = (npc) =>
        {
            npcSelector.RegisterSpawnedNpc(npc);
            log.Info($"Spawned NPC '{npc.Name}' registered as combat target.");
        };
        npcSpawner.OnSpawnError = (msg) =>
        {
            chatGui.PrintError($"[CombatSim] Spawn error: {msg}");
        };

        // Re-register alive spawned NPCs when simulation starts
        // (they get removed from npcSelector on StopSimulation via DeselectAll)
        combatEngine.OnSimulationStarted = () =>
        {
            foreach (var npc in npcSpawner.SpawnedNpcs)
            {
                if (npc.IsSpawned && npc.BattleChara != null)
                {
                    npcSelector.RegisterSpawnedNpc(npc);
                }
            }
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
        useActionHook = new UseActionHook(gameInterop, combatEngine, npcSelector, config, clientState, log);
        useActionHook.Enable();
        movementBlockHook.Enable();

        // Restore active camera if it was enabled in config
        if (config.EnableActiveCamera)
            activeCameraController.SetActive(true);

        // GUI
        mainWindow = new MainWindow(config, npcSelector, npcSpawner, combatEngine, glamourerIpc, animationController, ragdollController, deathCamController, activeCameraController, hookSafetyChecker, clientState, dataManager, chatGui, log);
        hpBarOverlay = new HpBarOverlay(npcSelector, combatEngine, boneTransformService, gameGui, clientState, config);
        combatLogWindow = new CombatLogWindow(combatEngine);
        ragdollDebugOverlay = new RagdollDebugOverlay(ragdollController, mainWindow, config, gameGui, clientState);

        // Register
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Combat Simulator configuration.\n" +
                          "/combatsim start - Start combat simulation\n" +
                          "/combatsim stop - Stop combat simulation\n" +
                          "/combatsim reset - Reset combat state",
        });

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        log.Info("Combat Simulator loaded.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        commandManager.RemoveHandler(CommandName);

        RestoreOcclusionHiddenNpcs();
        npcSpawner.DespawnAll();
        npcSpawner.Dispose();
        combatEngine.StopSimulation();
        npcSelector.DeselectAll();

        useActionHook.Dispose();
        movementBlockHook.Dispose();
        combatLogWindow.Dispose();
        hpBarOverlay.Dispose();
        mainWindow.Dispose();
        npcAiController.Dispose();
        combatEngine.Dispose();
        victorySequenceController.Dispose();
        ragdollController.Dispose();
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

            default:
                config.ShowMainWindow = !config.ShowMainWindow;
                config.Save();
                break;
        }
    }

    private void OnDraw()
    {
        if (config.ShowShortcuts)
            mainWindow.DrawShortcutsBar();

        if (config.ShowDeathCamToolbar)
            mainWindow.DrawDeathCamToolbar();

        if (config.ShowActiveCamToolbar)
            mainWindow.DrawActiveCamToolbar();

        if (config.ShowGrabToolbar)
            mainWindow.DrawGrabToolbar(victorySequenceController);

        if (config.ShowMainWindow)
            mainWindow.Draw();

        if (combatEngine.IsActive)
        {
            if (config.ShowEnemyHpBar || config.ShowPlayerHpBar || config.ShowHudPlayerHpBar)
                hpBarOverlay.Draw();

            if (config.ShowCombatLog)
                combatLogWindow.Draw();
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
        config.ShowMainWindow = true;
        config.Save();
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
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

            var deltaTime = (float)(1.0 / 60.0);

            // Camera controllers run independently of combat
            deathCamController.Tick(deltaTime);
            activeCameraController.Tick(deltaTime);

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

            combatEngine.Tick(deltaTime);
            npcAiController.Tick(deltaTime, npcSelector.SelectedNpcs);
            victorySequenceController.Tick(deltaTime);

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

    private void OnTerritoryChanged(ushort territoryId)
    {
        // Despawn client-spawned NPCs first (they don't survive zone changes)
        if (npcSpawner.SpawnedNpcs.Count > 0)
        {
            npcSpawner.DespawnAll();
            log.Info("Despawned all client NPCs on zone change.");
        }

        if (combatEngine.IsActive)
        {
            log.Info($"Territory changed to {territoryId} — auto-stopping combat simulation.");
            chatGui.Print("[CombatSim] Zone changed. Combat simulation stopped.");
            combatEngine.StopSimulation();
        }
    }
}
