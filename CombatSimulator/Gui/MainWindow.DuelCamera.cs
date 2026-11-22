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
        HelpMarker("All of them are close, low and slow, on a narrow lens.\n" +
                   "Auto: picks one by how the body lies and where the camera already is.\n" +
                   "Low three-quarter: hips to chest, from the front side and from below.\n" +
                   "Low on the face: the face, from in front of it.\n" +
                   "Hips up to the face: from by the hips, up along the torso to the face.\n" +
                   "Low from the feet: hips to chest, from the feet end.\n" +
                   "Low from behind: hips to chest, from the back side.\n" +
                   "Low full body: the whole body, lying diagonally across the picture.\n" +
                   "Changing it while you are down moves the camera over to the new shot.");

        var side = (int)config.DuelDeathShotSide;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Side preference##duelcam", DeathShotProfiles.SideLabels, side, out var pickedSide))
        {
            config.DuelDeathShotSide = (DeathShotSide)pickedSide;
            config.Save();
        }
        HelpMarker("Most shots can be taken from either side of the body. Which one:\n" +
                   "Nearest: the side nearest to where the camera already is.\n" +
                   "Face side: the side the face is turned to, so the face is seen.\n" +
                   "Behind the head: the side the back of the head is on.\n" +
                   "Body's left / Body's right: always that side of the body.\n" +
                   "With the face straight up or down, or that side blocked, the camera takes what it can.");

        var grabbed = (int)config.DuelDeathShotGrabbed;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("When grabbed##duelcam", DeathShotProfiles.GrabbedLabels, grabbed, out var pickedGrabbed))
        {
            config.DuelDeathShotGrabbed = (DeathShotGrabbedFraming)pickedGrabbed;
            config.Save();
        }
        HelpMarker("While an enemy holds your body up: the camera goes to your upper body, level with it or from slightly above, " +
                   "and comes back to the shot above once you are let go and lie still.");

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
