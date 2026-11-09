// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Voice;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    // Voice picker filter: 0 = all voices, else an index into the categories present.
    private int voicePickerCategory;

    private static readonly string[] VoiceCategoryOrder =
    {
        "Male young", "Male mature", "Male elderly", "Female young", "Female adult", "Female elderly", "Boss",
    };

    /// <summary>Set by the plugin after construction; the section is hidden while null.</summary>
    public VoiceDirector? VoiceDirector { get; set; }

    private void DrawEnemyVoiceSection()
    {
        var director = VoiceDirector;
        if (director == null || !ImGui.CollapsingHeader("Enemy Voices"))
            return;

        var enabled = config.EnableEnemyVoices;
        if (ImGui.Checkbox("Enemies talk", ref enabled))
        {
            config.EnableEnemyVoices = enabled;
            config.Save();
        }
        HelpMarker("Enemies call out battle lines during a fight (engaging, attacking, taking hits, low HP, kills, victory, dying) with the game's own voice recordings and a speech bubble. Each enemy keeps one voice: humans one that matches their gender, monsters a boss voice. Everything plays locally; nothing is sent to the server.");

        using (ImRaii.Disabled(!enabled))
        {
            SliderFloatSaved("Voice volume##enemyVoice", () => config.EnemyVoiceVolume, v => config.EnemyVoiceVolume = v, 0f, 1f,
                "Volume handed to the game's sound player, before the game's own volume sliders.");

            var bubbles = config.EnemyVoiceBubbles;
            if (ImGui.Checkbox("Speech bubbles##enemyVoice", ref bubbles))
            {
                config.EnemyVoiceBubbles = bubbles;
                config.Save();
            }
            HelpMarker("Show what the enemy says in the game's overhead speech bubble.");

            var taunt = !config.EnemyVoiceDefeatTaunts ? 0
                : Array.FindIndex(DefeatTauntIntervals, s => s > 0f && MathF.Abs(s - config.EnemyVoiceDefeatTauntInterval) < 0.01f);
            ImGui.SetNextItemWidth(160f);
            if (PresetCombo("Taunt the fallen##enemyVoice", DefeatTauntLabels, taunt, out var pickedTaunt))
            {
                config.EnemyVoiceDefeatTaunts = pickedTaunt > 0;
                if (pickedTaunt > 0) config.EnemyVoiceDefeatTauntInterval = DefeatTauntIntervals[pickedTaunt];
                config.Save();
            }
            HelpMarker("After the party is wiped, surviving enemies keep gloating until you revive or reset, each in its own voice. Short intervals let several enemies talk at once, each under its own bubble.");

            var player = director.Player;
            if (player.FailureReason != null)
                ImGui.TextColored(new System.Numerics.Vector4(1f, .45f, .35f, 1f), $"Voice playback unavailable: {player.FailureReason}");

            if (ImGui.TreeNode("Advanced##enemyVoice"))
            {
                DrawEnemyVoiceAdvanced(director);
                ImGui.TreePop();
            }
        }
    }

    private static readonly string[] DefeatTauntLabels = { "Off", "Every 8 s", "Every 4 s", "Every 1 s" };
    private static readonly float[] DefeatTauntIntervals = { 0f, 8f, 4f, 1f };
    private static readonly string[] ChattinessLabels = { "Quiet", "Normal", "Chatty" };
    private static readonly float[] ChattinessLineGaps = { 6f, 3.5f, 1.5f };

    private void DrawEnemyVoiceAdvanced(VoiceDirector director)
    {
        var chattiness = Array.FindIndex(ChattinessLineGaps, g => MathF.Abs(g - config.EnemyVoiceLineGap) < 0.01f);
        ImGui.SetNextItemWidth(160f);
        if (PresetCombo("Chattiness##enemyVoice", ChattinessLabels, chattiness, out var pickedChattiness))
        {
            config.EnemyVoiceLineGap = ChattinessLineGaps[pickedChattiness];
            config.Save();
        }
        HelpMarker("How long enemies stay quiet after a line before an ordinary moment (skill, hit, taunt) may start the next. Engage, kills, victory and deaths only wait half a second.");

        var enemies = director.VoiceEnemies();
        if (enemies.Count == 0)
        {
            ImGui.TextDisabled(director.PersonasActive
                ? "Start a fight to choose each enemy's voice."
                : "Voice choice needs a Japanese or English client.");
            return;
        }

        // Category filter for the voice lists below; any voice can go to any enemy.
        var categories = new List<string> { "All" };
        foreach (var name in VoiceCategoryOrder)
            if (enemies[0].Choices.Any(c => c.Category == name))
                categories.Add(name);
        voicePickerCategory = Math.Clamp(voicePickerCategory, 0, categories.Count - 1);
        ImGui.SetNextItemWidth(220f);
        ImGui.Combo("Voice type##enemyVoiceFilter", ref voicePickerCategory, categories.ToArray(), categories.Count);
        var filter = voicePickerCategory == 0 ? null : categories[voicePickerCategory];

        foreach (var enemy in enemies)
        {
            using var id = ImRaii.PushId((int)enemy.EntityId);
            var current = enemy.ChosenId != null && enemy.Choices.Any(c => c.Id == enemy.ChosenId)
                ? enemy.Choices.First(c => c.Id == enemy.ChosenId).Label
                : enemy.Current != null ? $"Auto ({enemy.Choices.FirstOrDefault(c => c.Id == enemy.Current.Id).Label ?? enemy.Current.Id})" : "Auto";
            ImGui.SetNextItemWidth(220f);
            if (ImGui.BeginCombo(string.IsNullOrEmpty(enemy.Name) ? "?" : enemy.Name, current))
            {
                if (ImGui.Selectable("Auto", enemy.ChosenId == null))
                    director.SetVoiceChoice(enemy.EntityId, null);
                foreach (var (personaId, label, category) in enemy.Choices)
                {
                    if (filter != null && category != filter)
                        continue;
                    var shown = filter == null ? $"{label}  [{category}]" : label;
                    if (ImGui.Selectable(shown, personaId == enemy.ChosenId))
                        director.SetVoiceChoice(enemy.EntityId, personaId);
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(enemy.Current == null))
            {
                if (ImGui.SmallButton("Preview"))
                    director.RequestPreview(enemy.EntityId);
            }
        }
    }
}
