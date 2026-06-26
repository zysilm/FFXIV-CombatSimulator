using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Animation.CollapseProfiles;

public sealed class CollapseProfileBook
{
    private readonly IPluginLog log;
    private List<CollapseProfile>? profiles;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public CollapseProfileBook(IPluginLog log)
    {
        this.log = log;
    }

    public IReadOnlyList<CollapseProfile> Profiles
    {
        get
        {
            profiles ??= LoadProfiles();
            return profiles;
        }
    }

    public CollapseProfile? FindById(string id)
    {
        foreach (var profile in Profiles)
            if (profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return profile;
        return null;
    }

    private List<CollapseProfile> LoadProfiles()
    {
        var loaded = new List<CollapseProfile>();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith("CombatSimulator.Resources.CollapseProfiles.", StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var profile = JsonSerializer.Deserialize<CollapseProfile>(json, JsonOptions);
                if (profile != null && !string.IsNullOrWhiteSpace(profile.Id))
                    loaded.Add(profile);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to load collapse profile resource '{resourceName}'.");
            }
        }

        return loaded;
    }
}
