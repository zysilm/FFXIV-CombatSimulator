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
    /// <summary>With nobody to frame the player against for this long, the camera goes back to the game.</summary>
    private const float NobodySeconds = 2f;
    private const float StickDeadZone = 0.2f;

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
    private readonly IGamepadState gamepad;
    private readonly IGameConfig gameConfig;
    private readonly IPluginLog log;
    private readonly CameraCollisionWorld world = new();
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
    private float heldDistance, originalDirV, heldDirV;
    /// <summary>
    /// The game's pitch field against the eye's elevation about what it looks at: opposite. The camera struct documents
    /// the field as "positive is looking up", i.e. the eye below. A constant, not measured: read off the picture at a
    /// start it came out wrong whenever the camera was close and nearly level (the game tilts its look-at point with
    /// the zoom, so the eye can sit above it with a positive pitch), and the drag was then inverted.
    /// The player's own "invert vertical" setting needs no handling: the game has applied it by the time it changes
    /// its pitch field, and only that change is read.
    /// </summary>
    private const float PitchSense = -1f;
    /// <summary>The game's FoV field → rendered lens ratio, measured with the lens at rest and kept for the session.</summary>
    private float? sessionRatio;
    private bool lensAtRest;
    private float lastField = float.NaN;
    private int fieldStableTicks;
    private bool haveHeldDistance, haveHeldDirV;
    private float writtenDirH;
    private bool wroteDirH;
    private float blendOutTime;

    // the player's hand
    private float pendingWheel;
    private bool handWasOn;
    private float ignoredYaw;

    // the fighters
    private HeadBone playerHead, opponentHead;
    private float nobodyFor;
    private int statusKey = int.MinValue;
    private bool saidStandard;

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
        IGamepadState gamepad,
        IGameConfig gameConfig,
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
        this.gamepad = gamepad;
        this.gameConfig = gameConfig;
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
        TrackLens();
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
            Say("duel camera stopped after an error (see the log); pick the mode again to restart it");
        }
    }

    /// <summary>Every tick: whether the camera's lens is at rest, i.e. nobody's FoV request is applied and the game's
    /// FoV field has not changed over the last ticks (so the picture last rendered was rendered with it).</summary>
    private void TrackLens()
    {
        lensAtRest = false;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
            {
                fieldStableTicks = 0;
                return;
            }
            var field = camMgr->Camera->FoV;
            fieldStableTicks = float.IsFinite(field) && MathF.Abs(field - lastField) < 1e-5f ? Math.Min(fieldStableTicks + 1, 1000) : 0;
            lastField = field;
            lensAtRest = fieldStableTicks >= 3 && coordinator.SavedFov == null;
        }
        catch
        {
            fieldStableTicks = 0;
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
            if (state == State.Idle) Say(off);
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
                if (state == State.Running)
                {
                    log.Info($"DuelCam: waits at {time:0.0} s — {above} has the camera.");
                    // The game gets its own pitch back for whoever has the camera now.
                    RestorePitch();
                }
                coordinator.Release(CameraOwner.Duel);
                state = State.Waiting;
                havePose = false;
                wroteDirH = false;
            }
            if (statusKey != -10 - (int)above)
            {
                statusKey = -10 - (int)above;
                status = $"duel camera waits — {above} has the camera";
            }
            return;
        }

        // ---- nobody to frame the player against: the game's own camera, until somebody is ----
        if (state != State.Running && !AnyOpponent(player!.Address))
        {
            if (state == State.Waiting) Stop("nobody to fight");
            Say("duel camera waits — nobody to fight");
            return;
        }

        if (state != State.Running && !Begin(game, player!.Address, state == State.Waiting))
            return;
        Step(game, player!.Address, dt);
    }

    /// <summary>A status line that is a constant (the ones with names or numbers in them are rebuilt only when what
    /// they say changes, by <see cref="statusKey"/>).</summary>
    private void Say(string text)
    {
        status = text;
        statusKey = int.MinValue;
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
            if (AnyOpponent(player.Address) && Begin(camMgr->Camera, player.Address, true))
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
        if (beginFailure != reason) Say("duel camera cannot start — " + reason);
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
        if (!float.IsFinite(rendered) || rendered <= 0.01f)
            return CannotBegin("the lens cannot be measured (projection unusable)");
        // How the game's FoV field maps to the lens it renders is a property of the game, not of this start. It can
        // only be measured with the camera's lens at rest: right after another owner let go (a death shot, a cutscene
        // shot) the field is already the game's own again while the picture last rendered still has that owner's
        // lens, and the two do not belong together. So: measured when at rest, kept for the session otherwise.
        var measuredNow = false;
        if (lensAtRest && CutsceneView.FovRatio(rendered, game->FoV) is { } fresh && fresh > 0.5f && fresh < 2f)
        {
            sessionRatio = fresh;
            measuredNow = true;
        }
        if (sessionRatio is not { } ratio)
            return CannotBegin("the lens is not at rest yet (another camera's lens was on a moment ago)");
        var eye = new Vector3(sc->Position.X, sc->Position.Y, sc->Position.Z);
        var look = new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
        if (!CutsceneView.CanHandOver(eye, look))
            return CannotBegin("the game camera's eye and look-at point are less than 0.1 m apart (first person?) or not finite");
        if (!ReadSubject(playerAddress, false, ref playerHead, out _))
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
        nobodyFor = 0f;
        handWasOn = false;
        ignoredYaw = 0f;
        statusKey = int.MinValue;
        var legacy = LegacyMovement();

        var from = eye - look;
        log.Info($"DuelCam: {(afterWaiting ? "resumes" : "start")} — framing {config.DuelCameraFraming}; from the camera at azimuth " +
                 $"{Degrees(DeathShotMath.Azimuth(from)):0}°, {from.Length():0.00} m, lens {Degrees(rendered):0.0}° " +
                 $"(game yaw {Degrees(game->DirH):0}°, pitch {Degrees(originalDirV):0}° held at {Degrees(heldDirV):0}°; a drag is read off the game's pitch as the game counts it, " +
                 $"positive = looking up = the eye goes down, so it moves the view as it does in the game's own camera, inverted or not; " +
                 $"FoV field {game->FoV:0.000}, field→lens ratio {fovRatio:0.000} ({(measuredNow ? "measured now" : "kept from an earlier start: the lens was not at rest")}), aspect {aspect:0.00}); " +
                 $"movement mode {(legacy == null ? "unknown" : legacy.Value ? "Legacy" : "Standard")}.");
        if (legacy == false && !saidStandard)
        {
            saidStandard = true;
            log.Info("DuelCam: the game's movement mode is Standard — expected: W goes at the opponent and Q/E (strafe) go round them, but A/D turn the " +
                     "character on the spot and the camera does not follow the turn (the duel camera keeps its own yaw). Legacy moves by the camera: W at the opponent, A/D round them.");
        }
        return true;
    }

    private void Step(FFXIVClientStructs.FFXIV.Client.Game.Camera* game, nint playerAddress, float dt)
    {
        if (!ReadSubject(playerAddress, false, ref playerHead, out var player))
        {
            // Nothing to solve from this frame (the model is being redrawn): the last pose stays.
            Submit();
            return;
        }

        // ---- who the player is fighting ----
        candidates.Clear();
        var target = currentTarget();
        var selected = npcSelector.SelectedNpcs;
        for (var n = 0; n < selected.Count; n++)
        {
            var npc = selected[n];
            if (npc.BattleChara == null) continue;
            var obj = (GameObject*)npc.BattleChara;
            candidates.Add(new DuelCandidate(IdOf(npc), new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z),
                npc.IsAlive, npc.IsAttackingPlayer, IsStriking(npc), target != nint.Zero && target == npc.Address));
        }
        var id = picker.Pick(candidates, player.Feet, dt, out var why);
        SimulatedNpc? opponent = null;
        if (id != 0)
            for (var n = 0; n < selected.Count; n++)
                if (selected[n].BattleChara != null && IdOf(selected[n]) == id) opponent = selected[n];
        if (why != null)
        {
            opponentName = opponent?.Name ?? "";
            log.Info(opponent != null
                ? $"DuelCam: opponent at {time:0.0} s — {opponentName} ({why}), {Vector3.Distance(player.Feet, Position(opponent)):0.0} m away."
                : $"DuelCam: opponent at {time:0.0} s — nobody ({why}); the camera goes back to the game in {NobodySeconds:0} s unless somebody comes.");
        }
        opponentId = id;
        DuelSubject? other = null;
        if (opponent != null && ReadSubject(opponent.Address, !opponent.IsAlive, ref opponentHead, out var body)) other = body;

        // Nobody for a while: the game's own camera (free pitch and zoom), until somebody is there again.
        nobodyFor = other == null ? nobodyFor + dt : 0f;
        if (nobodyFor >= NobodySeconds)
        {
            Stop("nobody to fight", blend: true);
            return;
        }

        // ---- key moments ----
        if (pendingFinish) Beat(DuelBeat.Finish);
        else if (pendingGiven) Beat(DuelBeat.HitGiven);
        if (pendingTaken) Beat(DuelBeat.HitTaken);
        pendingGiven = pendingTaken = pendingFinish = false;
        PollTelegraphs();

        // ---- the player's hand: yaw and pitch off the game camera's own (since they were written), the wheel ----
        // Only while a hand is on the camera (HandOnCamera); what the game turns by itself is written over.
        var hand = HandOnCamera();
        float yawIn = 0f, pitchIn = 0f;
        if (wroteDirH && float.IsFinite(game->DirH))
        {
            var turned = DeathShotMath.AngleDelta(writtenDirH, game->DirH);
            if (!hand) ignoredYaw = MathF.Max(ignoredYaw, MathF.Abs(turned));
            else if (MathF.Abs(turned) <= MaxInputStep) yawIn = turned;
        }
        if (wroteDirH && haveHeldDirV && float.IsFinite(game->DirV))
        {
            var tilted = game->DirV - heldDirV;
            if (hand && MathF.Abs(tilted) <= MaxInputStep) pitchIn = tilted * PitchSense;
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
        var key = o.TwoShot ? (int)(opponentId & 0x3FFFFFFF) + 1 : 0;
        if (key != statusKey)
        {
            statusKey = key;
            status = o.TwoShot
                ? $"duel camera — you and {(opponentName.Length > 0 ? opponentName : "the opponent")}"
                : "duel camera — nobody to fight";
        }
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
    private bool ReadSubject(nint address, bool down, ref HeadBone cache, out DuelSubject subject)
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
        head ??= Head(address, ref cache) is { } bone && Finite(bone) && MathF.Abs(bone.Y - feet.Y) < 15f ? bone : null;
        subject = new DuelSubject(feet, head ?? feet + new Vector3(0f, tall, 0f), radius);
        return subject.IsFinite;
    }

    /// <summary>Where a character's head bone was found: looked up by name once per character and skeleton (again
    /// when the model is redrawn with another one); −1: it has none (a monster), and is not looked for again.</summary>
    private struct HeadBone
    {
        public nint Address, Skeleton;
        public int BoneCount, Index;
    }

    private Vector3? Head(nint address, ref HeadBone cache)
    {
        if (bones.TryGetSkeleton(address) is not { } skeleton) return null;
        if (cache.Address != address || cache.Skeleton != (nint)skeleton.HavokSkeleton || cache.BoneCount != skeleton.BoneCount)
        {
            cache = new HeadBone
            {
                Address = address,
                Skeleton = (nint)skeleton.HavokSkeleton,
                BoneCount = skeleton.BoneCount,
                Index = bones.ResolveBoneIndex(skeleton, "j_kao"),
            };
        }
        return cache.Index >= 0 ? bones.GetBoneWorldPos(skeleton, cache.Index) : null;
    }

    /// <summary>
    /// Whether the player's hand is on the camera: a mouse button held (the game turns its camera only while one is)
    /// or the right stick off centre, on this tick or the one before. Only then is a change of the game camera's yaw
    /// or pitch the player's. The game also turns its camera by itself (keyboard turning in the Standard movement
    /// mode, the pivot behind the character while running in Legacy, the gamepad's recentring): that is not the
    /// player dragging the camera, and is written over.
    /// </summary>
    private bool HandOnCamera()
    {
        var now = false;
        try
        {
            now = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 ||
                  gamepad.RightStick.LengthSquared() > StickDeadZone * StickDeadZone;
        }
        catch
        {
            // No input state: nobody is dragging.
        }
        var on = now || handWasOn;
        handWasOn = now;
        return on;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>The game's movement mode (character configuration: Standard turns the character with A/D and the
    /// camera with it, Legacy moves relative to the camera), or null when the client does not say.</summary>
    private bool? LegacyMovement()
    {
        try
        {
            return gameConfig.TryGet(Dalamud.Game.Config.UiControlOption.MoveMode, out uint mode) ? mode == 1 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether somebody is there to frame the player against (alive, selected, in range).</summary>
    private bool AnyOpponent(nint playerAddress)
    {
        var at = ((GameObject*)playerAddress)->Position;
        var list = npcSelector.SelectedNpcs;
        for (var i = 0; i < list.Count; i++)
        {
            var npc = list[i];
            if (npc.BattleChara == null || !npc.IsAlive) continue;
            var obj = (GameObject*)npc.BattleChara;
            var dx = obj->Position.X - at.X;
            var dz = obj->Position.Z - at.Z;
            if (dx * dx + dz * dz <= DuelOpponentPicker.PickRange * DuelOpponentPicker.PickRange) return true;
        }
        return false;
    }

    /// <summary>The game's own pitch, put back once when the duel camera lets go without a blend (it was held at
    /// <see cref="heldDirV"/> meanwhile; the request that held it is gone, so this one value is written directly).</summary>
    private void RestorePitch()
    {
        if (!haveHeldDirV || !float.IsFinite(originalDirV)) return;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null) camMgr->Camera->DirV = originalDirV;
        }
        catch
        {
            // The camera is gone (logout, zone change): nothing to put back.
        }
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
                   $"lens by {checkFov:0.0}°; the game's yaw up to {checkYaw:0.0}° off the one written, of which up to {Degrees(ignoredYaw):0.0}° with no hand on the camera (turned by the game itself; written over); " +
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
            if (!blend && state == State.BlendOut)
            {
                RestorePitch();
                EndBlendOut();
            }
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
            // Ended at once: the request that held the game's pitch goes with it, so the pitch is put back here.
            if (state == State.Running) RestorePitch();
            state = State.BlendOut;
            EndBlendOut();
        }
        Say(reason);
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
}
