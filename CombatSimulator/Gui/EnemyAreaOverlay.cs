// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Simulation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Draws every live enemy attack area (<see cref="EnemyAreaMarkers"/>) in its real shape —
/// circle, cone, rectangle or donut — projected onto the ground the same way
/// <see cref="TelegraphOverlay"/> projects its circles. A translucent fill grows from the origin
/// with the cast/windup progress over the full outline, so the zone reads as "filling up". The
/// outline is the exact area the hit test uses. Markers whose native omen VFX is showing are
/// skipped unless the overlay is asked to draw on top of it.
/// </summary>
public sealed class EnemyAreaOverlay
{
    private const int ArcSegments = 40;
    private static readonly Vector3 WarnColor = new(1.00f, 0.55f, 0.10f);
    private static readonly Vector3 ImminentColor = new(1.00f, 0.18f, 0.12f);

    private readonly EnemyAreaMarkers markers;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    /// <summary>True when a native omen is rendering for this marker (set by the omen layer).</summary>
    public Func<EnemyAreaMarker, bool>? HasNativeOmen { private get; set; }

    public EnemyAreaOverlay(EnemyAreaMarkers markers, IGameGui gameGui, Configuration config)
    {
        this.markers = markers;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        var list = markers.Active;
        if (list.Count == 0)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var m in list)
        {
            if (HasNativeOmen?.Invoke(m) == true && !config.EnemyAreaOverlayWithOmen)
                continue;
            // Single-target strike circles are the legacy telegraph, still governed by its own
            // "Show telegraph circles" switch; only real areas are always drawn.
            if (!m.Shape.IsArea && !config.ShowTelegraphs)
                continue;
            DrawMarker(drawList, m);
        }
    }

    private void DrawMarker(ImDrawListPtr drawList, EnemyAreaMarker m)
    {
        var progress = m.Progress;
        var color = Vector3.Lerp(WarnColor, ImminentColor, progress);
        var alpha = Math.Clamp(config.TelegraphAlpha, 0.05f, 1f);
        var thickness = MathF.Max(1f, config.TelegraphThickness);
        var outline = Col(color, alpha);
        var fill = Col(color, alpha * 0.35f);
        var s = m.Shape;

        switch (s.Kind)
        {
            case TelegraphKind.Circle:
                FillSector(drawList, s.Origin, 0f, s.Radius * progress, 0f, MathF.PI, fill);
                Arc(drawList, s.Origin, s.Radius, 0f, MathF.PI, outline, thickness, closed: false);
                break;
            case TelegraphKind.Donut:
            {
                var inner = s.InnerRadius;
                FillSector(drawList, s.Origin, inner, inner + (s.Radius - inner) * progress, 0f, MathF.PI, fill);
                Arc(drawList, s.Origin, s.Radius, 0f, MathF.PI, outline, thickness, closed: false);
                Arc(drawList, s.Origin, inner, 0f, MathF.PI, outline, thickness, closed: false);
                break;
            }
            case TelegraphKind.Cone:
                FillSector(drawList, s.Origin, 0f, s.Radius * progress, s.Rotation, s.HalfAngleRad, fill);
                Arc(drawList, s.Origin, s.Radius, s.Rotation, s.HalfAngleRad, outline, thickness, closed: s.HalfAngleRad < MathF.PI - 0.01f);
                break;
            case TelegraphKind.Rect:
                DrawRect(drawList, s, progress, fill, outline, thickness);
                break;
        }
    }

    // Ground point at polar (angle from the shape's forward, radius) around an origin, in the
    // game's yaw convention (forward = (sin, cos)).
    private static Vector3 Polar(Vector3 origin, float yaw, float r)
        => new(origin.X + MathF.Sin(yaw) * r, origin.Y, origin.Z + MathF.Cos(yaw) * r);

    private void FillSector(ImDrawListPtr dl, Vector3 o, float r0, float r1, float centreYaw, float halfAngle, uint col)
    {
        if (r1 <= r0 + 0.01f)
            return;
        var segs = Math.Max(4, (int)(ArcSegments * halfAngle / MathF.PI));
        for (var i = 0; i < segs; i++)
        {
            var a0 = centreYaw - halfAngle + 2f * halfAngle * i / segs;
            var a1 = centreYaw - halfAngle + 2f * halfAngle * (i + 1) / segs;
            if (!gameGui.WorldToScreen(Polar(o, a0, r1), out var p0) ||
                !gameGui.WorldToScreen(Polar(o, a1, r1), out var p1))
                continue;
            if (r0 <= 0.01f)
            {
                if (gameGui.WorldToScreen(o, out var c))
                    dl.AddTriangleFilled(c, p0, p1, col);
                continue;
            }
            if (gameGui.WorldToScreen(Polar(o, a0, r0), out var q0) &&
                gameGui.WorldToScreen(Polar(o, a1, r0), out var q1))
                dl.AddQuadFilled(q0, p0, p1, q1, col);
        }
    }

    private void Arc(ImDrawListPtr dl, Vector3 o, float r, float centreYaw, float halfAngle, uint col, float thickness, bool closed)
    {
        if (r <= 0.01f)
            return;
        var segs = Math.Max(4, (int)(ArcSegments * halfAngle / MathF.PI));
        var hasPrev = false;
        var prev = Vector2.Zero;
        for (var i = 0; i <= segs; i++)
        {
            var a = centreYaw - halfAngle + 2f * halfAngle * i / segs;
            var ok = gameGui.WorldToScreen(Polar(o, a, r), out var p);
            if (ok && hasPrev)
                dl.AddLine(prev, p, col, thickness);
            hasPrev = ok;
            prev = p;
        }
        if (!closed || !gameGui.WorldToScreen(o, out var c))
            return;
        if (gameGui.WorldToScreen(Polar(o, centreYaw - halfAngle, r), out var e0))
            dl.AddLine(c, e0, col, thickness);
        if (gameGui.WorldToScreen(Polar(o, centreYaw + halfAngle, r), out var e1))
            dl.AddLine(c, e1, col, thickness);
    }

    private void DrawRect(ImDrawListPtr dl, TelegraphShape s, float progress, uint fill, uint outline, float thickness)
    {
        var f = s.Forward2D;
        var right = new Vector2(f.Y, -f.X);
        var hw = s.HalfWidth;
        Vector3 At(float along, float side)
            => new(s.Origin.X + f.X * along + right.X * side, s.Origin.Y, s.Origin.Z + f.Y * along + right.Y * side);

        if (gameGui.WorldToScreen(At(0, -hw), out var a) && gameGui.WorldToScreen(At(0, hw), out var b))
        {
            var len = s.Radius * progress;
            if (len > 0.01f && gameGui.WorldToScreen(At(len, hw), out var c) && gameGui.WorldToScreen(At(len, -hw), out var d))
                dl.AddQuadFilled(a, b, c, d, fill);
        }

        var corners = new[] { At(0, -hw), At(0, hw), At(s.Radius, hw), At(s.Radius, -hw) };
        for (var i = 0; i < 4; i++)
        {
            if (gameGui.WorldToScreen(corners[i], out var p0) && gameGui.WorldToScreen(corners[(i + 1) % 4], out var p1))
                dl.AddLine(p0, p1, outline, thickness);
        }
    }

    private static uint Col(Vector3 rgb, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));
}
