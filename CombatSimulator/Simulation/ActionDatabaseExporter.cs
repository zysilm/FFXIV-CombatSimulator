using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Simulation;

/// <summary>
/// Generates Actions.json from the game's Lumina data. Geometry (name, range,
/// AoE shape/radius/width, damage type) comes straight from the Action sheet;
/// potency is left at 0 because it is not present in client data and has to be
/// sourced separately. The result is written to the writable on-disk copy that
/// <see cref="ActionDatabaseProvider"/> prefers, giving a complete, hand-editable
/// starting point.
/// </summary>
public class ActionDatabaseExporter
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public ActionDatabaseExporter(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>
    /// Serialization shape written to disk. Geometry is emitted under "_"-prefixed
    /// REFERENCE keys that <see cref="ActionDatabaseProvider"/> deliberately ignores
    /// on load — so an export never freezes geometry (it stays live from Lumina),
    /// it just shows the human what each action looks like next to the editable
    /// "potency"/"comboPotency". To pin a geometry value, rename the field without
    /// the underscore (e.g. "_radius" → "radius"), which the reader then honours.
    /// </summary>
    private sealed class ExportEntry
    {
        [JsonPropertyName("_name")] public string? RefName { get; init; }
        [JsonPropertyName("_shape")] public AoeShape RefShape { get; init; }
        [JsonPropertyName("_range")] public float RefRange { get; init; }
        [JsonPropertyName("_radius")] public float RefRadius { get; init; }
        [JsonPropertyName("_width")] public float RefWidth { get; init; }
        [JsonPropertyName("_damageType")] public SimDamageType RefDamageType { get; init; }
        [JsonPropertyName("_comboFrom")] public uint RefComboFrom { get; init; }
        [JsonPropertyName("potency")] public int Potency { get; set; }
        [JsonPropertyName("comboPotency")] public int ComboPotency { get; set; }
    }

    public sealed class ExportResult
    {
        public bool Success { get; init; }
        public int Count { get; init; }
        public string? Path { get; init; }
        public string? Error { get; init; }
        public string? AttackTypeSummary { get; init; }
    }

    /// <summary>
    /// Export every player damage action. "Damage action" = IsPlayerAction with a
    /// real AttackType (slashing/piercing/blunt/shot/magic); pure utility actions
    /// (AttackType none) are skipped. Existing authored entries are preserved:
    /// only fields the exporter owns (geometry) are refreshed, so re-running after
    /// a patch never wipes hand-filled potencies.
    /// </summary>
    public ExportResult Export()
    {
        var path = ActionDatabaseProvider.DiskPath;
        if (string.IsNullOrEmpty(path))
            return new ExportResult { Success = false, Error = "Config directory unavailable." };

        var sheet = dataManager.GetExcelSheet<Action>();
        if (sheet == null)
            return new ExportResult { Success = false, Error = "Action sheet unavailable." };

        // Preserve any potencies/overrides already on disk.
        var existing = LoadExisting(path);

        // Tally distinct AttackType names we encounter so we can sanity-check the
        // physical/magical classification after an export.
        var attackTypeTally = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = new SortedDictionary<uint, ExportEntry>();
        foreach (var action in sheet)
        {
            if (!action.IsPlayerAction)
                continue;
            if (action.AttackType.RowId == 0)
                continue; // no damage attack type → utility/buff, skip

            var name = action.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var attackTypeName = action.AttackType.ValueNullable?.Name.ExtractText() ?? string.Empty;
            var tallyKey = string.IsNullOrWhiteSpace(attackTypeName)
                ? $"(blank #{action.AttackType.RowId})"
                : $"{attackTypeName} #{action.AttackType.RowId}";
            attackTypeTally[tallyKey] = attackTypeTally.GetValueOrDefault(tallyKey) + 1;

            var entry = new ExportEntry
            {
                RefName = name,
                RefShape = ActionDataProvider.MapCastType(action.CastType),
                RefRange = action.Range,
                RefRadius = action.EffectRange,
                RefWidth = action.XAxisModifier,
                RefDamageType = ActionDataProvider.ClassifyDamageType(action.AttackType.RowId),
                RefComboFrom = action.ActionCombo.RowId,
            };

            // Carry over authored damage values from the previous file.
            if (existing.TryGetValue(action.RowId, out var prev))
            {
                entry.Potency = prev.Potency;
                entry.ComboPotency = prev.ComboPotency;
            }

            result[action.RowId] = entry;
        }

        try
        {
            var asStringKeys = result.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
            var json = JsonSerializer.Serialize(asStringKeys, ActionDatabaseProvider.SerializerOptions);
            File.WriteAllText(path, json);
            var summary = string.Join(", ", attackTypeTally.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            log.Info($"Exported {result.Count} player damage actions to {path}. AttackTypes: {summary}");
            return new ExportResult { Success = true, Count = result.Count, Path = path, AttackTypeSummary = summary };
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to write action database export.");
            return new ExportResult { Success = false, Error = ex.Message };
        }
    }

    private Dictionary<uint, ActionDbEntry> LoadExisting(string path)
    {
        var map = new Dictionary<uint, ActionDbEntry>();
        try
        {
            if (!File.Exists(path))
                return map;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, ActionDbEntry>>(
                File.ReadAllText(path), ActionDatabaseProvider.SerializerOptions);
            if (loaded == null)
                return map;

            foreach (var (key, value) in loaded)
            {
                if (value != null && uint.TryParse(key, out var id))
                    map[id] = value;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read existing action database before export; starting fresh.");
        }
        return map;
    }
}
