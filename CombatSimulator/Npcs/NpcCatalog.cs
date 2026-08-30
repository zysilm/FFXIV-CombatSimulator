// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace CombatSimulator.Npcs;

/// <summary>
/// Human/DemiHuman/Monster are real ModelChara categories. BNpc and ENpc remain as legacy recipe
/// filters so existing JSON continues to mean "use this source sheet" rather than a model kind.
/// </summary>
public enum NpcCatalogType
{
    BNpc,
    ENpc,
    Human,
    DemiHuman,
    Monster,
}

public enum NpcCatalogSource
{
    BNpcBase,
    ENpcBase,
    ModelChara,
}

public class NpcCatalogEntry
{
    /// <summary>Row ID in <see cref="Source"/>.</summary>
    public uint Id { get; set; }
    public uint BNpcNameId { get; set; }
    public uint ModelCharaId { get; set; }
    public string Name { get; set; } = string.Empty;
    public NpcCatalogType Type { get; set; }
    public NpcCatalogSource Source { get; set; }
    public bool HasDuplicateName { get; set; }
    /// <summary>
    /// Supplemental names that can discover an otherwise unnamed base. They are deliberately not
    /// presented as the actor's identity because BNpcLink is a many-to-many search index, not a
    /// guaranteed SetupBNpc pair.
    /// </summary>
    public IReadOnlyList<string> SearchAliases { get; set; } = Array.Empty<string>();

    /// <summary>
    /// ModelChara-only Monsters can use the existing direct-model spawn path. ModelChara-only
    /// DemiHumans lack the customize/equipment payload required to construct a complete actor.
    /// </summary>
    public bool IsSpawnable => Source != NpcCatalogSource.ModelChara || Type == NpcCatalogType.Monster;
}

/// <summary>
/// Search catalog built from the game's ModelChara, ENpcBase, and BNpcBase sheets. ModelChara.Type
/// is the authoritative category (1 Human, 2 DemiHuman, 3 Monster). The existing curated NpcNames
/// resource provides localized names; otherwise non-Human ModelChara rows remain searchable by
/// their model ID.
/// </summary>
public class NpcCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private List<NpcCatalogEntry>? allEntries;
    private List<NpcCatalogEntry>? popularEntries;
    private readonly Dictionary<(string Filter, NpcCatalogType? Type, NpcCatalogSource? Source), IReadOnlyList<NpcCatalogEntry>> searchCache = new();
    private bool loaded;

    public bool IsLoaded => loaded;

    public NpcCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;

        try
        {
            LoadEntries();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load NPC catalog.");
            allEntries ??= new List<NpcCatalogEntry>();
        }
    }

    private void LoadEntries()
    {
        allEntries = new List<NpcCatalogEntry>();

        var modelRows = new Dictionary<uint, (NpcCatalogType Type, uint ModelId)>();
        foreach (var row in dataManager.GetExcelSheet<ModelChara>())
        {
            if (TryClassifyModel(row.Type, out var type))
                modelRows[row.RowId] = (type, row.RowId);
        }

        var bNpcNames = new Dictionary<uint, string>();
        foreach (var row in dataManager.GetExcelSheet<BNpcName>())
        {
            var name = row.Singular.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                bNpcNames[row.RowId] = name;
        }

        var bNpcRows = new Dictionary<uint, (NpcCatalogType Type, uint ModelId)>();
        foreach (var row in dataManager.GetExcelSheet<BNpcBase>())
        {
            var modelId = row.ModelChara.RowId;
            if (modelRows.TryGetValue(modelId, out var model))
                bNpcRows[row.RowId] = model;
        }

        var eNpcRows = new Dictionary<uint, (ENpcBase Row, NpcCatalogType Type, uint ModelId)>();
        foreach (var row in dataManager.GetExcelSheet<ENpcBase>())
        {
            var modelId = row.ModelChara.RowId;
            if (modelRows.TryGetValue(modelId, out var model))
                eNpcRows[row.RowId] = (row, model.Type, model.ModelId);
        }

        var addedSources = new HashSet<(NpcCatalogSource Source, uint Id)>();

        // NpcNames describes the appearance represented by a base row. Keep it authoritative when
        // present; unlike BNpcLink it does not cross-product one display name over many appearances.
        var curatedBattleNames = new Dictionary<uint, (string Name, uint NameId)>();
        var curatedEventNames = new Dictionary<uint, string>();
        var curatedNames = LoadEmbeddedNpcNames();
        if (curatedNames == null)
        {
            log.Warning("NpcNames.json not found in embedded resources.");
        }
        else
        {
            foreach (var (key, nameValue) in curatedNames)
            {
                if (!TryResolveName(nameValue, bNpcNames, out var displayName, out var bNpcNameId))
                    continue;

                if (key.StartsWith("B:", StringComparison.Ordinal) &&
                    uint.TryParse(key.AsSpan(2), out var bNpcBaseId))
                {
                    curatedBattleNames[bNpcBaseId] = (displayName, bNpcNameId);
                }
                else if (key.StartsWith("E:", StringComparison.Ordinal) &&
                         uint.TryParse(key.AsSpan(2), out var eNpcBaseId))
                {
                    curatedEventNames[eNpcBaseId] = displayName;
                }
            }
        }

        // BNpcLink remains valuable for discovering new models, but it is deliberately many-to-many.
        // ActorMorpher applies appearances directly; passing every pair to SetupBNpc instead creates
        // unrelated actors with the same label. Build one canonical identity per BNpcBase.
        var battleNpcNameLinks = LoadBattleNpcNameLinks();
        var ambiguousBattleNpcBases = 0;
        foreach (var (id, model) in bNpcRows)
        {
            var linkedNameIds = battleNpcNameLinks.TryGetValue(id, out var nameIds)
                ? nameIds
                : Array.Empty<uint>();
            var linkedNames = new List<string>();
            foreach (var linkedNameId in linkedNameIds)
            {
                if (bNpcNames.TryGetValue(linkedNameId, out var linkedName) &&
                    !string.IsNullOrWhiteSpace(linkedName) &&
                    !ContainsExact(linkedNames, linkedName))
                {
                    linkedNames.Add(linkedName);
                }
            }

            string displayName;
            uint bNpcNameId;
            IReadOnlyList<string> searchAliases = Array.Empty<string>();
            if (curatedBattleNames.TryGetValue(id, out var curated))
            {
                displayName = curated.Name;
                bNpcNameId = curated.NameId;

                // Literal curated labels have no native name row. Confirm the self-ID through
                // BNpcLink before using it; numerical equality alone is not evidence of a pair.
                if (bNpcNameId == 0 && Array.IndexOf(linkedNameIds, id) >= 0)
                    bNpcNameId = id;
            }
            else if (Array.IndexOf(linkedNameIds, id) >= 0 &&
                     bNpcNames.TryGetValue(id, out var selfName))
            {
                displayName = selfName;
                bNpcNameId = id;
            }
            else if (linkedNameIds.Length == 1 &&
                     bNpcNames.TryGetValue(linkedNameIds[0], out var uniqueName))
            {
                displayName = uniqueName;
                bNpcNameId = linkedNameIds[0];
            }
            else
            {
                displayName = $"Battle NPC {id}";
                bNpcNameId = 0;
                searchAliases = linkedNames;
                if (linkedNames.Count > 0)
                    ambiguousBattleNpcBases++;
            }

            AddSourceEntry(
                id,
                bNpcNameId,
                model.ModelId,
                displayName,
                model.Type,
                NpcCatalogSource.BNpcBase,
                addedSources,
                searchAliases);
        }

        // ENpcBase and ENpcResident share RowIds, so every named Event NPC can be added directly.
        // Unlike the previous Race/ModelCharaId heuristic this includes all three model categories.
        var residents = dataManager.GetExcelSheet<ENpcResident>();
        foreach (var (id, value) in eNpcRows)
        {
            var name = curatedEventNames.GetValueOrDefault(id, string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                var resident = residents.GetRowOrDefault(id);
                if (resident != null)
                    name = resident.Value.Singular.ExtractText();
            }
            if (string.IsNullOrWhiteSpace(name))
                continue;

            AddSourceEntry(
                id,
                0,
                value.ModelId,
                name,
                value.Type,
                NpcCatalogSource.ENpcBase,
                addedSources);
        }

        // Keep otherwise-unreferenced non-Human models discoverable. A Monster can be spawned by
        // direct ModelChara ID; a DemiHuman is listed but disabled unless a complete base row exists.
        var referencedModels = new HashSet<uint>();
        foreach (var entry in allEntries)
            referencedModels.Add(entry.ModelCharaId);
        foreach (var (modelId, model) in modelRows)
        {
            if (modelId == 0 || model.Type == NpcCatalogType.Human || referencedModels.Contains(modelId))
                continue;

            allEntries.Add(new NpcCatalogEntry
            {
                Id = modelId,
                ModelCharaId = modelId,
                Name = $"ModelChara {modelId}",
                Type = model.Type,
                Source = NpcCatalogSource.ModelChara,
            });
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in allEntries)
            nameCounts[entry.Name] = nameCounts.GetValueOrDefault(entry.Name) + 1;
        foreach (var entry in allEntries)
            entry.HasDuplicateName = nameCounts[entry.Name] > 1;

        allEntries.Sort(static (a, b) =>
        {
            var category = a.Type.CompareTo(b.Type);
            if (category != 0) return category;
            var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            return a.Id.CompareTo(b.Id);
        });

        var humans = CountCategory(NpcCatalogType.Human);
        var demiHumans = CountCategory(NpcCatalogType.DemiHuman);
        var monsters = CountCategory(NpcCatalogType.Monster);
        var modelOnly = 0;
        foreach (var entry in allEntries)
            if (entry.Source == NpcCatalogSource.ModelChara) modelOnly++;
        log.Info(
            $"NPC catalog loaded from ModelChara classification: {humans} Human, " +
            $"{demiHumans} DemiHuman, {monsters} Monster ({modelOnly} ModelChara-only), " +
            $"{allEntries.Count} total; {ambiguousBattleNpcBases} ambiguous BNpc bases kept numeric.");
    }

    private int CountCategory(NpcCatalogType type)
    {
        var count = 0;
        foreach (var entry in allEntries!)
            if (entry.Type == type) count++;
        return count;
    }

    private void AddSourceEntry(
        uint id,
        uint bNpcNameId,
        uint modelCharaId,
        string name,
        NpcCatalogType type,
        NpcCatalogSource source,
        HashSet<(NpcCatalogSource Source, uint Id)> addedSources,
        IReadOnlyList<string>? searchAliases = null)
    {
        if (!addedSources.Add((source, id)))
            return;
        allEntries!.Add(new NpcCatalogEntry
        {
            Id = id,
            BNpcNameId = bNpcNameId,
            ModelCharaId = modelCharaId,
            Name = name,
            Type = type,
            Source = source,
            SearchAliases = searchAliases ?? Array.Empty<string>(),
        });
    }

    private IReadOnlyDictionary<uint, uint[]> LoadBattleNpcNameLinks()
    {
        try
        {
            var links = CsvLoader.LoadResource<BNpcLink>(
                CsvLoader.BNpcLinkResourceName,
                true,
                out var failedLines,
                out var exceptions);

            if (failedLines.Count > 0 || exceptions.Count > 0)
                log.Warning(
                    $"BNpcLink supplemental data loaded with {failedLines.Count} failed line(s) " +
                    $"and {exceptions.Count} exception(s).");

            var result = new Dictionary<uint, HashSet<uint>>();
            foreach (var link in links)
            {
                if (!result.TryGetValue(link.BNpcBaseId, out var names))
                {
                    names = new HashSet<uint>();
                    result[link.BNpcBaseId] = names;
                }
                names.Add(link.BNpcNameId);
            }

            var flattened = new Dictionary<uint, uint[]>(result.Count);
            foreach (var (baseId, names) in result)
            {
                var values = new uint[names.Count];
                names.CopyTo(values);
                flattened[baseId] = values;
            }

            log.Info($"Loaded supplemental BNpcLink mappings for {flattened.Count} BNpcBase rows.");
            return flattened;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load supplemental BNpcLink data; using numeric BNpc fallbacks.");
            return new Dictionary<uint, uint[]>();
        }
    }

    private static bool TryClassifyModel(byte modelType, out NpcCatalogType type)
    {
        type = modelType switch
        {
            1 => NpcCatalogType.Human,
            2 => NpcCatalogType.DemiHuman,
            3 => NpcCatalogType.Monster,
            _ => default,
        };
        return modelType is 1 or 2 or 3;
    }

    private static bool TryResolveName(
        string nameValue,
        IReadOnlyDictionary<uint, string> bNpcNames,
        out string displayName,
        out uint bNpcNameId)
    {
        bNpcNameId = 0;
        if (nameValue.StartsWith("N:", StringComparison.Ordinal) &&
            uint.TryParse(nameValue.AsSpan(2), out var nameRefId))
        {
            bNpcNameId = nameRefId;
            if (!bNpcNames.TryGetValue(nameRefId, out displayName!))
            {
                displayName = string.Empty;
                return false;
            }
        }
        else
        {
            displayName = nameValue;
        }

        return !string.IsNullOrWhiteSpace(displayName);
    }

    private Dictionary<string, string>? LoadEmbeddedNpcNames()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "CombatSimulator.Npcs.NpcNames.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
    }

    public IReadOnlyList<NpcCatalogEntry> Search(
        string filter,
        NpcCatalogType? typeFilter = null,
        NpcCatalogSource? sourceFilter = null)
    {
        EnsureLoaded();
        if (allEntries == null) return Array.Empty<NpcCatalogEntry>();

        var noFilter = string.IsNullOrWhiteSpace(filter);
        if (noFilter && typeFilter == null && sourceFilter == null)
            return allEntries;

        var normalizedFilter = noFilter ? string.Empty : filter.Trim();
        var cacheKey = (normalizedFilter, typeFilter, sourceFilter);
        if (searchCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var results = new List<NpcCatalogEntry>();
        foreach (var entry in allEntries)
        {
            if (typeFilter.HasValue && !MatchesType(entry, typeFilter.Value)) continue;
            if (sourceFilter.HasValue && entry.Source != sourceFilter.Value) continue;
            if (!noFilter &&
                !entry.Name.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase) &&
                !ContainsAlias(entry.SearchAliases, normalizedFilter) &&
                !entry.Id.ToString().Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase) &&
                !entry.ModelCharaId.ToString().Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(entry);
        }

        searchCache[cacheKey] = results;
        return results;
    }

    private static bool ContainsAlias(IReadOnlyList<string> aliases, string filter)
    {
        foreach (var alias in aliases)
            if (alias.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool ContainsExact(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public NpcCatalogEntry? FindById(
        NpcCatalogType type,
        uint id,
        NpcCatalogSource? sourceFilter = null)
    {
        EnsureLoaded();
        if (allEntries == null) return null;
        foreach (var entry in allEntries)
        {
            if (entry.Id == id && MatchesType(entry, type) &&
                (!sourceFilter.HasValue || entry.Source == sourceFilter.Value))
                return entry;
        }
        return null;
    }

    public NpcCatalogEntry? FindBySourceAndId(NpcCatalogSource source, uint id, uint bNpcNameId = 0)
    {
        EnsureLoaded();
        if (allEntries == null) return null;
        if (source == NpcCatalogSource.BNpcBase && bNpcNameId != 0)
        {
            foreach (var entry in allEntries)
                if (entry.Source == source && entry.Id == id && entry.BNpcNameId == bNpcNameId)
                    return entry;
        }
        foreach (var entry in allEntries)
            if (entry.Source == source && entry.Id == id) return entry;
        return null;
    }

    public NpcCatalogEntry? FindByNameOccurrence(string name, NpcCatalogType? typeFilter, int occurrence)
    {
        EnsureLoaded();
        if (allEntries == null) return null;
        var desired = Math.Max(1, occurrence);
        var seen = 0;
        foreach (var entry in allEntries)
        {
            if (typeFilter.HasValue && !MatchesType(entry, typeFilter.Value)) continue;
            if (!entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen == desired) return entry;
        }
        return null;
    }

    private static bool MatchesType(NpcCatalogEntry entry, NpcCatalogType filter)
        => filter switch
        {
            NpcCatalogType.BNpc => entry.Source == NpcCatalogSource.BNpcBase,
            NpcCatalogType.ENpc => entry.Source == NpcCatalogSource.ENpcBase,
            _ => entry.Type == filter,
        };

    public IReadOnlyList<NpcCatalogEntry> GetPopularEntries()
    {
        if (popularEntries != null) return popularEntries;
        EnsureLoaded();
        popularEntries = new List<NpcCatalogEntry>();

        var popular = new (uint Id, NpcCatalogSource Source)[]
        {
            // Resolve these through the canonical catalog. Do not fabricate BaseId/NameId pairs.
            (5459, NpcCatalogSource.BNpcBase), // Old World Striking Dummy
            (3347, NpcCatalogSource.BNpcBase), // Sabotender Guardia
            (15, NpcCatalogSource.BNpcBase),   // Hog
            (21, NpcCatalogSource.BNpcBase),   // Imp
            (23, NpcCatalogSource.BNpcBase),   // Flytrap
            (30, NpcCatalogSource.BNpcBase),   // Mudestone Golem
            (34, NpcCatalogSource.BNpcBase),   // Tortoise
            (38, NpcCatalogSource.BNpcBase),   // Bat
            (45, NpcCatalogSource.BNpcBase),   // Wisp
            (48, NpcCatalogSource.BNpcBase),   // Myconid
            (1028802, NpcCatalogSource.ENpcBase),
            (1018510, NpcCatalogSource.ENpcBase),
        };

        foreach (var item in popular)
        {
            var entry = FindBySourceAndId(item.Source, item.Id);
            if (entry != null)
                popularEntries.Add(entry);
        }
        return popularEntries;
    }

    public IReadOnlyList<NpcCatalogEntry> GetRecentEntries(IReadOnlyList<RecentNpcEntry> recentEntries)
    {
        EnsureLoaded();
        var results = new List<NpcCatalogEntry>();
        foreach (var recent in recentEntries)
        {
            var found = FindBySourceAndId(recent.Source, recent.BNpcBaseId, recent.BNpcNameId);
            // Older configurations only stored an ID. Their default values look like BNpcBase,
            // so fall back to ENpcBase when that ID is not actually present in the BNpc catalog.
            if (found == null &&
                recent.Source == NpcCatalogSource.BNpcBase &&
                recent.Type == NpcCatalogType.BNpc &&
                recent.ModelCharaId == 0)
            {
                found = FindBySourceAndId(NpcCatalogSource.ENpcBase, recent.BNpcBaseId);
            }
            results.Add(found ?? new NpcCatalogEntry
            {
                Id = recent.BNpcBaseId,
                BNpcNameId = recent.BNpcNameId,
                ModelCharaId = recent.ModelCharaId,
                Name = recent.Source == NpcCatalogSource.ModelChara
                    ? $"ModelChara {recent.ModelCharaId}"
                    : $"NPC #{recent.BNpcBaseId}",
                Type = recent.Type is NpcCatalogType.Human or NpcCatalogType.DemiHuman or NpcCatalogType.Monster
                    ? recent.Type
                    : recent.Source == NpcCatalogSource.BNpcBase
                        ? NpcCatalogType.Monster
                        : NpcCatalogType.Human,
                Source = recent.Source,
            });
        }
        return results;
    }
}
