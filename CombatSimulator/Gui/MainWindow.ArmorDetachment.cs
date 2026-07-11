using System;
using System.Numerics;
using CombatSimulator.Dev;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    private static readonly string[] ArmorDetachmentClothHoldPresetLabels =
    {
        "Quick",
        "Natural",
        "Clingy",
        "Slide to floor",
        "Visual only",
    };

    private void DrawArmorDetachmentEntrySection()
    {
        if (!ImGui.CollapsingHeader("Armor Detachment"))
            return;

        var showControls = config.ShowArmorDetachmentControls;
        if (ImGui.Checkbox("Show Armor Detachment controls", ref showControls))
        {
            config.ShowArmorDetachmentControls = showControls;
            config.Save();
        }
        HelpMarker("Open the compact Armor Detachment control window. Detachment modes stay off until enabled there.");
    }

    public void DrawArmorDetachmentControls(KoStripController ctrl)
    {
        var showWindow = config.ShowArmorDetachmentControls;
        if (!ImGui.Begin("Armor Detachment", ref showWindow,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (config.ShowArmorDetachmentControls != showWindow)
            {
                config.ShowArmorDetachmentControls = showWindow;
                config.Save();
            }
            ImGui.End();
            return;
        }

        if (config.ShowArmorDetachmentControls != showWindow)
        {
            config.ShowArmorDetachmentControls = showWindow;
            config.Save();
        }

        var enabled = config.KoStripEnabled;
        if (ImGui.Checkbox("Detach on KO##armordetach", ref enabled))
        {
            config.KoStripEnabled = enabled;
            if (enabled) config.KoStripOnHitEnabled = false;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When you are knocked out, visually detach the selected armor slots.\nYour character only. Visual only.");

        if (DevExperimentalUnlocked)
        {
            var onHit = config.KoStripOnHitEnabled;
            if (ImGui.Checkbox("Detach on monster hit##armordetach_onhit", ref onHit))
            {
                config.KoStripOnHitEnabled = onHit;
                if (onHit) config.KoStripEnabled = false;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Dev experimental: when a monster attack connects with your KO'd character,\n" +
                                 "detach one configured armor slot first. Once configured gear is gone,\n" +
                                 "monster part-separation profiles continue as usual.");
        }
        else if (config.KoStripOnHitEnabled)
        {
            config.KoStripOnHitEnabled = false;
            config.Save();
        }

        var syncWithRagdoll = config.KoStripSyncWithRagdoll;
        if (ImGui.Checkbox("Sync with ragdoll##armordetach_sync_ragdoll", ref syncWithRagdoll))
        {
            config.KoStripSyncWithRagdoll = syncWithRagdoll;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When Detach on KO is enabled, delay detachment by the player ragdoll activation delay.\n" +
                             "Disable this to detach immediately on death while ragdoll can still be delayed.");

        ImGui.Separator();
        ImGui.TextDisabled("Slots");

        ArmorDetachmentSlotCheckbox("Head", () => config.KoStripHead, v => config.KoStripHead = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("Body", () => config.KoStripBody, v => config.KoStripBody = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("Hands", () => config.KoStripHands, v => config.KoStripHands = v);
        ArmorDetachmentSlotCheckbox("Legs", () => config.KoStripLegs, v => config.KoStripLegs = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("Feet", () => config.KoStripFeet, v => config.KoStripFeet = v);

        ImGui.TextDisabled("Accessories");
        ArmorDetachmentSlotCheckbox("Ears", () => config.KoStripEars, v => config.KoStripEars = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("Neck", () => config.KoStripNeck, v => config.KoStripNeck = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("Wrists", () => config.KoStripWrists, v => config.KoStripWrists = v);
        ArmorDetachmentSlotCheckbox("R.Finger", () => config.KoStripRFinger, v => config.KoStripRFinger = v);
        ImGui.SameLine();
        ArmorDetachmentSlotCheckbox("L.Finger", () => config.KoStripLFinger, v => config.KoStripLFinger = v);

        ImGui.Separator();

        var physicsDrop = config.KoStripPhysicsDrop;
        if (ImGui.Checkbox("Physics drop: hat / accessories##armordetachdrop", ref physicsDrop))
        {
            config.KoStripPhysicsDrop = physicsDrop;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Let droppable hat/accessory slots (Head, Ears, Neck, Wrists, Rings)\n" +
                             "physically fall and tumble to the ground instead of just vanishing.");

        var physicsDropCloth = config.KoStripPhysicsDropClothing;
        if (ImGui.Checkbox("Physics drop: clothing##armordetachdropcloth", ref physicsDropCloth))
        {
            config.KoStripPhysicsDropClothing = physicsDropCloth;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drop supported clothing slots (Body / Legs) as falling shells. Work in progress: the\n" +
                             "equipment model bakes in body skin, so the dropped shell still carries\n" +
                             "a layer of skin.");

        var advancedCloth = config.KoStripAdvancedClothPhysics;
        ImGui.BeginDisabled(!config.KoStripPhysicsDropClothing);
        if (ImGui.Checkbox("Advanced clothing settle##armordetachclothadvanced", ref advancedCloth))
        {
            config.KoStripAdvancedClothPhysics = advancedCloth;
            config.Save();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Optional polish for Body / Legs physics drop: short visual follow on the\n" +
                             "dying body, stronger contact friction/damping, and delayed collapse until\n" +
                             "the garment is closer to rest. Default off.");

        ImGui.BeginDisabled(!config.KoStripPhysicsDropClothing || !config.KoStripAdvancedClothPhysics);
        var tubeModel = config.KoStripGarmentTubeModel;
        if (ImGui.Checkbox("Tube model (Body / Legs, experimental)##armordetachtube", ref tubeModel))
        {
            config.KoStripGarmentTubeModel = tubeModel;
            config.Save();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Experimental: drive the Body and Legs garments with a ring-tube physics model\n" +
                             "that wraps the body, so the garment slides down off the corpse instead of\n" +
                             "folding. Host ragdoll only; falls back to the chain rig otherwise. Default off.");

        ImGui.BeginDisabled(!config.KoStripPhysicsDropClothing);
        var followsBody = config.KoStripGarmentFollowsBody;
        if (ImGui.Checkbox("Still-attached pieces follow the body##armordetachfollow", ref followsBody))
        {
            config.KoStripGarmentFollowsBody = followsBody;
            config.Save();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Anything still on the body travels with it when the body is moved as a whole.\n" +
                             "Anything already detached stays where it fell. Default on.");

        ImGui.BeginDisabled(!config.KoStripGarmentTubeModel);
        var tubeDebug = config.KoStripGarmentTubeDebugDraw;
        if (ImGui.Checkbox("Tube debug wireframe##armordetachtubedebug", ref tubeDebug))
        {
            config.KoStripGarmentTubeDebugDraw = tubeDebug;
            config.Save();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Draw the tube's ring bodies as a wireframe to see how the physics model behaves.");

        ImGui.BeginDisabled(!config.KoStripGarmentTubeModel);
        ImGui.SetNextItemWidth(200f);
        var tubeBodyFriction = config.KoStripGarmentTubeBodyFriction;
        if (ImGui.SliderFloat("Tube body friction##armordetachtubebodyfriction", ref tubeBodyFriction, 0.1f, 10f, "%.2f"))
        {
            config.KoStripGarmentTubeBodyFriction = tubeBodyFriction;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Friction between the tube and the corpse. Higher = the shirt clings and\n" +
                             $"slides down more slowly. Default {Configuration.KoStripGarmentTubeBodyFrictionDefault:0.00}.");

        ImGui.SetNextItemWidth(200f);
        var tubeGroundFriction = config.KoStripGarmentTubeGroundFriction;
        if (ImGui.SliderFloat("Tube ground friction##armordetachtubegroundfriction", ref tubeGroundFriction, 0.1f, 10f, "%.2f"))
        {
            config.KoStripGarmentTubeGroundFriction = tubeGroundFriction;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Friction between the tube and the ground once it slides off the body.\n" +
                             $"Default {Configuration.KoStripGarmentTubeGroundFrictionDefault:0.00}.");

        ImGui.SetNextItemWidth(200f);
        var tubeHoldSeconds = config.KoStripGarmentTubeHoldSeconds;
        if (ImGui.SliderFloat("Tube handoff delay##armordetachtubehold", ref tubeHoldSeconds, 0f, 10f, "%.2f s"))
        {
            config.KoStripGarmentTubeHoldSeconds = Math.Clamp(tubeHoldSeconds, 0f, 10f);
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("How long the tube stays visually bound to the body pose before physics takes\n" +
                             $"over. Default {Configuration.KoStripGarmentTubeHoldSecondsDefault:0.00}s.");

        if (ImGui.Button("Reset tube tuning##armordetachtubereset"))
        {
            config.KoStripGarmentTubeBodyFriction = Configuration.KoStripGarmentTubeBodyFrictionDefault;
            config.KoStripGarmentTubeGroundFriction = Configuration.KoStripGarmentTubeGroundFrictionDefault;
            config.KoStripGarmentTubeHoldSeconds = Configuration.KoStripGarmentTubeHoldSecondsDefault;
            config.Save();
        }
        ImGui.EndDisabled();

        if (config.KoStripGarmentTubeModel)
            ImGui.TextDisabled("Tube model uses 'Tube handoff delay' above —\nthe cloth hold profile below is inactive.");

        // The cloth hold profile / delay only governs the chain rig. When the tube model is on it takes
        // over the handoff timing entirely (see ShouldReleaseGarmentBind), so grey these out to avoid the
        // "adjusting this does nothing" confusion.
        ImGui.BeginDisabled(!config.KoStripPhysicsDropClothing || !config.KoStripAdvancedClothPhysics
            || config.KoStripGarmentTubeModel);
        var clothHoldAuto = config.KoStripClothHoldAuto;
        if (ImGui.Checkbox("Auto cloth hold##armordetachclothholdauto", ref clothHoldAuto))
        {
            config.KoStripClothHoldAuto = clothHoldAuto;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Automatically release the garment when the source body settles, or in\n" +
                             "Slide to floor mode when the garment reaches the ground. Turn off to use\n" +
                             "the manual hold timer below.");

        if (config.KoStripClothHoldAuto)
        {
            var preset = Math.Clamp(config.KoStripClothHoldPreset, 0, ArmorDetachmentClothHoldPresetLabels.Length - 1);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Cloth hold preset##armordetachclothholdpreset", ref preset,
                    ArmorDetachmentClothHoldPresetLabels, ArmorDetachmentClothHoldPresetLabels.Length))
            {
                config.KoStripClothHoldPreset = preset;
                config.Save();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Quick: release soon after the body rests.\n" +
                                 "Natural: a short settling dwell.\n" +
                                 "Clingy: waits longer and follows dragged bodies.\n" +
                                 "Slide to floor: default, keeps sliding down until it touches the ground, then drops.\n" +
                                 "Visual only: slowly slides to the floor and stays visual, never handing off to physics.");

            // Visual-only slide tuning (preset index 4). Only this preset uses these; slide-to-floor is fixed.
            if (preset == 4)
            {
                ImGui.Indent();

                var vSlideDist = config.KoStripClothVisualOnlySlideDistance;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat("Visual-only slide distance##armordetachvisualslidedist", ref vSlideDist, 0.2f, 3.0f, "%.2f m"))
                {
                    config.KoStripClothVisualOnlySlideDistance = vSlideDist;
                    config.Save();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("How far the garment slides down the body before it freezes (or until it\n" +
                                     "reaches the ground). Raise it if the garment stops short in a standing KO.\n" +
                                     $"Default {Configuration.KoStripClothVisualOnlySlideDistanceDefault:0.00}m.");

                var vSlideSpeed = config.KoStripClothVisualOnlySlideSpeed;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat("Visual-only slide speed##armordetachvisualslidespeed", ref vSlideSpeed, 0.02f, 0.5f, "%.2f m/s"))
                {
                    config.KoStripClothVisualOnlySlideSpeed = vSlideSpeed;
                    config.Save();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("How fast the garment slides down. Raise it if the slide looks too slow.\n" +
                                     $"Default {Configuration.KoStripClothVisualOnlySlideSpeedDefault:0.00} m/s.");

                if (ImGui.Button("Reset##armordetachvisualslidereset"))
                {
                    config.KoStripClothVisualOnlySlideDistance = Configuration.KoStripClothVisualOnlySlideDistanceDefault;
                    config.KoStripClothVisualOnlySlideSpeed = Configuration.KoStripClothVisualOnlySlideSpeedDefault;
                    config.Save();
                }

                ImGui.Unindent();
            }
        }
        else
        {
            var clothHold = config.KoStripClothHoldSeconds;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat("Manual cloth hold##armordetachclothhold", ref clothHold, 0f, 20f, "%.1f s"))
            {
                config.KoStripClothHoldSeconds = Math.Clamp(MathF.Round(clothHold * 10f) / 10f, 0f, 20f);
                config.Save();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("How long the garment stays visually attached to the dying body before it\n" +
                                 "drops as a free rigid body. 0 = drop immediately.\n" +
                                 $"Default {Configuration.KoStripClothHoldSecondsDefault:0.00}s.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset##armordetachclothholdreset"))
        {
            config.KoStripClothHoldAuto = true;
            config.KoStripClothHoldPreset = 3;
            config.KoStripClothHoldSeconds = Configuration.KoStripClothHoldSecondsDefault;
            config.Save();
        }
        ImGui.EndDisabled();

        ImGui.Separator();

        ImGui.TextDisabled("Collapse on drop");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Per-slot: checked = the dropped piece deflates/flattens like cloth.\n" +
                             "Unchecked = it keeps its full rigid shape (better for armor / rigid gear).\n" +
                             "Only affects physically-dropped pieces. Default: clothing collapses,\n" +
                             "accessories stay rigid.");

        var anyPhysicsDrop = config.KoStripPhysicsDrop || config.KoStripPhysicsDropClothing;
        ImGui.BeginDisabled(!anyPhysicsDrop);

        ImGui.TextDisabled("Clothing");
        ArmorDetachmentCollapseCheckbox("Head", () => config.KoStripCollapseHead, v => config.KoStripCollapseHead = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("Body", () => config.KoStripCollapseBody, v => config.KoStripCollapseBody = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("Hands", () => config.KoStripCollapseHands, v => config.KoStripCollapseHands = v);
        ArmorDetachmentCollapseCheckbox("Legs", () => config.KoStripCollapseLegs, v => config.KoStripCollapseLegs = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("Feet", () => config.KoStripCollapseFeet, v => config.KoStripCollapseFeet = v);

        ImGui.TextDisabled("Accessories");
        ArmorDetachmentCollapseCheckbox("Ears", () => config.KoStripCollapseEars, v => config.KoStripCollapseEars = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("Neck", () => config.KoStripCollapseNeck, v => config.KoStripCollapseNeck = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("Wrists", () => config.KoStripCollapseWrists, v => config.KoStripCollapseWrists = v);
        ArmorDetachmentCollapseCheckbox("R.Finger", () => config.KoStripCollapseRFinger, v => config.KoStripCollapseRFinger = v);
        ImGui.SameLine();
        ArmorDetachmentCollapseCheckbox("L.Finger", () => config.KoStripCollapseLFinger, v => config.KoStripCollapseLFinger = v);

        if (ImGui.Button("Reset collapse defaults##armordetachcollapsereset"))
        {
            config.ResetKoStripCollapseDefaults();
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Restore defaults: clothing collapses, accessories stay rigid.");

        ImGui.EndDisabled();

        ImGui.Separator();

        if (ImGui.Button("Detach Now##armordetach"))
        {
            var player = CombatSimulator.Core.Services.ObjectTable.LocalPlayer;
            if (player != null) ctrl.StripNow(player.Address);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Detach configured armor from your character right now (test).");

        ImGui.End();
    }

    private void ArmorDetachmentSlotCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (ImGui.Checkbox($"{label}##armorDetachSlot", ref v))
        {
            set(v);
            config.Save();
        }
    }

    private void ArmorDetachmentCollapseCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (ImGui.Checkbox($"{label}##armorDetachCollapse", ref v))
        {
            set(v);
            config.Save();
        }
    }
}
