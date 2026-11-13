// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>
/// A battle's result across the upper middle of the screen: the result in large letters and, under
/// it in smaller ones, why. It stays until the battle is reset.
/// </summary>
public sealed class WarBanner
{
    private const float FadeInSeconds = 0.25f;
    private const float TitleScale = 3.2f;
    private const float ReasonScale = 1.45f;

    private readonly Stopwatch clock = new();
    private string title = string.Empty;
    private string reason = string.Empty;
    private Vector4 color;

    public void Show(string message, string why, Vector4 tint)
    {
        title = message;
        reason = why;
        color = tint;
        clock.Restart();
    }

    public void Hide()
    {
        clock.Reset();
        title = string.Empty;
        reason = string.Empty;
    }

    public void Draw()
    {
        if (title.Length == 0 || !clock.IsRunning)
            return;

        var alpha = Math.Clamp((float)clock.Elapsed.TotalSeconds / FadeInSeconds, 0f, 1f);
        var drawList = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var display = ImGui.GetIO().DisplaySize;
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.75f * alpha));

        var extent = ImGui.CalcTextSize(title) * TitleScale;
        var pos = new Vector2((display.X - extent.X) / 2f, display.Y * 0.22f);
        drawList.AddText(font, ImGui.GetFontSize() * TitleScale, pos + new Vector2(3f, 3f), shadow, title);
        drawList.AddText(font, ImGui.GetFontSize() * TitleScale, pos, ImGui.ColorConvertFloat4ToU32(color with { W = alpha }), title);

        if (reason.Length == 0)
            return;
        var small = ImGui.CalcTextSize(reason) * ReasonScale;
        var under = new Vector2((display.X - small.X) / 2f, pos.Y + extent.Y + 6f);
        drawList.AddText(font, ImGui.GetFontSize() * ReasonScale, under + new Vector2(2f, 2f), shadow, reason);
        drawList.AddText(font, ImGui.GetFontSize() * ReasonScale, under, ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.92f, 0.85f, alpha)), reason);
    }
}
