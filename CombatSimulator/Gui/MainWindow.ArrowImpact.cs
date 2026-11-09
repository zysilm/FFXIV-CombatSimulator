// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public Effects.ArrowImpactController? ArrowImpacts { get; set; }

    private static readonly string[] ArrowRegionLabels = { "Anywhere", "Head", "Torso", "Limbs" };
    private static readonly string[][] ArrowRegionBones =
    {
        Array.Empty<string>(),
        new[] { "j_kao" },
        new[] { "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kosi" },
        new[] { "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r" },
    };
    private static readonly string[] ArrowLingerLabels = { "Never", "After 45 s", "After 2 min", "After 3 min" };
    private static readonly float[] ArrowLingerSeconds = { 0f, 45f, 120f, 180f };

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
                   "and rides along with it. The arrow drawn is the one that bow fires. Once a body carries " +
                   "40, the oldest falls away as a new one lands.");
        if (!enabled)
            return;

        ImGui.Indent();
        var spots = config.ArrowImpactPreferredSpots;
        var region = Array.FindIndex(ArrowRegionBones, bones =>
            bones.Length == spots.Count && bones.All(spots.Contains));
        ImGui.SetNextItemWidth(180);
        if (PresetCombo("Aim for##arrowimpact", ArrowRegionLabels, region, out var pickedRegion))
        {
            spots.Clear();
            spots.AddRange(ArrowRegionBones[pickedRegion]);
            config.Save();
        }
        HelpMarker("Anywhere: arrows land anywhere on the body. Otherwise they land in the chosen region first, " +
                   "on a neighbouring bone next, and anywhere only now and then.");

        var linger = !config.ArrowImpactFallAway ? 0
            : Array.FindIndex(ArrowLingerSeconds, s => s > 0f && MathF.Abs(s - config.ArrowImpactLingerSeconds) < 0.5f);
        ImGui.SetNextItemWidth(180);
        if (PresetCombo("Fall away##arrowimpact", ArrowLingerLabels, linger, out var pickedLinger))
        {
            config.ArrowImpactFallAway = pickedLinger > 0;
            if (pickedLinger > 0) config.ArrowImpactLingerSeconds = ArrowLingerSeconds[pickedLinger];
            config.Save();
        }
        HelpMarker("Never: arrows stay for as long as the body is there.");

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
