// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera.Cutscene;
using CombatSimulator.Camera.DeathShots;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>What the Directed camera asks of the death shot on a frame it wants one.</summary>
public readonly record struct DeathShotRequest(
    /// <summary>The player's own body: framed when <paramref name="Subject"/> cannot be read.</summary>
    nint Player,
    /// <summary>The body to frame (the player's, unless a scene names another).</summary>
    nint Subject,
    /// <summary>For the log: "you", or the name of the other body.</summary>
    string SubjectLabel,
    DeathShotProfile Profile,
    /// <summary>Shot size on top of the profile's own and of the player's wheel (1 = the profile's).</summary>
    float Size,
    /// <summary>A ragdoll is still to be armed for this body (the player between the killing blow and the death
    /// animation).</summary>
    bool RagdollToCome);

/// <summary>
/// The automatic death shot in the game (docs/DEATH_SHOTS.md): runs <see cref="DeathShotSolver"/> on the fallen body
/// every frame and hands its eye and look-at point to the game.
///
/// Positions: the animated bones (<see cref="BoneTransformService"/>) until the body's ragdoll says its physics has
/// started, the ragdoll's rigid bodies from then on. The solver is told which of the two produced the frame, and on
/// the frame of the change it also gets the bones of that same frame, so the difference between the two sources is
/// absorbed instead of shown. The world it asks about is the game's collision (<see cref="GameWorld"/>).
///
/// Camera: owner <see cref="CameraOwner.DynamicDeath"/> in the coordinator (the FoV, through the measured ratio of the
/// game's FoV field to the lens it renders; the game's zoom held where it was), and the pose through the same
/// SetCameraLookAt hand-over the cutscene shots use (<see cref="OverridePose"/>). A cutscene shot (owner 60) wins: while
/// one plays this shot waits, and starts again from the camera the cutscene left. A cut of the solver is written as it
/// is; nothing here blends except the 0.3 s back to the game's camera when the player is revived.
///
/// The player keeps the left/right orbit (the change of the game camera's own yaw is added to the shot's side) and the
/// wheel (shot size); the pitch is the shot's. Both go back to nothing when the solver picks a side again.
///
/// Driven by <see cref="DynamicCameraController"/>: <see cref="Advance"/> every frame, <see cref="Run"/> on the frames
/// a death shot is wanted. A frame without <see cref="Run"/> ends the shot.
/// </summary>
public sealed unsafe class DeathShotDirector : IDisposable
{
    public const float BlendOutSeconds = 0.3f;
    /// <summary>Overridden camera updates checked (once per shot) against the scene camera the game ended with.</summary>
    private const int CheckFrames = 60;
    /// <summary>The camera writes stop when the director has not been advanced for this long.</summary>
    private const long TickStallMs = 500;
    private const long SummaryMs = 5000;
    /// <summary>A change of the game camera's yaw larger than this in one frame is not the player's hand.</summary>
    private const float MaxYawStep = 1.0f;
    /// <summary>One wheel notch changes the shot size by about 10 %.</summary>
    private const float WheelStep = 0.10f;
    /// <summary>Frames without a readable body before the shot gives up.</summary>
    private const int MaxUnreadableFrames = 120;

    /// <summary>The seven points of the body frame, then the rest of the body for whole-body framing: the same bones,
    /// in the same order, as the offline trajectories (Tests/RagdollReplay/TrajectoryExport).</summary>
    public static readonly string[] Bones =
    {
        "j_kosi", "j_sebo_c", "j_kao", "j_sako_l", "j_sako_r", "j_asi_d_l", "j_asi_d_r",
        "j_sebo_a", "j_sebo_b", "j_kubi",
        "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_te_l", "j_te_r",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r",
    };
    private const int CoreBones = 7;

    public static readonly string[] ProfileLabels = { "Auto", "Low three-quarter", "Face", "Along the body", "Overhead", "Ground profile" };
    public static readonly DeathShotProfile[] Profiles =
    {
        DeathShotProfile.Auto, DeathShotProfile.LowThreeQuarter, DeathShotProfile.Face, DeathShotProfile.AlongBody,
        DeathShotProfile.Overhead, DeathShotProfile.GroundProfile,
    };

    public static string Label(DeathShotProfile profile)
    {
        var i = Array.IndexOf(Profiles, profile);
        return i >= 0 ? ProfileLabels[i] : profile.ToString();
    }

    private enum State { Idle, Waiting, Running, BlendOut }

    private readonly Configuration config;
    private readonly CameraModeCoordinator coordinator;
    private readonly BoneTransformService bones;
    private readonly Func<nint, RagdollController?> ragdollFor;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly GameWorld world = new();
    private readonly DeathShotSolver solver;

    /// <summary>True while a cutscene shot holds the camera (not while it blends out): the death shot waits.</summary>
    public Func<bool>? CutsceneOwnsCamera { get; set; }

    // ---- shot state ----
    private State state = State.Idle;
    private bool ranThisFrame;
    private bool failed;
    private nint subject, requestedSubject;
    private string subjectLabel = "you";
    private DeathShotProfile profile;
    private float shotTime;
    private bool resumed;
    private string status = "off";

    // positions
    private readonly Vector3[] boneNow = new Vector3[Bones.Length];
    private readonly Vector3[] bodyNow = new Vector3[Bones.Length];
    private readonly bool[] boneKnown = new bool[Bones.Length];
    private readonly Vector3[] extra = new Vector3[Bones.Length - CoreBones];
    private readonly Vector3[] extraBones = new Vector3[Bones.Length - CoreBones];
    private bool lastPhysics, hadFrame;
    private int unreadable;
    private float bodyScale;

    // camera
    private Vector3 poseEye, poseLook;
    private float poseFov;
    private bool havePose;
    private float fovRatio = 1f;
    private float aspect = 16f / 9f;
    private float heldDistance;
    private bool haveHeldDistance;

    // the player's hand
    private float userYaw, userSize = 1f, lastDirH, pendingWheel;
    private bool haveDirH;

    // blend back to the game's camera
    private float blendOutTime;

    // hand-over bookkeeping
    private long lastAdvanceAt;
    private int updatesSinceAdvance;
    private bool wroteThisUpdate, supersededThisUpdate, loggedOverrideError;
    private int overrideCount, checkCount;
    private float checkEye, checkLook, checkFov;

    // log state
    private bool lastSettled, lastPulled, lastExpect;
    private int lastCuts, lastFallbacks, lastPops;
    private DeathShotOutput last;
    private Vector3 lastOutEye, lastOutDir;
    private bool haveLastOut;
    private long nextSummaryAt;
    private float windowEyeSpeed, windowViewRate;
    private int windowFrames, windowRays, windowRaysMax, skippedFrames;

    public DeathShotDirector(
        Configuration config,
        CameraModeCoordinator coordinator,
        BoneTransformService bones,
        Func<nint, RagdollController?> ragdollFor,
        ICondition condition,
        IClientState clientState,
        IPluginLog log)
    {
        this.config = config;
        this.coordinator = coordinator;
        this.bones = bones;
        this.ragdollFor = ragdollFor;
        this.condition = condition;
        this.clientState = clientState;
        this.log = log;
        solver = new DeathShotSolver(world);
    }

    /// <summary>A shot is writing the camera (or blending back from one): models near the eye are kept drawn.</summary>
    public bool IsRunning => state is State.Running or State.BlendOut;

    /// <summary>One line for the Camera tab.</summary>
    public string Status => status;

    /// <summary>Raw mouse wheel from the plugin's draw pass (the game's zoom is held during the shot).</summary>
    public void NotifyMouseWheel(float wheel)
    {
        if (state == State.Running && wheel != 0f && float.IsFinite(wheel))
            pendingWheel += wheel;
    }

    // ------------------------------------------------------------------
    // Per frame
    // ------------------------------------------------------------------

    /// <summary>Every framework tick, before <see cref="Run"/>: the blend back to the game's camera, and the end of
    /// a shot nobody asked for on the frame before.</summary>
    public void Advance(float dt)
    {
        lastAdvanceAt = Environment.TickCount64;
        updatesSinceAdvance = 0;
        // ranThisFrame still holds the frame before this one.
        if (state is State.Running or State.Waiting && !ranThisFrame)
            Stop("no longer wanted");
        ranThisFrame = false;
        if (state == State.BlendOut)
        {
            blendOutTime += dt;
            if (blendOutTime >= BlendOutSeconds)
            {
                state = State.Idle;
                havePose = false;
                status = "off";
            }
        }
    }

    /// <summary>A death shot is wanted this frame: start one (from the camera as it is), or carry on.</summary>
    public void Run(float dt, in DeathShotRequest request)
    {
        ranThisFrame = true;
        if (failed)
        {
            status = "death shot stopped after an error (see the log)";
            return;
        }
        try
        {
            RunCore(dt, in request);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathShot: frame failed; the shot ends and the camera goes back to the game.");
            Stop("error");
            failed = true;
        }
    }

    private void RunCore(float dt, in DeathShotRequest request)
    {
        // The game's own cutscene or group pose takes the camera: let go at once, and do not start under it.
        if (GameOwnsCamera() is { } gameReason)
        {
            if (state != State.Idle) Stop(gameReason);
            status = $"death shot waits — {gameReason}";
            return;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null) return;
        var game = camMgr->Camera;

        // A cutscene shot (or anything else above the death shot) has the camera: wait, and start again from the
        // camera it leaves.
        var above = CutsceneOwnsCamera?.Invoke() == true ? CameraOwner.CutsceneShot : coordinator.CurrentOwner;
        if (above > CameraOwner.DynamicDeath)
        {
            if (state != State.Waiting)
            {
                if (state == State.Running)
                    log.Info($"DeathShot: waits at {shotTime:0.0} s — {above} has the camera.");
                else
                    log.Info($"DeathShot: armed, waiting — {above} has the camera.");
                coordinator.Release(CameraOwner.DynamicDeath);
                resumed = state == State.Running || resumed;
                state = State.Waiting;
                havePose = false;
            }
            status = $"death shot waits — {above} has the camera";
            return;
        }

        if (state == State.Running && request.Subject != requestedSubject)
        {
            log.Info($"DeathShot: subject changes to {request.SubjectLabel} at {shotTime:0.0} s; the shot starts again from here.");
            state = State.Waiting;
            resumed = true;
        }

        if (state != State.Running)
        {
            var wasWaiting = state == State.Waiting;
            if (!Begin(game, in request, wasWaiting))
                return;
        }

        Step(game, dt, in request);
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

    /// <summary>Starts the solver from the camera as the game last rendered it, and measures how the game's FoV field
    /// maps to the lens it renders.</summary>
    private bool Begin(FFXIVClientStructs.FFXIV.Client.Game.Camera* game, in DeathShotRequest request, bool afterWaiting)
    {
        var sc = &game->CameraBase.SceneCamera;
        var rc = sc->RenderCamera;
        if (rc == null) return false;
        var projection = ToNumerics(rc->ProjectionMatrix);
        var rendered = CutsceneView.VerticalFovOf(projection);
        if (CutsceneView.FovRatio(rendered, game->FoV) is not { } ratio) return false;
        var eye = new Vector3(sc->Position.X, sc->Position.Y, sc->Position.Z);
        var look = new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
        if (!CutsceneView.CanHandOver(eye, look)) return false;
        var body = request.Subject;
        var label = request.SubjectLabel;
        Array.Clear(boneKnown);
        if (!ReadBones(body) && !ReadBodies(ragdollFor(body)))
        {
            // A body without the seven bones of the body frame (not a humanoid, or gone): the player's own.
            if (body == request.Player) return false;
            body = request.Player;
            label = "you (the body asked for cannot be read)";
            Array.Clear(boneKnown);
            if (!ReadBones(body) && !ReadBodies(ragdollFor(body))) return false;
        }

        fovRatio = ratio;
        if (MathF.Abs(projection.M11) > 1e-6f)
            aspect = MathF.Abs(projection.M22 / projection.M11);

        requestedSubject = request.Subject;
        subject = body;
        subjectLabel = label;
        profile = request.Profile;
        // Body size from the animated bones' spine, whichever source the first frame uses: the rigid-body centres
        // sit elsewhere along the spine, and the recipes' lengths were set against the bones.
        bodyScale = 0f;
        if (boneKnown[0] && boneKnown[1] && boneKnown[2])
        {
            var spine = Vector3.Distance(boneNow[1], boneNow[0]) + Vector3.Distance(boneNow[2], boneNow[1]);
            var s = spine / DeathShotSolver.ReferenceSpine;
            if (float.IsFinite(s) && s > 0.2f) bodyScale = Math.Clamp(s, 0.4f, 4f);
        }

        solver.Begin(eye, look, rendered);
        state = State.Running;
        shotTime = 0f;
        hadFrame = false;
        lastPhysics = false;
        unreadable = 0;
        havePose = false;
        userYaw = 0f;
        userSize = 1f;
        pendingWheel = 0f;
        haveDirH = false;
        if (!haveHeldDistance)
        {
            heldDistance = game->Distance;
            haveHeldDistance = float.IsFinite(heldDistance) && heldDistance > 0f;
        }
        overrideCount = 0;
        checkCount = 0;
        checkEye = checkLook = checkFov = 0f;
        loggedOverrideError = false;
        lastSettled = lastPulled = false;
        lastCuts = lastFallbacks = lastPops = 0;
        haveLastOut = false;
        last = default;
        lastExpect = false;
        skippedFrames = 0;
        ResetWindow();
        nextSummaryAt = Environment.TickCount64 + SummaryMs;

        var away = eye - look;
        log.Info($"DeathShot: {(afterWaiting && resumed ? "resumes" : "start")} — subject {subjectLabel}, profile {Label(profile)}, " +
                 $"body scale {(bodyScale > 0f ? bodyScale.ToString("0.00") : "from the first frame")}; " +
                 $"from the camera at azimuth {Degrees(DeathShotMath.Azimuth(away)):0}°, {away.Length():0.00} m, lens {Degrees(rendered):0.0}° " +
                 $"(game yaw {Degrees(game->DirH):0}°, FoV field {game->FoV:0.000}, field→lens ratio {fovRatio:0.000}, aspect {aspect:0.00}).");
        resumed = false;
        return true;
    }

    private void Step(FFXIVClientStructs.FFXIV.Client.Game.Camera* game, float dt, in DeathShotRequest request)
    {
        // ---- the body: animated bones until the ragdoll's physics has started, its rigid bodies after ----
        var ragdoll = ragdollFor(subject);
        var haveBones = ReadBones(subject);
        var physics = ragdoll is { IsActive: true, PhysicsStarted: true } && ReadBodies(ragdoll);
        var source = physics ? bodyNow : boneNow;
        var points = PointsOf(source);
        if (!physics && !haveBones || !points.IsFinite)
        {
            // Nothing to solve from this frame (the model is being redrawn, ...): the last pose stays.
            skippedFrames++;
            if (++unreadable > MaxUnreadableFrames) Stop("the body cannot be read");
            else Submit();
            return;
        }
        unreadable = 0;
        Array.Copy(source, CoreBones, extra, 0, extra.Length);
        var switching = hadFrame && physics != lastPhysics;
        BodyPoints? previousSource = null;
        Vector3[]? previousExtra = null;
        if (switching && physics && haveBones)
        {
            // The frame the ragdoll takes over: the bones of this same frame are still readable.
            previousSource = PointsOf(boneNow);
            Array.Copy(boneNow, CoreBones, extraBones, 0, extraBones.Length);
            previousExtra = extraBones;
        }

        // A ragdoll armed for this body (or still to be armed): the death animation coming to rest is not "settled".
        // One whose bodies cannot be read is not waited for (its pose reaches the bones anyway).
        var expect = config.EnableRagdoll &&
            (request.RagdollToCome && subject == request.Player || ragdoll is { IsActive: true } && (physics || !ragdoll.PhysicsStarted));

        // ---- the player's hand: left/right orbit from the game camera's own yaw, size from the wheel ----
        var dirH = game->DirH;
        if (haveDirH && float.IsFinite(dirH))
        {
            var turned = DeathShotMath.AngleDelta(lastDirH, dirH);
            if (MathF.Abs(turned) <= MaxYawStep)
                userYaw = DeathShotMath.WrapAngle(userYaw + turned);
        }
        if (float.IsFinite(dirH))
        {
            lastDirH = dirH;
            haveDirH = true;
        }
        if (pendingWheel != 0f)
        {
            // Wheel up = closer (the game's own convention).
            userSize = Math.Clamp(userSize * MathF.Exp(-WheelStep * pendingWheel), 0.5f, 3f);
            pendingWheel = 0f;
        }

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

        profile = request.Profile;
        world.NewFrame(points.Hips.Y);
        var size = float.IsFinite(request.Size) && request.Size > 0.01f ? request.Size : 1f;
        var o = solver.Step(new DeathShotInput
        {
            Dt = dt,
            Points = points,
            Extra = extra,
            PhysicsStarted = physics,
            PreviousSource = previousSource,
            PreviousSourceExtra = previousExtra,
            ExpectRagdoll = expect,
            Profile = profile,
            Aspect = aspect,
            BodyScale = bodyScale,
            UserYaw = userYaw,
            UserSize = Math.Clamp(userSize * size, 0.5f, 3f),
        });

        var first = !hadFrame;
        hadFrame = true;
        lastPhysics = physics;
        shotTime += Math.Clamp(dt, 0f, 0.1f);

        if (CutsceneView.CanHandOver(o.Eye, o.LookAt) && float.IsFinite(o.Fov))
        {
            poseEye = o.Eye;
            poseLook = o.LookAt;
            poseFov = o.Fov;
            havePose = true;
        }
        Submit();

        Report(in o, in points, previousSource, first, physics, expect, dt);
        last = o;

        // The solver picked a side again: the player's offsets were about the old one.
        if (o.SideChanged)
        {
            userYaw = 0f;
            userSize = 1f;
        }

        status = $"death shot — {Label(o.Shot)}{(profile == DeathShotProfile.Auto ? " (Auto)" : "")}, {PoseWord(o.Pose)}, " +
                 (o.Holding ? "holding" : expect && !physics ? "following the fall" : "following");
    }

    /// <summary>This frame's request to the coordinator: the lens (as the game's FoV field), and the game's own zoom
    /// held where it was (the wheel is the shot's size while it runs).</summary>
    private void Submit() => coordinator.Submit(CameraOwner.DynamicDeath, new CameraRequest
    {
        Fov = havePose ? CutsceneView.GameFovFor(poseFov, fovRatio) : null,
        Distance = haveHeldDistance ? heldDistance : null,
    });

    // ------------------------------------------------------------------
    // Positions
    // ------------------------------------------------------------------

    /// <summary>The 22 animated bones into <see cref="boneNow"/>. False when one of the seven of the body frame is
    /// missing; a missing bone of the rest keeps its last value (the chest before it was ever read).</summary>
    private bool ReadBones(nint address)
    {
        if (address == nint.Zero) return false;
        for (var i = 0; i < Bones.Length; i++)
        {
            var p = bones.GetBoneWorldPos(address, Bones[i]);
            if (p is { } v && Finite(v))
            {
                boneNow[i] = v;
                boneKnown[i] = true;
            }
            else if (i < CoreBones)
            {
                return false;
            }
            else if (!boneKnown[i])
            {
                boneNow[i] = boneNow[1];
            }
        }
        return true;
    }

    /// <summary>The 22 rigid bodies into <see cref="bodyNow"/>. False when one of the seven of the body frame has no
    /// body (the frame then stays on the bones, and is reported as bones); a bone of the rest without a body uses
    /// its bone.</summary>
    private bool ReadBodies(RagdollController? ragdoll)
    {
        if (ragdoll is not { IsActive: true }) return false;
        for (var i = 0; i < Bones.Length; i++)
        {
            var p = ragdoll.GetBodyWorldPosition(Bones[i]);
            if (p is { } v && Finite(v)) bodyNow[i] = v;
            else if (i < CoreBones) return false;
            else bodyNow[i] = boneKnown[i] ? boneNow[i] : bodyNow[1];
        }
        return true;
    }

    private static BodyPoints PointsOf(Vector3[] p) => new(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);

    // ------------------------------------------------------------------
    // Camera hand-over
    // ------------------------------------------------------------------

    /// <summary>
    /// Inside the game's camera update, right after it chose its eye and look-at point (ActiveCameraController's
    /// SetCameraLookAt detour, main camera only; before the cutscene shot's own override, which wins): the death
    /// shot's pose, as the solver gave it. After a revive, 0.3 s from the last pose to the game's own camera.
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
                log.Warning(ex, "DeathShot: pose override failed; leaving the game's camera alone this frame.");
            }
            return false;
        }
    }

    /// <summary>A cutscene shot wrote its own pose over this update's (it blends out toward the death shot).</summary>
    public void NotePoseSuperseded() => supersededThisUpdate = true;

    /// <summary>After the game's camera update: stop a shot nobody advances any more, and check once per shot that
    /// the game kept the pose it was handed.</summary>
    public void OnPostCameraUpdate(CameraBase* camera)
    {
        if (camera == null) return;
        var wrote = wroteThisUpdate && !supersededThisUpdate;
        wroteThisUpdate = false;
        supersededThisUpdate = false;
        if (state == State.Idle) return;

        updatesSinceAdvance++;
        if (updatesSinceAdvance > 1 && Environment.TickCount64 - lastAdvanceAt > TickStallMs)
        {
            log.Warning($"DeathShot: no tick for {Environment.TickCount64 - lastAdvanceAt} ms ({updatesSinceAdvance} camera updates); stopping the shot.");
            Stop("tick stalled");
            return;
        }

        if (wrote && state == State.Running)
            CheckTaken(camera);
    }

    /// <summary>Over the first <see cref="CheckFrames"/> overridden updates, the largest gap between the pose handed
    /// to the game and the scene camera it ended with, and between the rendered lens and the solver's; logged once.</summary>
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
        if (++checkCount < CheckFrames) return;

        var text = $"DeathShot: camera check over {CheckFrames} frames — scene eye off by up to {checkEye:0.000} m, " +
                   $"look-at by {checkLook:0.000} m, lens by {checkFov:0.0}°.";
        if (checkEye > 0.05f || checkLook > 0.05f)
            log.Warning(text + " The game did not keep the pose it was handed.");
        else if (checkFov > 1.5f)
            log.Warning(text + " The lens is not the one asked for (FoV field ratio?).");
        else
            log.Info(text);
    }

    /// <summary>End the shot. <paramref name="blend"/>: ease back to the game's camera over 0.3 s (a revive);
    /// otherwise the game's camera is back on the next update (reset, zone change, logout, unload, a game cutscene).</summary>
    public void Stop(string reason = "stopped", bool blend = false)
    {
        failed = false;
        haveHeldDistance = false;
        pendingWheel = 0f;
        if (state is State.Idle or State.BlendOut)
        {
            if (!blend && state == State.BlendOut)
            {
                state = State.Idle;
                havePose = false;
                status = "off";
            }
            return;
        }
        coordinator.Release(CameraOwner.DynamicDeath);
        if (state == State.Running)
            log.Info($"DeathShot: end after {shotTime:0.0} s ({reason}) — {Label(last.Shot)}, {PoseWord(last.Pose)}; " +
                     $"pose handed to the game in {overrideCount} camera update(s); {last.SideChanges} change(s) of side, " +
                     $"{last.Cuts} cut(s), {last.Fallbacks} fallback(s), eye found inside terrain {last.TerrainPops} time(s), " +
                     $"{skippedFrames} frame(s) without a readable body.");
        else
            log.Info($"DeathShot: end while waiting ({reason}).");
        resumed = false;
        if (blend && state == State.Running && havePose)
        {
            state = State.BlendOut;
            blendOutTime = 0f;
            status = "death shot — back to the game's camera";
        }
        else
        {
            state = State.Idle;
            havePose = false;
            status = "off";
        }
    }

    // ------------------------------------------------------------------
    // Log
    // ------------------------------------------------------------------

    private void Report(in DeathShotOutput o, in BodyPoints points, BodyPoints? previousSource, bool first, bool physics, bool expect, float dt)
    {
        var t = shotTime;
        if (first)
        {
            log.Info($"DeathShot: first frame — {Label(profile)} → {Label(o.Shot)}, pose {PoseWord(o.Pose)}, {Side(in o, in points)}; " +
                     $"positions from the {(physics ? "ragdoll bodies" : "bones")}, " +
                     $"{(expect ? physics ? "ragdoll running" : "waiting for the ragdoll" : "no ragdoll expected")}, body scale {solver.BodyScale:0.00}.");
        }
        else if (o.SideChanged)
        {
            log.Info($"DeathShot: side at {t:0.0} s — {o.Reason} → {Label(o.Shot)} ({(o.Cut ? "cut" : "ease")}), pose {PoseWord(o.Pose)}, " +
                     $"{Side(in o, in points)}; change {o.SideChanges} of this death.");
        }

        if (o.SourceSwitched)
        {
            var largest = previousSource is { } old ? $", largest of the seven points {old.MaxDistance(points) * 100f:0.0} cm" : " (no bones this frame: carried on at the speed it had)";
            var moved = haveLastOut ? Vector3.Distance(o.Eye, lastOutEye) * 100f : 0f;
            log.Info($"DeathShot: ragdoll at {t:0.0} s — positions now from the {(physics ? "ragdoll bodies" : "bones")}; " +
                     $"source jump at the hips {o.SourceJump * 100f:0.0} cm{largest}, absorbed over {DeathShotSolver.SwitchSeconds:0.0} s; " +
                     $"the eye moved {moved:0.0} cm this frame.");
        }

        if (o.BodySettled != lastSettled)
        {
            lastSettled = o.BodySettled;
            if (o.BodySettled)
                log.Info($"DeathShot: settled at {t:0.0} s — pose {PoseWord(o.Pose)}, {Label(o.Shot)} holds, {Side(in o, in points)}.");
            else
                log.Info($"DeathShot: the body moves again at {t:0.0} s — following.");
        }

        if (o.Cuts > lastCuts && !o.SideChanged)
            log.Info($"DeathShot: cut at {t:0.0} s ({o.Reason}), cut {o.Cuts} of this death.");
        if (o.Fallbacks > lastFallbacks)
            log.Info($"DeathShot: fallback at {t:0.0} s — the side the rule asks for is blocked or loses the body; using {Label(o.Shot)}, " +
                     $"{Side(in o, in points)} (fallback {o.Fallbacks}).");
        if (o.TerrainPops > lastPops)
            log.Info($"DeathShot: the eye was inside terrain at {t:0.0} s and was put on its surface ({o.TerrainPops} so far).");
        if (o.PulledIn != lastPulled)
        {
            lastPulled = o.PulledIn;
            if (o.PulledIn) log.Info($"DeathShot: view blocked at {t:0.0} s — eye pulled in front of it, lens widened to {Degrees(o.GoalFov):0.0}°.");
        }
        if (expect != lastExpect && !first && !expect && !physics)
            log.Info($"DeathShot: no ragdoll is coming at {t:0.0} s — the death animation coming to rest counts as settled.");
        lastExpect = expect;
        lastCuts = o.Cuts;
        lastFallbacks = o.Fallbacks;
        lastPops = o.TerrainPops;

        // Window numbers for the summary: how fast the eye and the view moved outside cuts, and what the world cost.
        var direction = o.LookAt - o.Eye;
        var length = direction.Length();
        if (length > 1e-4f) direction /= length;
        if (haveLastOut && !o.Cut && dt > 1e-4f)
        {
            windowEyeSpeed = MathF.Max(windowEyeSpeed, Vector3.Distance(o.Eye, lastOutEye) / dt);
            var cos = Math.Clamp(Vector3.Dot(direction, lastOutDir), -1f, 1f);
            windowViewRate = MathF.Max(windowViewRate, Degrees(MathF.Acos(cos)) / dt);
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
        var ground = world.GroundBelow(o.Eye + new Vector3(0f, DeathShotSolver.GroundRayLift, 0f));
        log.Info($"DeathShot: {t:0.0} s — {Label(o.Shot)}{(profile == DeathShotProfile.Auto ? " (Auto)" : "")} [{PoseWord(o.Pose)}], " +
                 $"{(o.Holding ? "holding" : expect && !physics ? "waiting for the ragdoll" : "following")}, {(physics ? "ragdoll bodies" : "bones")}; " +
                 $"{Side(in o, in points)}, eye {(ground is { } g ? $"{o.Eye.Y - g:0.00} m above ground" : "over no ground")}, " +
                 $"lens {Degrees(o.Fov):0.0}°; player yaw {Degrees(userYaw):0}°, size ×{userSize:0.00}; " +
                 $"last 5 s: eye up to {windowEyeSpeed:0.00} m/s, view up to {windowViewRate:0}°/s, " +
                 $"{(windowFrames > 0 ? (float)windowRays / windowFrames : 0f):0.0} rays/frame (max {windowRaysMax}); " +
                 $"sides {o.SideChanges}, cuts {o.Cuts}, fallbacks {o.Fallbacks}; handed {overrideCount}.");
        ResetWindow();
    }

    private void ResetWindow()
    {
        windowEyeSpeed = windowViewRate = 0f;
        windowFrames = windowRays = windowRaysMax = 0;
    }

    /// <summary>Where the eye sits about the body: degrees off the body's (horizontal) front, the eye's elevation
    /// about the look-at point, the distance.</summary>
    private static string Side(in DeathShotOutput o, in BodyPoints points)
    {
        var frame = BodyFrame.From(points, points.FeetMid.Y);
        var away = o.GoalEye - o.GoalLookAt;
        var distance = away.Length();
        if (distance < 1e-3f) return "side unknown";
        var off = Degrees(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frame.FrontHorizontal), DeathShotMath.Azimuth(away)));
        var elevation = Degrees(MathF.Asin(Math.Clamp(away.Y / distance, -1f, 1f)));
        return $"side {off:+0;-0}° off the body's front, eye {elevation:+0;-0}° about the subject, {distance:0.00} m";
    }

    private static string PoseWord(PoseClass pose) => pose switch
    {
        PoseClass.Standing => "standing",
        PoseClass.Kneeling => "kneeling/sitting",
        PoseClass.Supine => "on the back",
        PoseClass.Prone => "face down",
        _ => "on the side",
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
    /// The solver's world, answered with the game's collision: ground and walls by
    /// <see cref="BGCollisionModule.RaycastMaterialFilter"/>, water by the water-material lookup. Identical queries
    /// within one frame are answered once (to the millimetre: a coarser grid would step the eye's floor, which the
    /// solver applies after its damping).
    /// </summary>
    private sealed class GameWorld : IDeathShotWorld
    {
        private const float RayLength = 80f;
        /// <summary>A water surface is looked for up to here above the query and down to here below it. Not higher:
        /// a pool on a terrace above must not lift the eye onto it.</summary>
        private const float WaterSearchAbove = 4f, WaterSearchBelow = 20f;

        private readonly Dictionary<(int, int, int), float?> ground = new();
        private readonly Dictionary<(int, int, int), float?> water = new();
        private float subjectY = float.NaN;

        /// <summary>Rays cast this frame.</summary>
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

        /// <summary>The ground under the eye: from where the solver starts the ray (about 1.5 m above the eye), and,
        /// when that start is lower than the same lift above the body, from that height as well: a surface between
        /// the two is one the first ray started under (the eye is inside a slope or a bank at the body's level).</summary>
        public float? GroundBelow(Vector3 from)
        {
            var key = Key(from);
            if (ground.TryGetValue(key, out var known)) return known;
            var result = Down(from);
            if (float.IsFinite(subjectY))
            {
                var top = subjectY + DeathShotSolver.GroundRayLift;
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
