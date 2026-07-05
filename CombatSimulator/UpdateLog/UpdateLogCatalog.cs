using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace CombatSimulator.UpdateLog;

public sealed class UpdateLogCatalog
{
    private const string EmbeddedResourceName = "CombatSimulator.UpdateLog.update-log.json";

    private readonly List<UpdateLogEntry> entries;

    private UpdateLogCatalog(List<UpdateLogEntry> entries)
    {
        this.entries = entries;
    }

    public static UpdateLogCatalog Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null)
            return new UpdateLogCatalog([]);

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var entries = JsonSerializer.Deserialize<List<UpdateLogEntry>>(json) ?? [];
        return new UpdateLogCatalog(entries);
    }

    public static string CurrentPluginVersion
    {
        get
        {
            var version = typeof(CombatSimulatorPlugin).Assembly.GetName().Version;
            return version == null ? "" : NormalizeVersion(version.ToString());
        }
    }

    public UpdateLogEntry? Find(string version)
    {
        var normalized = NormalizeVersion(version);
        return entries.FirstOrDefault(e => NormalizeVersion(e.Version) == normalized);
    }

    /// <summary>The most recent entries, newest first, capped at <paramref name="maxCount"/>.</summary>
    public IReadOnlyList<UpdateLogEntry> RecentEntries(int maxCount = 10)
    {
        return entries
            .OrderByDescending(ParseVersion)
            .Take(maxCount)
            .ToList();
    }

    private static Version ParseVersion(UpdateLogEntry entry)
    {
        var normalized = NormalizeVersion(entry.Version);
        return Version.TryParse(normalized, out var parsed) ? parsed : new Version(0, 0, 0, 0);
    }

    public static string NormalizeVersion(string? version)
    {
        var trimmed = (version ?? "").Trim();
        if (!Version.TryParse(trimmed, out var parsed))
            return trimmed;

        return new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0)).ToString();
    }
}
