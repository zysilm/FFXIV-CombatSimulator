// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Animation.SurfaceProfiles;
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
    private readonly DismembermentController dismemberment;
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;

    public RagdollDebugOverlay(RagdollController ragdollController, DismembermentController dismemberment,
        MainWindow mainWindow, Configuration config, IGameGui gameGui, IClientState clientState)
    {
        this.ragdollController = ragdollController;
        this.dismemberment = dismemberment;
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
        DrawCharacterSurfaces(drawList);
        if (!string.IsNullOrEmpty(ragdollController.MeshGeometryStatus))
            drawList.AddText(ImGui.GetMainViewport().WorkPos + new Vector2(12f, 12f),
                ImGui.GetColorU32(new Vector4(0.7f, 0.95f, 1f, 1f)), ragdollController.MeshGeometryStatus);

        foreach (var cap in capsules)
        {
            const float alpha = 0.8f;
            const float thickness = 1.5f;
            var colorCapsule = ImGui.GetColorU32(new Vector4(0.2f, 0.8f, 0.3f, alpha));
            var jointColor = ImGui.GetColorU32(
                cap.Joint == RagdollController.JointType.Hinge
                    ? new Vector4(1f, 0.5f, 0.1f, alpha)
                    : new Vector4(0.3f, 0.5f, 1f, alpha));
            var colorLabel = ImGui.GetColorU32(new Vector4(0.7f, 0.9f, 0.7f, 0.9f));

            // Capsule axes
            var yAxis = Vector3.Transform(Vector3.UnitY, cap.Orientation);
            var xAxis = Vector3.Transform(Vector3.UnitX, cap.Orientation);
            var zAxis = Vector3.Transform(Vector3.UnitZ, cap.Orientation);

            if (cap.ColliderShape == RagdollController.RagdollColliderShape.Mesh ||
                cap.ColliderShape == RagdollController.RagdollColliderShape.ProfileHull)
            {
                // The surface wireframe above is the contact geometry. This marker only
                // identifies the body's centre and anatomical frame, not another collider.
                DrawCircle3D(drawList, cap.Position, xAxis, zAxis,
                    MathF.Max(0.005f, cap.Radius * 0.35f), jointColor, thickness + 0.5f);
                if (gameGui.WorldToScreen(cap.Position, out var profileLabelPos))
                {
                    var textSize = ImGui.CalcTextSize(cap.Name);
                    drawList.AddText(profileLabelPos - textSize * 0.5f, colorLabel, cap.Name);
                }
                continue;
            }

            if (cap.ColliderShape == RagdollController.RagdollColliderShape.Box)
            {
                DrawBox3D(drawList, cap.Position, xAxis, yAxis, zAxis, cap.BoxHalfExtents, colorCapsule, thickness);
                DrawCircle3D(drawList, cap.Position, xAxis, zAxis, MathF.Min(cap.BoxHalfExtents.X, cap.BoxHalfExtents.Z), jointColor, thickness + 0.5f);
                if (gameGui.WorldToScreen(cap.Position, out var boxLabelPos))
                {
                    var textSize = ImGui.CalcTextSize(cap.Name);
                    drawList.AddText(boxLabelPos - textSize * 0.5f, colorLabel, cap.Name);
                }
                continue;
            }

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

            // Label
            if (gameGui.WorldToScreen(cap.Position, out var labelPos))
            {
                var textSize = ImGui.CalcTextSize(cap.Name);
                drawList.AddText(labelPos - textSize * 0.5f, colorLabel, cap.Name);
            }
        }

        DrawLimitStress(drawList);
    }

    private void DrawCharacterSurfaces(ImDrawListPtr drawList)
    {
        var color = ImGui.GetColorU32(new Vector4(0.15f, 0.9f, 1f, 0.82f));
        const float thickness = 1.25f;
        // Display sampling only: full triangle surfaces remain in collision/query geometry.
        // Limits bound projection/draw-list work even with multiple high-poly mod bodies.
        foreach (var surface in ragdollController.GetCharacterSurfaceDebugBones(CharacterSurfaceUsage.Physics, 128, 2048))
        {
            var vertices = surface.Vertices.Span;
            var indices = surface.Indices.Span;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                DrawEdge(drawList, a, b, color, thickness);
                DrawEdge(drawList, b, c, color, thickness);
                DrawEdge(drawList, c, a, color, thickness);
            }
        }
    }

    private readonly List<RagdollController.JointLimitStress> stressBuffer = new();

    /// <summary>Highlight joints pinned against their swing limits — the diagnostic for
    /// unrealistic "spread-eagle" poses (which joints froze at their range edge).</summary>
    private void DrawLimitStress(ImDrawListPtr drawList)
    {
        ragdollController.CollectJointLimitStress(stressBuffer);
        foreach (var s in stressBuffer)
        {
            if (s.Stress < 0.85f)
                continue;

            var t = Math.Clamp((s.Stress - 0.85f) / 0.15f, 0f, 1f);
            // Yellow approaching the limit → red pinned on it.
            var color = ImGui.GetColorU32(new Vector4(1f, 1f - t * 0.8f, 0.1f, 0.95f));
            if (!gameGui.WorldToScreen(s.WorldPosition, out var screen))
                continue;

            drawList.AddCircleFilled(screen, 4f + 3f * t, color);
            drawList.AddText(screen + new Vector2(8f, -8f), color, $"{s.BoneName} {s.Stress * 100f:F0}%");
        }
    }

    private void DrawBox3D(ImDrawListPtr drawList, Vector3 center, Vector3 xAxis, Vector3 yAxis, Vector3 zAxis,
        Vector3 halfExtents, uint color, float thickness)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        for (int sz = -1; sz <= 1; sz += 2)
            corners[idx++] = center +
                             xAxis * (halfExtents.X * sx) +
                             yAxis * (halfExtents.Y * sy) +
                             zAxis * (halfExtents.Z * sz);

        DrawEdge(drawList, corners[0], corners[1], color, thickness);
        DrawEdge(drawList, corners[0], corners[2], color, thickness);
        DrawEdge(drawList, corners[0], corners[4], color, thickness);
        DrawEdge(drawList, corners[3], corners[1], color, thickness);
        DrawEdge(drawList, corners[3], corners[2], color, thickness);
        DrawEdge(drawList, corners[3], corners[7], color, thickness);
        DrawEdge(drawList, corners[5], corners[1], color, thickness);
        DrawEdge(drawList, corners[5], corners[4], color, thickness);
        DrawEdge(drawList, corners[5], corners[7], color, thickness);
        DrawEdge(drawList, corners[6], corners[2], color, thickness);
        DrawEdge(drawList, corners[6], corners[4], color, thickness);
        DrawEdge(drawList, corners[6], corners[7], color, thickness);
    }

    private void DrawEdge(ImDrawListPtr drawList, Vector3 a, Vector3 b, uint color, float thickness)
    {
        if (gameGui.WorldToScreen(a, out var sa) &&
            gameGui.WorldToScreen(b, out var sb))
            drawList.AddLine(sa, sb, color, thickness);
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
