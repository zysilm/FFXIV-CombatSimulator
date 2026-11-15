// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// The camera: the coordinator (the one writer), the game camera hook, the active and the dynamic
/// camera (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed unsafe class CameraModule
{
    private readonly PluginModules m;

    private CameraModeCoordinator cameraModeCoordinator = null!;
    private GameCameraUpdateHook gameCameraUpdateHook = null!;
    private ActiveCameraController activeCameraController = null!;
    private DynamicCameraController dynamicCameraController = null!;

    public CameraModule(PluginModules modules) => m = modules;

    public CameraModeCoordinator Coordinator => cameraModeCoordinator;
    public GameCameraUpdateHook UpdateHook => gameCameraUpdateHook;
    public ActiveCameraController Active => activeCameraController;
    public DynamicCameraController Dynamic => dynamicCameraController;

    private IPluginLog log => m.Log;
    private CombatEngine combatEngine => m.Combat.Engine;
    private NpcSelector npcSelector => m.Actors.Selector;
    private PlayerTargetController playerTargetController => m.Combat.PlayerTarget;
    private BoneTransformService boneTransformService => m.World.BoneTransforms;
    private RagdollController ragdollController => m.Corpse.PlayerRagdoll;

    /// <summary>The coordinator, the game camera hook and the active camera.</summary>
    public void CreateCoordinator(IGameInteropProvider gameInterop, IClientState clientState, ISigScanner sigScanner)
    {
        cameraModeCoordinator = new CameraModeCoordinator(m.Config, log);
        gameCameraUpdateHook = new GameCameraUpdateHook(gameInterop, log);
        activeCameraController = new ActiveCameraController(gameInterop, clientState, sigScanner, m.Config, log);
        // Single camera write authority: modes submit requests, the coordinator applies
        // the winner each frame; the orbit hook consumes its resolution.
        activeCameraController.CoordinatorOrbitCenter = () => cameraModeCoordinator.CurrentOrbitCenter;
        activeCameraController.GetCurrentOwner = () => cameraModeCoordinator.CurrentOwner;
    }

    /// <summary>
    /// Dynamic Camera: over-the-shoulder combat framing, and a two-subject composition
    /// (corpse + killer) on death. Submits through the coordinator like every other mode
    /// and never clears player input — see DynamicCameraController.
    /// </summary>
    public void CreateDynamic(ISigScanner sigScanner)
    {
        dynamicCameraController = new DynamicCameraController(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector, playerTargetController,
            boneTransformService, ragdollController, sigScanner, log);
        dynamicCameraController.GetCurrentOwner = () => cameraModeCoordinator.CurrentOwner;
        // The death shot forces its locked pitch/zoom inside the game's camera update.
        // Framework-time writes are otherwise overwritten by controller, keyboard and wheel input.
        gameCameraUpdateHook.PreCameraUpdate = camera =>
        {
            activeCameraController.UpdateCollisionPolicy();
            dynamicCameraController.OnPreCameraUpdate(camera);
        };
        activeCameraController.ModeLookAtOverride = dynamicCameraController.OverrideDeathLookAt;
    }

    /// <summary>Put the camera in Directed mode for a battle, when that is asked for.</summary>
    public void SwitchToDirectedCameraForBattle()
    {
        if (!m.Config.DirectedCameraOnBattle || (m.Config.EnableDynamicCamera && !m.Config.EnableActiveCamera))
            return;
        m.Config.EnableActiveCamera = false;
        m.Config.EnableDynamicCamera = true;
        activeCameraController.SetActive(false);
        m.Config.Save();
    }
}
