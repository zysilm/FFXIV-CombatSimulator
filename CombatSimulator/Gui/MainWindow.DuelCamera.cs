// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Camera;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>
/// Duel camera controls, shown in the Camera page when the camera mode is Duel (docs/DUEL_CAMERA.md): one option.
/// </summary>
public partial class MainWindow
{
    /// <summary>The duel camera's one-line status (set by the composition root).</summary>
    public Func<string>? DuelCameraStatus { get; set; }

    private void DrawDuelCamSection()
    {
        if (!config.EnableDuelCamera || config.EnableActiveCamera || config.EnableDynamicCamera)
            return;
        if (!ImGui.CollapsingHeader("Duel Camera", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();
        ImGui.TextWrapped(
            "While a simulation runs the camera frames you and one opponent together, from behind you and to one side, " +
            "closer when you are close and wider when you separate, and pushes in for a moment on heavy hits and guards. " +
            "Drag to look round (it comes back on its own) or up and down a little; the wheel changes the size. " +
            "When you go down, the automatic death shot takes over.");
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Status: {DuelCameraStatus?.Invoke() ?? "off"}");

        var framing = Array.IndexOf(DuelCameraDirector.Framings, config.DuelCameraFraming);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Framing##duelcam", DuelCameraDirector.FramingLabels, framing, out var picked))
        {
            config.DuelCameraFraming = DuelCameraDirector.Framings[picked];
            config.Save();
        }
        HelpMarker("Tight: close, from the waist up. Standard: most of the body. Wide: further back, whole bodies.");
        ImGui.Unindent();
    }
}
