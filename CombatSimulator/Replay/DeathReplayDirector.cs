// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Replay.Model;
using CombatSimulator.Safety;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Replay;

/// <summary>
/// The death replay (docs/DEATH_REPLAY.md): records the last 20 s of a simulation (the player and up to 16 of the
/// characters in it nearest to them), and once the player's body has come to rest after a death replays the last
/// N seconds of it, the fall included, through the saved camera shots; then (or on a loop, until the player leaves
/// it) puts everything back as it was. Also the shot editor's free camera.
///
/// States: idle (the feature off, or no simulation) → recording (a simulation runs, the player alive) → waiting
/// (the player is down: still recording, until the body rests) → playing → done (until revived). The ways out of a
/// replay: revive, reset, stop, Esc, "Stop replay", the feature switched off, a zone change, logout, unload, an
/// error; on every one the characters and the camera are put back.
/// </summary>
internal sealed unsafe class DeathReplayDirector : IDisposable
{
    public const int MaxShots = 100;
    public const float MinSeconds = 3f, MaxSeconds = 20f;
    /// <summary>How many characters are recorded besides the player, and how far from them.</summary>
    public const int MaxOthers = 16;
    public const float Radius = 30f;
    private const double RefreshSeconds = 0.5;
    private const long SummaryMs = 10000;
    /// <summary>A ragdoll that has not started this long after the death is not waited for any more.</summary>
    private const double RagdollWaitSeconds = 15.0;
    /// <summary>A body that keeps moving (floating in water, sliding) this long after its physics started (or after
    /// the death, without a ragdoll) is replayed anyway.</summary>
    private const double RestGiveUpSeconds = 10.0;
    private const double MinAfterDeath = 1.0;

    private enum State { Idle, Recording, Waiting, Playing, Done }

    private readonly Configuration config;
    private readonly Simulation.CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly CombatCompanionManager companions;
    private readonly RagdollController playerRagdoll;
    private readonly MovementBlockHook movementBlock;
    private readonly Func<bool> bodyGrabbed;
    private readonly Func<bool> cutsceneOwnsCamera;
    private readonly Func<bool> sceneActive;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IKeyState keyState;
    private readonly IPluginLog log;
    private readonly ReplayRecorder recorder;
    private readonly ReplayPosePlayer player;
    private readonly ReplayCamera camera;

    private State state = State.Idle;
    private bool lateSubscribed;
    private double nextRefresh;
    private long nextSummaryAt;
    private bool escWasDown;
    private string status = "off";

    // this death
    private bool deathSeen;
    private ReplayFrame deathFrame;
    private double deathAt;
    private bool physicsSeen;
    private double restFrom;

    // the replay
    private double windowStart, windowLength;
    private float playTime;
    private int passes;
    private DeathReplayShot[] shots = Array.Empty<DeathReplayShot>();
    private ReplayView heldView;
    private int lastShot = -1;

    // what the window asked for, done in the next tick
    private bool wantStop, wantAdd, wantToggleFree;
    private int wantUpdate = -1, wantGoTo = -1;

    private readonly List<ReplayActor> candidates = new();
    private readonly List<ReplayActor> validating = new();

    public DeathReplayDirector(
        Configuration config,
        CameraModeCoordinator coordinator,
        Simulation.CombatEngine combatEngine,
        NpcSelector npcSelector,
        CombatCompanionManager companions,
        BoneTransformService bones,
        RagdollController playerRagdoll,
        MovementBlockHook movementBlock,
        Func<bool> bodyGrabbed,
        Func<bool> cutsceneOwnsCamera,
        Func<bool> sceneActive,
        ICondition condition,
        IClientState clientState,
        IFramework framework,
        IKeyState keyState,
        Dalamud.Plugin.Services.IGamepadState gamepad,
        IPluginLog log)
    {
        this.config = config;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.companions = companions;
        this.playerRagdoll = playerRagdoll;
        this.movementBlock = movementBlock;
        this.bodyGrabbed = bodyGrabbed;
        this.cutsceneOwnsCamera = cutsceneOwnsCamera;
        this.sceneActive = sceneActive;
        this.condition = condition;
        this.clientState = clientState;
        this.framework = framework;
        this.keyState = keyState;
        this.log = log;
        recorder = new ReplayRecorder(bones, log);
        player = new ReplayPosePlayer(bones, log);
        camera = new ReplayCamera(coordinator, keyState, gamepad, log);
    }

    // ------------------------------------------------------------------
    // For the window
    // ------------------------------------------------------------------

    public string Status => status;
    public bool FreeCamOn => camera.FreeCamOn;
    public bool Playing => state == State.Playing;
    /// <summary>The camera is the replay's (free camera, shots, or blending back): models near the eye stay drawn.</summary>
    public bool CameraRunning => camera.IsRunning;
    /// <summary>The keyboard flies the free camera now (Enemy Control leaves the same keys alone meanwhile).</summary>
    public bool KeysTaken => camera.FreeCamOn;
    /// <summary>Whether shots are placed relative to the body at death now (else to where the player stands).</summary>
    public bool FrameIsDeath => DeathFrameInUse;

    public void RequestToggleFreeCam() => wantToggleFree = true;
    public void RequestAddShot() => wantAdd = true;
    public void RequestUpdateShot(int index) => wantUpdate = index;
    public void RequestGoTo(int index) => wantGoTo = index;
    public void RequestStop() => wantStop = true;
    public void NotifyMouseWheel(float wheel) => camera.NotifyMouseWheel(wheel);

    /// <summary>The camera hand-over, called last in the pose chain (Composition/CameraModule).</summary>
    public bool OverridePose(ref Vector3 lookAt, ref Vector3 eye) => camera.OverridePose(ref lookAt, ref eye);

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    /// <summary>CombatEvents.SimulationStarted: a new battle, new characters.</summary>
    public void OnSimulationStarted()
    {
        if (state == State.Playing) StopReplay("a new simulation");
        recorder.Clear();
        ForgetDeath();
        if (state != State.Idle) state = State.Recording;
    }

    /// <summary>CombatEvents.SimulationReset (also on stop): everyone is put back; the recording starts over.
    /// Touches no game memory here (it can come from the window); the characters are put back on the next frame.</summary>
    public void OnSimulationReset()
    {
        if (state == State.Playing) StopReplay("simulation reset");
        recorder.Clear();
        ForgetDeath();
        if (state != State.Idle) state = State.Recording;
    }

    /// <summary>A zone change: everything stops; what survives is put back on the next frame.</summary>
    public void Stop(string reason)
    {
        if (state == State.Playing) StopReplay(reason);
        camera.LeaveFreeCam(reason);
        camera.Reset();
        movementBlock.HoldLocalPlayer = false;
        recorder.Pause();
        recorder.Clear();
        ForgetDeath();
        state = State.Idle;
        SetStatus();
    }

    /// <summary>Logout: the characters are gone; nothing is touched.</summary>
    public void Abandon(string reason)
    {
        if (state == State.Playing) log.Info($"DeathReplay: replay dropped ({reason}).");
        player.Abandon();
        camera.Reset();
        movementBlock.HoldLocalPlayer = false;
        recorder.Pause();
        recorder.Clear();
        ForgetDeath();
        state = State.Idle;
        SetStatus();
    }

    private void ForgetDeath()
    {
        deathSeen = false;
        physicsSeen = false;
        recorder.ResetSettle();
    }

    // ------------------------------------------------------------------
    // Per frame
    // ------------------------------------------------------------------

    /// <summary>Every framework tick, before the coordinator applies (the camera's request).</summary>
    public void Tick(float dt)
    {
        // Off: one flag check (and whatever is still to be put back).
        if (!config.DeathReplayEnabled && state == State.Idle && camera.Idle && !player.Restoring && !wantToggleFree &&
            wantGoTo < 0 && !wantAdd)
            return;
        try
        {
            TickCore(dt);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathReplay: frame failed; the replay stops and everything is put back.");
            if (state == State.Playing) StopReplay("error");
            camera.LeaveFreeCam("error");
            movementBlock.HoldLocalPlayer = false;
            state = State.Idle;
        }
    }

    private void TickCore(float dt)
    {
        EnsureLateTick();
        var local = Core.Services.ObjectTable.LocalPlayer;
        if (local == null)
        {
            if (state != State.Idle || !camera.Idle) Abandon("no player");
            return;
        }

        // ---- the window's requests ----
        HandleRequests(local);

        // ---- the free camera: the game takes the camera (its cutscene, group pose) → off ----
        if (camera.FreeCamOn && GameOwnsCamera() is { } gameReason) camera.LeaveFreeCam(gameReason);

        // ---- Esc: the free camera first, then the replay ----
        // (Only typing in one of the plugin's text fields keeps the keys from the free camera and the replay, as for
        // Enemy Control: a focused plugin window does not.)
        var typing = ImGui.GetIO().WantTextInput;
        var esc = KeyDown(VirtualKey.ESCAPE) && !typing;
        var escPressed = esc && !escWasDown;
        escWasDown = esc;
        if (escPressed)
        {
            if (camera.FreeCamOn) camera.LeaveFreeCam("Esc");
            else if (state == State.Playing) wantStop = true;
        }

        // ---- the state ----
        var enabled = config.DeathReplayEnabled;
        if (!enabled || !combatEngine.IsActive)
        {
            if (state == State.Playing) StopReplay(!enabled ? "turned off" : "no simulation");
            if (state != State.Idle)
            {
                recorder.Pause();
                recorder.Clear();
                ForgetDeath();
                state = State.Idle;
            }
        }
        else
        {
            var alive = combatEngine.State.PlayerState.IsAlive;
            if (alive)
            {
                if (state == State.Playing) StopReplay("revived");
                if (state != State.Recording) ForgetDeath();
                state = State.Recording;
            }
            else
            {
                if (state is State.Idle or State.Recording)
                {
                    // The moment of death: the frame the shots are placed in.
                    if (!deathSeen)
                    {
                        deathSeen = true;
                        deathFrame = new ReplayFrame(local.Position, local.Rotation);
                        deathAt = ReplayRecorder.Now;
                        restFrom = deathAt;
                        physicsSeen = false;
                        recorder.ResetSettle();
                        log.Info($"DeathReplay: death at ({deathFrame.Origin.X:0.0}, {deathFrame.Origin.Y:0.0}, {deathFrame.Origin.Z:0.0}), " +
                                 $"facing {deathFrame.Facing / ReplayMath.Deg:0}°; waiting for the body to rest.");
                    }
                    state = State.Waiting;
                }
                if (state == State.Waiting)
                {
                    if (ReadyToPlay(out var why)) BeginReplay();
                    else status = "waiting for the body to rest" + (why != null ? $" ({why})" : "");
                }
            }
        }

        if (wantStop)
        {
            wantStop = false;
            if (state == State.Playing) StopReplay("stopped");
        }

        if (state == State.Playing) AdvanceReplay(dt);

        // ---- recording ----
        var record = state is State.Recording or State.Waiting;
        if (record && !recorder.Recording) recorder.Start();
        else if (!record && recorder.Recording) recorder.Pause();
        if (record && ReplayRecorder.Now >= nextRefresh)
        {
            nextRefresh = ReplayRecorder.Now + RefreshSeconds;
            ChooseActors(local);
        }
        if (record) Summary();

        // ---- the camera ----
        movementBlock.HoldLocalPlayer = camera.FreeCamOn;
        camera.Tick(dt, keyboardFree: !typing);
        SetStatus();
    }

    private void HandleRequests(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter local)
    {
        if (wantToggleFree)
        {
            wantToggleFree = false;
            if (camera.FreeCamOn) camera.LeaveFreeCam("button");
            else if (GameOwnsCamera() is { } reason) log.Info($"DeathReplay: the free camera cannot start — {reason}.");
            else camera.EnterFreeCam();
        }
        if (wantGoTo >= 0)
        {
            var i = wantGoTo;
            wantGoTo = -1;
            if (i < config.DeathReplayShots.Count && GameOwnsCamera() == null)
                camera.EnterFreeCam(ReplayMath.ViewFromShot(FrameNow(local), config.DeathReplayShots[i]));
        }
        if (wantAdd)
        {
            wantAdd = false;
            if (config.DeathReplayShots.Count < MaxShots && (camera.FreeCamOn ? camera.FreeView : ReplayCamera.RenderedView()) is { IsFinite: true } view)
            {
                config.DeathReplayShots.Add(ReplayMath.ShotFromView(FrameNow(local), view));
                config.Save();
                log.Info($"DeathReplay: shot {config.DeathReplayShots.Count} added ({(DeathFrameInUse ? "relative to the body at death" : "relative to where you stand")}).");
            }
        }
        if (wantUpdate >= 0)
        {
            var i = wantUpdate;
            wantUpdate = -1;
            if (i < config.DeathReplayShots.Count && camera.FreeCamOn)
            {
                config.DeathReplayShots[i] = ReplayMath.ShotFromView(FrameNow(local), camera.FreeView);
                config.Save();
                log.Info($"DeathReplay: shot {i + 1} updated.");
            }
        }
    }

    /// <summary>A death of this simulation is being shown: shots are placed relative to the body at death.</summary>
    private bool DeathFrameInUse => deathSeen && combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;

    private ReplayFrame FrameNow(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter local) =>
        DeathFrameInUse ? deathFrame : new ReplayFrame(local.Position, local.Rotation);

    /// <summary>The body has come to rest: the ragdoll (when one is to come) has started and the body has kept still
    /// (or, without one, the death animation has come to its end and holds). Not while it is held, nor while a
    /// cutscene shot or a scene has the camera.</summary>
    private bool ReadyToPlay(out string? why)
    {
        why = null;
        var since = ReplayRecorder.Now - deathAt;
        if (since < MinAfterDeath) return false;
        if (config.EnableRagdoll && !physicsSeen)
        {
            if (playerRagdoll is { IsActive: true, PhysicsStarted: true })
            {
                // From here the body moves under physics: what kept still before (the death animation's end) does
                // not count.
                physicsSeen = true;
                restFrom = ReplayRecorder.Now;
                recorder.ResetSettle();
                return false;
            }
            if (since < RagdollWaitSeconds)
            {
                why = "the ragdoll has not started yet";
                return false;
            }
        }
        if (bodyGrabbed())
        {
            why = "the body is held";
            return false;
        }
        if (cutsceneOwnsCamera() || sceneActive() || GameOwnsCamera() != null)
        {
            why = "another shot has the camera";
            return false;
        }
        if (!recorder.PlayerSettled)
        {
            if (ReplayRecorder.Now - restFrom < RestGiveUpSeconds) return false;
            log.Info($"DeathReplay: the body has not come to rest {ReplayRecorder.Now - restFrom:0} s on; replaying anyway.");
        }
        return true;
    }

    private void BeginReplay()
    {
        var local = Core.Services.ObjectTable.LocalPlayer!;
        var playerActor = MakeActor(local, isPlayer: true);
        var playerTrack = playerActor != null ? recorder.TrackOf(playerActor) : null;
        var seconds = Math.Clamp(config.DeathReplaySeconds, MinSeconds, MaxSeconds);
        if (playerTrack == null ||
            !ReplayWindow.For(playerTrack.OldestTime, playerTrack.NewestTime, playerTrack.NewestTime, seconds, 1.0, out var start, out var stop))
        {
            log.Info("DeathReplay: nothing to replay — less than a second of you was recorded.");
            state = State.Done;
            return;
        }
        recorder.Pause();

        // The recorded characters that are still the same objects now.
        var list = new List<(ReplayActor, PoseTrack)>();
        var gone = 0;
        foreach (var (actor, track) in recorder.Tracks())
        {
            if (track.NewestTime < start) continue; // recorded only before the window
            if (!IsLive(actor))
            {
                gone++;
                continue;
            }
            list.Add((actor, track));
        }

        windowStart = start;
        windowLength = stop - start;
        playTime = 0f;
        passes = 0;
        lastShot = -1;
        shots = config.DeathReplayShots.ConvertAll(s => s.Clone()).ToArray();
        heldView = ReplayCamera.RenderedView() ?? new ReplayView(local.Position + new Vector3(0, 2, 3), -Vector3.UnitZ, ReplayMath.DefaultFov);
        foreach (var (actor, _) in list) actor.Valid = false; // validated in the late tick before the first render
        player.Begin(list, windowStart);
        state = State.Playing;
        camera.SetShotView(CurrentView());
        log.Info($"DeathReplay: replaying {windowLength:0.0} s (asked {seconds:0.0} s) up to the body coming to rest " +
                 $"{ReplayRecorder.Now - deathAt:0.0} s after the death — {list.Count} character(s), {gone} no longer there; " +
                 $"{(shots.Length > 0 ? $"{shots.Length} shot(s), {ShotSchedule.SegmentLength((float)windowLength, shots.Length):0.00} s each" : "no shots: the view as it is")}" +
                 $"{(config.DeathReplayLoop ? ", looping" : "")}.");
    }

    private ReplayView CurrentView()
    {
        if (shots.Length == 0) return heldView;
        var i = ShotSchedule.IndexAt(playTime, (float)windowLength, shots.Length, config.DeathReplayLoop);
        if (i != lastShot && lastShot >= 0)
            log.Debug($"DeathReplay: shot {i + 1} at {playTime:0.00} s.");
        lastShot = i;
        return ReplayMath.ViewFromShot(deathFrame, shots[i]);
    }

    private void AdvanceReplay(float dt)
    {
        playTime += Math.Clamp(dt, 0f, 0.1f);
        var length = (float)windowLength;
        if (playTime >= length)
        {
            if (!config.DeathReplayLoop)
            {
                StopReplay("finished");
                return;
            }
            passes = (int)(playTime / length);
        }
        var inPass = config.DeathReplayLoop ? ShotSchedule.Wrap(playTime, length) : Math.Min(playTime, length);
        player.SetTime(windowStart + inPass);
        camera.SetShotView(CurrentView());
    }

    private void StopReplay(string reason)
    {
        if (state != State.Playing) return;
        var (mean, max, frames) = player.Costs();
        log.Info($"DeathReplay: end ({reason}) after {playTime:0.0} s{(passes > 0 ? $", {passes + 1} pass(es)" : "")} — " +
                 $"{player.Count} character(s), {player.SkippedCount()} skipped; writing the poses took {mean:0.000} ms a frame " +
                 $"on average, {max:0.000} ms at most, over {frames} frame(s). Putting everything back.");
        player.End();
        camera.SetShotView(null);
        state = State.Done;
        // Once revived the recording goes on from here.
    }

    // ------------------------------------------------------------------
    // The characters
    // ------------------------------------------------------------------

    private ReplayActor? MakeActor(Dalamud.Game.ClientState.Objects.Types.IGameObject o, bool isPlayer)
    {
        if (o.Address == nint.Zero) return null;
        return new ReplayActor
        {
            Address = o.Address,
            ObjectIndex = o.ObjectIndex,
            EntityId = o.EntityId,
            IsPlayer = isPlayer,
            Name = isPlayer ? "you" : o.Name.TextValue,
        };
    }

    /// <summary>The player, and the simulation's characters (its enemies, map enemies taken into it, allies) within
    /// <see cref="Radius"/> of the player, nearest first, at most <see cref="MaxOthers"/>. Only objects the object
    /// table lists now (so nothing is read from an address that may be gone).</summary>
    private void ChooseActors(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter local)
    {
        var wanted = new HashSet<nint>();
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address != nint.Zero) wanted.Add(npc.Address);
        foreach (var c in companions.Companions)
            if (c.IsSpawned && c.Address != nint.Zero) wanted.Add(c.Address);

        var near = new List<(float Distance, Dalamud.Game.ClientState.Objects.Types.IGameObject Object)>();
        var at = local.Position;
        if (wanted.Count > 0)
        {
            foreach (var o in Core.Services.ObjectTable)
            {
                if (o == null || o.Address == local.Address || !wanted.Contains(o.Address)) continue;
                var d = Vector3.Distance(o.Position, at);
                if (d <= Radius) near.Add((d, o));
            }
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        candidates.Clear();
        if (MakeActor(local, isPlayer: true) is { } me) candidates.Add(me);
        for (var i = 0; i < near.Count && i < MaxOthers; i++)
            if (MakeActor(near[i].Object, isPlayer: false) is { } a) candidates.Add(a);
        // Keep the objects already validated this frame valid until the late tick checks them again.
        foreach (var a in candidates) a.Valid = false;
        recorder.SetActors(candidates);
    }

    private static bool IsLive(ReplayActor a)
    {
        var o = Core.Services.ObjectTable[a.ObjectIndex];
        return o != null && o.Address == a.Address && o.EntityId == a.EntityId;
    }

    /// <summary>A framework tick of our own, subscribed late (after the plugin's): the latest point before the render
    /// to check that the characters still exist, and where the draw objects are written.</summary>
    private void EnsureLateTick()
    {
        if (lateSubscribed) return;
        framework.Update += OnLateTick;
        lateSubscribed = true;
    }

    private void OnLateTick(IFramework _)
    {
        if (state is State.Idle && !player.Restoring && !player.Playing) return;
        try
        {
            if (!clientState.IsLoggedIn || Core.Services.ObjectTable.LocalPlayer == null)
            {
                foreach (var a in candidates) a.Valid = false;
                foreach (var a in player.Actors()) a.Valid = false;
                return;
            }
            validating.Clear();
            if (recorder.Recording) validating.AddRange(candidates);
            if (player.Playing || player.Restoring) validating.AddRange(player.Actors());
            foreach (var a in validating) Validate(a);
            player.TickRoots();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathReplay: late tick failed; the replay stops.");
            foreach (var a in candidates) a.Valid = false;
            if (state == State.Playing) StopReplay("error");
        }
    }

    private static void Validate(ReplayActor a)
    {
        a.Valid = false;
        if (!IsLive(a)) return;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)a.Address;
        if (go->DrawObject == null) return;
        a.DrawObject = (nint)go->DrawObject;
        a.Valid = true;
    }

    // ------------------------------------------------------------------
    // Bits
    // ------------------------------------------------------------------

    private string? GameOwnsCamera()
    {
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return "the game's cutscene has the camera";
        if (clientState.IsGPosing)
            return "group pose has the camera";
        return null;
    }

    private bool KeyDown(VirtualKey k)
    {
        try { return keyState[k]; }
        catch { return false; }
    }

    private void Summary()
    {
        var now = Environment.TickCount64;
        if (nextSummaryAt == 0) nextSummaryAt = now + SummaryMs;
        if (now < nextSummaryAt) return;
        nextSummaryAt = now + SummaryMs;
        var (frames, mean, max, meanBones) = recorder.TakeWindow();
        var span = 0.0;
        foreach (var (actor, track) in recorder.Tracks())
            if (actor.IsPlayer) span = track.NewestTime - track.OldestTime;
        var perFrame = frames > 0 ? mean * frames / (SummaryMs / 1000.0) : 0;
        log.Info($"DeathReplay: recording {recorder.ActiveCount} character(s) ({meanBones:0} bones a sample) — {frames} samples in the last 10 s, " +
                 $"{mean:0.000} ms a sample on average, {max:0.000} ms at most (≈{perFrame:0.000} ms a second of play); " +
                 $"buffer {recorder.BufferBytes() / (1024.0 * 1024.0):0.0} MB holding {span:0.0} s of you.");
    }

    private void SetStatus()
    {
        status = state switch
        {
            State.Idle => config.DeathReplayEnabled ? "waiting for a simulation" : "off",
            State.Recording => $"recording {recorder.ActiveCount} character(s)",
            State.Waiting => status.StartsWith("waiting", StringComparison.Ordinal) ? status : "waiting for the body to rest",
            State.Playing => $"playing {Math.Min(ShotSchedule.Wrap(playTime, (float)windowLength), (float)windowLength):0.0} / {windowLength:0.0} s" +
                             (shots.Length > 0 ? $", shot {Math.Max(lastShot, 0) + 1} of {shots.Length}" : "") +
                             (config.DeathReplayLoop ? " (loop)" : ""),
            State.Done => "replayed (again after the next death)",
            _ => status,
        };
        if (camera.FreeCamOn) status += "; free camera on";
    }

    /// <summary>Plugin unload: everything is put back now (when the world is still there).</summary>
    public void Dispose()
    {
        try
        {
            if (Core.Services.ObjectTable.LocalPlayer != null)
            {
                foreach (var a in player.Actors()) Validate(a);
                player.RestoreImmediately();
            }
            else
            {
                player.Abandon();
            }
        }
        catch (Exception ex) { log.Warning(ex, "DeathReplay: putting the characters back on unload failed."); }
        camera.Reset();
        movementBlock.HoldLocalPlayer = false;
        if (lateSubscribed) framework.Update -= OnLateTick;
        lateSubscribed = false;
        recorder.Dispose();
        player.Dispose();
    }
}
