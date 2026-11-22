// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>
/// Duel camera controls, shown in the Camera page when the camera mode is Duel (docs/DUEL_CAMERA.md): one option of
/// its own, and the automatic death shot's three (death shots run in this mode, and only in it).
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
            "With nobody to fight the camera is the game's own. When you go down, the automatic death shot takes over.");
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Status: {DuelCameraStatus?.Invoke() ?? "off"}");

        var framing = Array.IndexOf(DuelCameraDirector.Framings, config.DuelCameraFraming);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Framing##duelcam", DuelCameraDirector.FramingLabels, framing, out var picked))
        {
            config.DuelCameraFraming = DuelCameraDirector.Framings[picked];
            config.Save();
        }
        HelpMarker("Tight: close, from the waist up. Standard: most of the body. Wide: further back, whole bodies.");

        ImGui.TextWrapped("When you go down in Duel mode the automatic death shot takes over:");
        DrawDuelDeathShotControls();
        ImGui.Unindent();
    }

    /// <summary>The automatic death shot's options. It runs in Duel mode only, so they are here only (the Directed
    /// camera has its own death framing and its own "Death shot" option).</summary>
    private void DrawDuelDeathShotControls()
    {
        var shot = Array.IndexOf(DeathShotProfiles.Choices, config.DuelDeathShot);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Duel death shot##duelcam", DeathShotProfiles.ChoiceLabels, shot, out var pickedShot))
        {
            config.DuelDeathShot = DeathShotProfiles.Choices[pickedShot];
            config.Save();
        }
        HelpMarker("Which way the camera looks from is yours: drag left or right to go round the body, as with the game's camera. " +
                   "It stays where you leave it. The shot sets what is framed, how close and how low; the wheel changes the size.\n" +
                   "Low close: hips to chest, close, from below.\n" +
                   "Low on the face: the head, close, low.\n" +
                   "Low full body: the whole body, from further back, low.\n" +
                   "Changing it while you are down eases the camera over to the new shot.");

        var grabbed = (int)config.DuelDeathShotGrabbed;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("When grabbed##duelcam", DeathShotProfiles.GrabbedLabels, grabbed, out var pickedGrabbed))
        {
            config.DuelDeathShotGrabbed = (DeathShotGrabbedFraming)pickedGrabbed;
            config.Save();
        }
        HelpMarker("While an enemy holds your body up: the camera frames you from the head down to mid-thigh, level with you or " +
                   "from slightly above, and comes back to the shot above once you are let go and lie still. " +
                   "Left and right stay yours here too.");

        var below = config.DuelDeathShotBelowGround;
        if (ImGui.Checkbox("Allow below ground##duelcam", ref below))
        {
            config.DuelDeathShotBelowGround = below;
            config.Save();
        }
        HelpMarker("The camera may go under the ground to look up at a body lying on it (the ground is not drawn from below). " +
                   "Off: the camera stays above the ground.");
    }
}
