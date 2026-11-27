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
/// camera, the automatic death shots, the cutscene shots and the duel camera (docs/PLUGIN_ARCHITECTURE.md §6.1).
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
    private DuelCameraDirector duelCameraDirector = null!;
    private Replay.DeathReplayDirector deathReplayDirector = null!;

    public CameraModule(PluginModules modules) => m = modules;

    public CutsceneShotDirector CutsceneShots => cutsceneShotDirector;

    public CameraModeCoordinator Coordinator => cameraModeCoordinator;
    public GameCameraUpdateHook UpdateHook => gameCameraUpdateHook;
    public ActiveCameraController Active => activeCameraController;
    public DynamicCameraController Dynamic => dynamicCameraController;
    public DeathShotDirector DeathShots => deathShotDirector;
    public DuelCameraDirector Duel => duelCameraDirector;
    public Replay.DeathReplayDirector DeathReplay => deathReplayDirector;

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
    /// Dynamic Camera: over-the-shoulder combat framing, and a two-subject composition
    /// (corpse + killer) on death. Submits through the coordinator like every other mode
    /// and never clears player input — see DynamicCameraController.
    /// </summary>
    public void CreateDynamic(ISigScanner sigScanner)
    {
        dynamicCameraController = new DynamicCameraController(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector,
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

    /// <summary>
    /// Cutscene follow shots (docs/CUTSCENE_SHOTS.md): the game's own cutscene camera work on our characters at victory
    /// and death. Hands the game the shot's eye and look-at point inside its camera update (the active camera's
    /// SetCameraLookAt detour) and its FoV through the coordinator; the post-update hook stops a stalled shot and checks the result.
    /// </summary>
    public void CreateCutsceneShots(ITargetManager targetManager, IDataManager dataManager, ICondition condition, IClientState clientState,
        string configDirectory, System.Func<bool> sceneActive)
    {
        cutsceneShotDirector = new CutsceneShotDirector(
            m.Config, cameraModeCoordinator, dynamicCameraController, combatEngine, npcSelector, m.Actors.Companions,
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
        gameCameraUpdateHook.PostCameraUpdate = cutsceneShotDirector.OnPostCameraUpdate;
        activeCameraController.PoseOverride = cutsceneShotDirector.OverridePose;
        activeCameraController.PoseOverrideActive = () => cutsceneShotDirector.IsPlaying;
    }

    /// <summary>
    /// The duel camera (docs/DUEL_CAMERA.md), camera mode "Duel": a two-shot of the player and one opponent while a
    /// simulation runs. Its lens, the game camera's yaw and the held zoom and pitch go through the coordinator (owner
    /// Duel, just above the Directed camera's combat framing and below everything else); its eye and look-at point
    /// through the same hand-over as the shots.
    ///
    /// With it, the automatic death shot (docs/DEATH_SHOTS.md): in Duel mode, and only there, it takes the camera when
    /// the player goes down (the Directed camera keeps its own death framing). It reads the player's body, asks Enemy
    /// Control whether a grab holds it, and hands its pose over the same way.
    ///
    /// The pose hand-over has three levels from here on: the duel camera writes first, the automatic death shot over
    /// it, a cutscene shot over both. A cutscene shot that ends blends out to whichever of the two is running below it.
    /// </summary>
    public void CreateDuel(ITargetManager targetManager, ICondition condition, IClientState clientState, IGamepadState gamepadState,
        IGameConfig gameConfig, System.Func<bool> sceneActive)
    {
        deathShotDirector = new DeathShotDirector(
            m.Config, cameraModeCoordinator, combatEngine, boneTransformService, RagdollFor,
            () => m.Actors.EnemyControl.IsHoldingGrab,
            sceneActive, condition, clientState, log);
        deathShotDirector.CutsceneOwnsCamera = () => cutsceneShotDirector.OwnsCamera;
        duelCameraDirector = new DuelCameraDirector(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector, boneTransformService, m.Combat.Telegraphs,
            RagdollFor,
            () => targetManager.Target?.Address ?? nint.Zero,
            sceneActive,
            condition,
            clientState,
            gamepadState,
            gameConfig,
            log);
        duelCameraDirector.CutsceneOwnsCamera = () => cutsceneShotDirector.OwnsCamera;
        cutsceneShotDirector.HandingBack = () =>
        {
            deathShotDirector.ResumeNow();
            duelCameraDirector.ResumeNow();
        };
        gameCameraUpdateHook.PostCameraUpdate = camera =>
        {
            cutsceneShotDirector.OnPostCameraUpdate(camera);
            deathShotDirector.OnPostCameraUpdate(camera);
            duelCameraDirector.OnPostCameraUpdate(camera);
        };
        activeCameraController.PoseOverride = (ref System.Numerics.Vector3 lookAt, ref System.Numerics.Vector3 eye) =>
        {
            var duel = duelCameraDirector.OverridePose(ref lookAt, ref eye);
            var death = deathShotDirector.OverridePose(ref lookAt, ref eye);
            if (death) duelCameraDirector.NotePoseSuperseded();
            var cutscene = cutsceneShotDirector.OverridePose(ref lookAt, ref eye);
            if (cutscene)
            {
                deathShotDirector.NotePoseSuperseded();
                duelCameraDirector.NotePoseSuperseded();
            }
            return duel || death || cutscene;
        };
        activeCameraController.PoseOverrideActive = () => cutsceneShotDirector.IsPlaying || deathShotDirector.IsRunning || duelCameraDirector.IsRunning;
    }

    /// <summary>
    /// The death replay (docs/DEATH_REPLAY.md): records the simulation's last 20 s and replays the end of it after the
    /// player's death, through camera shots edited with its free camera. Its camera is written last in the pose
    /// hand-over (a fourth level, over the cutscene shots: the replay waits for a cutscene shot to end before it
    /// starts, and the free camera is the user's own tool). While the free camera flies on the movement keys the local
    /// player is held where they are (the same freeze as while dead) and Enemy Control leaves those keys alone.
    /// </summary>
    public void CreateDeathReplay(Dalamud.Plugin.Services.IKeyState keyState, IGamepadState gamepadState, ICondition condition,
        IClientState clientState, IFramework framework, System.Func<bool> sceneActive)
    {
        deathReplayDirector = new Replay.DeathReplayDirector(
            m.Config, cameraModeCoordinator, combatEngine, npcSelector, m.Actors.Companions, boneTransformService,
            ragdollController, m.World.MovementBlock,
            () => m.Actors.EnemyControl.IsHoldingGrab,
            () => cutsceneShotDirector.IsPlaying,
            sceneActive, condition, clientState, framework, keyState, gamepadState, log);
        m.Actors.EnemyControl.KeysTaken = () => deathReplayDirector.KeysTaken;
        var below = activeCameraController.PoseOverride;
        activeCameraController.PoseOverride = (ref System.Numerics.Vector3 lookAt, ref System.Numerics.Vector3 eye) =>
        {
            var under = below != null && below(ref lookAt, ref eye);
            var replay = deathReplayDirector.OverridePose(ref lookAt, ref eye);
            if (replay)
            {
                deathShotDirector.NotePoseSuperseded();
                duelCameraDirector.NotePoseSuperseded();
            }
            return under || replay;
        };
        var drawn = activeCameraController.PoseOverrideActive;
        activeCameraController.PoseOverrideActive = () => (drawn?.Invoke() ?? false) || deathReplayDirector.CameraRunning;
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
        // (Not out of Duel mode: that is a battle camera the user picked.)
        if (!m.Config.DirectedCameraOnBattle || m.Config.EnableDuelCamera && !m.Config.EnableActiveCamera ||
            (m.Config.EnableDynamicCamera && !m.Config.EnableActiveCamera))
            return;
        m.Config.EnableActiveCamera = false;
        m.Config.EnableDynamicCamera = true;
        m.Config.EnableDuelCamera = false;
        activeCameraController.SetActive(false);
        m.Config.Save();
    }
}
