using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Simulation;

public class ActionPotencyProvider
{
    private const string ResourceName = "CombatSimulator.Resources.ActionPotencies.json";

    private readonly IPluginLog log;
    private readonly Dictionary<uint, ActionPotencyEntry> entries = new();

    public ActionPotencyProvider(IPluginLog log)
    {
        this.log = log;
        LoadEmbeddedEntries();
    }

    public bool TryGet(uint actionId, out ActionPotencyEntry entry)
    {
        if (entries.TryGetValue(actionId, out var found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private void LoadEmbeddedEntries()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream == null)
                return;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ActionPotencyEntry>>(json);
            if (loaded == null)
                return;

            foreach (var (key, value) in loaded)
            {
                if (!uint.TryParse(key, out var actionId))
                    continue;
                if (value.Potency <= 0 && value.ComboPotency <= 0)
                    continue;
                entries[actionId] = value;
            }

            log.Info($"Loaded {entries.Count} action potency overrides.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to load action potency overrides.");
        }
    }
}

public class ActionPotencyEntry
{
    public int Potency { get; set; }
    public int ComboPotency { get; set; }
}
