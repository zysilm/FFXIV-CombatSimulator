using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Simulation;

/// <summary>
/// One authored/curated entry in Actions.json. Every field is optional: a null
/// (or empty) value means "inherit whatever the game's Lumina data gives us".
/// This lets the file act as a thin override layer — a hand edit can pin just a
/// potency, or just widen an AoE, without having to restate the rest.
/// Potency uses 0 (not null) as "unknown" since that is the natural default and
/// 0-potency actions don't deal damage anyway.
/// </summary>
public class ActionDbEntry
{
    public string? Name { get; set; }
    public float? Range { get; set; }
    public AoeShape? Shape { get; set; }
    public float? Radius { get; set; }
    public float? Width { get; set; }
    public SimDamageType? DamageType { get; set; }
    public int Potency { get; set; }
    public int ComboPotency { get; set; }
    public uint? ComboFrom { get; set; }
}

/// <summary>
/// Loads the action database (Actions.json) that supplements the game's Lumina
/// data with curated combat values — AoE shape/size, range, damage type and
/// (eventually) potency. Resolution order: a writable copy in the plugin config
/// directory wins (this is what the in-game exporter writes and what users hand
/// edit), falling back to the embedded default shipped in the DLL.
/// </summary>
public class ActionDatabaseProvider
{
    public const string FileName = "Actions.json";
    private const string EmbeddedResourceName = "CombatSimulator.Resources.Actions.json";

    private readonly IPluginLog log;
    private readonly Dictionary<uint, ActionDbEntry> entries = new();

    public ActionDatabaseProvider(IPluginLog log)
    {
        this.log = log;
        Reload();
    }

    public bool TryGet(uint actionId, out ActionDbEntry entry)
        => entries.TryGetValue(actionId, out entry!);

    public int Count => entries.Count;

    /// <summary>Full path to the writable on-disk copy, or null if unavailable.</summary>
    public static string? DiskPath
    {
        get
        {
            try
            {
                var dir = Core.Services.PluginInterface?.GetPluginConfigDirectory();
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
            }
            catch
            {
                return null;
            }
        }
    }

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Rebuild the database: the embedded resource is the base (shipped potency
    /// library / defaults); the on-disk copy is overlaid on top so user edits and
    /// exports combine with — rather than wholesale replace — the shipped data.
    /// </summary>
    public void Reload()
    {
        entries.Clear();

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                MergeJson(reader.ReadToEnd(), "embedded");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to load embedded action database.");
        }

        var disk = DiskPath;
        if (!string.IsNullOrEmpty(disk) && File.Exists(disk))
        {
            try
            {
                MergeJson(File.ReadAllText(disk), $"disk ({disk})");
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Failed to load disk action database at {disk}.");
            }
        }

        log.Info($"Action database ready: {entries.Count} entries.");
    }

    private void MergeJson(string json, string sourceLabel)
    {
        Dictionary<string, ActionDbEntry>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Dictionary<string, ActionDbEntry>>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to parse action database from {sourceLabel}.");
            return;
        }
        if (loaded == null)
            return;

        var added = 0;
        foreach (var (key, value) in loaded)
        {
            if (value == null || !uint.TryParse(key, out var actionId))
                continue;

            entries[actionId] = entries.TryGetValue(actionId, out var existing)
                ? Merge(existing, value)
                : value;
            added++;
        }

        log.Info($"Merged {added} action database entries from {sourceLabel}.");
    }

    /// <summary>
    /// Overlay <paramref name="overlay"/> onto <paramref name="baseEntry"/>: any
    /// field the overlay specifies wins, but a 0 potency (= "unknown") never wipes
    /// a value already present in the base. This lets a re-exported disk file (whose
    /// potencies may still be 0) sit on top of shipped potencies without erasing
    /// them.
    /// </summary>
    private static ActionDbEntry Merge(ActionDbEntry baseEntry, ActionDbEntry overlay) => new()
    {
        Name = overlay.Name ?? baseEntry.Name,
        Range = overlay.Range ?? baseEntry.Range,
        Shape = overlay.Shape ?? baseEntry.Shape,
        Radius = overlay.Radius ?? baseEntry.Radius,
        Width = overlay.Width ?? baseEntry.Width,
        DamageType = overlay.DamageType ?? baseEntry.DamageType,
        ComboFrom = overlay.ComboFrom ?? baseEntry.ComboFrom,
        Potency = overlay.Potency > 0 ? overlay.Potency : baseEntry.Potency,
        ComboPotency = overlay.ComboPotency > 0 ? overlay.ComboPotency : baseEntry.ComboPotency,
    };
}
