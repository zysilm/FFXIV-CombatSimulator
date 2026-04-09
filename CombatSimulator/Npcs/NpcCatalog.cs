using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

public class NpcCatalogEntry
{
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ModelCharaId { get; set; }
    public float Scale { get; set; } = 1.0f;
}

/// <summary>
/// Lazy-loaded catalog of FFXIV battle NPCs from BNpcBase + BNpcName Excel sheets.
/// Provides search, curated popular list, and recent entries tracking.
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

        var nameSheet = dataManager.GetExcelSheet<BNpcName>();
        var baseSheet = dataManager.GetExcelSheet<BNpcBase>();
        if (nameSheet == null || baseSheet == null)
        {
            log.Warning("BNpcName or BNpcBase sheet not available.");
            return;
        }

        // Build a lookup of BNpcName ID -> display name
        var nameMap = new Dictionary<uint, string>();
        foreach (var row in nameSheet)
        {
            var name = row.Singular.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                nameMap[row.RowId] = name;
        }

        // Iterate BNpcBase for entries with valid models
        foreach (var row in baseSheet)
        {
            var modelCharaId = (int)row.ModelChara.RowId;
            if (modelCharaId <= 0) continue;

            // Try to find a name: many BNpcBase rows share RowId with BNpcName
            // Also check the BNpcName reference if the sheet exposes one
            uint nameId = row.RowId;
            if (!nameMap.TryGetValue(nameId, out var displayName))
                continue; // Skip nameless entries for the catalog

            var scale = row.Scale > 0 ? row.Scale / 100f : 1.0f;

            allEntries.Add(new NpcCatalogEntry
            {
                BNpcBaseId = row.RowId,
                BNpcNameId = nameId,
                Name = displayName,
                ModelCharaId = modelCharaId,
                Scale = scale,
            });
        }

        // Sort alphabetically
        allEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        log.Info($"NPC catalog loaded: {allEntries.Count} entries.");
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

        // Curated popular enemies: (BNpcBaseId, BNpcNameId, fallback name)
        // These are well-known enemies players commonly fight
        var popularIds = new (uint BaseId, uint NameId, string FallbackName)[]
        {
            // Training
            (541, 541, "Striking Dummy"),

            // A Realm Reborn Primals
            (1185, 1185, "Ifrit"),
            (1186, 1186, "Titan"),
            (1187, 1187, "Garuda"),
            (2612, 2612, "Leviathan"),
            (3193, 3193, "Ramuh"),
            (3374, 3374, "Shiva"),

            // Heavensward
            (4127, 4127, "Ravana"),
            (4893, 4893, "Bismarck"),
            (4901, 4901, "Thordan VII"),
            (4954, 4954, "Sephirot"),
            (5559, 5559, "Nidhogg"),
            (5569, 5569, "Sophia"),

            // Common overworld mobs
            (3, 3, "Cactuar"),
            (12, 12, "Goblin"),
            (118, 118, "Bomb"),
            (188, 188, "Tonberry"),
            (358, 358, "Mandragora"),

            // Stormblood
            (6166, 6166, "Susano"),
            (6177, 6177, "Lakshmi"),
            (7091, 7091, "Shinryu"),
            (7639, 7639, "Tsukuyomi"),

            // Shadowbringers
            (8486, 8486, "Titania"),
            (8885, 8885, "Innocence"),
            (9356, 9356, "Hades"),

            // Endwalker
            (10238, 10238, "Zodiark"),
            (10290, 10290, "Hydaelyn"),
        };

        foreach (var (baseId, nameId, fallback) in popularIds)
        {
            // Try to find in loaded catalog
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

            if (found != null)
            {
                popularEntries.Add(found);
            }
            else
            {
                // Entry not in catalog (maybe filtered out), add with fallback name
                popularEntries.Add(new NpcCatalogEntry
                {
                    BNpcBaseId = baseId,
                    BNpcNameId = nameId,
                    Name = fallback,
                });
            }
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
            // Try to find in loaded catalog
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

            if (found != null)
            {
                results.Add(found);
            }
            else
            {
                // Not in catalog, create minimal entry
                results.Add(new NpcCatalogEntry
                {
                    BNpcBaseId = recent.BNpcBaseId,
                    BNpcNameId = recent.BNpcNameId,
                    Name = $"NPC #{recent.BNpcBaseId}",
                });
            }
        }

        return results;
    }
}
