// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Composition;
using CombatSimulator.Core;
using CombatSimulator.Fighting;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Spectators;
using CombatSimulator.Targeting;
using CombatSimulator.Voice;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator;

/// <summary>
/// The composition root (docs/PLUGIN_ARCHITECTURE.md §6). The plugin's objects live in feature modules
/// (<see cref="Composition.PluginModules"/>: World, Actors, Corpse, Camera, Combat, Fighting, Battle,
/// Presentation, Safety/Dev); this class keeps what only it can do:
/// <list type="bullet">
/// <item>the build list (the constructor): every module build step in the one order the game objects and
/// hooks have always been created, and between them the subscriptions to the engine's notifications
/// (<see cref="CombatEvents"/>) at the points they were always made;</item>
/// <item>the host's events (draw, the two UI openers, framework update, territory change) and the slash command;</item>
/// <item>the per-frame list (<see cref="OnFrameworkUpdate"/>, §6.4), written out in one place;</item>
/// <item>Dispose, in the order it always ran;</item>
/// <item>the private module's partial-method extension points, and the members it reads by name
/// (forwarding properties to the module that now owns each object; §6.6).</item>
/// </list>
/// </summary>
public sealed unsafe partial class CombatSimulatorPlugin : IDalamudPlugin
{
    private const string CommandName = "/combatsim";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly PluginModules modules;
    private readonly Dev.IDevExperimental devExperimental;
    partial void InitPrivateDirectors();
    partial void TickPrivateDirectors(float deltaTime);
    partial void ResetPrivateDirectors();
    partial void DisposePrivateDirectors();
    partial void GetPrivateDirectorActive(ref bool active);
    private bool PrivateDirectorActive
    {
        get { var active = false; GetPrivateDirectorActive(ref active); return active; }
    }
    partial void InitPrivateEffects();
    partial void AttachPrivateEffects();
    partial void TickPrivateEffects(float deltaTime);
    partial void TickPrivateCommandBridge();
    partial void OnPrivateEffectsKo();
    partial void ClearPrivateEffects();
    partial void DisposePrivateEffects();
    partial void TryPrivateCommand(string[] parts, ref bool handled);

    private bool hookSafetyScanned;
    private bool wasLoggedIn;
    private bool wasDevExperimentalUnlocked;

    // ---- The feature modules (docs/PLUGIN_ARCHITECTURE.md §6.1) ----
    private WorldModule worldModule => modules.World;
    private ActorsModule actorsModule => modules.Actors;
    private CorpseModule corpseModule => modules.Corpse;
    // Kept by name for the optional module's partial code (it lists the active corpse ragdolls).
    private IReadOnlyList<RagdollController> GetActiveCorpseRagdolls() => corpseModule.GetActiveCorpseRagdolls();
    private CameraModule cameraModule => modules.Camera;
    private CombatModule combatModule => modules.Combat;
    private FightingModule fightingModule => modules.Fighting;
    private BattleModule battleModule => modules.Battle;
    private PresentationModule presentationModule => modules.Presentation;
    private SafetyDevModule safetyDevModule => modules.SafetyDev;
    /// <summary>The engine's notifications; subscribed only in the constructor below (§6.3).</summary>
    private CombatEvents combatEvents => combatModule.Events;

    // ---- The plugin's objects, under the names the plugin class (and the private module's partial code)
    // has always used for them. Each forwards to the module that owns the object now (§6.6), and reads
    // null before that module has built it, as the field did before it was assigned. ----
    // World
    private GlamourerIpc glamourerIpc => worldModule.Glamourer;
    private VNavmeshIpc vnavmeshIpc => worldModule.VNavmesh;
    private MovementBlockHook movementBlockHook => worldModule.MovementBlock;
    private AnimationController animationController => worldModule.Animation;
    private BoneTransformService boneTransformService => worldModule.BoneTransforms;
    private Rendering.WorldGeometry.WorldGeometryRenderer worldGeometryRenderer => worldModule.GeometryRenderer;
    private Rendering.WorldGeometry.WorldGeometryPreview worldGeometryPreview => worldModule.GeometryPreview;
    private Navigation.NavMeshService navMesh => worldModule.NavMesh;
    private Navigation.CrowdMover crowdMover => worldModule.Crowd;
    public Rendering.WorldGeometry.WorldGeometryRenderer WorldGeometry => worldGeometryRenderer;
    // Actors
    private ActionDataProvider actionDataProvider => actorsModule.ActionData;
    private NpcSelector npcSelector => actorsModule.Selector;
    private NpcSpawner npcSpawner => actorsModule.Spawner;
    private Effects.NpcScaleController npcScaleController => actorsModule.Scale;
    private CombatCompanionManager companionManager => actorsModule.Companions;
    private SpectatorController spectatorController => actorsModule.Spectators;
    private MapEnemyController mapEnemyController => actorsModule.MapEnemies;
    // Corpse
    private RagdollController ragdollController => corpseModule.PlayerRagdoll;
    private WeaponDropController weaponDropController => corpseModule.WeaponDrop;
    private DismembermentController dismembermentController => corpseModule.Dismemberment;
    private Dev.KoStripController armorDetachmentController => corpseModule.ArmorDetachment;
    private Effects.ArrowImpactController arrowImpactController => corpseModule.ArrowImpacts;
    private Effects.PlayerWeaponVisibilityController playerWeaponVisibilityController => corpseModule.PlayerWeaponVisibility;
    private IReadOnlyDictionary<nint, RagdollController> npcRagdolls => corpseModule.NpcRagdolls;
    // Camera
    private CameraModeCoordinator cameraModeCoordinator => cameraModule.Coordinator;
    private GameCameraUpdateHook gameCameraUpdateHook => cameraModule.UpdateHook;
    private ActiveCameraController activeCameraController => cameraModule.Active;
    private DynamicCameraController dynamicCameraController => cameraModule.Dynamic;
    // Combat
    private CombatEngine combatEngine => combatModule.Engine;
    private CombatSimulator.ActionCombat.ActionComboSink actionComboSink => combatModule.ComboSink;
    private CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter => combatModule.Router;
    private CombatSimulator.ActionCombat.TelegraphSystem telegraphSystem => combatModule.Telegraphs;
    private CombatSimulator.ActionCombat.EnemyAreaMarkers enemyAreaMarkers => combatModule.AreaMarkers;
    private CombatSimulator.ActionCombat.PlayerGuardController playerGuardController => combatModule.Guard;
    private CombatSimulator.ActionCombat.ActionModeController actionModeController => combatModule.ActionMode;
    private CombatSimulator.ActionCombat.HitFeedbackController hitFeedbackController => combatModule.HitFeedback;
    private NpcAiController npcAiController => combatModule.EnemyAi;
    private NpcCorpseFootIkSolver npcCorpseFootIkSolver => combatModule.CorpseFootIk;
    private VirtualEnemyLookAtController virtualEnemyLookAt => combatModule.EnemyLookAt;
    private PlayerTargetController playerTargetController => combatModule.PlayerTarget;
    private UseActionHook useActionHook => combatModule.UseAction;
    // Fighting / Enemy Control
    private EnemyControlController enemyControlController => fightingModule.EnemyControl;
    private FightingModeController fightingModeController => fightingModule.Mode;
    private FightingCombatController fightingCombatController => fightingModule.Combat;
    // Battle
    private VictoryCelebrationDirector? victoryCelebration => battleModule.VictoryCelebration;
    /// <summary>Exposed for private directors (stories) that hand actors in and out of it.</summary>
    internal VictoryCelebrationDirector? VictoryCelebration => victoryCelebration;
    // Presentation
    private MainWindow mainWindow => presentationModule.MainWindow;
    private HpBarOverlay hpBarOverlay => presentationModule.HpBars;
    private Gui.DamageNumberOverlay damageNumbers => presentationModule.DamageNumbers;
    private CombatLogWindow combatLogWindow => presentationModule.CombatLog;
    private CombatSimulator.ActionCombat.EnemyOmenVfx enemyOmenVfx => presentationModule.EnemyOmens;
    private VoiceDirector voiceDirector => presentationModule.Voices;
    // Safety / Dev
    private HookSafetyChecker hookSafetyChecker => safetyDevModule.HookSafety;

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
        var hadExistingConfig = pluginInterface.ConfigFile.Exists;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // Security: Fighting Mode drives the local player's position/rotation directly
        // (lane projection, min-separation clamp, vault) and is under review for movement
        // patterns a server could flag. Force it off on every load regardless of the
        // persisted value — see update log 2.6.0.1. The UI entry is also now hidden unless
        // Dev Experimental is unlocked (see OnFrameworkUpdate / MainWindow).
        if (config.FightingMode)
        {
            config.FightingMode = false;
            config.Save();
            log.Info("Fighting Mode force-disabled on load (security).");
        }

#if DEV_EXPERIMENTAL
        // One-time default fix: mounted-death dismemberment shipped defaulting to on, firing on every
        // player death while mounted with no unlock gate — never the intent. Existing configs already
        // have it persisted as true, so the corrected default alone won't reach them; force it off once
        // per config, then leave the user's own choice (via the Dev Experimental panel) alone from then on.
        if (!config.MigratedMountedDeathDismembermentDefault)
        {
            config.EnableMountedDeathDismemberment = false;
            config.MigratedMountedDeathDismembermentDefault = true;
            config.Save();
            log.Info("Mounted-death dismemberment default corrected to off (one-time migration).");
        }

#endif
        config.SeedEnemyControlGrabProfiles();

        // ---- The build list. The order below is the order the game objects, hooks and subscriptions
        // have always been made in (docs/PLUGIN_ARCHITECTURE.md §6, docs/REFACTOR_PLAN.md §4 "must
        // preserve"); Tests/PluginOrder checks it against the code before the modules. Creating the
        // modules builds nothing. ----
        modules = new PluginModules(config, log);

        actorsModule.CreateActionData(pluginInterface, dataManager);
        combatModule.CreateDamageCalculator();
        worldModule.CreateGameAccess(pluginInterface, gameInterop, clientState, dataManager, sigScanner, config, log);
        actorsModule.AttachMonsterKits(dataManager);
        worldModule.CreateBoneTransforms(gameInterop, sigScanner, log);
        actorsModule.CreateRoster(objectTable, targetManager, dataManager, clientState);
        corpseModule.CreatePlayerBody(objectTable);
        worldModule.CreateGeometryRenderer(gameInterop, sigScanner, log);
        corpseModule.CreateArrowImpacts();
        InitPrivateEffects();
        worldModule.CreateNavigation(pluginInterface, log);
        corpseModule.CreateLimbsAndArmor(pluginInterface, objectTable);
        cameraModule.CreateCoordinator(gameInterop, clientState, sigScanner);
        combatModule.CreateEngineAndActionCombat(clientState, gamepadState);
        // Clear any live telegraphs when a combat session ends.                 (SimulationReset #1)
        combatEvents.SimulationReset += telegraphSystem.Clear;
        combatEvents.SimulationReset += enemyAreaMarkers.Clear;                 // (#2)
        // Release any outstanding hitstop freezes when combat ends so no enemy is left frozen. (#3)
        combatEvents.SimulationReset += hitFeedbackController.Clear;
        // Super-armor enemies during their telegraph windup (no flinch from player hits).
        combatEngine.IsTargetSuperArmored = telegraphSystem.IsWindingUp;

        fightingModule.CreateEnemyControl(keyState, gamepadState, framework);

        // The remaining experimental ("easter-egg") dev features live in one module (Victory/Hold +
        // dev-only per-frame tweaks). The engine drives the cinematic victory through the
        // IVictorySequence seam.
#if DEV_EXPERIMENTAL
        devExperimental = new Dev.Experimental.DevExperimentalModule(
            ragdollController, animationController, boneTransformService,
            movementBlockHook, vnavmeshIpc, dismembermentController, glamourerIpc,
            armorDetachmentController, combatEngine, clientState, targetManager, npcSelector,
            config, log, weaponDropController);
#else
        devExperimental = new Dev.DevExperimentalStub();
#endif
        safetyDevModule.WireDevExperimental(devExperimental);
        actorsModule.CreateCompanions(objectTable, clientState, targetManager);
        combatModule.WireCompanions();
        battleModule.CreateVictoryCelebration();
        // Crowd reactions to the result; React() is a no-op unless SpectatorReactionEmotes.
        // spectatorController is constructed after this runs, hence the late lookup.
        // ('=' as always: the first handler of the outcome replaces anything set before it.) (OutcomeDecided #1)
        combatEvents.OutcomeDecided = playerWon => spectatorController?.React(
            playerWon ? Spectators.SpectatorReaction.PlayerVictory : Spectators.SpectatorReaction.PlayerDefeat);
        actorsModule.CreateSpectators(clientState);
        // NpcAiController's constructor subscribes its own SimulationStarted #1 and SimulationReset #4.
        combatModule.CreateEnemyAi();
        combatModule.CreatePlayerTargeting(gameInterop, objectTable, gameGui);
        combatEvents.PlayerHitBy = playerTargetController.NotifyPlayerHitBy;
        cameraModule.CreateDynamic(sigScanner);
        actorsModule.CreateMapEnemies(objectTable);
        fightingModule.CreateFighting(gamepadState);
        combatEvents.BeforePlayerDeath = () =>
        {
            fightingModeController.HandlePlayerDeath();
            // The killing blow was recorded when it landed; deaths resolve on a delay, so by
            // now the engine knows exactly who to frame alongside the body.
            dynamicCameraController.HandlePlayerDeath(
                combatEngine.LastPlayerKillerAddress, combatEngine.LastPlayerKillerEntityId);
            devExperimental.BeforePlayerDeath();
        };
        actorsModule.WireActorCallbacks(chatGui);

        // Re-register alive spawned NPCs on sim start (they're removed from
        // npcSelector by DeselectAll during StopSimulation). For real
        // first-time starts this just re-populates the selector so
        // NpcAiController can tick the existing spawns.
        combatEvents.SimulationStarted += cameraModule.SwitchToDirectedCameraForBattle;   // (SimulationStarted #2)
        // (Reset also fires on Stop, when the simulation is no longer active: only a reset mid-battle counts.) (SimulationReset #5)
        combatEvents.SimulationReset += () =>
        {
            if (combatEngine.State.IsActive)
                cameraModule.SwitchToDirectedCameraForBattle();
        };
        combatEvents.SimulationStarted += () =>                                            // (SimulationStarted #3)
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
        // them first to avoid dangling pointers.                                 (SimulationReset #6)
        combatEvents.SimulationReset += () =>
        {
            ResetPrivateDirectors();
            corpseModule.DeactivateAllNpcRagdolls();
            weaponDropController.RemoveAll();
            dismembermentController.RemoveAll();
            armorDetachmentController.Reset();
            npcScaleController.Reset();
            devExperimental.ResetTransientState();
            enemyControlController.Despawn();
            fightingModeController.Reset();
            dynamicCameraController.Reset();

            // Keep companions across a combat *reset* (IsActive stays true) — revive/heal
            // them instead of despawning. Stopping the simulation (IsActive false) always despawns.
            if (combatEngine.State.IsActive)
                companionManager.ResetForCombatReset();
            else
                companionManager.DespawnAll();

            if (npcSpawner.SpawnedNpcs.Count == 0) return;

            log.Info($"Sim reset — despawning and re-queuing {npcSpawner.SpawnedNpcs.Count} virtual enemies.");

            foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
                npcSelector.UnregisterSpawnedNpc(npc);

            npcSpawner.RegenerateAll();
        };

        combatEvents.EnemyDied = corpseModule.OnNpcDeathRagdoll;
        combatEvents.PlayerDamaged = () =>
        {
            var player = objectTable.LocalPlayer;
            if (player != null && player.Address != nint.Zero)
                armorDetachmentController.TryStripNextOnHit(player.Address);
        };
        combatEvents.PlayerDied = addr =>
        {
            OnPrivateEffectsKo();
            weaponDropController.SpawnFor(addr); // at the moment of death, not after the ragdoll delay
            armorDetachmentController.StripOnKo(addr);
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
            devExperimental.OnPlayerDeath(addr);

            // Under Warring States with Retreat the lord's body is fought over: an enemy takes it with
            // Enemy Control's grab only if it reaches it (the corpse seizure rule), not the moment the lord falls.
            var bodyFoughtOver = companionManager.RuleParts.SuppressesKillerPossession();
            if (bodyFoughtOver)
                log.Info("EnemyControl: the lord's body is fought over; nobody takes it yet.");
            else if (config.EnemyControlControlKiller)
            {
                var killer = actorsModule.FindNpcByAddress(combatEngine.LastPlayerKillerAddress);
                if (killer != null) enemyControlController.ControlKiller(killer);
                else log.Info("EnemyControl: no valid killer to control.");
            }
            else if (config.EnemyControlSpawnOnDeath)
            {
                enemyControlController.Spawn();
            }
        };

        safetyDevModule.CreateHookSafetyChecker(pluginInterface);

        // Safety — enable hooks immediately; they gate on internal state
        combatModule.CreateUseActionHook(gameInterop, clientState);
        useActionHook.Enable();
        movementBlockHook.Enable();

        activeCameraController.SetActive(config.EnableActiveCamera);

        // GUI
        presentationModule.CreateMainWindow(clientState, dataManager, chatGui);
        // Enemy voices. Subscriptions are additive (+=) and registered after the constructor's own
        // assignments, so they never replace another listener.
        presentationModule.CreateVoice(dataManager);
        combatModeRouter.AttackCommitted += (npc, request) => voiceDirector.NotifyAttackCommitted(npc, request);
        combatEvents.SimulationReset += voiceDirector.Reset;                     // (SimulationReset #7)
        // Stories speak through the same bubbles; enemy lines wait until the scene is over.
        voiceDirector.LinesSuppressed = () => PrivateDirectorActive;
        // Only a story's performer is held back, and only while the story runs, so it never talks
        // over its own lines. Enemy Control possesses the killer on every defeat; that actor is
        // often the last enemy standing and must still gloat. (ControlsNpc would also cover the
        // pack following it, which silenced the taunts entirely.)
        voiceDirector.IsActorReserved = addr => PrivateDirectorActive && enemyControlController.PossessesNpc(addr);
        mainWindow.VoiceDirector = voiceDirector;
        InitPrivateDirectors();
        AttachPrivateEffects();
        presentationModule.CreateHpBarsAndDamageNumbers(gameGui, clientState);
        combatEvents.DamageLanded += damageNumbers.Add;
        combatEvents.DamageLanded += hpBarOverlay.NoteDamage;
        hpBarOverlay.PlayerTarget = () => targetManager.Target?.Address ?? nint.Zero;
        combatEvents.SimulationReset += damageNumbers.Clear;                     // (SimulationReset #8)
        // The battle's end: the fight over the lord's body, then the banner after the crowd's reaction.
        battleModule.CreateCorpseSeizure();
        // A seizer struck at the body knocks the seizing back; its bar above the body.
        combatEvents.DamageLanded += battleModule.NoteDamage;
        hpBarOverlay.SeizureBar = battleModule.SeizureBar;
        // (OutcomeDecided #2: after whatever is there now, as it always was — written as a wrapper of the
        // previous handler rather than +=, exactly as before the split.)
        var previous = combatEvents.OutcomeDecided;
        combatEvents.OutcomeDecided = playerWon =>
        {
            previous?.Invoke(playerWon);
            battleModule.ShowOutcome(playerWon);
        };
        combatEvents.SimulationReset += battleModule.ResetWar;                   // (SimulationReset #9)
        presentationModule.CreateOverlays(gameGui, clientState, dataManager, hadExistingConfig);

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

        pluginInterface.UiBuilder.Draw += OnDrawTimed;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        framework.Update += OnFrameworkUpdateTimed;
        clientState.TerritoryChanged += OnTerritoryChanged;

        log.Info("Combat Simulator loaded. [virtual-enemies: humanoid clone-from-player bootstrap]");
    }

    public void Dispose()
    {
        DisposePrivateDirectors();
        Animation.BodyTypeAnimationFallback.Shutdown();
        framework.Update -= OnFrameworkUpdateTimed;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw -= OnDrawTimed;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        commandManager.RemoveHandler(CommandName);

        // Game-state cleanup touches live game objects (despawn, restore animations/visibility).
        // On game CLOSE, Dispose runs after the game has freed those objects, so doing it would
        // dereference freed memory and crash. Only do it while a session is alive (plugin unload
        // while playing); on close the game frees everything itself.
        if (Core.Services.ObjectTable.LocalPlayer != null)
        {
            devExperimental.RestoreOcclusion();
            corpseModule.DeactivateAllNpcRagdolls();
            npcSpawner.SpawnModeActive = false;
            companionManager.DespawnAll();
            spectatorController.DespawnAll();
            npcSpawner.DespawnAll();
            enemyOmenVfx.Dispose();
            combatEngine.StopSimulation();
            npcSelector.DeselectAll();
        }

        // Resource disposal (BEPU sims, hooks, event handlers) — always safe; any game-memory
        // access inside is itself guarded.
        companionManager.Dispose();
        spectatorController.Dispose();
        npcScaleController.Dispose();
        playerWeaponVisibilityController.Dispose();
        DisposePrivateEffects();
        arrowImpactController.Dispose();
        worldGeometryPreview.Dispose();
        navMesh.Dispose();
        worldGeometryRenderer.Dispose();
        npcSpawner.Dispose();
        useActionHook.Dispose();
        playerTargetController.Dispose();
        movementBlockHook.Dispose();
        combatLogWindow.Dispose();
        hpBarOverlay.Dispose();
        mainWindow.Dispose();
        virtualEnemyLookAt.Dispose();
        npcAiController.Dispose();
        npcCorpseFootIkSolver.Dispose();
        combatEngine.Dispose();
        devExperimental.Dispose();
        enemyControlController.Dispose();
        armorDetachmentController.Dispose();
        ragdollController.Dispose();
        weaponDropController.Dispose();
        dismembermentController.Dispose();
        // Any ragdoll recording still open (docs/REFACTOR_PLAN.md, stage A0) is closed with the plugin.
        RagdollRecordingSession.Stop();
        boneTransformService.Dispose();
        dynamicCameraController.Dispose();
        gameCameraUpdateHook.Dispose();
        cameraModeCoordinator.Reset();
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

        bool privateCommandHandled = false;
        TryPrivateCommand(parts, ref privateCommandHandled);
        if (privateCommandHandled) return;

        switch (parts[0].ToLowerInvariant())
        {
            case "navmesh":
                {
                    // Phase A of the movement rewrite (docs/AI_MOVEMENT_REWRITE.md): build and look at the mesh.
                    var navCommand = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "status";
                    var navPlayer = Services.ObjectTable.LocalPlayer;
                    if (navCommand == "build" && navPlayer != null)
                    {
                        navMesh.Build(navPlayer.Position, clientState.TerritoryType);
                        navMesh.ShowDebug = true;
                    }
                    else if (navCommand == "show") navMesh.ShowDebug = true;
                    else if (navCommand == "hide") navMesh.ShowDebug = false;
                    else if (navCommand == "clear") navMesh.Clear();
                    else if (navCommand == "floor" && navPlayer != null)
                    {
                        var floorText = navMesh.TryFloor(navPlayer.Position, 2f, out var floor, out _)
                            ? $"floor under you at y {floor.Y:F2} (you are at {navPlayer.Position.Y:F2})"
                            : "no floor under you within 2 m";
                        chatGui.Print($"[CombatSim] Navigation mesh: {floorText}");
                        log.Info($"NavMesh: {floorText} at {navPlayer.Position}");
                    }
                    chatGui.Print($"[CombatSim] Navigation mesh: {navMesh.Status}" + (navMesh.ShowDebug ? " (shown)" : ""));
                    break;
                }

            case "ragdollrec":
                {
                    // Stage A0 of the refactor (docs/REFACTOR_PLAN.md): record ragdoll runs for offline replay.
                    var recCommand = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "status";
                    var recText = recCommand switch
                    {
                        "start" => RagdollRecordingSession.Start(),
                        "stop" => RagdollRecordingSession.Stop(),
                        _ => RagdollRecordingSession.Status(),
                    };
                    chatGui.Print($"[CombatSim] Ragdoll recording: {recText}");
                    break;
                }

            case "geometry":
                var geometryCommand = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "status";
                if (geometryCommand == "marker" && clientState.IsLoggedIn && Services.ObjectTable.LocalPlayer is { } geometryPlayer)
                {
                    var markerOrigin = boneTransformService.GetBoneWorldPos(geometryPlayer.Address, "j_kao") ??
                        (geometryPlayer.Position + Vector3.UnitY * 1.3f);
                    worldGeometryPreview.Show(markerOrigin);
                }
                else if (geometryCommand == "clear") worldGeometryPreview.Clear();
                chatGui.Print($"[CombatSim] World geometry: {worldGeometryPreview.Status}");
                log.Info($"World geometry API preview: {worldGeometryPreview.Status}");
                break;

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

    /// <summary>The windows and overlays (<see cref="PresentationModule.OnDraw"/>).</summary>
    private void OnDraw() => presentationModule.OnDraw(clientState);

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

    /// <summary>
    /// The per-frame list (docs/PLUGIN_ARCHITECTURE.md §6.4; docs/REFACTOR_PLAN.md §4 "must preserve",
    /// item 1): every module's tick, in the one order they run. Allies (in the companion manager's tick)
    /// act before the crowd steps and run outside a battle too; enemies act after it, only in a battle;
    /// the war rules write the seizers before the enemy AI reads them in the same frame.
    /// </summary>
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
            TickPrivateCommandBridge();

            // Real wall-clock delta for all per-frame simulation. The framework fires
            // once per rendered frame at whatever framerate the game runs; using a fixed
            // 1/60 made every AI behavior (movement, attack cadence, casts, cooldowns,
            // DPS) run fast above 60fps and slow below it. Clamp out loading/hitch gaps.
            var deltaTime = Math.Clamp((float)fw.UpdateDelta.TotalSeconds, 0f, 0.1f);
            NoteFrameCost((float)fw.UpdateDelta.TotalSeconds);

            // Scan for hook conflicts once after all plugins have loaded
            if (!hookSafetyScanned)
            {
                hookSafetyScanned = true;
                hookSafetyChecker.Scan();
            }

            // Security: Fighting Mode is a hidden Dev Experimental feature (see MainWindow
            // gating). Force it off the moment Dev Experimental gets unlocked in a session,
            // so it can never be silently re-armed by a stale config value.
            var devUnlocked = mainWindow.DevExperimentalUnlocked;
            if (devUnlocked && !wasDevExperimentalUnlocked && config.FightingMode)
            {
                config.FightingMode = false;
                config.Save();
                log.Info("Fighting Mode force-disabled on Dev Experimental unlock (security).");
            }
            wasDevExperimentalUnlocked = devUnlocked;

            // Validate selected NPCs still exist
            npcSelector.Tick();

            // Process queued spawns and pending draw enables (works outside combat)
            npcSpawner.Tick();
            companionManager.Tick(deltaTime, npcSelector.SelectedNpcs);
            actorsModule.ProcessPendingGlamourerApplies();

            animationController.Tick(deltaTime);
            victoryCelebration?.Tick(deltaTime);
            armorDetachmentController.Tick(deltaTime);

            // Camera controllers run independently of combat. Submitters tick first,
            // then the coordinator resolves priority and writes the camera once, and
            // the orbit hook host reflects whether any mode supplies an orbit center.
            if (!PrivateDirectorActive) fightingModeController.Tick(deltaTime);
            dynamicCameraController.Tick(deltaTime);
            TickPrivateDirectors(deltaTime);
            gameCameraUpdateHook.Tick();
            cameraModeCoordinator.Apply(deltaTime);
            activeCameraController.SetModeActive(cameraModeCoordinator.WantsOrbitHook);
            activeCameraController.Tick(deltaTime);

            // Target lock upkeep (drops dead/stale locks, clears when inactive).
            // Runs every frame so the lock is cleared the moment the sim stops.
            playerTargetController.Tick(deltaTime);

            // Guard + telegraphs are shared by Action Mode and Fighting Mode — tick
            // them centrally so neither mode owns their lifecycle (inert when nothing
            // is active).
            if (!PrivateDirectorActive)
            {
                playerGuardController.Tick(deltaTime);
                telegraphSystem.Tick(deltaTime);
                enemyAreaMarkers.Tick(deltaTime);
                enemyOmenVfx.Tick();
            }

            // Fighting Mode combat input (guard key); gates internally on engagement.
            if (!PrivateDirectorActive) fightingCombatController.Tick(deltaTime);

            // Action Mode loop (player combo/guard + enemy telegraphs). Runs every
            // frame so guard windows and toggle-off cleanup are handled even
            // when the sim is not active; gates internally on config.ActionMode.
            if (!PrivateDirectorActive) actionModeController.Tick(deltaTime);

            // Hit feedback (camera punch decay + hitstop restore). Runs every frame.
            hitFeedbackController.Tick(deltaTime);

            // Standalone spectator crowds and world-level dev features continue to process
            // outside combat simulations.
            spectatorController.Tick(deltaTime);
            devExperimental.TickWorld(deltaTime);
            npcScaleController.Tick();
            arrowImpactController.Tick();
            // A battle moves its actors on the plugin's own navigation mesh, kept around the player.
            if (combatEngine.IsActive && Services.ObjectTable.LocalPlayer is { } meshPlayer)
                navMesh.EnsureAround(meshPlayer.Position, clientState.TerritoryType);
            navMesh.Tick(deltaTime, Services.ObjectTable.LocalPlayer?.Position);
            worldGeometryRenderer.Tick();
            worldGeometryPreview.Tick(deltaTime);
            TickPrivateEffects(deltaTime);
            // Before the active gate: the director clears its state when the fight ends. Inert when voices are off.
            presentationModule.TickVoice(deltaTime);

            // One step of the crowd for everyone it moves: allies (who set their goals above, and move
            // outside a battle too) and enemies (who set theirs after the previous step).
            crowdMover.Update(deltaTime);

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

            if (PrivateDirectorActive) return;
            mapEnemyController.Tick(deltaTime);
            combatEngine.Tick(deltaTime);
            corpseModule.ProcessPendingNpcRagdolls(deltaTime);
            battleModule.TickWar(deltaTime);
            npcAiController.Tick(deltaTime, npcSelector.SelectedNpcs);
            // After AI: its movement cleanup would otherwise erase celebration loops every frame.
            victoryCelebration?.Reassert();
            // Re-apply after NPC AI so the final frame pose is still constrained to the 2D lane.
            // Lane only — the full Tick already ran this frame; running it again would advance
            // camera smoothing and translate timers at double speed.
            fightingModeController.ReapplyLane();
            // Remaining experimental dev ticking (Victory/Hold + occlusion hide).
            devExperimental.Tick(deltaTime);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in framework update, stopping simulation.");
            combatEngine.StopSimulation();
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        ResetPrivateDirectors();
        ClearPrivateEffects();
        worldGeometryPreview.Clear();
        navMesh.Clear();
        worldGeometryRenderer.ClearAll();
        // Despawn client-spawned NPCs first (they don't survive zone changes)
        npcSpawner.SpawnModeActive = false;
        if (npcSpawner.SpawnedNpcs.Count > 0)
        {
            npcSpawner.DespawnAll();
            log.Info("Despawned all client NPCs on zone change.");
        }

        corpseModule.DeactivateAllNpcRagdolls();
        weaponDropController.RemoveAll();
        dismembermentController.RemoveAll();
        armorDetachmentController.Reset();
        npcScaleController.Reset();
        spectatorController.DespawnAll();
        devExperimental.ResetWorldState();
        enemyControlController.Despawn();
        dynamicCameraController.Reset();

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
        ResetPrivateDirectors();
        ClearPrivateEffects();
        worldGeometryPreview.Clear();
        worldGeometryRenderer.ClearAll();
        try
        {
            npcSpawner.SpawnModeActive = false;
            corpseModule.DeactivateAllNpcRagdolls();
            weaponDropController.RemoveAll();
            dismembermentController.RemoveAll();
            armorDetachmentController.Reset();
            npcScaleController.Reset();
            spectatorController.DespawnAll();
            devExperimental.ResetWorldState();
            enemyControlController.Despawn();
            dynamicCameraController.Reset();
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
