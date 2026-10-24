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

            var guarantee = config.EnemyVoiceGuaranteeKills;
            if (ImGui.Checkbox("Always call out kills##enemyVoice", ref guarantee))
            {
                config.EnemyVoiceGuaranteeKills = guarantee;
                config.Save();
            }
            HelpMarker("When the player or a companion falls, an enemy always says a kill line: it waits for the killer to finish its own shout, and if the killer cannot speak (already dead, no line) the nearest enemy that can says it instead.");

            var defeatTaunts = config.EnemyVoiceDefeatTaunts;
            if (ImGui.Checkbox("Taunt the fallen##enemyVoice", ref defeatTaunts))
            {
                config.EnemyVoiceDefeatTaunts = defeatTaunts;
                config.Save();
            }
            HelpMarker("After the party is wiped, surviving enemies keep gloating until you revive or reset, each in its own voice with its kill, victory and taunt lines.");
            if (defeatTaunts)
            {
                ImGui.Indent();
                SliderFloatSaved("Every (s)##enemyVoiceDefeat", () => config.EnemyVoiceDefeatTauntInterval, v => config.EnemyVoiceDefeatTauntInterval = v, 1f, 30f,
                    "Seconds between taunts. Short intervals let several enemies talk at once, each under its own bubble.");
                ImGui.Unindent();
            }

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

    private void DrawEnemyVoiceAdvanced(VoiceDirector director)
    {
        SliderFloatSaved("Pause between lines (s)##enemyVoice", () => config.EnemyVoiceLineGap, v => config.EnemyVoiceLineGap = v, 0f, 15f,
            "How chatty enemies are: the quiet after each line before an ordinary moment (skill, hit, taunt) may start the next, and how often engaged enemies taunt between attacks. Engage, kills, victory and deaths only wait half a second.");

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
        ImGui.SliderInt("Voice type##enemyVoiceFilter", ref voicePickerCategory, 0, categories.Count - 1, categories[voicePickerCategory]);
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
