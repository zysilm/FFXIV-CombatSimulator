// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Dev;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    public KoStripController? ArmorDetachment { get; set; }

    private static readonly string[] ArmorDetachmentTriggerLabels = { "Off", "On KO", "On each enemy hit" };
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

        var trigger = config.KoStripOnHitEnabled ? 2 : config.KoStripEnabled ? 1 : 0;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Detach armor##armordetach", ref trigger, ArmorDetachmentTriggerLabels, ArmorDetachmentTriggerLabels.Length))
        {
            config.KoStripEnabled = trigger == 1;
            config.KoStripOnHitEnabled = trigger == 2;
            config.Save();
        }
        HelpMarker("Your character only.\nOn KO: every selected piece comes off when you are knocked out.\n" +
                   "On each enemy hit: every hit that damages you removes the next selected piece\n" +
                   "(head and accessories first, then body, hands, legs, feet).");

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
        ImGui.SetNextItemWidth(200f);
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
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Dropped pieces##armordetach", ref lifetime, ArmorDetachmentLifetimeLabels, ArmorDetachmentLifetimeLabels.Length))
        {
            config.KoStripCloneAutoExpireEnabled = lifetime > 0;
            if (lifetime > 0) config.KoStripCloneAutoExpireSeconds = ArmorDetachmentLifetimeSeconds[lifetime];
            config.Save();
        }
        HelpMarker("Fading pieces out reduces the chance of one surviving into a zone change.");

        var garment = Math.Max(0, Array.IndexOf(ArmorDetachmentGarmentPresets, config.KoStripClothHoldPreset));
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Body / Legs##armordetach", ref garment, ArmorDetachmentGarmentLabels, ArmorDetachmentGarmentLabels.Length))
        {
            config.KoStripClothHoldPreset = ArmorDetachmentGarmentPresets[garment];
            config.Save();
        }
        HelpMarker("Drop when the body settles: the garment stays on until the body comes to rest, then falls off\n" +
                   "and goes limp and flat wherever it is no longer around the body.\n" +
                   "Half off, hanging on the body: the top is pulled down and hangs from the waist; trousers or a skirt\n" +
                   "are pulled down and hang from the shins. The loose part folds, lies flat and swings with the body.");

        ArmorDetachmentCheckbox("Still-attached pieces follow the body",
            () => config.KoStripGarmentFollowsBody, v => config.KoStripGarmentFollowsBody = v);
        HelpMarker("Pieces still on the body travel with it when the body is moved as a whole.\nPieces that already came off stay where they fell.");

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
