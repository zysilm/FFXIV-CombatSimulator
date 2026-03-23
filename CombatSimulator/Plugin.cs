using System;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Core;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Physics;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

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
    private readonly ActionDataProvider actionDataProvider;
    private readonly DamageCalculator damageCalculator;
    private readonly ChatCommandExecutor chatCommandExecutor;
    private readonly GlamourerIpc glamourerIpc;
    private readonly AnimationController animationController;
    private readonly CombatEngine combatEngine;
    private readonly NpcAiController npcAiController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly UseActionHook useActionHook;
    private readonly BoneManipulator boneManipulator;
    private readonly RagdollController ragdollController;
    private readonly DeathCamController deathCamController;
    private readonly MainWindow mainWindow;
    private readonly HpBarOverlay hpBarOverlay;
    private readonly CombatLogWindow combatLogWindow;

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
        actionDataProvider = new ActionDataProvider(dataManager);
        damageCalculator = new DamageCalculator();
        chatCommandExecutor = new ChatCommandExecutor(log);
        glamourerIpc = new GlamourerIpc(pluginInterface, log);
        movementBlockHook = new MovementBlockHook(gameInterop, clientState, log);
        animationController = new AnimationController(log, clientState, dataManager, sigScanner, chatCommandExecutor, config);
        npcSelector = new NpcSelector(objectTable, targetManager, config, log);
        boneManipulator = new BoneManipulator(gameInterop, sigScanner, log);
        ragdollController = new RagdollController(boneManipulator, config, clientState, log);
        deathCamController = new DeathCamController(gameInterop, clientState, sigScanner, config, log);
        combatEngine = new CombatEngine(
            actionDataProvider, damageCalculator, animationController,
            glamourerIpc, movementBlockHook, config, npcSelector, clientState, log,
            deathCamController, ragdollController);
        npcAiController = new NpcAiController(combatEngine, animationController, movementBlockHook, clientState, config, log);

        // Safety — enable hooks immediately; they gate on internal state
        useActionHook = new UseActionHook(gameInterop, combatEngine, npcSelector, config, clientState, log);
        useActionHook.Enable();
        movementBlockHook.Enable();

        // GUI
        mainWindow = new MainWindow(config, npcSelector, combatEngine, glamourerIpc, animationController, deathCamController, ragdollController, chatGui, log);
        hpBarOverlay = new HpBarOverlay(npcSelector, combatEngine, gameGui, clientState, config);
        combatLogWindow = new CombatLogWindow(combatEngine);

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

        combatEngine.StopSimulation();
        npcSelector.DeselectAll();

        useActionHook.Dispose();
        movementBlockHook.Dispose();
        combatLogWindow.Dispose();
        hpBarOverlay.Dispose();
        mainWindow.Dispose();
        npcAiController.Dispose();
        combatEngine.Dispose();
        ragdollController.Dispose();
        boneManipulator.Dispose();
        deathCamController.Dispose();
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

        if (config.ShowMainWindow)
            mainWindow.Draw();

        if (combatEngine.IsActive)
        {
            if (config.ShowEnemyHpBar || config.ShowPlayerHpBar || config.ShowHudPlayerHpBar)
                hpBarOverlay.Draw();

            if (config.ShowCombatLog)
                combatLogWindow.Draw();
        }
        else
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
            // Validate selected NPCs still exist
            npcSelector.Tick();

            var deltaTime = (float)(1.0 / 60.0);

            // Death cam preview runs independently of combat
            deathCamController.Tick(deltaTime);

            // Ragdoll physics tick (runs independently of combat for settle timer)
            ragdollController.Tick(deltaTime);

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
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in framework update, stopping simulation.");
            combatEngine.StopSimulation();
        }
    }

    private void OnTerritoryChanged(ushort territoryId)
    {
        if (combatEngine.IsActive)
        {
            log.Info($"Territory changed to {territoryId} — auto-stopping combat simulation.");
            chatGui.Print("[CombatSim] Zone changed. Combat simulation stopped.");
            combatEngine.StopSimulation();
        }
    }
}
