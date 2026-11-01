// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using CombatSimulator.Effects;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    private void DrawArrowImpactSection()
    {
        if (!ImGui.CollapsingHeader("Arrows"))
            return;

        var enabled = config.ArrowImpactEnabled;
        if (ImGui.Checkbox("Arrows stay in the target##arrowimpact", ref enabled))
        {
            config.ArrowImpactEnabled = enabled;
            config.Save();
        }
        HelpMarker("Every arrow loosed from a bow, yours or an enemy's, stays lodged in the body it hit " +
                   "and rides along with it. With no preferred spot the arrow can land anywhere on the body; " +
                   "with preferred spots it lands on one of them first, in the flesh around them next, and " +
                   "anywhere only now and then.");
        if (!enabled)
            return;

        ImGui.Indent();
        ImGui.TextUnformatted("Preferred spots");
        var spots = config.ArrowImpactPreferredSpots;
        var changed = false;
        for (var i = 0; i < spots.Count; i++)
        {
            var spot = ArrowImpactController.FindSpot(spots[i]);
            var label = spot?.Label ?? spots[i];
            if (ImGui.SmallButton($"x##arrowspot{i}"))
            {
                spots.RemoveAt(i);
                changed = true;
                break;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
        }
        if (spots.Count == 0)
            ImGui.TextDisabled("None: arrows land anywhere on the body.");

        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##arrowspotadd", "Add a spot..."))
        {
            foreach (var spot in ArrowImpactController.HumanSpots)
            {
                if (spots.Contains(spot.Key))
                    continue;
                if (ImGui.Selectable(spot.Label))
                {
                    spots.Add(spot.Key);
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (changed)
            config.Save();

        var perTarget = config.ArrowImpactMaxPerTarget;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Arrows per body", ref perTarget, 1, 40))
        {
            config.ArrowImpactMaxPerTarget = Math.Clamp(perTarget, 1, 40);
            config.Save();
        }
        HelpMarker("Once a body carries this many, the oldest arrow falls away as a new one lands.");

        var linger = config.ArrowImpactLingerSeconds;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("Linger", ref linger, 5f, 180f, "%.0f s"))
        {
            config.ArrowImpactLingerSeconds = Math.Clamp(linger, 5f, 180f);
            config.Save();
        }
        HelpMarker("How long each arrow stays before it falls away.");
        ImGui.Unindent();
    }
}
