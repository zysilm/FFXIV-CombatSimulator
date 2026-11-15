using System.Numerics;

namespace CutsceneCamera;

/// <summary>Statistics for the lens-side channels (g9 c116..c121), the g10 node and key flag 1.</summary>
internal static class Lens
{
    private static string Pct(List<float> v)
    {
        if (v.Count == 0) return "n/a";
        var s = v.OrderBy(x => x).ToList();
        return $"n={s.Count} min {s[0]:G4} p10 {s[s.Count / 10]:G4} p50 {s[s.Count / 2]:G4} p90 {s[s.Count * 9 / 10]:G4} max {s[^1]:G4}";
    }

    private static float Corr(List<(float A, float B)> xy)
    {
        if (xy.Count < 3) return float.NaN;
        double ma = xy.Average(p => p.A), mb = xy.Average(p => p.B), sab = 0, saa = 0, sbb = 0;
        foreach (var (a, b) in xy) { sab += (a - ma) * (b - mb); saa += (a - ma) * (a - ma); sbb += (b - mb) * (b - mb); }
        return (float)(sab / Math.Sqrt(saa * sbb + 1e-12));
    }

    public static void Run(IEnumerable<CutbFile> files, Bind.Body body)
    {
        var c117 = new List<float>(); int c117Shots = 0, c117Nonzero = 0, c117NonzeroLevelUp = 0;
        var c117VsUpRoll = new List<(float, float)>();
        var c118 = new List<float>(); var c119 = new List<float>(); var c120 = new List<float>(); var c121 = new List<float>();
        var focus = new List<(float A, float B)>(); var focusRatio = new List<float>();
        var g10 = new List<float>(); var g10VsDist = new List<(float, float)>();
        var fracV = new List<float>(); var fracH = new List<float>();
        int flag1 = 0, flag1Last = 0, flag1First = 0, flag1Mid = 0, flag1Linearish = 0, flag1Segs = 0, flag0Segs = 0, flag0Linearish = 0;
        foreach (var c in files)
        {
            var pl = Bind.Placements(c);
            foreach (var tl in c.Timelines)
                foreach (var s in CameraShot.FromTimeline(c, tl).Where(s => s.Channels != null))
                {
                    var mid = s.Duration / 2;
                    var e = s.Evaluate(mid, a => a.Actor == 255 || Bind.ActorAt(pl, a.Actor, tl.Index, s.Start) is not { } am
                        ? Matrix4x4.Identity : Bind.PointFrame(Bind.Mapping.EidBindPoint, body, a.Point, am));
                    var dist = Vector3.Distance(e.Eye, e.Aim);
                    var upRoll = CameraShot.RollDegrees(e.Eye, e.Aim, e.Up);
                    var v117 = s.Channel(9, 117, 255)?.Eval(mid);
                    if (v117 is { } r)
                    {
                        c117Shots++;
                        if (MathF.Abs(r) > 0.01f) { c117Nonzero++; c117.Add(r); c117VsUpRoll.Add((r, upRoll)); if (MathF.Abs(upRoll) < 0.5f) c117NonzeroLevelUp++; }
                    }
                    if (s.Channel(9, 118, 255) is { } a118) c118.Add(a118.Eval(mid));
                    if (s.Channel(9, 119, 255) is { } a119) c119.Add(a119.Eval(mid));
                    if (s.Channel(9, 120, 255) is { } a120) c120.Add(a120.Eval(mid));
                    if (s.Channel(9, 121, 255) is { } a121)
                    {
                        var v = a121.Eval(mid); c121.Add(v);
                        if (v > 0.11f && dist > 0.2f && dist < 200f) { focus.Add((v, dist)); focusRatio.Add(v / dist); }
                    }
                    var g = s.Channels!.Where(ch => ch.Group == 10 && ch.Code == 66).FirstOrDefault();
                    if (g != null) { var gv = g.Eval(mid); if (MathF.Abs(gv) > 1e-3f) { g10.Add(gv); g10VsDist.Add((gv, dist)); } }

                    // Close-up test: root on an actor, aim within 0.6 m of that actor's head.
                    if (s.Root.Actor != 255 && Bind.ActorAt(pl, s.Root.Actor, tl.Index, s.Start) is { } rm)
                    {
                        var head = Vector3.Transform(body.Head, rm);
                        if (Vector3.Distance(head, e.Aim) < 0.6f && dist > 0.3f && e.Fov is > 1 and < 120)
                        {
                            var tan = MathF.Tan(e.Fov * MathF.PI / 360f);
                            var f = 0.25f / (2 * dist * tan);           // c116 = vertical FOV
                            fracV.Add(f);
                            fracH.Add(f * 16f / 9f);                    // c116 = horizontal FOV on a 16:9 frame
                        }
                    }
                    foreach (var ch in s.Channels!)
                        for (var k = 0; k < ch.Keys.Count; k++)
                        {
                            var key = ch.Keys[k];
                            var isSeg = k + 1 < ch.Keys.Count && MathF.Abs(ch.Keys[k + 1].Value - key.Value) > 1e-3f;
                            var lin = false;
                            if (isSeg)
                            {
                                var slope = (ch.Keys[k + 1].Value - key.Value) / MathF.Max(1e-3f, ch.Keys[k + 1].Frame - key.Frame);
                                lin = MathF.Abs(key.TanOut - slope) < MathF.Abs(slope) * 0.05f;
                            }
                            if (key.Flags == 1)
                            {
                                flag1++;
                                if (k == ch.Keys.Count - 1) flag1Last++; else if (k == 0) flag1First++; else flag1Mid++;
                                if (isSeg) { flag1Segs++; if (lin) flag1Linearish++; }
                            }
                            else if (isSeg) { flag0Segs++; if (lin) flag0Linearish++; }
                        }
                }
        }
        Console.WriteLine($"c117: present in {c117Shots} shots, non-zero in {c117Nonzero}: {Pct(c117)}; |c117|: {Pct(c117.Select(MathF.Abs).ToList())}");
        Console.WriteLine($"      non-zero c117 shots whose up-object roll is level (<0.5°): {c117NonzeroLevelUp}/{c117Nonzero}; corr(c117, up-roll) {Corr(c117VsUpRoll):F2}");
        Console.WriteLine($"c118: {Pct(c118)}");
        Console.WriteLine($"c119: {Pct(c119)}  distinct: {string.Join(",", c119.Select(v => MathF.Round(v, 2)).Distinct().Take(8))}");
        Console.WriteLine($"c120: {Pct(c120)}");
        Console.WriteLine($"c121: {Pct(c121)}");
        Console.WriteLine($"      c121 vs eye→aim distance (c121>0.11): corr {Corr(focus):F2}, ratio c121/dist {Pct(focusRatio)}");
        Console.WriteLine($"g10 c66 (non-zero): {Pct(g10)}; corr with eye→aim distance {Corr(g10VsDist):F2}");
        Console.WriteLine($"close-ups (aim on the attached actor's head): head height as a fraction of frame height");
        Console.WriteLine($"      if c116 is vertical FOV:   {Pct(fracV)}; >100% of frame: {fracV.Count(f => f > 1f) * 100f / Math.Max(1, fracV.Count):F1}%");
        Console.WriteLine($"      if c116 is horizontal FOV: {Pct(fracH)}; >100% of frame: {fracH.Count(f => f > 1f) * 100f / Math.Max(1, fracH.Count):F1}%");
        Console.WriteLine($"key flag 1: {flag1} keys (first {flag1First}, middle {flag1Mid}, last {flag1Last}); segments leaving a flag-1 key whose tangent equals the straight slope: {flag1Linearish}/{flag1Segs}; same for flag-0 keys: {flag0Linearish}/{flag0Segs}");
    }
}
