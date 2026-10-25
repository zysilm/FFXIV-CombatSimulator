// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Voice;

/// <summary>Whose voice a recorded line is, and whose body a speaker has. A line only plays from a
/// speaker of the same kind: a woman's line from a male soldier breaks the scene.</summary>
public enum VoiceLineVoice
{
    Male,
    Female,
    /// <summary>Monster, beast and demihuman models. As a speaker they take only the processed boss
    /// voices (<see cref="VoiceLineEntry.Fx"/> lines, through a boss persona), whatever the line's gender.</summary>
    NonHuman,
}

/// <summary>How old a recorded voice sounds, or how old a speaker's body is. Coarse on purpose:
/// the line labels come from an age model smoothed per speaker and are only trusted this far.</summary>
public enum VoiceLineAge
{
    /// <summary>Not known. A line without an age suits every speaker; a speaker without one
    /// (monsters, unreadable bodies) takes any line.</summary>
    Any,
    Young,
    Adult,
    Elderly,
}

/// <summary>Excel sheet holding a voiced line's words. Most battle barks are NpcYell rows; duty
/// dialogue lives in InstanceContentTextData.</summary>
public enum VoiceLineTextSheet
{
    NpcYell,
    InstanceContentTextData,
}

/// <summary>
/// One curated voiced line. <see cref="VoiceId"/> is the vo_line file id and <see cref="YellId"/>
/// the row with the same words in <see cref="Sheet"/>. They are separate because the game does
/// not number them alike (file 8202037 is NpcYell 7768, not 2037); the pairs were proven offline.
/// <see cref="Extended"/> is provenance only (matched by speech recognition). <see cref="Fx"/> marks a
/// recording with heavy studio processing (reverb, layered or shifted voice: bosses, primals); a
/// human speaker never gets one, it would make a common soldier sound like a god, and a monster or
/// demihuman gets only these.
/// <see cref="PersonaJa"/>/<see cref="PersonaEn"/> name the speaker persona (one voice actor, see
/// <see cref="VoicePersonaTable"/>) of the recording in that language; null when the line has none.
/// For an fx line it is a boss persona.
/// </summary>
public sealed record VoiceLineEntry(uint VoiceId, uint YellId, VoiceEvent[] Events, VoiceLineVoice Voice, bool Story, float Weight,
    VoiceLineAge Age = VoiceLineAge.Any, VoiceLineTextSheet Sheet = VoiceLineTextSheet.NpcYell, bool Extended = false, bool Fx = false,
    string? PersonaJa = null, string? PersonaEn = null)
{
    public string PathFor(string language) => $"sound/voice/vo_line/{VoiceId:D7}_{language}.scd";

    /// <summary>Persona of this line's recording in a persona language ("ja"/"en"); null otherwise.</summary>
    public string? PersonaFor(string? language) => language switch
    {
        "ja" => PersonaJa,
        "en" => PersonaEn,
        _ => null,
    };

    /// <summary>Pool spelling of a text sheet; absent means NpcYell, an unknown name is null.</summary>
    public static VoiceLineTextSheet? ParseSheet(string? sheet) => sheet switch
    {
        null or "" or "NpcYell" => VoiceLineTextSheet.NpcYell,
        "InstanceContentTextData" => VoiceLineTextSheet.InstanceContentTextData,
        _ => null,
    };

    /// <summary>Pool spelling of an age ("young", "adult", "elderly"); anything else is unknown.</summary>
    public static VoiceLineAge ParseAge(string? age) => age?.ToLowerInvariant() switch
    {
        "young" => VoiceLineAge.Young,
        "adult" => VoiceLineAge.Adult,
        "elderly" => VoiceLineAge.Elderly,
        _ => VoiceLineAge.Any,
    };

    /// <summary>Speaker age from the human Customize BodyType byte (1 adult, 3 elderly, 4 child).</summary>
    public static VoiceLineAge AgeFromBodyType(byte bodyType) => bodyType switch
    {
        1 => VoiceLineAge.Adult,
        3 => VoiceLineAge.Elderly,
        4 => VoiceLineAge.Young,
        _ => VoiceLineAge.Any,
    };
}

/// <summary>Tunables for how often enemies talk. The defaults keep a big fight to roughly one line
/// every few seconds; the outcome moments (engage, kill, victory, death) get through sooner.</summary>
public sealed class VoiceLineSettings
{
    /// <summary>Quiet time after a line before an ordinary event may start the next one.</summary>
    public float Gap { get; set; } = 3.5f;
    /// <summary>Quiet time after a line for <see cref="VoiceLinePicker.IsPriority"/> events.</summary>
    public float PriorityGap { get; set; } = 0.5f;
    /// <summary>One enemy may not speak two lines within this long.</summary>
    public float ActorCooldown { get; set; } = 8f;
    /// <summary>Lines remembered as "recent"; a pick avoids them while anything else qualifies.</summary>
    public int RecentMemory { get; set; } = 24;
}

/// <summary>
/// Chooses voiced lines and keeps them rare: one line on screen at a time across all enemies, a
/// quiet gap after each, per-enemy and per-event cooldowns, a chance roll for the frequent events,
/// and no repeats of a recent line. Pure and clocked by <see cref="Advance"/> (Tests/Voice).
/// </summary>
public sealed class VoiceLinePicker
{
    private readonly List<VoiceLineEntry> entries = new();
    private readonly VoiceLineSettings settings;
    private readonly Dictionary<ulong, float> actorReadyAt = new();
    private readonly Dictionary<VoiceEvent, float> eventReadyAt = new();
    private readonly LinkedList<uint> recent = new();
    private readonly HashSet<uint> unavailable = new();
    private float busyUntil = float.NegativeInfinity;

    public VoiceLinePicker(VoiceLineSettings settings) => this.settings = settings;

    public float Now { get; private set; }
    public int Count => entries.Count;
    public IReadOnlyList<VoiceLineEntry> Entries => entries;
    /// <summary>A line is still on screen / playing.</summary>
    public bool IsBusy => Now < busyUntil;

    public void SetEntries(IEnumerable<VoiceLineEntry> source)
    {
        entries.Clear();
        unavailable.Clear();
        recent.Clear();
        foreach (var e in source)
            if (e.Events.Length > 0 && e.Weight > 0f)
                entries.Add(e);
    }

    public void Advance(float deltaTime)
    {
        if (deltaTime > 0f && !float.IsNaN(deltaTime))
            Now += deltaTime;
    }

    /// <summary>Cooldowns only; which lines exist and were heard stays (no repeats across fights).</summary>
    public void Reset()
    {
        actorReadyAt.Clear();
        eventReadyAt.Clear();
        busyUntil = float.NegativeInfinity;
    }

    public void Forget(ulong actorKey) => actorReadyAt.Remove(actorKey);

    /// <summary>Never offer this line again this session (its file or text is missing in the
    /// client language).</summary>
    public void MarkUnavailable(uint voiceId) => unavailable.Add(voiceId);

    /// <summary>Once-per-fight moments that read as a bug when skipped.</summary>
    public static bool IsPriority(VoiceEvent e) =>
        e is VoiceEvent.Engage or VoiceEvent.KilledPlayer or VoiceEvent.Victory or VoiceEvent.Death;

    /// <summary>Share of events that try for a line at all. Skills and heavy hits happen every few
    /// seconds per enemy; voicing each would be a wall of talk.</summary>
    public static double Chance(VoiceEvent e) => e switch
    {
        VoiceEvent.Skill => 0.45,
        VoiceEvent.Hurt => 0.35,
        VoiceEvent.Engage => 0.9,
        VoiceEvent.LowHp => 0.8,
        VoiceEvent.AllyDeath => 0.7,
        VoiceEvent.Death => 0.75,
        _ => 1.0,
    };

    /// <summary>Across all enemies: after a line for this event, how long until the event may
    /// voice another. Keeps the same kind of line from following itself.</summary>
    public static float EventCooldown(VoiceEvent e) => e switch
    {
        VoiceEvent.Skill => 8f,
        VoiceEvent.Hurt => 10f,
        VoiceEvent.Taunt => 12f,
        VoiceEvent.Engage => 6f,
        VoiceEvent.LowHp => 6f,
        VoiceEvent.AllyDeath => 10f,
        VoiceEvent.Death => 3f,
        _ => 0f,
    };

    /// <summary>Events tried in order when the first has no line for this voice. Victory and
    /// kill lines are few, so they borrow from each other and from taunts.</summary>
    public static IReadOnlyList<VoiceEvent> Chain(VoiceEvent e) => e switch
    {
        VoiceEvent.KilledPlayer => new[] { VoiceEvent.KilledPlayer, VoiceEvent.Victory, VoiceEvent.Taunt },
        VoiceEvent.Victory => new[] { VoiceEvent.Victory, VoiceEvent.KilledPlayer },
        VoiceEvent.Engage => new[] { VoiceEvent.Engage, VoiceEvent.Taunt },
        _ => new[] { e },
    };

    /// <summary>
    /// Whether a line tagged <paramref name="chained"/> may answer <paramref name="asked"/>. A kill or
    /// victory that borrows a taunt must not get an opening or resuming call ("here I come", "now it
    /// begins": taunts also tagged Engage), which reads as the enemy restarting the fight over a fallen
    /// player. Taunts over the fallen use the same rule.
    /// </summary>
    public static bool Answers(VoiceLineEntry line, VoiceEvent asked, VoiceEvent chained) =>
        !(asked is VoiceEvent.KilledPlayer or VoiceEvent.Victory && chained == VoiceEvent.Taunt &&
          Array.IndexOf(line.Events, VoiceEvent.Engage) >= 0);

    /// <summary>Only the shared line gate: no line on screen and the quiet gap after it has passed.
    /// Scripted talk (taunts over a fallen player) waits on this alone, not on event cooldowns.</summary>
    public bool GateOpen(VoiceEvent e) => Now >= busyUntil + (IsPriority(e) ? settings.PriorityGap : settings.Gap);

    public bool CanSpeak(ulong actorKey, VoiceEvent e)
    {
        var gap = IsPriority(e) ? settings.PriorityGap : settings.Gap;
        if (Now < busyUntil + gap)
            return false;
        // The per-actor rest keeps one enemy from monopolising the talk, but the outcome moments must
        // get through: the killer has usually just voiced the killing attack itself.
        if (!IsPriority(e) && actorReadyAt.TryGetValue(actorKey, out var a) && Now < a)
            return false;
        return !eventReadyAt.TryGetValue(e, out var t) || Now >= t;
    }

    /// <summary>How many lines could ever voice this event for this voice (chain included).</summary>
    public int Available(VoiceEvent e, VoiceLineVoice voice)
    {
        var n = 0;
        foreach (var chained in Chain(e))
            foreach (var line in entries)
                if (Fits(line, chained, voice, e))
                    n++;
        return n;
    }

    /// <summary>Weighted pick for an event and voice; recent lines only when nothing else fits.
    /// With a speaker <paramref name="age"/>, lines of that age (or of no recorded age) come first
    /// within each event of the chain; any other line of the event is the fallback, so an age
    /// never silences a speaker. Not repeating a recent line still beats the age match (a small
    /// elderly pool would otherwise loop). <paramref name="random"/> returns [0,1).</summary>
    public VoiceLineEntry? Pick(VoiceEvent e, VoiceLineVoice voice, Func<double> random, VoiceLineAge age = VoiceLineAge.Any)
    {
        foreach (var chained in Chain(e))
        {
            var pick = age == VoiceLineAge.Any ? null : PickFrom(chained, voice, random, avoidRecent: true, age, asked: e);
            pick ??= PickFrom(chained, voice, random, avoidRecent: true, asked: e);
            if (age != VoiceLineAge.Any)
                pick ??= PickFrom(chained, voice, random, avoidRecent: false, age, asked: e);
            pick ??= PickFrom(chained, voice, random, avoidRecent: false, asked: e);
            if (pick != null)
                return pick;
        }
        return null;
    }

    /// <summary>A line started: <paramref name="seconds"/> is how long it occupies the screen.</summary>
    public void Commit(ulong actorKey, VoiceEvent e, VoiceLineEntry line, float seconds)
    {
        busyUntil = Now + Math.Max(0.5f, seconds);
        actorReadyAt[actorKey] = Now + settings.ActorCooldown;
        eventReadyAt[e] = Now + EventCooldown(e);
        recent.Remove(line.VoiceId);
        recent.AddFirst(line.VoiceId);
        while (recent.Count > Math.Max(0, settings.RecentMemory))
            recent.RemoveLast();
    }

    public bool WasRecent(uint voiceId) => recent.Contains(voiceId);

    /// <summary>Remember a line as heard without touching the gate or cooldowns (a voice preview).</summary>
    public void MarkHeard(uint voiceId)
    {
        recent.Remove(voiceId);
        recent.AddFirst(voiceId);
        while (recent.Count > Math.Max(0, settings.RecentMemory))
            recent.RemoveLast();
    }

    private bool Fits(VoiceLineEntry line, VoiceEvent e, VoiceLineVoice voice, VoiceEvent? asked = null) =>
        SuitsSpeaker(line, voice) && !unavailable.Contains(line.VoiceId) && Array.IndexOf(line.Events, e) >= 0 &&
        (asked == null || Answers(line, asked.Value, e));

    /// <summary>A human speaker takes unprocessed lines of its gender; a non-human one (monster,
    /// demihuman) takes only processed boss lines, of either gender.</summary>
    public static bool SuitsSpeaker(VoiceLineEntry line, VoiceLineVoice voice) =>
        voice == VoiceLineVoice.NonHuman ? line.Fx || line.Voice == VoiceLineVoice.NonHuman : line.Voice == voice && !line.Fx;

    private static bool AgeFits(VoiceLineEntry line, VoiceLineAge age) =>
        age == VoiceLineAge.Any || line.Age == VoiceLineAge.Any || line.Age == age;

    // ---- Persona-restricted picking: one enemy, one voice actor. ----

    /// <summary>Lines of exactly this persona for exactly this event (no event chain). With <paramref name="asked"/>,
    /// only the lines that may answer that event through <paramref name="e"/> (<see cref="Answers"/>).</summary>
    public int CountFor(VoiceEvent e, VoiceLineVoice voice, string persona, string language, VoiceEvent? asked = null)
    {
        var n = 0;
        foreach (var line in entries)
            if (Fits(line, e, voice, asked) && line.PersonaFor(language) == persona)
                n++;
        return n;
    }

    /// <summary>How many lines could voice this event for a speaker with this persona chain (own
    /// persona first, then its fallbacks), event chain included. 0 means the speaker stays silent.</summary>
    public int Available(VoiceEvent e, VoiceLineVoice voice, IReadOnlyList<string> personas, string language)
    {
        var n = 0;
        foreach (var persona in personas)
            foreach (var chained in Chain(e))
                n += CountFor(chained, voice, persona, language, e);
        return n;
    }

    /// <summary>
    /// Pick for a speaker locked to a persona. <paramref name="personas"/> is its own persona then the
    /// fallbacks to borrow from, in order; the first persona that has a line anywhere in the event
    /// chain (<see cref="Chain"/>, e.g. kill -> victory -> taunt) is used, so a speaker keeps its own
    /// voice for a taunt rather than borrow another actor's kill line. Recent lines only when that
    /// persona has nothing else for the event. No persona with a line = null (silence, never a
    /// random voice). Age is not consulted: the persona is the age.
    /// </summary>
    public VoiceLineEntry? Pick(VoiceEvent e, VoiceLineVoice voice, IReadOnlyList<string> personas, string language, Func<double> random)
    {
        foreach (var persona in personas)
            foreach (var chained in Chain(e))
            {
                var pick = PickFrom(chained, voice, random, avoidRecent: true, persona: persona, language: language, asked: e)
                           ?? PickFrom(chained, voice, random, avoidRecent: false, persona: persona, language: language, asked: e);
                if (pick != null)
                    return pick;
            }
        return null;
    }

    // ---- Taunts over the fallen: variety in one voice. ----

    /// <summary>What a speaker says over a fallen party: its kill, victory and taunt lines together.</summary>
    public static IReadOnlyList<VoiceEvent> FallenTauntEvents { get; } = new[] { VoiceEvent.KilledPlayer, VoiceEvent.Victory, VoiceEvent.Taunt };

    /// <summary>Below this many lines the own persona's pool is topped up from its fallbacks.</summary>
    public const int MinFallenTauntPool = 3;

    /// <summary>
    /// The lines a speaker locked to a persona chain may say over the fallen: every kill, victory and
    /// taunt line of its own persona (<paramref name="personas"/>[0]). Only when that is fewer than
    /// <see cref="MinFallenTauntPool"/> lines are the fallbacks' lines added, one whole persona at a time,
    /// in chain order (same gender, so the voice stays close).
    /// </summary>
    public List<VoiceLineEntry> FallenTauntPool(VoiceLineVoice voice, IReadOnlyList<string> personas, string language)
    {
        var pool = new List<VoiceLineEntry>();
        foreach (var persona in personas)
        {
            foreach (var line in entries)
                if (line.PersonaFor(language) == persona && !pool.Contains(line) && FitsAny(line, FallenTauntEvents, voice))
                    pool.Add(line);
            if (pool.Count >= MinFallenTauntPool)
                break;
        }
        return pool;
    }

    /// <summary>
    /// A taunt over the fallen: uniform among <see cref="FallenTauntPool"/>, never a recent line while
    /// another is fresh; when every line is recent, the one heard longest ago. Null = no line.
    /// </summary>
    public VoiceLineEntry? PickFallenTaunt(VoiceLineVoice voice, IReadOnlyList<string> personas, string language, Func<double> random) =>
        PickUniform(FallenTauntPool(voice, personas, language), random);

    /// <summary>Any line of exactly this persona (any event), for a voice preview; same no-repeat rule.</summary>
    public VoiceLineEntry? PickAnyOf(VoiceLineVoice voice, string persona, string language, Func<double> random)
    {
        var pool = new List<VoiceLineEntry>();
        foreach (var line in entries)
            if (line.PersonaFor(language) == persona && SuitsSpeaker(line, voice) && !unavailable.Contains(line.VoiceId))
                pool.Add(line);
        return PickUniform(pool, random);
    }

    private VoiceLineEntry? PickUniform(List<VoiceLineEntry> pool, Func<double> random)
    {
        if (pool.Count == 0)
            return null;
        var fresh = pool.FindAll(l => !recent.Contains(l.VoiceId));
        if (fresh.Count > 0)
            return fresh[Math.Min(fresh.Count - 1, (int)(random() * fresh.Count))];
        // Everything was heard lately: the oldest of them (the recent list is newest first).
        VoiceLineEntry? oldest = null;
        foreach (var id in recent)
            if (pool.Find(l => l.VoiceId == id) is { } hit)
                oldest = hit;
        return oldest ?? pool[0];
    }

    private bool FitsAny(VoiceLineEntry line, IReadOnlyList<VoiceEvent> events, VoiceLineVoice voice)
    {
        foreach (var e in events)
            if (Fits(line, e, voice, VoiceEvent.KilledPlayer))
                return true;
        return false;
    }

    private VoiceLineEntry? PickFrom(VoiceEvent e, VoiceLineVoice voice, Func<double> random, bool avoidRecent, VoiceLineAge age = VoiceLineAge.Any,
        string? persona = null, string? language = null, VoiceEvent? asked = null)
    {
        var total = 0.0;
        foreach (var line in entries)
            if (Fits(line, e, voice, asked) && AgeFits(line, age) && !(avoidRecent && recent.Contains(line.VoiceId)) &&
                (persona == null || line.PersonaFor(language) == persona))
                total += line.Weight;
        if (total <= 0)
            return null;
        var r = random() * total;
        VoiceLineEntry? last = null;
        foreach (var line in entries)
        {
            if (!Fits(line, e, voice, asked) || !AgeFits(line, age) || avoidRecent && recent.Contains(line.VoiceId) ||
                persona != null && line.PersonaFor(language) != persona)
                continue;
            last = line;
            r -= line.Weight;
            if (r < 0)
                return line;
        }
        return last;
    }
}

/// <summary>Reads what the bubble needs from a voiced line's .scd without decoding it.</summary>
public static class VoiceLineAudio
{
    /// <summary>
    /// Duration of the first audio entry. SCD layout (verified on the vo_line set): u16 header size
    /// at 0x0E, the offsets table there; +0x0C points at the audio-entry offset list. An entry starts
    /// with data size, channels, sample rate, codec; MS-ADPCM (0x0C) keeps block align at +0x2C and
    /// samples per block at +0x32 of the entry. Unknown codecs return false (the caller then uses a
    /// default bubble time, it does not skip the line).
    /// </summary>
    public static bool TryReadDuration(ReadOnlySpan<byte> scd, out float seconds)
    {
        seconds = 0f;
        try
        {
            if (scd.Length < 0x40 || scd[0] != (byte)'S' || scd[1] != (byte)'E' || scd[2] != (byte)'D' || scd[3] != (byte)'B')
                return false;
            int tables = BitConverter.ToUInt16(scd.Slice(0x0E, 2));
            if (tables <= 0 || tables + 0x10 > scd.Length)
                return false;
            var list = BitConverter.ToInt32(scd.Slice(tables + 0x0C, 4));
            if (list <= 0 || list + 4 > scd.Length)
                return false;
            var entry = BitConverter.ToInt32(scd.Slice(list, 4));
            if (entry <= 0 || entry + 0x34 > scd.Length)
                return false;
            var size = BitConverter.ToInt32(scd.Slice(entry, 4));
            var channels = BitConverter.ToInt32(scd.Slice(entry + 4, 4));
            var rate = BitConverter.ToInt32(scd.Slice(entry + 8, 4));
            var codec = BitConverter.ToInt32(scd.Slice(entry + 12, 4));
            if (size <= 0 || rate <= 0 || channels <= 0)
                return false;
            if (codec == 0x0C)
            {
                int blockAlign = BitConverter.ToUInt16(scd.Slice(entry + 0x2C, 2));
                int samplesPerBlock = BitConverter.ToUInt16(scd.Slice(entry + 0x32, 2));
                if (blockAlign <= 0 || samplesPerBlock <= 0)
                    return false;
                seconds = (float)size / blockAlign * samplesPerBlock / rate;
            }
            else if (codec == 0x01)
            {
                seconds = size / 2f / channels / rate;
            }
            else
            {
                return false;
            }
            return seconds > 0f && seconds < 60f;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Bubble time for a line: a little longer than the voice, readable when short.</summary>
    public static float BubbleSeconds(float audioSeconds) => Math.Clamp(audioSeconds + 1.2f, 2.5f, 7f);
}
