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

// Enemy Control: post-defeat possession of one enemy (take over the killer, walk it, grab the body
// with finger conform). Moved out of the private Dev/Experimental
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
        HelpMarker("Open the Enemy Control window: after the battle is lost, take over the enemy that defeated you " +
                   "(set On death to Control killer there). Only that one enemy; everyone else carries on.");
    }

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
        // Its own window (the pick catcher) or the foreground draw list (markers): drawn even while this one is collapsed.
        DrawEnemyControlAnchorOverlay(ctrl);

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

        var onDeath = config.EnemyControlControlKiller ? 1 : 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("On death##EnemyControl", ref onDeath, EnemyControlOnDeathLabels, EnemyControlOnDeathLabels.Length))
        {
            config.EnemyControlControlKiller = onDeath == 1;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Control killer: after the battle is lost, take control of the enemy that defeated you.\n" +
                             "Your fall alone is not the end: while your side can still win (or raise you), nothing happens.\n" +
                             "Only that one enemy; every other enemy keeps fighting or patrolling as before.\n" +
                             "Warring States with Retreat: if your body is carried off, you control the enemy that took it.");

        ImGui.BeginDisabled(!active);
        if (ImGui.Button("Release##EnemyControl")) ctrl.Despawn();
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

        var keyIdx = Array.FindIndex(EnemyControlAttackKeys, k => k.Key == config.EnemyControlAttackKey);
        if (keyIdx < 0) keyIdx = 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Grab key##EnemyControl", EnemyControlAttackKeys[keyIdx].Name))
        {
            for (int i = 0; i < EnemyControlAttackKeys.Length; i++)
                if (ImGui.Selectable(EnemyControlAttackKeys[i].Name, i == keyIdx))
                { config.EnemyControlAttackKey = EnemyControlAttackKeys[i].Key; config.Save(); }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Starts and releases the configured grab.");

        DrawEnemyControlGrabAnimationPicker();
        DrawEnemyControlGrabPairsList(ctrl);
        DrawEnemyControlGrabFitting();
        DrawEnemyControlGrabProfilesSection();

        ImGui.TextDisabled("WASD = camera-relative move; Q/E = down/up");
        ImGui.TextDisabled("Gamepad: left stick = move; L2/R2 = down/up; Cross = grab");
        ImGui.TextDisabled(active ? "EnemyControl: active" : "EnemyControl: none");

        ImGui.End();
    }

    private static readonly string[] EnemyControlOnDeathLabels = { "Nothing", "Control killer" };

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
            snapshot.Pairs.Add(EnemyControlGrabPairList.Clone(pair));
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
        config.EnemyControlGrabPairs.AddRange(EnemyControlGrabPairList.CopyForUse(p.Pairs));
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

            DrawEnemyControlGrabSide(ctrl, pair, i, enemySide: true);
            ImGui.SameLine();
            ImGui.Text("->");
            ImGui.SameLine();
            DrawEnemyControlGrabSide(ctrl, pair, i, enemySide: false);

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

    // ── Grab points picked on the models (docs/ENEMY_CONTROL_GRAB.md §3) ──

    private int enemyControlAnchorPickPair = -1;
    private bool enemyControlAnchorPickEnemy;
    private bool enemyControlAnchorPickFocus;

    /// <summary>One end of a pair: its bone picker and a Pick button, or, once a point is picked, the point's bone
    /// and a Clear button.</summary>
    private void DrawEnemyControlGrabSide(EnemyControlController ctrl, EnemyControlGrabPairConfig pair, int index, bool enemySide)
    {
        var anchor = enemySide ? pair.NpcAnchor : pair.PlayerAnchor;
        var id = enemySide ? "Npc" : "Player";
        if (anchor != null)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{anchor.Bone} · anchor set");
            ImGui.SameLine();
            if (ImGui.Button($"Clear##EnemyControlAnchorClear{id}"))
                ClearEnemyControlAnchor(pair, enemySide);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Forget the picked point; the grab holds the bone itself again.");
            return;
        }

        if (enemySide) DrawEnemyControlGrabbingBonePicker(ctrl, pair);
        else DrawEnemyControlGrabbedBonePicker(pair);

        ImGui.SameLine();
        var canPick = enemySide ? ctrl.ControlledActor != nint.Zero : Core.Services.ObjectTable.LocalPlayer != null;
        ImGui.BeginDisabled(!canPick || enemyControlAnchorPickPair >= 0);
        if (ImGui.Button($"Pick##EnemyControlAnchorPick{id}"))
        {
            enemyControlAnchorPickPair = index;
            enemyControlAnchorPickEnemy = enemySide;
            enemyControlAnchorPickFocus = true;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(enemySide
                ? canPick
                    ? "Click on the enemy you control to choose exactly where its grip is."
                    : "Take control of an enemy first (On death: Control killer)."
                : "Click on your character to choose exactly where the body is held.");
    }

    private void ClearEnemyControlAnchor(EnemyControlGrabPairConfig pair, bool enemySide)
    {
        if (enemySide)
        {
            pair.NpcAnchor = null;
            // The short list cannot show a bone outside it; put the row back on a bone it can show.
            if (!config.EnemyControlGrabAnyNpcBone && Array.IndexOf(EnemyControlGrabNpcBones, pair.NpcBone) < 0)
                pair.NpcBone = EnemyControlGrabNpcBones[0];
        }
        else
        {
            pair.PlayerAnchor = null;
            if (!config.EnemyControlGrabAnyBone && Array.IndexOf(EnemyControlGrabPlayerBones, pair.PlayerBone) < 0)
                pair.PlayerBone = EnemyControlGrabPlayerBones[0];
        }
        config.Save();
    }

    /// <summary>
    /// While a Pick is armed: a transparent window over the whole screen takes the mouse (so the click never reaches
    /// the game's targeting or camera); a left click on the model sets the point, Esc cancels. Otherwise: a marker on
    /// each picked point while the Enemy Control window is open.
    /// </summary>
    private void DrawEnemyControlAnchorOverlay(EnemyControlController ctrl)
    {
        var pairs = config.EnemyControlGrabPairs;
        var player = Core.Services.ObjectTable.LocalPlayer?.Address ?? nint.Zero;
        var enemy = ctrl.ControlledActor;
        var io = ImGui.GetIO();

        if (enemyControlAnchorPickPair < 0)
        {
            DrawEnemyControlAnchorMarkers(ctrl, pairs, enemy, player);
            return;
        }

        var actor = enemyControlAnchorPickEnemy ? enemy : player;
        if (enemyControlAnchorPickPair >= pairs.Count || actor == nint.Zero)
        {
            enemyControlAnchorPickPair = -1;
            return;
        }

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(io.DisplaySize);
        if (enemyControlAnchorPickFocus)
        {
            ImGui.SetNextWindowFocus();
            enemyControlAnchorPickFocus = false;
        }
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                                       ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##EnemyControlAnchorPick", flags))
        {
            var hint = enemyControlAnchorPickEnemy
                ? "Click on the enemy to set where it grips.  Esc: cancel"
                : "Click on your character to set where it is held.  Esc: cancel";
            var size = ImGui.CalcTextSize(hint);
            var at = new Vector2((io.DisplaySize.X - size.X) * 0.5f, io.DisplaySize.Y * 0.12f);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(at - new Vector2(10, 6), at + size + new Vector2(10, 6), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), 6f);
            draw.AddText(at, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 1f)), hint);
            draw.AddCircle(io.MousePos, 6f, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 1f)), 16, 1.5f);

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                enemyControlAnchorPickPair = -1;
            else if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (ctrl.AnchorPicker.TryPick(actor, io.MousePos, io.DisplaySize, out var anchor, out var message))
                {
                    var pair = pairs[enemyControlAnchorPickPair];
                    if (enemyControlAnchorPickEnemy) { pair.NpcAnchor = anchor; pair.NpcBone = anchor.Bone; }
                    else { pair.PlayerAnchor = anchor; pair.PlayerBone = anchor.Bone; }
                    config.Save();
                    enemyControlAnchorPickPair = -1;
                    chatGui.Print($"[CombatSim] Grab point set on {anchor.Bone}.");
                }
                else
                    chatGui.Print($"[CombatSim] No grab point: {message}. Click on the model again, or Esc to cancel.");
            }
        }
        ImGui.End();
    }

    private void DrawEnemyControlAnchorMarkers(EnemyControlController ctrl, List<EnemyControlGrabPairConfig> pairs,
        nint enemy, nint player)
    {
        var draw = ImGui.GetForegroundDrawList();
        var enemyColor = ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 1f));
        var playerColor = ImGui.GetColorU32(new Vector4(0.3f, 0.85f, 1f, 1f));
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            Mark(pair.NpcAnchor, enemy, enemyColor, i);
            Mark(pair.PlayerAnchor, player, playerColor, i);
        }

        void Mark(EnemyControlGrabAnchor? anchor, nint actor, uint color, int index)
        {
            if (anchor == null || actor == nint.Zero || !ctrl.TryResolveAnchor(actor, anchor, out var world)) return;
            if (!Core.Services.GameGui.WorldToScreen(world, out var screen)) return;
            draw.AddCircleFilled(screen, 4f, color);
            draw.AddCircle(screen, 7f, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.8f)), 16, 1.5f);
            draw.AddText(screen + new Vector2(8f, -8f), color, (index + 1).ToString());
        }
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
        foreach (var bone in RagdollController.AllBoneDefaults)
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
    /// How the grabbing hand meets the body. These fix the two separate reasons it used to pass
    /// straight through: where the body gets pulled to, and what the digits do once it is there.
    /// </summary>
    private void DrawEnemyControlGrabFitting()
    {
        if (!ImGui.CollapsingHeader("Fitting##EnemyControlgrab"))
            return;

        // Hold mode, servo and finger-conform tuning are no longer shown; they keep their saved values
        // and are still stored in grab profiles.
        ImGui.SetNextItemWidth(120);
        var reach = config.EnemyControlGrabGripReach;
        if (ImGui.SliderFloat("Grip depth##EnemyControlgrab", ref reach, 0f, 3f, "%.2f"))
        { config.EnemyControlGrabGripReach = reach; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where in the hand the body sits, as a multiple of the grip volume the creature's own digits\n" +
                             "describe — so it scales itself to any hand size. Lower drags the body back towards the wrist,\n" +
                             "higher pushes it out past the fingertips.");

        ImGui.SetNextItemWidth(120);
        var clearance = config.EnemyControlGrabFingerClearance;
        if (ImGui.SliderFloat("Skin gap##EnemyControlgrab", ref clearance, -0.02f, 0.05f, "%.3f m"))
        { config.EnemyControlGrabFingerClearance = clearance; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Extra gap between the creature's digits and the skin. Raise it if the fingers read as sunk in;\n" +
                             "go slightly negative to let them dimple the surface.");
    }
}
