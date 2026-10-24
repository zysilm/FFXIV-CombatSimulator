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
    /// <summary>Monster/beast models. No curated line is non-human today, so these stay silent
    /// (their label-based monster sounds still work).</summary>
    NonHuman,
}

/// <summary>
/// One curated voiced line. <see cref="VoiceId"/> is the vo_line file id and <see cref="YellId"/>
/// the NpcYell row with the same words. They are separate because the game does not number them
/// alike (file 8202037 is NpcYell 7768, not 2037); the pairs were proven offline.
/// </summary>
public sealed record VoiceLineEntry(uint VoiceId, uint YellId, VoiceEvent[] Events, VoiceLineVoice Voice, bool Story, float Weight)
{
    public string PathFor(string language) => $"sound/voice/vo_line/{VoiceId:D7}_{language}.scd";
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
        VoiceEvent.Skill => 0.3,
        VoiceEvent.Hurt => 0.35,
        VoiceEvent.Engage => 0.7,
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

    public bool CanSpeak(ulong actorKey, VoiceEvent e)
    {
        var gap = IsPriority(e) ? settings.PriorityGap : settings.Gap;
        if (Now < busyUntil + gap)
            return false;
        if (actorReadyAt.TryGetValue(actorKey, out var a) && Now < a)
            return false;
        return !eventReadyAt.TryGetValue(e, out var t) || Now >= t;
    }

    /// <summary>How many lines could ever voice this event for this voice (chain included).</summary>
    public int Available(VoiceEvent e, VoiceLineVoice voice)
    {
        var n = 0;
        foreach (var chained in Chain(e))
            foreach (var line in entries)
                if (Fits(line, chained, voice))
                    n++;
        return n;
    }

    /// <summary>Weighted pick for an event and voice; recent lines only when nothing else fits.
    /// <paramref name="random"/> returns [0,1).</summary>
    public VoiceLineEntry? Pick(VoiceEvent e, VoiceLineVoice voice, Func<double> random)
    {
        foreach (var chained in Chain(e))
        {
            var pick = PickFrom(chained, voice, random, avoidRecent: true) ?? PickFrom(chained, voice, random, avoidRecent: false);
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

    private bool Fits(VoiceLineEntry line, VoiceEvent e, VoiceLineVoice voice) =>
        line.Voice == voice && !unavailable.Contains(line.VoiceId) && Array.IndexOf(line.Events, e) >= 0;

    private VoiceLineEntry? PickFrom(VoiceEvent e, VoiceLineVoice voice, Func<double> random, bool avoidRecent)
    {
        var total = 0.0;
        foreach (var line in entries)
            if (Fits(line, e, voice) && !(avoidRecent && recent.Contains(line.VoiceId)))
                total += line.Weight;
        if (total <= 0)
            return null;
        var r = random() * total;
        VoiceLineEntry? last = null;
        foreach (var line in entries)
        {
            if (!Fits(line, e, voice) || avoidRecent && recent.Contains(line.VoiceId))
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
