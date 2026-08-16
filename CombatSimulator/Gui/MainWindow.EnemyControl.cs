// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Recipes;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Gui;

// Enemy Control: post-defeat creature possession (spawn or take over the killer, walk it, grab-as-
// attack with finger conform, optional swarm formation). Moved out of the private Dev/Experimental
// module — entry point is DrawEnemyControlEntrySection (Effects tab); the window itself only opens
// when the player turns it on there, same pattern as Armor Detachment.
public partial class MainWindow
{
    // Short lists for the non-"any bone" grab pickers below: a handful of bones worth grabbing
    // most of the time, without opening the full skeleton dropdown.
    private static readonly string[] EnemyControlGrabPlayerBones =
        { "j_kubi", "j_sebo_c", "j_kosi", "j_kao", "j_ude_b_r", "j_ude_b_l" };
    private static readonly string[] EnemyControlGrabNpcBones = { "j_te_r", "j_te_l" };

    private void DrawEnemyControlEntrySection()
    {
        if (!ImGui.CollapsingHeader("Enemy Control"))
            return;

        var showControls = config.ShowEnemyControlGui;
        if (ImGui.Checkbox("Show Enemy Control GUI", ref showControls))
        {
            config.ShowEnemyControlGui = showControls;
            config.Save();
        }
        HelpMarker("Open the Enemy Control window: after a death, spawn or take over a controllable creature. " +
                   "Every Enemy Control feature (spawn/control-killer, swarm, grab-as-attack) stays off until you enable it there.");
    }

    private static readonly (string Name, uint Id, uint NameId)[] EnemyControlModels =
    {
        ("Bat", 38, 38),
        ("Cactuar", 3, 3),
        ("Hog", 15, 15),
        ("Imp", 21, 21),
        ("Flytrap", 23, 23),
        ("Tortoise", 34, 34),
        ("Wisp", 45, 45),
        ("Myconid", 48, 48),
        ("Striking Dummy", 541, 541),
    };

    private static readonly (string Name, int Key)[] EnemyControlAttackKeys =
    {
        ("Y", 0x59),
        ("F", 0x46),
        ("R", 0x52),
        ("C", 0x43),
        ("X", 0x58),
        ("Space", 0x20),
    };

    public void DrawEnemyControlWindow(EnemyControlController ctrl)
    {
        var showWindow = config.ShowEnemyControlGui;
        if (!ImGui.Begin("Enemy Control", ref showWindow,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (config.ShowEnemyControlGui != showWindow)
            {
                config.ShowEnemyControlGui = showWindow;
                config.Save();
            }
            ImGui.End();
            return;
        }

        if (config.ShowEnemyControlGui != showWindow)
        {
            config.ShowEnemyControlGui = showWindow;
            config.Save();
        }

        var active = ctrl.IsActive;

        var onDeath = config.EnemyControlSpawnOnDeath;
        if (ImGui.Checkbox("Spawn on death##EnemyControl", ref onDeath))
        {
            config.EnemyControlSpawnOnDeath = onDeath;
            if (onDeath) config.EnemyControlControlKiller = false; // mutually exclusive
            config.Save();
        }

        var controlKiller = config.EnemyControlControlKiller;
        if (ImGui.Checkbox("Control killer on death##EnemyControl", ref controlKiller))
        {
            config.EnemyControlControlKiller = controlKiller;
            if (controlKiller)
            {
                config.EnemyControlSpawnOnDeath = false; // can't spawn while controlling the killer
                ctrl.Despawn();                      // force-despawn any active EnemyControl
            }
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On death, take control of the enemy that just defeated you instead of\nspawning a creature. Same controls. Disables spawning while on.");

        var modelIdx = Array.FindIndex(EnemyControlModels, m => m.Id == config.EnemyControlModelId);
        if (modelIdx < 0) modelIdx = 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Model##EnemyControl", EnemyControlModels[modelIdx].Name))
        {
            for (int i = 0; i < EnemyControlModels.Length; i++)
                if (ImGui.Selectable(EnemyControlModels[i].Name, i == modelIdx))
                {
                    config.EnemyControlModelId = EnemyControlModels[i].Id;
                    config.EnemyControlModelNameId = EnemyControlModels[i].NameId;
                    config.Save();
                }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(active || config.EnemyControlControlKiller);
        if (ImGui.Button("Spawn##EnemyControl")) ctrl.Spawn();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!active);
        if (ImGui.Button("Despawn##EnemyControl")) ctrl.Despawn();
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!active);
        if (ImGui.Button($"Cam: {(ctrl.CameraFollowsControlledTarget ? "EnemyControl" : "Character")}##EnemyControl"))
            ctrl.ToggleCamera();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Switch the active camera between following the EnemyControl and your character.");

        ImGui.Separator();

        var moveSpeed = config.EnemyControlMoveSpeed;
        if (ImGui.SliderFloat("Move##EnemyControl", ref moveSpeed, 1f, 20f, "%.1f")) { config.EnemyControlMoveSpeed = moveSpeed; config.Save(); }

        var groundWalk = config.EnemyControlGroundWalk;
        if (ImGui.Checkbox("Walk on ground (no fly)##EnemyControl", ref groundWalk))
        {
            config.EnemyControlGroundWalk = groundWalk;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clamp the creature to the floor (raycast / navmesh) so it walks instead of flying.\nOff = free flight (Q/E or L2/R2 up/down).");

        ImGui.BeginDisabled(config.EnemyControlGroundWalk);
        var vSpeed = config.EnemyControlVerticalSpeed;
        if (ImGui.SliderFloat("Fly##EnemyControl", ref vSpeed, 1f, 15f, "%.1f")) { config.EnemyControlVerticalSpeed = vSpeed; config.Save(); }
        ImGui.EndDisabled();

        var keyIdx = Array.FindIndex(EnemyControlAttackKeys, k => k.Key == config.EnemyControlAttackKey);
        if (keyIdx < 0) keyIdx = 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Attack key##EnemyControl", EnemyControlAttackKeys[keyIdx].Name))
        {
            for (int i = 0; i < EnemyControlAttackKeys.Length; i++)
                if (ImGui.Selectable(EnemyControlAttackKeys[i].Name, i == keyIdx))
                { config.EnemyControlAttackKey = EnemyControlAttackKeys[i].Key; config.Save(); }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(config.EnemyControlAttackEnabled
                ? "Attack key plays a physical melee attack. With Swarm enabled, every follower attacks."
                : "Attack key toggles the configured grab. Enable Attack below to replace grab with melee.");

        DrawEnemyControlGrabAnimationPicker();
        DrawEnemyControlGrabPairsList(ctrl);
        DrawEnemyControlArmPoseSection();
        DrawEnemyControlGrabProfilesSection();
        DrawEnemyControlSwarmSection(ctrl);
        DrawEnemyControlAttackSection(ctrl);

        ImGui.TextDisabled("WASD = camera-relative move; Q/E = down/up");
        ImGui.TextDisabled("Gamepad: left stick = move; L2/R2 = down/up; Cross = attack");
        ImGui.TextDisabled(active ? "EnemyControl: active" : "EnemyControl: none");

        ImGui.End();
    }

    private void DrawEnemyControlSwarmSection(EnemyControlController ctrl)
    {
        if (!ImGui.CollapsingHeader("Swarm##EnemyControlswarm"))
            return;

        ImGui.Indent();
        var enabled = config.EnemyControlSwarmEnabled;
        if (ImGui.Checkbox("Enable swarm following##EnemyControlswarm", ref enabled))
        {
            config.EnemyControlSwarmEnabled = enabled;
            config.Save();
            ctrl.OnSwarmSettingChanged(enabled);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Selected living enemies stop their normal AI and follow randomized stable slots around\n" +
                             "the controlled EnemyControl. Turning this off immediately releases them back to AI.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!config.EnemyControlSwarmEnabled);
        if (ImGui.SmallButton("Reset##EnemyControlswarm"))
            ctrl.ResetSwarm();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hard-reset corpse assignments and support caches, then immediately redistribute all\n" +
                             "followers into fresh formation slots around the controlled EnemyControl.");

        var climbCorpses = config.EnemyControlSwarmClimbCorpses;
        if (ImGui.Checkbox("Swarm auto-climbs nearest corpse##EnemyControlswarm", ref climbCorpses))
        {
            config.EnemyControlSwarmClimbCorpses = climbCorpses;
            config.Save();
            ctrl.OnSwarmClimbSettingChanged(climbCorpses);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Followers independently choose the nearest active player, enemy, or party corpse at any\n" +
                             "distance. The controlled EnemyControl remains under free manual control.");

        ImGui.BeginDisabled(!config.EnemyControlSwarmEnabled || !config.EnemyControlSwarmClimbCorpses);
        ImGui.SetNextItemWidth(150);
        var climbDelay = config.EnemyControlSwarmClimbDelay;
        if (ImGui.SliderFloat("Climb delay##EnemyControlswarm", ref climbDelay, 0f, 5f, "%.2f s"))
        {
            config.EnemyControlSwarmClimbDelay = climbDelay;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Delay before followers begin reserving corpse landmarks after Swarm/climbing is enabled\n" +
                             "or Reset is pressed.");
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!config.EnemyControlSwarmEnabled || !config.EnemyControlSwarmClimbCorpses);
        var reposition = config.EnemyControlSwarmRepositionOnCorpse;
        if (ImGui.Checkbox("Followers occasionally change landmarks##EnemyControlswarm", ref reposition))
        {
            config.EnemyControlSwarmRepositionOnCorpse = reposition;
            config.Save();
            ctrl.OnSwarmRepositionSettingChanged(reposition);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Every several seconds, an occupied follower chooses another weighted landmark or a\n" +
                             "new small offset around it. Off keeps its assigned bone until it becomes invalid.");

        ImGui.BeginDisabled(!config.EnemyControlSwarmRepositionOnCorpse);
        ImGui.SetNextItemWidth(150);
        var landmarkFrequency = config.EnemyControlSwarmLandmarkChangeFrequency;
        if (ImGui.SliderFloat("Landmark change frequency##EnemyControlswarm", ref landmarkFrequency,
                0.25f, 4f, "%.2fx"))
        {
            var previousFrequency = config.EnemyControlSwarmLandmarkChangeFrequency;
            config.EnemyControlSwarmLandmarkChangeFrequency = landmarkFrequency;
            config.Save();
            ctrl.OnSwarmLandmarkChangeFrequencyChanged(previousFrequency, landmarkFrequency);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("1.00x is the original randomized 5-12 second change interval.\n" +
                             "Higher values change landmarks more often; lower values hold them longer.");
        ImGui.EndDisabled();

        var enhancedStomp = config.EnemyControlSwarmEnhancedStompTracking;
        if (ImGui.Checkbox("High-frequency stomp tracking##EnemyControlswarm", ref enhancedStomp))
        {
            config.EnemyControlSwarmEnhancedStompTracking = enhancedStomp;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Refreshes terrain roughly four times faster, samples the approach path more densely,\n" +
                             "and corrects root height faster while followers occupy corpse landmarks. No foot IK.");

        ImGui.SetNextItemWidth(150);
        var corpseMass = config.EnemyControlSwarmStompCorpseMassMultiplier;
        if (ImGui.SliderFloat("Stomp corpse weight##EnemyControlswarm", ref corpseMass, 1f, 12f, "%.1fx"))
        {
            config.EnemyControlSwarmStompCorpseMassMultiplier = corpseMass;
            config.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Temporarily multiplies the selected corpse's physical mass only while swarm feet are\n" +
                             "supported by it. This reduces corpse shoving without changing normal ragdoll motion.");
        ImGui.EndDisabled();

        ImGui.SetNextItemWidth(150);
        var distance = config.EnemyControlSwarmFollowDistance;
        if (ImGui.SliderFloat("Follow distance##EnemyControlswarm", ref distance, 0.35f, 12f, "%.2f m"))
        {
            config.EnemyControlSwarmFollowDistance = distance;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Radius of the randomized formation around the controlled EnemyControl.");

        ImGui.SetNextItemWidth(150);
        var compactness = config.EnemyControlSwarmCompactness;
        if (ImGui.SliderFloat("Compactness##EnemyControlswarm", ref compactness, 0f, 1f, "%.2f"))
        {
            config.EnemyControlSwarmCompactness = compactness;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Higher values hold formation more tightly, reduce random drift, and catch up faster.\n" +
                             "Lower values produce a looser, more organic swarm.");

        ImGui.TextDisabled($"Followers: {ctrl.SwarmFollowerCount} | On corpse: {ctrl.SwarmCorpseOccupantCount}");
        ImGui.Unindent();
    }

    private void DrawEnemyControlAttackSection(EnemyControlController ctrl)
    {
        if (!ImGui.CollapsingHeader("Attack##EnemyControlattack"))
            return;

        ImGui.Indent();
        var enabled = config.EnemyControlAttackEnabled;
        if (ImGui.Checkbox("Attack instead of grab##EnemyControlattack", ref enabled))
        {
            config.EnemyControlAttackEnabled = enabled;
            config.Save();
            ctrl.OnAttackModeChanged(enabled);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the configured attack key plays a melee swing instead of toggling grab.\n" +
                             "With Swarm enabled, the controlled EnemyControl and every follower swing together.");

        ImGui.SetNextItemWidth(150);
        var strength = config.EnemyControlAttackStrength;
        if (ImGui.SliderFloat("Corpse reaction##EnemyControlattack", ref strength, 0f, 0.25f, "%.3f"))
        {
            config.EnemyControlAttackStrength = strength;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Extra impulse from measured limb speed. The default 0.015 is deliberately gentle.\n" +
                             "0 keeps animation and direct collider contact but adds no strike impulse. Group attacks\n" +
                             "share one hit budget per corpse body, so follower count does not multiply the force.");

        ImGui.Unindent();
    }

    private uint[]? EnemyControlGrabEmoteIds;
    private string[]? EnemyControlGrabEmoteNames;
    private string EnemyControlGrabEmoteFilter = string.Empty;

    /// <summary>
    /// Every real Emote, sorted alphabetically, plus a "Default" entry standing in for the built-in
    /// grab-and-hold pose (normal/aettouch_loop) — that one isn't a proper Emote sheet row, so it
    /// can't appear in the sheet scan and gets added by hand instead. Same shape as
    /// VictorySequenceGui.EnsureEmoteCache; searchable via the filter box in the combo below (that
    /// part VictorySequenceGui's own combo doesn't have).
    /// </summary>
    private void EnsureEnemyControlGrabEmoteCache()
    {
        if (EnemyControlGrabEmoteIds != null) return;

        var ids = new List<uint> { 0 };
        var names = new List<string> { "Default (grab-and-hold pose)" };
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet != null)
            {
                var emotes = new List<(uint Id, string Name)>();
                foreach (var emote in sheet)
                {
                    var name = emote.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        emotes.Add((emote.RowId, name));
                }
                emotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var e in emotes) { ids.Add(e.Id); names.Add(e.Name); }
            }
        }
        catch { }

        EnemyControlGrabEmoteIds = ids.ToArray();
        EnemyControlGrabEmoteNames = names.ToArray();
    }

    /// <summary>
    /// Which animation the creature plays while holding a grab. Defaults to the built-in pose; any
    /// other pick is a real emote, resolved to its own loop ActionTimeline (see
    /// EnemyControlController.ResolveGrabAnimationTimeline) the same way Victory Sequence resolves an
    /// emote to a timeline (VictorySequenceGui.ResolveEmoteTimelines) — just searchable here.
    /// </summary>
    private void DrawEnemyControlGrabAnimationPicker()
    {
        EnsureEnemyControlGrabEmoteCache();

        var idx = Array.IndexOf(EnemyControlGrabEmoteIds!, config.EnemyControlGrabEmoteId);
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Grab animation##EnemyControlgrabemote", EnemyControlGrabEmoteNames![idx]))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##EnemyControlGrabEmoteFilter", "search", ref EnemyControlGrabEmoteFilter, 32);

            for (int i = 0; i < EnemyControlGrabEmoteNames.Length; i++)
            {
                if (EnemyControlGrabEmoteFilter.Length > 0 &&
                    EnemyControlGrabEmoteNames[i].IndexOf(EnemyControlGrabEmoteFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (ImGui.Selectable(EnemyControlGrabEmoteNames[i], i == idx))
                { config.EnemyControlGrabEmoteId = EnemyControlGrabEmoteIds![i]; config.Save(); }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which animation the creature plays while holding the grab. \"Default\" is the built-in\n" +
                             "grab-and-hold pose; anything else is an emote's own loop animation, searchable by name.");
    }

    private string newEnemyControlGrabProfileName = "";
    private int selectedEnemyControlGrabProfileIndex = -1;
    private bool EnemyControlGrabProfileOverwritePopupOpen = false;
    private string EnemyControlGrabProfileOverwriteTarget = "";

    /// <summary>
    /// Save/load/delete a named snapshot of the whole grab setup below (pairs, fitting, arm pose).
    /// Interaction mirrors DrawRagdollBoneProfilesSection (Ragdoll's bone-profile list).
    /// </summary>
    private void DrawEnemyControlGrabProfilesSection()
    {
        if (!ImGui.CollapsingHeader("Grab Profiles##EnemyControlGrabProfiles"))
            return;

        var profiles = config.EnemyControlGrabProfiles;
        var profileNames = new string[profiles.Count];
        for (int i = 0; i < profiles.Count; i++)
            profileNames[i] = profiles[i].Name;

        var hasSelection = selectedEnemyControlGrabProfileIndex >= 0 && selectedEnemyControlGrabProfileIndex < profiles.Count;

        if (ImGui.BeginListBox("##EnemyControlGrabProfileSelect",
                new Vector2(250, ImGui.GetTextLineHeightWithSpacing() * 4 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            for (int i = 0; i < profileNames.Length; i++)
            {
                bool isSelected = selectedEnemyControlGrabProfileIndex == i;
                if (ImGui.Selectable(profileNames[i], isSelected))
                    selectedEnemyControlGrabProfileIndex = i;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndListBox();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load##EnemyControlGrabProfile") && hasSelection)
            LoadEnemyControlGrabProfile(profiles[selectedEnemyControlGrabProfileIndex]);

        ImGui.SameLine();
        if (ImGui.Button("Overwrite##EnemyControlGrabProfile") && hasSelection)
        {
            EnemyControlGrabProfileOverwriteTarget = profiles[selectedEnemyControlGrabProfileIndex].Name;
            EnemyControlGrabProfileOverwritePopupOpen = true;
            ImGui.OpenPopup("Confirm Overwrite##EnemyControlGrabProfileOverwrite");
        }

        ImGui.SameLine();
        var io = ImGui.GetIO();
        bool ctrlShiftHeld = io.KeyCtrl && io.KeyShift;
        if (!ctrlShiftHeld)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Delete##EnemyControlGrabProfile");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl+Shift to enable delete.");
        }
        else if (ImGui.Button("Delete##EnemyControlGrabProfile") && hasSelection)
        {
            profiles.RemoveAt(selectedEnemyControlGrabProfileIndex);
            selectedEnemyControlGrabProfileIndex = Math.Min(selectedEnemyControlGrabProfileIndex, profiles.Count - 1);
            config.Save();
        }

        ImGui.SetNextItemWidth(250);
        ImGui.InputText("##EnemyControlGrabProfileName", ref newEnemyControlGrabProfileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save Profile##EnemyControlGrabProfile") && newEnemyControlGrabProfileName.Length > 0)
        {
            var existingIdx = profiles.FindIndex(p =>
                p.Name.Equals(newEnemyControlGrabProfileName, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                EnemyControlGrabProfileOverwriteTarget = newEnemyControlGrabProfileName;
                EnemyControlGrabProfileOverwritePopupOpen = true;
                ImGui.OpenPopup("Confirm Overwrite##EnemyControlGrabProfileOverwrite");
            }
            else
            {
                SaveEnemyControlGrabProfile(newEnemyControlGrabProfileName);
                newEnemyControlGrabProfileName = "";
            }
        }

        // Overwrite confirmation popup
        if (ImGui.BeginPopupModal("Confirm Overwrite##EnemyControlGrabProfileOverwrite", ref EnemyControlGrabProfileOverwritePopupOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text($"Overwrite profile \"{EnemyControlGrabProfileOverwriteTarget}\"?");
            ImGui.Spacing();

            if (ImGui.Button("Yes", new Vector2(80, 0)))
            {
                SaveEnemyControlGrabProfile(EnemyControlGrabProfileOverwriteTarget);
                newEnemyControlGrabProfileName = "";
                EnemyControlGrabProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("No", new Vector2(80, 0)))
            {
                EnemyControlGrabProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void SaveEnemyControlGrabProfile(string name)
    {
        var snapshot = new EnemyControlGrabProfile { Name = name };
        foreach (var pair in config.EnemyControlGrabPairs)
            snapshot.Pairs.Add(new EnemyControlGrabPairConfig { NpcBone = pair.NpcBone, PlayerBone = pair.PlayerBone });
        snapshot.GrabEmoteId = config.EnemyControlGrabEmoteId;
        snapshot.GrabAnyBone = config.EnemyControlGrabAnyBone;
        snapshot.GrabAnyNpcBone = config.EnemyControlGrabAnyNpcBone;
        snapshot.GrabRigid = config.EnemyControlGrabRigid;
        snapshot.GrabSoftContact = config.EnemyControlGrabSoftContact;
        snapshot.GrabGripReach = config.EnemyControlGrabGripReach;
        snapshot.GrabServoForce = config.EnemyControlGrabServoForce;
        snapshot.GrabServoSpeed = config.EnemyControlGrabServoSpeed;
        snapshot.GrabServoFrequency = config.EnemyControlGrabServoFrequency;
        snapshot.GrabConformFingers = config.EnemyControlGrabConformFingers;
        snapshot.GrabFingerStrength = config.EnemyControlGrabFingerStrength;
        snapshot.GrabFingerMaxAngle = config.EnemyControlGrabFingerMaxAngle;
        snapshot.GrabFingerClearance = config.EnemyControlGrabFingerClearance;
        snapshot.ArmPoseEnabled = config.EnemyControlArmPoseEnabled;
        snapshot.LeftArmUpperPitch = config.EnemyControlLeftArmUpperPitch;
        snapshot.LeftArmUpperYaw = config.EnemyControlLeftArmUpperYaw;
        snapshot.LeftArmUpperRoll = config.EnemyControlLeftArmUpperRoll;
        snapshot.LeftArmLowerPitch = config.EnemyControlLeftArmLowerPitch;
        snapshot.LeftArmLowerYaw = config.EnemyControlLeftArmLowerYaw;
        snapshot.LeftArmLowerRoll = config.EnemyControlLeftArmLowerRoll;
        snapshot.RightArmUpperPitch = config.EnemyControlRightArmUpperPitch;
        snapshot.RightArmUpperYaw = config.EnemyControlRightArmUpperYaw;
        snapshot.RightArmUpperRoll = config.EnemyControlRightArmUpperRoll;
        snapshot.RightArmLowerPitch = config.EnemyControlRightArmLowerPitch;
        snapshot.RightArmLowerYaw = config.EnemyControlRightArmLowerYaw;
        snapshot.RightArmLowerRoll = config.EnemyControlRightArmLowerRoll;

        var existing = config.EnemyControlGrabProfiles.FindIndex(p => p.Name == name);
        if (existing >= 0)
            config.EnemyControlGrabProfiles[existing] = snapshot;
        else
            config.EnemyControlGrabProfiles.Add(snapshot);

        config.Save();
        chatGui.Print($"[CombatSim] EnemyControl grab profile '{name}' saved.");
    }

    private void LoadEnemyControlGrabProfile(EnemyControlGrabProfile p)
    {
        config.EnemyControlGrabPairs.Clear();
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in p.Pairs)
        {
            var npcBone = pair.NpcBone.Trim();
            var playerBone = pair.PlayerBone.Trim();
            if (seenPairs.Add($"{npcBone}\u001f{playerBone}"))
                config.EnemyControlGrabPairs.Add(new EnemyControlGrabPairConfig { NpcBone = npcBone, PlayerBone = playerBone });
        }
        if (config.EnemyControlGrabPairs.Count == 0)
            config.EnemyControlGrabPairs.Add(new EnemyControlGrabPairConfig());
        config.EnemyControlGrabEmoteId = p.GrabEmoteId;
        config.EnemyControlGrabAnyBone = p.GrabAnyBone;
        config.EnemyControlGrabAnyNpcBone = p.GrabAnyNpcBone;
        config.EnemyControlGrabRigid = p.GrabRigid;
        config.EnemyControlGrabSoftContact = p.GrabSoftContact;
        config.EnemyControlGrabGripReach = p.GrabGripReach;
        config.EnemyControlGrabServoForce = p.GrabServoForce;
        config.EnemyControlGrabServoSpeed = p.GrabServoSpeed;
        config.EnemyControlGrabServoFrequency = p.GrabServoFrequency;
        config.EnemyControlGrabConformFingers = p.GrabConformFingers;
        config.EnemyControlGrabFingerStrength = p.GrabFingerStrength;
        config.EnemyControlGrabFingerMaxAngle = p.GrabFingerMaxAngle;
        config.EnemyControlGrabFingerClearance = p.GrabFingerClearance;
        config.EnemyControlArmPoseEnabled = p.ArmPoseEnabled;
        config.EnemyControlLeftArmUpperPitch = p.LeftArmUpperPitch;
        config.EnemyControlLeftArmUpperYaw = p.LeftArmUpperYaw;
        config.EnemyControlLeftArmUpperRoll = p.LeftArmUpperRoll;
        config.EnemyControlLeftArmLowerPitch = p.LeftArmLowerPitch;
        config.EnemyControlLeftArmLowerYaw = p.LeftArmLowerYaw;
        config.EnemyControlLeftArmLowerRoll = p.LeftArmLowerRoll;
        config.EnemyControlRightArmUpperPitch = p.RightArmUpperPitch;
        config.EnemyControlRightArmUpperYaw = p.RightArmUpperYaw;
        config.EnemyControlRightArmUpperRoll = p.RightArmUpperRoll;
        config.EnemyControlRightArmLowerPitch = p.RightArmLowerPitch;
        config.EnemyControlRightArmLowerYaw = p.RightArmLowerYaw;
        config.EnemyControlRightArmLowerRoll = p.RightArmLowerRoll;
        config.Save();
        chatGui.Print($"[CombatSim] EnemyControl grab profile '{p.Name}' loaded.");
    }

    private string EnemyControlGrabBoneFilter = string.Empty;

    /// <summary>
    /// One grab pair per row: which of the creature's own bones grabs, and which of the grabbed
    /// body's bones it catches. Any number of pairs can be held at once — e.g. one hand on the neck,
    /// the other on the pelvis, for a carry/lift pose — so this is a growable list rather than the
    /// fixed single pair it used to be.
    /// </summary>
    private void DrawEnemyControlGrabPairsList(EnemyControlController ctrl)
    {
        ImGui.Text("Grab pairs");

        ImGui.SameLine();
        var anyNpcBone = config.EnemyControlGrabAnyNpcBone;
        if (ImGui.Checkbox("Any npc##EnemyControlGrabAnyNpcBone", ref anyNpcBone))
        { config.EnemyControlGrabAnyNpcBone = anyNpcBone; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose each pair's grabbing bone from the creature's own skeleton rather than assuming\nit has hands. Applies to every row below.");

        ImGui.SameLine();
        var anyBone = config.EnemyControlGrabAnyBone;
        if (ImGui.Checkbox("Any player##EnemyControlGrabAnyBone", ref anyBone))
        { config.EnemyControlGrabAnyBone = anyBone; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick each pair's grabbed bone from every bone on the body rather than the usual few.\n" +
                             "Grabbing an extremity is a very different physical problem from grabbing a throat —\n" +
                             "expect to retune Grip depth, and a light bone may simply be dragged along behind.\n" +
                             "Applies to every row below.");

        var pairs = config.EnemyControlGrabPairs;
        var removeIndex = -1;
        for (int i = 0; i < pairs.Count; i++)
        {
            ImGui.PushID(i);
            var pair = pairs[i];

            DrawEnemyControlGrabbingBonePicker(ctrl, pair);
            ImGui.SameLine();
            ImGui.Text("->");
            ImGui.SameLine();
            DrawEnemyControlGrabbedBonePicker(pair);

            ImGui.SameLine();
            if (ImGui.Button("X##EnemyControlGrabRemovePair"))
                removeIndex = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove this pair.");

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            pairs.RemoveAt(removeIndex);
            config.Save();
        }

        if (ImGui.Button("+ Add pair##EnemyControlGrabAddPair"))
        {
            // Alternate the default grabbing hand so a fresh two-pair "hold with both hands" setup
            // is one click away.
            var npcBone = pairs.Count % 2 == 0 ? "j_te_r" : "j_te_l";
            if (pairs.Exists(p => string.Equals(p.NpcBone, npcBone, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(p.PlayerBone, "j_kubi", StringComparison.OrdinalIgnoreCase)))
            {
                chatGui.Print("[CombatSim] That grab pair already exists; edit an existing row before adding another.");
            }
            else
            {
                pairs.Add(new EnemyControlGrabPairConfig { NpcBone = npcBone, PlayerBone = "j_kubi" });
                config.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add another grab pair. Any number can be held at once — e.g. one hand on the neck\n" +
                             "and the other on the pelvis for a carry/lift pose.");
    }

    /// <summary>
    /// Which bone of the grabbed body this pair's hand takes hold of. The short list is the handful
    /// worth grabbing most of the time; the shared "Any player" toggle above opens it up to every bone
    /// the ragdoll has a body for, which is exactly the set the constraint can resolve — offering more
    /// than that would just fail at grab time.
    /// </summary>
    private void DrawEnemyControlGrabbedBonePicker(EnemyControlGrabPairConfig pair)
    {
        if (!config.EnemyControlGrabAnyBone)
        {
            var idx = Array.IndexOf(EnemyControlGrabPlayerBones, pair.PlayerBone);
            if (idx < 0) idx = 0;
            ImGui.SetNextItemWidth(80);
            if (ImGui.Combo("##EnemyControlGrabPlayer", ref idx, EnemyControlGrabPlayerBones, EnemyControlGrabPlayerBones.Length))
            { pair.PlayerBone = EnemyControlGrabPlayerBones[idx]; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Grabbed bone");
        }
        else
        {
            var bones = GrabbableBones();
            ImGui.SetNextItemWidth(130);
            if (ImGui.BeginCombo("##EnemyControlGrabPlayerAny", pair.PlayerBone))
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##EnemyControlGrabBoneFilter", "filter", ref EnemyControlGrabBoneFilter, 32);

                foreach (var bone in bones)
                {
                    if (EnemyControlGrabBoneFilter.Length > 0 &&
                        bone.IndexOf(EnemyControlGrabBoneFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (ImGui.Selectable(bone, bone == pair.PlayerBone))
                    { pair.PlayerBone = bone; config.Save(); }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Grabbed bone — every bone on the body's skeleton, not merely the forty-odd the ragdoll\n" +
                                 "gives a body of its own. Ears, horns, tails, fingers, toes: a bone with no body is one that\n" +
                                 "is rigidly carried by something that has one, so the grab takes hold of the carrier at the\n" +
                                 "point where that bone sits. Pull a corpse by the ear and the head comes round by its ear.\n\n" +
                                 "Takes effect on the next grab, not the one already held.");
        }
    }

    /// <summary>
    /// Every bone on the grabbed body — the real skeleton, ears and all, not the forty-odd the ragdoll
    /// builds bodies for. A bone without a body is not ungrabbable: it has no body precisely BECAUSE it
    /// is rigidly carried by one that does, so the grab holds the carrier at the offset where the bone
    /// sits (see RagdollController.TryResolveGrabAttachment).
    ///
    /// Falls back to the bone table before there is a corpse to read a skeleton off.
    /// </summary>
    private IReadOnlyList<string> GrabbableBones()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var live = ragdollController.GetGrabbableBoneNames(player.Address);
            if (live.Count > 0) return live;
        }

        var pending = new List<string>();
        foreach (var bone in config.RagdollBoneConfigs)
            pending.Add(bone.Name);
        return pending;
    }

    private string EnemyControlGrabbingBoneFilter = string.Empty;

    /// <summary>
    /// Which bone on the creature does the grabbing for this pair. The short list is two hands, which
    /// covers the humanoids; the shared "Any npc" toggle above opens it to the creature's own
    /// skeleton, because a bat, a wisp or a cactuar has no hand to offer and the grab has to come from
    /// something it actually has.
    /// </summary>
    private void DrawEnemyControlGrabbingBonePicker(EnemyControlController ctrl, EnemyControlGrabPairConfig pair)
    {
        if (!config.EnemyControlGrabAnyNpcBone)
        {
            var idx = Array.IndexOf(EnemyControlGrabNpcBones, pair.NpcBone);
            if (idx < 0) idx = 0;
            ImGui.SetNextItemWidth(60);
            if (ImGui.Combo("##EnemyControlGrabNpc", ref idx, EnemyControlGrabNpcBones, EnemyControlGrabNpcBones.Length))
            { pair.NpcBone = EnemyControlGrabNpcBones[idx]; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Grabbing bone. A creature with no such bone falls back to one it does have —\n" +
                                 "the other hand, then its head or jaw. Tick \"Any npc\" to choose from its own skeleton.");
        }
        else
        {
            var bones = ctrl.GetGrabbingBoneCandidates();
            ImGui.SetNextItemWidth(120);
            if (ImGui.BeginCombo("##EnemyControlGrabNpcAny", pair.NpcBone))
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##EnemyControlGrabbingBoneFilter", "filter", ref EnemyControlGrabbingBoneFilter, 32);

                if (bones.Count == 0)
                    ImGui.TextDisabled("no creature active");

                foreach (var bone in bones)
                {
                    if (EnemyControlGrabbingBoneFilter.Length > 0 &&
                        bone.IndexOf(EnemyControlGrabbingBoneFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (ImGui.Selectable(bone, bone == pair.NpcBone))
                    { pair.NpcBone = bone; config.Save(); }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Grabbing bone — every bone on the ACTIVE creature's own skeleton. Claws, jaws, tentacles.\n" +
                                 "Only populated while a creature is out; spawn one first.");
        }
    }

    /// <summary>
    /// Manual arm-pose override: bends the creature's own upper-arm/forearm bones independent of
    /// whatever the grab animation is doing, so the reach can be lined up with wherever a grab pair
    /// actually targets. Applied only while a grab is held (see
    /// EnemyControlController.ApplyArmPoseOverrides).
    /// </summary>
    private void DrawEnemyControlArmPoseSection()
    {
        var enabled = config.EnemyControlArmPoseEnabled;
        if (ImGui.Checkbox("Enable arm pose override##EnemyControlarmpose", ref enabled))
        { config.EnemyControlArmPoseEnabled = enabled; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bend the creature's own upper-arm/forearm bones by hand, independent of whatever the grab\n" +
                             "animation is doing. Only applied while a grab is held — outside a grab the creature moves\n" +
                             "on its own animation.");

        DrawEnemyControlGrabFitting();

        if (!enabled)
            return;

        if (ImGui.CollapsingHeader("Left Arm Pose##EnemyControlarm"))
        {
            ImGui.Indent();
            ImGui.TextDisabled("Upper arm");
            DrawArmPoseAxisSlider("X##leftupperx", config.EnemyControlLeftArmUpperPitch, v => config.EnemyControlLeftArmUpperPitch = v);
            DrawArmPoseAxisSlider("Y##leftuppery", config.EnemyControlLeftArmUpperYaw, v => config.EnemyControlLeftArmUpperYaw = v);
            DrawArmPoseAxisSlider("Z##leftupperz", config.EnemyControlLeftArmUpperRoll, v => config.EnemyControlLeftArmUpperRoll = v);
            ImGui.TextDisabled("Forearm");
            DrawArmPoseAxisSlider("X##leftlowerx", config.EnemyControlLeftArmLowerPitch, v => config.EnemyControlLeftArmLowerPitch = v);
            DrawArmPoseAxisSlider("Y##leftlowery", config.EnemyControlLeftArmLowerYaw, v => config.EnemyControlLeftArmLowerYaw = v);
            DrawArmPoseAxisSlider("Z##leftlowerz", config.EnemyControlLeftArmLowerRoll, v => config.EnemyControlLeftArmLowerRoll = v);
            if (ImGui.Button("Reset##EnemyControlLeftArmPoseReset"))
            {
                config.EnemyControlLeftArmUpperPitch = 0f; config.EnemyControlLeftArmUpperYaw = 0f; config.EnemyControlLeftArmUpperRoll = 0f;
                config.EnemyControlLeftArmLowerPitch = 0f; config.EnemyControlLeftArmLowerYaw = 0f; config.EnemyControlLeftArmLowerRoll = 0f;
                config.Save();
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Right Arm Pose##EnemyControlarm"))
        {
            ImGui.Indent();
            ImGui.TextDisabled("Upper arm");
            DrawArmPoseAxisSlider("X##rightupperx", config.EnemyControlRightArmUpperPitch, v => config.EnemyControlRightArmUpperPitch = v);
            DrawArmPoseAxisSlider("Y##rightuppery", config.EnemyControlRightArmUpperYaw, v => config.EnemyControlRightArmUpperYaw = v);
            DrawArmPoseAxisSlider("Z##rightupperz", config.EnemyControlRightArmUpperRoll, v => config.EnemyControlRightArmUpperRoll = v);
            ImGui.TextDisabled("Forearm");
            DrawArmPoseAxisSlider("X##rightlowerx", config.EnemyControlRightArmLowerPitch, v => config.EnemyControlRightArmLowerPitch = v);
            DrawArmPoseAxisSlider("Y##rightlowery", config.EnemyControlRightArmLowerYaw, v => config.EnemyControlRightArmLowerYaw = v);
            DrawArmPoseAxisSlider("Z##rightlowerz", config.EnemyControlRightArmLowerRoll, v => config.EnemyControlRightArmLowerRoll = v);
            if (ImGui.Button("Reset##EnemyControlRightArmPoseReset"))
            {
                config.EnemyControlRightArmUpperPitch = 0f; config.EnemyControlRightArmUpperYaw = 0f; config.EnemyControlRightArmUpperRoll = 0f;
                config.EnemyControlRightArmLowerPitch = 0f; config.EnemyControlRightArmLowerYaw = 0f; config.EnemyControlRightArmLowerRoll = 0f;
                config.Save();
            }
            ImGui.Unindent();
        }
    }

    private void DrawArmPoseAxisSlider(string label, float current, Action<float> setter)
    {
        ImGui.SetNextItemWidth(120);
        var v = current;
        if (ImGui.SliderFloat(label, ref v, -90f, 90f, "%.0f°"))
        { setter(v); config.Save(); }
    }

    /// <summary>
    /// How the grabbing hand meets the body. These fix the two separate reasons it used to pass
    /// straight through: where the body gets pulled to, and what the digits do once it is there.
    /// </summary>
    private void DrawEnemyControlGrabFitting()
    {
        if (!ImGui.CollapsingHeader("Fitting##EnemyControlgrab"))
            return;

        var rigid = config.EnemyControlGrabRigid;
        if (ImGui.Checkbox("Take hold instantly##EnemyControlgrab", ref rigid))
        { config.EnemyControlGrabRigid = rigid; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hold the caught bone exactly rather than pulling it with a spring: it is caught where it\n" +
                             "lies, reeled into the hand over a quarter second, and from then on never trails a walking\n" +
                             "creature.\n\n" +
                             "Reads worse than the spring in practice, so it is off by default. The spring carries any\n" +
                             "weight now and no longer stretches, which is most of what this was for.");

        var softContact = config.EnemyControlGrabSoftContact;
        if (ImGui.Checkbox("Rest on the creature##EnemyControlgrab", ref softContact))
        { config.EnemyControlGrabSoftContact = softContact; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Leave the creature's collider live through the grab, so the body hangs against it and rests\n" +
                             "on it rather than through it. Turn off to let the corpse pass straight through the creature.");

        // Spring-grab servo. Meaningless under "Take hold instantly" — a rigid grab drives the bone
        // kinematically and never builds a servo. All three are re-applied every frame by
        // EnemyControlController.TickGrabAttack, so they retune a grab that is already held.
        ImGui.BeginDisabled(config.EnemyControlGrabRigid);

        ImGui.SetNextItemWidth(120);
        var servoFreq = config.EnemyControlGrabServoFrequency;
        if (ImGui.SliderFloat("Hold stiffness##EnemyControlgrab", ref servoFreq, 5f, 120f, "%.0f Hz"))
        { config.EnemyControlGrabServoFrequency = servoFreq; config.Save(); }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("How stiff the spring holding the body to the grip is — the one knob that decides whether a\n" +
                             "grab bounces.\n\n" +
                             "Too high and it fights its own force ceiling: stiffness rises with the square of this, so at\n" +
                             "120 Hz a torso bone saturates past about a millimetre of error, and a saturated servo loses\n" +
                             "its damping along with its spring — full force one way, then the other. That is the bounce.\n" +
                             "Low enough and a few centimetres of travel stay in the linear range, so the body settles\n" +
                             "into the hand instead of ringing; too low and it visibly sags out of the grip.\n\n" +
                             "Retunes a grab that is already held — drag it mid-grab and watch.");

        ImGui.SetNextItemWidth(120);
        var servoForce = config.EnemyControlGrabServoForce;
        if (ImGui.SliderFloat("Hold force##EnemyControlgrab", ref servoForce, 200f, 5000f, "%.0f N"))
        { config.EnemyControlGrabServoForce = servoForce; config.Save(); }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Ceiling on the force the hold may apply. Floored internally at enough to carry this rig's own\n" +
                             "weight with headroom, so setting it below that changes nothing. Raising it widens the error\n" +
                             "range the spring can cover before it saturates.");

        ImGui.SetNextItemWidth(120);
        var servoSpeed = config.EnemyControlGrabServoSpeed;
        if (ImGui.SliderFloat("Hold speed cap##EnemyControlgrab", ref servoSpeed, 1f, 100f, "%.0f m/s"))
        { config.EnemyControlGrabServoSpeed = servoSpeed; config.Save(); }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Ceiling on how fast the hold may move the caught bone. Rarely the binding constraint at the\n" +
                             "default; lower it to make a catch reel in slowly instead of snapping to the hand.");

        ImGui.EndDisabled();

        ImGui.SetNextItemWidth(120);
        var reach = config.EnemyControlGrabGripReach;
        if (ImGui.SliderFloat("Grip depth##EnemyControlgrab", ref reach, 0f, 3f, "%.2f"))
        { config.EnemyControlGrabGripReach = reach; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where in the hand the body sits, as a multiple of the grip volume the creature's own digits\n" +
                             "describe — so it scales itself to any hand size. Lower drags the body back towards the wrist,\n" +
                             "higher pushes it out past the fingertips. 1.75 by default: the raw centroid sits closer to the\n" +
                             "palm than a grip really closes around, because it averages in knuckles that barely move.");

        var conform = config.EnemyControlGrabConformFingers;
        if (ImGui.Checkbox("Conform fingers##EnemyControlgrab", ref conform))
        { config.EnemyControlGrabConformFingers = conform; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Curl the creature's digits onto the grabbed body's surface each frame instead of letting the\n" +
                             "grab animation drive them through it. Digits are found by walking the skeleton below the grab\n" +
                             "bone, so claws and talons work too; a creature with no digits just skips this.");

        if (config.EnemyControlGrabConformFingers)
        {
            ImGui.Indent();

            ImGui.SetNextItemWidth(120);
            var strength = config.EnemyControlGrabFingerStrength;
            if (ImGui.SliderFloat("Strength##EnemyControlgrab", ref strength, 0f, 1f, "%.2f"))
            { config.EnemyControlGrabFingerStrength = strength; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How far each joint moves towards its conformed pose per frame. Below 1 it eases in, which\n" +
                                 "hides the pop as the grab lands and rides out a body that is still thrashing.");

            ImGui.SetNextItemWidth(120);
            var maxAngle = config.EnemyControlGrabFingerMaxAngle;
            if (ImGui.SliderFloat("Max bend##EnemyControlgrab", ref maxAngle, 0f, 120f, "%.0f deg"))
            { config.EnemyControlGrabFingerMaxAngle = maxAngle; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ceiling on how far one joint may bend away from its animated pose. Keeps a digit from folding\n" +
                                 "inside out reaching for a surface it was never going to touch.");

            ImGui.SetNextItemWidth(120);
            var clearance = config.EnemyControlGrabFingerClearance;
            if (ImGui.SliderFloat("Skin gap##EnemyControlgrab", ref clearance, -0.02f, 0.05f, "%.3f m"))
            { config.EnemyControlGrabFingerClearance = clearance; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Extra gap between the digits and the skin, on top of the digit's own thickness.\n" +
                                 "Raise it if the fingers still read as sunk in; go slightly negative to let them dimple the surface.");

            ImGui.Unindent();
        }
    }
}
