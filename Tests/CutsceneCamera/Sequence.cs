namespace CutsceneCamera;

/// <summary>
/// How a timeline's camera shots combine into what is actually seen, and how many of them follow an actor.
/// Rule tested here: shots live on TMTR tracks (owned by one camera TMAC); a track may carry a Lua condition
/// (variants); C004 entries on no track are never referenced. Within the unconditional tracks a later track
/// overrides an earlier one while its shot is active ("last track wins"). The rule is checked by coverage:
/// if it is right, the resolved default sequence should cover the timeline's TMDH length with no gaps.
/// </summary>
internal static class Sequence
{
    // The rule itself lives in the plugin (CombatSimulator/Camera/Cutscene/CutsceneSequence.cs); this class keeps the statistics.
    public static CutsceneSequence.Resolved Resolve(CutbFile c, TmbTimeline tl, Func<string, bool>? acceptCondition = null)
        => CutsceneSequence.Resolve(c, tl, acceptCondition);

    public static bool Follows(CameraShot s) => s.Root.Actor != 255;

    public static void Run(IEnumerable<CutbFile> files, Bind.Body body)
    {
        int timelines = 0, full = 0, ge95 = 0, untracked = 0, tracked = 0, condTracks = 0, uncondTracks = 0;
        int oldTiled = 0;
        var condVars = new Dictionary<string, int>();
        var condValues = new Dictionary<string, SortedSet<int>>();
        int spanCount = 0, followSpans = 0, followAimRig = 0, followAimActor = 0, followAimOther = 0, aimOnly = 0, world = 0;
        int followFrames = 0, allFrames = 0, unknownPoints = 0, attachSlots = 0;
        var runs = new List<(int Shots, int Frames)>();
        int fullyFollowTimelines = 0;
        foreach (var c in files)
            foreach (var tl in c.Timelines)
            {
                var shots = CameraShot.FromTimeline(c, tl).ToList();
                if (shots.Count == 0) continue;
                timelines++;
                if (shots.Where(s => !s.IsOverlay).Sum(s => s.Duration) == tl.Length) oldTiled++;
                foreach (var it in tl.Items.Where(i => i.Magic == "C004"))
                {
                    if (it.OwnerTrack < 0) { untracked++; continue; }
                    tracked++;
                }
                foreach (var tid in tl.Items.Where(i => i.Magic == "C004" && i.OwnerTrack >= 0).Select(i => i.OwnerTrack).Distinct())
                {
                    if (tl.TrackLua.TryGetValue(tid, out var lua))
                    {
                        condTracks++;
                        var vars = lua.Where(t => t.Op == 0x13).Select(t => Branches.Text(new() { t })).Distinct();
                        foreach (var v in vars) condVars[v] = condVars.GetValueOrDefault(v) + 1;
                        for (var ti = 0; ti + 2 < lua.Count; ti++)
                            if (lua[ti].Op == 0x13 && (lua[ti + 1].Op is 0x0D or 0x0C) && lua[ti + 2].Op == 0x11)
                            {
                                var key = Branches.Text(new() { lua[ti] });
                                if (!condValues.TryGetValue(key, out var set)) condValues[key] = set = new SortedSet<int>();
                                set.Add((int)lua[ti + 2].Value);
                            }
                    }
                    else uncondTracks++;
                }
                var r = Resolve(c, tl);
                if (r.Covered >= tl.Length) full++;
                if (r.Covered >= tl.Length * 0.95) ge95++;
                var run = (0, 0);
                var allFollow = r.Spans.Count > 0;
                foreach (var (from, to, s) in r.Spans)
                {
                    spanCount++;
                    allFrames += to - from;
                    foreach (var a in new[] { s.Root, s.Eye, s.Aim }.Where(a => a.Actor != 255))
                    {
                        attachSlots++;
                        if (!body.Eid.ContainsKey(a.Point)) unknownPoints++;
                    }
                    if (Follows(s))
                    {
                        followSpans++; followFrames += to - from;
                        if (s.Aim.Actor == 255) followAimRig++;
                        else if (s.Aim.Actor == s.Root.Actor) followAimActor++;
                        else followAimOther++;
                        run = (run.Item1 + 1, run.Item2 + to - from);
                    }
                    else
                    {
                        allFollow = false;
                        if (s.Aim.Actor != 255) aimOnly++; else world++;
                        if (run.Item1 > 0) runs.Add(run);
                        run = (0, 0);
                    }
                }
                if (run.Item1 > 0) runs.Add(run);
                if (allFollow) fullyFollowTimelines++;
            }
        Console.WriteLine($"timelines with camera shots {timelines}; old 'sum of durations == length' test {oldTiled}");
        Console.WriteLine($"C004 on a track {tracked}, on no track (never referenced) {untracked}; camera tracks: unconditional {uncondTracks}, Lua-conditional {condTracks}");
        Console.WriteLine("variables in camera-track conditions: " + string.Join("  ", condVars.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}×{k.Value}")));
        Console.WriteLine("values compared against: " + string.Join("  ", condValues.OrderByDescending(k => condVars.GetValueOrDefault(k.Key)).Take(6).Select(k => $"{k.Key}={{{string.Join(",", k.Value.Take(20))}}}")));
        Console.WriteLine($"resolved default sequence (unconditional tracks, last track wins) covers the whole timeline: {full}/{timelines} ({full * 100f / timelines:F1}%), ≥95%: {ge95}");
        Console.WriteLine($"resolved shots {spanCount}: follow (root attached) {followSpans} ({followSpans * 100f / spanCount:F1}%) " +
                          $"[aim in the rig {followAimRig}, aim on the same actor {followAimActor}, aim on another actor {followAimOther}], " +
                          $"world camera tracking an actor (aim only) {aimOnly}, fixed world {world}");
        Console.WriteLine($"follow time {followFrames / 30f / 60f:F1} min of {allFrames / 30f / 60f:F1} min ({followFrames * 100f / Math.Max(1, allFrames):F1}%); attach slots whose point has no c0101 EID entry {unknownPoints}/{attachSlots}");
        Console.WriteLine($"timelines whose whole resolved sequence follows actors: {fullyFollowTimelines}");
        Console.WriteLine($"runs of consecutive follow shots: {runs.Count}; ≥3 shots {runs.Count(x => x.Shots >= 3)}, ≥5 s {runs.Count(x => x.Frames >= 150)}, ≥10 s {runs.Count(x => x.Frames >= 300)}; " +
                          $"median run {runs.Select(x => x.Frames).OrderBy(x => x).ElementAtOrDefault(runs.Count / 2) / 30f:F1} s");
    }
}
