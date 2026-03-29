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

    // Cached lists (alphabetically sorted, loaded once)
    private List<(uint Id, string Name)>? emoteCache;
    private List<(uint Id, string Name)>? actionTimelineCache;

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
        if (actionTimelineCache != null) return;
        actionTimelineCache = new List<(uint, string)> { (0, "(None)") };
        try
        {
            var sheet = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet != null)
            {
                var items = new List<(uint Id, string Name)>();
                foreach (var row in sheet)
                {
                    var key = row.Key.ToString();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    items.Add((row.RowId, $"{key} [{row.RowId}]"));
                }
                items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                actionTimelineCache.AddRange(items);
            }
        }
        catch { }
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
        HelpMarker("Cinematic multi-stage victory sequence when the player dies. One random NPC performs choreographed approach with animations and optional grab constraint.");

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
                    selectedStageIndex = i;

                ImGui.TableNextColumn();
                ImGui.Text(s.EndTime < 0 ? $"{s.StartTime:F1}-∞" : $"{s.StartTime:F1}-{s.EndTime:F1}s");

                ImGui.TableNextColumn();
                ImGui.Text($"{s.StartDistance:F1}→{s.EndDistance:F1}");

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
                newStage.StartDistance = prev.EndDistance;
                newStage.EndDistance = prev.EndDistance;
            }
            stages.Add(newStage);
            selectedStageIndex = stages.Count - 1;
            config.Save();
        }

        // Selected stage detail editor
        if (selectedStageIndex >= 0 && selectedStageIndex < stages.Count)
        {
            ImGui.Separator();
            var s = stages[selectedStageIndex];
            ImGui.Text($"Stage {selectedStageIndex}");

            // Time range (coupled: start syncs prev end, end syncs next start)
            var idx = selectedStageIndex;
            var st = s.StartTime;
            if (ImGui.DragFloat("Start Time (s)##vsd", ref st, 0.1f, 0, 120, "%.1f"))
            {
                s.StartTime = st;
                if (idx > 0 && stages[idx - 1].EndTime >= 0)
                    stages[idx - 1].EndTime = st;
                config.Save();
            }

            var isInfinite = s.EndTime < 0;
            if (ImGui.Checkbox("Infinite##vsd", ref isInfinite))
            { s.EndTime = isInfinite ? -1f : s.StartTime + 3f; config.Save(); }
            ImGui.SameLine();
            if (!isInfinite)
            {
                var et = s.EndTime;
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragFloat("End Time (s)##vsd", ref et, 0.1f, 0, 120, "%.1f"))
                {
                    s.EndTime = et;
                    if (idx < stages.Count - 1)
                        stages[idx + 1].StartTime = et;
                    config.Save();
                }
            }
            else
            {
                ImGui.TextDisabled("End Time: infinite");
            }

            // Distances (coupled: start syncs prev end, end syncs next start)
            var sd = s.StartDistance;
            if (ImGui.DragFloat("Start Distance##vsd", ref sd, 0.1f, 0, 30, "%.1f"))
            {
                s.StartDistance = sd;
                if (idx > 0)
                    stages[idx - 1].EndDistance = sd;
                config.Save();
            }

            var ed = s.EndDistance;
            if (ImGui.DragFloat("End Distance##vsd", ref ed, 0.1f, 0, 30, "%.1f"))
            {
                s.EndDistance = ed;
                if (idx < stages.Count - 1)
                    stages[idx + 1].StartDistance = ed;
                config.Save();
            }

            var ho = s.HeightOffset;
            if (ImGui.DragFloat("Height Offset##vsd", ref ho, 0.01f, -5, 5, "%.2f"))
            { s.HeightOffset = ho; config.Save(); }

            // Behavior: action timeline or emote (toggle on right)
            var useEmote = s.UseEmote;
            if (useEmote)
            {
                // Emote mode
                var emoteIdx = FindEmoteIndex(s.EmoteId);
                var emoteName = emoteIdx < emoteCache!.Count ? emoteCache[emoteIdx].Name : "(None)";
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
                if (ImGui.BeginCombo("##behavvsd", emoteName))
                {
                    for (int i = 0; i < emoteCache.Count; i++)
                    {
                        var isSelected = i == emoteIdx;
                        if (ImGui.Selectable(emoteCache[i].Name, isSelected))
                        {
                            s.EmoteId = emoteCache[i].Id;
                            s.ActionTimelineId = 0;
                            ResolveEmoteTimelines(s);
                            config.Save();
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                if (s.EmoteId > 0)
                    ImGui.TextDisabled($"  Intro={s.ResolvedIntroTimeline} Loop={s.ResolvedLoopTimeline}");
            }
            else
            {
                // Action timeline mode
                int atIdx = 0;
                for (int i = 0; i < actionTimelineCache!.Count; i++)
                    if (actionTimelineCache[i].Id == s.ActionTimelineId) { atIdx = i; break; }
                var atName = atIdx < actionTimelineCache.Count ? actionTimelineCache[atIdx].Name : "(None)";
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
                if (ImGui.BeginCombo("##behavvsd", atName))
                {
                    for (int i = 0; i < actionTimelineCache.Count; i++)
                    {
                        var isSelected = i == atIdx;
                        if (ImGui.Selectable(actionTimelineCache[i].Name, isSelected))
                        {
                            s.ActionTimelineId = actionTimelineCache[i].Id;
                            s.EmoteId = 0;
                            s.ResolvedIntroTimeline = 0;
                            s.ResolvedLoopTimeline = 0;
                            config.Save();
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Emote##modevsd", ref useEmote))
            { s.UseEmote = useEmote; config.Save(); }
            ImGui.SameLine();
            ImGui.Text("Behavior");

            // Grab section
            var grab = s.GrabEnabled;
            if (ImGui.Checkbox("Grab Enabled##vsd", ref grab))
            { s.GrabEnabled = grab; config.Save(); }

            if (s.GrabEnabled)
            {
                // NPC bone dropdown (dynamic — refreshable from selected target)
                var npcIdx = FindBoneIndex(npcBoneList, s.NpcBoneName);
                if (ImGui.Combo("NPC Bone##vsd", ref npcIdx, npcBoneList, npcBoneList.Length))
                { s.NpcBoneName = npcBoneList[npcIdx]; config.Save(); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh##npcbones"))
                    RefreshNpcBones();

                // Player bone dropdown
                var playerIdx = FindBoneIndex(playerBoneList, s.PlayerBoneName);
                if (ImGui.Combo("Player Bone##vsd", ref playerIdx, playerBoneList, playerBoneList.Length))
                { s.PlayerBoneName = playerBoneList[playerIdx]; config.Save(); }

                // Grab physics tweaks
                ImGui.Separator();
                ImGui.TextDisabled("Grab Physics");
                if (ImGui.SmallButton("Reset Defaults##grab"))
                {
                    s.GrabForce = 1000f;
                    s.GrabSpeed = 50f;
                    s.GrabSpringFreq = 120f;
                    config.Save();
                }

                var gf = s.GrabForce;
                if (ImGui.DragFloat("Force##grab", ref gf, 10f, 10, 5000, "%.0f"))
                { s.GrabForce = gf; config.Save(); }

                var gs = s.GrabSpeed;
                if (ImGui.DragFloat("Speed##grab", ref gs, 1f, 1, 200, "%.0f"))
                { s.GrabSpeed = gs; config.Save(); }

                var gsf = s.GrabSpringFreq;
                if (ImGui.DragFloat("Spring Freq##grab", ref gsf, 5f, 10, 500, "%.0f"))
                { s.GrabSpringFreq = gsf; config.Save(); }
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
        if (actionTimelineCache == null) return "?";
        for (int i = 0; i < actionTimelineCache.Count; i++)
            if (actionTimelineCache[i].Id == atId)
            {
                var name = actionTimelineCache[i].Name;
                var bracket = name.LastIndexOf(" [");
                return bracket > 0 ? name[..bracket] : name;
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
