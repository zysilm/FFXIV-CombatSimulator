// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Attachment;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    private int attachmentEditSlot = 1;
    private string? attachmentEditModel;
    private AttachmentSettings? attachmentUndo;
    private string? attachmentUndoKey;
    private readonly string[] attachmentMaterials = Enum.GetNames<GarmentMaterial>();

    private void DrawRealAttachmentSettings()
    {
        config.KoStripAttachmentBody ??= new();
        config.KoStripAttachmentLegs ??= new() { Template = GarmentTemplate.Trousers };
        config.KoStripAttachmentOverrides ??= new();
        var upper = attachmentEditSlot == 1;
        if (ImGui.RadioButton("Body defaults##attach", upper)) { attachmentEditSlot = 1; attachmentEditModel = null; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Legs defaults##attach", !upper)) { attachmentEditSlot = 3; attachmentEditModel = null; }
        var models = dismembermentController.GetAttachmentModels();
        var label = attachmentEditModel == null ? "Slot defaults" : "Equipment override";
        if (ImGui.BeginCombo("Equipment##attachment", label))
        {
            if (ImGui.Selectable("Slot defaults", attachmentEditModel == null)) attachmentEditModel = null;
            foreach (var model in models)
                if (model.Slot == attachmentEditSlot && ImGui.Selectable(model.Label, attachmentEditModel == model.Key))
                    attachmentEditModel = model.Key;
            // Saved equipment can still be edited after it has been removed or its clone recycled.
            foreach (var entry in config.KoStripAttachmentOverrides)
                if (entry.Key.StartsWith($"{attachmentEditSlot}:", StringComparison.Ordinal) &&
                    !models.Exists(m => m.Key == entry.Key) && ImGui.Selectable(entry.Key.Split('|')[0], attachmentEditModel == entry.Key))
                    attachmentEditModel = entry.Key;
            ImGui.EndCombo();
        }
        var defaults = attachmentEditSlot == 1 ? config.KoStripAttachmentBody : config.KoStripAttachmentLegs;
        var settings = defaults;
        if (attachmentEditModel != null)
        {
            if (!config.KoStripAttachmentOverrides.TryGetValue(attachmentEditModel, out settings) || settings == null)
            {
                settings = defaults.Copy();
                config.KoStripAttachmentOverrides[attachmentEditModel] = settings;
                config.Save();
            }
            ImGui.TextWrapped(attachmentEditModel.Split('|')[0]);
        }
        var key = attachmentEditModel ?? $"slot:{attachmentEditSlot}";
        if (attachmentUndoKey != key)
        {
            attachmentUndoKey = key;
            attachmentUndo = settings.Copy();
        }
        settings.Anchors ??= new();
        settings.AnchorOffsets ??= new();
        var changed = false;
        if (attachmentEditSlot == 1)
            ImGui.TextWrapped("Upper clothing retains its neckline, shoulders and sleeves. Only the lower torso/hem loosens downward, capped at 0.08 m before regional weighting. Gravity changes speed, not direction.");
        var templates = attachmentEditSlot == 1
            ? new[] { GarmentTemplate.Top, GarmentTemplate.Coat, GarmentTemplate.Dress, GarmentTemplate.Rigid }
            : new[] { GarmentTemplate.Trousers, GarmentTemplate.Skirt, GarmentTemplate.Rigid };
        var names = Array.ConvertAll(templates, t => t.ToString());
        var template = Math.Max(0, Array.IndexOf(templates, settings.Template));
        if (ImGui.Combo("Garment construction", ref template, names, names.Length))
        { settings.Template = templates[template]; changed = true; }
        var material = Math.Clamp((int)settings.Material, 0, attachmentMaterials.Length - 1);
        if (ImGui.Combo("Material", ref material, attachmentMaterials, attachmentMaterials.Length))
        { settings.ApplyMaterial((GarmentMaterial)material); changed = true; }
        HelpMarker("Material sets sliding resistance, skirt stiffness and damping. Rigid construction fixes skirt hinges.\n" +
            "Skirt bones are detected automatically, including on trouser equipment.");
        changed |= AttachmentSlider("Slip distance", settings.SlipDistance, 0f, 1.5f, "%.2f m", v => settings.SlipDistance = v);
        HelpMarker("Upper travel is capped at 0.08 m before weighting. Trouser waist travel is capped at 45% of thigh length, leg travel at 65% of each segment. Cuffs stay fixed. Skirt hinges use the separate controls below.");
        changed |= AttachmentSlider("Sliding speed limit", settings.SpeedLimit, 0.05f, 4f, "%.2f m/s", v => settings.SpeedLimit = v);
        changed |= AttachmentSlider("Sliding resistance", settings.BodyFriction, 0f, 2f, "%.2f", v => settings.BodyFriction = v);
        changed |= AttachmentSlider("Motion damping", settings.Damping, 0f, 12f, "%.2f", v => settings.Damping = v);
        changed |= AttachmentSlider("Skirt maximum opening", settings.SkirtSwingDegrees, 0f, 110f, "%.0f deg", v => settings.SkirtSwingDegrees = v);
        changed |= AttachmentSlider("Skirt stiffness", settings.SkirtStiffness, 4f, 100f, "%.1f", v => settings.SkirtStiffness = v);
        changed |= AttachmentSlider("Skirt damping", settings.SkirtDamping, 1f, 4f, "%.2f", v => settings.SkirtDamping = v);
        changed |= AttachmentSlider("Skirt contact thickness", settings.Thickness, .003f, .04f, "%.3f m", v => settings.Thickness = v);
        HelpMarker("Skirt chains pivot at a fixed waist with gravity, shape retention and damping. Leg capsules and a floor plane constrain opening angles.\n" +
            "Zero opening fixes the reference shape. Blocked panels report contacts that cannot be cleared within the angle limit. This is a bone proxy, not mesh cloth or self-collision.");
        if (ImGui.TreeNode("Retained connections"))
        {
            ImGui.TextWrapped("Multipliers control bounded torso or leg sliding. Trouser cuffs stay fixed. Waist and leg travel have anatomical limits; the Skirt template retains its waist.");
            var anchors = attachmentEditSlot == 1
                ? new[] { ("j_kosi", "Lower hem", 0.7f), ("j_sebo_a", "Lower torso", 0.7f),
                    ("j_sebo_b", "Mid torso", 0.7f), ("j_sebo_c", "Upper torso", 0.7f) }
                : new[] { ("j_kosi", "Waist slide", 0.65f), ("j_asi_a_l", "Left thigh", 0.65f), ("j_asi_a_r", "Right thigh", 0.65f),
                    ("j_asi_b_l", "Left knee", 0.65f), ("j_asi_b_r", "Right knee", 0.65f) };
            foreach (var (bone, title, fallback) in anchors)
            {
                var range = settings.Anchors.GetValueOrDefault(bone, fallback);
                if (ImGui.SliderFloat(title, ref range, 0f, 2f, "%.2fx"))
                { settings.Anchors[bone] = range; changed = true; }
                if (attachmentEditSlot == 1 && ImGui.TreeNode($"{title} position##{bone}"))
                {
                    var offset = settings.AnchorOffsets.TryGetValue(bone, out var saved) && saved != null
                        ? saved.ToVector() : Vector3.Zero;
                    if (ImGui.SliderFloat3($"Offset##{bone}", ref offset, -0.3f, 0.3f, "%.2f m"))
                    {
                        settings.AnchorOffsets[bone] = new AttachmentOffset { X = offset.X, Y = offset.Y, Z = offset.Z };
                        changed = true;
                    }
                    HelpMarker("Offset in the opening's local frame. Use the offset overlay to see the attachment move.");
                    ImGui.TreePop();
                }
            }
            ImGui.TreePop();
        }
        if (changed) config.Save();
        if (ImGui.Button("Undo edits##attachment") && attachmentUndo != null)
        {
            SaveAttachmentSettings(attachmentUndo.Copy());
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset settings##attachment"))
            SaveAttachmentSettings(new AttachmentSettings { Template = attachmentEditSlot == 1 ? GarmentTemplate.Top : GarmentTemplate.Trousers });
        if (attachmentEditModel != null && ImGui.Button("Use slot defaults##attachment"))
        {
            config.KoStripAttachmentOverrides.Remove(attachmentEditModel);
            attachmentEditModel = null;
            config.Save();
        }
        var debug = config.KoStripAttachmentDebugDraw;
        if (ImGui.Checkbox("Show attachment offsets", ref debug))
        { config.KoStripAttachmentDebugDraw = debug; config.Save(); }
        ImGui.TextWrapped(dismembermentController.GetAttachmentStatus());
        if (ImGui.Button("Apply settings / restart slide")) dismembermentController.RebindRealAttachments();
        HelpMarker("Apply edited settings and restart the slide at the worn pose. Following the live body is automatic every frame.\n" +
            "Otherwise settings apply on the next detachment. Profile changes also apply to new pieces.");
        ImGui.TextWrapped("Body/Legs only. Live body following with bounded sliding and retained skirt hinges. Bone proxies cannot guarantee mesh clearance for every garment or pose.");
    }

    private void SaveAttachmentSettings(AttachmentSettings settings)
    {
        if (attachmentEditModel != null) config.KoStripAttachmentOverrides[attachmentEditModel] = settings;
        else if (attachmentEditSlot == 1) config.KoStripAttachmentBody = settings;
        else config.KoStripAttachmentLegs = settings;
        config.Save();
    }

    private static bool AttachmentSlider(string label, float value, float min, float max, string format, Action<float> set)
    {
        if (!ImGui.SliderFloat(label + "##realattachment", ref value, min, max, format)) return false;
        set(value);
        return true;
    }
}
