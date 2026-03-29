using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Dev;

public class VictorySequenceGui
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private int selectedStageIndex = -1;

    public VictorySequenceGui(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    public void Draw()
    {
        ImGui.Separator();
        ImGui.Text("Victory Cinematic");

        var enabled = config.EnableVictorySequence;
        if (ImGui.Checkbox("Enable Victory Sequence##dev", ref enabled))
        {
            config.EnableVictorySequence = enabled;
            config.Save();
        }
        HelpMarker("Cinematic multi-stage victory sequence when the player dies. One random surviving NPC performs a choreographed approach with configurable animations and grab constraints.");

        if (!config.EnableVictorySequence) return;

        ImGui.Indent();
        var stages = config.VictorySequenceStages;

        // Stage list
        if (ImGui.BeginTable("##vseqstages", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Anim", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Grab", ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            int deleteIdx = -1;
            int moveUpIdx = -1;
            int moveDownIdx = -1;

            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i}##sel{i}", selectedStageIndex == i, ImGuiSelectableFlags.SpanAllColumns))
                    selectedStageIndex = i;

                ImGui.TableNextColumn();
                ImGui.Text(s.Label);

                ImGui.TableNextColumn();
                ImGui.Text($"{s.StartTime:F1}-{s.EndTime:F1}s");

                ImGui.TableNextColumn();
                ImGui.Text($"{s.StartDistance:F1}→{s.EndDistance:F1}");

                ImGui.TableNextColumn();
                ImGui.Text(s.AnimationTimelineId > 0 ? $"{s.AnimationTimelineId}" : "-");

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

            // Process actions
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

        // Buttons
        if (ImGui.Button("+ Add Stage##vseq"))
        {
            stages.Add(new VictorySequenceStage());
            selectedStageIndex = stages.Count - 1;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Load Defaults##vseq"))
        {
            stages.Clear();
            stages.AddRange(GetDefaultStages());
            selectedStageIndex = -1;
            config.Save();
        }

        // Selected stage detail editor
        if (selectedStageIndex >= 0 && selectedStageIndex < stages.Count)
        {
            ImGui.Separator();
            var s = stages[selectedStageIndex];

            ImGui.Text($"Stage {selectedStageIndex} Detail");

            var label = s.Label;
            if (ImGui.InputText("Label##vsd", ref label, 32))
            { s.Label = label; config.Save(); }

            var st = s.StartTime;
            if (ImGui.DragFloat("Start Time (s)##vsd", ref st, 0.1f, 0, 60, "%.1f"))
            { s.StartTime = st; config.Save(); }

            var et = s.EndTime;
            if (ImGui.DragFloat("End Time (s)##vsd", ref et, 0.1f, 0, 60, "%.1f"))
            { s.EndTime = et; config.Save(); }

            var sd = s.StartDistance;
            if (ImGui.DragFloat("Start Distance##vsd", ref sd, 0.1f, 0, 30, "%.1f"))
            { s.StartDistance = sd; config.Save(); }

            var ed = s.EndDistance;
            if (ImGui.DragFloat("End Distance##vsd", ref ed, 0.1f, 0, 30, "%.1f"))
            { s.EndDistance = ed; config.Save(); }

            var ho = s.HeightOffset;
            if (ImGui.DragFloat("Height Offset##vsd", ref ho, 0.01f, -5, 5, "%.2f"))
            { s.HeightOffset = ho; config.Save(); }

            int animId = s.AnimationTimelineId;
            if (ImGui.InputInt("Intro/One-Shot Timeline##vsd", ref animId))
            { s.AnimationTimelineId = (ushort)System.Math.Max(0, animId); config.Save(); }

            int loopId = s.LoopTimelineId;
            if (ImGui.InputInt("Loop Timeline##vsd", ref loopId))
            { s.LoopTimelineId = (ushort)System.Math.Max(0, loopId); config.Save(); }

            var grab = s.GrabEnabled;
            if (ImGui.Checkbox("Grab Enabled##vsd", ref grab))
            { s.GrabEnabled = grab; config.Save(); }

            if (s.GrabEnabled)
            {
                var nb = s.NpcBoneName;
                if (ImGui.InputText("NPC Bone##vsd", ref nb, 32))
                { s.NpcBoneName = nb; config.Save(); }

                var pb = s.PlayerBoneName;
                if (ImGui.InputText("Player Bone##vsd", ref pb, 32))
                { s.PlayerBoneName = pb; config.Save(); }
            }
        }

        ImGui.Unindent();
    }

    private static List<VictorySequenceStage> GetDefaultStages()
    {
        return new List<VictorySequenceStage>
        {
            new() { Label = "Position", StartTime = 0, EndTime = 0.1f, StartDistance = 5.0f, EndDistance = 5.0f },
            new() { Label = "Walk", StartTime = 0.1f, EndTime = 5.0f, StartDistance = 5.0f, EndDistance = 1.5f },
            new() { Label = "Bend Down", StartTime = 5.0f, EndTime = 8.0f, StartDistance = 1.5f, EndDistance = 1.5f },
            new() { Label = "Grab & Raise", StartTime = 8.0f, EndTime = 12.0f, StartDistance = 1.5f, EndDistance = 1.5f, GrabEnabled = true },
        };
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
