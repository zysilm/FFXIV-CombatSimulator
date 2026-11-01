// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public Effects.ArrowImpactController? ArrowImpacts { get; set; }
    private string arrowImpactBoneFilter = string.Empty;

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
                   "and rides along with it. The arrow drawn is the one that bow fires. With no preferred " +
                   "bone the arrow can land anywhere on the body; with preferred bones it lands on one of " +
                   "them first, on a neighbouring bone next, and anywhere only now and then.");
        if (!enabled)
            return;

        ImGui.Indent();
        ImGui.TextUnformatted("Preferred bones");
        var spots = config.ArrowImpactPreferredSpots;
        var changed = false;
        for (var i = 0; i < spots.Count; i++)
        {
            if (ImGui.SmallButton($"x##arrowspot{i}"))
            {
                spots.RemoveAt(i);
                changed = true;
                break;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(spots[i]);
        }
        if (spots.Count == 0)
            ImGui.TextDisabled("None: arrows land anywhere on the body.");

        // Every bone on the skeleton, the same list the grab pickers offer, with a filter box.
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##arrowspotadd", "Add a bone..."))
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputTextWithHint("##arrowspotfilter", "filter", ref arrowImpactBoneFilter, 32);
            foreach (var bone in GrabbableBones())
            {
                if (spots.Contains(bone))
                    continue;
                if (arrowImpactBoneFilter.Length > 0 &&
                    bone.IndexOf(arrowImpactBoneFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (ImGui.Selectable(bone))
                {
                    spots.Add(bone);
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Raw skeleton bones. The arrow lands in the flesh between the bone and its parent;\n" +
                             "head, hands and feet also cover the body past the joint.");
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

        var fallAway = config.ArrowImpactFallAway;
        if (ImGui.Checkbox("Fall away after a while##arrowimpact", ref fallAway))
        {
            config.ArrowImpactFallAway = fallAway;
            config.Save();
        }
        HelpMarker("Off: arrows stay for as long as the body is there.");
        if (fallAway)
        {
            var linger = config.ArrowImpactLingerSeconds;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("Linger", ref linger, 5f, 180f, "%.0f s"))
            {
                config.ArrowImpactLingerSeconds = Math.Clamp(linger, 5f, 180f);
                config.Save();
            }
        }

        // Offered only when a bleeding effect has hooked in; it decides which bodies it can serve.
        if (ArrowImpacts?.BleedAvailable == true)
        {
            var bleed = config.ArrowImpactBleed;
            if (ImGui.Checkbox("Wounds bleed##arrowimpact", ref bleed))
            {
                config.ArrowImpactBleed = bleed;
                config.Save();
            }
            HelpMarker("Each arrow opens a wound that bleeds down the body for a while, slowing as it goes. " +
                       "Needs the body-fluid surface, which currently runs on your own character only.");
        }
        ImGui.Unindent();
    }
}
