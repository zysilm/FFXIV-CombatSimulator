// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using CombatSimulator.Effects.BodyFluids;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public BodyFluidController? BodyFluids { get; set; }

    private void DrawBodyFluidsSection()
    {
        if (!ImGui.CollapsingHeader("Body fluids - Saliva (experimental)##bodyfluids")) return;
        var enabled = config.BodyFluidsEnabled;
        if (ImGui.Checkbox("Enable saliva##bodyfluids", ref enabled))
        {
            config.BodyFluidsEnabled = enabled;
            if (!enabled) BodyFluids?.Clear();
            config.Save();
        }
        HelpMarker("Local cosmetic effect on your character. Start a preview below, or enable the KO trigger. Stop emission lets existing drops finish; Clear removes the effect immediately.");
        var onKo = config.BodyFluidsOnPlayerKo;
        if (ImGui.Checkbox("Emit on player KO##bodyfluids", ref onKo))
        { config.BodyFluidsOnPlayerKo = onKo; config.Save(); }
        HelpMarker("Emission continues until Stop emission or Clear. Older effects are recycled only when storage is full.");
        if (ImGui.Button("Start preview##bodyfluids"))
        { BodyFluids?.Start(); config.Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Stop emission##bodyfluids")) BodyFluids?.StopEmission();
        ImGui.SameLine();
        if (ImGui.Button("Clear##bodyfluids")) BodyFluids?.Clear();
        if (ImGui.Button("Reset defaults##bodyfluids"))
        {
            BodyFluids?.Clear();
            config.ResetBodyFluids();
            config.Save();
        }
        HelpMarker("Reset restores all saliva settings, stops emission and clears existing saliva.");

        var flow = config.BodyFluidFlowMlPerSecond;
        if (ImGui.SliderFloat("Flow##bodyfluids", ref flow, 0.001f, 0.12f, "%.3f ml/s"))
        { config.BodyFluidFlowMlPerSecond = flow; config.Save(); }
        var viscosity = config.BodyFluidViscosityPaSeconds;
        var thicknessScale = config.BodyFluidThicknessScale;
        if (ImGui.SliderFloat("Visible thickness##bodyfluids", ref thicknessScale, 1f, 4f, "%.2fx"))
        { config.BodyFluidThicknessScale = thicknessScale; config.Save(); }
        HelpMarker("Enlarges visible liquid thickness for close-up testing without changing flow, gravity or contact timing. Reset restores 1x.");
        if (ImGui.SliderFloat("Viscosity##bodyfluids", ref viscosity, 0.03f, 1.2f, "%.3f Pa s"))
        { config.BodyFluidViscosityPaSeconds = viscosity; config.Save(); }
        HelpMarker("Higher viscosity slows skin flow and resists stretching. These are artistic controls, not measured saliva properties.");
        var stringiness = config.BodyFluidStringiness;
        if (ImGui.SliderFloat("Stringiness##bodyfluids", ref stringiness, 1f, 20f, "%.1f"))
        { config.BodyFluidStringiness = stringiness; config.Save(); }
        HelpMarker("Raises the thread's stretching resistance independently of skin flow. Higher values keep a hanging strand cohesive for longer. This is an artistic calibration, not measured saliva rheology.");
        var relaxation = config.BodyFluidFilamentRelaxation;
        if (ImGui.SliderFloat("Stress relaxation##bodyfluids", ref relaxation, 0.05f, 2f, "%.2f s"))
        { config.BodyFluidFilamentRelaxation = relaxation; config.Save(); }
        var reflection = config.BodyFluidReflectionStrength;
        if (ImGui.SliderFloat("Reflection strength##bodyfluids", ref reflection, 0f, 1f, "%.2f"))
        { config.BodyFluidReflectionStrength = reflection; config.Save(); }
        var roughness = config.BodyFluidRoughness;
        if (ImGui.SliderFloat("Highlight roughness##bodyfluids", ref roughness, 0.02f, 1f, "%.2f"))
        { config.BodyFluidRoughness = roughness; config.Save(); }
        HelpMarker("Lower reflection keeps the liquid clearer. Higher roughness broadens highlights. Clear removes accumulated liquid.");
        var cloudiness = config.BodyFluidCloudiness;
        if (ImGui.SliderFloat("Cloudiness##bodyfluids", ref cloudiness, 0f, 1f, "%.2f"))
        { config.BodyFluidCloudiness = cloudiness; config.Save(); }
        var foam = config.BodyFluidFoamAmount;
        if (ImGui.SliderFloat("Foam flecks##bodyfluids", ref foam, 0f, 1f, "%.2f"))
        { config.BodyFluidFoamAmount = foam; config.Save(); }
        HelpMarker("Cloudiness adds a translucent milky tint. Foam adds small, irregular white flecks that follow the liquid surface. Both are cosmetic controls; zero keeps the clear material.");
        if (enabled && BodyFluids != null)
        {
            ImGui.TextWrapped(BodyFluids.SurfaceStatus);
            ImGui.TextWrapped(BodyFluids.RenderStatus);
        }
    }
}
