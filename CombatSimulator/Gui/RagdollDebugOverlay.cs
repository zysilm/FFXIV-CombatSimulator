using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Renders ragdoll capsules and joint limits as a 3D wireframe overlay.
/// Uses ImGui foreground draw list + WorldToScreen projection.
/// </summary>
public class RagdollDebugOverlay
{
    private readonly RagdollController ragdollController;
    private readonly Configuration config;
    private readonly IGameGui gameGui;

    public RagdollDebugOverlay(RagdollController ragdollController, Configuration config, IGameGui gameGui)
    {
        this.ragdollController = ragdollController;
        this.config = config;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!config.RagdollDebugOverlay || !ragdollController.IsActive) return;

        var drawList = ImGui.GetForegroundDrawList();
        var capsules = ragdollController.GetDebugCapsules();

        // Colors
        var colorCapsule = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.4f, 0.7f));
        var colorHinge = ImGui.GetColorU32(new Vector4(1f, 0.6f, 0.2f, 0.7f));
        var colorBall = ImGui.GetColorU32(new Vector4(0.4f, 0.6f, 1f, 0.7f));
        var colorLabel = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f));

        foreach (var cap in capsules)
        {
            // Capsule axes in world space
            var yAxis = Vector3.Transform(Vector3.UnitY, cap.Orientation);
            var xAxis = Vector3.Transform(Vector3.UnitX, cap.Orientation);
            var zAxis = Vector3.Transform(Vector3.UnitZ, cap.Orientation);

            var top = cap.Position + yAxis * cap.HalfLength;
            var bottom = cap.Position - yAxis * cap.HalfLength;

            // End circles
            DrawCircle3D(drawList, top, xAxis, zAxis, cap.Radius, colorCapsule);
            DrawCircle3D(drawList, bottom, xAxis, zAxis, cap.Radius, colorCapsule);

            // Connecting rails (4 lines)
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI / 2;
                var offset = (xAxis * MathF.Cos(angle) + zAxis * MathF.Sin(angle)) * cap.Radius;
                if (gameGui.WorldToScreen(top + offset, out var s1) &&
                    gameGui.WorldToScreen(bottom + offset, out var s2))
                    drawList.AddLine(s1, s2, colorCapsule, 1.5f);
            }

            // Joint type indicator at center
            var jointColor = cap.Joint == RagdollController.JointType.Hinge ? colorHinge : colorBall;
            DrawCircle3D(drawList, cap.Position, xAxis, zAxis, cap.Radius * 0.5f, jointColor, 2f);

            // Swing limit cone (circle at top showing range)
            if (cap.SwingLimit > 0.01f)
            {
                var coneRadius = MathF.Min(cap.HalfLength * MathF.Tan(cap.SwingLimit), 0.3f);
                DrawCircle3D(drawList, top + yAxis * 0.05f, xAxis, zAxis, coneRadius, jointColor, 1f);
            }

            // Label
            if (gameGui.WorldToScreen(cap.Position, out var labelPos))
            {
                var textSize = ImGui.CalcTextSize(cap.Name);
                drawList.AddText(labelPos - textSize * 0.5f, colorLabel, cap.Name);
            }
        }
    }

    private void DrawCircle3D(ImDrawListPtr drawList, Vector3 center, Vector3 right, Vector3 forward,
        float radius, uint color, float thickness = 1.5f, int segments = 12)
    {
        Vector2 prevScreen = default;
        bool prevValid = false;

        for (int i = 0; i <= segments; i++)
        {
            float angle = i * 2f * MathF.PI / segments;
            var worldPos = center + (right * MathF.Cos(angle) + forward * MathF.Sin(angle)) * radius;

            if (gameGui.WorldToScreen(worldPos, out var screenPos))
            {
                if (prevValid)
                    drawList.AddLine(prevScreen, screenPos, color, thickness);
                prevScreen = screenPos;
                prevValid = true;
            }
            else
            {
                prevValid = false;
            }
        }
    }
}
