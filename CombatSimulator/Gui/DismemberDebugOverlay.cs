using System;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Wireframe overlay for the dropped-head collision hull (vertices + body axes), so the head
/// physics shape can be tuned against the rendered mesh — like the ragdoll debug overlay.
/// </summary>
public class DismemberDebugOverlay
{
    private readonly DismembermentController dismemberment;
    private readonly Configuration config;
    private readonly IGameGui gameGui;

    public DismemberDebugOverlay(DismembermentController dismemberment, Configuration config, IGameGui gameGui)
    {
        this.dismemberment = dismemberment;
        this.config = config;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!config.DismemberDebugOverlay) return;
        var hulls = dismemberment.GetDebugHeadHulls();
        if (hulls.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();
        var vtxColor = ImGui.GetColorU32(new Vector4(0.2f, 0.9f, 1f, 0.9f));
        var linkColor = ImGui.GetColorU32(new Vector4(0.2f, 0.9f, 1f, 0.35f));

        foreach (var h in hulls)
        {
            // Hull vertices as dots, faintly linked to the centroid so the cloud's extent reads.
            Vector2 centerScreen = default;
            var haveCenter = gameGui.WorldToScreen(h.Center, out centerScreen);
            foreach (var wp in h.WorldPoints)
            {
                if (!gameGui.WorldToScreen(wp, out var sp)) continue;
                drawList.AddCircleFilled(sp, 2.2f, vtxColor);
                if (haveCenter) drawList.AddLine(centerScreen, sp, linkColor, 1f);
            }

            // Orientation axes from the centroid: X red, Y(up) green, Z(face/+Z) blue — so the nose
            // direction is visible at a glance.
            DrawAxis(drawList, h.Center, Vector3.Transform(Vector3.UnitX, h.Orientation) * 0.12f, new Vector4(1f, 0.3f, 0.3f, 1f));
            DrawAxis(drawList, h.Center, Vector3.Transform(Vector3.UnitY, h.Orientation) * 0.12f, new Vector4(0.3f, 1f, 0.3f, 1f));
            DrawAxis(drawList, h.Center, Vector3.Transform(Vector3.UnitZ, h.Orientation) * 0.14f, new Vector4(0.4f, 0.6f, 1f, 1f));
        }
    }

    private void DrawAxis(ImDrawListPtr drawList, Vector3 from, Vector3 delta, Vector4 color)
    {
        if (gameGui.WorldToScreen(from, out var a) && gameGui.WorldToScreen(from + delta, out var b))
            drawList.AddLine(a, b, ImGui.GetColorU32(color), 2f);
    }
}
