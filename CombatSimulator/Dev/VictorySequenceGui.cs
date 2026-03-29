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
    private int selectedStageIndex = -1;

    // Cached emote list (alphabetically sorted, loaded once)
    private List<(uint Id, string Name)>? emoteCache;

    // Known bone names for dropdowns
    private static readonly string[] NpcBones =
    {
        "j_te_r", "j_te_l",           // hands (right/left)
        "j_ude_b_r", "j_ude_b_l",     // forearms
        "j_ude_a_r", "j_ude_a_l",     // upper arms
        "j_asi_c_r", "j_asi_c_l",     // feet
        "j_asi_b_r", "j_asi_b_l",     // lower legs
        "j_asi_a_r", "j_asi_a_l",     // upper legs
        "j_kao",                        // head
        "j_kubi",                       // neck
        "j_sebo_c",                     // chest
        "j_kosi",                       // pelvis
    };

    private static readonly string[] PlayerBones =
    {
        "j_kubi",                       // neck
        "j_kao",                        // head
        "j_sebo_c",                     // chest
        "j_sebo_b",                     // mid spine
        "j_kosi",                       // pelvis
        "n_hara",                       // waist
        "j_te_r", "j_te_l",           // hands
    };

    public VictorySequenceGui(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
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

    /// <summary>Resolve emote ID to intro + loop timeline IDs from Lumina.</summary>
    private void ResolveEmoteTimelines(VictorySequenceStage stage)
    {
        stage.AnimationTimelineId = 0;
        stage.LoopTimelineId = 0;
        if (stage.EmoteId == 0) return;
        try
        {
            var emoteSheet = Core.Services.DataManager.GetExcelSheet<Emote>();
            if (emoteSheet == null) return;
            var emote = emoteSheet.GetRow(stage.EmoteId);
            stage.LoopTimelineId = (ushort)emote.ActionTimeline[0].RowId;
            stage.AnimationTimelineId = (ushort)emote.ActionTimeline[1].RowId;
        }
        catch { }
    }

    public void Draw()
    {
        EnsureEmoteCache();
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
            ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Grab", ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            int deleteIdx = -1, moveUpIdx = -1, moveDownIdx = -1;

            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i}##sel{i}", selectedStageIndex == i, ImGuiSelectableFlags.SpanAllColumns))
                    selectedStageIndex = i;

                ImGui.TableNextColumn();
                ImGui.Text($"{s.StartTime:F1}-{s.EndTime:F1}s");

                ImGui.TableNextColumn();
                ImGui.Text($"{s.StartDistance:F1}→{s.EndDistance:F1}");

                ImGui.TableNextColumn();
                var emoteName = s.EmoteId > 0 ? FindEmoteName(s.EmoteId) : "-";
                ImGui.Text(emoteName);

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
            stages.Add(new VictorySequenceStage());
            selectedStageIndex = stages.Count - 1;
            config.Save();
        }
        if (stages.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove All##vseq"))
            {
                stages.Clear();
                selectedStageIndex = -1;
                config.Save();
            }
        }

        // Selected stage detail editor
        if (selectedStageIndex >= 0 && selectedStageIndex < stages.Count)
        {
            ImGui.Separator();
            var s = stages[selectedStageIndex];
            ImGui.Text($"Stage {selectedStageIndex}");

            // Time range
            var st = s.StartTime;
            if (ImGui.DragFloat("Start Time (s)##vsd", ref st, 0.1f, 0, 120, "%.1f"))
            { s.StartTime = st; config.Save(); }

            var et = s.EndTime;
            if (ImGui.DragFloat("End Time (s)##vsd", ref et, 0.1f, 0, 120, "%.1f"))
            { s.EndTime = et; config.Save(); }

            // Distances
            var sd = s.StartDistance;
            if (ImGui.DragFloat("Start Distance##vsd", ref sd, 0.1f, 0, 30, "%.1f"))
            { s.StartDistance = sd; config.Save(); }

            var ed = s.EndDistance;
            if (ImGui.DragFloat("End Distance##vsd", ref ed, 0.1f, 0, 30, "%.1f"))
            { s.EndDistance = ed; config.Save(); }

            var ho = s.HeightOffset;
            if (ImGui.DragFloat("Height Offset##vsd", ref ho, 0.01f, -5, 5, "%.2f"))
            { s.HeightOffset = ho; config.Save(); }

            // Emote dropdown
            var emoteIdx = FindEmoteIndex(s.EmoteId);
            var emoteName = emoteIdx < emoteCache!.Count ? emoteCache[emoteIdx].Name : "(None)";
            if (ImGui.BeginCombo("Emote##vsd", emoteName))
            {
                for (int i = 0; i < emoteCache.Count; i++)
                {
                    var isSelected = i == emoteIdx;
                    if (ImGui.Selectable(emoteCache[i].Name, isSelected))
                    {
                        s.EmoteId = emoteCache[i].Id;
                        ResolveEmoteTimelines(s);
                        config.Save();
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (s.EmoteId > 0)
                ImGui.TextDisabled($"  Intro={s.AnimationTimelineId} Loop={s.LoopTimelineId}");

            // Grab section
            var grab = s.GrabEnabled;
            if (ImGui.Checkbox("Grab Enabled##vsd", ref grab))
            { s.GrabEnabled = grab; config.Save(); }

            if (s.GrabEnabled)
            {
                // NPC bone dropdown
                var npcIdx = FindBoneIndex(NpcBones, s.NpcBoneName);
                if (ImGui.Combo("NPC Bone##vsd", ref npcIdx, NpcBones, NpcBones.Length))
                { s.NpcBoneName = NpcBones[npcIdx]; config.Save(); }

                // Player bone dropdown
                var playerIdx = FindBoneIndex(PlayerBones, s.PlayerBoneName);
                if (ImGui.Combo("Player Bone##vsd", ref playerIdx, PlayerBones, PlayerBones.Length))
                { s.PlayerBoneName = PlayerBones[playerIdx]; config.Save(); }
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
                // Return just the name part (before the [id])
                var name = emoteCache[i].Name;
                var bracket = name.LastIndexOf(" [");
                return bracket > 0 ? name[..bracket] : name;
            }
        return $"#{emoteId}";
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
