// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Camera;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>
/// Dynamic Camera controls, shown in the Camera page after Active Camera.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] DynCamDirectsLabels = { "Combat and death", "Combat only", "Death only" };
    private static readonly (bool Combat, bool Death)[] DynCamDirects = { (true, true), (true, false), (false, true) };
    private static readonly string[] DynCamBodySizeLabels = { "Close", "Medium", "Wide" };
    private static readonly float[] DynCamBodySizes = { 0.65f, 0.55f, 0.45f };
    private static readonly string[] DynCamEnemiesLabels = { "Off", "Some", "All" };
    private static readonly (bool FitVolume, float CrowdRelief)[] DynCamEnemies = { (false, 0f), (false, 0.5f), (true, 0.5f) };
    private static readonly string[] DynCamPositionLabels = { "Centre", "Auto (opposite the enemy)", "Left", "Right" };
    private static readonly (float Frac, int Side)[] DynCamPositions = { (0f, 0), (0.15f, 0), (0.15f, 1), (0.15f, 2) };
    private static readonly string[] DynCamResponseLabels = { "Smooth", "Normal", "Snappy" };
    private static readonly (float Follow, float Zoom)[] DynCamResponses = { (3f, 1.5f), (6f, 3f), (12f, 6f) };

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.005f;

    public void DrawDynamicCamSection()
    {
        if (!config.EnableDynamicCamera || config.EnableActiveCamera)
            return;
        if (!ImGui.CollapsingHeader("Directed Camera", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();
        ImGui.TextWrapped(
            "The character is framed off to one side during the fight, and when they go down an automatic death shot " +
            "frames the body. In combat you keep full control of the camera; during a death shot you can orbit left and " +
            "right and change the shot size with the wheel.");
        DrawDynamicCamStatus();

        var directs = Array.FindIndex(DynCamDirects, d => d.Combat == config.DynCamCombatFraming && d.Death == config.DynCamDeathFraming);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Directs##dyncam", DynCamDirectsLabels, directs, out var pickedDirects))
        {
            (config.DynCamCombatFraming, config.DynCamDeathFraming) = DynCamDirects[pickedDirects];
            config.Save();
        }
        HelpMarker("Combat: over-the-shoulder framing while you fight. Death: when you go down, an automatic shot frames your body " +
                   "and follows it as it falls and comes to rest.");

        if (config.DynCamCombatFraming)
        {
            var size = Array.FindIndex(DynCamBodySizes, s => Near(s, config.DynCamSubjectScreenShare));
            ImGui.SetNextItemWidth(CameraItemWidth());
            if (PresetCombo("Character size##dyncam", DynCamBodySizeLabels, size, out var pickedSize))
            {
                config.DynCamSubjectScreenShare = DynCamBodySizes[pickedSize];
                config.Save();
            }
            HelpMarker("How much of the screen height your character fills in combat. Scroll-wheel zoom still works and is remembered.");

            var enemies = Array.FindIndex(DynCamEnemies, e => e.FitVolume == config.DynCamEnemyVolumeFitting && Near(e.CrowdRelief, config.DynCamCrowdingRelief));
            ImGui.SetNextItemWidth(CameraItemWidth());
            if (PresetCombo("Keep enemies in frame##dyncam", DynCamEnemiesLabels, enemies, out var pickedEnemies))
            {
                (config.DynCamEnemyVolumeFitting, config.DynCamCrowdingRelief) = DynCamEnemies[pickedEnemies];
                config.Save();
            }
            HelpMarker("Some: pull back when several enemies spread wider than the frame. All: also keep every engaged enemy fully in view.");

            var position = Array.FindIndex(DynCamPositions, p => Near(p.Frac, config.DynCamShoulderScreenFrac) &&
                (p.Frac == 0f || p.Side == config.DynCamShoulderSide));
            ImGui.SetNextItemWidth(CameraItemWidth());
            if (PresetCombo("Character position##dyncam", DynCamPositionLabels, position, out var pickedPosition))
            {
                (config.DynCamShoulderScreenFrac, config.DynCamShoulderSide) = DynCamPositions[pickedPosition];
                config.Save();
            }
            HelpMarker("Where the character sits on screen. Auto keeps them on the far side from whatever they are fighting.");

            var response = Array.FindIndex(DynCamResponses, r => Near(r.Follow, config.DynCamPivotSmoothing) && Near(r.Zoom, config.DynCamDistanceSmoothing));
            ImGui.SetNextItemWidth(CameraItemWidth());
            if (PresetCombo("Responsiveness##dyncam", DynCamResponseLabels, response, out var pickedResponse))
            {
                (config.DynCamPivotSmoothing, config.DynCamDistanceSmoothing) = DynCamResponses[pickedResponse];
                config.Save();
            }
            HelpMarker("How quickly the camera follows the body and settles its distance.");
        }

        if (config.DynCamDeathFraming)
        {
            var shot = Array.IndexOf(DeathShotDirector.Profiles, config.DeathShotProfile);
            ImGui.SetNextItemWidth(CameraItemWidth());
            if (PresetCombo("Death shot##dyncam", DeathShotDirector.ProfileLabels, shot, out var pickedShot))
            {
                config.DeathShotProfile = DeathShotDirector.Profiles[pickedShot];
                config.Save();
            }
            HelpMarker("Auto: picks the shot by how the body lies.\n" +
                       "Low three-quarter: hips to chest, close, from the front side and from below.\n" +
                       "Face: close-up of the head.\n" +
                       "Along the body: the whole body from beyond the feet, low.\n" +
                       "Overhead: the whole body from above.\n" +
                       "Ground profile: the whole body from the side, at ground level, looking up.\n" +
                       "Changing it while you are down switches the shot at once.");

            var below = config.DeathShotBelowGround;
            if (ImGui.Checkbox("Allow below ground##dyncam", ref below))
            {
                config.DeathShotBelowGround = below;
                config.Save();
            }
            HelpMarker("Low three-quarter and Face may put the camera under the ground, looking up at a body lying on it " +
                       "(the ground is not drawn from below). Off: the camera stays above the ground.");
        }

        if (ImGui.Button("Reset to Defaults##dyncam"))
            config.ResetDynamicCameraDefaults();
        ImGui.Unindent();
    }

    private void DrawDynamicCamStatus()
    {
        var phase = dynamicCameraController.CurrentPhase;
        var color = phase switch
        {
            DynamicCameraController.Phase.Combat => new Vector4(0.4f, 1f, 0.5f, 1f),
            DynamicCameraController.Phase.Death => new Vector4(1f, 0.55f, 0.55f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
        };
        ImGui.TextColored(color, $"Status: {dynamicCameraController.StatusText}");
    }
}
