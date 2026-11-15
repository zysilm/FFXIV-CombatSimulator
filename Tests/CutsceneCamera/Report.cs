using System.Numerics;

namespace CutsceneCamera;

internal static class Report
{
    /// <summary>Actor placements: C018 entries (world position, rotation in RADIANS, scale), owned by actor index.</summary>
    public static Dictionary<int, List<(int Timeline, int Time, Vector3 Pos, float Yaw)>> ActorPlacements(CutbFile c)
    {
        var res = new Dictionary<int, List<(int, int, Vector3, float)>>();
        foreach (var tl in c.Timelines)
            foreach (var it in tl.Items.Where(i => i.Magic == "C018" && i.OwnerActor >= 0))
            {
                var p = new Vector3(it.Float(c.Data, 2), it.Float(c.Data, 3), it.Float(c.Data, 4));
                if (p == Vector3.Zero) continue; // placeholder placements
                if (!res.TryGetValue(it.OwnerActor, out var l)) res[it.OwnerActor] = l = new();
                l.Add((tl.Index, it.Time, p, it.Float(c.Data, 6)));
            }
        return res;
    }

    /// <summary>Frame of an attach slot: the actor's C018 placement in that timeline (see Bind.ActorAt), the attach point
    /// resolved as an EID bind point on the Hyur reference body (Bind.DefaultBody, set by Program). Reference pose only:
    /// animation is not evaluated.</summary>
    internal static Func<Attach, Matrix4x4> Anchors(CutbFile c, int timeline, int time)
    {
        var pl = Bind.Placements(c);
        return a =>
        {
            if (a.Actor == 255 || Bind.ActorAt(pl, a.Actor, timeline, time) is not { } m) return Matrix4x4.Identity;
            return Bind.DefaultBody == null ? m : Bind.PointFrame(Bind.Mapping.EidBindPoint, Bind.DefaultBody, a.Point, m);
        };
    }

    public static void Print(CutbFile c, TextWriter w)
    {
        var centre = c.SceneCentre();
        w.WriteLine($"  chunks: " + string.Join(" ", c.Chunks.GroupBy(x => x.Tag).Select(g => $"{g.Key}×{g.Count()}")));
        if (centre is { } ce) w.WriteLine($"  CTDS centre ({ce.X:F2}, {ce.Y:F2}, {ce.Z:F2}) radius {ce.R}");
        var shots = c.Timelines.SelectMany(tl => CameraShot.FromTimeline(c, tl)).ToList();
        var main = shots.Where(s => !s.IsOverlay).ToList();
        var withShots = c.Timelines.Where(tl => shots.Any(s => s.Timeline == tl.Index)).ToList();
        w.WriteLine($"  timelines {c.Timelines.Count}, with camera shots {withShots.Count}; shots (cuts) {main.Count} + overlay layers {shots.Count - main.Count}");
        w.WriteLine($"  camera time {main.Sum(s => s.Duration)} frames = {main.Sum(s => s.Duration) / 30f:F1} s; " +
                    $"tiled timelines {withShots.Count(tl => main.Where(s => s.Timeline == tl.Index).Sum(s => s.Duration) == tl.Length)}/{withShots.Count}");
        var keys = shots.Where(s => s.Channels != null).SelectMany(s => s.Channels!).Sum(ch => ch.Keys.Count);
        var animatedShots = shots.Count(s => s.Channels?.Any(ch => ch.Animated) == true);
        w.WriteLine($"  curve keys {keys}; shots with moving camera {animatedShots}/{shots.Count}; " +
                    $"attached root {shots.Count(s => s.Root.Actor != 255)}, eye {shots.Count(s => s.Eye.Actor != 255)}, aim {shots.Count(s => s.Aim.Actor != 255)}");
        var fovs = shots.Where(s => s.Channels != null).SelectMany(s => s.Channel(9, 116, 255)!.Keys.Select(k => k.Value)).ToList();
        if (fovs.Count > 0) w.WriteLine($"  FOV keys {fovs.Min():F1}°..{fovs.Max():F1}°");

        // World-space path over all shots, sampled every 5 frames.
        var eyes = new List<Vector3>();
        float minDist = float.MaxValue, maxDist = 0f;
        foreach (var s in main.Where(s => s.Channels != null))
            for (var f = 0; f <= s.Duration; f += 5)
            {
                var e = s.Evaluate(f, Anchors(c, s.Timeline, s.Start + f));
                eyes.Add(e.Eye);
                var dist = Vector3.Distance(e.Eye, e.Aim);
                minDist = MathF.Min(minDist, dist); maxDist = MathF.Max(maxDist, dist);
            }
        if (eyes.Count > 0)
        {
            w.WriteLine($"  eye X {eyes.Min(e => e.X):F1}..{eyes.Max(e => e.X):F1}  Y {eyes.Min(e => e.Y):F1}..{eyes.Max(e => e.Y):F1}  Z {eyes.Min(e => e.Z):F1}..{eyes.Max(e => e.Z):F1}" +
                        $"   eye→aim distance {minDist:F2}..{maxDist:F2} m");
        }
        w.WriteLine("  first shots:");
        foreach (var s in main.Take(6))
        {
            s.Dump(w, verbose: false);
            if (s.Channels == null) continue;
            foreach (var f in new[] { 0, s.Duration / 2, s.Duration })
            {
                var e = s.Evaluate(f, Anchors(c, s.Timeline, s.Start + f));
                w.WriteLine($"     f{f,4}: eye ({e.Eye.X:F2},{e.Eye.Y:F2},{e.Eye.Z:F2}) aim ({e.Aim.X:F2},{e.Aim.Y:F2},{e.Aim.Z:F2}) roll {CameraShot.RollDegrees(e.Eye, e.Aim, e.Up):F1}° fov {e.Fov:F1}");
            }
        }
    }
}
