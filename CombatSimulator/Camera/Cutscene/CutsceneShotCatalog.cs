// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>One visible part of a camera shot inside a curated segment: C004 index among the timeline's C004
/// entries (file order) and the timeline frames [From, To) at 30 fps.</summary>
public readonly record struct CutsceneShotSpan(int Shot, int From, int To);

/// <summary>
/// One entry of the curated follow-shot list (<c>Resources/CutsceneShots.json</c>, docs/CUTSCENE_SHOTS.md §4): a reference
/// into one of the game's cutscenes plus our own derived features. The camera curves are read from the user's game at
/// run time (<see cref="CutsceneShotLoader"/>).
/// </summary>
public sealed class CutsceneShotEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>Cutscene sheet Path; the file is <c>cut/&lt;path&gt;.cutb</c>.</summary>
    public string Cutscene { get; init; } = "";
    public int Timeline { get; init; }
    public IReadOnlyList<CutsceneShotSpan> Spans { get; init; } = Array.Empty<CutsceneShotSpan>();
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public float Duration { get; init; }
    public bool TwoPerson { get; init; }
    /// <summary>Role bound to the followed cutscene actor (camera root): "subject" or "other".</summary>
    public string Follow { get; init; } = "subject";
    /// <summary>Role bound to the looked-at cutscene actor (two-person only): "subject" or "other".</summary>
    public string? LookAt { get; init; }
    /// <summary>Cutscene actor index of the camera root (the followed actor).</summary>
    public int FollowActor { get; init; }
    /// <summary>Cutscene actor index of the aim (two-person only).</summary>
    public int? LookActor { get; init; }
    public bool CorpseSuitable { get; init; }
    public IReadOnlyList<int> AttachPoints { get; init; } = Array.Empty<int>();

    /// <summary>Total frames of the segment (sum of its spans).</summary>
    public int Frames => Spans.Sum(s => s.To - s.From);

    /// <summary>Search: every whitespace-separated word of the filter must occur in the name or in one of the tags
    /// (case-insensitive). An empty filter matches everything.</summary>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        foreach (var word in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Name.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (Tags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            return false;
        }
        return true;
    }
}

/// <summary>The curated list, read from its JSON (version 1).</summary>
public sealed class CutsceneShotCatalog
{
    public IReadOnlyList<CutsceneShotEntry> Entries { get; }
    private readonly Dictionary<string, CutsceneShotEntry> byId;

    private CutsceneShotCatalog(List<CutsceneShotEntry> entries)
    {
        Entries = entries;
        byId = new Dictionary<string, CutsceneShotEntry>(StringComparer.Ordinal);
        foreach (var e in entries) byId.TryAdd(e.Id, e);
    }

    public static CutsceneShotCatalog Empty { get; } = new(new List<CutsceneShotEntry>());

    public CutsceneShotEntry? Find(string? id) => id != null && byId.TryGetValue(id, out var e) ? e : null;

    public static CutsceneShotCatalog Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static CutsceneShotCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    private static CutsceneShotCatalog Parse(JsonElement root)
    {
        if (root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("CutsceneShots.json: unsupported version");
        var list = new List<CutsceneShotEntry>();
        foreach (var e in root.GetProperty("shots").EnumerateArray())
        {
            var lookAt = e.GetProperty("lookAt");
            var lookActor = e.GetProperty("lookActor");
            list.Add(new CutsceneShotEntry
            {
                Id = e.GetProperty("id").GetString() ?? "",
                Name = e.GetProperty("name").GetString() ?? "",
                Category = e.GetProperty("category").GetString() ?? "",
                Tags = e.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "").ToArray(),
                Cutscene = e.GetProperty("cutscene").GetString() ?? "",
                Timeline = e.GetProperty("timeline").GetInt32(),
                Spans = e.GetProperty("shots").EnumerateArray()
                    .Select(s => new CutsceneShotSpan(s.GetProperty("shot").GetInt32(), s.GetProperty("from").GetInt32(), s.GetProperty("to").GetInt32()))
                    .ToArray(),
                StartFrame = e.GetProperty("startFrame").GetInt32(),
                EndFrame = e.GetProperty("endFrame").GetInt32(),
                Duration = e.GetProperty("duration").GetSingle(),
                TwoPerson = e.GetProperty("kind").GetString() == "two-person",
                Follow = e.GetProperty("follow").GetString() ?? "subject",
                LookAt = lookAt.ValueKind == JsonValueKind.String ? lookAt.GetString() : null,
                FollowActor = e.GetProperty("followActor").GetInt32(),
                LookActor = lookActor.ValueKind == JsonValueKind.Number ? lookActor.GetInt32() : null,
                CorpseSuitable = e.GetProperty("corpseSuitable").GetBoolean(),
                AttachPoints = e.GetProperty("attachPoints").EnumerateArray().Select(p => p.GetInt32()).ToArray(),
            });
        }
        return new CutsceneShotCatalog(list);
    }
}
