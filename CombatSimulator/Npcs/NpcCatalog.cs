using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

public enum NpcCatalogType
{
    BNpc,  // Monster/creature — spawned via SetupBNpc
    ENpc,  // Humanoid NPC — spawned via customize data from ENpcBase
}

public class NpcCatalogEntry
{
    public uint Id { get; set; }           // BNpcBaseId for BNpc, ENpcBaseId for ENpc
    public uint BNpcNameId { get; set; }   // For BNpc name display
    public string Name { get; set; } = string.Empty;
    public NpcCatalogType Type { get; set; }
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

        // Collect valid BNpcBase RowIds with monster models (ModelChara > 0).
        // BNpcBase and BNpcName are independent sheets — RowIds do NOT correspond.
        // The pairing only exists in per-spawn game data, so we rely on NpcNames.json
        // for accurate base→name mapping. Humanoid enemies (ENpcBase) need a separate
        // spawn pipeline and are not yet supported.
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

        // NpcNames.json: curated BNpcBase→name mapping (Anamnesis-sourced, verified)
        var npcNamesJson = LoadEmbeddedNpcNames();
        if (npcNamesJson == null)
        {
            log.Warning("NpcNames.json not found in embedded resources.");
            return;
        }

        int bCount = 0, eCount = 0;

        foreach (var kvp in npcNamesJson)
        {
            var key = kvp.Key;
            var nameValue = kvp.Value;

            // Resolve display name (direct string or "N:XXXXXX" BNpcName reference)
            string displayName;
            uint bNpcNameId = 0;

            if (nameValue.StartsWith("N:") && uint.TryParse(nameValue.AsSpan(2), out var nameRefId))
            {
                bNpcNameId = nameRefId;
                if (!bNpcNameLookup.TryGetValue(nameRefId, out var resolvedName))
                    continue;
                displayName = resolvedName;
            }
            else
            {
                displayName = nameValue;
            }

            if (string.IsNullOrWhiteSpace(displayName)) continue;

            if (key.StartsWith("B:") && uint.TryParse(key.AsSpan(2), out var bNpcBaseId))
            {
                // BNpcBase entry (monster/creature)
                if (!validBaseIds.Contains(bNpcBaseId)) continue;
                if (bNpcNameId == 0) bNpcNameId = bNpcBaseId;

                allEntries.Add(new NpcCatalogEntry
                {
                    Id = bNpcBaseId,
                    BNpcNameId = bNpcNameId,
                    Name = displayName,
                    Type = NpcCatalogType.BNpc,
                });
                bCount++;
            }
            else if (key.StartsWith("E:") && uint.TryParse(key.AsSpan(2), out var eNpcBaseId))
            {
                // ENpcBase entry (humanoid NPC)
                allEntries.Add(new NpcCatalogEntry
                {
                    Id = eNpcBaseId,
                    BNpcNameId = 0,
                    Name = displayName,
                    Type = NpcCatalogType.ENpc,
                });
                eCount++;
            }
        }

        allEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        log.Info($"NPC catalog loaded: {bCount} monsters + {eCount} humanoids = {allEntries.Count} entries.");
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
    public IReadOnlyList<NpcCatalogEntry> Search(string filter)
    {
        EnsureLoaded();
        if (allEntries == null) return Array.Empty<NpcCatalogEntry>();

        if (string.IsNullOrWhiteSpace(filter))
            return allEntries;

        var results = new List<NpcCatalogEntry>();
        foreach (var entry in allEntries)
        {
            if (entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                results.Add(entry);
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

        var popularIds = new (uint Id, uint NameId, NpcCatalogType Type, string FallbackName)[]
        {
            // Monsters
            (541, 541, NpcCatalogType.BNpc, "Striking Dummy"),
            (3, 3, NpcCatalogType.BNpc, "Cactuar"),
            (15, 15, NpcCatalogType.BNpc, "Hog"),
            (21, 21, NpcCatalogType.BNpc, "Imp"),
            (23, 23, NpcCatalogType.BNpc, "Flytrap"),
            (30, 30, NpcCatalogType.BNpc, "Mudestone Golem"),
            (34, 34, NpcCatalogType.BNpc, "Tortoise"),
            (38, 38, NpcCatalogType.BNpc, "Bat"),
            (45, 45, NpcCatalogType.BNpc, "Wisp"),
            (48, 48, NpcCatalogType.BNpc, "Myconid"),

            // Humanoid enemies (ENpcBase)
            (1028802, 0, NpcCatalogType.ENpc, "Zenos"),
            (1018510, 0, NpcCatalogType.ENpc, "Zenos yae Galvus (No Helm)"),
        };

        foreach (var (id, nameId, type, fallback) in popularIds)
        {
            NpcCatalogEntry? found = null;
            if (allEntries != null)
            {
                foreach (var e in allEntries)
                {
                    if (e.Id == id && e.Type == type)
                    {
                        found = e;
                        break;
                    }
                }
            }

            popularEntries.Add(found ?? new NpcCatalogEntry
            {
                Id = id,
                BNpcNameId = nameId,
                Name = fallback,
                Type = type,
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
                    if (e.Id == recent.BNpcBaseId)
                    {
                        found = e;
                        break;
                    }
                }
            }

            results.Add(found ?? new NpcCatalogEntry
            {
                Id = recent.BNpcBaseId,
                BNpcNameId = recent.BNpcNameId,
                Name = $"NPC #{recent.BNpcBaseId}",
            });
        }

        return results;
    }
}
