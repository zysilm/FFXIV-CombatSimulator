using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Recipes;

public sealed class CombatRecipeBook
{
    private readonly IPluginLog log;
    private List<CombatRecipe>? recipes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public CombatRecipeBook(IPluginLog log)
    {
        this.log = log;
    }

    public IReadOnlyList<CombatRecipe> Recipes
    {
        get
        {
            recipes ??= LoadRecipes();
            return recipes;
        }
    }

    private List<CombatRecipe> LoadRecipes()
    {
        var loaded = new List<CombatRecipe>();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith("CombatSimulator.Resources.CombatRecipes.", StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var recipesFromFile = JsonSerializer.Deserialize<List<CombatRecipe>>(json, JsonOptions);
                if (recipesFromFile != null)
                    loaded.AddRange(recipesFromFile);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to load combat recipe resource '{resourceName}'.");
            }
        }

        if (loaded.Count == 0)
            loaded.Add(new CombatRecipe { Name = "Empty Skirmish", Description = "No embedded recipes were loaded." });

        return loaded;
    }
}
