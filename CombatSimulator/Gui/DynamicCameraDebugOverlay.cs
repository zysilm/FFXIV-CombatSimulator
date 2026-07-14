using System;
using System.Numerics;
using CombatSimulator.Camera;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Gui;

/// <summary>
/// Dynamic Camera tuning overlay. Draws the safe frame, every anchor the framing solver
/// is required to keep in shot, and a readout of what it solved.
///
/// It also does one job that is not cosmetic: each anchor is plotted twice — once from
/// the game's own WorldToScreen (green) and once from the solver's projection maths
/// (magenta). If the two ever disagree, the solver's idea of the camera basis is wrong,
/// and every framing bug downstream would be misattributed. They should sit on top of
/// each other; a systematic mirror or offset says the DirH/DirV/FoV convention needs
/// fixing, not the framing.
/// </summary>
public sealed unsafe class DynamicCameraDebugOverlay
{
    private readonly DynamicCameraController dynamicCam;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public DynamicCameraDebugOverlay(DynamicCameraController dynamicCam, IGameGui gameGui, Configuration config)
    {
        this.dynamicCam = dynamicCam;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        if (!config.DynCamDebugOverlay || !config.EnableDynamicCamera)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        var io = ImGui.GetIO();
        var screen = io.DisplaySize;
        if (screen.X < 1f || screen.Y < 1f)
            return;

        DrawSafeFrame(drawList, screen);
        DrawAnchors(drawList, screen);
        DrawReadout(screen);
    }

    private void DrawSafeFrame(ImDrawListPtr drawList, Vector2 screen)
    {
        if (dynamicCam.CurrentPhase is not (DynamicCameraController.Phase.DeathTranslate
            or DynamicCameraController.Phase.DeathHold))
            return;

        var margin = Math.Clamp(config.DynCamDeathSafeMargin, 0.01f, 0.4f);
        var half = new Vector2(screen.X * 0.5f, screen.Y * 0.5f);
        var inset = new Vector2(half.X * margin, half.Y * margin);

        drawList.AddRect(
            inset,
            screen - inset,
            ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 0.55f)),
            0f, ImDrawFlags.None, 2f);
    }

    private void DrawAnchors(ImDrawListPtr drawList, Vector2 screen)
    {
        var points = dynamicCam.RequiredPoints;
        if (points.Count == 0)
            return;

        var haveSolverView = TryGetCameraView(out var cam, out var dirH, out var dirV, out var fov);
        DynamicCameraSolver.Basis(dirH, dirV, out var fwd, out var right, out var up);

        var gameColor = ImGui.GetColorU32(new Vector4(0.3f, 1f, 0.4f, 0.95f));
        var solverColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.9f, 0.8f));

        for (var i = 0; i < points.Count; i++)
        {
            var world = points[i];

            if (gameGui.WorldToScreen(world, out var gamePt))
                drawList.AddCircleFilled(gamePt, 5f, gameColor);

            if (!haveSolverView)
                continue;

            var ndc = DynamicCameraSolver.Project(world, cam, fwd, right, up, fov, dynamicCam.Aspect);
            if (ndc.Z <= 0.05f)
                continue;

            // NDC → screen. +1 in y is the TOP of the frame, so it flips.
            var solverPt = new Vector2(
                (ndc.X * 0.5f + 0.5f) * screen.X,
                (0.5f - ndc.Y * 0.5f) * screen.Y);
            drawList.AddCircle(solverPt, 9f, solverColor, 12, 2f);
        }
    }

    private void DrawReadout(Vector2 screen)
    {
        ImGui.SetNextWindowPos(new Vector2(20f, screen.Y * 0.35f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.7f);
        if (!ImGui.Begin("Dynamic Cam##dyncamdebug",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), dynamicCam.CurrentPhase.ToString());
        ImGui.TextUnformatted(dynamicCam.StatusText);
        ImGui.Separator();

        if (dynamicCam.LastSolveOk)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "solve: ok");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "solve: FAILED (holding last frame)");

        ImGui.TextUnformatted($"distance {dynamicCam.SolvedDistance:F2}   DirV {dynamicCam.SolvedPitch:F3}");
        ImGui.TextUnformatted($"fov {dynamicCam.SolvedFov:F3}   yaw {dynamicCam.SolvedYaw:F3}   aspect {dynamicCam.Aspect:F2}");
        ImGui.TextUnformatted($"anchors {dynamicCam.RequiredPoints.Count}   killer {dynamicCam.CurrentKillerFit}   ladder {dynamicCam.LadderLevel}");

        if (dynamicCam.CurrentPhase is DynamicCameraController.Phase.DeathTranslate
            or DynamicCameraController.Phase.DeathHold)
        {
            var camY = TryGetCameraView(out var camPos, out _, out _, out _) ? camPos.Y : float.NaN;
            var aboveGround = camY - dynamicCam.DebugGroundY;
            var color = aboveGround < 0.05f
                ? new Vector4(1f, 0.3f, 0.3f, 1f)   // camera at or below the floor — the v1 failure
                : new Vector4(0.6f, 0.9f, 0.6f, 1f);
            ImGui.TextColored(color, $"ground {dynamicCam.DebugGroundY:F2}   cam {camY:F2}   above {aboveGround:+0.00;-0.00}");
            ImGui.TextUnformatted($"standoff {dynamicCam.DebugStandoff:F2} y from body");
        }

        ImGui.Separator();
        ImGui.TextDisabled("green = game WorldToScreen");
        ImGui.TextDisabled("magenta = solver projection");
        ImGui.TextDisabled("they must coincide");

        ImGui.End();
    }

    private static bool TryGetCameraView(out Vector3 cam, out float dirH, out float dirV, out float fov)
    {
        cam = default;
        dirH = 0f;
        dirV = 0f;
        fov = 0.78f;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return false;

            var gameCam = camMgr->Camera;
            var pos = gameCam->CameraBase.SceneCamera.Position;
            cam = new Vector3(pos.X, pos.Y, pos.Z);
            dirH = gameCam->DirH;
            dirV = gameCam->DirV;
            fov = gameCam->FoV;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
