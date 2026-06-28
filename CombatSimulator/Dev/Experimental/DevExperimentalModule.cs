using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Dev.Experimental;

/// <summary>
/// Single owner for the experimental ("easter-egg") dev features: it constructs and drives the dev
/// controllers (Victory cinema, Hold, KO Strip, Monster) and the dev-only per-frame tweaks (NPC
/// scale, occlusion hide) that were previously scattered through Plugin.cs. The plugin forwards its
/// lifecycle (tick / player-death / reset / zone-change / draw / dispose) here, so all dev-exclusive
/// orchestration lives in one place.
/// </summary>
public sealed unsafe class DevExperimentalModule : IDevExperimental
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly Func<nint, SimulatedNpc?> resolveNpc;
    private readonly IPluginLog log;

    public VictorySequenceController Victory { get; }
    public BoneHoldTestModeController Hold { get; }
    public KoStripController KoStrip { get; }
    public MonsterModeController Monster { get; }

    // NPCs hidden by the occlusion check (need restoring when the feature/frame disables them).
    private readonly HashSet<nint> occlusionHiddenNpcs = new();

    public DevExperimentalModule(
        IKeyState keyState, IGamepadState gamepad, IFramework framework,
        RagdollController ragdoll, AnimationController animation, BoneTransformService boneService,
        MovementBlockHook movementBlock, ActiveCameraController activeCamera,
        VNavmeshIpc vnav, DismembermentController dismemberment, GlamourerIpc glamourer,
        CombatEngine combatEngine, IClientState clientState, ITargetManager targetManager,
        NpcSelector npcSelector, Func<nint, SimulatedNpc?> resolveNpc,
        Configuration config, IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.resolveNpc = resolveNpc;
        this.log = log;

        Victory = new VictorySequenceController(
            boneService, animation.EmotePlayer, movementBlock, ragdoll, vnav,
            clientState, targetManager, config, log);
        Hold = new BoneHoldTestModeController(
            boneService, animation.EmotePlayer, ragdoll, animation, movementBlock, vnav, combatEngine, log);
        KoStrip = new KoStripController(config, glamourer, log);
        Monster = new MonsterModeController(
            keyState, gamepad, framework, ragdoll, animation, boneService, movementBlock,
            activeCamera, vnav, dismemberment, glamourer, config, log);
        Monster.AttachHold(Hold);
    }

    // Engine seam: expose the cinematic victory controller as the narrow interface.
    IVictorySequence? IDevExperimental.VictorySequence => Victory;

    /// <summary>True while a dev controller is driving this NPC (suppresses its AI).</summary>
    public bool ControlsNpc(nint address) => Victory.ControlsNpc(address) || Monster.ControlsNpc(address);

    // ── Lifecycle forwarded from the plugin ──────────────────────────────────

    public void Tick(float deltaTime)
    {
        Victory.Tick(deltaTime);
        Hold.Tick(deltaTime, npcSelector.SelectedNpcs);
        ApplyNpcScale();
        TickNpcOcclusionHide();
    }

    public void OnPlayerDeath(nint playerAddress)
    {
        KoStrip.StripOnKo(playerAddress);
        if (config.MonsterControlKiller)
        {
            var killer = resolveNpc(combatEngine.LastPlayerKillerAddress);
            if (killer != null) Monster.ControlKiller(killer);
            else log.Info("MonsterMode: no valid killer to control.");
        }
        else if (config.MonsterSpawnOnDeath)
        {
            Monster.Spawn();
        }
    }

    /// <summary>Reset transient dev state on combat reset / zone change.</summary>
    public void ResetTransientState()
    {
        KoStrip.Reset();
        Monster.Despawn();
    }

    public void DrawToolbars(MainWindow mainWindow)
    {
        if (config.ShowGrabToolbar) mainWindow.DrawGrabToolbar(Victory);
        if (config.ShowHoldToolbar) mainWindow.DrawHoldToolbar(Hold);
        if (config.ShowKoStripToolbar) mainWindow.DrawKoStripToolbar(KoStrip);
        if (config.ShowMonsterToolbar) mainWindow.DrawMonsterToolbar(Monster);
    }

    // ── Dev-only per-frame tweaks ────────────────────────────────────────────

    // Apply the NPC scale override via the DrawObject transform.
    private void ApplyNpcScale()
    {
        if (config.DevNpcScale == 1.0f) return;
        var s = config.DevNpcScale;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            var gameObj = (GameObject*)npc.BattleChara;
            if (gameObj->DrawObject == null) continue;
            gameObj->DrawObject->Scale = new Vector3(s, s, s);
            gameObj->DrawObject->NotifyTransformChanged();
        }
    }

    // Hide NPCs that block the active camera's view of the player.
    private void TickNpcOcclusionHide()
    {
        if (!config.DevNpcOcclusionHide || !config.EnableActiveCamera)
        {
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

        var cinematicAddr = Victory.CinematicNpcAddress;
        var threshold = config.DevNpcOcclusionRadius;

        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            var addr = npc.Address;

            // Never hide the cinematic (grabbing) NPC.
            if (addr == cinematicAddr) continue;

            var gameObj = (GameObject*)npc.BattleChara;
            if (gameObj->DrawObject == null) continue;

            // Check multiple heights along the NPC body (feet, knees, chest, head).
            var baseX = gameObj->Position.X;
            var baseY = gameObj->Position.Y;
            var baseZ = gameObj->Position.Z;
            bool blocking = false;

            for (float yOff = 0.0f; yOff <= 1.8f; yOff += 0.45f)
            {
                var samplePos = new Vector3(baseX, baseY + yOff, baseZ);
                var camToSample = samplePos - camPos;
                var t = Vector3.Dot(camToSample, lineDir);
                if (t < 0f || t > lineLen) continue;

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
                if (occlusionHiddenNpcs.Add(addr))
                    gameObj->DrawObject->IsVisible = false;
            }
            else
            {
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
            var gameObj = (GameObject*)npc.BattleChara;
            if (gameObj->DrawObject != null)
                gameObj->DrawObject->IsVisible = true;
        }
        occlusionHiddenNpcs.Clear();
    }

    /// <summary>Restore any occlusion-hidden NPCs (session cleanup before the dev controllers go away).</summary>
    public void RestoreOcclusion() => RestoreOcclusionHiddenNpcs();

    public void Dispose()
    {
        Victory.Dispose();
        Hold.Dispose();
        KoStrip.Dispose();
        Monster.Dispose();
    }
}
