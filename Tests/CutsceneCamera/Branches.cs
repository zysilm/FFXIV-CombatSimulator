namespace CutsceneCamera;

/// <summary>How the shots of one cutscene are organised: per timeline, which tracks hold camera shots and the Lua
/// condition (if any) that gates each track. The token text comes from the plugin's <c>TmbLua</c>.</summary>
public static class Branches
{
    public static string Text(List<(int Op, uint Value, float F)> toks) => TmbLua.Text(toks);

    public static string? ShotCondition(TmbTimeline tl, int shotEntryId)
    {
        var it = tl.ById[shotEntryId];
        return it.OwnerTrack >= 0 && tl.TrackLua.TryGetValue(it.OwnerTrack, out var toks) ? Text(toks) : null;
    }

    public static void Print(CutbFile c)
    {
        foreach (var tl in c.Timelines)
        {
            var shots = tl.Items.Where(i => i.Magic == "C004").ToList();
            var luaTracks = tl.TrackLua.Count;
            Console.WriteLine($"CTTL#{tl.Index} len {tl.Length} shots {shots.Count} lua-tracks {luaTracks}" +
                (tl.TrackLua.Count > 0 ? "  conditions: " + string.Join(" | ", tl.TrackLua.Values.Select(Text).Distinct().Take(4)) : ""));
            foreach (var g in shots.GroupBy(s => s.OwnerTrack))
            {
                var cond = g.Key >= 0 && tl.TrackLua.TryGetValue(g.Key, out var t) ? Text(t) : "(always)";
                Console.WriteLine($"   track {g.Key} actor {g.First().OwnerActor}: {cond} → " + string.Join(" ", g.Select(s => $"[{s.Time}+{s.Word(c.Data, 0)}]")));
            }
        }
    }
}
