// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>A curated segment with its camera shots read from the game: one entry per span, in play order.</summary>
public sealed class LoadedCutsceneShot
{
    public required CutsceneShotEntry Entry { get; init; }
    public required IReadOnlyList<(CutsceneShotSpan Span, CameraShot Shot)> Spans { get; init; }
    /// <summary>Total frames (30 fps).</summary>
    public int Frames => Spans.Sum(s => s.Span.To - s.Span.From);
    public float Seconds => Frames / CutscenePlayback.Fps;
}

/// <summary>
/// Reads what the curated list refers to from the user's own game install, through a file-loader delegate (the plugin
/// passes Dalamud's data manager; the offline tool passes Lumina): the cutscene's <c>.cutb</c> (camera shots) and the
/// races' bind-point tables (<c>.eid</c>). Nothing read here is stored anywhere but memory. Game-free.
/// </summary>
public sealed class CutsceneShotLoader
{
    /// <summary>Hyur midlander male, the reference body the curated list was measured with and the fallback table.</summary>
    public const ushort ReferenceRace = 101;

    private readonly Func<string, byte[]?> loadFile;
    private readonly Dictionary<string, LoadedCutsceneShot> shots = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, IReadOnlyDictionary<int, BindPoint>?> eids = new();

    public CutsceneShotLoader(Func<string, byte[]?> loadFile) => this.loadFile = loadFile;

    public static string CutbPath(string cutscene) => $"cut/{cutscene}.cutb";
    public static string EidPath(ushort raceSexId) => $"chara/human/c{raceSexId:D4}/skeleton/base/b0001/eid_c{raceSexId:D4}b0001.eid";

    /// <summary>The segment's shots, read once and cached; null with a reason when the game data does not match the list.</summary>
    public LoadedCutsceneShot? Load(CutsceneShotEntry entry, out string? error)
    {
        error = null;
        if (shots.TryGetValue(entry.Id, out var cached)) return cached;
        var data = loadFile(CutbPath(entry.Cutscene));
        if (data == null) { error = $"{CutbPath(entry.Cutscene)} not found in the game data"; return null; }
        CutbFile file;
        try { file = new CutbFile(data); }
        catch (Exception ex) { error = $"{CutbPath(entry.Cutscene)}: {ex.Message}"; return null; }
        if (entry.Timeline < 0 || entry.Timeline >= file.Timelines.Count) { error = $"timeline {entry.Timeline} missing ({file.Timelines.Count} in the file)"; return null; }
        var all = CameraShot.FromTimeline(file, file.Timelines[entry.Timeline]).ToList();
        var spans = new List<(CutsceneShotSpan, CameraShot)>();
        foreach (var span in entry.Spans)
        {
            if (span.Shot < 0 || span.Shot >= all.Count) { error = $"shot {span.Shot} missing ({all.Count} in timeline {entry.Timeline})"; return null; }
            var s = all[span.Shot];
            if (s.Channels == null) { error = $"shot {span.Shot} has no curves"; return null; }
            if (s.Root.Actor != entry.FollowActor) { error = $"shot {span.Shot} is attached to actor {s.Root.Actor}, the list says {entry.FollowActor} (game data changed?)"; return null; }
            if (span.To <= span.From) { error = $"shot {span.Shot} has an empty span"; return null; }
            spans.Add((span, s));
        }
        if (spans.Count == 0) { error = "no shots"; return null; }
        var loaded = new LoadedCutsceneShot { Entry = entry, Spans = spans };
        shots[entry.Id] = loaded;
        return loaded;
    }

    /// <summary>Bind points of a human race (RaceSexId, e.g. 101 = c0101), read once; the reference race when the
    /// race has no table of its own; null only when not even that can be read.</summary>
    public IReadOnlyDictionary<int, BindPoint>? BindPoints(ushort raceSexId)
    {
        if (raceSexId == 0) raceSexId = ReferenceRace;
        if (eids.TryGetValue(raceSexId, out var cached)) return cached ?? (raceSexId != ReferenceRace ? BindPoints(ReferenceRace) : null);
        IReadOnlyDictionary<int, BindPoint>? table = null;
        try
        {
            var data = loadFile(EidPath(raceSexId));
            if (data != null)
                table = Eid.Read(data).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        }
        catch
        {
            table = null;
        }
        eids[raceSexId] = table;
        return table ?? (raceSexId != ReferenceRace ? BindPoints(ReferenceRace) : null);
    }
}
