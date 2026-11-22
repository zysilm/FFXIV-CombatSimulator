// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using AdvancedRagdoll.Attachment;
using CombatSimulator.Dev;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public KoStripController? ArmorDetachment { get; set; }

    private static readonly string[] ArmorDetachmentTriggerLabels = { "Off", "On KO", "On ragdoll activated" };
    private static readonly string[] ArmorDetachmentRemovedLabels = { "Fall physically", "Just vanish" };
    private static readonly string[] ArmorDetachmentLifetimeLabels =
        { "Stay until reset", "Fade after 2 s", "Fade after 10 s", "Fade after 30 s" };
    private static readonly float[] ArmorDetachmentLifetimeSeconds = { 0f, 2f, 10f, 30f };
    private static readonly string[] ArmorDetachmentGarmentLabels =
        { "Drop when the body settles", "Half off, hanging on the body" };
    private static readonly int[] ArmorDetachmentGarmentPresets = { 1, 5 };

    private void DrawArmorDetachmentEntrySection()
    {
        if (!ImGui.CollapsingHeader("Armor Detachment"))
            return;

        var trigger = Math.Clamp((int)config.KoStripTrigger, 0, ArmorDetachmentTriggerLabels.Length - 1);
        ImGui.SetNextItemWidth(ArmorDetachmentItemWidth());
        if (ImGui.Combo("Detach armor##armordetach", ref trigger, ArmorDetachmentTriggerLabels, ArmorDetachmentTriggerLabels.Length))
        {
            config.KoStripTrigger = (ArmorDetachmentTrigger)trigger;
            config.Save();
        }
        HelpMarker("Your character (allies and enemies: next section).\n" +
                   "On KO: every selected piece comes off the moment you are knocked out.\n" +
                   "On ragdoll activated: every selected piece comes off when the body goes limp\n" +
                   "(the ragdoll starts: Ragdoll Trigger > Ragdoll starts).");

        ArmorDetachmentCheckbox("Head", () => config.KoStripHead, v => config.KoStripHead = v);
        ImGui.SameLine();
        ArmorDetachmentCheckbox("Body", () => config.KoStripBody, v => config.KoStripBody = v);
        ImGui.SameLine();
        ArmorDetachmentCheckbox("Hands", () => config.KoStripHands, v => config.KoStripHands = v);
        ImGui.SameLine();
        ArmorDetachmentCheckbox("Legs", () => config.KoStripLegs, v => config.KoStripLegs = v);
        ImGui.SameLine();
        ArmorDetachmentCheckbox("Feet", () => config.KoStripFeet, v => config.KoStripFeet = v);
        ImGui.SameLine();
        ArmorDetachmentCheckbox("Accessories",
            () => config.KoStripEars || config.KoStripNeck || config.KoStripWrists || config.KoStripRFinger || config.KoStripLFinger,
            v => config.KoStripEars = config.KoStripNeck = config.KoStripWrists = config.KoStripRFinger = config.KoStripLFinger = v);

        var removed = config.KoStripPhysicsDrop || config.KoStripPhysicsDropClothing ? 0 : 1;
        ImGui.SetNextItemWidth(ArmorDetachmentItemWidth());
        if (ImGui.Combo("Removed pieces##armordetach", ref removed, ArmorDetachmentRemovedLabels, ArmorDetachmentRemovedLabels.Length))
        {
            config.KoStripPhysicsDrop = config.KoStripPhysicsDropClothing = removed == 0;
            config.Save();
        }
        HelpMarker("Fall physically: each piece drops as its own object.\nJust vanish: pieces are hidden without spawning anything.");

        ImGui.BeginDisabled(removed != 0);

        var lifetime = config.KoStripCloneAutoExpireEnabled
            ? Math.Max(1, Array.IndexOf(ArmorDetachmentLifetimeSeconds, config.KoStripCloneAutoExpireSeconds))
            : 0;
        ImGui.SetNextItemWidth(ArmorDetachmentItemWidth());
        if (ImGui.Combo("Dropped pieces##armordetach", ref lifetime, ArmorDetachmentLifetimeLabels, ArmorDetachmentLifetimeLabels.Length))
        {
            config.KoStripCloneAutoExpireEnabled = lifetime > 0;
            if (lifetime > 0) config.KoStripCloneAutoExpireSeconds = ArmorDetachmentLifetimeSeconds[lifetime];
            config.Save();
        }
        HelpMarker("Fading pieces out reduces the chance of one surviving into a zone change.");

        var garment = Math.Max(0, Array.IndexOf(ArmorDetachmentGarmentPresets, config.KoStripClothHoldPreset));
        ImGui.SetNextItemWidth(ArmorDetachmentItemWidth());
        if (ImGui.Combo("Body / Legs##armordetach", ref garment, ArmorDetachmentGarmentLabels, ArmorDetachmentGarmentLabels.Length))
        {
            config.KoStripClothHoldPreset = ArmorDetachmentGarmentPresets[garment];
            config.Save();
        }
        HelpMarker("Drop when the body settles: the garment stays on until the body comes to rest, then falls off\n" +
                   "and goes limp and flat wherever it is no longer around the body.\n" +
                   "Half off, hanging on the body: the parts ticked below stay on the body; the rest is pulled off,\n" +
                   "folds, lies flat and swings with the body.");

        if (ArmorDetachmentGarmentPresets[garment] == 5)
        {
            ImGui.Indent();

            ImGui.TextUnformatted("Top stays on at");
            HelpMarker("Tick every part of the top that should stay on the body. Everything else comes off and\n" +
                       "hangs from the ticked parts. More holds keep it from drooping; left and right are the\n" +
                       "character's own. Applies to the next detachment.");
            if (ImGui.BeginTable("##armordetachtophold", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableSetupColumn("Left arm");
                ImGui.TableSetupColumn("Body");
                ImGui.TableSetupColumn("Right arm");
                ImGui.TableHeadersRow();
                ArmorDetachmentHoldRow(true, ("Upper arm", GarmentTether.TopLeftUpperArm), ("Collar", GarmentTether.TopCollar), ("Upper arm", GarmentTether.TopRightUpperArm));
                ArmorDetachmentHoldRow(true, ("Forearm", GarmentTether.TopLeftForearm), ("Chest", GarmentTether.TopChest), ("Forearm", GarmentTether.TopRightForearm));
                ArmorDetachmentHoldRow(true, null, ("Belly", GarmentTether.TopBelly), null);
                ArmorDetachmentHoldRow(true, null, ("Waist", GarmentTether.TopWaist), null);
                ImGui.EndTable();
            }
            if (config.KoStripTopHolds == 0)
                ImGui.TextDisabled("Nothing ticked: the top drops off completely.");

            ImGui.Spacing();
            ImGui.TextUnformatted("Trousers / skirt stay on at");
            HelpMarker("Tick every part of the lower garment that should stay on the body.\nApplies to the next detachment.");
            if (ImGui.BeginTable("##armordetachlowerhold", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableSetupColumn("Left leg");
                ImGui.TableSetupColumn("Body");
                ImGui.TableSetupColumn("Right leg");
                ImGui.TableHeadersRow();
                ArmorDetachmentHoldRow(false, ("Thigh", GarmentTether.LowerLeftThigh), ("Waist", GarmentTether.LowerWaist), ("Thigh", GarmentTether.LowerRightThigh));
                ArmorDetachmentHoldRow(false, ("Shin", GarmentTether.LowerLeftShin), null, ("Shin", GarmentTether.LowerRightShin));
                ImGui.EndTable();
            }
            if (config.KoStripLowerHolds == 0)
                ImGui.TextDisabled("Nothing ticked: the lower garment drops off completely.");

            ImGui.Spacing();
            ArmorDetachmentCombo("Pull##armordetach", GarmentTether.PullLabels,
                () => config.KoStripHangPull, v => config.KoStripHangPull = v);
            HelpMarker("How hard the loose part is pulled down and away from the body when it comes off.\n" +
                       "Applies to the next detachment.");
            ImGui.Unindent();
        }

        ImGui.EndDisabled();

        if (ImGui.Button("Detach Now##armordetach"))
        {
            var player = CombatSimulator.Core.Services.ObjectTable.LocalPlayer;
            if (player != null) ArmorDetachment?.StripNow(player.Address);
        }
        HelpMarker("Detach the selected pieces from your character right now.");
        ImGui.SameLine();
        if (ImGui.Button("Reset to Defaults##armordetach"))
            config.ResetArmorDetachmentDefaults();
    }

    private void DrawArmorDetachmentOthersSection()
    {
        if (!ImGui.CollapsingHeader("Armor Detachment: Allies and Enemies"))
            return;

        const string tooltip = "Uses the Armor Detachment settings above (trigger, pieces, how they come off).\n" +
                               "Does nothing while \"Detach armor\" is Off.\n" +
                               "Humanoids wearing equipment only.";
        ArmorDetachmentCheckbox("Allies", () => config.KoStripAllies, v => config.KoStripAllies = v);
        HelpMarker(tooltip);
        ArmorDetachmentCheckbox("Enemies", () => config.KoStripEnemies, v => config.KoStripEnemies = v);
        HelpMarker(tooltip);

        if (config.KoStripAllies || config.KoStripEnemies)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f));
            ImGui.TextWrapped("Each dropped piece is its own physics body: many at once can cost frames in big battles.");
            ImGui.PopStyleColor();
        }
    }

    // Wide enough for the longest option at any window size, without swallowing the label.
    private static float ArmorDetachmentItemWidth()
        => Math.Clamp(ImGui.GetContentRegionAvail().X * 0.6f, 220f, 380f);

    private void ArmorDetachmentHoldRow(bool top, params (string Label, int Bit)?[] cells)
    {
        ImGui.TableNextRow();
        for (var column = 0; column < cells.Length; column++)
        {
            ImGui.TableNextColumn();
            if (cells[column] is not { } cell) continue;
            var holds = top ? config.KoStripTopHolds : config.KoStripLowerHolds;
            var held = (holds & cell.Bit) != 0;
            if (!ImGui.Checkbox($"{cell.Label}##armordetachhold{(top ? "t" : "l")}{cell.Bit}", ref held)) continue;
            holds = held ? holds | cell.Bit : holds & ~cell.Bit;
            if (top) config.KoStripTopHolds = holds; else config.KoStripLowerHolds = holds;
            config.Save();
        }
    }

    private void ArmorDetachmentCombo(string label, string[] items, Func<int> get, Action<int> set)
    {
        var v = Math.Clamp(get(), 0, items.Length - 1);
        ImGui.SetNextItemWidth(ArmorDetachmentItemWidth());
        if (ImGui.Combo(label, ref v, items, items.Length))
        {
            set(v);
            config.Save();
        }
    }

    private void ArmorDetachmentCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (ImGui.Checkbox($"{label}##armordetach", ref v))
        {
            set(v);
            config.Save();
        }
    }
}
