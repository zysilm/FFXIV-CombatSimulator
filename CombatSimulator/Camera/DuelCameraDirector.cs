// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Animation;
using CombatSimulator.Camera.Cutscene;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Camera;

/// <summary>
/// The duel camera in the game (docs/DUEL_CAMERA.md), the third camera mode next to Follow bone and Directed: runs
/// <see cref="DuelCameraSolver"/> on the player and one opponent every frame and hands its eye and look-at point to
/// the game.
///
/// Who the opponent is: <see cref="DuelOpponentPicker"/> over the selected enemies (attacking the player, an attack
/// of theirs winding up, the player's target, nearest). Key moments come from what the plugin already raises: hits
/// (<see cref="NoteDamage"/>, CombatEvents.DamageLanded) and the telegraphs' outcomes (polled).
///
/// Camera: owner <see cref="CameraOwner.Duel"/> in the coordinator for the lens, the game's zoom and pitch (held), and
/// the game camera's own yaw, which is written every frame: the game moves the player relative to that yaw, so it is
/// kept on the rendered view (leaned towards the opponent, see the solver). The pose goes through the same
/// SetCameraLookAt hand-over as the death and cutscene shots (<see cref="OverridePose"/>), under both of them.
/// Anything above it in the coordinator (Enemy Control, a death shot, a scene, a cutscene shot) has the camera: the
/// duel camera waits and starts again from the camera it finds.
///
/// The player keeps the left/right orbit (the change of the game camera's yaw since it was written), a limited pitch
/// (the change of its pitch) and the wheel (size).
/// </summary>
public sealed unsafe class DuelCameraDirector : IDisposable
{
    public const float BlendOutSeconds = 0.3f;
    private const int CheckFrames = 60;
    private const long TickStallMs = 500;
    private const long SummaryMs = 5000;
    /// <summary>A change of the game camera's yaw or pitch larger than this in one frame is not the player's hand.</summary>
    private const float MaxInputStep = 1.0f;
    /// <summary>A hit that takes this much of the target's HP is a heavy one (a critical hit: half of it).</summary>
    private const float HeavyHit = 0.10f;
    /// <summary>A telegraphed attack on the player is a big one with a wind-up this long or an area this wide; the
    /// camera answers it this long before it lands.</summary>
    private const float BigWindup = 1.2f, BigRadius = 4f, ThreatLead = 0.35f;

    public static readonly string[] FramingLabels = { "Tight", "Standard", "Wide" };
    public static readonly DuelFraming[] Framings = { DuelFraming.Tight, DuelFraming.Standard, DuelFraming.Wide };

    private enum State { Idle, Waiting, Running, BlendOut }

    private readonly Configuration config;
    private readonly CameraModeCoordinator coordinator;
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly BoneTransformService bones;
    private readonly TelegraphSystem telegraphs;
    private readonly Func<nint, RagdollController?> ragdollFor;
    private readonly Func<nint> currentTarget;
    private readonly Func<bool> sceneActive;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly GameWorld world = new();
    private readonly DuelCameraSolver solver;
    private readonly DuelOpponentPicker picker = new();
    private readonly List<DuelCandidate> candidates = new();

    /// <summary>True while a cutscene shot holds the camera (not while it blends out): the duel camera waits.</summary>
    public Func<bool>? CutsceneOwnsCamera { get; set; }

    // ---- state ----
    private State state = State.Idle;
    private bool failed;
    private string status = "off";
    private string? beginFailure;
    private float time;

    // camera
    private Vector3 poseEye, poseLook;
    private float poseFov;
    private bool havePose;
    private float fovRatio = 1f, aspect = 16f / 9f;
    private float heldDistance, originalDirV, heldDirV, pitchSense = -1f;
    private bool haveHeldDistance, haveHeldDirV;
    private float writtenDirH;
    private bool wroteDirH;
    private float blendOutTime;

    // the player's hand
    private float pendingWheel;

    // key moments
    private bool pendingGiven, pendingTaken, pendingFinish;
    private readonly HashSet<ActiveTelegraph> guarded = new(), threatened = new();
    private readonly List<ActiveTelegraph> stale = new();

    // opponent
    private uint opponentId;
    private string opponentName = "";

    // hand-over bookkeeping
    private long lastTickAt;
    private int updatesSinceTick;
    private bool wroteThisUpdate, supersededThisUpdate, loggedOverrideError;
    private int overrideCount, checkCount;
    private float checkEye, checkLook, checkFov, checkYaw;

    // log
    private DuelOutput last;
    private Vector3 lastOutEye, lastOutDir;
    private bool haveLastOut;
    private long nextSummaryAt;
    private float windowEyeSpeed, windowViewRate, lastGive;
    private int windowFrames, windowRays, windowRaysMax, beatsTaken, beatsRefused;

    public DuelCameraDirector(
        Configuration config,
        CameraModeCoordinator coordinator,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        BoneTransformService bones,
        TelegraphSystem telegraphs,
        Func<nint, RagdollController?> ragdollFor,
        Func<nint> currentTarget,
        Func<bool> sceneActive,
        ICondition condition,
        IClientState clientState,
        IPluginLog log)
    {
        this.config = config;
        this.coordinator = coordinator;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.bones = bones;
        this.telegraphs = telegraphs;
        this.ragdollFor = ragdollFor;
        this.currentTarget = currentTarget;
        this.sceneActive = sceneActive;
        this.condition = condition;
        this.clientState = clientState;
        this.log = log;
        solver = new DuelCameraSolver(world);
    }

    /// <summary>The camera mode is Duel (Follow bone and Directed come first when a flag of theirs is on as well).</summary>
    private bool DuelMode => config.EnableDuelCamera && !config.EnableActiveCamera && !config.EnableDynamicCamera;

    /// <summary>The duel camera is writing the camera (or blending back from it): models near the eye are kept drawn.</summary>
    public bool IsRunning => state is State.Running or State.BlendOut;

    /// <summary>One line for the Camera tab.</summary>
    public string Status => status;

    /// <summary>Raw mouse wheel from the plugin's draw pass (the game's zoom is held while the duel camera runs).</summary>
    public void NotifyMouseWheel(float wheel)
    {
        if (state == State.Running && wheel != 0f && float.IsFinite(wheel))
            pendingWheel += wheel;
    }

    /// <summary>CombatEvents.DamageLanded: heavy hits and the finishing blow are key moments.</summary>
    public void NoteDamage(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source)
    {
        if (state != State.Running || target == null || damage == null || damage.Damage <= 0) return;
        var share = target.MaxHp > 0 ? (float)damage.Damage / target.MaxHp : 0f;
        var heavy = share >= HeavyHit || damage.IsCritical && share >= HeavyHit * 0.5f;
        if (target.IsPlayer)
        {
            if (heavy && target.IsAlive) pendingTaken = true;
        }
        else if (source is { IsPlayer: true } && target.EntityId == opponentId)
        {
            if (!target.IsAlive) pendingFinish = true;
            else if (heavy) pendingGiven = true;
        }
    }

    // ------------------------------------------------------------------
    // Per frame
    // ------------------------------------------------------------------

    /// <summary>Every framework tick, after the Directed camera (and its death shot) and before the cutscene shots.</summary>
    public void Tick(float dt)
    {
        lastTickAt = Environment.TickCount64;
        updatesSinceTick = 0;
        if (state == State.BlendOut)
        {
            blendOutTime += dt;
            if (blendOutTime >= BlendOutSeconds) EndBlendOut();
            else SubmitBlendOut();
        }
        if (failed && !config.EnableDuelCamera) failed = false;
        if (failed) return;
        try
        {
            TickCore(dt);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DuelCam: frame failed; the duel camera stops and the camera goes back to the game.");
            Stop("error");
            failed = true;
            status = "duel camera stopped after an error (see the log); pick the mode again to restart it";
        }
    }

    private void TickCore(float dt)
    {
        // ---- is a duel camera wanted at all ----
        string? off = null;
        var blend = true;
        if (!DuelMode) off = "off";
        else if (!combatEngine.IsActive) off = "idle — simulation not running";
        else if (sceneActive())
        {
            off = "another scene has the camera";
            blend = false;
        }
        var player = off == null ? Core.Services.ObjectTable.LocalPlayer : null;
        if (off == null && (player == null || player.Address == nint.Zero))
        {
            off = "no player";
            blend = false;
        }
        if (off == null && !combatEngine.State.PlayerState.IsAlive)
        {
            // The automatic death shot takes it from here, from the picture as it is.
            off = "the player is down";
            blend = false;
        }
        if (off == null && GameOwnsCamera() is { } gameReason)
        {
            off = gameReason;
            blend = false;
        }
        if (off != null)
        {
            if (state is State.Running or State.Waiting) Stop(off, blend);
            if (state == State.Idle) status = off;
            return;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
        {
            CannotBegin("no game camera");
            return;
        }
        var game = camMgr->Camera;

        // ---- somebody above has the camera: wait, and start again from the camera they leave ----
        var above = CutsceneOwnsCamera?.Invoke() == true ? CameraOwner.CutsceneShot : coordinator.CurrentOwner;
        if (above > CameraOwner.Duel)
        {
            if (state != State.Waiting)
            {
                if (state == State.Running) log.Info($"DuelCam: waits at {time:0.0} s — {above} has the camera.");
                coordinator.Release(CameraOwner.Duel);
                state = State.Waiting;
                havePose = false;
                wroteDirH = false;
            }
            status = $"duel camera waits — {above} has the camera";
            return;
        }

        if (state != State.Running && !Begin(game, player!.Address, state == State.Waiting))
            return;
        Step(game, player!.Address, dt);
    }

    /// <summary>
    /// A cutscene shot starts blending out (CutsceneShotDirector.HandingBack, inside its tick, before the coordinator
    /// applies): a waiting duel camera starts now, from the camera the cutscene last rendered, and asks for its lens in
    /// this same frame, so the cutscene's blend-out lands on the duel camera's picture.
    /// </summary>
    public void ResumeNow()
    {
        if (state != State.Waiting || failed) return;
        try
        {
            if (!DuelMode || !combatEngine.IsActive || sceneActive() ||
                !combatEngine.State.PlayerState.IsAlive || GameOwnsCamera() != null)
                return;
            var player = Core.Services.ObjectTable.LocalPlayer;
            var camMgr = GameCameraManager.Instance();
            if (player == null || player.Address == nint.Zero || camMgr == null || camMgr->Camera == null) return;
            if (Begin(camMgr->Camera, player.Address, true))
                Step(camMgr->Camera, player.Address, 0f);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DuelCam: could not take the camera back from the cutscene shot; the duel camera stops.");
            Stop("error");
            failed = true;
        }
    }

    private string? GameOwnsCamera()
    {
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return "the game's cutscene took the camera";
        if (clientState.IsGPosing)
            return "group pose took the camera";
        return null;
    }

    private bool CannotBegin(string reason)
    {
        status = "duel camera cannot start — " + reason;
        if (beginFailure == reason) return false;
        beginFailure = reason;
        log.Info($"DuelCam: cannot start yet — {reason}; trying again every frame.");
        return false;
    }

    /// <summary>Starts the solver from the camera as the game last rendered it, and measures how the game's FoV field
    /// maps to the lens it renders and which way its pitch runs.</summary>
    private bool Begin(FFXIVClientStructs.FFXIV.Client.Game.Camera* game, nint playerAddress, bool afterWaiting)
    {
        var sc = &game->CameraBase.SceneCamera;
        var rc = sc->RenderCamera;
        if (rc == null) return CannotBegin("no render camera");
        var projection = ToNumerics(rc->ProjectionMatrix);
        var rendered = CutsceneView.VerticalFovOf(projection);
        if (CutsceneView.FovRatio(rendered, game->FoV) is not { } ratio)
            return CannotBegin("the lens cannot be measured (projection or FoV field unusable)");
        var eye = new Vector3(sc->Position.X, sc->Position.Y, sc->Position.Z);
        var look = new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
        if (!CutsceneView.CanHandOver(eye, look))
            return CannotBegin("the game camera's eye and look-at point are less than 0.1 m apart (first person?) or not finite");
        if (!ReadSubject(playerAddress, false, out _))
            return CannotBegin("the player's position cannot be read");
        beginFailure = null;

        fovRatio = ratio;
        if (MathF.Abs(projection.M11) > 1e-6f)
            aspect = MathF.Abs(projection.M22 / projection.M11);

        // The game's own zoom and pitch as they were: the zoom is held (the wheel is the shot's size), the pitch is
        // held too and what the player drags is read off it. Both are what the player gets back.
        if (!afterWaiting || !haveHeldDistance)
        {
            heldDistance = game->Distance;
            haveHeldDistance = float.IsFinite(heldDistance) && heldDistance > 0f;
        }
        if (!afterWaiting || !haveHeldDirV)
        {
            originalDirV = game->DirV;
            haveHeldDirV = float.IsFinite(originalDirV);
            // Held away from the game's own limits, so that a drag either way still moves it.
            heldDirV = originalDirV;
            var room = (game->DirVMax - game->DirVMin) * 0.25f;
            if (haveHeldDirV && float.IsFinite(room) && room > 0.05f)
                heldDirV = Math.Clamp(originalDirV, game->DirVMin + room, game->DirVMax - room);
            // Which way the game's pitch runs, from the camera as it is: the eye's elevation about what it looks at
            // against the pitch field (the game's convention has the field negative with the eye above).
            var away = eye - look;
            var elevation = MathF.Asin(Math.Clamp(away.Y / MathF.Max(away.Length(), 1e-3f), -1f, 1f));
            pitchSense = MathF.Abs(elevation) > 0.05f && MathF.Abs(originalDirV) > 0.05f ? MathF.Sign(elevation * originalDirV) : -1f;
        }

        solver.Begin(eye, look, rendered);
        picker.Reset();
        opponentId = 0;
        opponentName = "";
        state = State.Running;
        time = 0f;
        havePose = false;
        wroteDirH = false;
        pendingWheel = 0f;
        pendingGiven = pendingTaken = pendingFinish = false;
        guarded.Clear();
        threatened.Clear();
        overrideCount = checkCount = 0;
        checkEye = checkLook = checkFov = checkYaw = 0f;
        loggedOverrideError = false;
        haveLastOut = false;
        last = default;
        lastGive = 0f;
        beatsTaken = beatsRefused = 0;
        ResetWindow();
        nextSummaryAt = Environment.TickCount64 + SummaryMs;

        var from = eye - look;
        log.Info($"DuelCam: {(afterWaiting ? "resumes" : "start")} — framing {config.DuelCameraFraming}; from the camera at azimuth " +
                 $"{Degrees(DeathShotMath.Azimuth(from)):0}°, {from.Length():0.00} m, lens {Degrees(rendered):0.0}° " +
                 $"(game yaw {Degrees(game->DirH):0}°, pitch {Degrees(originalDirV):0}° held at {Degrees(heldDirV):0}° and read {(pitchSense > 0f ? "as" : "against")} the eye's elevation, " +
                 $"FoV field {game->FoV:0.000}, field→lens ratio {fovRatio:0.000}, aspect {aspect:0.00}).");
        return true;
    }

    private void Step(FFXIVClientStructs.FFXIV.Client.Game.Camera* game, nint playerAddress, float dt)
    {
        if (!ReadSubject(playerAddress, false, out var player))
        {
            // Nothing to solve from this frame (the model is being redrawn): the last pose stays.
            Submit();
            return;
        }

        // ---- who the player is fighting ----
        candidates.Clear();
        var target = currentTarget();
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            var obj = (GameObject*)npc.BattleChara;
            candidates.Add(new DuelCandidate(IdOf(npc), new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z),
                npc.IsAlive, npc.IsAttackingPlayer, IsStriking(npc), target != nint.Zero && target == npc.Address));
        }
        var id = picker.Pick(candidates, player.Feet, dt, out var why);
        SimulatedNpc? opponent = null;
        if (id != 0)
            foreach (var npc in npcSelector.SelectedNpcs)
                if (npc.BattleChara != null && IdOf(npc) == id) opponent = npc;
        if (why != null)
        {
            opponentName = opponent?.Name ?? "";
            log.Info(opponent != null
                ? $"DuelCam: opponent at {time:0.0} s — {opponentName} ({why}), {Vector3.Distance(player.Feet, Position(opponent)):0.0} m away."
                : $"DuelCam: opponent at {time:0.0} s — nobody ({why}); the camera frames the player alone.");
        }
        opponentId = id;
        DuelSubject? other = null;
        if (opponent != null && ReadSubject(opponent.Address, !opponent.IsAlive, out var body)) other = body;

        // ---- key moments ----
        if (pendingFinish) Beat(DuelBeat.Finish);
        else if (pendingGiven) Beat(DuelBeat.HitGiven);
        if (pendingTaken) Beat(DuelBeat.HitTaken);
        pendingGiven = pendingTaken = pendingFinish = false;
        PollTelegraphs();

        // ---- the player's hand: yaw and pitch off the game camera's own (since they were written), the wheel ----
        float yawIn = 0f, pitchIn = 0f;
        if (wroteDirH && float.IsFinite(game->DirH))
        {
            var turned = DeathShotMath.AngleDelta(writtenDirH, game->DirH);
            if (MathF.Abs(turned) <= MaxInputStep) yawIn = turned;
        }
        if (wroteDirH && haveHeldDirV && float.IsFinite(game->DirV))
        {
            var tilted = game->DirV - heldDirV;
            if (MathF.Abs(tilted) <= MaxInputStep) pitchIn = tilted * pitchSense;
        }
        var wheel = pendingWheel;
        pendingWheel = 0f;

        var sc = &game->CameraBase.SceneCamera;
        if (sc->RenderCamera != null)
        {
            var projection = ToNumerics(sc->RenderCamera->ProjectionMatrix);
            if (MathF.Abs(projection.M11) > 1e-6f)
            {
                var a = MathF.Abs(projection.M22 / projection.M11);
                if (float.IsFinite(a) && a > 0.2f && a < 8f) aspect = a;
            }
        }

        world.NewFrame(player.Head.Y);
        var o = solver.Step(new DuelInput
        {
            Dt = dt,
            Player = player,
            Opponent = other,
            OpponentId = id,
            Aspect = aspect,
            Framing = config.DuelCameraFraming,
            YawInput = yawIn,
            PitchInput = pitchIn,
            WheelInput = wheel,
        });
        time += Math.Clamp(dt, 0f, 0.1f);

        if (CutsceneView.CanHandOver(o.Eye, o.LookAt) && float.IsFinite(o.Fov) && float.IsFinite(o.MoveYaw))
        {
            poseEye = o.Eye;
            poseLook = o.LookAt;
            poseFov = o.Fov;
            havePose = true;
            writtenDirH = o.MoveYaw;
            wroteDirH = true;
        }
        Submit();
        Report(in o, dt);
        last = o;
        status = o.TwoShot
            ? $"duel camera — you and {(opponentName.Length > 0 ? opponentName : "the opponent")}, {o.Separation:0.0} m apart"
            : "duel camera — nobody to fight: framing you alone";
    }

    private void Beat(DuelBeat kind)
    {
        if (solver.Beat(kind))
        {
            beatsTaken++;
            log.Info($"DuelCam: beat at {time:0.0} s — {BeatWord(kind)}: pushes in.");
        }
        else
        {
            beatsRefused++;
        }
    }

    /// <summary>The telegraphs on the player: one that was guarded, and a big one about to land.</summary>
    private void PollTelegraphs()
    {
        var active = telegraphs.Active;
        for (var i = 0; i < active.Count; i++)
        {
            var t = active[i];
            if (!t.TargetIsPlayer) continue;
            if (t.Outcome == TelegraphOutcome.Guarded)
            {
                if (guarded.Add(t)) Beat(DuelBeat.Guard);
            }
            else if (!t.Resolved && t.IsApproachActive && !t.Request.IsAutoAttack &&
                     (t.WindupTotal >= BigWindup || t.Radius >= BigRadius) &&
                     t.WindupTotal - t.WindupElapsed <= ThreatLead && threatened.Add(t))
            {
                Beat(DuelBeat.Threat);
            }
        }
        if (guarded.Count + threatened.Count == 0) return;
        stale.Clear();
        foreach (var t in guarded) if (!Contains(active, t)) stale.Add(t);
        foreach (var t in stale) guarded.Remove(t);
        stale.Clear();
        foreach (var t in threatened) if (!Contains(active, t)) stale.Add(t);
        foreach (var t in stale) threatened.Remove(t);
    }

    private static bool Contains(IReadOnlyList<ActiveTelegraph> list, ActiveTelegraph item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return true;
        return false;
    }

    private bool IsStriking(SimulatedNpc npc)
    {
        var active = telegraphs.Active;
        for (var i = 0; i < active.Count; i++)
            if (ReferenceEquals(active[i].Source, npc) && active[i].TargetIsPlayer && !active[i].Resolved) return true;
        return false;
    }

    private static uint IdOf(SimulatedNpc npc) => npc.SimulatedEntityId != 0 ? npc.SimulatedEntityId : unchecked((uint)npc.Address) | 1u;

    private static Vector3 Position(SimulatedNpc npc)
    {
        var obj = (GameObject*)npc.BattleChara;
        return new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
    }

    /// <summary>
    /// A fighter for the solver: where it stands, its head (the head bone of a humanoid; the top of anything else), its
    /// width. A body that is down is read from its ragdoll when it has one (the bones stay where it died).
    /// </summary>
    private bool ReadSubject(nint address, bool down, out DuelSubject subject)
    {
        subject = default;
        if (address == nint.Zero) return false;
        var obj = (GameObject*)address;
        var feet = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        var radius = obj->HitboxRadius;
        if (!float.IsFinite(radius) || radius < 0.05f) radius = 0.5f;
        radius = Math.Clamp(radius, 0.3f, 4f);
        var tall = obj->Height;
        if (!float.IsFinite(tall) || tall < 0.3f) tall = MathF.Max(1.5f, radius * 2f);
        tall = Math.Clamp(tall, 0.3f, 12f);

        Vector3? head = null;
        if (down && ragdollFor(address) is { IsActive: true, PhysicsStarted: true } ragdoll &&
            ragdoll.GetBodyWorldPosition("j_kao") is { } fallen && Finite(fallen))
        {
            head = fallen;
            if (ragdoll.GetBodyWorldPosition("j_kosi") is { } hips && Finite(hips))
                feet = new Vector3(hips.X, MathF.Min(hips.Y, fallen.Y) - 0.1f, hips.Z);
        }
        head ??= bones.GetBoneWorldPos(address, "j_kao") is { } bone && Finite(bone) && MathF.Abs(bone.Y - feet.Y) < 15f ? bone : null;
        subject = new DuelSubject(feet, head ?? feet + new Vector3(0f, tall, 0f), radius);
        return subject.IsFinite;
    }

    // ------------------------------------------------------------------
    // Coordinator requests
    // ------------------------------------------------------------------

    /// <summary>This frame's request: the lens (as the game's FoV field), the game camera's yaw (what the player's
    /// movement is relative to), and the game's own zoom and pitch held.</summary>
    private void Submit() => coordinator.Submit(CameraOwner.Duel, new CameraRequest
    {
        Fov = havePose ? CutsceneView.GameFovFor(poseFov, fovRatio) : null,
        DirH = wroteDirH ? writtenDirH : null,
        DirV = haveHeldDirV ? heldDirV : null,
        Distance = haveHeldDistance ? heldDistance : null,
    });

    /// <summary>While the picture goes back to the game's camera: the lens goes with it, and the game gets its own
    /// pitch back. The yaw stays where the duel camera left it, so the game's camera looks the way the picture did.</summary>
    private void SubmitBlendOut()
    {
        float? field = null;
        if (CutsceneView.GameFovFor(poseFov, fovRatio) is { } shot && coordinator.SavedFov is { } own && float.IsFinite(own))
            field = shot + (own - shot) * CutPose.Ease(blendOutTime / BlendOutSeconds);
        coordinator.Submit(CameraOwner.Duel, new CameraRequest
        {
            Fov = field,
            DirV = haveHeldDirV ? originalDirV : null,
            Distance = haveHeldDistance ? heldDistance : null,
        });
    }

    private void EndBlendOut()
    {
        coordinator.Release(CameraOwner.Duel);
        state = State.Idle;
        havePose = false;
        haveHeldDistance = haveHeldDirV = false;
        wroteDirH = false;
    }

    // ------------------------------------------------------------------
    // Camera hand-over
    // ------------------------------------------------------------------

    /// <summary>
    /// Inside the game's camera update, right after it chose its eye and look-at point (ActiveCameraController's
    /// SetCameraLookAt detour, main camera only; before the death shot's and the cutscene shot's own overrides, which
    /// win): the duel camera's pose, as the solver gave it. Ending, 0.3 s from the last pose to the game's own camera.
    /// </summary>
    public bool OverridePose(ref Vector3 lookAt, ref Vector3 eye)
    {
        if (!havePose || state is not (State.Running or State.BlendOut)) return false;
        try
        {
            if (state == State.BlendOut)
            {
                var w = CutPose.Ease(blendOutTime / BlendOutSeconds);
                var e = Vector3.Lerp(poseEye, eye, w);
                var l = Vector3.Lerp(poseLook, lookAt, w);
                if (!CutsceneView.CanHandOver(e, l)) return false;
                eye = e;
                lookAt = l;
                return true;
            }
            lookAt = poseLook;
            eye = poseEye;
            wroteThisUpdate = true;
            supersededThisUpdate = false;
            overrideCount++;
            return true;
        }
        catch (Exception ex)
        {
            if (!loggedOverrideError)
            {
                loggedOverrideError = true;
                log.Warning(ex, "DuelCam: pose override failed; leaving the game's camera alone this frame.");
            }
            return false;
        }
    }

    /// <summary>A death shot or a cutscene shot wrote its own pose over this update's.</summary>
    public void NotePoseSuperseded() => supersededThisUpdate = true;

    /// <summary>After the game's camera update: stop a camera nobody ticks any more, and check once per start that
    /// the game kept the pose and the yaw it was handed.</summary>
    public void OnPostCameraUpdate(CameraBase* camera)
    {
        if (camera == null) return;
        var wrote = wroteThisUpdate && !supersededThisUpdate;
        wroteThisUpdate = false;
        supersededThisUpdate = false;
        if (state == State.Idle) return;

        updatesSinceTick++;
        if (updatesSinceTick > 1 && Environment.TickCount64 - lastTickAt > TickStallMs)
        {
            log.Warning($"DuelCam: no tick for {Environment.TickCount64 - lastTickAt} ms ({updatesSinceTick} camera updates); stopping.");
            Stop("tick stalled");
            return;
        }
        if (wrote && state == State.Running)
            CheckTaken(camera);
    }

    private void CheckTaken(CameraBase* camera)
    {
        if (checkCount >= CheckFrames) return;
        var sc = &camera->SceneCamera;
        var eyeErr = Vector3.Distance(new Vector3(sc->Position.X, sc->Position.Y, sc->Position.Z), poseEye);
        var lookErr = Vector3.Distance(new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z), poseLook);
        var rc = sc->RenderCamera;
        if (rc != null)
        {
            var rendered = CutsceneView.VerticalFovOf(ToNumerics(rc->ProjectionMatrix));
            if (float.IsFinite(rendered))
                checkFov = MathF.Max(checkFov, MathF.Abs(rendered - CutsceneView.ClampFov(poseFov)) * 180f / MathF.PI);
        }
        if (float.IsFinite(eyeErr)) checkEye = MathF.Max(checkEye, eyeErr);
        if (float.IsFinite(lookErr)) checkLook = MathF.Max(checkLook, lookErr);
        // The yaw the game ends the update with against the one written before it: what the player turned in between.
        var game = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera;
        if (wroteDirH && float.IsFinite(game->DirH))
            checkYaw = MathF.Max(checkYaw, MathF.Abs(Degrees(DeathShotMath.AngleDelta(writtenDirH, game->DirH))));
        if (++checkCount < CheckFrames) return;

        var away = poseEye - poseLook;
        var text = $"DuelCam: camera check over {CheckFrames} frames — scene eye off by up to {checkEye:0.000} m, look-at by {checkLook:0.000} m, " +
                   $"lens by {checkFov:0.0}°; the game's yaw up to {checkYaw:0.0}° off the one written (the player's hand, or the game not keeping it); " +
                   $"now: rendered yaw {Degrees(DeathShotMath.Azimuth(away)):0}°, movement yaw {Degrees(writtenDirH):0}°.";
        if (checkEye > 0.05f || checkLook > 0.05f)
            log.Warning(text + " The game did not keep the pose it was handed.");
        else if (checkFov > 1.5f)
            log.Warning(text + " The lens is not the one asked for (FoV field ratio?).");
        else
            log.Info(text);
    }

    /// <summary>End the duel camera. <paramref name="blend"/>: ease back to the game's camera over 0.3 s; otherwise
    /// the game's camera (or whoever took over) is there on the next update.</summary>
    public void Stop(string reason = "stopped", bool blend = false)
    {
        pendingWheel = 0f;
        beginFailure = null;
        if (state is State.Idle or State.BlendOut)
        {
            if (!blend && state == State.BlendOut) EndBlendOut();
            return;
        }
        if (state == State.Running)
            log.Info($"DuelCam: end after {time:0.0} s ({reason}) — pose handed to the game in {overrideCount} camera update(s); " +
                     $"{last.SideChanges} crossing(s) of the line, side picked again {last.Reanchors} time(s), {beatsTaken} beat(s) ({beatsRefused} refused).");
        else
            log.Info($"DuelCam: end while waiting ({reason}).");
        if (blend && state == State.Running && havePose)
        {
            state = State.BlendOut;
            blendOutTime = 0f;
            SubmitBlendOut();
        }
        else
        {
            state = State.BlendOut;
            EndBlendOut();
        }
        status = reason;
    }

    // ------------------------------------------------------------------
    // Log
    // ------------------------------------------------------------------

    private void Report(in DuelOutput o, float dt)
    {
        if (o.SideChanged)
            log.Info($"DuelCam: side at {time:0.0} s — crosses the line to the {SideWord(o.Side)} ({o.Reason}); crossing {o.SideChanges} since the start.");
        else if (o.Reanchored && o.Reason != "start")
            log.Info($"DuelCam: side at {time:0.0} s — picked again, {SideWord(o.Side)} ({(o.Reason == "crossed" ? "the fighters crossed over" : "another opponent")}); the camera stays on its side of the line.");
        if (MathF.Abs(o.Give - lastGive) > 1e-3f)
        {
            log.Info(o.Give > lastGive
                ? $"DuelCam: view blocked at {time:0.0} s — the camera goes {Degrees(o.Give):0}° further round on its side."
                : $"DuelCam: at {time:0.0} s the usual place is free again ({Degrees(o.Give):0}° further round left).");
            lastGive = o.Give;
        }
        if (o.Cut && haveLastOut)
            log.Info($"DuelCam: cut at {time:0.0} s — the player was moved in one frame; the camera went with them.");

        var direction = o.LookAt - o.Eye;
        var length = direction.Length();
        if (length > 1e-4f) direction /= length;
        if (haveLastOut && !o.Cut && dt > 1e-4f)
        {
            windowEyeSpeed = MathF.Max(windowEyeSpeed, Vector3.Distance(o.Eye, lastOutEye) / dt);
            windowViewRate = MathF.Max(windowViewRate, Degrees(MathF.Acos(Math.Clamp(Vector3.Dot(direction, lastOutDir), -1f, 1f))) / dt);
        }
        lastOutEye = o.Eye;
        lastOutDir = direction;
        haveLastOut = true;
        windowFrames++;
        windowRays += world.Rays;
        windowRaysMax = Math.Max(windowRaysMax, world.Rays);

        var now = Environment.TickCount64;
        if (now < nextSummaryAt) return;
        nextSummaryAt = now + SummaryMs;
        var ground = world.GroundBelow(o.Eye + new Vector3(0f, DuelCameraSolver.GroundRayLift, 0f));
        log.Info($"DuelCam: {time:0.0} s — {(o.TwoShot ? $"two-shot with {opponentName}, {o.Separation:0.0} m apart, camera {Degrees(o.OffAxis):+0;-0}° off behind the player ({SideWord(o.Side)})" : "the player alone")}, " +
                 $"{o.Distance:0.00} m from the player, lens {Degrees(o.Fov):0.0}°, eye {(ground is { } g ? $"{o.Eye.Y - g:0.00} m above ground" : "over no ground")}" +
                 $"{(o.Pull < 0.98f ? $", pulled in to {o.Pull * 100f:0} %" : "")}{(o.Lift > 0.01f ? $", lifted {o.Lift:0.00} m" : "")}; " +
                 $"rendered yaw {Degrees(o.Yaw):0}°, movement yaw {Degrees(o.MoveYaw):0}° ({Degrees(DeathShotMath.AngleDelta(o.Yaw, o.MoveYaw)):+0;-0}°); " +
                 $"player yaw {Degrees(o.UserYaw):0}°, pitch {Degrees(o.UserPitch):0}°, size ×{o.UserSize:0.00}; " +
                 $"last 5 s: eye up to {windowEyeSpeed:0.0} m/s, view up to {windowViewRate:0}°/s, " +
                 $"{(windowFrames > 0 ? (float)windowRays / windowFrames : 0f):0.0} rays/frame (max {windowRaysMax}); " +
                 $"crossings {o.SideChanges}, picked again {o.Reanchors}, beats {beatsTaken} (+{beatsRefused} refused); handed {overrideCount}.");
        ResetWindow();
    }

    private void ResetWindow()
    {
        windowEyeSpeed = windowViewRate = 0f;
        windowFrames = windowRays = windowRaysMax = 0;
    }

    private static string SideWord(int side) => side > 0 ? "side +" : "side −";

    private static string BeatWord(DuelBeat kind) => kind switch
    {
        DuelBeat.HitGiven => "a heavy hit on the opponent",
        DuelBeat.HitTaken => "a heavy hit on the player",
        DuelBeat.Guard => "a guard",
        DuelBeat.Threat => "a big attack about to land",
        _ => "the opponent goes down",
    };

    private static float Degrees(float radians) => radians * 180f / MathF.PI;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Matrix4x4 ToNumerics(in FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    public void Dispose() => Stop("unloaded");

    // ------------------------------------------------------------------
    // The world
    // ------------------------------------------------------------------

    /// <summary>
    /// The solver's world, answered with the game's collision, the way the death shots' world is: ground and walls by
    /// <see cref="BGCollisionModule.RaycastMaterialFilter"/>, water by the water-material lookup. Characters are not
    /// in it. Identical queries within one frame are answered once.
    /// </summary>
    private sealed class GameWorld : IDeathShotWorld
    {
        private const float RayLength = 80f;
        private const float WaterSearchAbove = 4f, WaterSearchBelow = 20f;

        private readonly Dictionary<(int, int, int), float?> ground = new();
        private readonly Dictionary<(int, int, int), float?> water = new();
        private float subjectY = float.NaN;

        public int Rays { get; private set; }

        public void NewFrame(float subjectHeight)
        {
            ground.Clear();
            water.Clear();
            Rays = 0;
            subjectY = subjectHeight;
        }

        private static (int, int, int) Key(Vector3 p) =>
            ((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f));

        /// <summary>The ground under the eye: from where the solver starts the ray, and, when that start is lower
        /// than the same lift above the player's head, from that height as well (a surface between the two is one
        /// the first ray started under: the eye is inside a slope or a bank).</summary>
        public float? GroundBelow(Vector3 from)
        {
            var key = Key(from);
            if (ground.TryGetValue(key, out var known)) return known;
            var result = Down(from);
            if (float.IsFinite(subjectY))
            {
                var top = subjectY + DuelCameraSolver.GroundRayLift;
                if (top > from.Y + 0.05f && Down(new Vector3(from.X, top, from.Z)) is { } higher && higher > from.Y)
                    result = higher;
            }
            ground[key] = result;
            return result;
        }

        private float? Down(Vector3 from)
        {
            try
            {
                Rays++;
                if (BGCollisionModule.RaycastMaterialFilter(from, new Vector3(0f, -1f, 0f), out var hit, RayLength) &&
                    float.IsFinite(hit.Point.Y))
                    return hit.Point.Y;
            }
            catch
            {
                // Collision module unavailable (zone transition): no ground.
            }
            return null;
        }

        public float? WaterBelow(Vector3 from)
        {
            var key = Key(from);
            if (water.TryGetValue(key, out var known)) return known;
            float? result = null;
            try
            {
                Rays++;
                if (WorldCollisionMesh.TryFindWaterSurface(from, WaterSearchAbove, WaterSearchBelow, out var surface))
                    result = surface;
            }
            catch
            {
            }
            water[key] = result;
            return result;
        }

        public bool Blocked(Vector3 from, Vector3 to, out float distance)
        {
            distance = 0f;
            var along = to - from;
            var length = along.Length();
            if (!(length > 1e-3f)) return false;
            try
            {
                Rays++;
                if (!BGCollisionModule.RaycastMaterialFilter(from, along / length, out var hit, length))
                    return false;
                distance = Vector3.Distance(hit.Point, from);
                return float.IsFinite(distance) && distance < length;
            }
            catch
            {
                return false;
            }
        }
    }
}
