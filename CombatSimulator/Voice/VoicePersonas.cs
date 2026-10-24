// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CombatSimulator.Voice;

/// <summary>
/// One speaker persona of the voiced line pool: the lines one voice actor recorded, in one language
/// (Resources/VoicePersonas.json, built offline by clustering the ja and en recordings). An enemy is
/// given one persona and speaks only its lines, so it keeps one voice for the whole fight.
/// <see cref="Band"/> is <see cref="VoiceLineAge.Adult"/> for both male "mature" and female "adult".
/// <see cref="Fallback"/> lists personas to borrow an event from when this one has no line for it.
/// </summary>
public sealed record VoicePersona(string Id, string Language, VoiceLineVoice Voice, VoiceLineAge Band, bool Assignable, int Lines,
    string? Character, IReadOnlyList<string> Fallback)
{
    /// <summary>A named story character (Alphinaud, Y'shtola...), as opposed to an unnamed voice.</summary>
    public bool IsStoryCharacter => !string.IsNullOrEmpty(Character);

    public string BandName => Band switch
    {
        VoiceLineAge.Young => "young",
        VoiceLineAge.Elderly => "elderly",
        _ => Voice == VoiceLineVoice.Male ? "mature" : "adult",
    };

    /// <summary>For the debug view: the character when known, else the persona id.</summary>
    public string Display => IsStoryCharacter ? $"{Character} ({Id})" : Id;

    public static VoiceLineAge ParseBand(string? band) => band?.ToLowerInvariant() switch
    {
        "young" => VoiceLineAge.Young,
        "mature" or "adult" => VoiceLineAge.Adult,
        "elderly" => VoiceLineAge.Elderly,
        _ => VoiceLineAge.Any,
    };
}

/// <summary>The persona metadata, per language. Pure (Tests/Voice).</summary>
public sealed class VoicePersonaTable
{
    private readonly Dictionary<string, VoicePersona> byId = new(StringComparer.Ordinal);
    private readonly List<VoicePersona> all = new();

    public static VoicePersonaTable Empty { get; } = new();

    public int Count => all.Count;
    public IReadOnlyList<VoicePersona> All => all;

    public VoicePersona? Get(string? id) => id != null && byId.TryGetValue(id, out var p) ? p : null;

    public void Add(VoicePersona persona)
    {
        if (byId.ContainsKey(persona.Id))
            return;
        byId[persona.Id] = persona;
        all.Add(persona);
    }

    /// <summary>
    /// Which persona set a client language uses. The personas were built from the ja and en
    /// recordings only; German and French lines are other actors and were never clustered, so they
    /// get null (the director then picks per line, as before personas existed).
    /// </summary>
    public static string? PersonaLanguage(string clientLanguage) => clientLanguage switch
    {
        "ja" => "ja",
        "en" => "en",
        _ => null,
    };

    /// <summary>Any assignable persona exists for this persona language.</summary>
    public bool HasLanguage(string? language)
    {
        if (language == null)
            return false;
        foreach (var p in all)
            if (p.Assignable && p.Language == language)
                return true;
        return false;
    }

    /// <summary>Parses VoicePersonas.json. Rows with an unknown gender or band are kept but never
    /// assignable; a malformed file throws.</summary>
    public static VoicePersonaTable Parse(string json)
    {
        var table = new VoicePersonaTable();
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.GetProperty("personas").EnumerateArray())
        {
            var id = Str(row, "id");
            var language = Str(row, "lang");
            // Unknown gender or band: kept for lookups, never assignable.
            var voice = Str(row, "gender")?.ToLowerInvariant() switch
            {
                "male" => VoiceLineVoice.Male,
                "female" => VoiceLineVoice.Female,
                _ => VoiceLineVoice.NonHuman,
            };
            var band = VoicePersona.ParseBand(Str(row, "band"));
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(language))
                continue;
            var fallback = new List<string>();
            if (row.TryGetProperty("fallback", out var fb) && fb.ValueKind == JsonValueKind.Array)
                foreach (var f in fb.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(f.GetString()))
                        fallback.Add(f.GetString()!);
            var assignable = row.TryGetProperty("assignable", out var a) && a.ValueKind == JsonValueKind.True &&
                             voice != VoiceLineVoice.NonHuman && band != VoiceLineAge.Any;
            var lines = row.TryGetProperty("lines", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            var character = Str(row, "character");
            table.Add(new VoicePersona(id, language, voice, band, assignable, lines,
                string.IsNullOrWhiteSpace(character) ? null : character, fallback));
        }
        return table;
    }

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Assignable personas for a body, in id order. The body's band comes first: elderly bodies get
    /// elderly voices, children young ones, adults any young or mature/adult voice (so a group of
    /// adults spreads over the most voices). An empty band falls to the next (female elderly has no
    /// persona and takes female adult). Empty only when the gender has no persona at all.
    /// </summary>
    public List<VoicePersona> Candidates(string language, VoiceLineVoice voice, VoiceLineAge bodyAge, bool allowStoryCharacters)
    {
        VoiceLineAge[][] tiers = bodyAge switch
        {
            VoiceLineAge.Elderly => new[] { new[] { VoiceLineAge.Elderly }, new[] { VoiceLineAge.Adult }, new[] { VoiceLineAge.Young } },
            VoiceLineAge.Young => new[] { new[] { VoiceLineAge.Young }, new[] { VoiceLineAge.Adult }, new[] { VoiceLineAge.Elderly } },
            _ => new[] { new[] { VoiceLineAge.Young, VoiceLineAge.Adult }, new[] { VoiceLineAge.Elderly } },
        };
        var result = new List<VoicePersona>();
        foreach (var tier in tiers)
        {
            foreach (var p in all)
                if (Usable(p, language, voice, allowStoryCharacters) && Array.IndexOf(tier, p.Band) >= 0)
                    result.Add(p);
            if (result.Count > 0)
                break;
        }
        result.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return result;
    }

    /// <summary>
    /// The personas a speaker with <paramref name="persona"/> may voice lines from, in order: itself,
    /// then its fallbacks that are assignable, of the same language and gender (never crossing
    /// gender), same band before other bands, file order within each.
    /// </summary>
    public List<string> ChainFor(VoicePersona persona, bool allowStoryCharacters)
    {
        var chain = new List<string> { persona.Id };
        var otherBand = new List<string>();
        foreach (var id in persona.Fallback)
        {
            var p = Get(id);
            if (p == null || chain.Contains(p.Id) || otherBand.Contains(p.Id) ||
                !Usable(p, persona.Language, persona.Voice, allowStoryCharacters))
                continue;
            (p.Band == persona.Band ? chain : otherBand).Add(p.Id);
        }
        chain.AddRange(otherBand);
        return chain;
    }

    private static bool Usable(VoicePersona p, string language, VoiceLineVoice voice, bool allowStoryCharacters) =>
        p.Assignable && p.Language == language && p.Voice == voice && (allowStoryCharacters || !p.IsStoryCharacter);
}

/// <summary>An enemy's persona and the persona chain its lines come from (empty = no curated lines).</summary>
public sealed record VoicePersonaAssignment(VoicePersona? Persona, IReadOnlyList<string> Chain)
{
    public static VoicePersonaAssignment None { get; } = new(null, Array.Empty<string>());
}

/// <summary>
/// Gives each enemy one persona on first sight and keeps it. The choice is a stable hash of the
/// enemy's identity among the candidates for its body, moved on to a persona nobody on the field
/// holds yet when the hashed one is taken, so two adult men usually sound different. The same
/// identity gets its previous persona back for the rest of the session while that persona is free.
/// Pure (Tests/Voice).
/// </summary>
public sealed class VoicePersonaAssigner
{
    private readonly Dictionary<ulong, VoicePersonaAssignment> assigned = new();
    private readonly Dictionary<uint, string> remembered = new();
    private VoicePersonaTable table = VoicePersonaTable.Empty;
    private string? lastClientLanguage;

    /// <summary>Persona language in use; null when personas are off (no data, or de/fr).</summary>
    public string? Language { get; private set; }
    public bool AllowStoryCharacters { get; private set; } = true;
    public VoicePersonaTable Table => table;

    /// <summary>Personas drive line choice (data present for the language).</summary>
    public bool Active => Language != null;

    public IReadOnlyDictionary<ulong, VoicePersonaAssignment> Assigned => assigned;

    /// <summary>Set the data and language. Any change drops the current assignments; a language or
    /// data change also forgets the session memory (its persona ids mean other actors).</summary>
    public void Configure(VoicePersonaTable personas, string clientLanguage, bool allowStoryCharacters)
    {
        // Called every frame: nothing changed is the common case.
        if (ReferenceEquals(personas, table) && clientLanguage == lastClientLanguage && allowStoryCharacters == AllowStoryCharacters)
            return;
        lastClientLanguage = clientLanguage;
        var language = VoicePersonaTable.PersonaLanguage(clientLanguage);
        if (!personas.HasLanguage(language))
            language = null;
        if (!ReferenceEquals(personas, table) || language != Language)
        {
            table = personas;
            Language = language;
            AllowStoryCharacters = allowStoryCharacters;
            assigned.Clear();
            remembered.Clear();
            return;
        }
        if (allowStoryCharacters != AllowStoryCharacters)
        {
            AllowStoryCharacters = allowStoryCharacters;
            assigned.Clear();
        }
    }

    public VoicePersonaAssignment? Get(ulong key) => assigned.TryGetValue(key, out var a) ? a : null;

    public void Forget(ulong key) => assigned.Remove(key);

    /// <summary>Drop who holds what (end of a fight); the session memory stays.</summary>
    public void Clear() => assigned.Clear();

    /// <summary>Stable identity of an enemy: its BNpcBase and BNpcName rows and its entity id.</summary>
    public static uint IdentitySeed(uint bNpcBaseId, uint bNpcNameId, uint entityId) =>
        VoiceSelection.StableHash(VoiceSelection.StableHash(VoiceSelection.StableHash(bNpcBaseId) ^ bNpcNameId) ^ entityId);

    /// <summary>The persona of <paramref name="key"/>, assigning it on first call. Non-human bodies,
    /// inactive personas and a gender without personas get <see cref="VoicePersonaAssignment.None"/>.</summary>
    public VoicePersonaAssignment Assign(ulong key, uint identity, VoiceLineVoice voice, VoiceLineAge bodyAge)
    {
        if (assigned.TryGetValue(key, out var existing))
            return existing;
        var result = VoicePersonaAssignment.None;
        if (Language != null && voice is VoiceLineVoice.Male or VoiceLineVoice.Female)
        {
            var candidates = table.Candidates(Language, voice, bodyAge, AllowStoryCharacters);
            if (candidates.Count > 0)
            {
                var chosen = Choose(candidates, identity);
                remembered[identity] = chosen.Id;
                result = new VoicePersonaAssignment(chosen, table.ChainFor(chosen, AllowStoryCharacters));
            }
        }
        assigned[key] = result;
        return result;
    }

    private VoicePersona Choose(List<VoicePersona> candidates, uint identity)
    {
        var uses = new int[candidates.Count];
        foreach (var a in assigned.Values)
            if (a.Persona != null)
            {
                var i = candidates.FindIndex(c => c.Id == a.Persona.Id);
                if (i >= 0)
                    uses[i]++;
            }
        var least = int.MaxValue;
        foreach (var u in uses)
            least = Math.Min(least, u);
        if (remembered.TryGetValue(identity, out var before))
        {
            var i = candidates.FindIndex(c => c.Id == before);
            if (i >= 0 && uses[i] == least)
                return candidates[i];
        }
        var start = (int)(VoiceSelection.StableHash(identity) % (uint)candidates.Count);
        for (var step = 0; step < candidates.Count; step++)
        {
            var i = (start + step) % candidates.Count;
            if (uses[i] == least)
                return candidates[i];
        }
        return candidates[start];
    }
}
