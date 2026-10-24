// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Voice;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    /// <summary>Set by the plugin after construction; the section is hidden while null.</summary>
    public VoiceDirector? VoiceDirector { get; set; }

    private static readonly string[] VoiceAuditionKinds = { "PC battle shout", "NPC battle voice", "Monster SE pack", "Current target", "Voiced line" };
    private static readonly string[] VoiceCategoryNames = { "Player (own sounds slider)", "Party", "Other (others' sounds slider)", "Bypass volume rules" };
    private static readonly int[] VoiceCategoryValues = { 0, 1, 2, 6 };
    private IReadOnlyList<string>? voicePcCodes;
    private int voiceAuditionKind;
    private int voicePcIndex;
    private int voiceNpcNumber = 1;
    private int voiceSePack = 3091;
    private int voiceYellId = 8202037;
    private int voiceEntry;
    private bool voiceAuditionPositional;

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
        HelpMarker("Human enemies call out battle lines during a fight (engaging, attacking, taking hits, low HP, kills, victory, dying) with the game's own voice recordings and a speech bubble. Voices are matched to the enemy's gender; monsters stay quiet. Everything plays locally; nothing is sent to the server.");

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
        if (config.EnableEnemyVoices)
            ImGui.TextDisabled($"Lines spoken {director.LinesSpoken}, sounds played {director.Player.PlayCount}. {director.LastVoiceDescription}");

        var lines = config.EnemyVoiceLines;
        if (ImGui.Checkbox("Voiced battle lines##enemyVoice", ref lines))
        {
            config.EnemyVoiceLines = lines;
            config.Save();
        }
        var pool = director.Lines;
        HelpMarker($"The built-in pool of {pool.Count} recorded battle lines. Off: only the labeled shouts below play.");
        if (lines)
            SliderFloatSaved("Quiet between lines (s)##enemyVoice", () => config.EnemyVoiceLineGap, v => config.EnemyVoiceLineGap = v, 0f, 15f,
                "Silence after each line before an ordinary moment (skill, hit, taunt) may start the next. Engage, kills, victory and deaths only wait half a second. Only one line is ever on screen at a time.");

        {
            var categoryIndex = Math.Max(0, Array.IndexOf(VoiceCategoryValues, config.EnemyVoiceVolumeCategory));
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo("Volume category##enemyVoice", ref categoryIndex, VoiceCategoryNames, VoiceCategoryNames.Length))
            {
                config.EnemyVoiceVolumeCategory = VoiceCategoryValues[categoryIndex];
                config.Save();
            }
            HelpMarker("Which of the game's sound settings scales the voices (System Configuration > Sound > Player Sound Settings). Which slider actually applies is part of the audition checklist; 'Bypass' ignores them.");

            var positional = config.EnemyVoicePositional;
            if (ImGui.Checkbox("Positional (3D)##enemyVoice", ref positional))
            {
                config.EnemyVoicePositional = positional;
                config.Save();
            }
            HelpMarker("Play each voice at the speaking enemy's position. Turn off if voices are inaudible or pan wrongly; they then play flat.");

            IntSliderSaved("Closest speakers##enemyVoice", () => config.EnemyVoiceClosestSpeakers, v => config.EnemyVoiceClosestSpeakers = v, 1, 12,
                "Only this many enemies nearest to you may speak at all.");
            IntSliderSaved("Max concurrent##enemyVoice", () => config.EnemyVoiceMaxConcurrent, v => config.EnemyVoiceMaxConcurrent = v, 1, 6,
                "Upper bound on overlapping voices.");
            SliderFloatSaved("Voices per second##enemyVoice", () => config.EnemyVoicesPerSecond, v => config.EnemyVoicesPerSecond = v, 0.2f, 4f,
                "Global budget across all enemies. Deaths, killing blows and victory ignore it (they happen once per fight).");

            var appearance = config.EnemyVoiceHumanoidUseAppearance;
            if (ImGui.Checkbox("Humanoid field enemies use appearance voices##enemyVoice", ref appearance))
            {
                config.EnemyVoiceHumanoidUseAppearance = appearance;
                config.Save();
            }
            HelpMarker("Humanoid map enemies normally get a generic NPC battle voice (stable per enemy type, gender not matched). On: use the player shout that matches their race and gender instead. Player clones always use appearance voices.");

            var taunts = config.EnemyVoiceIdleTaunts;
            if (ImGui.Checkbox("Idle taunts##enemyVoice", ref taunts))
            {
                config.EnemyVoiceIdleTaunts = taunts;
                config.Save();
            }
            HelpMarker("Engaged enemies occasionally taunt between attacks (a voiced line, or a 'taunt'-labeled shout).");
            if (taunts)
                SliderFloatSaved("Taunt interval (s)##enemyVoice", () => config.EnemyVoiceTauntInterval, v => config.EnemyVoiceTauntInterval = v, 5f, 120f,
                    "Average seconds between taunts of one enemy (never more often than every 20 s).");

            DrawVoiceAudition(director);
        }
    }

    private void IntSliderSaved(string label, Func<int> get, Action<int> set, int min, int max, string help)
    {
        var v = get();
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt(label, ref v, min, max))
        {
            set(Math.Clamp(v, min, max));
            config.Save();
        }
        HelpMarker(help);
    }

    private void DrawVoiceAudition(VoiceDirector director)
    {
        if (!ImGui.TreeNode("Audition and shout labels##enemyVoice"))
            return;

        ImGui.SetNextItemWidth(200f);
        ImGui.Combo("Source##voiceAudition", ref voiceAuditionKind, VoiceAuditionKinds, VoiceAuditionKinds.Length);
        HelpMarker("Pick any game voice file and play its entries one by one. Label what each entry sounds like; the director only plays labeled entries.");

        var source = VoiceSource.None;
        string? note = null;
        switch (voiceAuditionKind)
        {
            case 0:
                voicePcCodes ??= VoiceSelection.AllPcShoutCodes();
                var codes = voicePcCodes.ToArray();
                ImGui.SetNextItemWidth(200f);
                ImGui.Combo("Race/gender/variant##voiceAudition", ref voicePcIndex, codes, codes.Length);
                source = new VoiceSource(VoiceSourceKind.PcShout, codes[Math.Clamp(voicePcIndex, 0, codes.Length - 1)], 0);
                note = "Labels recorded here apply to all 42 player shout files (they share one layout).";
                break;
            case 1:
                ImGui.SetNextItemWidth(200f);
                if (ImGui.InputInt("NPC voice (1-84)##voiceAudition", ref voiceNpcNumber))
                    voiceNpcNumber = Math.Clamp(voiceNpcNumber, 1, VoiceSelection.NpcBattleVoiceCount);
                source = new VoiceSource(VoiceSourceKind.NpcBattle, string.Empty, (uint)voiceNpcNumber);
                note = "Labels recorded here apply to all 84 NPC battle voice files.";
                break;
            case 2:
                ImGui.SetNextItemWidth(200f);
                if (ImGui.InputInt("SE pack id##voiceAudition", ref voiceSePack))
                    voiceSePack = Math.Max(1, voiceSePack);
                source = VoiceSelection.MonsterFromSePack((uint)voiceSePack);
                note = "Labels recorded here apply to every monster model using this SE pack.";
                break;
            case 3:
                source = ResolveTargetVoice(director, out note);
                break;
            case 4:
                ImGui.SetNextItemWidth(200f);
                if (ImGui.InputInt("vo_line id (8201000+)##voiceAudition", ref voiceYellId))
                    voiceYellId = Math.Max((int)VoiceSelection.YellLineBase, voiceYellId);
                source = new VoiceSource(VoiceSourceKind.YellLine, string.Empty, (uint)voiceYellId - VoiceSelection.YellLineBase);
                // A vo_line id is not an NpcYell row number; only curated lines have known text.
                var curated = director.Lines.Entries.FirstOrDefault(e => e.VoiceId == (uint)voiceYellId);
                var yell = curated == null ? null : dataManager.GetExcelSheet<NpcYell>()?.GetRowOrDefault(curated.YellId);
                ImGui.TextWrapped(yell != null
                    ? $"Text (NpcYell {curated!.YellId}): {yell.Value.Text.ExtractText()}  [{string.Join(", ", curated.Events)}; {curated.Voice}{(curated.Story ? "; story" : string.Empty)}]"
                    : "Not in the curated pool: text unknown.");
                if (ImGui.Button("Next curated line##voiceAudition") && director.Lines.Count > 0)
                {
                    var entries = director.Lines.Entries;
                    var index = entries.ToList().FindIndex(e => e.VoiceId == (uint)voiceYellId);
                    voiceYellId = (int)entries[(index + 1) % entries.Count].VoiceId;
                }
                note = "Plays the voiced line file in your client language. Curated lines are what enemies say in combat.";
                break;
        }

        if (note != null)
            ImGui.TextDisabled(note);
        if (source.IsNone)
        {
            ImGui.TreePop();
            return;
        }

        var path = director.ResolvePath(source);
        var entryCount = director.ReadEntryCount(path);
        ImGui.TextUnformatted(path != null ? $"{source}: {path} ({entryCount} entries)" : $"{source}: no file in any language");

        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputInt("Entry##voiceAudition", ref voiceEntry))
            voiceEntry = Math.Clamp(voiceEntry, 0, Math.Max(0, entryCount - 1));
        ImGui.SameLine();
        ImGui.Checkbox("At my position##voiceAudition", ref voiceAuditionPositional);
        HelpMarker("Off: play flat (always audible). On: play positionally at your character, to compare with in-combat 3D playback.");

        using (ImRaii.Disabled(path == null))
        {
            if (ImGui.Button("Play##voiceAudition"))
                director.RequestAudition(source, (uint)voiceEntry, voiceAuditionPositional);
            ImGui.SameLine();
            if (ImGui.Button("Next##voiceAudition"))
            {
                voiceEntry = entryCount > 0 ? (voiceEntry + 1) % entryCount : voiceEntry + 1;
                director.RequestAudition(source, (uint)voiceEntry, voiceAuditionPositional);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop##voiceAudition"))
            director.StopAudition();

        if (source.Kind != VoiceSourceKind.YellLine)
            DrawVoiceLabelEditor(director, source.LabelKey, voiceEntry);

        ImGui.TreePop();
    }

    private VoiceSource ResolveTargetVoice(VoiceDirector director, out string? note)
    {
        var target = Core.Services.ObjectTable.LocalPlayer?.TargetObject;
        if (target == null)
        {
            note = "No target.";
            return VoiceSource.None;
        }
        var tracked = npcSelector.SelectedNpcs.FirstOrDefault(n => n.Address == target.Address);
        var baseId = tracked?.BNpcBaseId ?? (target as Dalamud.Game.ClientState.Objects.Types.IBattleNpc)?.BaseId ?? 0;
        var source = director.Resolver.Resolve(new VoiceSpeaker(target.Address, (uint)target.GameObjectId, baseId,
            tracked?.IsClientControlled == true));
        note = $"{target.Name}: {source}";
        return source;
    }

    private void DrawVoiceLabelEditor(VoiceDirector director, string key, int entry)
    {
        ImGui.TextUnformatted($"Entry {entry} is:");
        foreach (var label in VoiceLabels.All)
        {
            ImGui.SameLine();
            var current = director.Labels.Get(key, label);
            var on = current.Contains(entry);
            if (!ImGui.Checkbox($"{label}##voiceLabel{label}", ref on))
                continue;
            var updated = current.Where(n => n != entry).ToList();
            if (on)
                updated.Add(entry);
            updated.Sort();
            if (!config.EnemyVoiceEntryLabels.TryGetValue(key, out var labels))
                config.EnemyVoiceEntryLabels[key] = labels = new Dictionary<string, List<int>>();
            labels[label] = updated;
            config.Save();
            director.ReloadLabels();
        }
        HelpMarker("Saved to your config and used immediately. Engage falls back to attack, low HP to hurt/engage, victory to taunt; nothing falls back to an unlabeled entry.");

        var summary = string.Join("  ", VoiceLabels.All
            .Select(l => (l, director.Labels.Get(key, l)))
            .Where(x => x.Item2.Count > 0)
            .Select(x => $"{x.l}: {string.Join(",", x.Item2)}"));
        ImGui.TextDisabled(summary.Length > 0 ? $"[{key}] {summary}" : $"[{key}] no labels yet (silent in combat)");
    }
}
