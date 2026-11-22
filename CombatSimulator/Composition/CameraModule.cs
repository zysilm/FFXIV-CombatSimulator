// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// The camera: the coordinator (the one writer), the game camera hook, the active and the dynamic
/// camera, the automatic death shots and the cutscene shots (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed unsafe class CameraModule
{
    private readonly PluginModules m;

    private CameraModeCoordinator cameraModeCoordinator = null!;
    private GameCameraUpdateHook gameCameraUpdateHook = null!;
    private ActiveCameraController activeCameraController = null!;
    private DynamicCameraController dynamicCameraController = null!;
    private DeathShotDirector deathShotDirector = null!;
    private CutsceneShotDirector cutsceneShotDirector = null!;

    public CameraModule(PluginModules modules) => m = modules;

    public CutsceneShotDirector CutsceneShots => cutsceneShotDirector;

    public CameraModeCoordinator Coordinator => cameraModeCoordinator;
    public GameCameraUpdateHook UpdateHook => gameCameraUpdateHook;
    public ActiveCameraController Active => activeCameraController;
    public DynamicCameraController Dynamic => dynamicCameraController;
    public DeathShotDirector DeathShots => deathShotDirector;

    private IPluginLog log => m.Log;
    private CombatEngine combatEngine => m.Combat.Engine;
    private NpcSelector npcSelector => m.Actors.Selector;
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
    /// Dynamic Camera: over-the-shoulder combat framing, and the automatic death shot when the player goes down
    /// (docs/DEATH_SHOTS.md). The combat framing submits through the coordinator like every other mode and never
    /// clears player input — see DynamicCameraController. The death shot's FoV goes through the coordinator too; its
    /// eye and look-at point are handed to the game inside its camera update (the active camera's SetCameraLookAt
    /// detour), and the post-update hook stops a stalled shot and checks the result.
    /// </summary>
    public void CreateDynamic(ISigScanner sigScanner, ICondition condition, IClientState clientState)
    {
        deathShotDirector = new DeathShotDirector(
            m.Config, cameraModeCoordinator, boneTransformService, RagdollFor, condition, clientState, log);
        dynamicCameraController = new DynamicCameraController(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector,
            boneTransformService, deathShotDirector, sigScanner, log);
        dynamicCameraController.GetCurrentOwner = () => cameraModeCoordinator.CurrentOwner;
        gameCameraUpdateHook.PreCameraUpdate = camera => activeCameraController.UpdateCollisionPolicy();
        gameCameraUpdateHook.PostCameraUpdate = deathShotDirector.OnPostCameraUpdate;
        activeCameraController.PoseOverride = deathShotDirector.OverridePose;
        activeCameraController.PoseOverrideActive = () => deathShotDirector.IsRunning;
    }

    /// <summary>
    /// Cutscene follow shots (docs/CUTSCENE_SHOTS.md): the game's own cutscene camera work on our characters at victory
    /// and death. Hands the game the shot's eye and look-at point inside its camera update (the active camera's
    /// SetCameraLookAt detour) and its FoV through the coordinator; the post-update hook stops a stalled shot and checks the result.
    /// The pose hand-over has two levels: the automatic death shot writes first, a cutscene shot (owner 60) writes over
    /// it and wins; a cutscene shot that ends blends out to the death shot's pose.
    /// </summary>
    public void CreateCutsceneShots(ITargetManager targetManager, IDataManager dataManager, ICondition condition, IClientState clientState,
        string configDirectory, System.Func<bool> sceneActive)
    {
        cutsceneShotDirector = new CutsceneShotDirector(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector, m.Actors.Companions,
            boneTransformService,
            address => RagdollFor(address)?.IsActive == true,
            (address, bone) => RagdollFor(address) is { IsActive: true } ragdoll ? ragdoll.GetBodyWorldPosition(bone) : null,
            () => targetManager.Target?.Address ?? nint.Zero,
            path => dataManager.GetFile(path)?.Data,
            configDirectory,
            sceneActive,
            condition,
            clientState,
            log);
        deathShotDirector.CutsceneOwnsCamera = () => cutsceneShotDirector.OwnsCamera;
        gameCameraUpdateHook.PostCameraUpdate = camera =>
        {
            cutsceneShotDirector.OnPostCameraUpdate(camera);
            deathShotDirector.OnPostCameraUpdate(camera);
        };
        activeCameraController.PoseOverride = (ref System.Numerics.Vector3 lookAt, ref System.Numerics.Vector3 eye) =>
        {
            var death = deathShotDirector.OverridePose(ref lookAt, ref eye);
            var cutscene = cutsceneShotDirector.OverridePose(ref lookAt, ref eye);
            if (cutscene) deathShotDirector.NotePoseSuperseded();
            return death || cutscene;
        };
        activeCameraController.PoseOverrideActive = () => cutsceneShotDirector.IsPlaying || deathShotDirector.IsRunning;
    }

    /// <summary>The ragdoll of a body: the player's, or an enemy's / ally's death ragdoll.</summary>
    private RagdollController? RagdollFor(nint address)
    {
        if (address == nint.Zero) return null;
        if (Core.Services.ObjectTable.LocalPlayer?.Address == address) return ragdollController;
        return m.Corpse.NpcRagdolls.TryGetValue(address, out var npc) ? npc : null;
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
