using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Draws Action-Mode enemy attack telegraphs as perspective-correct ground circles
/// (same world→screen path as <see cref="CombatLinkOverlay"/>). An inner fill grows
/// with the windup and the colour ramps warn→imminent, so the danger circle reads as
/// "filling up" before the strike. The outline is the exact circle the hit test uses.
/// </summary>
public sealed class TelegraphOverlay
{
    private const int RingSegments = 48;
    private static readonly Vector3 WarnColor = new(1.00f, 0.55f, 0.10f);
    private static readonly Vector3 ImminentColor = new(1.00f, 0.18f, 0.12f);

    private readonly TelegraphSystem telegraphs;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public TelegraphOverlay(TelegraphSystem telegraphs, IGameGui gameGui, Configuration config)
    {
        this.telegraphs = telegraphs;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        var list = telegraphs.Active;
        if (list.Count == 0)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var t in list)
        {
            if (t.Resolved)
                continue;
            DrawCircle(drawList, t.AnchorPos, t.Radius, t.Progress);
        }
    }

    private void DrawCircle(ImDrawListPtr drawList, Vector3 center, float radius, float progress)
    {
        var color = Vector3.Lerp(WarnColor, ImminentColor, progress);
        float alpha = Math.Clamp(config.TelegraphAlpha, 0.05f, 1f);
        float thickness = MathF.Max(1f, config.TelegraphThickness);

        DrawRing(drawList, center, radius, color, alpha, thickness);
        // Inner fill ring expands as the windup nears the active frame.
        DrawRing(drawList, center, radius * progress, color, alpha * 0.5f, 2f);
    }

    private void DrawRing(ImDrawListPtr drawList, Vector3 center, float radius, Vector3 color, float alpha, float thickness)
    {
        if (radius <= 0.01f)
            return;

        Span<Vector2> pts = stackalloc Vector2[RingSegments + 1];
        Span<bool> ok = stackalloc bool[RingSegments + 1];
        for (int i = 0; i <= RingSegments; i++)
        {
            float a = i / (float)RingSegments * MathF.Tau;
            var world = new Vector3(center.X + MathF.Cos(a) * radius, center.Y, center.Z + MathF.Sin(a) * radius);
            ok[i] = gameGui.WorldToScreen(world, out pts[i]);
        }

        var col = Col(color, alpha);
        for (int i = 0; i < RingSegments; i++)
            if (ok[i] && ok[i + 1])
                drawList.AddLine(pts[i], pts[i + 1], col, thickness);
    }

    private static uint Col(Vector3 rgb, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));
}
