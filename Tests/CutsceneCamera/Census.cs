using Lumina;
using Lumina.Excel.Sheets;

namespace CutsceneCamera;

/// <summary>Statistics over many cutscenes: which rig channels exist, which ones ever animate, value ranges,
/// how the shot entries (C004) tile their timelines. This is how the channel meanings were pinned down.</summary>
public static class Census
{
    private sealed class Stat
    {
        public int Seen, Animated;
        public float Min = float.MaxValue, Max = float.MinValue;
        public void Add(CurveChannel c)
        {
            Seen++;
            if (c.Animated) Animated++;
            foreach (var k in c.Keys) { Min = MathF.Min(Min, k.Value); Max = MathF.Max(Max, k.Value); }
        }
    }

    public static void Run(GameData gd, int max, Func<string, CutbFile?> load)
    {
        var rows = gd.GetExcelSheet<Cutscene>()!.Select(r => r.Path.ToString()).Where(p => p.Length > 0).ToList();
        // Spread the sample over the whole sheet (all expansions), not just the first rows.
        var step = Math.Max(1, rows.Count / max);
        var stats = new SortedDictionary<string, Stat>();
        var sizes = new Dictionary<int, int>();
        var layouts = new Dictionary<string, int>();
        var keyFlags = new Dictionary<int, int>();
        int files = 0, failed = 0, shots = 0, timelinesWithShots = 0, tiled = 0, attachedRoot = 0, attachedAim = 0, attachedEye = 0;
        var durations = new List<int>();
        var fovs = new List<float>();
        var rolls = new List<float>();
        var points = new Dictionary<int, int>();
        var degenerate = 0;
        int flag1 = 0, flag1Flat = 0, flag1Changes = 0;
        for (var i = 0; i < rows.Count; i += step)
        {
            CutbFile? c;
            try { c = load(rows[i]); }
            catch { failed++; continue; }
            if (c == null) { failed++; continue; }
            files++;
            foreach (var tl in c.Timelines)
            {
                var list = CameraShot.FromTimeline(c, tl).ToList();
                if (list.Count == 0) continue;
                timelinesWithShots++;
                // Shots on the main layer tile the timeline: sum of durations == TMDH length.
                var main = list.Where(s => !s.IsOverlay).ToList();
                if (main.Sum(s => s.Duration) == tl.Length) tiled++;
                foreach (var s in list)
                {
                    shots++;
                    durations.Add(s.Duration);
                    sizes[s.EntrySize] = sizes.GetValueOrDefault(s.EntrySize) + 1;
                    foreach (var at in new[] { s.Root, s.Eye, s.Aim }) if (at.Actor != 255) points[at.Point] = points.GetValueOrDefault(at.Point) + 1;
                    if (s.Root.Actor != 255) attachedRoot++;
                    if (s.Eye.Actor != 255) attachedEye++;
                    if (s.Aim.Actor != 255) attachedAim++;
                    if (s.Channels == null) continue;
                    var sig = string.Join(",", s.Channels.Select(ch => $"{ch.Group}:{ch.Code}@{ch.Node}<{ch.Parent}"));
                    layouts[sig] = layouts.GetValueOrDefault(sig) + 1;
                    foreach (var ch in s.Channels)
                    {
                        var key = $"g{ch.Group} c{ch.Code,3} node{ch.Node,3} parent{ch.Parent,2}";
                        if (!stats.TryGetValue(key, out var st)) stats[key] = st = new Stat();
                        st.Add(ch);
                        for (var ki = 0; ki < ch.Keys.Count; ki++)
                        {
                            var k = ch.Keys[ki];
                            keyFlags[k.Flags] = keyFlags.GetValueOrDefault(k.Flags) + 1;
                            if (k.Flags == 1) { flag1++; if (k.TanIn == 0f && k.TanOut == 0f) flag1Flat++; if (ki + 1 < ch.Keys.Count && ch.Keys[ki + 1].Value != k.Value) flag1Changes++; }
                        }
                    }
                    fovs.Add(s.Fov(0));
                    var ev = s.Evaluate(0, _ => System.Numerics.Matrix4x4.Identity);
                    if (System.Numerics.Vector3.Distance(ev.Eye, ev.Aim) < 0.01f) degenerate++;
                    rolls.Add(CameraShot.RollDegrees(ev.Eye, ev.Aim, ev.Up));
                }
            }
        }

        Console.WriteLine($"cutscene files read {files}, missing/failed {failed}; timelines with camera shots {timelinesWithShots}, " +
                          $"of which main-layer shots tile the TMDH length exactly: {tiled}");
        Console.WriteLine($"camera shots (C004) {shots}; entry sizes: " + string.Join(" ", sizes.OrderBy(k => k.Key).Select(k => $"{k.Key}B×{k.Value}")));
        Console.WriteLine($"attach slots used: root {attachedRoot}, eye {attachedEye}, aim {attachedAim}; attach point values: " + string.Join(" ", points.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key}×{k.Value}")));
        durations.Sort();
        if (durations.Count > 0)
            Console.WriteLine($"shot length frames (30fps): p10 {durations[durations.Count / 10]}, median {durations[durations.Count / 2]}, p90 {durations[durations.Count * 9 / 10]}, max {durations[^1]}");
        fovs.Sort();
        if (fovs.Count > 0)
            Console.WriteLine($"FOV channel (g9 c116) at shot start: min {fovs[0]:F1}, p10 {fovs[fovs.Count / 10]:F1}, median {fovs[fovs.Count / 2]:F1}, p90 {fovs[fovs.Count * 9 / 10]:F1}, max {fovs[^1]:F1}");
        rolls.Sort();
        if (rolls.Count > 0) Console.WriteLine($"roll from up-object at shot start: |roll|>1°: {rolls.Count(r => MathF.Abs(r) > 1f)}, >5°: {rolls.Count(r => MathF.Abs(r) > 5f)} of {rolls.Count}; min {rolls[0]:F1} max {rolls[^1]:F1}; eye==aim (degenerate) {degenerate}");
        Console.WriteLine("key flag values: " + string.Join(" ", keyFlags.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}×{k.Value}")) + $"; flag-1 keys with zero tangents {flag1Flat}/{flag1}, followed by a different value {flag1Changes}");
        Console.WriteLine($"distinct rig layouts {layouts.Count}; top:");
        foreach (var kv in layouts.OrderByDescending(k => k.Value).Take(3))
            Console.WriteLine($"  ×{kv.Value}: {kv.Key}");
        Console.WriteLine("channel                         seen  animated        min        max");
        foreach (var kv in stats)
            Console.WriteLine($"  {kv.Key,-28} {kv.Value.Seen,6} {kv.Value.Animated,8} {kv.Value.Min,10:G5} {kv.Value.Max,10:G5}");
    }
}
