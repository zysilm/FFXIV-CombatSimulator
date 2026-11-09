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
    private static readonly string[] DynCamDeathShotLabels = { "Looking up", "Default", "Looking down" };
    private static readonly (float Band, float Angle)[] DynCamDeathShots = { (0.3f, -0.25f), (-0.45f, 0.45f), (-0.6f, 0.8f) };

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.005f;

    public void DrawDynamicCamSection()
    {
        if (!config.EnableDynamicCamera || config.EnableActiveCamera)
            return;
        if (!ImGui.CollapsingHeader("Directed Camera", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();
        ImGui.TextWrapped(
            "The character is framed off to one side during the fight, and when they go down the shot pulls back " +
            "to hold both the body and whoever killed them. You keep full control of the camera throughout.");
        DrawDynamicCamStatus();

        var directs = Array.FindIndex(DynCamDirects, d => d.Combat == config.DynCamCombatFraming && d.Death == config.DynCamDeathFraming);
        ImGui.SetNextItemWidth(200);
        if (PresetCombo("Directs##dyncam", DynCamDirectsLabels, directs, out var pickedDirects))
        {
            (config.DynCamCombatFraming, config.DynCamDeathFraming) = DynCamDirects[pickedDirects];
            config.Save();
        }
        HelpMarker("Combat: over-the-shoulder framing while you fight. Death: when you go down, the camera drops beside your body " +
                   "and backs away only as far as it must to also hold the enemy that killed you.");

        if (config.DynCamCombatFraming)
        {
            var size = Array.FindIndex(DynCamBodySizes, s => Near(s, config.DynCamSubjectScreenShare));
            ImGui.SetNextItemWidth(200);
            if (PresetCombo("Character size##dyncam", DynCamBodySizeLabels, size, out var pickedSize))
            {
                config.DynCamSubjectScreenShare = DynCamBodySizes[pickedSize];
                config.Save();
            }
            HelpMarker("How much of the screen height your character fills in combat. Scroll-wheel zoom still works and is remembered.");

            var enemies = Array.FindIndex(DynCamEnemies, e => e.FitVolume == config.DynCamEnemyVolumeFitting && Near(e.CrowdRelief, config.DynCamCrowdingRelief));
            ImGui.SetNextItemWidth(200);
            if (PresetCombo("Keep enemies in frame##dyncam", DynCamEnemiesLabels, enemies, out var pickedEnemies))
            {
                (config.DynCamEnemyVolumeFitting, config.DynCamCrowdingRelief) = DynCamEnemies[pickedEnemies];
                config.Save();
            }
            HelpMarker("Some: pull back when several enemies spread wider than the frame. All: also keep every engaged enemy fully in view.");

            var position = Array.FindIndex(DynCamPositions, p => Near(p.Frac, config.DynCamShoulderScreenFrac) &&
                (p.Frac == 0f || p.Side == config.DynCamShoulderSide));
            ImGui.SetNextItemWidth(200);
            if (PresetCombo("Character position##dyncam", DynCamPositionLabels, position, out var pickedPosition))
            {
                (config.DynCamShoulderScreenFrac, config.DynCamShoulderSide) = DynCamPositions[pickedPosition];
                config.Save();
            }
            HelpMarker("Where the character sits on screen. Auto keeps them on the far side from whatever they are fighting.");

            var response = Array.FindIndex(DynCamResponses, r => Near(r.Follow, config.DynCamPivotSmoothing) && Near(r.Zoom, config.DynCamDistanceSmoothing));
            ImGui.SetNextItemWidth(200);
            if (PresetCombo("Responsiveness##dyncam", DynCamResponseLabels, response, out var pickedResponse))
            {
                (config.DynCamPivotSmoothing, config.DynCamDistanceSmoothing) = DynCamResponses[pickedResponse];
                config.Save();
            }
            HelpMarker("How quickly the camera follows the body and settles its distance.");
        }

        if (config.DynCamDeathFraming)
        {
            var body = config.DynCamDeathBodyVisibility;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Body coverage##dyncam", ref body, 0.25f, 2f, "%.2f"))
            {
                config.DynCamDeathBodyVisibility = body;
                config.Save();
            }
            HelpMarker("How much of your body stays in the death shot, counted from the head: 1.00 head to feet, 0.50 head to waist, " +
                       "0.25 head and chest. Above 1.00 adds space around the body. The scroll wheel drives this live during the shot.");

            var shot = Array.FindIndex(DynCamDeathShots, s => Near(s.Band, config.DynCamDeathBodyBand) && Near(s.Angle, config.DynCamDeathAngle));
            ImGui.SetNextItemWidth(200);
            if (PresetCombo("Death shot##dyncam", DynCamDeathShotLabels, shot, out var pickedShot))
            {
                (config.DynCamDeathBodyBand, config.DynCamDeathAngle) = DynCamDeathShots[pickedShot];
                config.Save();
            }
            HelpMarker("Looking up: flat on the ground, looking up past your body at the killer. " +
                       "Looking down: raised up, showing more of the ground around the body.");
        }

        var toolbar = config.ShowDynamicCamToolbar;
        if (ImGui.Checkbox("Show toolbar##dyncam", ref toolbar))
        {
            config.ShowDynamicCamToolbar = toolbar;
            config.Save();
        }
        HelpMarker("A small floating bar to adjust body coverage, and whether the killer is framed, without this window open.");

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
            DynamicCameraController.Phase.DeathTranslate => new Vector4(1f, 0.75f, 0.35f, 1f),
            DynamicCameraController.Phase.DeathHold => new Vector4(1f, 0.55f, 0.55f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
        };
        ImGui.TextColored(color, $"Status: {dynamicCameraController.StatusText}");
    }

    /// <summary>Floating toolbar for tuning the framing in-game without the settings window in the way.</summary>
    public void DrawDynamicCamToolbar()
    {
        using var toolbarStyle = PushCompactToolbarStyle();
        // Closable, so the toolbar can be dismissed while its camera mode is not selected.
        var open = config.ShowDynamicCamToolbar;
        var visible = ImGui.Begin("Dynamic Cam", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize);
        if (!open)
        {
            config.ShowDynamicCamToolbar = false;
            config.Save();
        }
        if (!visible)
        {
            ImGui.End();
            return;
        }
        RestoreToolbarFontScale();
        var ignoreKiller = config.DynCamDeathIgnoreKiller;
        if (ImGui.Checkbox("##dcIgnoreKiller", ref ignoreKiller))
        {
            config.DynCamDeathIgnoreKiller = ignoreKiller;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Frame only your body in the death shot, not the killer.");

        ImGui.SameLine();
        var body = config.DynCamDeathBodyVisibility;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderFloat("##dcBody", ref body, 0.25f, 2f, "%.2f"))
        {
            config.DynCamDeathBodyVisibility = body;
            config.Save();
        }

        ImGui.End();
    }
}
