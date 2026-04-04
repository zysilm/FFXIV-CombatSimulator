using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Renders ragdoll capsules and joint limits as a 3D wireframe overlay.
/// Works both when ragdoll is active (reads physics bodies) and inactive (reads skeleton pose).
/// Highlights the bone being edited in the UI.
/// </summary>
public class RagdollDebugOverlay
{
    private readonly RagdollController ragdollController;
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;

    public RagdollDebugOverlay(RagdollController ragdollController, MainWindow mainWindow,
        Configuration config, IGameGui gameGui, IClientState clientState)
    {
        this.ragdollController = ragdollController;
        this.mainWindow = mainWindow;
        this.config = config;
        this.gameGui = gameGui;
        this.clientState = clientState;
    }

    public void Draw()
    {
        if (!config.RagdollDebugOverlay || !ragdollController.IsActive) return;

        var capsules = ragdollController.GetDebugCapsules();
        if (capsules.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();
        var editingBone = mainWindow.EditingBoneName;

        foreach (var cap in capsules)
        {
            var isEditing = editingBone != null && cap.Name == editingBone;

            // Colors: bright highlighted for editing, solid green for others
            float alpha = isEditing ? 1.0f : 0.8f;
            float thickness = isEditing ? 3.0f : 1.5f;
            var colorCapsule = ImGui.GetColorU32(isEditing
                ? new Vector4(1f, 1f, 0.2f, alpha)
                : new Vector4(0.2f, 0.8f, 0.3f, alpha));
            var jointColor = ImGui.GetColorU32(
                cap.Joint == RagdollController.JointType.Hinge
                    ? new Vector4(1f, 0.5f, 0.1f, alpha)
                    : new Vector4(0.3f, 0.5f, 1f, alpha));
            var colorLabel = ImGui.GetColorU32(isEditing
                ? new Vector4(1f, 1f, 0.2f, 1f)
                : new Vector4(0.7f, 0.9f, 0.7f, 0.9f));

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

            // Swing limit cone (editing bone only)
            if (cap.SwingLimit > 0.01f && isEditing)
            {
                var coneRadius = MathF.Min(cap.HalfLength * MathF.Tan(cap.SwingLimit), 0.3f);
                DrawCircle3D(drawList, top + yAxis * 0.05f, xAxis, zAxis, coneRadius, jointColor, 1.5f);
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

        // Joint limit visualization — only when actively dragging a swing/twist slider
        var editParam = mainWindow.EditingParameter;
        if (editingBone != null && editParam != MainWindow.EditParam.None)
        {
            var jv = ragdollController.GetDebugJointVis(editingBone);
            if (jv.Valid)
                DrawJointLimits(drawList, jv, editParam);
        }
    }

    private void DrawJointLimits(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        MainWindow.EditParam editParam)
    {
        var colorLimit = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.3f, 0.9f));
        var colorFill = ImGui.GetColorU32(new Vector4(1f, 0.5f, 0.2f, 0.3f));
        var colorChild = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.5f, 1f));
        var colorAxis = ImGui.GetColorU32(new Vector4(1f, 1f, 0.3f, 0.8f));

        if (editParam == MainWindow.EditParam.Swing)
        {
            if (jv.Joint == RagdollController.JointType.Ball)
                DrawSwingCone(drawList, jv, colorLimit, colorChild, colorAxis);
            else
                DrawSwingArc(drawList, jv, colorLimit, colorChild, colorAxis);
        }
        else if (editParam == MainWindow.EditParam.TwistMin || editParam == MainWindow.EditParam.TwistMax)
        {
            DrawTwistArc(drawList, jv, colorLimit, colorChild, colorAxis);
        }
    }

    /// <summary>Draw a cone showing ball joint swing limit range.</summary>
    private void DrawSwingCone(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var coneLength = 0.15f;
        var coneRadius = coneLength * MathF.Tan(jv.SwingLimit);
        coneRadius = MathF.Min(coneRadius, 0.4f);
        var coneTip = jv.JointPosition;
        var coneBase = coneTip + jv.ParentAxis * coneLength;

        // Cone edge circle
        DrawCircle3D(drawList, coneBase, jv.ParentRight, jv.ParentForward, coneRadius, colorLimit, 2f, 24);

        // Cone lines from tip to edge (8 lines)
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4;
            var edgePoint = coneBase + (jv.ParentRight * MathF.Cos(angle) + jv.ParentForward * MathF.Sin(angle)) * coneRadius;
            if (gameGui.WorldToScreen(coneTip, out var st) && gameGui.WorldToScreen(edgePoint, out var se))
                drawList.AddLine(st, se, colorLimit, 1.5f);
        }

        // Parent axis line (cone center)
        var axisEnd = coneTip + jv.ParentAxis * (coneLength * 1.3f);
        if (gameGui.WorldToScreen(coneTip, out var sa) && gameGui.WorldToScreen(axisEnd, out var sb))
            drawList.AddLine(sa, sb, colorAxis, 1f);

        // Child direction line (should be inside cone)
        var childEnd = coneTip + jv.ChildAxis * coneLength;
        if (gameGui.WorldToScreen(coneTip, out var sc) && gameGui.WorldToScreen(childEnd, out var sd))
            drawList.AddLine(sc, sd, colorChild, 2.5f);
    }

    /// <summary>Draw an arc showing hinge joint swing limit range.</summary>
    private void DrawSwingArc(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var arcRadius = 0.12f;
        var center = jv.JointPosition;

        // Arc from 0 to SwingLimit in the bending plane
        DrawArc3D(drawList, center, jv.ParentRight, jv.ParentAxis,
            arcRadius, -jv.SwingLimit, jv.SwingLimit, colorLimit, 2f, 20);

        // Limit lines
        for (int sign = -1; sign <= 1; sign += 2)
        {
            float angle = sign * jv.SwingLimit;
            var endPoint = center + (jv.ParentRight * MathF.Cos(angle) + jv.ParentAxis * MathF.Sin(angle)) * arcRadius;
            if (gameGui.WorldToScreen(center, out var s1) && gameGui.WorldToScreen(endPoint, out var s2))
                drawList.AddLine(s1, s2, colorLimit, 1.5f);
        }

        // Child direction
        var childEnd = center + jv.ChildAxis * arcRadius;
        if (gameGui.WorldToScreen(center, out var sc) && gameGui.WorldToScreen(childEnd, out var sd))
            drawList.AddLine(sc, sd, colorChild, 2.5f);
    }

    /// <summary>Draw an arc showing twist rotation limits around the segment axis.</summary>
    private void DrawTwistArc(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var arcRadius = 0.1f;
        var center = jv.JointPosition;

        // Build perpendicular axes to the child segment direction (twist axis)
        var up = MathF.Abs(Vector3.Dot(jv.ChildAxis, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var twistRight = Vector3.Normalize(Vector3.Cross(jv.ChildAxis, up));
        var twistForward = Vector3.Normalize(Vector3.Cross(twistRight, jv.ChildAxis));

        // Draw arc from TwistMin to TwistMax
        DrawArc3D(drawList, center, twistRight, twistForward,
            arcRadius, jv.TwistMinAngle, jv.TwistMaxAngle, colorLimit, 2f, 20);

        // Min/max limit lines
        var minPoint = center + (twistRight * MathF.Cos(jv.TwistMinAngle) + twistForward * MathF.Sin(jv.TwistMinAngle)) * arcRadius;
        var maxPoint = center + (twistRight * MathF.Cos(jv.TwistMaxAngle) + twistForward * MathF.Sin(jv.TwistMaxAngle)) * arcRadius;

        if (gameGui.WorldToScreen(center, out var sc1) && gameGui.WorldToScreen(minPoint, out var sm1))
            drawList.AddLine(sc1, sm1, colorLimit, 1.5f);
        if (gameGui.WorldToScreen(center, out var sc2) && gameGui.WorldToScreen(maxPoint, out var sm2))
            drawList.AddLine(sc2, sm2, colorLimit, 1.5f);

        // Twist axis line
        var axisEnd = center + jv.ChildAxis * (arcRadius * 1.5f);
        if (gameGui.WorldToScreen(center, out var sa) && gameGui.WorldToScreen(axisEnd, out var sb))
            drawList.AddLine(sa, sb, colorAxis, 1f);
    }

    private void DrawArc3D(ImDrawListPtr drawList, Vector3 center, Vector3 right, Vector3 forward,
        float radius, float fromAngle, float toAngle, uint color, float thickness, int segments)
    {
        Vector2 prevScreen = default;
        bool prevValid = false;
        float step = (toAngle - fromAngle) / segments;

        for (int i = 0; i <= segments; i++)
        {
            float angle = fromAngle + i * step;
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
