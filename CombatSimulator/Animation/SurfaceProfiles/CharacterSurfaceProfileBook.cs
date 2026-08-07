// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Animation.SurfaceProfiles;

public sealed class CharacterSurfaceProfileBook
{
    private const string ResourceName = "CombatSimulator.Resources.CharacterSurfaceProfiles.json";
    private readonly List<CharacterSurfaceProfile> profiles = new();

    public CharacterSurfaceProfileBook(IPluginLog log)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                log.Warning($"Character surface profiles missing: {ResourceName}");
                return;
            }

            using var reader = new StreamReader(stream);
            var set = JsonSerializer.Deserialize<CharacterSurfaceProfileSet>(reader.ReadToEnd(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (set?.Profiles == null)
                return;

            foreach (var profile in set.Profiles)
            {
                profile.RoleScales ??= new Dictionary<string, CharacterSurfaceRoleScales>(StringComparer.OrdinalIgnoreCase);
                profile.Bones ??= new Dictionary<string, CharacterSurfaceBoneOverride>(StringComparer.Ordinal);
                profiles.Add(profile);
            }
            log.Info($"Character surface profiles loaded: {profiles.Count} (schema {set.Version})");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Character surface profile load failed; legacy shapes will be used");
        }
    }

    public IReadOnlyList<CharacterSurfaceProfile> Profiles => profiles;

    public CharacterSurfaceProfile? Resolve(CharacterSurfaceIdentity identity)
    {
        CharacterSurfaceProfile? best = null;
        var bestScore = -1;
        foreach (var profile in profiles)
        {
            var score = profile.Specificity(identity);
            if (score <= bestScore) continue;
            best = profile;
            bestScore = score;
        }
        return best;
    }
}
