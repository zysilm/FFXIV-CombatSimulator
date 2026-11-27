// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Camera.Cutscene;
using CombatSimulator.Replay.Model;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Replay;

/// <summary>
/// The death replay's camera (docs/DEATH_REPLAY.md §6): the free camera (the shot editor) and the replay's shots.
/// Both go through the one camera path every plugin camera uses: the eye and look-at point handed to the game inside
/// its camera update (<see cref="ActiveCameraController.PoseOverride"/>, written last), the lens, the held zoom and
/// the game camera's yaw and pitch through the coordinator.
///
/// Owners: the free camera is <see cref="CameraOwner.FreeCam"/> (above everything: it is the user's tool, switched on
/// and off by hand); the replay's shots are <see cref="CameraOwner.DeathReplay"/> (above the death shot, the duel and
/// Directed cameras and the user's Follow-bone camera, below a cutscene shot, which the replay waits for before it
/// starts). Letting go blends from the last pose to whatever is under it over 0.3 s.
///
/// The free camera looks with the mouse through the game camera itself: the game turns its camera on a drag with
/// the player's own sensitivity and inversion; each tick the yaw and pitch it reached are read against what was
/// written the tick before and the difference turns the free camera (only while a mouse button is held or the right
/// stick is off centre: the game also turns its camera on its own). It flies on W/S/A/D, E (up) and Q (down); Shift
/// four times faster, Ctrl four times slower; the wheel changes the field of view.
/// </summary>
internal sealed unsafe class ReplayCamera
{
    public const float BlendOutSeconds = 0.3f;
    private const long TickStallMs = 500;
    private const float BaseSpeed = 3f;
    private const float MaxInputStep = 1.0f;
    private const float StickDeadZone = 0.2f;
    private const float WheelStep = 0.08f;

    private readonly CameraModeCoordinator coordinator;
    private readonly IKeyState keyState;
    private readonly IGamepadState gamepad;
    private readonly IPluginLog log;

    // the free camera
    private bool free;
    private Vector3 freeEye;
    private float freeYaw, freePitch, freeFov = ReplayMath.DefaultFov;
    private float pendingWheel;
    private bool handWasOn;

    // the replay's view (null: not asked for)
    private ReplayView? shotView;

    // what is handed over
    private ReplayView pose;
    private bool havePose;
    private CameraOwner owner = CameraOwner.None;
    private float fovRatio = 1f;
    private bool haveRatio;

    // the game camera held meanwhile
    private float writtenDirH, heldDirV, heldDistance;

    // letting go
    private bool blendingOut;
    private float blendTime;
    private Vector3 blendEye, blendLook;

    private long lastTickAt;
    private bool loggedOverrideError;

    public ReplayCamera(CameraModeCoordinator coordinator, IKeyState keyState, IGamepadState gamepad, IPluginLog log)
    {
        this.coordinator = coordinator;
        this.keyState = keyState;
        this.gamepad = gamepad;
        this.log = log;
    }

    public bool FreeCamOn => free;
    /// <summary>Writing the camera (or blending back from it): models near the eye are kept drawn.</summary>
    public bool IsRunning => owner != CameraOwner.None || blendingOut;
    public bool Idle => !free && shotView == null && !blendingOut && owner == CameraOwner.None;
    public ReplayView FreeView => new(freeEye, ReplayMath.Forward(freeYaw, freePitch), freeFov);

    public void NotifyMouseWheel(float wheel)
    {
        if (free && wheel != 0f && float.IsFinite(wheel)) pendingWheel += wheel;
    }

    /// <summary>The view the game rendered last (eye, direction, vertical lens), or null when it cannot be read.</summary>
    public static ReplayView? RenderedView()
    {
        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null) return null;
        var sc = &camMgr->Camera->CameraBase.SceneCamera;
        if (sc->RenderCamera == null) return null;
        var eye = new Vector3(sc->Position.X, sc->Position.Y, sc->Position.Z);
        var look = new Vector3(sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
        var d = look - eye;
        if (!(d.LengthSquared() > 1e-6f)) return null;
        var fov = CutsceneView.VerticalFovOf(ToNumerics(sc->RenderCamera->ProjectionMatrix));
        var view = new ReplayView(eye, Vector3.Normalize(d), float.IsFinite(fov) ? Math.Clamp(fov, ReplayMath.MinFov, ReplayMath.MaxFov) : ReplayMath.DefaultFov);
        return view.IsFinite ? view : null;
    }

    /// <summary>Switches the free camera on (from <paramref name="start"/>, or the view on the screen) or moves it to
    /// <paramref name="start"/> when it is on already.</summary>
    public bool EnterFreeCam(ReplayView? start = null)
    {
        var from = start ?? (free ? FreeView : RenderedView());
        if (from is not { IsFinite: true } v)
        {
            log.Warning("DeathReplay: the free camera cannot start — the game camera cannot be read.");
            return false;
        }
        freeEye = v.Eye;
        freeYaw = ReplayMath.Azimuth(v.Forward);
        freePitch = Math.Clamp(ReplayMath.PitchOf(v.Forward), -ReplayMath.MaxPitch, ReplayMath.MaxPitch);
        freeFov = Math.Clamp(v.Fov, ReplayMath.MinFov, ReplayMath.MaxFov);
        if (!free)
        {
            free = true;
            pendingWheel = 0f;
            handWasOn = false;
            log.Info($"DeathReplay: free camera on at ({freeEye.X:0.0}, {freeEye.Y:0.0}, {freeEye.Z:0.0}), lens {freeFov / ReplayMath.Deg:0}°.");
        }
        return true;
    }

    public void LeaveFreeCam(string reason)
    {
        if (!free) return;
        free = false;
        log.Info($"DeathReplay: free camera off ({reason}).");
    }

    /// <summary>The replay's view for this frame, or null to let go of the camera.</summary>
    public void SetShotView(ReplayView? view) => shotView = view is { IsFinite: true } ? view : null;

    /// <summary>Every framework tick, before the coordinator applies: input, then the request.</summary>
    public void Tick(float dt, bool keyboardFree)
    {
        lastTickAt = Environment.TickCount64;
        var camMgr = GameCameraManager.Instance();
        var game = camMgr != null ? camMgr->Camera : null;

        var want = free ? CameraOwner.FreeCam : shotView != null ? CameraOwner.DeathReplay : CameraOwner.None;
        if (want == CameraOwner.None || game == null)
        {
            if (owner != CameraOwner.None) LetGo();
            AdvanceBlend(dt);
            return;
        }

        if (owner == CameraOwner.None)
        {
            if (!TakeHold(game)) return;
        }
        else if (owner != want)
        {
            coordinator.Release(owner);
        }
        owner = want;
        blendingOut = false;

        if (free)
        {
            // ---- the look: the game camera's yaw and pitch since they were written ----
            if (HandOnCamera())
            {
                var dYaw = AngleDelta(writtenDirH, game->DirH);
                var dPitch = game->DirV - heldDirV;
                if (float.IsFinite(dYaw) && MathF.Abs(dYaw) <= MaxInputStep) freeYaw = ReplayMath.WrapAngle(freeYaw + dYaw);
                if (float.IsFinite(dPitch) && MathF.Abs(dPitch) <= MaxInputStep)
                    freePitch = Math.Clamp(freePitch + dPitch, -ReplayMath.MaxPitch, ReplayMath.MaxPitch);
            }
            // ---- the wheel: the lens ----
            if (pendingWheel != 0f)
            {
                freeFov = Math.Clamp(freeFov * MathF.Exp(-WheelStep * Math.Clamp(pendingWheel, -20f, 20f)), ReplayMath.MinFov, ReplayMath.MaxFov);
                pendingWheel = 0f;
            }
            // ---- flying ----
            if (keyboardFree)
            {
                float fwd = 0f, strafe = 0f, up = 0f;
                if (Down(VirtualKey.W)) fwd += 1f;
                if (Down(VirtualKey.S)) fwd -= 1f;
                if (Down(VirtualKey.D)) strafe += 1f;
                if (Down(VirtualKey.A)) strafe -= 1f;
                if (Down(VirtualKey.E)) up += 1f;
                if (Down(VirtualKey.Q)) up -= 1f;
                var speed = BaseSpeed * (Down(VirtualKey.SHIFT) ? 4f : 1f) * (Down(VirtualKey.CONTROL) ? 0.25f : 1f);
                freeEye = ReplayMath.FlyStep(freeEye, ReplayMath.Forward(freeYaw, freePitch), fwd, strafe, up, speed, Math.Clamp(dt, 0f, 0.1f));
            }
            pose = FreeView;
            havePose = pose.IsFinite;
        }
        else
        {
            pose = shotView!.Value;
            havePose = true;
        }

        coordinator.Submit(owner, new CameraRequest
        {
            Fov = havePose && haveRatio ? CutsceneView.GameFovFor(pose.Fov, fovRatio) : null,
            Distance = heldDistance,
            DirH = writtenDirH,
            DirV = heldDirV,
        });
    }

    /// <summary>Takes the camera: the lens ratio measured from what the game renders now, its zoom, yaw and pitch
    /// held (the pitch in the middle of its range, so a drag either way reads).</summary>
    private bool TakeHold(FFXIVClientStructs.FFXIV.Client.Game.Camera* game)
    {
        var sc = &game->CameraBase.SceneCamera;
        if (sc->RenderCamera == null) return false;
        var rendered = CutsceneView.VerticalFovOf(ToNumerics(sc->RenderCamera->ProjectionMatrix));
        if (CutsceneView.FovRatio(rendered, game->FoV) is { } ratio)
        {
            fovRatio = ratio;
            haveRatio = true;
        }
        heldDistance = float.IsFinite(game->Distance) && game->Distance > 0f ? game->Distance : 5f;
        writtenDirH = float.IsFinite(game->DirH) ? game->DirH : 0f;
        var mid = (game->DirVMin + game->DirVMax) * 0.5f;
        heldDirV = float.IsFinite(mid) ? mid : 0f;
        loggedOverrideError = false;
        return true;
    }

    private void LetGo()
    {
        coordinator.Release(owner);
        owner = CameraOwner.None;
        if (havePose)
        {
            blendingOut = true;
            blendTime = 0f;
            blendEye = pose.Eye;
            blendLook = pose.LookAt;
        }
        havePose = false;
    }

    private void AdvanceBlend(float dt)
    {
        if (!blendingOut) return;
        blendTime += Math.Clamp(dt, 0f, 0.1f);
        if (blendTime >= BlendOutSeconds) blendingOut = false;
    }

    /// <summary>Drops the camera at once (zone change, logout, unload, an error): no blend.</summary>
    public void Reset()
    {
        free = false;
        shotView = null;
        if (owner != CameraOwner.None) coordinator.Release(owner);
        owner = CameraOwner.None;
        havePose = blendingOut = false;
    }

    /// <summary>Inside the game's camera update (written after every other camera): the pose, or the blend from the
    /// last one to whatever is under it.</summary>
    public bool OverridePose(ref Vector3 lookAt, ref Vector3 eye)
    {
        if (Environment.TickCount64 - lastTickAt > TickStallMs) return false;
        try
        {
            if (owner != CameraOwner.None && havePose)
            {
                var l = pose.LookAt;
                if (!CutsceneView.CanHandOver(pose.Eye, l)) return false;
                eye = pose.Eye;
                lookAt = l;
                return true;
            }
            if (blendingOut)
            {
                var w = CutPose.Ease(Math.Clamp(blendTime / BlendOutSeconds, 0f, 1f));
                var e = Vector3.Lerp(blendEye, eye, w);
                var l = Vector3.Lerp(blendLook, lookAt, w);
                if (!CutsceneView.CanHandOver(e, l)) return false;
                eye = e;
                lookAt = l;
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!loggedOverrideError)
            {
                loggedOverrideError = true;
                log.Warning(ex, "DeathReplay: camera hand-over failed; leaving the game's camera alone this frame.");
            }
        }
        return false;
    }

    private bool Down(VirtualKey k)
    {
        try { return keyState[k]; }
        catch { return false; }
    }

    /// <summary>A mouse button held or the right stick off centre, on this tick or the one before (only then is a
    /// change of the game camera's yaw or pitch the player's hand).</summary>
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

    private static float AngleDelta(float from, float to) => ReplayMath.WrapAngle(to - from);

    private static Matrix4x4 ToNumerics(in FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
}
