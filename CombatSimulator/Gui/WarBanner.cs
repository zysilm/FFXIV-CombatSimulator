// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>A battle's result, in large letters across the upper middle of the screen for a few seconds.</summary>
public sealed class WarBanner
{
    private const float ShowSeconds = 6f;
    private const float FadeSeconds = 1f;
    private const float TextScale = 3.2f;

    private readonly Stopwatch clock = new();
    private string text = string.Empty;
    private Vector4 color;

    public void Show(string message, Vector4 tint)
    {
        text = message;
        color = tint;
        clock.Restart();
    }

    public void Hide()
    {
        clock.Reset();
        text = string.Empty;
    }

    public void Draw()
    {
        if (text.Length == 0 || !clock.IsRunning)
            return;

        var t = (float)clock.Elapsed.TotalSeconds;
        if (t >= ShowSeconds)
        {
            Hide();
            return;
        }

        var alpha = Math.Clamp(MathF.Min(t / 0.25f, (ShowSeconds - t) / FadeSeconds), 0f, 1f);
        var drawList = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize() * TextScale;
        var extent = ImGui.CalcTextSize(text) * TextScale;
        var display = ImGui.GetIO().DisplaySize;
        var pos = new Vector2((display.X - extent.X) / 2f, display.Y * 0.22f);

        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.75f * alpha));
        drawList.AddText(font, size, pos + new Vector2(3f, 3f), shadow, text);
        drawList.AddText(font, size, pos, ImGui.ColorConvertFloat4ToU32(color with { W = alpha }), text);
    }
}
