// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Voice;

/// <summary>Which family of game sound files an actor's voice comes from.</summary>
public enum VoiceSourceKind
{
    None,
    /// <summary>Player-character battle shouts (vo_battle_pc_{race}_{m|f}{a|b|c}).</summary>
    PcShout,
    /// <summary>Generic NPC battle voices (vo_npc{nn}_battle).</summary>
    NpcBattle,
    /// <summary>Monster sound-effect pack (sound/battle/mon/{sepack}.scd). Language independent.</summary>
    MonsterSePack,
    /// <summary>A voiced NpcYell line (sound/voice/vo_line/{8200000+id}). Audition only in phase 1.</summary>
    YellLine,
}

/// <summary>
/// A resolved voice: the file family plus the one parameter that picks the file inside it.
/// The label key groups sources that share an entry layout, so one set of owner-recorded
/// labels ("entry 3 is the death cry") applies to every file of that layout.
/// </summary>
public readonly record struct VoiceSource(VoiceSourceKind Kind, string Code, uint Number)
{
    public static readonly VoiceSource None = new(VoiceSourceKind.None, string.Empty, 0);

    public bool IsNone => Kind == VoiceSourceKind.None;

    /// <summary>
    /// PC shouts all share one entry layout (5 entries) and NPC battle files another (3 entries),
    /// so they are labeled once per family. Each monster pack has its own layout.
    /// </summary>
    public string LabelKey => Kind switch
    {
        VoiceSourceKind.PcShout => "pc",
        VoiceSourceKind.NpcBattle => "npc",
        VoiceSourceKind.MonsterSePack => $"mon/{Number}",
        VoiceSourceKind.YellLine => $"line/{Number}",
        _ => string.Empty,
    };

    public bool IsLanguageSpecific => Kind is VoiceSourceKind.PcShout or VoiceSourceKind.NpcBattle or VoiceSourceKind.YellLine;

    /// <summary>Game path for one language (ignored for monster packs). Lower case: sqpack hashes
    /// lower-cased paths, and the audition panel shows these verbatim.</summary>
    public string PathFor(string language) => Kind switch
    {
        VoiceSourceKind.PcShout => $"sound/voice/vo_battle/vo_battle_pc_{Code}_{language}.scd",
        VoiceSourceKind.NpcBattle => $"sound/voice/vo_battle/vo_npc{Number:D2}_battle_{language}.scd",
        VoiceSourceKind.MonsterSePack => $"sound/battle/mon/{Number}.scd",
        VoiceSourceKind.YellLine => $"sound/voice/vo_line/{VoiceSelection.YellLineBase + Number:D7}_{language}.scd",
        _ => string.Empty,
    };

    public override string ToString() => Kind switch
    {
        VoiceSourceKind.PcShout => $"PC shout {Code}",
        VoiceSourceKind.NpcBattle => $"NPC voice {Number:D2}",
        VoiceSourceKind.MonsterSePack => $"Monster SE pack {Number}",
        VoiceSourceKind.YellLine => $"NpcYell line {Number}",
        _ => "none",
    };
}

/// <summary>
/// Pure mapping from actor appearance/identity to a game voice file. No game state: everything
/// here is checked offline by Tests/Voice. The tables were read from the game data (CharaMakeType,
/// Race/Tribe and a FileExists sweep of vo_battle); see docs/acceptance/voice.md.
/// </summary>
public static class VoiceSelection
{
    public const uint YellLineBase = 8200000;
    public const int NpcBattleVoiceCount = 84;
    public const int PcShoutEntryCount = 5;
    public const int NpcBattleEntryCount = 3;

    /// <summary>Every client language that ships these files, in fallback order after the
    /// client's own language. English first: it is the most complete voice set.</summary>
    public static readonly string[] FallbackLanguages = { "en", "ja" };

    // Customize race id (1-based, Race sheet) -> shout race code. Hyur splits by tribe.
    // Race 7 (Hrothgar) files are "ros"; there is no "hro" set.
    private static string? RaceCode(byte race, byte tribe) => race switch
    {
        1 => tribe == 2 ? "hil" : "mid",
        2 => "ele",
        3 => "lal",
        4 => "miq",
        5 => "rog",
        6 => "aur",
        7 => "ros",
        8 => "vie",
        _ => null,
    };

    /// <summary>The newer races ship three shout variants (a/b/c); the original six ship two.</summary>
    public static int ShoutVariantCount(byte race) => race is 6 or 7 or 8 ? 3 : 2;

    /// <summary>
    /// PC battle shout from appearance. The voice the player picked at character creation is not
    /// part of Customize and is not exposed by ClientStructs, so the a/b(/c) variant is chosen
    /// from a stable seed instead (same seed, same voice): race and gender are always right,
    /// only the timbre variant is a guess.
    /// </summary>
    public static VoiceSource PcShoutFromCustomize(byte race, byte tribe, byte sex, uint seed)
    {
        var code = RaceCode(race, tribe);
        if (code == null || sex > 1)
            return VoiceSource.None;
        var variant = (char)('a' + (int)(seed % (uint)ShoutVariantCount(race)));
        return new VoiceSource(VoiceSourceKind.PcShout, $"{code}_{(sex == 0 ? 'm' : 'f')}{variant}", 0);
    }

    // Voice ids 1..96 are 12 blocks of 8, one per original race/gender, in this order (from
    // CharaMakeType.VoiceStruct: each race lists its own 8 plus 4 borrowed from a related block).
    private static readonly (string Code, char Gender)[] LegacyVoiceBlocks =
    {
        ("mid", 'm'), ("mid", 'f'), ("lal", 'm'), ("lal", 'f'), ("ele", 'm'), ("ele", 'f'),
        ("hil", 'm'), ("hil", 'f'), ("miq", 'm'), ("miq", 'f'), ("rog", 'm'), ("rog", 'f'),
    };

    // Ids 97..168: blocks of 12 for Au Ra, Hrothgar and Viera, male then female.
    private static readonly string[] NewVoiceBlocks = { "aur", "ros", "vie" };

    /// <summary>
    /// PC battle shout from a character-creation voice id (1..168). Not used yet — the voice id
    /// cannot be read from a live actor — but kept so a future voice read plugs straight in.
    /// The variant assumes the shout files alternate a/b(/c) the same way the voice list does;
    /// UNVERIFIED, listed in the audition checklist.
    /// </summary>
    public static VoiceSource PcShoutFromVoiceId(int voiceId)
    {
        if (voiceId >= 1 && voiceId <= 96)
        {
            var block = (voiceId - 1) / 8;
            var (code, gender) = LegacyVoiceBlocks[block];
            var variant = (char)('a' + (voiceId - 1) % 8 % 2);
            return new VoiceSource(VoiceSourceKind.PcShout, $"{code}_{gender}{variant}", 0);
        }
        if (voiceId >= 97 && voiceId <= 168)
        {
            var offset = voiceId - 97;
            var code = NewVoiceBlocks[offset / 24];
            var gender = offset % 24 < 12 ? 'm' : 'f';
            var variant = (char)('a' + offset % 12 % 3);
            return new VoiceSource(VoiceSourceKind.PcShout, $"{code}_{gender}{variant}", 0);
        }
        return VoiceSource.None;
    }

    /// <summary>Generic NPC battle voice for a human enemy that is not a player clone. The same
    /// BNpcBase always gets the same voice, so a squad of one soldier type sounds consistent.</summary>
    public static VoiceSource NpcBattleFromId(uint stableId)
    {
        if (stableId == 0)
            return VoiceSource.None;
        var nn = (uint)(StableHash(stableId) % NpcBattleVoiceCount) + 1;
        return new VoiceSource(VoiceSourceKind.NpcBattle, string.Empty, nn);
    }

    public static VoiceSource MonsterFromSePack(uint sePack) =>
        sePack == 0 ? VoiceSource.None : new VoiceSource(VoiceSourceKind.MonsterSePack, string.Empty, sePack);

    /// <summary>
    /// All 42 PC shout combinations (for the audition panel). Only Hyur splits into two codes;
    /// every other race has one code for both tribes.
    /// </summary>
    public static IReadOnlyList<string> AllPcShoutCodes()
    {
        var codes = new List<string>();
        foreach (var (code, race) in new[] { ("mid", 1), ("hil", 1), ("ele", 2), ("lal", 3), ("miq", 4), ("rog", 5), ("aur", 6), ("ros", 7), ("vie", 8) })
            foreach (var gender in "mf")
                for (var v = 0; v < ShoutVariantCount((byte)race); v++)
                    codes.Add($"{code}_{gender}{(char)('a' + v)}");
        return codes;
    }

    /// <summary>Languages to try, client language first, without duplicates.</summary>
    public static IEnumerable<string> LanguageOrder(string clientLanguage)
    {
        if (!string.IsNullOrEmpty(clientLanguage))
            yield return clientLanguage;
        foreach (var lang in FallbackLanguages)
            if (lang != clientLanguage)
                yield return lang;
    }

    /// <summary>
    /// First path that exists, client language first. Null means "skip": a missing file is never
    /// handed to the native player.
    /// </summary>
    public static string? ResolvePath(VoiceSource source, string clientLanguage, Func<string, bool> fileExists)
    {
        if (source.IsNone)
            return null;
        if (!source.IsLanguageSpecific)
        {
            var path = source.PathFor(string.Empty);
            return fileExists(path) ? path : null;
        }
        foreach (var lang in LanguageOrder(clientLanguage))
        {
            var path = source.PathFor(lang);
            if (fileExists(path))
                return path;
        }
        return null;
    }

    /// <summary>Deterministic integer mix (no per-process randomization, unlike string.GetHashCode),
    /// so an enemy keeps its voice across sessions.</summary>
    public static uint StableHash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return value;
        }
    }

    public static uint StableHash(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var b in bytes)
                h = (h ^ b) * 16777619u;
            return StableHash(h);
        }
    }
}
