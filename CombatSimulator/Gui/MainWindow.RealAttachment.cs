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
        HelpMarker("Material sets mass, stretch/bend resistance and default friction/damping.\n" +
            "Rigid construction adds braces; use Armor material for rigid plates.\n" +
            "Coat/Dress/Skirt use the equipment's skirt bones when available.");
        changed |= AttachmentSlider("Slip distance", settings.SlipDistance, 0f, 1.5f, "%.2f m", v => settings.SlipDistance = v);
        HelpMarker("Maximum slack at retained openings. The garment need not travel this distance. Zero holds openings in place.");
        changed |= AttachmentSlider("Sliding speed limit", settings.SpeedLimit, 0.05f, 4f, "%.2f m/s", v => settings.SpeedLimit = v);
        changed |= AttachmentSlider("Attachment firmness", settings.Firmness, 0f, 1f, "%.2f", v => settings.Firmness = v);
        changed |= AttachmentSlider("Body friction", settings.BodyFriction, 0f, 2f, "%.2f", v => settings.BodyFriction = v);
        changed |= AttachmentSlider("Ground friction", settings.GroundFriction, 0f, 2f, "%.2f", v => settings.GroundFriction = v);
        changed |= AttachmentSlider("Motion damping", settings.Damping, 0f, 12f, "%.2f", v => settings.Damping = v);
        changed |= AttachmentSlider("Contact thickness", settings.Thickness, 0.003f, 0.04f, "%.3f m", v => settings.Thickness = v);
        changed |= AttachmentSlider("Opening clearance", settings.OpeningScale, 1f, 1.8f, "%.2fx", v => settings.OpeningScale = v);
        HelpMarker("Space around the body used by opening contact. Larger values allow looser movement; this does not resize the equipment mesh.");
        changed |= AttachmentSlider("Uneven slack", settings.Asymmetry, 0f, 0.6f, "%.2f", v => settings.Asymmetry = v);
        var self = settings.SelfCollision;
        if (ImGui.Checkbox("Cloth self contact", ref self)) { settings.SelfCollision = self; changed = true; }
        var layers = settings.LayerCollision;
        if (ImGui.Checkbox("Contact with other attached clothing", ref layers)) { settings.LayerCollision = layers; changed = true; }
        if (ImGui.TreeNode("Retained connections"))
        {
            ImGui.TextWrapped("Each multiplier scales Slip distance at that opening. Zero pins it. Connections remain retained.");
            var anchors = attachmentEditSlot == 1
                ? new[] { ("j_kubi", "Neckline", 0.7f), ("j_sako_l", "Left shoulder", 1f), ("j_sako_r", "Right shoulder", 1f),
                    ("j_te_l", "Left cuff", 0.45f), ("j_te_r", "Right cuff", 0.45f) }
                : new[] { ("j_kosi", "Waist", 0.65f), ("j_asi_d_l", "Left trouser cuff", 0.5f), ("j_asi_d_r", "Right trouser cuff", 0.5f) };
            foreach (var (bone, title, fallback) in anchors)
            {
                var range = settings.Anchors.GetValueOrDefault(bone, fallback);
                if (ImGui.SliderFloat(title, ref range, 0f, 2f, "%.2fx"))
                { settings.Anchors[bone] = range; changed = true; }
                if (ImGui.TreeNode($"{title} position##{bone}"))
                {
                    var offset = settings.AnchorOffsets.TryGetValue(bone, out var saved) && saved != null
                        ? saved.ToVector() : Vector3.Zero;
                    if (ImGui.SliderFloat3($"Offset##{bone}", ref offset, -0.3f, 0.3f, "%.2f m"))
                    {
                        settings.AnchorOffsets[bone] = new AttachmentOffset { X = offset.X, Y = offset.Y, Z = offset.Z };
                        changed = true;
                    }
                    HelpMarker("Offset in the opening's local frame. Use the attachment cage to see the retained connection move.");
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
        if (ImGui.Checkbox("Show attachment cage", ref debug))
        { config.KoStripAttachmentDebugDraw = debug; config.Save(); }
        ImGui.TextWrapped(dismembermentController.GetAttachmentStatus());
        if (ImGui.Button("Rebind attached clothing")) dismembermentController.RebindRealAttachments();
        HelpMarker("Apply edited settings to currently attached pieces and restart them at the worn pose.\n" +
            "Otherwise settings apply on the next detachment. Profile changes also apply to new pieces.");
        ImGui.TextWrapped("Body/Legs only. Shape detail depends on the equipment's bones. Final connections stay attached; automatic recycling does not remove them.");
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
