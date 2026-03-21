using System;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using CombatSimulator.Gui;
using CombatSimulator.Npcs;
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
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly ActionDataProvider actionDataProvider;
    private readonly DamageCalculator damageCalculator;
    private readonly ChatCommandExecutor chatCommandExecutor;
    private readonly AnimationController animationController;
    private readonly CombatEngine combatEngine;
    private readonly NpcAiController npcAiController;
    private readonly UseActionHook useActionHook;
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
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
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
        animationController = new AnimationController(log, clientState, dataManager, chatCommandExecutor, config);
        npcSelector = new NpcSelector(objectTable, targetManager, log);
        combatEngine = new CombatEngine(
            actionDataProvider, damageCalculator, animationController,
            npcSelector, clientState, log);
        npcAiController = new NpcAiController(combatEngine, animationController, clientState, log);

        // Safety — enable hook immediately; it already gates on combatEngine.IsActive
        useActionHook = new UseActionHook(gameInterop, combatEngine, npcSelector, config, clientState, log);
        useActionHook.Enable();

        // GUI
        mainWindow = new MainWindow(config, npcSelector, combatEngine, chatGui, log);
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

        log.Info("Combat Simulator loaded.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        commandManager.RemoveHandler(CommandName);

        combatEngine.StopSimulation();
        npcSelector.DeselectAll();

        useActionHook.Dispose();
        combatLogWindow.Dispose();
        hpBarOverlay.Dispose();
        mainWindow.Dispose();
        npcAiController.Dispose();
        combatEngine.Dispose();
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
        if (config.ShowMainWindow)
            mainWindow.Draw();

        if (combatEngine.IsActive)
        {
            if (config.ShowEnemyHpBar || config.ShowPlayerHpBar || config.ShowHudPlayerHpBar)
                hpBarOverlay.Draw();

            if (config.ShowCombatLog)
                combatLogWindow.Draw();
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

            if (!combatEngine.IsActive)
                return;

            var deltaTime = (float)(1.0 / 60.0);

            combatEngine.Tick(deltaTime);
            npcAiController.Tick(deltaTime, npcSelector.SelectedNpcs);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in framework update, stopping simulation.");
            combatEngine.StopSimulation();
        }
    }
}
