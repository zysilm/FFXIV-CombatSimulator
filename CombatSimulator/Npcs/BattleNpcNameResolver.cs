// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CombatSimulator.Npcs;

internal readonly record struct BattleNpcName(uint NameId, string Name);

/// <summary>Only explicit source pairs can name a battle appearance. No degree/ID guessing.</summary>
internal static class BattleNpcNameResolver
{
    internal const string ResourceName = "CombatSimulator.Npcs.Data.BNpcLinkNoGubal.csv";

    public static IReadOnlyDictionary<uint, uint[]> ReadLinks(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, leaveOpen: true);
        if (reader.ReadLine()?.Trim() != "BNpcNameId,BNpcBaseId")
            throw new InvalidDataException("Battle NPC name-link CSV header is missing or invalid.");
        var pairs = new Dictionary<uint, SortedSet<uint>>();
        int line = 1;
        while (reader.ReadLine() is { } text)
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var columns = text.Split(',');
            if (columns.Length != 2 ||
                !uint.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var name) || name == 0 ||
                !uint.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var appearance) || appearance == 0)
                throw new InvalidDataException($"Invalid battle NPC name-link CSV row {line}.");
            if (!pairs.TryGetValue(appearance, out var names)) pairs[appearance] = names = new();
            names.Add(name);
        }
        if (pairs.Count == 0) throw new InvalidDataException("Battle NPC name-link CSV has no pairs.");
        var result = new Dictionary<uint, uint[]>(pairs.Count);
        foreach (var (appearance, names) in pairs)
        {
            var values = new uint[names.Count]; names.CopyTo(values);
            result[appearance] = values;
        }
        return result;
    }

    public static IReadOnlyList<BattleNpcName> Resolve(uint appearance,
        IReadOnlyDictionary<uint, uint[]> links, IReadOnlyDictionary<uint, string> localizedNames)
    {
        var result = new List<BattleNpcName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (links.TryGetValue(appearance, out var names))
            foreach (var id in names)
                if (id != 0 && localizedNames.TryGetValue(id, out var name) &&
                    !string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(new(id, name));
        if (result.Count == 0) result.Add(new(0, $"Battle NPC {appearance}"));
        return result;
    }
}
