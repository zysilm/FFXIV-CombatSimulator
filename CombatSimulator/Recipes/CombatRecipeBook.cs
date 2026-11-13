// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Recipes;

/// <summary>
/// The battle profiles: the built-in ones (embedded JSON, each file an array) followed by the
/// user's own (one JSON object per file in <c>BattleProfiles</c> under the plugin's configuration
/// folder).
/// </summary>
public sealed class CombatRecipeBook
{
    private readonly IPluginLog log;
    private readonly string? userDirectory;
    private List<CombatRecipe>? recipes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="userDirectory">Where the user's own profiles are kept; null for built-in profiles only.</param>
    public CombatRecipeBook(IPluginLog log, string? userDirectory = null)
    {
        this.log = log;
        this.userDirectory = userDirectory;
    }

    public IReadOnlyList<CombatRecipe> Recipes
    {
        get
        {
            recipes ??= LoadRecipes();
            return recipes;
        }
    }

    public bool CanSave => !string.IsNullOrEmpty(userDirectory);

    /// <summary>Whether a profile of this name is built in (and so cannot be saved over).</summary>
    public bool IsBuiltIn(string name)
        => Recipes.Any(r => !r.IsUser && string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Save a profile as the user's own, over one of theirs of the same name. False (with the
    /// reason) when the name is empty or built in, or the file cannot be written.
    /// </summary>
    public bool Save(CombatRecipe recipe, out string error)
    {
        error = string.Empty;
        recipe.Name = recipe.Name.Trim();
        if (!CanSave || recipe.Name.Length == 0)
        {
            error = "A profile needs a name.";
            return false;
        }
        if (IsBuiltIn(recipe.Name))
        {
            error = $"'{recipe.Name}' is a built-in profile; choose another name.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(userDirectory!);
            File.WriteAllText(PathOf(recipe.Name), JsonSerializer.Serialize(recipe, JsonOptions));
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to save battle profile '{recipe.Name}'.");
            error = ex.Message;
            return false;
        }

        recipes = null;
        return true;
    }

    /// <summary>Delete one of the user's own profiles.</summary>
    public bool Delete(CombatRecipe recipe)
    {
        if (!recipe.IsUser || !CanSave)
            return false;

        try
        {
            File.Delete(PathOf(recipe.Name));
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to delete battle profile '{recipe.Name}'.");
            return false;
        }

        recipes = null;
        return true;
    }

    private string PathOf(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var file = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return Path.Combine(userDirectory!, file + ".json");
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

        LoadUserRecipes(loaded);
        return loaded;
    }

    private void LoadUserRecipes(List<CombatRecipe> loaded)
    {
        if (!CanSave || !Directory.Exists(userDirectory))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(userDirectory!, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to list the user's battle profiles.");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<CombatRecipe>(File.ReadAllText(file), JsonOptions);
                if (recipe == null)
                    continue;
                if (string.IsNullOrWhiteSpace(recipe.Name))
                    recipe.Name = Path.GetFileNameWithoutExtension(file);
                // A built-in profile of the same name wins: the file is left alone but not listed.
                if (loaded.Any(r => string.Equals(r.Name, recipe.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                recipe.IsUser = true;
                loaded.Add(recipe);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to load battle profile '{file}'.");
            }
        }
    }
}
