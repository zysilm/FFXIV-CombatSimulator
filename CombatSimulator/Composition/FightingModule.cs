// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Fighting;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// Taking over someone else's body: Enemy Control (post-defeat creature possession, the grab) and the
/// Fighting Mode lane with its combat and AI (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed class FightingModule
{
    private readonly PluginModules m;

    private EnemyControlController enemyControlController = null!;
    private FightingModeController fightingModeController = null!;
    private WeaponHitboxService weaponHitboxService = null!;
    private FightingCombatController fightingCombatController = null!;
    private FightingAiController fightingAiController = null!;

    public FightingModule(PluginModules modules) => m = modules;

    public EnemyControlController EnemyControl => enemyControlController;
    public FightingModeController Mode => fightingModeController;
    public WeaponHitboxService WeaponHitboxes => weaponHitboxService;
    public FightingCombatController Combat => fightingCombatController;
    public FightingAiController Ai => fightingAiController;

    private IPluginLog log => m.Log;
    private RagdollController ragdollController => m.Corpse.PlayerRagdoll;
    private AnimationController animationController => m.World.Animation;
    private BoneTransformService boneTransformService => m.World.BoneTransforms;
    private MovementBlockHook movementBlockHook => m.World.MovementBlock;
    private VNavmeshIpc vnavmeshIpc => m.World.VNavmesh;
    private ActiveCameraController activeCameraController => m.Camera.Active;
    private CameraModeCoordinator cameraModeCoordinator => m.Camera.Coordinator;
    private NpcSelector npcSelector => m.Actors.Selector;
    private MapEnemyController mapEnemyController => m.Actors.MapEnemies;
    private CombatEngine combatEngine => m.Combat.Engine;
    private CombatSimulator.ActionCombat.PlayerGuardController playerGuardController => m.Combat.Guard;
    private CombatSimulator.ActionCombat.HitFeedbackController hitFeedbackController => m.Combat.HitFeedback;
    private CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter => m.Combat.Router;
    private CombatSimulator.ActionCombat.TelegraphSystem telegraphSystem => m.Combat.Telegraphs;
    private Dev.IDevExperimental devExperimental => m.SafetyDev.DevExperimental;

    /// <summary>
    /// Enemy Control (post-defeat creature possession) is a normal production controller now —
    /// owned and driven directly here, not through the dev-experimental seam.
    /// </summary>
    public void CreateEnemyControl(IKeyState keyState, IGamepadState gamepadState, IFramework framework)
    {
        enemyControlController = new EnemyControlController(
            keyState, gamepadState, framework, ragdollController, animationController, boneTransformService,
            movementBlockHook, activeCameraController, vnavmeshIpc,
            m.Config, log);
    }

    /// <summary>The Fighting Mode lane, its weapon hitboxes, combat input and AI.</summary>
    public void CreateFighting(IGamepadState gamepadState)
    {
        fightingModeController = new FightingModeController(
            m.Config, combatEngine, npcSelector, mapEnemyController, movementBlockHook,
            cameraModeCoordinator, boneTransformService,
            addr => devExperimental.ControlsNpc(addr) || enemyControlController.ControlsNpc(addr), log);
        // Post-defeat camera modes must track the corpse through the ragdoll bodies — the skeleton
        // pose read at framework time stays at the death spot even after kicks.
        fightingModeController.GetRagdollBonePosition = bone => ragdollController.GetBodyWorldPosition(bone);
        enemyControlController.FightingLane = fightingModeController;
        enemyControlController.SetCameraCoordinator(cameraModeCoordinator);
        weaponHitboxService = new WeaponHitboxService(m.Config, boneTransformService, log);
        fightingCombatController = new FightingCombatController(
            m.Config, playerGuardController, fightingModeController, combatEngine,
            animationController, weaponHitboxService, hitFeedbackController, gamepadState, log);
        fightingModeController.AttackSink = fightingCombatController.OnAttackInput;
        fightingAiController = new FightingAiController(
            m.Config, combatModeRouter, telegraphSystem, animationController, log);
        fightingModeController.FightingAi = fightingAiController;
        fightingCombatController.OnPlayerHitLanded = _ => fightingAiController.NotifyPlayerHitLanded();
        fightingModeController.GetEnemyControlFollowCenter = () => enemyControlController.FollowCenter;
    }
}
