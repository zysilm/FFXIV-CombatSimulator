using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Dev;

public class VictorySequenceGui
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly Npcs.NpcSelector npcSelector;
    private int selectedStageIndex = -1;
    private int selectedOtherStageIndex = -1;
    private string newPresetName = "";
    private int selectedPresetIdx = -1;

    // Cached lists (loaded once)
    private List<(uint Id, string Name)>? emoteCache;
    // All action timelines keyed by prefix (e.g., "battle", "normal", "resident")
    private Dictionary<string, List<(uint Id, string Name)>>? actionTimelineByPrefix;
    private string[]? actionTimelinePrefixes;
    private int selectedPrefixIndex;

    // Dynamic bone lists — refreshed from actual skeletons
    private string[] npcBoneList = Array.Empty<string>();
    private string[] playerBoneList = Array.Empty<string>();
    private bool playerBonesLoaded;

    public VictorySequenceGui(Configuration config, Npcs.NpcSelector npcSelector, IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.log = log;
    }

    private unsafe string[] ReadBonesFromCharacter(nint address)
    {
        if (address == nint.Zero) return Array.Empty<string>();
        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if (gameObj->DrawObject == null) return Array.Empty<string>();
        var charBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return Array.Empty<string>();
        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return Array.Empty<string>();

        var havokBones = pose->Skeleton->Bones;
        var bones = new List<string>();
        for (int i = 0; i < havokBones.Length; i++)
        {
            var name = havokBones[i].Name.String;
            if (!string.IsNullOrWhiteSpace(name))
                bones.Add(name);
        }
        bones.Sort(StringComparer.OrdinalIgnoreCase);
        return bones.ToArray();
    }

    private unsafe void RefreshNpcBones()
    {
        // Read from game's current target (not combat sim target list)
        var target = Core.Services.ClientState.LocalPlayer?.TargetObject;
        if (target != null)
        {
            npcBoneList = ReadBonesFromCharacter(target.Address);
            if (npcBoneList.Length > 0)
            {
                log.Info($"VictorySequenceGui: Refreshed NPC bones from '{target.Name}' — {npcBoneList.Length} bones");
                return;
            }
        }

        // Fallback: try combat sim selected NPCs
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null) continue;
            npcBoneList = ReadBonesFromCharacter(npc.Address);
            if (npcBoneList.Length > 0)
            {
                log.Info($"VictorySequenceGui: Refreshed NPC bones from '{npc.Name}' — {npcBoneList.Length} bones");
                return;
            }
        }
        log.Info("VictorySequenceGui: No target found — target an NPC in-game then click Refresh");
    }

    private unsafe void EnsurePlayerBones()
    {
        if (playerBonesLoaded) return;
        var player = Core.Services.ClientState.LocalPlayer;
        if (player == null) return;
        playerBoneList = ReadBonesFromCharacter(player.Address);
        if (playerBoneList.Length > 0)
            playerBonesLoaded = true;
    }

    private void EnsureEmoteCache()
    {
        if (emoteCache != null) return;
        emoteCache = new List<(uint, string)> { (0, "(None)") };
        try
        {
            var emoteSheet = Core.Services.DataManager.GetExcelSheet<Emote>();
            if (emoteSheet != null)
            {
                var emotes = new List<(uint Id, string Name)>();
                foreach (var emote in emoteSheet)
                {
                    var name = emote.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        emotes.Add((emote.RowId, $"{name} [{emote.RowId}]"));
                }
                emotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                emoteCache.AddRange(emotes);
            }
        }
        catch { }
    }

    private void EnsureActionTimelineCache()
    {
        if (actionTimelineByPrefix != null) return;
        actionTimelineByPrefix = new Dictionary<string, List<(uint, string)>>();
        try
        {
            var sheet = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet == null) return;
            foreach (var row in sheet)
            {
                var key = row.Key.ToString();
                if (string.IsNullOrWhiteSpace(key)) continue;
                var slash = key.IndexOf('/');
                var prefix = slash > 0 ? key[..slash] : "other";
                if (!actionTimelineByPrefix.ContainsKey(prefix))
                    actionTimelineByPrefix[prefix] = new List<(uint, string)> { (0, "(None)") };
                actionTimelineByPrefix[prefix].Add((row.RowId, $"{key} [{row.RowId}]"));
            }
            // Sort each prefix list alphabetically
            foreach (var list in actionTimelineByPrefix.Values)
                list.Sort(1, list.Count - 1, Comparer<(uint, string)>.Create(
                    (a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase)));
            // Build sorted prefix list
            var prefixes = new List<string>(actionTimelineByPrefix.Keys);
            prefixes.Sort(StringComparer.OrdinalIgnoreCase);
            actionTimelinePrefixes = prefixes.ToArray();
        }
        catch { }
    }

    private List<(uint Id, string Name)> GetCurrentActionTimelineList()
    {
        if (actionTimelineByPrefix == null || actionTimelinePrefixes == null || actionTimelinePrefixes.Length == 0)
            return new List<(uint, string)> { (0, "(None)") };
        if (selectedPrefixIndex < 0 || selectedPrefixIndex >= actionTimelinePrefixes.Length)
            selectedPrefixIndex = 0;
        var prefix = actionTimelinePrefixes[selectedPrefixIndex];
        return actionTimelineByPrefix.TryGetValue(prefix, out var list) ? list : new List<(uint, string)> { (0, "(None)") };
    }

    private int FindEmoteIndex(uint emoteId)
    {
        if (emoteCache == null) return 0;
        for (int i = 0; i < emoteCache.Count; i++)
            if (emoteCache[i].Id == emoteId) return i;
        return 0;
    }

    private int FindBoneIndex(string[] bones, string boneName)
    {
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] == boneName) return i;
        return 0;
    }

    private void ResolveEmoteTimelines(VictorySequenceStage stage)
    {
        stage.ResolvedIntroTimeline = 0;
        stage.ResolvedLoopTimeline = 0;
        if (stage.EmoteId == 0) return;
        try
        {
            var emoteSheet = Core.Services.DataManager.GetExcelSheet<Emote>();
            if (emoteSheet == null) return;
            var emote = emoteSheet.GetRow(stage.EmoteId);
            stage.ResolvedLoopTimeline = (ushort)emote.ActionTimeline[0].RowId;
            stage.ResolvedIntroTimeline = (ushort)emote.ActionTimeline[1].RowId;
        }
        catch { }
    }

    public void Draw()
    {
        EnsureEmoteCache();
        EnsureActionTimelineCache();
        EnsurePlayerBones();
        ImGui.Separator();
        ImGui.Text("Victory Cinematic");

        var enabled = config.EnableVictorySequence;
        if (ImGui.Checkbox("Enable Victory Sequence##dev", ref enabled))
        {
            config.EnableVictorySequence = enabled;
            config.Save();
        }
        HelpMarker("Cinematic multi-stage victory sequence when the player dies. The last targeted NPC moves from its current position to the configured distance, with animations and optional grab constraint.");

        if (!config.EnableVictorySequence) return;

        ImGui.Indent();
        var stages = config.VictorySequenceStages;

        // Stage list table
        if (ImGui.BeginTable("##vseqstages", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Behavior", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Grab", ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            int deleteIdx = -1, moveUpIdx = -1, moveDownIdx = -1;

            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i}##sel{i}", selectedStageIndex == i))
                {
                    selectedStageIndex = i;
                    selectedOtherStageIndex = -1;
                }

                ImGui.TableNextColumn();
                ImGui.Text(s.EndTime < 0 ? $"{s.StartTime:F1}-∞" : $"{s.StartTime:F1}-{s.EndTime:F1}s");

                ImGui.TableNextColumn();
                ImGui.Text(s.KeepPosition ? "keep" : $"→{s.EndDistance:F1}");

                ImGui.TableNextColumn();
                var behaviorName = s.UseEmote
                    ? (s.EmoteId > 0 ? FindEmoteName(s.EmoteId) : "-")
                    : (s.ActionTimelineId > 0 ? FindActionTimelineName(s.ActionTimelineId) : "-");
                ImGui.Text(behaviorName);

                ImGui.TableNextColumn();
                ImGui.Text(s.GrabEnabled ? "Y" : "");

                ImGui.TableNextColumn();
                if (i > 0 && ImGui.SmallButton($"^##{i}")) moveUpIdx = i;
                ImGui.SameLine();
                if (i < stages.Count - 1 && ImGui.SmallButton($"v##{i}")) moveDownIdx = i;
                ImGui.SameLine();
                if (ImGui.SmallButton($"X##{i}")) deleteIdx = i;
            }

            ImGui.EndTable();

            if (deleteIdx >= 0)
            {
                stages.RemoveAt(deleteIdx);
                if (selectedStageIndex >= stages.Count) selectedStageIndex = stages.Count - 1;
                config.Save();
            }
            if (moveUpIdx > 0)
            {
                (stages[moveUpIdx], stages[moveUpIdx - 1]) = (stages[moveUpIdx - 1], stages[moveUpIdx]);
                selectedStageIndex = moveUpIdx - 1;
                config.Save();
            }
            if (moveDownIdx >= 0 && moveDownIdx < stages.Count - 1)
            {
                (stages[moveDownIdx], stages[moveDownIdx + 1]) = (stages[moveDownIdx + 1], stages[moveDownIdx]);
                selectedStageIndex = moveDownIdx + 1;
                config.Save();
            }
        }

        if (ImGui.Button("+ Add Stage##vseq"))
        {
            var newStage = new VictorySequenceStage();
            // Chain from previous stage's end values
            if (stages.Count > 0)
            {
                var prev = stages[^1];
                var prevEnd = prev.EndTime < 0 ? prev.StartTime + 5f : prev.EndTime;
                newStage.StartTime = prevEnd;
                newStage.EndTime = prevEnd; // user adjusts end time
                newStage.EndDistance = prev.EndDistance;
            }
            stages.Add(newStage);
            selectedStageIndex = stages.Count - 1;
            config.Save();
        }

        // ============================================================
        // OTHER NPCs SEQUENCE
        // ============================================================
        ImGui.Separator();
        ImGui.Text("Other NPCs Sequence");
        HelpMarker("Animation stages for all non-cinematic NPCs (those not doing the grab). Uses the same timeline as the cinematic NPC.");

        var otherStages = config.VictorySequenceOtherStages;

        if (ImGui.BeginTable("##vseqother", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Behavior", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            int oDeleteIdx = -1;
            for (int i = 0; i < otherStages.Count; i++)
            {
                var s = otherStages[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i}##osel{i}", selectedOtherStageIndex == i))
                {
                    selectedOtherStageIndex = i;
                    selectedStageIndex = -1;
                }
                ImGui.TableNextColumn();
                ImGui.Text(s.EndTime < 0 ? $"{s.StartTime:F1}-∞" : $"{s.StartTime:F1}-{s.EndTime:F1}s");
                ImGui.TableNextColumn();
                var oBehavior = s.UseEmote
                    ? (s.EmoteId > 0 ? FindEmoteName(s.EmoteId) : "-")
                    : (s.ActionTimelineId > 0 ? FindActionTimelineName(s.ActionTimelineId) : "-");
                ImGui.Text(oBehavior);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"X##o{i}")) oDeleteIdx = i;
            }
            ImGui.EndTable();
            if (oDeleteIdx >= 0)
            {
                otherStages.RemoveAt(oDeleteIdx);
                if (selectedOtherStageIndex >= otherStages.Count) selectedOtherStageIndex = otherStages.Count - 1;
                config.Save();
            }
        }

        if (ImGui.Button("+ Add Stage##other"))
        {
            var newStage = new VictorySequenceStage();
            if (otherStages.Count > 0)
            {
                var prev = otherStages[^1];
                var prevEnd = prev.EndTime < 0 ? prev.StartTime + 5f : prev.EndTime;
                newStage.StartTime = prevEnd;
                newStage.EndTime = prevEnd;
            }
            otherStages.Add(newStage);
            selectedOtherStageIndex = otherStages.Count - 1;
            config.Save();
        }

        // Selected other stage editor
        if (selectedOtherStageIndex >= 0 && selectedOtherStageIndex < otherStages.Count)
        {
            ImGui.Separator();
            var os = otherStages[selectedOtherStageIndex];
            var oidx = selectedOtherStageIndex;

            ImGui.TextDisabled($"Other Stage {oidx} — Timing");
            var ost = os.StartTime;
            if (ImGui.DragFloat("Start Time (s)##ot", ref ost, 0.1f, 0, 120, "%.1f"))
            {
                os.StartTime = ost;
                if (oidx > 0 && otherStages[oidx - 1].EndTime >= 0) otherStages[oidx - 1].EndTime = ost;
                config.Save();
            }
            if (os.EndTime >= 0)
            {
                var oet = os.EndTime;
                if (ImGui.DragFloat("End Time (s)##ot", ref oet, 0.1f, 0, 120, "%.1f"))
                {
                    os.EndTime = oet;
                    if (oidx < otherStages.Count - 1) otherStages[oidx + 1].StartTime = oet;
                    config.Save();
                }
            }
            var oInfTime = os.EndTime < 0;
            if (ImGui.Checkbox("Infinite Time##ovsd", ref oInfTime))
            { os.EndTime = oInfTime ? -1f : os.StartTime + 3f; config.Save(); }

            ImGui.TextDisabled("Behavior");
            var oUseEmote = os.UseEmote;
            if (oUseEmote)
            {
                var emoteIdx = FindEmoteIndex(os.EmoteId);
                var emoteName = emoteIdx < emoteCache!.Count ? emoteCache[emoteIdx].Name : "(None)";
                ImGui.SetNextItemWidth(500);
                if (ImGui.BeginCombo("##obehavvsd", emoteName))
                {
                    for (int i = 0; i < emoteCache.Count; i++)
                    {
                        if (ImGui.Selectable(emoteCache[i].Name, i == emoteIdx))
                        { os.EmoteId = emoteCache[i].Id; os.ActionTimelineId = 0; ResolveEmoteTimelines(os); config.Save(); }
                        if (i == emoteIdx) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("<##oem")) { var n = emoteIdx - 1; if (n >= 0) { os.EmoteId = emoteCache[n].Id; os.ActionTimelineId = 0; ResolveEmoteTimelines(os); config.Save(); } }
                ImGui.SameLine();
                if (ImGui.SmallButton(">##oem")) { var n = emoteIdx + 1; if (n < emoteCache.Count) { os.EmoteId = emoteCache[n].Id; os.ActionTimelineId = 0; ResolveEmoteTimelines(os); config.Save(); } }
            }
            else
            {
                if (actionTimelinePrefixes != null && actionTimelinePrefixes.Length > 0)
                    ImGui.Combo("Filter##oatfilt", ref selectedPrefixIndex, actionTimelinePrefixes, actionTimelinePrefixes.Length);
                var atList = GetCurrentActionTimelineList();
                int atIdx = 0;
                for (int i = 0; i < atList.Count; i++) if (atList[i].Id == os.ActionTimelineId) { atIdx = i; break; }
                var atName = atIdx < atList.Count ? atList[atIdx].Name : "(None)";
                ImGui.SetNextItemWidth(500);
                if (ImGui.BeginCombo("Action##obehavvsd", atName))
                {
                    for (int i = 0; i < atList.Count; i++)
                    {
                        if (ImGui.Selectable(atList[i].Name, i == atIdx))
                        { os.ActionTimelineId = atList[i].Id; os.EmoteId = 0; os.ResolvedIntroTimeline = 0; os.ResolvedLoopTimeline = 0; config.Save(); }
                        if (i == atIdx) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("<##oat")) { var n = atIdx - 1; if (n >= 0) { os.ActionTimelineId = atList[n].Id; os.EmoteId = 0; os.ResolvedIntroTimeline = 0; os.ResolvedLoopTimeline = 0; config.Save(); } }
                ImGui.SameLine();
                if (ImGui.SmallButton(">##oat")) { var n = atIdx + 1; if (n < atList.Count) { os.ActionTimelineId = atList[n].Id; os.EmoteId = 0; os.ResolvedIntroTimeline = 0; os.ResolvedLoopTimeline = 0; config.Save(); } }
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Emote##omode", ref oUseEmote))
            { os.UseEmote = oUseEmote; config.Save(); }

            var oLockFace = os.LockFacing;
            if (ImGui.Checkbox("Lock Facing##ovsd", ref oLockFace))
            { os.LockFacing = oLockFace; config.Save(); }
        }

        // --- Presets (stores both cinematic + other NPC stages) ---
        ImGui.Separator();
        ImGui.TextDisabled("Presets");
        var presets = config.VictoryCinematicPresets;

        ImGui.SetNextItemWidth(120);
        ImGui.InputText("##presetname", ref newPresetName, 32);
        ImGui.SameLine();
        if (ImGui.Button("Save##preset") && !string.IsNullOrWhiteSpace(newPresetName))
        {
            presets.Add(new VictoryCinematicPreset
            {
                Name = newPresetName,
                Stages = VictoryCinematicPreset.CloneStages(stages),
                OtherStages = VictoryCinematicPreset.CloneStages(otherStages),
            });
            newPresetName = "";
            config.Save();
        }

        if (presets.Count > 0)
        {
            var presetNames = new string[presets.Count];
            for (int i = 0; i < presets.Count; i++) presetNames[i] = presets[i].Name;
            if (selectedPresetIdx >= presets.Count) selectedPresetIdx = 0;

            ImGui.SetNextItemWidth(120);
            ImGui.Combo("##presetsel", ref selectedPresetIdx, presetNames, presetNames.Length);
            ImGui.SameLine();
            if (ImGui.Button("Load##preset"))
            {
                var preset = presets[selectedPresetIdx];
                stages.Clear();
                stages.AddRange(VictoryCinematicPreset.CloneStages(preset.Stages));
                otherStages.Clear();
                otherStages.AddRange(VictoryCinematicPreset.CloneStages(preset.OtherStages));
                selectedStageIndex = -1;
                selectedOtherStageIndex = -1;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete##preset"))
            {
                presets.RemoveAt(selectedPresetIdx);
                if (selectedPresetIdx >= presets.Count) selectedPresetIdx = presets.Count - 1;
                config.Save();
            }
        }

        // Selected stage detail editor
        if (selectedStageIndex >= 0 && selectedStageIndex < stages.Count)
        {
            ImGui.Separator();
            var s = stages[selectedStageIndex];
            var idx = selectedStageIndex;

            // === Timing ===
            ImGui.TextDisabled($"Stage {idx} — Timing");

            var st = s.StartTime;
            if (ImGui.DragFloat("Start Time (s)##t", ref st, 0.1f, 0, 120, "%.1f"))
            {
                s.StartTime = st;
                if (idx > 0 && stages[idx - 1].EndTime >= 0)
                    stages[idx - 1].EndTime = st;
                config.Save();
            }

            if (!s.InfiniteWalk && s.EndTime >= 0)
            {
                var et = s.EndTime;
                if (ImGui.DragFloat("End Time (s)##t", ref et, 0.1f, 0, 120, "%.1f"))
                {
                    s.EndTime = et;
                    if (idx < stages.Count - 1) stages[idx + 1].StartTime = et;
                    config.Save();
                }
            }

            var infTime = s.EndTime < 0;
            if (ImGui.Checkbox("Infinite Time##vsd", ref infTime))
            {
                s.EndTime = infTime ? -1f : s.StartTime + 3f;
                if (!infTime) s.InfiniteWalk = false;
                config.Save();
            }

            // === Movement ===
            ImGui.TextDisabled("Movement");

            var keepPos = s.KeepPosition;
            if (ImGui.Checkbox("Keep Position##vsd", ref keepPos))
            {
                s.KeepPosition = keepPos;
                config.Save();
            }
            HelpMarker("Stay at current position (where the previous stage ended). No movement.");

            if (!s.KeepPosition)
            {
                if (!s.InfiniteWalk)
                {
                    var ed = s.EndDistance;
                    if (ImGui.DragFloat("End Distance##d", ref ed, 0.1f, -30, 30, "%.1f"))
                    {
                        s.EndDistance = ed;
                        config.Save();
                    }
                }
                else
                {
                    var ws = s.WalkSpeed;
                    if (ImGui.DragFloat("Walk Speed (y/s)##walk", ref ws, 0.1f, -20f, 20f, "%.1f"))
                    { s.WalkSpeed = ws; config.Save(); }
                }

                var ho = s.HeightOffset;
                if (ImGui.DragFloat("Height Offset##h", ref ho, 0.01f, -5, 5, "%.2f"))
                { s.HeightOffset = ho; config.Save(); }

                var infWalk = s.InfiniteWalk;
                if (ImGui.Checkbox("Infinite Walk##vsd", ref infWalk))
                {
                    s.InfiniteWalk = infWalk;
                    if (infWalk) s.EndTime = -1f;
                    config.Save();
                }
            }

            var lockFace = s.LockFacing;
            if (ImGui.Checkbox("Lock Facing##vsd", ref lockFace))
            { s.LockFacing = lockFace; config.Save(); }

            // === Behavior ===
            ImGui.TextDisabled("Behavior");
            var useEmote = s.UseEmote;
            if (useEmote)
            {
                var emoteIdx = FindEmoteIndex(s.EmoteId);
                var emoteName = emoteIdx < emoteCache!.Count ? emoteCache[emoteIdx].Name : "(None)";
                ImGui.SetNextItemWidth(500);
                if (ImGui.BeginCombo("##behavvsd", emoteName))
                {
                    for (int i = 0; i < emoteCache.Count; i++)
                    {
                        if (ImGui.Selectable(emoteCache[i].Name, i == emoteIdx))
                        {
                            s.EmoteId = emoteCache[i].Id;
                            s.ActionTimelineId = 0;
                            ResolveEmoteTimelines(s);
                            config.Save();
                        }
                        if (i == emoteIdx) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                // Arrow buttons for quick emote navigation
                ImGui.SameLine();
                if (ImGui.SmallButton("<##em"))
                {
                    var newIdx = emoteIdx - 1;
                    if (newIdx >= 0)
                    {
                        s.EmoteId = emoteCache[newIdx].Id;
                        s.ActionTimelineId = 0;
                        ResolveEmoteTimelines(s);
                        config.Save();
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton(">##em"))
                {
                    var newIdx = emoteIdx + 1;
                    if (newIdx < emoteCache.Count)
                    {
                        s.EmoteId = emoteCache[newIdx].Id;
                        s.ActionTimelineId = 0;
                        ResolveEmoteTimelines(s);
                        config.Save();
                    }
                }
            }
            else
            {
                // Filter dropdown (prefix selector)
                if (actionTimelinePrefixes != null && actionTimelinePrefixes.Length > 0)
                {
                    if (ImGui.Combo("Filter##atfilt", ref selectedPrefixIndex, actionTimelinePrefixes, actionTimelinePrefixes.Length))
                    { /* just changes the filter, no save needed */ }
                }

                // Action timeline dropdown (filtered by prefix)
                var atList = GetCurrentActionTimelineList();
                int atIdx = 0;
                for (int i = 0; i < atList.Count; i++)
                    if (atList[i].Id == s.ActionTimelineId) { atIdx = i; break; }
                var atName = atIdx < atList.Count ? atList[atIdx].Name : "(None)";
                ImGui.SetNextItemWidth(500);
                if (ImGui.BeginCombo("Action##behavvsd", atName))
                {
                    for (int i = 0; i < atList.Count; i++)
                    {
                        if (ImGui.Selectable(atList[i].Name, i == atIdx))
                        {
                            s.ActionTimelineId = atList[i].Id;
                            s.EmoteId = 0; s.ResolvedIntroTimeline = 0; s.ResolvedLoopTimeline = 0;
                            config.Save();
                        }
                        if (i == atIdx) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                // Arrow buttons for quick action navigation
                ImGui.SameLine();
                if (ImGui.SmallButton("<##at"))
                {
                    var newIdx = atIdx - 1;
                    if (newIdx >= 0)
                    {
                        s.ActionTimelineId = atList[newIdx].Id;
                        s.EmoteId = 0; s.ResolvedIntroTimeline = 0; s.ResolvedLoopTimeline = 0;
                        config.Save();
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton(">##at"))
                {
                    var newIdx = atIdx + 1;
                    if (newIdx < atList.Count)
                    {
                        s.ActionTimelineId = atList[newIdx].Id;
                        s.EmoteId = 0; s.ResolvedIntroTimeline = 0; s.ResolvedLoopTimeline = 0;
                        config.Save();
                    }
                }
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Emote##mode", ref useEmote))
            { s.UseEmote = useEmote; config.Save(); }


            // === Grab ===
            var grab = s.GrabEnabled;
            if (ImGui.Checkbox("Grab##vsd", ref grab))
            { s.GrabEnabled = grab; config.Save(); }
            if (s.GrabEnabled)
            {
                ImGui.Indent();

                var npcIdx = FindBoneIndex(npcBoneList, s.NpcBoneName);
                if (ImGui.Combo("NPC Bone##gb", ref npcIdx, npcBoneList, npcBoneList.Length))
                { s.NpcBoneName = npcBoneList[npcIdx]; config.Save(); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh##nb")) RefreshNpcBones();

                var playerIdx = FindBoneIndex(playerBoneList, s.PlayerBoneName);
                if (ImGui.Combo("Player Bone##gb", ref playerIdx, playerBoneList, playerBoneList.Length))
                { s.PlayerBoneName = playerBoneList[playerIdx]; config.Save(); }

                var gf = s.GrabForce;
                if (ImGui.DragFloat("Force##gf", ref gf, 10f, 10, 5000, "%.0f"))
                { s.GrabForce = gf; config.Save(); }

                var gs2 = s.GrabSpeed;
                if (ImGui.DragFloat("Speed##gs", ref gs2, 1f, 1, 200, "%.0f"))
                { s.GrabSpeed = gs2; config.Save(); }

                var gsf = s.GrabSpringFreq;
                if (ImGui.DragFloat("Spring Freq##gf2", ref gsf, 5f, 10, 500, "%.0f"))
                { s.GrabSpringFreq = gsf; config.Save(); }

                if (ImGui.SmallButton("Reset Defaults##grst"))
                { s.GrabForce = 1000; s.GrabSpeed = 50; s.GrabSpringFreq = 120; config.Save(); }

                // --- Shoulder rotation override ---
                ImGui.Separator();
                var shoulder = s.ShoulderRotationEnabled;
                if (ImGui.Checkbox("Shoulder rotation##shldr", ref shoulder))
                { s.ShoulderRotationEnabled = shoulder; config.Save(); }
                if (s.ShoulderRotationEnabled)
                {
                    ImGui.Indent();

                    var shoulderIdx = FindBoneIndex(npcBoneList, s.ShoulderBoneName);
                    if (ImGui.Combo("Shoulder Bone##shb", ref shoulderIdx, npcBoneList, npcBoneList.Length))
                    { s.ShoulderBoneName = npcBoneList[shoulderIdx]; config.Save(); }

                    var pitch = s.ShoulderPitch;
                    if (ImGui.DragFloat("Pitch##shp", ref pitch, 0.5f, -180f, 180f, "%.1f"))
                    { s.ShoulderPitch = pitch; config.Save(); }

                    var yaw = s.ShoulderYaw;
                    if (ImGui.DragFloat("Yaw##shy", ref yaw, 0.5f, -180f, 180f, "%.1f"))
                    { s.ShoulderYaw = yaw; config.Save(); }

                    var roll = s.ShoulderRoll;
                    if (ImGui.DragFloat("Roll##shr", ref roll, 0.5f, -180f, 180f, "%.1f"))
                    { s.ShoulderRoll = roll; config.Save(); }

                    if (ImGui.SmallButton("Reset##shrst"))
                    { s.ShoulderPitch = 0; s.ShoulderYaw = 0; s.ShoulderRoll = 0; config.Save(); }

                    ImGui.Unindent();
                }

                ImGui.Unindent();
            }
        }

        ImGui.Unindent();
    }

    private string FindEmoteName(uint emoteId)
    {
        if (emoteCache == null) return "?";
        for (int i = 0; i < emoteCache.Count; i++)
            if (emoteCache[i].Id == emoteId)
            {
                var name = emoteCache[i].Name;
                var bracket = name.LastIndexOf(" [");
                return bracket > 0 ? name[..bracket] : name;
            }
        return $"#{emoteId}";
    }

    private string FindActionTimelineName(uint atId)
    {
        if (actionTimelineByPrefix == null) return $"#{atId}";
        foreach (var list in actionTimelineByPrefix.Values)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == atId)
                {
                    var name = list[i].Name;
                    var bracket = name.LastIndexOf(" [");
                    return bracket > 0 ? name[..bracket] : name;
                }
        }
        return $"#{atId}";
    }

    private static void HelpMarker(string desc)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20.0f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
