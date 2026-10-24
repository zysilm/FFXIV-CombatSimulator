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
/// A <see cref="IsBoss"/> persona is built from the studio-processed (fx) boss/primal lines instead:
/// it is given to monsters and demihumans only, whatever its recorded gender, and never to a human.
/// </summary>
public sealed record VoicePersona(string Id, string Language, VoiceLineVoice Voice, VoiceLineAge Band, bool Assignable, int Lines,
    string? Character, IReadOnlyList<string> Fallback, bool IsBoss = false)
{
    /// <summary>A named story character (Alphinaud, Y'shtola...), as opposed to an unnamed voice.</summary>
    public bool IsStoryCharacter => !string.IsNullOrEmpty(Character);

    /// <summary>The kind of speaker body this persona is for: its gender for a human voice,
    /// <see cref="VoiceLineVoice.NonHuman"/> for a boss voice.</summary>
    public VoiceLineVoice SpeakerVoice => IsBoss ? VoiceLineVoice.NonHuman : Voice;

    public string BandName => IsBoss ? "boss" : Band switch
    {
        VoiceLineAge.Young => "young",
        VoiceLineAge.Elderly => "elderly",
        _ => Voice == VoiceLineVoice.Male ? "mature" : "adult",
    };

    /// <summary>Picker filter group: "Boss", or gender and band ("Male mature", "Female young"...).</summary>
    public string Category => IsBoss ? "Boss" : $"{(Voice == VoiceLineVoice.Female ? "Female" : "Male")} {BandName}";

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
            var boss = string.Equals(Str(row, "kind"), "boss", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(language))
                continue;
            var fallback = new List<string>();
            if (row.TryGetProperty("fallback", out var fb) && fb.ValueKind == JsonValueKind.Array)
                foreach (var f in fb.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(f.GetString()))
                        fallback.Add(f.GetString()!);
            // A boss voice has no age band; its gender is only for the label.
            var assignable = row.TryGetProperty("assignable", out var a) && a.ValueKind == JsonValueKind.True &&
                             voice != VoiceLineVoice.NonHuman && (boss || band != VoiceLineAge.Any);
            var lines = row.TryGetProperty("lines", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            var character = Str(row, "character");
            table.Add(new VoicePersona(id, language, voice, boss ? VoiceLineAge.Any : band, assignable, lines,
                string.IsNullOrWhiteSpace(character) ? null : character, fallback, boss));
        }
        return table;
    }

    /// <summary>
    /// Every persona a speaker with this body may be given by hand, in id order: all assignable
    /// personas of its gender (any band) for a human, every boss persona for a monster/demihuman.
    /// </summary>
    public List<VoicePersona> Choices(string language, VoiceLineVoice speakerVoice, bool allowStoryCharacters)
    {
        var result = new List<VoicePersona>();
        foreach (var p in all)
            if (Usable(p, language, speakerVoice, allowStoryCharacters))
                result.Add(p);
        result.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return result;
    }

    /// <summary>A readable name for the voice picker: the character when known, else "Male voice 3
    /// (mature)" or "Boss voice 2 (female)", numbered among the assignable personas of that kind.</summary>
    public string Label(VoicePersona persona)
    {
        if (persona.IsStoryCharacter)
            return persona.Character!;
        var number = 1;
        foreach (var p in all)
            if (p.Assignable && p.Language == persona.Language && p.IsBoss == persona.IsBoss &&
                (persona.IsBoss || p.Voice == persona.Voice) && string.CompareOrdinal(p.Id, persona.Id) < 0)
                number++;
        var gender = persona.Voice == VoiceLineVoice.Female ? "female" : "male";
        return persona.IsBoss
            ? $"Boss voice {number} ({gender})"
            : $"{(persona.Voice == VoiceLineVoice.Female ? "Female" : "Male")} voice {number} ({persona.BandName})";
    }

    /// <summary>
    /// The persona a saved choice names, for a speaker of this body in this language; null when it no
    /// longer fits (other gender, removed, story characters off). A choice saved under the other
    /// persona language maps to the same-numbered human persona there (the ja and en personas are the
    /// same actors); boss personas are clustered per language and do not map.
    /// </summary>
    public VoicePersona? ResolveChoice(string? id, string language, VoiceLineVoice speakerVoice, bool allowStoryCharacters)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        var p = Get(id);
        if ((p == null || p.Language != language) && !id.Contains("-b", StringComparison.Ordinal) && id.Length > 3 && id[2] == '-')
            p = Get(language + id.Substring(2));
        // A hand-picked voice is honoured whatever the speaker's body: the owner may give a soldier a
        // woman's or a boss voice. The lines then follow the persona's kind, not the body's.
        return p != null && UsableAnyKind(p, language, allowStoryCharacters) ? p : null;
    }

    /// <summary>Every assignable persona of the language, any gender or kind, for the voice picker.</summary>
    public List<VoicePersona> AllChoices(string language, bool allowStoryCharacters)
    {
        var result = new List<VoicePersona>();
        foreach (var p in all)
            if (UsableAnyKind(p, language, allowStoryCharacters))
                result.Add(p);
        result.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return result;
    }

    private static bool UsableAnyKind(VoicePersona p, string language, bool allowStoryCharacters) =>
        p.Assignable && p.Language == language && (allowStoryCharacters || !p.IsStoryCharacter);

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Assignable personas for a body, in id order. The body's band comes first: elderly bodies get
    /// elderly voices, children young ones, adults any young or mature/adult voice (so a group of
    /// adults spreads over the most voices). An empty band falls to the next (female elderly has no
    /// persona and takes female adult). Empty only when the gender has no persona at all.
    /// A non-human body (monster, demihuman) gets every boss persona, whatever their gender.
    /// </summary>
    public List<VoicePersona> Candidates(string language, VoiceLineVoice voice, VoiceLineAge bodyAge, bool allowStoryCharacters)
    {
        if (voice == VoiceLineVoice.NonHuman)
            return Choices(language, voice, allowStoryCharacters);
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
    /// gender), same band before other bands, file order within each. A boss persona falls back to
    /// other boss personas only, most similar voice first (file order).
    /// </summary>
    public List<string> ChainFor(VoicePersona persona, bool allowStoryCharacters)
    {
        var chain = new List<string> { persona.Id };
        var otherBand = new List<string>();
        foreach (var id in persona.Fallback)
        {
            var p = Get(id);
            if (p == null || chain.Contains(p.Id) || otherBand.Contains(p.Id) ||
                !Usable(p, persona.Language, persona.SpeakerVoice, allowStoryCharacters))
                continue;
            (p.Band == persona.Band ? chain : otherBand).Add(p.Id);
        }
        chain.AddRange(otherBand);
        return chain;
    }

    /// <summary><paramref name="speakerVoice"/> is the speaker's body: a gender takes human personas
    /// of that gender only, <see cref="VoiceLineVoice.NonHuman"/> boss personas only.</summary>
    private static bool Usable(VoicePersona p, string language, VoiceLineVoice speakerVoice, bool allowStoryCharacters) =>
        p.Assignable && p.Language == language && p.SpeakerVoice == speakerVoice && (allowStoryCharacters || !p.IsStoryCharacter);
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

    /// <summary>Key under which a hand-picked voice is saved: the enemy's kind (BNpcBase + BNpcName),
    /// so every enemy of that kind keeps it across fights and sessions; the name when both ids are 0.
    /// Null when there is nothing to key on.</summary>
    public static string? OverrideKey(uint bNpcBaseId, uint bNpcNameId, string? name) =>
        bNpcBaseId != 0 || bNpcNameId != 0 ? $"{bNpcBaseId}:{bNpcNameId}"
        : string.IsNullOrWhiteSpace(name) ? null : $"name:{name.Trim()}";

    /// <summary>The persona of <paramref name="key"/>, assigning it on first call. Human bodies get a
    /// persona of their gender and age, monsters and demihumans (<see cref="VoiceLineVoice.NonHuman"/>) a
    /// boss persona. <paramref name="chosen"/> is a hand-picked persona id: used when it fits the body,
    /// ignored otherwise. Inactive personas and a body without personas get
    /// <see cref="VoicePersonaAssignment.None"/>.</summary>
    public VoicePersonaAssignment Assign(ulong key, uint identity, VoiceLineVoice voice, VoiceLineAge bodyAge, string? chosen = null)
    {
        if (assigned.TryGetValue(key, out var existing))
            return existing;
        var result = VoicePersonaAssignment.None;
        if (Language != null)
        {
            var persona = table.ResolveChoice(chosen, Language, voice, AllowStoryCharacters);
            if (persona == null)
            {
                var candidates = table.Candidates(Language, voice, bodyAge, AllowStoryCharacters);
                if (candidates.Count > 0)
                {
                    persona = Choose(candidates, identity);
                    remembered[identity] = persona.Id;
                }
            }
            if (persona != null)
                result = new VoicePersonaAssignment(persona, table.ChainFor(persona, AllowStoryCharacters));
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
