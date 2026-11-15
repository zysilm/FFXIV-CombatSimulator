// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Navigation;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// The fight: damage, the engine and its notifications (<see cref="CombatEvents"/>), action combat
/// (combo input, guard, telegraphs, enemy areas, hit feedback, the attack router), the enemies' AI and
/// their feet on corpses, the virtual enemies' look-at, the player's target lock and the UseAction hook
/// (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed class CombatModule
{
    private readonly PluginModules m;

    private DamageCalculator damageCalculator = null!;
    private CombatEngine combatEngine = null!;
    private CombatSimulator.ActionCombat.ActionComboSink actionComboSink = null!;
    private CombatSimulator.ActionCombat.PlayerGuardController playerGuardController = null!;
    private CombatSimulator.ActionCombat.TelegraphSystem telegraphSystem = null!;
    private readonly CombatSimulator.ActionCombat.EnemyAreaMarkers enemyAreaMarkers = new();
    private CombatSimulator.ActionCombat.PlayerHitboxResolver playerHitboxResolver = null!;
    private CombatSimulator.ActionCombat.HitFeedbackController hitFeedbackController = null!;
    private CombatSimulator.ActionCombat.ActionModeController actionModeController = null!;
    private CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter = null!;
    private NpcCorpseFootIkSolver npcCorpseFootIkSolver = null!;
    private NpcAiController npcAiController = null!;
    private VirtualEnemyLookAtController virtualEnemyLookAt = null!;
    private PlayerTargetController playerTargetController = null!;
    private UseActionHook useActionHook = null!;
    private SafetyBarriers safetyBarriers = null!;
    private FakeActorGuard fakeActorGuard = null!;
    private TargetingService targeting = null!;
    private BattleRuleSet ruleParts = null!;

    public CombatModule(PluginModules modules) => m = modules;

    public DamageCalculator Damage => damageCalculator;
    public CombatEngine Engine => combatEngine;
    /// <summary>The engine's notifications (null until the engine is built).</summary>
    public CombatEvents Events => combatEngine.Events;
    public CombatSimulator.ActionCombat.ActionComboSink ComboSink => actionComboSink;
    public CombatSimulator.ActionCombat.PlayerGuardController Guard => playerGuardController;
    public CombatSimulator.ActionCombat.TelegraphSystem Telegraphs => telegraphSystem;
    public CombatSimulator.ActionCombat.EnemyAreaMarkers AreaMarkers => enemyAreaMarkers;
    public CombatSimulator.ActionCombat.PlayerHitboxResolver PlayerHitbox => playerHitboxResolver;
    public CombatSimulator.ActionCombat.HitFeedbackController HitFeedback => hitFeedbackController;
    public CombatSimulator.ActionCombat.ActionModeController ActionMode => actionModeController;
    public CombatSimulator.ActionCombat.CombatModeRouter Router => combatModeRouter;
    public NpcCorpseFootIkSolver CorpseFootIk => npcCorpseFootIkSolver;
    public NpcAiController EnemyAi => npcAiController;
    public VirtualEnemyLookAtController EnemyLookAt => virtualEnemyLookAt;
    public PlayerTargetController PlayerTarget => playerTargetController;
    public UseActionHook UseAction => useActionHook;
    /// <summary>The client-side safety barriers every start and spawn path checks.</summary>
    public SafetyBarriers Barriers => safetyBarriers;
    /// <summary>Keeps the simulation's own actors out of the game's real targeting (ticked every frame).</summary>
    public FakeActorGuard FakeActors => fakeActorGuard;
    /// <summary>Who fights whom (both sides' threat, draw states and the enemies' targets).</summary>
    public TargetingService Targeting => targeting;
    /// <summary>The battle's rule parts (bound to their context by the allies' controller).</summary>
    public BattleRuleSet RuleParts => ruleParts;

    private ICombatModuleSettings config => m.Config;
    private IPluginLog log => m.Log;
    private ActionDataProvider actionDataProvider => m.Actors.ActionData;
    private AnimationController animationController => m.World.Animation;
    private MovementBlockHook movementBlockHook => m.World.MovementBlock;
    private BoneTransformService boneTransformService => m.World.BoneTransforms;
    private CrowdMover crowdMover => m.World.Crowd;
    private Integration.VNavmeshIpc vnavmeshIpc => m.World.VNavmesh;
    private RagdollController ragdollController => m.Corpse.PlayerRagdoll;
    private ICorpseQuery corpses => m.Corpse;
    private NpcSelector npcSelector => m.Actors.Selector;
    private CombatRoster combatRoster => m.Actors.Roster;
    private NpcSpawner npcSpawner => m.Actors.Spawner;
    private CombatCompanionManager companionManager => m.Actors.Companions;
    private MapEnemyController mapEnemyController => m.Actors.MapEnemies;
    private Camera.GameCameraUpdateHook gameCameraUpdateHook => m.Camera.UpdateHook;
    private EnemyControlController enemyControlController => m.Actors.EnemyControl;
    private Dev.IDevExperimental devExperimental => m.SafetyDev.DevExperimental;

    public void CreateDamageCalculator()
    {
        damageCalculator = new DamageCalculator
        {
            IsActionMode = () => config.ActionMode,
            // Everyone's damage is sized by the player's HP (see CombatBudgetModel.DamagePerPotency).
            PlayerMaxHp = () => combatEngine?.State.PlayerState.MaxHp ?? 0f,
        };
    }

    /// <summary>
    /// The engine and action combat. Seams: ActionComboSink (player input), CombatModeRouter
    /// (enemy attack executor), TelegraphSystem (windup→hitbox).
    /// </summary>
    public void CreateEngineAndActionCombat(IClientState clientState, IGamepadState gamepadState)
    {
        // Who fights whom, and the battle's rule parts, are the engine's by constructor (the allies'
        // controller, built later on the engine, is given the same two and binds the parts' context).
        targeting = new TargetingService(() => combatEngine.State.SimulationTime);
        ruleParts = new BattleRuleSet(BattleRuleFactory.CreateAll());
        combatEngine = new CombatEngine(
            actionDataProvider, damageCalculator, animationController,
            movementBlockHook, ragdollController,
            config, npcSelector, combatRoster, targeting, ruleParts, clientState, log);
        // Action Mode wiring. Seams: ActionComboSink (player input), CombatModeRouter
        // (enemy attack executor), TelegraphSystem (windup→hitbox). All gate on
        // config.ActionMode; off ⇒ behavior-equivalent simulation paths.
        actionComboSink = new CombatSimulator.ActionCombat.ActionComboSink(config);
        playerGuardController = new CombatSimulator.ActionCombat.PlayerGuardController(
            animationController,
            config,
            () => combatEngine.State.PlayerState.IsAlive,
            log);
        playerGuardController.OnChainGuardResolved += combatEngine.RestorePlayerGuardMp;
        telegraphSystem = new CombatSimulator.ActionCombat.TelegraphSystem(
            combatEngine, animationController, config, () => playerGuardController.IsGuardActive, playerGuardController.NotifyPerfectGuard, log,
            enemyAreaMarkers);
        playerHitboxResolver = new CombatSimulator.ActionCombat.PlayerHitboxResolver(combatEngine, npcSelector, config);
        hitFeedbackController = new CombatSimulator.ActionCombat.HitFeedbackController(
            config, npcSelector.GetSelectedNpc, animationController, log);
        gameCameraUpdateHook.HitShakeProvider = () => hitFeedbackController.CurrentShakeOffset;
        // A corpse landing hard shakes the view like any other impact.
        ragdollController.HardLanding += hitFeedbackController.ShakeForHardLanding;
        actionModeController = new CombatSimulator.ActionCombat.ActionModeController(
            config, actionComboSink, playerHitboxResolver, playerGuardController,
            telegraphSystem, combatEngine, animationController, hitFeedbackController, gamepadState, log);
        combatModeRouter = new CombatSimulator.ActionCombat.CombatModeRouter(
            config,
            new CombatSimulator.ActionCombat.InstantAttackExecutor(combatEngine, config,
                (npc, actionId) => animationController.PlayNpcWindupPose(npc, actionId)),
            new CombatSimulator.ActionCombat.TelegraphedAttackExecutor(telegraphSystem, config));
    }

    /// <summary>
    /// The engine and the allies' manager need each other; the manager is built after the engine, so the
    /// engine is given the manager's one remaining side here: an enemy's target, which the manager draws.
    /// (The roster, who fights whom and the rule parts the engine has by constructor.)
    /// </summary>
    public void WireCompanions()
    {
        combatEngine.ResolveNpcTarget = companionManager.SelectEnemyTarget;
    }

    /// <summary>
    /// The enemies' AI and their feet on corpses, the crowd for both sides, what the allies and the
    /// spawner are given from the fight, and the virtual enemies' look-at.
    /// </summary>
    public void CreateEnemyAi()
    {
        npcCorpseFootIkSolver = new NpcCorpseFootIkSolver(boneTransformService, log);
        npcAiController = new NpcAiController(
            combatEngine, animationController, movementBlockHook, config, log,
            combatModeRouter, npcCorpseFootIkSolver,
            companionManager.CollectPartyPositions,
            // The AI only queries during framework ticks.
            addr => devExperimental.ControlsNpc(addr) || enemyControlController.ControlsNpc(addr),
            // Don't commit a new attack while this enemy already has a live telegraph (fixes the
            // double-swing where the old 0.6s animation lock expired mid-animation).
            telegraphSystem.IsBusy);
        npcAiController.AreaMarkers = enemyAreaMarkers;
        npcAiController.Crowd = crowdMover;
        companionManager.Crowd = crowdMover;
        npcSpawner.Navmesh = vnavmeshIpc;
        // Spawns only where the party can walk to (the plugin's navigation mesh, when it is there).
        npcSpawner.ReachableSpot = point =>
            crowdMover.TryReachableSpot(point, CrowdMover.SpawnSearchRadius, out var spot) ? spot : null;
        npcSpawner.OnAllySpawnComplete = (npc, request) =>
        {
            // An ally counts as a companion, so the party features have to be on for it to fight.
            m.Config.EnableCombatCompanions = true;
            companionManager.AdoptNpc(npc, request);
        };
        combatEngine.InterruptTelegraphCast = telegraphSystem.TryInterrupt;
        npcCorpseFootIkSolver.CorpseSupportHeightProvider = corpses.ResolveCorpseTraversalHeight;
        enemyControlController.FootIkSolver = npcCorpseFootIkSolver;
        virtualEnemyLookAt = new VirtualEnemyLookAtController(boneTransformService, config, npcSpawner, combatEngine,
            addr => devExperimental.ControlsNpc(addr) || enemyControlController.ControlsNpc(addr),
            log);
    }

    /// <summary>
    /// Custom in-simulation target lock system. Takes over the game's target
    /// keybinds during simulation; the engine reads the locked target for
    /// auto-attack and UseActionHook routes manual actions to it.
    /// </summary>
    public void CreatePlayerTargeting(IGameInteropProvider gameInterop, IObjectTable objectTable, IGameGui gameGui)
    {
        playerTargetController = new PlayerTargetController(
            gameInterop, combatEngine, npcSelector, config, objectTable, gameGui, log);
        combatEngine.GetLockedTargetId = () => playerTargetController.LockedTargetEntityId;
    }

    /// <summary>
    /// The UseAction hook (created here, enabled by the plugin's constructor right after), and the safety barriers
    /// it is part of: the spawners create simulated actors only while every barrier is up.
    /// </summary>
    public void CreateUseActionHook(IGameInteropProvider gameInterop, IClientState clientState)
    {
        useActionHook = new UseActionHook(gameInterop, combatEngine, npcSelector, npcSpawner, config, clientState, log,
            playerTargetController, mapEnemyController, actionComboSink);
        safetyBarriers = new SafetyBarriers(useActionHook, playerTargetController);
        npcSpawner.SpawnAllowed = () => safetyBarriers.AllHealthy;
        companionManager.SpawnAllowed = () => safetyBarriers.AllHealthy;
        fakeActorGuard = new FakeActorGuard(combatEngine, npcSpawner, companionManager, safetyBarriers, log);
    }
}
