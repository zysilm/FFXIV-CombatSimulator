using System;
using System.Numerics;
using CombatSimulator.Camera;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Dynamic Camera tuning overlay. Draws the safe frame, every anchor the framing solver
/// is required to keep in shot, and a readout of what it solved.
///
/// Each anchor is plotted twice — green from the game's own WorldToScreen, magenta from
/// our projection built on the MEASURED view (ViewMatrix basis + projection-matrix lens).
/// These must coincide; if they do not, our projection maths itself is broken, not a
/// convention. The convention questions (what DirV means, what the FoV field means) are
/// answered numerically in the readout instead: it prints the measured pitch against
/// ±DirV and the measured lens against tan(FoV/2), so one glance settles what the game
/// actually does.
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

        var haveView = GameCameraView.TryRead(out var view);

        DrawSafeFrame(drawList, screen);
        if (haveView)
            DrawAnchors(drawList, screen, in view);
        DrawReadout(screen, haveView, in view);
    }

    private void DrawSafeFrame(ImDrawListPtr drawList, Vector2 screen)
    {
        if (dynamicCam.CurrentPhase is not (DynamicCameraController.Phase.DeathTranslate
            or DynamicCameraController.Phase.DeathHold))
            return;

        var margin = DynamicCameraController.DeathSafeMargin;
        var half = new Vector2(screen.X * 0.5f, screen.Y * 0.5f);
        var inset = new Vector2(half.X * margin, half.Y * margin);

        drawList.AddRect(
            inset,
            screen - inset,
            ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 0.55f)),
            0f, ImDrawFlags.None, 2f);
    }

    private void DrawAnchors(ImDrawListPtr drawList, Vector2 screen, in GameCameraView view)
    {
        var points = dynamicCam.RequiredPoints;
        if (points.Count == 0)
            return;

        var lens = new DynamicCameraSolver.Lens(view.TanHalfH, view.TanHalfV);
        DynamicCameraSolver.BasisFromForward(view.Forward, out var right, out var up);

        var gameColor = ImGui.GetColorU32(new Vector4(0.3f, 1f, 0.4f, 0.95f));
        var solverColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.9f, 0.8f));

        for (var i = 0; i < points.Count; i++)
        {
            var world = points[i];

            if (gameGui.WorldToScreen(world, out var gamePt))
                drawList.AddCircleFilled(gamePt, 5f, gameColor);

            var ndc = DynamicCameraSolver.Project(world, view.Position, view.Forward, right, up, lens);
            if (ndc.Z <= 0.05f)
                continue;

            // NDC → screen. +1 in y is the TOP of the frame, so it flips.
            var solverPt = new Vector2(
                (ndc.X * 0.5f + 0.5f) * screen.X,
                (0.5f - ndc.Y * 0.5f) * screen.Y);
            drawList.AddCircle(solverPt, 9f, solverColor, 12, 2f);
        }
    }

    private void DrawReadout(Vector2 screen, bool haveView, in GameCameraView view)
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

        ImGui.TextUnformatted($"distance {dynamicCam.SolvedDistance:F2}   chi {dynamicCam.SolvedChi:F3}");
        ImGui.TextUnformatted($"fov {dynamicCam.SolvedFov:F3}   yaw {dynamicCam.SolvedYaw:F3}");
        ImGui.TextUnformatted($"anchors {dynamicCam.RequiredPoints.Count}   killer {dynamicCam.CurrentKillerFit}   ladder {dynamicCam.LadderLevel}");

        if (haveView)
        {
            ImGui.Separator();

            // The convention report. chiReal is measured off the actual view direction;
            // whichever ±DirV column tracks it is the game's mapping. Same for the lens:
            // whichever tan column matches TanHalfV says what the FoV field means.
            DynamicCameraSolver.MeasureAngles(view.Forward, out var yawReal, out var chiReal);
            ImGui.TextUnformatted($"chi(real) {chiReal:+0.000;-0.000}   -DirV {-view.DirV:+0.000;-0.000}   +DirV {view.DirV:+0.000;-0.000}");
            var tanGame = MathF.Tan(Math.Clamp(view.GameFov, 0.05f, 2.8f) * 0.5f);
            var aspect = MathF.Abs(view.TanHalfV) > 1e-4f ? view.TanHalfH / view.TanHalfV : 0f;
            ImGui.TextUnformatted($"tanHalfV {view.TanHalfV:F3}   tan(fov/2) {tanGame:F3}   tan/aspect {(aspect != 0f ? tanGame / MathF.Abs(aspect) : 0f):F3}");
            ImGui.TextUnformatted($"tanHalfH {view.TanHalfH:F3}   aspect {aspect:F2}   pitch-sign belief {dynamicCam.PitchWriteSign:+0;-0}");
        }

        if (dynamicCam.CurrentPhase is DynamicCameraController.Phase.DeathTranslate
            or DynamicCameraController.Phase.DeathHold)
        {
            ImGui.Separator();
            var camY = haveView ? view.Position.Y : float.NaN;
            var aboveGround = camY - dynamicCam.DebugGroundY;
            var color = aboveGround < 0.05f
                ? new Vector4(1f, 0.3f, 0.3f, 1f)   // camera at or below the floor — the v1 failure
                : new Vector4(0.6f, 0.9f, 0.6f, 1f);
            ImGui.TextColored(color, $"ground {dynamicCam.DebugGroundY:F2}   cam {camY:F2}   above {aboveGround:+0.00;-0.00}");
            ImGui.TextUnformatted($"standoff {dynamicCam.DebugStandoff:F2} y from body");
        }

        ImGui.Separator();
        ImGui.TextDisabled("green = game WorldToScreen");
        ImGui.TextDisabled("magenta = our projection on the measured view");
        ImGui.TextDisabled("they must coincide");

        ImGui.Separator();
        if (ImGui.Button("Copy all diagnostics##dyncam"))
            ImGui.SetClipboardText(BuildDiagnostics(screen, haveView, in view));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies every number on this panel plus the per-anchor projections (game vs ours, with deltas) to the clipboard.");

        ImGui.End();
    }

    /// <summary>Everything the panel knows, as text — one click instead of reading numbers
    /// off the screen mid-death.</summary>
    private string BuildDiagnostics(Vector2 screen, bool haveView, in GameCameraView view)
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine("=== Dynamic Camera diagnostics ===");
        sb.AppendLine($"phase {dynamicCam.CurrentPhase} | {dynamicCam.StatusText}");
        sb.AppendLine($"solve ok={dynamicCam.LastSolveOk} distance={dynamicCam.SolvedDistance:F3} chi={dynamicCam.SolvedChi:F4} fov={dynamicCam.SolvedFov:F4} yaw={dynamicCam.SolvedYaw:F4}");
        sb.AppendLine($"anchors={dynamicCam.RequiredPoints.Count} killer={dynamicCam.CurrentKillerFit} ladder={dynamicCam.LadderLevel}");
        sb.AppendLine($"ground={dynamicCam.DebugGroundY:F3} standoff={dynamicCam.DebugStandoff:F3} pitchSignBelief={dynamicCam.PitchWriteSign:+0;-0}");
        sb.AppendLine($"screen={screen.X:F0}x{screen.Y:F0}");

        if (haveView)
        {
            DynamicCameraSolver.MeasureAngles(view.Forward, out var yawReal, out var chiReal);
            var tanGame = MathF.Tan(Math.Clamp(view.GameFov, 0.05f, 2.8f) * 0.5f);
            sb.AppendLine("--- measured view ---");
            sb.AppendLine($"pos=({view.Position.X:F2},{view.Position.Y:F2},{view.Position.Z:F2}) fwd=({view.Forward.X:F3},{view.Forward.Y:F3},{view.Forward.Z:F3})");
            sb.AppendLine($"yawReal={yawReal:F4} chiReal={chiReal:F4} | DirH={view.DirH:F4} DirV={view.DirV:F4} (-DirV={-view.DirV:F4})");
            sb.AppendLine($"tanHalfH={view.TanHalfH:F4} tanHalfV={view.TanHalfV:F4} | gameFov={view.GameFov:F4} tan(fov/2)={tanGame:F4}");
            sb.AppendLine($"viewMatrixAxes={(view.AxesAreRows ? "rows" : "cols")} axisScore={view.AxisScore:F3}");

            var lens = new DynamicCameraSolver.Lens(view.TanHalfH, view.TanHalfV);
            DynamicCameraSolver.BasisFromForward(view.Forward, out var right, out var up);

            sb.AppendLine("--- anchors: game px | ours px | delta ---");
            var points = dynamicCam.RequiredPoints;
            for (var i = 0; i < points.Count; i++)
            {
                var world = points[i];
                var haveGame = gameGui.WorldToScreen(world, out var gamePt);
                var ndc = DynamicCameraSolver.Project(world, view.Position, view.Forward, right, up, lens);
                var oursVisible = ndc.Z > 0.05f;
                var oursPt = new Vector2(
                    (ndc.X * 0.5f + 0.5f) * screen.X,
                    (0.5f - ndc.Y * 0.5f) * screen.Y);

                sb.Append($"[{i}] world=({world.X:F2},{world.Y:F2},{world.Z:F2}) ");
                sb.Append(haveGame ? $"game=({gamePt.X:F0},{gamePt.Y:F0}) " : "game=offscreen ");
                sb.Append(oursVisible ? $"ours=({oursPt.X:F0},{oursPt.Y:F0}) ndc=({ndc.X:F3},{ndc.Y:F3},{ndc.Z:F2}) " : "ours=behind ");
                if (haveGame && oursVisible)
                    sb.Append($"delta=({oursPt.X - gamePt.X:F0},{oursPt.Y - gamePt.Y:F0})");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("no render camera view available");
        }

        sb.AppendLine("--- config ---");
        sb.AppendLine($"bodyBand={config.DynCamDeathBodyBand:F2} angle={config.DynCamDeathAngle:F3} maximize={config.DynCamDeathMaximizeBody} closeUp={config.DynCamDeathCloseUpDistance:F2} body={config.DynCamDeathBodyVisibility:F2}");
        sb.AppendLine($"share={config.DynCamSubjectScreenShare:F2} shoulder={config.DynCamShoulderScreenFrac:F2} duration={config.DynCamDeathTranslateDuration:F1}");
        return sb.ToString();
    }
}
