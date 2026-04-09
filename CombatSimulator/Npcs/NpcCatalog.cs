using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

public class NpcCatalogEntry
{
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// NPC catalog built from Anamnesis NpcNames.json (embedded resource) which provides
/// verified BNpcBase → name mappings. Names reference either direct strings or
/// BNpcName sheet rows via "N:XXXXXX" notation.
/// </summary>
public class NpcCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private List<NpcCatalogEntry>? allEntries;
    private List<NpcCatalogEntry>? popularEntries;
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

        // Load BNpcName sheet for resolving "N:XXXXXX" references
        var nameSheet = dataManager.GetExcelSheet<BNpcName>();
        var bNpcNameLookup = new Dictionary<uint, string>();
        if (nameSheet != null)
        {
            foreach (var row in nameSheet)
            {
                var name = row.Singular.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    bNpcNameLookup[row.RowId] = name;
            }
        }

        // Load BNpcBase sheet to verify entries have valid monster models
        var baseSheet = dataManager.GetExcelSheet<BNpcBase>();
        var validBaseIds = new HashSet<uint>();
        if (baseSheet != null)
        {
            foreach (var row in baseSheet)
            {
                if ((int)row.ModelChara.RowId > 0)
                    validBaseIds.Add(row.RowId);
            }
        }

        // Load NpcNames.json embedded resource (Anamnesis-sourced BNpcBase→name mapping)
        var npcNamesJson = LoadEmbeddedNpcNames();
        if (npcNamesJson == null)
        {
            log.Warning("NpcNames.json not found in embedded resources.");
            return;
        }

        foreach (var kvp in npcNamesJson)
        {
            // Key format: "B:0000002" → BNpcBase RowId
            if (!kvp.Key.StartsWith("B:")) continue;
            if (!uint.TryParse(kvp.Key.AsSpan(2), out var bNpcBaseId)) continue;

            // Only include entries with a valid monster model (filters out humanoid NPCs)
            if (!validBaseIds.Contains(bNpcBaseId)) continue;

            // Resolve name: either direct string or "N:XXXXXX" BNpcName reference
            var nameValue = kvp.Value;
            string displayName;
            uint bNpcNameId = 0;

            if (nameValue.StartsWith("N:") && uint.TryParse(nameValue.AsSpan(2), out var nameRefId))
            {
                bNpcNameId = nameRefId;
                if (!bNpcNameLookup.TryGetValue(nameRefId, out var resolvedName))
                    continue; // Can't resolve name, skip
                displayName = resolvedName;
            }
            else
            {
                // Direct name string — use BNpcBaseId as the BNpcNameId
                // (SetupBNpc will use whatever name is set, the nameId is secondary)
                displayName = nameValue;
                bNpcNameId = bNpcBaseId;
            }

            if (string.IsNullOrWhiteSpace(displayName)) continue;

            allEntries.Add(new NpcCatalogEntry
            {
                BNpcBaseId = bNpcBaseId,
                BNpcNameId = bNpcNameId,
                Name = displayName,
            });
        }

        allEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        log.Info($"NPC catalog loaded: {allEntries.Count} entries from NpcNames.json.");
    }

    private Dictionary<string, string>? LoadEmbeddedNpcNames()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "CombatSimulator.Npcs.NpcNames.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    /// <summary>
    /// Search NPCs by name (case-insensitive substring match).
    /// Returns at most maxResults entries.
    /// </summary>
    public IReadOnlyList<NpcCatalogEntry> Search(string filter, int maxResults = 100)
    {
        EnsureLoaded();
        if (allEntries == null) return Array.Empty<NpcCatalogEntry>();

        if (string.IsNullOrWhiteSpace(filter))
            return allEntries.Count <= maxResults ? allEntries : allEntries.GetRange(0, maxResults);

        var results = new List<NpcCatalogEntry>();
        foreach (var entry in allEntries)
        {
            if (entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(entry);
                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }

    /// <summary>
    /// Get curated list of well-known combat enemies.
    /// </summary>
    public IReadOnlyList<NpcCatalogEntry> GetPopularEntries()
    {
        if (popularEntries != null)
            return popularEntries;

        EnsureLoaded();
        popularEntries = new List<NpcCatalogEntry>();

        // Curated popular enemies with verified BNpcBaseId/BNpcNameId pairs
        var popularIds = new (uint BaseId, uint NameId, string FallbackName)[]
        {
            // Training
            (541, 541, "Striking Dummy"),

            // Common overworld mobs (verified matching IDs)
            (3, 3, "Cactuar"),
            (15, 15, "Hog"),
            (21, 21, "Imp"),
            (23, 23, "Flytrap"),
            (30, 30, "Mudestone Golem"),
            (34, 34, "Tortoise"),
            (38, 38, "Bat"),
            (45, 45, "Wisp"),
            (48, 48, "Myconid"),
        };

        foreach (var (baseId, nameId, fallback) in popularIds)
        {
            NpcCatalogEntry? found = null;
            if (allEntries != null)
            {
                foreach (var e in allEntries)
                {
                    if (e.BNpcBaseId == baseId)
                    {
                        found = e;
                        break;
                    }
                }
            }

            popularEntries.Add(found ?? new NpcCatalogEntry
            {
                BNpcBaseId = baseId,
                BNpcNameId = nameId,
                Name = fallback,
            });
        }

        return popularEntries;
    }

    /// <summary>
    /// Get entries matching the recent NPC list from config.
    /// </summary>
    public IReadOnlyList<NpcCatalogEntry> GetRecentEntries(IReadOnlyList<RecentNpcEntry> recentEntries)
    {
        EnsureLoaded();
        var results = new List<NpcCatalogEntry>();

        foreach (var recent in recentEntries)
        {
            NpcCatalogEntry? found = null;
            if (allEntries != null)
            {
                foreach (var e in allEntries)
                {
                    if (e.BNpcBaseId == recent.BNpcBaseId)
                    {
                        found = e;
                        break;
                    }
                }
            }

            results.Add(found ?? new NpcCatalogEntry
            {
                BNpcBaseId = recent.BNpcBaseId,
                BNpcNameId = recent.BNpcNameId,
                Name = $"NPC #{recent.BNpcBaseId}",
            });
        }

        return results;
    }
}
