using System;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Renders ragdoll capsules and joint limits as a 3D wireframe overlay.
/// Only draws enabled physics bones. Highlights the bone being edited in the UI.
/// </summary>
public class RagdollDebugOverlay
{
    private readonly RagdollController ragdollController;
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly IGameGui gameGui;

    public RagdollDebugOverlay(RagdollController ragdollController, MainWindow mainWindow,
        Configuration config, IGameGui gameGui)
    {
        this.ragdollController = ragdollController;
        this.mainWindow = mainWindow;
        this.config = config;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!config.RagdollDebugOverlay || !ragdollController.IsActive) return;

        var drawList = ImGui.GetForegroundDrawList();
        var capsules = ragdollController.GetDebugCapsules();
        var editingBone = mainWindow.EditingBoneName;

        foreach (var cap in capsules)
        {
            var isEditing = cap.Name == editingBone;

            // Colors: bright for editing bone, dim for others
            float alpha = isEditing ? 1.0f : 0.35f;
            float thickness = isEditing ? 2.5f : 1.0f;
            var colorCapsule = ImGui.GetColorU32(isEditing
                ? new Vector4(0.3f, 1f, 0.5f, alpha)
                : new Vector4(0.5f, 0.5f, 0.5f, alpha));
            var jointColor = ImGui.GetColorU32(
                cap.Joint == RagdollController.JointType.Hinge
                    ? new Vector4(1f, 0.6f, 0.2f, alpha)
                    : new Vector4(0.4f, 0.6f, 1f, alpha));
            var colorLabel = ImGui.GetColorU32(isEditing
                ? new Vector4(1f, 1f, 0.3f, 1f)
                : new Vector4(0.7f, 0.7f, 0.7f, 0.6f));

            // Capsule axes
            var yAxis = Vector3.Transform(Vector3.UnitY, cap.Orientation);
            var xAxis = Vector3.Transform(Vector3.UnitX, cap.Orientation);
            var zAxis = Vector3.Transform(Vector3.UnitZ, cap.Orientation);

            var top = cap.Position + yAxis * cap.HalfLength;
            var bottom = cap.Position - yAxis * cap.HalfLength;

            // End circles
            DrawCircle3D(drawList, top, xAxis, zAxis, cap.Radius, colorCapsule, thickness);
            DrawCircle3D(drawList, bottom, xAxis, zAxis, cap.Radius, colorCapsule, thickness);

            // Connecting rails
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI / 2;
                var offset = (xAxis * MathF.Cos(angle) + zAxis * MathF.Sin(angle)) * cap.Radius;
                if (gameGui.WorldToScreen(top + offset, out var s1) &&
                    gameGui.WorldToScreen(bottom + offset, out var s2))
                    drawList.AddLine(s1, s2, colorCapsule, thickness);
            }

            // Joint indicator
            DrawCircle3D(drawList, cap.Position, xAxis, zAxis, cap.Radius * 0.5f, jointColor, thickness + 0.5f);

            // Swing limit cone
            if (cap.SwingLimit > 0.01f && isEditing)
            {
                var coneRadius = MathF.Min(cap.HalfLength * MathF.Tan(cap.SwingLimit), 0.3f);
                DrawCircle3D(drawList, top + yAxis * 0.05f, xAxis, zAxis, coneRadius, jointColor, 1.5f);
                // Cone lines from center to cone edge
                for (int i = 0; i < 4; i++)
                {
                    float angle = i * MathF.PI / 2;
                    var edgePoint = top + yAxis * 0.05f + (xAxis * MathF.Cos(angle) + zAxis * MathF.Sin(angle)) * coneRadius;
                    if (gameGui.WorldToScreen(cap.Position, out var sc) &&
                        gameGui.WorldToScreen(edgePoint, out var se))
                        drawList.AddLine(sc, se, jointColor, 1f);
                }
            }

            // Label
            if (gameGui.WorldToScreen(cap.Position, out var labelPos))
            {
                var label = isEditing ? $">> {cap.Name} <<" : cap.Name;
                var textSize = ImGui.CalcTextSize(label);
                drawList.AddText(labelPos - textSize * 0.5f, colorLabel, label);
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
