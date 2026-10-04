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
        if (ImGui.Button("Start preview##bodyfluids"))
        { BodyFluids?.Start(); config.Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Stop emission##bodyfluids")) BodyFluids?.StopEmission();
        ImGui.SameLine();
        if (ImGui.Button("Clear##bodyfluids")) BodyFluids?.Clear();

        var flow = config.BodyFluidFlowMlPerSecond;
        if (ImGui.SliderFloat("Flow##bodyfluids", ref flow, 0.01f, 0.6f, "%.2f ml/s"))
        { config.BodyFluidFlowMlPerSecond = flow; config.Save(); }
        var speed = config.BodyFluidSurfaceSpeed;
        if (ImGui.SliderFloat("Skin flow speed##bodyfluids", ref speed, 0.01f, 0.5f, "%.2f m/s"))
        { config.BodyFluidSurfaceSpeed = speed; config.Save(); }
        var relaxation = config.BodyFluidFilamentRelaxation;
        if (ImGui.SliderFloat("String persistence##bodyfluids", ref relaxation, 0.05f, 2f, "%.2f s"))
        { config.BodyFluidFilamentRelaxation = relaxation; config.Save(); }
        var length = config.BodyFluidFilamentLength;
        if (ImGui.SliderFloat("String length##bodyfluids", ref length, 0.03f, 0.35f, "%.2f m"))
        { config.BodyFluidFilamentLength = length; config.Save(); }
        var lifetime = config.BodyFluidPuddleLifetime;
        if (ImGui.SliderFloat("Puddle lifetime##bodyfluids", ref lifetime, 2f, 90f, "%.0f s"))
        { config.BodyFluidPuddleLifetime = lifetime; config.Save(); }
        var opacity = config.BodyFluidOpacity;
        if (ImGui.SliderFloat("Visibility##bodyfluids", ref opacity, 0.05f, 0.8f, "%.2f"))
        { config.BodyFluidOpacity = opacity; config.Save(); }
        var visualScale = config.BodyFluidVisualScale;
        if (ImGui.SliderFloat("Visual thickness##bodyfluids", ref visualScale, 1f, 4f, "%.1fx"))
        { config.BodyFluidVisualScale = visualScale; config.Save(); }
        HelpMarker("Enlarges visible drops and strings without changing flow, collision, or fluid volume.");
        if (ImGui.TreeNode("Mouth placement##bodyfluids"))
        {
            HelpMarker("Small adjustments for different faces and body mods. These move only the fluid source.");
            var forward = config.BodyFluidMouthForwardOffset;
            if (ImGui.SliderFloat("Forward##bodyfluidmouth", ref forward, -0.1f, 0.1f, "%.3f m"))
            { config.BodyFluidMouthForwardOffset = forward; config.Save(); }
            var height = config.BodyFluidMouthHeightOffset;
            if (ImGui.SliderFloat("Height##bodyfluidmouth", ref height, -0.1f, 0.1f, "%.3f m"))
            { config.BodyFluidMouthHeightOffset = height; config.Save(); }
            ImGui.TreePop();
        }
        if (enabled && BodyFluids != null)
        {
            ImGui.TextWrapped(BodyFluids.SurfaceStatus);
            ImGui.TextWrapped(BodyFluids.RenderStatus);
        }
    }
}
