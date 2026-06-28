using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

// Experimental dev config sections: the "Dev (Experimental)" panel (incl. the menu-entry / easter-egg
// unlock state) and the PC dismember panel. Partial of MainWindow so they keep access to MainWindow
// internals while living in the experimental module folder, hidden from the public source set.
public partial class MainWindow
{
    private Dev.VictorySequenceGui victorySequenceGui = null!;

    // Dev easter egg state (private to the experimental module — not present in public builds).
    private int devClickCount = 0;
    private bool devUnlocked = false;

    // Called from the MainWindow constructor (no-op when this module is absent).
    partial void InitDevExperimental()
        => victorySequenceGui = new Dev.VictorySequenceGui(config, npcSelector, log);

    partial void DrawPcDismemberSection()
    {
        if (!devUnlocked || !ImGui.CollapsingHeader("PC Dismemberment"))
            return;

        HelpMarker("Proof of concept: while the player ragdoll is active (on death), collapse each selected part's bones so it vanishes from the body. Multi-select. Validates the 'hide' step before the separate rolling limb prop is added. Trigger a death to see it.");
        for (int i = 0; i < DismemberParts.Length; i++)
        {
            var (label, bone) = DismemberParts[i];
            if (label.StartsWith("R ")) ImGui.SameLine();
            var on = config.DismemberPocBones.Contains(bone);
            if (ImGui.Checkbox(label + "##dismember", ref on))
            {
                if (on)
                {
                    if (!config.DismemberPocBones.Contains(bone))
                        config.DismemberPocBones.Add(bone);
                }
                else
                {
                    config.DismemberPocBones.Remove(bone);
                }
                config.Save();
                SyncDynamicDismemberSelection();
            }
        }

        ImGui.Separator();
        var rollaway = config.EnableDismemberRollaway;
        if (ImGui.Checkbox("Limb rolls away (clone)", ref rollaway))
        {
            config.EnableDismemberRollaway = rollaway;
            config.Save();
            SyncDynamicDismemberSelection();
        }
        HelpMarker("Also spawn a clone of you showing ONLY each severed limb, which tumbles to the ground (full appearance + live Glamourer if installed). Off = just hide the limb on the body. POC: local player only; one clone per selected limb.");
    }

    private void SyncDynamicDismemberSelection()
    {
        if (!combatEngine.IsActive || combatEngine.State.PlayerState.IsAlive || !ragdollController.IsActive)
            return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        if (!config.EnableDismemberRollaway)
        {
            dismembermentController.RemoveFor(player.Address);
            return;
        }

        var playerIdx = player.ObjectIndex;
        var glam = glamourerIpc.GetStateBase64((int)playerIdx);
        dismembermentController.SyncSelectionFor(player.Address, config.DismemberPocBones, glam);
    }

    partial void DrawDevSection()
    {
        if (ImGui.CollapsingHeader("Dev (Experimental)"))
        {
            if (!devUnlocked)
            {
                ImGui.BeginDisabled();
                var dummy = false;
                ImGui.Checkbox("Unlock experimental features", ref dummy);
                ImGui.EndDisabled();

                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    var mousePos = ImGui.GetMousePos();
                    if (mousePos.X >= min.X && mousePos.X <= max.X &&
                        mousePos.Y >= min.Y && mousePos.Y <= max.Y)
                    {
                        devClickCount++;
                        if (devClickCount >= 13)
                            devUnlocked = true;
                    }
                }
                return;
            }

            ImGui.Indent();

            var showGrabToolbar = config.ShowGrabToolbar;
            if (ImGui.Checkbox("Show Grab Toolbar##dev", ref showGrabToolbar))
            {
                config.ShowGrabToolbar = showGrabToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar to toggle grab and tweak force/speed on the current victory cinema stage.");

            var showHoldToolbar = config.ShowHoldToolbar;
            if (ImGui.Checkbox("Show Hold Toolbar##dev", ref showHoldToolbar))
            {
                config.ShowHoldToolbar = showHoldToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar to toggle standing hold on the active ragdoll while an NPC continues attacking.");

            var showKoStripToolbar = config.ShowKoStripToolbar;
            if (ImGui.Checkbox("Show KO Strip Toolbar##dev", ref showKoStripToolbar))
            {
                config.ShowKoStripToolbar = showKoStripToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar for 'Strip KO': visually unequip selected gear slots on knockout (swap to smallclothes). Visual only, restored on reset.");

            var showMonsterToolbar = config.ShowMonsterToolbar;
            if (ImGui.Checkbox("Show Monster Toolbar##dev", ref showMonsterToolbar))
            {
                config.ShowMonsterToolbar = showMonsterToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar for 'Monster': on death, spawn a controllable no-HP creature (default Bat) — WASD move, A/D turn, Q/E up/down, attack key to punt the ragdoll.");

            var ragdollLog = config.RagdollVerboseLog;
            if (ImGui.Checkbox("Ragdoll Verbose Log##dev", ref ragdollLog))
            {
                config.RagdollVerboseLog = ragdollLog;
                config.Save();
            }

            var followPos = config.RagdollFollowPosition;
            if (ImGui.Checkbox("Ragdoll Follow Position##dev", ref followPos))
            {
                config.RagdollFollowPosition = followPos;
                config.Save();
            }
            HelpMarker("Update GameObject.Position to follow the ragdoll root bone each frame. Prevents character model from unloading on long falls (e.g., off cliffs). May have side effects.");

            var npcScale = config.DevNpcScale;
            if (ImGui.SliderFloat("NPC Scale##dev", ref npcScale, 0.1f, 3.0f, "%.2f"))
            {
                config.DevNpcScale = npcScale;
                config.Save();
            }
            HelpMarker("Scale all active target NPCs. Applied live each frame.");

            var occlusionHide = config.DevNpcOcclusionHide;
            if (ImGui.Checkbox("Hide Blocking NPCs##dev", ref occlusionHide))
            {
                config.DevNpcOcclusionHide = occlusionHide;
                config.Save();
            }
            HelpMarker("Hide active target NPCs that block the camera's view of the player. Only works with Active Camera enabled. Never hides the cinematic (grabbing) NPC.");
            if (config.DevNpcOcclusionHide)
            {
                ImGui.Indent();
                var occRadius = config.DevNpcOcclusionRadius;
                if (ImGui.SliderFloat("Occlusion Radius##dev", ref occRadius, 0.2f, 3.0f, "%.1f"))
                {
                    config.DevNpcOcclusionRadius = occRadius;
                    config.Save();
                }
                HelpMarker("How close an NPC must be to the camera-to-player line to be considered blocking.");
                ImGui.Unindent();
            }

            var companionAppearance = config.DevCompanionAppearanceVariant;
            if (ImGui.Checkbox("Ripoff Parties##dev", ref companionAppearance))
            {
                config.DevCompanionAppearanceVariant = companionAppearance;
                config.Save();
            }
            HelpMarker("Applies an alternate visual state to party companions after they are defeated. Reset restores the original appearance.");

            var partyApproachDebug = config.DevPartyApproachDebugLog;
            if (ImGui.Checkbox("Party Approach Debug Log##dev", ref partyApproachDebug))
            {
                config.DevPartyApproachDebugLog = partyApproachDebug;
                config.Save();
            }
            HelpMarker("Logs enemy party-approach plan, waypoint, steering, and range state about twice per second per enemy. Use briefly for 1v10 debugging.");

            var autoEngage = config.EnableNpcAutoEngage;
            if (ImGui.Checkbox("NPC Auto Engage##dev", ref autoEngage))
            {
                config.EnableNpcAutoEngage = autoEngage;
                config.Save();
            }
            HelpMarker("Selected enemy NPCs start attacking the player automatically on Start / Reset / Reboot, without the player having to attack them first.");
            if (config.EnableNpcAutoEngage)
            {
                ImGui.Indent();
                var autoEngageDelay = config.NpcAutoEngageDelay;
                if (ImGui.SliderFloat("Engage Delay##dev", ref autoEngageDelay, 0f, 20f, "%.1f s"))
                {
                    config.NpcAutoEngageDelay = autoEngageDelay;
                    config.Save();
                }
                HelpMarker("Seconds to wait after Start / Reset / Reboot before NPCs start attacking.");
                ImGui.Unindent();
            }

            victorySequenceGui.Draw();

            ImGui.Unindent();
        }
    }
}
