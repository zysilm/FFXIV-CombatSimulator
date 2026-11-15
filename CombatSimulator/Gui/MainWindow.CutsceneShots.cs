// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Camera;
using CombatSimulator.Camera.Cutscene;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>Camera tab → "Cutscene Shots": pick one of the curated follow shots from the game's own cutscenes for
/// victory and for death (docs/CUTSCENE_SHOTS.md).</summary>
public partial class MainWindow
{
    /// <summary>The cutscene shot player; set by the composition root after the window is built.</summary>
    public CutsceneShotDirector? CutsceneShots { get; set; }

    private string cutsceneShotSelected = "";
    private string cutsceneShotFilter = "";

    private void DrawCutsceneShotsSection()
    {
        var shots = CutsceneShots;
        if (shots == null || !ImGui.CollapsingHeader("Cutscene Shots"))
            return;

        ImGui.Indent();
        var catalog = shots.Catalog;
        var selected = catalog.Find(cutsceneShotSelected);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (ImGui.BeginCombo("##cutsceneShot", selected?.Name ?? "None", ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##cutsceneShotFilter", "search", ref cutsceneShotFilter, 64);
            if (ImGui.Selectable("None##cutsceneShotNone", selected == null))
                cutsceneShotSelected = "";
            foreach (var entry in catalog.Entries)
            {
                if (!entry.Matches(cutsceneShotFilter))
                    continue;
                if (ImGui.Selectable($"{entry.Name}##{entry.Id}", entry == selected))
                    cutsceneShotSelected = entry.Id;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{entry.Duration:0.0} s · {string.Join(", ", entry.Tags)}");
            }
            ImGui.EndCombo();
        }

        if (ImGui.Button("Use for Victory##cutsceneShot"))
        {
            config.CutsceneVictoryShot = selected?.Id ?? "";
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Use for Death##cutsceneShot"))
        {
            config.CutsceneDeathShot = selected?.Id ?? "";
            config.Save();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(selected == null);
        if (ImGui.Button("Play Now##cutsceneShot") && selected != null)
            shots.PlayNow(selected.Id);
        ImGui.EndDisabled();

        DrawCutsceneShotChoice("Victory", config.CutsceneVictoryShot, catalog, () => config.CutsceneVictoryShot = "");
        DrawCutsceneShotChoice("Death", config.CutsceneDeathShot, catalog, () => config.CutsceneDeathShot = "");
        ImGui.Unindent();
    }

    private void DrawCutsceneShotChoice(string label, string id, CutsceneShotCatalog catalog, System.Action clear)
    {
        var entry = catalog.Find(id);
        ImGui.TextUnformatted($"{label}: {entry?.Name ?? "None"}");
        if (entry == null)
            return;
        ImGui.SameLine();
        if (ImGui.SmallButton($"x##cutsceneShotClear{label}"))
        {
            clear();
            config.Save();
        }
    }
}
