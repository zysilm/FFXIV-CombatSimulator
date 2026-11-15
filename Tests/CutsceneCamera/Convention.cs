using System.Numerics;

namespace CutsceneCamera;

/// <summary>Decides the Euler order/sign of the rig rotation channels from the data itself: the right convention
/// is the one whose look direction points at the cutscene's own actors. Only shots whose camera is NOT attached
/// to an actor and whose root rotation is non-zero take part (otherwise every convention gives the same answer).</summary>
public static class Convention
{
    public static void Run(IEnumerable<CutbFile> files)
    {
        var cands = new List<(EulerOrder, float)>();
        foreach (EulerOrder o in Enum.GetValues<EulerOrder>()) { cands.Add((o, 1f)); cands.Add((o, -1f)); }
        var angles = cands.ToDictionary(c => c, _ => new List<float>());
        var used = 0;
        foreach (var c in files)
        {
            var placements = Report.ActorPlacements(c);
            foreach (var tl in c.Timelines)
            {
                var actors = placements.Values.SelectMany(l => l.Where(x => x.Timeline == tl.Index || x.Timeline == 0).Select(x => x.Pos)).ToList();
                if (actors.Count == 0) continue;
                foreach (var s in CameraShot.FromTimeline(c, tl).Where(s => !s.IsOverlay && s.Channels != null && s.Root.Actor == 255 && s.Aim.Actor == 255))
                {
                    var rot = new[] { 67, 68, 69 }.Select(code => s.Channel(8, code, 1)?.Eval(0) ?? 0f).ToArray();
                    if (MathF.Abs(rot[0]) + MathF.Abs(rot[1]) < 5f) continue;
                    used++;
                    foreach (var cand in cands)
                    {
                        CameraShot.Order = cand.Item1; CameraShot.RotationSign = cand.Item2;
                        var e = s.Evaluate(0, _ => Matrix4x4.Identity);
                        var f = e.Aim - e.Eye;
                        if (f.LengthSquared() < 1e-6f) continue;
                        f = Vector3.Normalize(f);
                        var best = 180f;
                        foreach (var a in actors)
                        {
                            var to = a + new Vector3(0, 1.3f, 0) - e.Eye;
                            if (to.Length() > 40f || to.Length() < 0.2f) continue;
                            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(f, Vector3.Normalize(to)), -1f, 1f)) * 180f / MathF.PI;
                            best = MathF.Min(best, ang);
                        }
                        angles[cand].Add(best);
                    }
                }
            }
        }
        CameraShot.Order = EulerOrder.XYZ; CameraShot.RotationSign = 1f;
        Console.WriteLine($"convention test on {used} unattached, rotated shots: angle from look direction to the nearest actor (head height)");
        foreach (var kv in angles.OrderBy(k => Median(k.Value)))
            Console.WriteLine($"  {kv.Key.Item1} sign {kv.Key.Item2,2}: median {Median(kv.Value),6:F1}°  within 15°: {kv.Value.Count(a => a < 15f) * 100f / Math.Max(1, kv.Value.Count),5:F1}%");
    }

    private static float Median(List<float> v) { if (v.Count == 0) return float.NaN; var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
