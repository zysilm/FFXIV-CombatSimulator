using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Spectators;
using CombatSimulator.Npcs;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Gui;

/// <summary>Standalone experimental spectator crowd UI.</summary>
public partial class MainWindow
{
    private readonly SpectatorController spectatorController;
    private string spectatorSearchFilter = string.Empty;
    private NpcCatalogEntry? selectedSpectatorEntry;
    private string spectatorEmoteSearch = string.Empty;
    private List<SpectatorEmoteChoice>? spectatorEmoteCache;
    private uint selectedSpectatorEmoteId;
    private string spectatorNewBattleChatterLine = string.Empty;
    private string spectatorNewDecisiveChatterLine = string.Empty;

    private void DrawSpectatorsTab()
    {
        npcCatalog ??= new NpcCatalog(dataManager, log);
        config.SpectatorEmoteIds ??= SpectatorController.CreateDefaultEmoteIds();
        config.SpectatorExcludedNames ??= new List<string>();
        config.SpectatorExcludedENpcIds ??= new List<uint>();
        config.SpectatorBattleChatterLines ??= SpectatorController.CreateDefaultBattleChatterLines();
        config.SpectatorDecisiveChatterLines ??= SpectatorController.CreateDefaultDecisiveChatterLines();
        MigrateSpectatorExclusionsToNames();
        ResolveSavedSpectatorSelection();

        ImGui.TextDisabled("Stationary, client-only, non-combat actors. Maximum 200.");
        ImGui.Spacing();

        DrawSpectatorCharacterPicker();
        ImGui.Spacing();
        var exclusionCount = DistinctSpectatorExcludedNames().Count;
        if (ImGui.CollapsingHeader($"Random Exclusions ({exclusionCount})###spectatorExclusions"))
        {
            ImGui.Indent();
            DrawSpectatorExclusions();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Spawn & Formation##spectatorFormation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawSpectatorFormation();
            ImGui.Unindent();
        }

        var emoteCount = config.SpectatorEmoteIds.Count;
        if (ImGui.CollapsingHeader($"Random Emotes ({emoteCount})###spectatorEmotes"))
        {
            ImGui.Indent();
            DrawSpectatorEmotePool();
            ImGui.Unindent();
        }

        var chatterState = config.SpectatorChatterEnabled ? "on" : "off";
        if (ImGui.CollapsingHeader(
                $"Crowd Chatter ({chatterState}, {config.SpectatorBattleChatterLines.Count + config.SpectatorDecisiveChatterLines.Count} lines)###spectatorChatter"))
        {
            ImGui.Indent();
            DrawSpectatorChatter();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Current Crowd##spectatorCrowd", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawSpectatorCrowdStatus();
            ImGui.Unindent();
        }
    }

    private void DrawSpectatorCharacterPicker()
    {
        ImGui.TextDisabled("Human character");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint(
            "##spectatorCharacterSearch",
            "Search adult human NPC name...",
            ref spectatorSearchFilter,
            256);

        var entries = npcCatalog!.Search(spectatorSearchFilter, NpcCatalogType.Human)
            .Where(entry => spectatorController!.IsEmoteCompatibleAppearance(entry.Id))
            .ToList();
        var excludedNames = DistinctSpectatorExcludedNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 9;
        if (ImGui.BeginListBox("##spectatorCharacterList", new Vector2(-1, listHeight)))
        {
            if (entries.Count == 0)
            {
                ImGui.TextDisabled("No emote-compatible adult humans found.");
            }
            else
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var selected = selectedSpectatorEntry?.Id == entry.Id &&
                                   selectedSpectatorEntry.Type == entry.Type;
                    var excludedSuffix = excludedNames.Contains(NormalizeSpectatorName(entry.Name))
                        ? " [excluded from random]"
                        : string.Empty;
                    if (!ImGui.Selectable($"{entry.Name}{excludedSuffix}##spectatorHuman{entry.Id}", selected))
                        continue;

                    selectedSpectatorEntry = entry;
                    config.SpectatorHumanENpcId = entry.Id;
                    config.Save();
                }
            }
            ImGui.EndListBox();
        }

        if (selectedSpectatorEntry != null)
            ImGui.TextWrapped($"Selected: {selectedSpectatorEntry.Name} [{selectedSpectatorEntry.Id}]");
        else
            ImGui.TextDisabled("No adult appearance selected.");
    }

    private void DrawSpectatorFormation()
    {
        var count = Math.Clamp(config.SpectatorSpawnCount, 1, SpectatorController.MaxSpectators);
        ImGui.TextDisabled("Random crowd size");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputInt("##spectatorCount", ref count))
        {
            config.SpectatorSpawnCount = Math.Clamp(count, 1, SpectatorController.MaxSpectators);
            config.Save();
        }

        var distance = Math.Clamp(config.SpectatorDistance, 1f, 100f);
        ImGui.TextDisabled("Distance (+/-25%)");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat("##spectatorDistance", ref distance, 1f, 100f, "%.1f yalms"))
        {
            config.SpectatorDistance = distance;
            config.Save();
        }

        var hasFormation = spectatorController!.TryGetFormation(out _);
        if (!hasFormation)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.2f, 1f),
                "Local player unavailable.");
        }

        var remaining = SpectatorController.MaxSpectators - spectatorController.TotalCount;
        var canSpawnSelected = selectedSpectatorEntry != null && hasFormation && remaining > 0;
        ImGui.BeginDisabled(!canSpawnSelected);
        if (ImGui.Button("Spawn Selected"))
        {
            var result = spectatorController.QueueSingle(
                selectedSpectatorEntry!.Id,
                selectedSpectatorEntry.Name,
                config.SpectatorDistance,
                config.SpectatorEmoteIds);

            if (result.Success)
            {
                var suffix = result.Dropped > 0
                    ? $" ({result.Dropped} skipped because of spectator/client actor capacity)"
                    : string.Empty;
                chatGui.Print($"[CombatSim] Spectators: queued {selectedSpectatorEntry.Name}{suffix}.");
            }
            else
            {
                chatGui.PrintError($"[CombatSim] Spectators: {result.Error}");
            }
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        var canSpawnRandom = hasFormation && remaining > 0;
        ImGui.BeginDisabled(!canSpawnRandom);
        if (ImGui.Button("Spawn Random Crowd"))
        {
            var appearances = npcCatalog!.Search(string.Empty, NpcCatalogType.Human)
                .Select(entry => new SpectatorController.SpectatorAppearance(entry.Id, entry.Name))
                .ToList();
            var result = spectatorController.QueueRandomBatch(
                appearances,
                config.SpectatorExcludedNames,
                config.SpectatorSpawnCount,
                config.SpectatorDistance,
                config.SpectatorEmoteIds);

            if (result.Success)
            {
                var suffix = result.Dropped > 0
                    ? $" ({result.Dropped} skipped because of eligible-name/spectator/client actor capacity)"
                    : string.Empty;
                chatGui.Print($"[CombatSim] Spectators: queued {result.Queued} random, unique character name(s){suffix}.");
            }
            else
            {
                chatGui.PrintError($"[CombatSim] Spectators: {result.Error}");
            }
        }
        ImGui.EndDisabled();

        ImGui.TextDisabled($"Capacity: {spectatorController.TotalCount}/{SpectatorController.MaxSpectators}");
    }

    private void DrawSpectatorExclusions()
    {
        if (selectedSpectatorEntry == null)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Select an Appearance Above");
            ImGui.EndDisabled();
        }
        else
        {
            var selectedName = NormalizeSpectatorName(selectedSpectatorEntry.Name);
            var selectedIsExcluded = config.SpectatorExcludedNames.Contains(
                selectedName,
                StringComparer.OrdinalIgnoreCase);
            var label = selectedIsExcluded
                ? "Include Selected Again"
                : "Exclude Selected from Random";
            if (ImGui.Button($"{label}##spectatorSelectedExclusion"))
            {
                if (selectedIsExcluded)
                    config.SpectatorExcludedNames.RemoveAll(name =>
                        string.Equals(
                            NormalizeSpectatorName(name),
                            selectedName,
                            StringComparison.OrdinalIgnoreCase));
                else
                    config.SpectatorExcludedNames.Add(selectedName);
                config.Save();
            }
        }

        var excludedNames = DistinctSpectatorExcludedNames();
        if (excludedNames.Count == 0)
        {
            ImGui.TextDisabled("No excluded appearances.");
            return;
        }

        var listHeight = ImGui.GetTextLineHeightWithSpacing() * Math.Min(excludedNames.Count + 1, 7);
        if (ImGui.BeginListBox("##spectatorExclusionList", new Vector2(-1, listHeight)))
        {
            foreach (var excludedName in excludedNames)
            {
                ImGui.PushID($"spectatorExclusion{excludedName}");
                if (ImGui.SmallButton("Remove"))
                {
                    config.SpectatorExcludedNames.RemoveAll(name =>
                        string.Equals(
                            NormalizeSpectatorName(name),
                            excludedName,
                            StringComparison.OrdinalIgnoreCase));
                    config.Save();
                    ImGui.PopID();
                    break;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(excludedName);
                ImGui.PopID();
            }
            ImGui.EndListBox();
        }

        if (ImGui.SmallButton("Clear All Exclusions"))
        {
            config.SpectatorExcludedNames.Clear();
            config.Save();
        }
    }

    private void MigrateSpectatorExclusionsToNames()
    {
        if (config.SpectatorExcludedENpcIds.Count == 0)
            return;

        foreach (var excludedId in config.SpectatorExcludedENpcIds)
        {
            var entry = npcCatalog!.FindById(NpcCatalogType.Human, excludedId);
            var name = NormalizeSpectatorName(entry?.Name);
            if (name.Length > 0 &&
                !config.SpectatorExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                config.SpectatorExcludedNames.Add(name);
            }
        }

        config.SpectatorExcludedENpcIds.Clear();
        config.Save();
    }

    private List<string> DistinctSpectatorExcludedNames()
        => config.SpectatorExcludedNames
            .Select(NormalizeSpectatorName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeSpectatorName(string? name)
        => name?.Trim() ?? string.Empty;

    private void DrawSpectatorEmotePool()
    {
        EnsureSpectatorEmoteCache();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint(
            "##spectatorEmoteSearch",
            "Search emote name or ID...",
            ref spectatorEmoteSearch,
            256);

        var results = FilterSpectatorEmotes(spectatorEmoteSearch);
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 7;
        if (ImGui.BeginListBox("##spectatorEmoteResults", new Vector2(-1, listHeight)))
        {
            if (results.Count == 0)
            {
                ImGui.TextDisabled("No emotes found.");
            }
            else
            {
                foreach (var emote in results)
                {
                    var selected = selectedSpectatorEmoteId == emote.Id;
                    if (ImGui.Selectable($"{emote.DisplayName}##spectatorEmote{emote.Id}", selected))
                        selectedSpectatorEmoteId = emote.Id;
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        AddSpectatorEmote(emote.Id);
                }
            }
            ImGui.EndListBox();
        }

        var alreadyAdded = selectedSpectatorEmoteId != 0 &&
                           config.SpectatorEmoteIds.Contains(selectedSpectatorEmoteId);
        ImGui.BeginDisabled(selectedSpectatorEmoteId == 0 || alreadyAdded);
        if (ImGui.Button("Add Emote"))
            AddSpectatorEmote(selectedSpectatorEmoteId);
        ImGui.EndDisabled();
        if (alreadyAdded)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Already in pool");
        }

        var canReroll = spectatorController!.TotalCount > 0;
        ImGui.BeginDisabled(!canReroll);
        if (ImGui.Button("Re-roll Current Crowd"))
        {
            var result = spectatorController.QueueReroll(config.SpectatorEmoteIds);
            if (result.Success)
                chatGui.Print($"[CombatSim] Spectators: re-rolling {result.Queued} current spectator emote assignment(s).");
            else
                chatGui.PrintError($"[CombatSim] Spectators: {result.Error}");
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        var poolCount = config.SpectatorEmoteIds.Count;
        if (ImGui.CollapsingHeader($"Selected Emotes ({poolCount})###spectatorSelectedEmotes"))
        {
            ImGui.Indent();
            if (poolCount == 0)
            {
                ImGui.TextDisabled("Pool is empty: new spectators will remain idle.");
            }
            else
            {
                for (var i = 0; i < config.SpectatorEmoteIds.Count; i++)
                {
                    var id = config.SpectatorEmoteIds[i];
                    ImGui.PushID($"spectatorSelectedEmote{id}_{i}");
                    ImGui.TextUnformatted(FindSpectatorEmoteName(id));
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove"))
                    {
                        config.SpectatorEmoteIds.RemoveAt(i);
                        config.Save();
                        ImGui.PopID();
                        break;
                    }
                    ImGui.PopID();
                }

            }

            var defaultEmoteIds = SpectatorController.CreateDefaultEmoteIds();
            var alreadyDefaults = config.SpectatorEmoteIds.SequenceEqual(defaultEmoteIds);
            ImGui.BeginDisabled(alreadyDefaults);
            if (ImGui.SmallButton("Restore Defaults##spectatorEmoteRestoreDefaults"))
            {
                config.SpectatorEmoteIds = defaultEmoteIds;
                config.Save();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(config.SpectatorEmoteIds.Count == 0);
            if (ImGui.SmallButton("Clear##spectatorEmoteClear"))
            {
                config.SpectatorEmoteIds.Clear();
                config.Save();
            }
            ImGui.EndDisabled();
            ImGui.Unindent();
        }
    }

    private void DrawSpectatorCrowdStatus()
    {
        ImGui.Text(
            $"Live: {spectatorController!.LiveCount}   Pending: {spectatorController.PendingCount}   Replaying: {spectatorController.RerollPendingCount}");

        if (!string.IsNullOrWhiteSpace(spectatorController.LastError))
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.2f, 1f), spectatorController.LastError);

        var hasCrowd = spectatorController.TotalCount > 0;
        ImGui.BeginDisabled(!hasCrowd);
        if (ImGui.Button("Despawn All Spectators"))
        {
            spectatorController.DespawnAll();
            chatGui.Print("[CombatSim] Spectators: crowd despawned.");
        }
        ImGui.EndDisabled();
    }

    private void DrawSpectatorChatter()
    {
        var enabled = config.SpectatorChatterEnabled;
        if (ImGui.Checkbox("Enable random chatter##spectatorChatterEnabled", ref enabled))
        {
            config.SpectatorChatterEnabled = enabled;
            config.Save();
        }

        var ttsBridge = config.SpectatorChatterTtsBridge;
        if (ImGui.Checkbox("Trigger XivVoices TTS##spectatorChatterTts", ref ttsBridge))
        {
            config.SpectatorChatterTtsBridge = ttsBridge;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(spectatorController!.IsXivVoicesLoaded ? "detected" : "not detected");

        var chancePerSecond = Math.Clamp(config.SpectatorChatterChancePerSecond, 0.1f, 100f);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.DragFloat(
                "Talk chance##spectatorChatterChance",
                ref chancePerSecond,
                0.1f,
                0.1f,
                100f,
                "%.1f%% / spectator / sec"))
        {
            config.SpectatorChatterChancePerSecond = Math.Clamp(chancePerSecond, 0.1f, 100f);
            config.Save();
        }
        ImGui.TextDisabled("Each idle spectator rolls independently; the cap only limits overlap.");

        var duration = Math.Clamp(config.SpectatorChatterBubbleDuration, 1.5f, 10f);
        ImGui.SetNextItemWidth(150f);
        if (ImGui.DragFloat(
                "Bubble time##spectatorChatterDuration",
                ref duration,
                0.25f,
                1.5f,
                10f,
                "%.1f s"))
        {
            config.SpectatorChatterBubbleDuration = Math.Clamp(duration, 1.5f, 10f);
            config.Save();
        }

        var maxConcurrent = Math.Clamp(
            config.SpectatorChatterMaxConcurrent,
            1,
            SpectatorController.MaxChatterConcurrent);
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderInt(
                "Max talking##spectatorChatterConcurrent",
                ref maxConcurrent,
                1,
                SpectatorController.MaxChatterConcurrent))
        {
            config.SpectatorChatterMaxConcurrent = maxConcurrent;
            config.Save();
        }

        ImGui.TextDisabled(ttsBridge
            ? "XivVoices: enable Chat Messages + Say; disable Remote TTS for offline audio."
            : "Local speech bubble only; no chat command is sent.");

        var chatterPhase = spectatorController.CurrentChatterPhase;
        var activePoolName = chatterPhase switch
        {
            SpectatorChatterPhase.Battle => "Battle",
            SpectatorChatterPhase.Decisive => "Decisive",
            _ => "Waiting for an enemy roster (Test Once previews Battle)",
        };
        ImGui.TextDisabled($"Active dialogue: {activePoolName}");

        var previewLineCount = chatterPhase == SpectatorChatterPhase.Decisive
            ? config.SpectatorDecisiveChatterLines.Count
            : config.SpectatorBattleChatterLines.Count;
        var canTest = spectatorController.LiveCount > 0 && previewLineCount > 0;
        ImGui.BeginDisabled(!canTest);
        if (ImGui.SmallButton("Test Once##spectatorChatterTest"))
        {
            var result = spectatorController.TrySpeakRandomNow();
            if (!result.Success)
                chatGui.PrintError($"[CombatSim] Spectator chatter: {result.Error}");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"Talking: {spectatorController.ActiveChatterCount}/{maxConcurrent}");

        if (!string.IsNullOrWhiteSpace(spectatorController.LastChatterError))
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.2f, 1f),
                spectatorController.LastChatterError);
        }
        else if (!string.IsNullOrWhiteSpace(spectatorController.LastChatterText))
        {
            ImGui.TextWrapped(
                $"Last: {spectatorController.LastChatterSpeaker}: {spectatorController.LastChatterText}");
        }

        if (!ImGui.CollapsingHeader(
                $"Dialogue Pools (Battle {config.SpectatorBattleChatterLines.Count}, Decisive {config.SpectatorDecisiveChatterLines.Count})###spectatorChatterPools"))
        {
            return;
        }

        ImGui.Indent();
        if (ImGui.SmallButton("Restore All Defaults##spectatorChatterRestoreDefaults"))
        {
            config.SpectatorBattleChatterLines = SpectatorController.CreateDefaultBattleChatterLines();
            config.SpectatorDecisiveChatterLines = SpectatorController.CreateDefaultDecisiveChatterLines();
            config.Save();
        }
        ImGui.TextDisabled("Restores both pools. Custom lines in both pools will be replaced.");

        DrawSpectatorChatterPool(
            "Battle Lines",
            "battle",
            "Used while the player/party side and enemy side each have a survivor.",
            config.SpectatorBattleChatterLines,
            ref spectatorNewBattleChatterLine);
        DrawSpectatorChatterPool(
            "Decisive Lines",
            "decisive",
            "Used after either the player/party side or enemy side is wiped out.",
            config.SpectatorDecisiveChatterLines,
            ref spectatorNewDecisiveChatterLine);
        ImGui.Unindent();
    }

    private void DrawSpectatorChatterPool(
        string title,
        string id,
        string usage,
        List<string> lines,
        ref string newLine)
    {
        if (!ImGui.CollapsingHeader($"{title} ({lines.Count})###{id}SpectatorChatterPool"))
            return;

        ImGui.Indent();
        ImGui.TextDisabled(usage);

        var normalizedNewLine = newLine.Trim();
        var canAddLine = normalizedNewLine.Length > 0 &&
                         lines.Count < SpectatorController.MaxChatterLines &&
                         !lines.Any(line =>
                             string.Equals(line, normalizedNewLine, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 48f));
        var addWithEnter = ImGui.InputTextWithHint(
            $"##{id}SpectatorNewChatterLine",
            "Add a new phrase...",
            ref newLine,
            SpectatorController.MaxChatterLineLength + 1,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.BeginDisabled(!canAddLine);
        var addWithButton = ImGui.SmallButton($"Add##{id}SpectatorChatterAdd");
        ImGui.EndDisabled();
        if ((addWithEnter || addWithButton) && canAddLine)
        {
            lines.Add(normalizedNewLine);
            newLine = string.Empty;
            config.Save();
        }

        var removeIndex = -1;
        var tableFlags = ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable(
                $"##{id}SpectatorChatterLineList",
                2,
                tableFlags,
                new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 8f)))
        {
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableSetupColumn("Phrase", ImGuiTableColumnFlags.WidthStretch);
            for (var i = 0; i < lines.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.SmallButton($"X##{id}SpectatorChatterRemove{i}"))
                    removeIndex = i;
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(lines[i] ?? string.Empty);
            }
            ImGui.EndTable();
        }
        if (removeIndex >= 0)
        {
            lines.RemoveAt(removeIndex);
            config.Save();
        }

        ImGui.BeginDisabled(lines.Count == 0);
        if (ImGui.SmallButton($"Clear##{id}SpectatorChatterClear"))
        {
            lines.Clear();
            config.Save();
        }
        ImGui.EndDisabled();
        ImGui.Unindent();
    }

    private void ResolveSavedSpectatorSelection()
    {
        if (selectedSpectatorEntry != null || config.SpectatorHumanENpcId == 0)
            return;

        var saved = npcCatalog!.FindById(
            NpcCatalogType.Human,
            config.SpectatorHumanENpcId);
        if (saved != null && spectatorController!.IsEmoteCompatibleAppearance(saved.Id))
        {
            selectedSpectatorEntry = saved;
            return;
        }

        config.SpectatorHumanENpcId = 0;
        config.Save();
    }

    private void EnsureSpectatorEmoteCache()
    {
        if (spectatorEmoteCache != null)
            return;

        spectatorEmoteCache = new List<SpectatorEmoteChoice>();
        var sheet = dataManager.GetExcelSheet<Emote>();
        if (sheet == null)
            return;

        foreach (var emote in sheet)
        {
            var name = emote.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var loop = emote.ActionTimeline[0].RowId;
            var intro = emote.ActionTimeline[1].RowId;
            if (loop == 0 && intro == 0)
                continue;

            spectatorEmoteCache.Add(new SpectatorEmoteChoice(
                emote.RowId,
                name,
                $"{name} [{emote.RowId}]"));
        }

        spectatorEmoteCache.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<SpectatorEmoteChoice> FilterSpectatorEmotes(string filter)
    {
        if (spectatorEmoteCache == null)
            return Array.Empty<SpectatorEmoteChoice>();
        if (string.IsNullOrWhiteSpace(filter))
            return spectatorEmoteCache;

        return spectatorEmoteCache
            .Where(emote =>
                emote.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                emote.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void AddSpectatorEmote(uint id)
    {
        if (id == 0 || config.SpectatorEmoteIds.Contains(id))
            return;
        config.SpectatorEmoteIds.Add(id);
        config.Save();
    }

    private string FindSpectatorEmoteName(uint id)
    {
        var emote = spectatorEmoteCache?.FirstOrDefault(entry => entry.Id == id);
        return emote == null ? $"Unknown Emote [{id}]" : emote.DisplayName;
    }

    private sealed record SpectatorEmoteChoice(uint Id, string Name, string DisplayName);
}
