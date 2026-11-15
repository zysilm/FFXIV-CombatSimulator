// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CombatSimulator.Animation;
using CombatSimulator.Camera.Cutscene;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Camera;

/// <summary>
/// Plays the game's own cutscene follow-camera work on our characters (docs/CUTSCENE_SHOTS.md): the shot chosen for
/// death when the player is killed, the one chosen for victory when the player's side wins, and any shot on demand
/// ("Play Now", Camera tab → Cutscene Shots).
///
/// A shot is a curated segment of a cutscene timeline. Its curves are read from the user's game at run time
/// (<see cref="CutsceneShotLoader"/>), sampled at the game's frame rate (<see cref="CutscenePlayback"/>), with the
/// attach slots resolved on our actors (<see cref="CutsceneAnchoring"/>, roles by <see cref="CutsceneRoles"/>).
///
/// Camera: while a shot plays this is the highest-priority owner in the coordinator (<see cref="CameraOwner.CutsceneShot"/>;
/// it asks for the matching game FOV and freezes the orbit input), and after the game's camera update it writes the whole
/// pose — eye, look-at, view matrix, projection — the way the hit shake writes its offset. It blends in from the camera
/// it found over <see cref="BlendSeconds"/>, and at the end releases the coordinator and blends back over the same time to
/// whatever camera the game then produces (the Directed camera's death framing, which waits while the shot plays, Enemy
/// Control's follow, ...). Not in Fighting Mode, not while a private scene runs.
///
/// Log: one <c>CutShot:</c> line when a shot starts (shot, roles, actors, duration) and one when it ends (time, reason,
/// remaps and fallbacks used).
/// </summary>
public sealed unsafe class CutsceneShotDirector : IDisposable
{
    public const float BlendSeconds = 0.3f;
    private const float EyeFloorMargin = 0.15f;
    /// <summary>A view-matrix layout is trusted when it reproduces the game's own matrix within this error (sum over the
    /// 16 elements). The right layout gives ~0 (a little more if the game adds a tilt to its look-at); a wrong one at
    /// least ~4 (two axes flip sign) or the world translation in the wrong row.</summary>
    private const float ConventionTolerance = 1.0f;
    /// <summary>j_kao height of the reference race above its root (for non-human height scaling).</summary>
    private const float ReferenceHeadHeight = 1.55f;
    /// <summary>The camera writes stop when Tick has not run for this long (and over more than one camera update).</summary>
    private const long TickStallMs = 500;

    private enum Phase { Idle, Playing, BlendOut }

    private readonly Configuration config;
    private readonly CameraModeCoordinator coordinator;
    private readonly DynamicCameraController dynamicCamera;
    private readonly CombatEngine engine;
    private readonly NpcSelector npcSelector;
    private readonly CombatCompanionManager companions;
    private readonly BoneTransformService bones;
    private readonly Func<nint, bool> ragdollActive;
    private readonly Func<nint, string, Vector3?> ragdollBody;
    private readonly Func<nint> currentTarget;
    private readonly Func<bool> suppressed;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly CutsceneShotLoader loader;

    public CutsceneShotCatalog Catalog { get; }
    public CutsceneProbe Probe { get; }

    // ---- playback state ----
    private Phase phase = Phase.Idle;
    private LoadedCutsceneShot? shot;
    private CutsceneRoleResult roles;
    private CutsceneTrigger trigger;
    private float time;
    private float blendOutTime;
    private string? endReason;
    private CutPose cut;
    private bool haveCut;
    private CutPose snapshot;
    private bool haveSnapshot;
    private CutPose lastWritten;
    private long lastWrittenAt;
    private float fovRatio = 1f;
    private bool haveFovRatio;
    private ViewConvention convention;
    private bool haveConvention;
    private bool loggedConvention;
    private bool loggedNoConvention;
    private bool loggedLooseConvention;
    private bool rcFovIsVertical;
    private float aspect = 16f / 9f;
    private bool heldDynamic;
    private readonly HashSet<string> notes = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, float> heightScales = new();
    private string? pendingPlayNow;
    private bool preloadPending = true;
    private long lastTickAt;
    private int updatesSinceTick;

    // ---- who fell last (from the engine's hits) ----
    private uint lastFallenEnemyId;
    private uint lastFallenKillerId;
    private bool lastFallenKillerIsAlly;

    public CutsceneShotDirector(
        Configuration config,
        CameraModeCoordinator coordinator,
        DynamicCameraController dynamicCamera,
        CombatEngine engine,
        NpcSelector npcSelector,
        CombatCompanionManager companions,
        BoneTransformService bones,
        Func<nint, bool> ragdollActive,
        Func<nint, string, Vector3?> ragdollBody,
        Func<nint> currentTarget,
        Func<string, byte[]?> loadGameFile,
        string configDirectory,
        Func<bool> suppressed,
        ICondition condition,
        IClientState clientState,
        IPluginLog log)
    {
        this.config = config;
        this.coordinator = coordinator;
        this.dynamicCamera = dynamicCamera;
        this.engine = engine;
        this.npcSelector = npcSelector;
        this.companions = companions;
        this.bones = bones;
        this.ragdollActive = ragdollActive;
        this.ragdollBody = ragdollBody;
        this.currentTarget = currentTarget;
        this.suppressed = suppressed;
        this.condition = condition;
        this.clientState = clientState;
        this.log = log;
        loader = new CutsceneShotLoader(loadGameFile);
        Probe = new CutsceneProbe(configDirectory, bones, log);
        Catalog = LoadCatalog(log);
    }

    private static CutsceneShotCatalog LoadCatalog(IPluginLog log)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CombatSimulator.Resources.CutsceneShots.json");
            if (stream == null)
            {
                log.Warning("CutShot: Resources/CutsceneShots.json is not embedded; no shots.");
                return CutsceneShotCatalog.Empty;
            }
            var catalog = CutsceneShotCatalog.Parse(stream);
            log.Info($"CutShot: {catalog.Entries.Count} curated shots.");
            return catalog;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CutShot: could not read the curated shot list.");
            return CutsceneShotCatalog.Empty;
        }
    }

    public bool IsPlaying => phase != Phase.Idle;

    // ------------------------------------------------------------------
    // Triggers
    // ------------------------------------------------------------------

    /// <summary>CombatEvents.BeforePlayerDeath: the chosen death shot, if any.</summary>
    public void OnPlayerDeath()
    {
        if (string.IsNullOrEmpty(config.CutsceneDeathShot)) return;
        Start(config.CutsceneDeathShot, CutsceneTrigger.Death);
    }

    /// <summary>CombatEvents.OutcomeDecided: the chosen victory shot when the player's side won.</summary>
    public void OnOutcome(bool playerWon)
    {
        if (!playerWon || string.IsNullOrEmpty(config.CutsceneVictoryShot)) return;
        Start(config.CutsceneVictoryShot, CutsceneTrigger.Victory);
    }

    /// <summary>The UI's "Play Now" (consumed on the next framework tick).</summary>
    public void PlayNow(string id) => pendingPlayNow = id;

    /// <summary>CombatEvents.DamageLanded: remember the last enemy to fall and who struck it.</summary>
    public void NoteDamage(SimulatedEntityState target, DamageResult hit, SimulatedEntityState? source)
    {
        if (target.IsPlayer || target.IsCompanion || target.IsAlive)
            return;
        lastFallenEnemyId = target.EntityId;
        lastFallenKillerId = source?.EntityId ?? 0;
        lastFallenKillerIsAlly = source?.IsCompanion == true;
    }

    /// <summary>A battle starts or is reset/stopped: forget who fell, and stop a shot (a reset puts everyone back).</summary>
    public void OnSimulationStarted() => ForgetFallen();

    public void OnSimulationReset()
    {
        ForgetFallen();
        Stop("simulation reset");
    }

    private void ForgetFallen()
    {
        lastFallenEnemyId = 0;
        lastFallenKillerId = 0;
        lastFallenKillerIsAlly = false;
    }

    private void Start(string id, CutsceneTrigger why)
    {
        try
        {
            StartCore(id, why);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"CutShot: could not start {id}.");
            Stop("start failed");
        }
    }

    private void StartCore(string id, CutsceneTrigger why)
    {
        var entry = Catalog.Find(id);
        if (entry == null) { Skip(why, id, "not in the curated list (list updated?)"); return; }
        if (config.FightingMode) { Skip(why, id, "Fighting Mode is on"); return; }
        if (suppressed()) { Skip(why, id, "a scene owns the camera"); return; }
        if (GameOwnsCamera() is { } gameReason) { Skip(why, id, gameReason); return; }
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero) { Skip(why, id, "no player"); return; }

        var loaded = loader.Load(entry, out var error);
        if (loaded == null) { Skip(why, id, error ?? "could not be read"); return; }

        var playerAlive = engine.State.PlayerState.IsAlive && why != CutsceneTrigger.Death;
        var input = new CutsceneRoleInput(
            why,
            playerAlive,
            player.Address,
            why == CutsceneTrigger.Death ? KillerAddress() : 0,
            why == CutsceneTrigger.Victory ? EnemyAddress(lastFallenEnemyId) : 0,
            why == CutsceneTrigger.Victory && lastFallenKillerIsAlly ? AllyAddress(lastFallenKillerId) : 0,
            why == CutsceneTrigger.Victory ? NearestLivingAlly(player.Address) : 0,
            why == CutsceneTrigger.PlayNow ? PlayNowOther(player.Address) : 0);
        var resolved = CutsceneRoles.Resolve(entry.TwoPerson, input, out var whyNot);
        if (resolved == null) { Skip(why, id, whyNot); return; }

        // A shot already playing hands over from where the camera is now.
        var replacing = phase != Phase.Idle;
        if (replacing)
            log.Info($"CutShot: {shot?.Entry.Id} replaced by {entry.Id} ({why}).");

        shot = loaded;
        roles = resolved.Value;
        trigger = why;
        lastTickAt = Environment.TickCount64;
        updatesSinceTick = 0;
        time = 0f;
        blendOutTime = 0f;
        endReason = null;
        haveCut = false;
        haveSnapshot = false;
        haveFovRatio = false;
        notes.Clear();
        heightScales.Clear();
        phase = Phase.Playing;
        if (!heldDynamic)
        {
            dynamicCamera.HoldForCutscene(true);
            heldDynamic = true;
        }

        var follow = CutsceneRoles.ActorFor(entry, entry.FollowActor, roles);
        var look = entry.LookActor is { } la ? CutsceneRoles.ActorFor(entry, la, roles) : nint.Zero;
        log.Info($"CutShot: play {entry.Id} \"{entry.Name}\" ({why}, {(entry.TwoPerson ? "two-person" : "single")}, " +
                 $"{loaded.Seconds:0.00} s, {loaded.Spans.Count} shot(s), points {string.Join("/", entry.AttachPoints)}) — {roles.Rule}; " +
                 $"subject {Describe(roles.Subject)}" + (entry.TwoPerson ? $", other {Describe(roles.Other)}" : "") +
                 $"; camera follows {Describe(follow)}" + (look != 0 ? $", looks at {Describe(look)}" : "") + ".");
        Sample();
    }

    /// <summary>Why the game's own camera work is on (its cutscene, group pose), or null.</summary>
    private string? GameOwnsCamera()
    {
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return "the game's cutscene took the camera";
        if (clientState.IsGPosing)
            return "group pose took the camera";
        return null;
    }

    /// <summary>Reads a shot's curves (and the reference bind points) now, so its first trigger does not stall a frame.
    /// Main thread only (the UI and Tick): the loader's caches are not thread-safe.</summary>
    public void Preload(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var entry = Catalog.Find(id);
            if (entry == null) return;
            if (loader.Load(entry, out var error) == null)
                log.Info($"CutShot: preload of {id} failed — {error ?? "could not be read"}.");
            loader.BindPoints(CutsceneShotLoader.ReferenceRace);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"CutShot: preload of {id} failed.");
        }
    }

    private void Skip(CutsceneTrigger why, string id, string reason)
    {
        log.Info($"CutShot: {why} shot {id} skipped — {reason}.");
        if (why == CutsceneTrigger.PlayNow)
            Core.Services.ChatGui.PrintError($"[CombatSim] Cutscene shot not played: {reason}.");
    }

    /// <summary>End now, without a blend (reset, zone change, logout, unload).</summary>
    public void Stop(string reason = "stopped")
    {
        if (phase != Phase.Idle)
            Finish(reason);
        pendingPlayNow = null;
    }

    private void BeginBlendOut(string reason)
    {
        if (phase != Phase.Playing) return;
        endReason = reason;
        phase = Phase.BlendOut;
        blendOutTime = 0f;
        coordinator.Release(CameraOwner.CutsceneShot);
        ReleaseDynamic();
    }

    private void Finish(string reason)
    {
        var entry = shot?.Entry;
        coordinator.Release(CameraOwner.CutsceneShot);
        ReleaseDynamic();
        phase = Phase.Idle;
        if (entry != null)
            log.Info($"CutShot: end {entry.Id} ({trigger}) after {time:0.00} s ({endReason ?? reason})" +
                     (notes.Count > 0 ? $"; remaps/fallbacks: {string.Join("; ", notes)}" : "; no remaps") + ".");
        shot = null;
        haveCut = false;
        endReason = null;
    }

    private void ReleaseDynamic()
    {
        if (!heldDynamic) return;
        heldDynamic = false;
        dynamicCamera.HoldForCutscene(false);
    }

    // ------------------------------------------------------------------
    // Per frame
    // ------------------------------------------------------------------

    /// <summary>Framework tick, before the coordinator applies: advance the shot, sample it, keep the camera.</summary>
    public void Tick(float dt)
    {
        lastTickAt = Environment.TickCount64;
        updatesSinceTick = 0;
        try
        {
            if (preloadPending)
            {
                preloadPending = false;
                Preload(config.CutsceneVictoryShot);
                Preload(config.CutsceneDeathShot);
            }
            if (pendingPlayNow is { } id)
            {
                pendingPlayNow = null;
                Start(id, CutsceneTrigger.PlayNow);
            }
            if (phase == Phase.Idle || shot == null) return;

            // The game's own cutscene or group pose takes the camera: let go at once, without blending.
            if (GameOwnsCamera() is { } gameReason) { Finish(gameReason); return; }

            if (phase == Phase.Playing)
            {
                if (config.FightingMode || suppressed()) { BeginBlendOut("Fighting Mode or a scene took over"); }
                else if (!ActorValid(roles.Subject) || (shot.Entry.TwoPerson && !ActorValid(roles.Other))) { BeginBlendOut("an actor is gone"); }
                else
                {
                    time += dt;
                    if (time >= shot.Seconds) { time = shot.Seconds; BeginBlendOut("end of shot"); }
                }
            }
            else
            {
                blendOutTime += dt;
                if (blendOutTime >= BlendSeconds) { Finish("end of shot"); return; }
            }

            if (ActorValid(roles.Subject) && (!shot.Entry.TwoPerson || ActorValid(roles.Other)))
                Sample();

            if (phase == Phase.Playing)
            {
                float? fov = null;
                if (haveFovRatio && haveCut)
                {
                    var w = CutPose.Ease(time / BlendSeconds);
                    var target = haveSnapshot ? snapshot.VerticalFov + (cut.VerticalFov - snapshot.VerticalFov) * w : cut.VerticalFov;
                    fov = 2f * MathF.Atan(MathF.Tan(CutsceneView.ClampFov(target) / 2f) / fovRatio);
                    if (!float.IsFinite(fov.Value) || fov.Value <= 0.01f) fov = null;
                }
                coordinator.Submit(CameraOwner.CutsceneShot, new CameraRequest { Fov = fov, ClearInputH = true, ClearInputV = true });
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CutShot: tick failed; stopping the shot.");
            Stop("error");
        }
    }

    private void Sample()
    {
        if (shot == null) return;
        var entry = shot.Entry;
        var actors = new Dictionary<nint, CutsceneActor>();
        CutsceneActor? ActorOf(nint address)
        {
            if (address == nint.Zero) return null;
            if (!actors.TryGetValue(address, out var a))
            {
                a = BuildActor(address);
                if (a == null) return null;
                actors[address] = a;
            }
            return a;
        }

        Matrix4x4 Anchor(Cutscene.Attach at)
        {
            if (at.Actor == 255) return Matrix4x4.Identity;
            var actor = ActorOf(CutsceneRoles.ActorFor(entry, at.Actor, roles));
            if (actor == null) return Matrix4x4.Identity;
            var frame = CutsceneAnchoring.Frame(actor, at.Point, out var note);
            if (note != null) notes.Add($"{actor.Label} {note}");
            return frame;
        }

        var sample = CutscenePlayback.Evaluate(shot, phase == Phase.BlendOut ? shot.Seconds : time, Anchor, aspect);
        var pose = sample.Pose;
        if (ActorOf(CutsceneRoles.ActorFor(entry, entry.FollowActor, roles)) is { } followed)
        {
            var clamped = CutscenePlayback.ClampEye(pose, followed.FloorY, EyeFloorMargin);
            if (clamped.Eye != pose.Eye) notes.Add("eye kept above the floor");
            pose = clamped;
        }
        if (!pose.IsFinite)
        {
            notes.Add($"non-finite pose at {time:0.00} s (kept the last one)");
            return;
        }
        cut = pose;
        haveCut = true;
    }

    /// <summary>After the game's camera update (GameCameraUpdateHook.PostCameraUpdate): record for the probe, then
    /// write the shot's pose over the game's.</summary>
    public void OnPostCameraUpdate(CameraBase* camera)
    {
        if (camera == null) return;
        Probe.Record(camera);
        if (phase == Phase.Idle) return;

        // Tick drives the shot; if it stops running (an earlier step of the framework update throwing every frame, ...)
        // nothing would ever end the shot, so stop writing and hand the camera back.
        updatesSinceTick++;
        if (updatesSinceTick > 1 && Environment.TickCount64 - lastTickAt > TickStallMs)
        {
            log.Warning($"CutShot: no tick for {Environment.TickCount64 - lastTickAt} ms ({updatesSinceTick} camera updates); stopping the shot.");
            Finish("tick stalled");
            return;
        }

        var sc = &camera->SceneCamera;
        var rc = sc->RenderCamera;
        if (rc == null) return;

        var eye = new Vector3(rc->Origin.X, rc->Origin.Y, rc->Origin.Z);
        var look = new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
        var view = ToNumerics(rc->ViewMatrix);
        var projection = ToNumerics(rc->ProjectionMatrix);
        var nativeFov = CutsceneView.VerticalFovOf(projection);
        if (!float.IsFinite(nativeFov)) return;
        if (MathF.Abs(projection.M11) > 1e-6f)
            aspect = MathF.Abs(projection.M22 / projection.M11);

        // The game's own matrix tells which layout to write ours in.
        if (Vector3.DistanceSquared(eye, look) > 1e-6f)
        {
            var (c, err) = CutsceneView.Detect(view, eye, look);
            if (err <= ConventionTolerance)
            {
                if (!loggedConvention || c != convention || (err > 0.05f && !loggedLooseConvention))
                {
                    log.Info($"CutShot: view matrix layout {c} (error {err:0.0000}); render FOV field {rc->FoV:0.0000} vs projection {nativeFov:0.0000}.");
                    loggedConvention = true;
                    loggedLooseConvention |= err > 0.05f;
                }
                convention = c;
                haveConvention = true;
                rcFovIsVertical = MathF.Abs(rc->FoV - nativeFov) < 0.02f;
            }
            else if (!haveConvention && !loggedNoConvention)
            {
                log.Warning($"CutShot: no view matrix layout matches the game's (best {c}, error {err:0.000}); not writing until one does.");
                loggedNoConvention = true;
            }
        }
        if (!haveConvention) return;

        var native = new CutPose(eye, look, Vector3.UnitY, nativeFov);
        if (!haveSnapshot)
        {
            // The camera we found: what we last wrote when a shot is handed over, otherwise the game's.
            snapshot = Environment.TickCount64 - lastWrittenAt < 200 ? lastWritten : native;
            haveSnapshot = true;
            var game = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera;
            var gameFov = game->FoV;
            if (gameFov > 0.01f && float.IsFinite(gameFov))
            {
                fovRatio = MathF.Tan(nativeFov / 2f) / MathF.Tan(gameFov / 2f);
                if (!float.IsFinite(fovRatio) || fovRatio <= 0f) fovRatio = 1f;
            }
            haveFovRatio = true;
        }
        if (!haveCut) return;

        var pose = phase == Phase.Playing
            ? CutPose.Lerp(snapshot, cut, CutPose.Ease(time / BlendSeconds))
            : CutPose.Lerp(cut, native, CutPose.Ease(blendOutTime / BlendSeconds));
        if (!pose.IsFinite) return;
        if (!Write(sc, rc, pose, projection)) return;
        lastWritten = pose;
        lastWrittenAt = Environment.TickCount64;
    }

    /// <summary>Writes the pose; false (nothing written) when the view or projection matrix would not be finite.</summary>
    private bool Write(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera* sc,
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera* rc, in CutPose pose, in Matrix4x4 projection)
    {
        var fov = CutsceneView.ClampFov(pose.VerticalFov);
        var view = CutsceneView.Build(convention, pose.Eye, pose.Target, pose.Up);
        var proj = CutsceneView.WithVerticalFov(projection, fov);
        if (!CutsceneView.IsFinite(view) || !CutsceneView.IsFinite(proj))
        {
            notes.Add("non-finite camera matrix (frame skipped)");
            return false;
        }
        sc->Position = pose.Eye;
        sc->LookAtVector = pose.Target;
        sc->ViewMatrix = ToGame(view);
        rc->Origin = pose.Eye;
        rc->ViewMatrix = ToGame(view);
        rc->ProjectionMatrix = ToGame(proj);
        if (rcFovIsVertical)
            rc->FoV = fov;
        return true;
    }

    private static Matrix4x4 ToNumerics(in FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    private static FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 ToGame(in Matrix4x4 m)
    {
        var r = new FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4();
        r.M11 = m.M11; r.M12 = m.M12; r.M13 = m.M13; r.M14 = m.M14;
        r.M21 = m.M21; r.M22 = m.M22; r.M23 = m.M23; r.M24 = m.M24;
        r.M31 = m.M31; r.M32 = m.M32; r.M33 = m.M33; r.M34 = m.M34;
        r.M41 = m.M41; r.M42 = m.M42; r.M43 = m.M43; r.M44 = m.M44;
        return r;
    }

    // ------------------------------------------------------------------
    // Actors
    // ------------------------------------------------------------------

    private CutsceneActor? BuildActor(nint address)
    {
        var go = (GameObject*)address;
        if (go == null) return null;
        var pos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)) return null;
        var down = IsDown(address);
        var ragdoll = ragdollActive(address);

        ushort race = 0;
        var draw = go->DrawObject;
        if (draw != null && draw->GetObjectType() == ObjectType.CharacterBase)
        {
            var cb = (CharacterBase*)draw;
            if (cb->GetModelType() == CharacterBase.ModelType.Human)
                race = ((Human*)cb)->RaceSexId;
        }

        (Vector3 Position, Quaternion? Rotation)? Bone(string name)
        {
            if (ragdoll)
            {
                var p = ragdollBody(address, name) ?? ragdollBody(address, RagdollAlias(name));
                if (p is { } body && float.IsFinite(body.X)) return (body, null);
            }
            var t = bones.GetBoneWorldTransform(address, name);
            return t is { } b ? (b.Position, b.Rotation) : null;
        }

        var scale = 1f;
        if (race == 0)
        {
            if (!heightScales.TryGetValue(address, out scale))
            {
                scale = 1f;
                if (!down && bones.GetBoneWorldPos(address, CutsceneAnchoring.HeadBone) is { } head)
                {
                    var h = head.Y - pos.Y;
                    if (float.IsFinite(h) && h > 0.2f) scale = Math.Clamp(h / ReferenceHeadHeight, 0.3f, 8f);
                }
                heightScales[address] = scale;
                if (MathF.Abs(scale - 1f) > 0.05f) notes.Add($"{Describe(address)} not human: heights ×{scale:0.00}");
            }
        }

        var floor = pos.Y;
        if (down && Bone(CutsceneAnchoring.PelvisBone) is { } pelvis)
            floor = MathF.Min(floor, pelvis.Position.Y - 0.2f);

        return new CutsceneActor
        {
            Label = Describe(address),
            Position = pos,
            Yaw = go->Rotation,
            Down = down,
            HeightScale = scale,
            BindPoints = loader.BindPoints(race == 0 ? CutsceneShotLoader.ReferenceRace : race),
            Bone = Bone,
            FloorY = floor,
        };
    }

    /// <summary>A ragdoll has bodies for the main bones only: the nearest one for points it does not have.</summary>
    private static string RagdollAlias(string bone) => bone switch
    {
        "n_hara" => "j_kosi",
        "n_buki_r" => "j_te_r",
        "n_buki_l" => "j_te_l",
        "j_kao" => "j_kubi",
        "j_sebo_c" => "j_sebo_b",
        _ => bone,
    };

    private bool IsDown(nint address)
    {
        if (ragdollActive(address)) return true;
        // Outside a battle the simulated states mean nothing (the player's is empty before the first one).
        if (!engine.IsActive) return false;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address == address) return !engine.State.PlayerState.IsAlive;
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address == address) return !npc.State.IsAlive;
        foreach (var ally in companions.Companions)
            if (ally.Address == address) return !ally.State.IsAlive;
        return false;
    }

    private bool ActorValid(nint address)
    {
        if (address == nint.Zero) return false;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address == address) return true;
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address == address) return npc.IsSpawned;
        foreach (var ally in companions.Companions)
            if (ally.Address == address) return ally.IsSpawned;
        foreach (var o in Core.Services.ObjectTable)
            if (o != null && o.Address == address) return true;
        return false;
    }

    private string Describe(nint address)
    {
        if (address == nint.Zero) return "none";
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address == address) return $"you (0x{address:X})";
        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address == address) return $"{npc.Name} (0x{address:X})";
        foreach (var ally in companions.Companions)
            if (ally.Address == address) return $"{ally.Name} (0x{address:X})";
        foreach (var o in Core.Services.ObjectTable)
            if (o != null && o.Address == address) return $"{o.Name.TextValue} (0x{address:X})";
        return $"0x{address:X}";
    }

    private nint KillerAddress()
    {
        var id = engine.LastPlayerKillerEntityId;
        var npc = id != 0 ? npcSelector.GetSelectedNpc(id) : null;
        if (npc is { BattleChara: not null }) return npc.Address;
        return engine.LastPlayerKillerAddress;
    }

    private nint EnemyAddress(uint entityId)
    {
        if (entityId == 0) return 0;
        var npc = npcSelector.GetSelectedNpc(entityId);
        if (npc is { BattleChara: not null }) return npc.Address;
        foreach (var n in npcSelector.SelectedNpcs)
            if (n.SimulatedEntityId == entityId && n.Address != nint.Zero) return n.Address;
        return 0;
    }

    private nint AllyAddress(uint entityId)
    {
        if (entityId == 0) return 0;
        var ally = companions.GetCompanion(entityId);
        return ally is { IsSpawned: true } && ally.Address != nint.Zero ? ally.Address : 0;
    }

    private nint NearestLivingAlly(nint player)
    {
        var from = ragdollActive(player) ? ragdollBody(player, CutsceneAnchoring.PelvisBone) ?? Position(player) : Position(player);
        nint best = 0;
        var bestDist = float.MaxValue;
        foreach (var ally in companions.Companions)
        {
            if (!ally.IsSpawned || !ally.State.IsAlive || ally.Address == nint.Zero) continue;
            var d = Vector3.DistanceSquared(Position(ally.Address), from);
            if (d < bestDist) { bestDist = d; best = ally.Address; }
        }
        return best;
    }

    private nint PlayNowOther(nint player)
    {
        var target = currentTarget();
        if (target != nint.Zero && target != player) return target;
        var from = Position(player);
        nint best = 0;
        var bestDist = float.MaxValue;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.IsSpawned || !npc.State.IsAlive || npc.Address == nint.Zero) continue;
            var d = Vector3.DistanceSquared(Position(npc.Address), from);
            if (d < bestDist) { bestDist = d; best = npc.Address; }
        }
        return best;
    }

    private static Vector3 Position(nint address)
    {
        var go = (GameObject*)address;
        return go == null ? Vector3.Zero : new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
    }

    public void Dispose()
    {
        Stop("unloaded");
        Probe.Dispose();
    }
}
