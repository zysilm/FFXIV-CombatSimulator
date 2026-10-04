// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using CombatSimulator.Effects.BodyFluids;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public BodyFluidController? BodyFluids { get; set; }
    private int bodyFluidOutletToAdd;
    private BodyFluidOutletKind bodyFluidSelectedOutlet = BodyFluidOutletKind.Mouth;
    private static readonly string[] BodyFluidOutletLabels =
        { "Mouth", "Part 1 - Left", "Part 1 - Right", "Part 2", "Part 3",
            "Nose - Left", "Nose - Right", "Eye - Left", "Eye - Right" };

    private void DrawBodyFluidsSection()
    {
        if (!ImGui.CollapsingHeader("Body fluids (experimental)##bodyfluids")) return;
        var enabled = config.BodyFluidsEnabled;
        if (ImGui.Checkbox("Enable body fluids##bodyfluids", ref enabled))
        {
            config.BodyFluidsEnabled = enabled;
            if (!enabled) BodyFluids?.Clear();
            config.Save();
        }
        var onKo = config.BodyFluidsOnPlayerKo;
        if (ImGui.Checkbox("Emit on player KO##bodyfluids", ref onKo))
        { config.BodyFluidsOnPlayerKo = onKo; config.Save(); }
        HelpMarker("Start preview supplies every enabled site. Stop emission lets existing fluid finish; Clear removes it immediately. Older effects are recycled only when storage is full.");
        if (ImGui.Button("Start preview##bodyfluids"))
        { BodyFluids?.Start(); config.Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Stop emission##bodyfluids")) BodyFluids?.StopEmission();
        ImGui.SameLine();
        if (ImGui.Button("Clear##bodyfluids")) BodyFluids?.Clear();
        if (ImGui.Button("Reset all defaults##bodyfluids"))
        {
            BodyFluids?.Clear(); config.ResetBodyFluids();
            bodyFluidSelectedOutlet = BodyFluidOutletKind.Mouth; config.Save();
        }
        HelpMarker("Reset all returns to one clear Mouth site with default settings and clears existing fluid.");
        DrawBodyFluidOutlets();
        if (enabled && BodyFluids != null && ImGui.TreeNode("Runtime diagnostics##bodyfluids"))
        {
            ImGui.TextWrapped(BodyFluids.SurfaceStatus);
            ImGui.TextWrapped(BodyFluids.RenderStatus); ImGui.TreePop();
        }
    }

    private void DrawBodyFluidOutlets()
    {
        config.ClampBodyFluids();
        ImGui.Separator(); ImGui.TextUnformatted("Emission sites");
        ImGui.Combo("Site##bodyfluidsadd", ref bodyFluidOutletToAdd,
            BodyFluidOutletLabels, BodyFluidOutletLabels.Length);
        var selectedKind = (BodyFluidOutletKind)bodyFluidOutletToAdd;
        var alreadyAdded = false;
        foreach (var source in config.BodyFluidOutlets)
            if (source.Kind == selectedKind) { alreadyAdded = true; break; }
        if (alreadyAdded) ImGui.BeginDisabled();
        if (ImGui.Button("Add site##bodyfluids"))
        {
            config.BodyFluidOutlets.Add(new BodyFluidOutletSettings { Kind = selectedKind, SettingsVersion = 1 });
            bodyFluidSelectedOutlet = selectedKind; config.Save();
        }
        if (alreadyAdded) ImGui.EndDisabled();
        // Sequence editor pattern: selectable table followed by one selected editor.
        // Stacking the editor also keeps narrow Effects windows usable.
        if (ImGui.BeginTable("##bodyfluidsites", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Site", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();
            for (var i = 0; i < config.BodyFluidOutlets.Count;)
            {
                var source = config.BodyFluidOutlets[i];
                ImGui.PushID((int)source.Kind);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(BodyFluidOutletLabels[(int)source.Kind], bodyFluidSelectedOutlet == source.Kind))
                    bodyFluidSelectedOutlet = source.Kind;
                ImGui.TableNextColumn();
                var active = source.Enabled;
                if (ImGui.Checkbox("##enabled", ref active))
                { source.Enabled = active; config.Save(); }
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Remove"))
                {
                    config.BodyFluidOutlets.RemoveAt(i);
                    if (bodyFluidSelectedOutlet == source.Kind && config.BodyFluidOutlets.Count > 0)
                        bodyFluidSelectedOutlet = config.BodyFluidOutlets[0].Kind;
                    config.Save(); ImGui.PopID(); continue;
                }
                ImGui.PopID(); i++;
            }
            ImGui.EndTable();
        }
        BodyFluidOutletSettings? selected = null;
        foreach (var source in config.BodyFluidOutlets)
            if (source.Kind == bodyFluidSelectedOutlet) { selected = source; break; }
        if (selected == null && config.BodyFluidOutlets.Count > 0)
        { selected = config.BodyFluidOutlets[0]; bodyFluidSelectedOutlet = selected.Kind; }
        if (selected == null)
        { ImGui.TextWrapped("No emission sites. Add a site to supply new fluid."); return; }

        ImGui.Separator();
        ImGui.TextUnformatted($"Edit: {BodyFluidOutletLabels[(int)selected.Kind]}");
        if (ImGui.Button("Reset this site##bodyfluids"))
        { selected.ResetSettings(); config.Save(); }
        HelpMarker("Restores only this site's settings, enables it and clears its offsets. Other sites are unchanged.");
        ImGui.PushID((int)selected.Kind);
        var changed = false;
        if (ImGui.BeginTabBar("##bodyfluidsiteeditor"))
        {
            if (ImGui.BeginTabItem("Flow"))
            {
                selected.FlowMlPerSecond = BodyFluidSiteSlider("Flow", selected.FlowMlPerSecond, 0.001f, 0.48f, "%.3f ml/s", ref changed);
                selected.ViscosityPaSeconds = BodyFluidSiteSlider("Viscosity", selected.ViscosityPaSeconds, 0.03f, 1.2f, "%.3f Pa s", ref changed);
                selected.Stringiness = BodyFluidSiteSlider("Stringiness", selected.Stringiness, 1f, 20f, "%.1f", ref changed);
                selected.FilamentRelaxation = BodyFluidSiteSlider("Stress relaxation", selected.FilamentRelaxation, 0.05f, 2f, "%.2f s", ref changed);
                selected.SurfaceSpeed = BodyFluidSiteSlider("Skin flow speed", selected.SurfaceSpeed, 0.01f, 0.5f, "%.2f m/s", ref changed);
                HelpMarker("These controls belong only to the selected site. Viscosity slows skin flow; stringiness controls stretching resistance.");
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Appearance"))
            {
                var red = selected.BloodTint;
                if (ImGui.Checkbox("Blood red", ref red))
                { selected.BloodTint = red; changed = true; }
                HelpMarker("Off uses the existing clear material. Color is independent for each site.");
                selected.ThicknessScale = BodyFluidSiteSlider("Visible thickness", selected.ThicknessScale, 1f, 4f, "%.2fx", ref changed);
                HelpMarker("Changes visible thickness without increasing supply. Default is 2x.");
                selected.ReflectionStrength = BodyFluidSiteSlider("Reflection strength", selected.ReflectionStrength, 0f, 1f, "%.2f", ref changed);
                selected.Roughness = BodyFluidSiteSlider("Highlight roughness", selected.Roughness, 0.02f, 1f, "%.2f", ref changed);
                selected.Cloudiness = BodyFluidSiteSlider("Cloudiness", selected.Cloudiness, 0f, 1f, "%.2f", ref changed);
                selected.FoamAmount = BodyFluidSiteSlider("Foam flecks", selected.FoamAmount, 0f, 1f, "%.2f", ref changed);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Position"))
            {
                selected.OffsetX = BodyFluidSiteSlider("Offset X", selected.OffsetX, -0.1f, 0.1f, "%.3f m", ref changed);
                selected.OffsetY = BodyFluidSiteSlider("Offset Y", selected.OffsetY, -0.1f, 0.1f, "%.3f m", ref changed);
                selected.OffsetZ = BodyFluidSiteSlider("Offset Z", selected.OffsetZ, -0.1f, 0.1f, "%.3f m", ref changed);
                HelpMarker("Offsets follow the site's local bone axes and current pose. Mouth and eye endpoints are selected automatically by gravity.");
                if (BodyFluids != null) ImGui.TextWrapped(BodyFluids.GetOutletStatus(selected.Kind));
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.PopID();
        if (changed) config.Save();
    }

    private static float BodyFluidSiteSlider(string label, float value, float min, float max,
        string format, ref bool changed)
    {
        changed |= ImGui.SliderFloat(label, ref value, min, max, format);
        return value;
    }
}
