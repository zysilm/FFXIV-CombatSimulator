using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CutsceneCamera;

/// <summary>
/// Builds the curated list of FOLLOW shots (camera root attached to a character) for the plugin's Camera tab:
/// victory and death shots picked by name from a dropdown. Everything here is computed from the numbers — no manual
/// viewing. Output: references (cutscene path, timeline, shot indices, frame range) plus our own derived features.
/// No curves or keyframes leave the user's game install; the plugin reads them at runtime.
///
/// Pipeline: every cutscene in the Cutscene sheet → default sequence of every timeline (unconditional tracks, last
/// track wins; <see cref="Sequence.Resolve"/>) → runs of consecutive shots whose root is attached to the same actor
/// and that look at the same thing → windows of 3–15 s → features sampled every 2 frames in the followed actor's own
/// frame (Hyur reference body, reference pose, C018 placements) → quality filters → per-class selection → names.
/// Deterministic: sorted input, ordinal tie-breaks, no clocks, invariant formatting.
/// </summary>
internal static class Curate
{
    private const int Fps = 30;
    private const int MinFrames = 3 * Fps, MaxFrames = 15 * Fps, WindowFrames = 12 * Fps;
    private const int Step = 2;

    // ---- data -------------------------------------------------------------------------------------------------

    private sealed record Span(CameraShot Shot, int From, int To) { public int Frames => To - From; }

    private sealed class Seg
    {
        public required string Path;
        public required int Timeline;
        public required int A;           // followed actor (camera root)
        public required int B;           // looked-at actor, -1 = single
        public required List<Span> Spans;
        public int Frames => Spans.Sum(s => s.Frames);
        public int Start => Spans[0].From;
        public int End => Spans[^1].To;

        // features
        public string Framing = "", Angle = "", Side = "", Motion = "", Follow = "subject";
        public List<string> Motions = new();
        public bool Slow, Roll, CorpseOk;
        public List<int> Points = new();
        public float FovMin, FovMax, Distance, MaxRoll, Centre, Smooth, Score, OtherVisible, FollowedVisible;
        public string Id = "", Name = "", Category = "", Era = "";
        public string Print = ""; // camera fingerprint: identical camera work reused by another cutscene (variants, re-releases)
    }

    private sealed class Sample
    {
        public Vector3 Eye, Aim, Dir;
        public float Fov, Roll, Dist, FrameH, Off, Elev, Az;
        public bool OtherIn, FollowedIn;
    }

    // ---- entry point ------------------------------------------------------------------------------------------

    public static void Run(Lumina.GameData gd, Func<string, CutbFile?> load, Bind.Body body, string outPath)
    {
        var paths = gd.GetExcelSheet<Lumina.Excel.Sheets.Cutscene>()!.Select(r => r.Path.ToString()).Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var kept = new List<Seg>();
        int files = 0, failed = 0, candidates = 0;
        var rejects = new Dictionary<string, int>();
        string[] steps = { "humanoid", "fov", "ground", "roll", "smooth", "in-frame" };
        var passed = steps.ToDictionary(s => s, _ => 0);
        foreach (var path in paths)
        {
            CutbFile? c;
            try { c = load(path); } catch { failed++; continue; }
            if (c == null) { failed++; continue; }
            files++;
            var pl = Bind.Placements(c);
            foreach (var tl in c.Timelines)
            {
                if (!tl.Items.Any(i => i.Magic == "C004")) continue;
                Sequence.Resolved r;
                try { r = Sequence.Resolve(c, tl); } catch { continue; }
                foreach (var seg in Segments(path, tl.Index, r.Spans))
                {
                    candidates++;
                    var why = Evaluate(seg, body, pl);
                    var reached = why == null ? steps.Length : Array.IndexOf(steps, why);
                    for (var i = 0; i < reached; i++) passed[steps[i]]++;
                    if (why != null) { rejects[why] = rejects.GetValueOrDefault(why) + 1; continue; }
                    kept.Add(seg);
                }
            }
        }
        Console.WriteLine($"cutscenes read {files}, missing/failed {failed}");
        Console.WriteLine($"candidate follow segments (3–15 s): {candidates}");
        Console.WriteLine("left after each filter: " + string.Join(" → ", steps.Select(s => $"{s} {passed[s]}")));
        Console.WriteLine($"  passing: {kept.Count} (single {kept.Count(k => k.B < 0)}, two-person {kept.Count(k => k.B >= 0)}, corpse-suitable {kept.Count(k => k.CorpseOk)}); distinct camera work {kept.Select(k => k.Print).Distinct().Count()}");

        var chosen = Select(kept);
        Name(chosen);
        Console.WriteLine($"curated {chosen.Count}: single {chosen.Count(k => k.B < 0)}, two-person {chosen.Count(k => k.B >= 0)} " +
                          $"(follow subject {chosen.Count(k => k.B >= 0 && k.Follow == "subject")}, follow other {chosen.Count(k => k.B >= 0 && k.Follow == "other")}), " +
                          $"corpse-suitable {chosen.Count(k => k.CorpseOk)}");
        foreach (var g in chosen.GroupBy(k => k.Framing).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key,-9} {g.Count(),3}: " + string.Join(" ", g.GroupBy(k => k.Motion).OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}×{m.Count()}")));
        Console.WriteLine("  eras: " + string.Join(" ", chosen.GroupBy(k => k.Era).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{g.Count()}")));
        foreach (var k in chosen) Console.WriteLine($"  {k.Id,-28} {k.Frames / 30f,5:F1}s cuts {k.Spans.Count - 1}  {k.Name}");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, Json(chosen), new UTF8Encoding(false));
        Console.WriteLine($"written {outPath}");
    }

    // ---- segmentation -----------------------------------------------------------------------------------------

    /// <summary>Look target class of a shot: -1 = in the rig or on the followed actor itself (single), else the other
    /// actor's index. Returns null for shots that cannot be followed (unattached root, eye on a third actor).</summary>
    private static (int A, int B)? Key(CameraShot s)
    {
        if (s.Channels == null || s.Root.Actor == 255) return null;
        var a = s.Root.Actor;
        var b = s.Aim.Actor == 255 || s.Aim.Actor == a ? -1 : s.Aim.Actor;
        if (s.Eye.Actor != 255 && s.Eye.Actor != a) return null;
        return (a, b);
    }

    private static IEnumerable<Seg> Segments(string path, int tl, List<(int From, int To, CameraShot Shot)> spans)
    {
        var i = 0;
        while (i < spans.Count)
        {
            var k = Key(spans[i].Shot);
            if (k == null) { i++; continue; }
            var run = new List<Span>();
            var j = i;
            while (j < spans.Count && Key(spans[j].Shot) is { } kj && kj == k && (run.Count == 0 || run[^1].To == spans[j].From))
            {
                run.Add(new Span(spans[j].Shot, spans[j].From, spans[j].To));
                j++;
            }
            i = j;
            // Cut the run into windows on shot boundaries (≤ 12 s each when it is long); trim a single overlong shot.
            var total = run.Sum(s => s.Frames);
            var windows = new List<List<Span>>();
            if (total <= MaxFrames) windows.Add(run);
            else
            {
                var cur = new List<Span>();
                foreach (var s in run)
                {
                    if (cur.Count > 0 && cur.Sum(x => x.Frames) + s.Frames > WindowFrames) { windows.Add(cur); cur = new(); }
                    cur.Add(s);
                }
                if (cur.Count > 0) windows.Add(cur);
            }
            foreach (var w in windows)
            {
                var list = w;
                if (list.Count == 1 && list[0].Frames > MaxFrames)
                    list = new() { list[0] with { To = list[0].From + WindowFrames } };
                var frames = list.Sum(s => s.Frames);
                if (frames < MinFrames || frames > MaxFrames) continue;
                yield return new Seg { Path = path, Timeline = tl, A = k.Value.A, B = k.Value.B, Spans = list };
            }
        }
    }

    // ---- features and filters ---------------------------------------------------------------------------------

    /// <summary>Computes features; returns the name of the first failed filter, or null.</summary>
    private static string? Evaluate(Seg seg, Bind.Body body, Dictionary<int, List<(int Tl, int Time, Matrix4x4 M)>> pl)
    {
        // Humanoid: every attach point must be a bind point of the humanoid body.
        var points = seg.Spans.SelectMany(s => new[] { s.Shot.Root, s.Shot.Eye, s.Shot.Aim }).Where(a => a.Actor != 255).Select(a => a.Point).Distinct().OrderBy(p => p).ToList();
        if (points.Any(p => !body.Eid.ContainsKey(p))) return "humanoid";
        seg.Points = points;
        seg.CorpseOk = points.All(p => body.Eid[p].Bone != "n_root");

        var headA = body.Head;
        var chestA = Vector3.Transform(Vector3.Zero, body.Skel.Model[body.Skel.Index("j_sebo_c")]);
        Matrix4x4? Rel(int actor, int time)
        {
            if (Bind.ActorAt(pl, seg.A, seg.Timeline, time) is not { } am || Bind.ActorAt(pl, actor, seg.Timeline, time) is not { } bm) return null;
            return Matrix4x4.Invert(am, out var inv) ? bm * inv : null;
        }
        if (seg.B >= 0 && Rel(seg.B, seg.Start) == null) return "humanoid";

        var per = new List<List<Sample>>();
        foreach (var sp in seg.Spans)
        {
            var list = new List<Sample>();
            for (var f = sp.From; f < sp.To; f += Step)
                list.Add(SampleAt(seg, sp.Shot, f, body, headA, chestA, Rel));
            if (sp.From + (list.Count - 1) * Step != sp.To - 1)
                list.Add(SampleAt(seg, sp.Shot, sp.To - 1, body, headA, chestA, Rel));
            per.Add(list);
        }
        var all = per.SelectMany(x => x).ToList();
        if (all.Any(s => s.Dist < 0.25f || s.Dist > 25f || float.IsNaN(s.Dist))) return "humanoid";
        // Monster-sized offsets: the look point of a humanoid follow shot stays within a person's height of the root,
        // and the camera stays within a dozen metres of the followed actor.
        if (all.Any(s => s.Eye.Length() > 14f)) return "humanoid";
        if (seg.B < 0 && all.Any(s => s.Aim.Y < -0.4f || s.Aim.Y > 2.6f || new Vector2(s.Aim.X, s.Aim.Z).Length() > 3f)) return "humanoid";
        if (seg.B >= 0)
        {
            var b = Rel(seg.B, seg.Start)!.Value.Translation;
            if (new Vector2(b.X, b.Z).Length() > 10f || MathF.Abs(b.Y) > 1.5f || new Vector2(b.X, b.Z).Length() < 0.3f) return "humanoid";
        }

        if (all.Any(s => s.Fov < 15f || s.Fov > 80f)) return "fov";
        if (all.Any(s => s.Eye.Y < 0.1f)) return "ground";
        seg.MaxRoll = all.Max(s => MathF.Abs(s.Roll));
        if (seg.MaxRoll > 25f) return "roll";

        // Smooth: no wild jump inside a shot (cuts are between spans and are not checked).
        var worst = 0f;
        foreach (var list in per)
        {
            var steps = new List<float>();
            for (var i = 1; i < list.Count; i++) steps.Add(Vector3.Distance(list[i].Eye, list[i - 1].Eye));
            var med = steps.Count == 0 ? 0 : steps.OrderBy(x => x).ElementAt(steps.Count / 2);
            for (var i = 1; i < list.Count; i++)
            {
                var a = list[i - 1]; var b = list[i];
                var eyeStep = Vector3.Distance(a.Eye, b.Eye) / Step;
                var dirStep = MathF.Acos(Math.Clamp(Vector3.Dot(a.Dir, b.Dir), -1f, 1f)) * 180f / MathF.PI / Step;
                var fovStep = MathF.Abs(a.Fov - b.Fov) / Step;
                if (eyeStep > 0.4f || dirStep > 6f || fovStep > 2f) return "smooth";
                if (eyeStep * Step > 0.1f && eyeStep * Step > 6f * med) return "smooth";
                worst = MathF.Max(worst, MathF.Max(eyeStep / MathF.Max(0.5f, b.Dist) / 0.02f, dirStep / 1.5f));
            }
        }
        seg.Smooth = worst;
        seg.Print = string.Join(";", per.Select(l => string.Join(",", new[] { l[0], l[l.Count / 2], l[^1] }
            .Select(x => FormattableString.Invariant($"{MathF.Round(x.Eye.X * 10)}/{MathF.Round(x.Eye.Y * 10)}/{MathF.Round(x.Eye.Z * 10)}/{MathF.Round(x.Fov)}"))) + $"#{l.Count}"));

        // The look target stays in frame (single: the followed actor's head or chest; two-person: the other's head).
        var inFrame = all.Count(s => s.OtherIn) / (float)all.Count;
        if (inFrame < 0.85f) return "in-frame";
        seg.OtherVisible = inFrame;
        seg.FollowedVisible = all.Count(s => s.FollowedIn) / (float)all.Count;

        // Features.
        seg.FovMin = all.Min(s => s.Fov); seg.FovMax = all.Max(s => s.Fov);
        seg.Distance = Median(all.Select(s => s.Dist));
        var h = Median(all.Select(s => s.FrameH));
        seg.Framing = h < 0.8f ? "close-up" : h < 1.8f ? "medium" : h < 4f ? "full" : "wide";
        var elev = Median(all.Select(s => s.Elev));
        seg.Angle = elev < -8f ? "low" : elev > 12f ? "high" : "eye-level";
        var az = Median(all.Select(s => MathF.Abs(s.Az)));
        seg.Side = az < 55f ? "front" : az > 125f ? "back" : "side";
        seg.Roll = seg.MaxRoll > 3f;
        seg.Centre = all.Average(s => s.Off / (s.Fov / 2f));
        // Two-person roles: the shot features the face it shows. Camera in front of / beside the followed actor
        // shows that actor's face → the followed one is the subject. Camera behind the followed actor (over the
        // shoulder) shows the other's face → the plugin follows the OTHER and looks at the subject.
        if (seg.B >= 0) seg.Follow = seg.Side == "back" || seg.FollowedVisible < 0.5f ? "other" : "subject";

        var motions = new List<(string M, float Score, int Frames)>();
        for (var i = 0; i < per.Count; i++)
            motions.Add(MotionOf(per[i], seg.Spans[i], seg, pl));
        seg.Motions = motions.Select(m => m.M).ToList();
        var main = motions.OrderByDescending(m => m.Frames).ThenBy(m => motions.IndexOf(m)).First();
        seg.Motion = main.M;
        seg.Slow = main.M is not ("static" or "tracking") && main.Score < 2.5f && main.Frames >= 4 * Fps;

        // Quality: centred subject, smooth, a moving camera, 4–12 s, little roll.
        var secs = seg.Frames / (float)Fps;
        seg.Score = 1f - 0.5f * Math.Clamp(seg.Centre, 0, 1) - 0.3f * Math.Clamp(seg.Smooth, 0, 1)
                    + (seg.Motion == "static" ? 0f : 0.15f) + (secs is >= 4 and <= 12 ? 0.2f : 0f)
                    - (seg.Roll ? 0.1f : 0f) - 0.03f * Math.Max(0, seg.Spans.Count - 3);
        return null;
    }

    private static Sample SampleAt(Seg seg, CameraShot s, int frame, Bind.Body body, Vector3 headA, Vector3 chestA, Func<int, int, Matrix4x4?> rel)
    {
        Matrix4x4 Anchor(Attach a)
        {
            if (a.Actor == 255) return Matrix4x4.Identity;
            var m = a.Actor == seg.A ? Matrix4x4.Identity : rel(a.Actor, frame) ?? Matrix4x4.Identity;
            return Bind.PointFrame(Bind.Mapping.EidBindPoint, body, a.Point, m);
        }
        var local = frame - s.Start;
        var e = s.Evaluate(local, Anchor);
        var d = e.Aim - e.Eye;
        var dir = d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : Vector3.UnitZ;
        var c117 = s.Channel(9, 117, 255)?.Eval(local) ?? 0f;
        var upRoll = CameraShot.RollDegrees(e.Eye, e.Aim, e.Up);
        float Angle(Vector3 p) { var t = p - e.Eye; return t.LengthSquared() < 1e-6f ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(dir, Vector3.Normalize(t)), -1f, 1f)) * 180f / MathF.PI; }
        var half = e.Fov / 2f;
        Vector3 subject;
        bool otherIn, followedIn = Angle(headA) < half || Angle(chestA) < half;
        float off;
        if (seg.B < 0)
        {
            var oh = Angle(headA); var oc = Angle(chestA);
            subject = oh <= oc ? headA : chestA;
            off = MathF.Min(oh, oc);
            otherIn = followedIn;
        }
        else
        {
            var bm = rel(seg.B, frame) ?? Matrix4x4.Identity;
            subject = Vector3.Transform(body.Head, bm);
            off = Angle(subject);
            otherIn = off < half;
        }
        var dist = Vector3.Distance(subject, e.Eye);
        var hd = new Vector2(e.Eye.X - subject.X, e.Eye.Z - subject.Z).Length();
        return new Sample
        {
            Eye = e.Eye, Aim = e.Aim, Dir = dir, Fov = e.Fov,
            Roll = MathF.Abs(c117) > MathF.Abs(upRoll) ? c117 : upRoll,
            Dist = dist, FrameH = 2f * dist * MathF.Tan(e.Fov * MathF.PI / 360f), Off = off,
            Elev = MathF.Atan2(e.Eye.Y - subject.Y, MathF.Max(hd, 1e-3f)) * 180f / MathF.PI,
            Az = MathF.Atan2(e.Eye.X, e.Eye.Z) * 180f / MathF.PI, // followed actor faces +Z
            OtherIn = otherIn, FollowedIn = followedIn,
        };
    }

    /// <summary>Camera move of one shot, from its first and last sample in the followed actor's frame.</summary>
    private static (string M, float Score, int Frames) MotionOf(List<Sample> l, Span sp, Seg seg, Dictionary<int, List<(int Tl, int Time, Matrix4x4 M)>> pl)
    {
        var a = l[0]; var b = l[^1];
        var cands = new List<(string, float)>();
        var r = b.FrameH / MathF.Max(1e-3f, a.FrameH);
        if (r < 1f) cands.Add(("push-in", MathF.Log(1f / r) / MathF.Log(1f / 0.85f)));
        else cands.Add(("pull-out", MathF.Log(r) / MathF.Log(1.18f)));
        // Orbit: angle of the eye around the look point, seen from above.
        var pa = a.Eye - a.Aim; var pb = b.Eye - b.Aim;
        var aza = MathF.Atan2(pa.X, pa.Z); var azb = MathF.Atan2(pb.X, pb.Z);
        var daz = (azb - aza) * 180f / MathF.PI;
        while (daz > 180f) daz -= 360f; while (daz < -180f) daz += 360f;
        var right = Vector3.Cross(a.Dir, Vector3.UnitY);
        var side = right.LengthSquared() > 1e-6f ? Vector3.Dot(b.Eye - a.Eye, Vector3.Normalize(right)) : 0f;
        cands.Add((side >= 0 ? "orbit-right" : "orbit-left", MathF.Abs(daz) / 12f));
        var dy = b.Eye.Y - a.Eye.Y;
        cands.Add((dy >= 0 ? "crane-up" : "crane-down", MathF.Abs(dy) / MathF.Max(0.25f, 0.12f * a.Dist)));
        var turn = MathF.Acos(Math.Clamp(Vector3.Dot(a.Dir, b.Dir), -1f, 1f)) * 180f / MathF.PI;
        if (Vector3.Distance(a.Eye, b.Eye) < 0.1f * a.Dist) cands.Add(("pan", turn / 8f));
        var best = cands.OrderByDescending(c => c.Item2).ThenBy(c => c.Item1, StringComparer.Ordinal).First();
        if (best.Item2 >= 1f) return (best.Item1, best.Item2, sp.Frames);
        // The camera holds still relative to the actor; if the actor itself walks, it is a tracking shot.
        if (Bind.ActorAt(pl, seg.A, seg.Timeline, sp.From) is { } m0 && Bind.ActorAt(pl, seg.A, seg.Timeline, sp.To - 1) is { } m1
            && Vector3.Distance(m0.Translation, m1.Translation) > 1f)
            return ("tracking", 1f, sp.Frames);
        return ("static", 0f, sp.Frames);
    }

    private static float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }

    // ---- selection --------------------------------------------------------------------------------------------

    private static string Kind(Seg s) => s.B < 0 ? "single" : s.Follow == "subject" ? "two-follow-subject" : "two-follow-other";

    private static List<Seg> Select(List<Seg> all)
    {
        int Cmp(Seg x, Seg y)
        {
            var c = y.Score.CompareTo(x.Score);
            if (c != 0) return c;
            c = string.CompareOrdinal(x.Path, y.Path);
            if (c != 0) return c;
            c = x.Timeline.CompareTo(y.Timeline);
            return c != 0 ? c : x.Start.CompareTo(y.Start);
        }
        var sorted = all.ToList();
        sorted.Sort(Cmp);
        var chosen = new List<Seg>();
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var perClass = new Dictionary<string, int>(StringComparer.Ordinal);
        string Cls(Seg s) => $"{Kind(s)}|{s.Framing}|{s.Motion}";
        bool Take(Seg s, int classCap, int fileCap = 1)
        {
            if (chosen.Contains(s) || perFile.GetValueOrDefault(s.Path) >= fileCap || perClass.GetValueOrDefault(Cls(s)) >= classCap) return false;
            // The same timeline window twice (overlapping frames) adds nothing.
            if (chosen.Any(o => o.Path == s.Path && o.Timeline == s.Timeline && o.Start < s.End && s.Start < o.End)) return false;
            // The same camera work reused by another cutscene (language/job variants, re-releases) adds nothing either.
            if (chosen.Any(o => o.Print == s.Print)) return false;
            chosen.Add(s);
            perFile[s.Path] = perFile.GetValueOrDefault(s.Path) + 1;
            perClass[Cls(s)] = perClass.GetValueOrDefault(Cls(s)) + 1;
            return true;
        }
        // Pass 1: the best of every class. Pass 2: a second of every class. Pass 3: top up two-person and
        // corpse-suitable shots (bone-attached) until there are enough of each.
        foreach (var s in sorted) Take(s, 1);
        foreach (var s in sorted) Take(s, 2);
        foreach (var kind in new[] { "two-follow-subject", "two-follow-other" })
            foreach (var s in sorted.Where(s => Kind(s) == kind))
            {
                if (chosen.Count(c => Kind(c) == kind) >= 20) break;
                Take(s, 4);
            }
        foreach (var s in sorted.Where(s => s.CorpseOk))
        {
            if (chosen.Count(c => c.CorpseOk) >= 40) break;
            Take(s, 4);
        }
        // Cap: drop the weakest second-of-class entries when over 150.
        while (chosen.Count > 150)
        {
            var drop = chosen.Where(c => chosen.Count(o => Cls(o) == Cls(c)) > 1).OrderBy(c => c.Score).ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault()
                       ?? chosen.OrderBy(c => c.Score).First();
            chosen.Remove(drop);
        }
        chosen.Sort((x, y) =>
        {
            var c = string.CompareOrdinal(Kind(x), Kind(y));
            if (c != 0) return c;
            c = FramingOrder(x.Framing).CompareTo(FramingOrder(y.Framing));
            if (c != 0) return c;
            c = string.CompareOrdinal(x.Motion, y.Motion);
            return c != 0 ? c : Cmp(x, y);
        });
        return chosen;
    }

    private static int FramingOrder(string f) => f switch { "close-up" => 0, "medium" => 1, "full" => 2, _ => 3 };

    // ---- names ------------------------------------------------------------------------------------------------

    private static string Era(string path) => path.Split('/')[0] switch
    {
        "ffxiv" => "ARR", "ex1" => "HW", "ex2" => "SB", "ex3" => "ShB", "ex4" => "EW", "ex5" => "DT", var p => p.ToUpperInvariant(),
    };

    private static string MotionWords(string m) => m switch
    {
        "static" => "Still", "push-in" => "Push-in", "pull-out" => "Pull-out", "orbit-left" => "Orbit left", "orbit-right" => "Orbit right",
        "crane-up" => "Crane up", "crane-down" => "Crane down", "pan" => "Pan", "tracking" => "Tracking", _ => m,
    };

    private static string FramingWords(string f, bool shortForm) => f switch
    {
        "close-up" => "close-up", "medium" => shortForm ? "medium" : "medium shot", "full" => shortForm ? "full" : "full body", _ => shortForm ? "wide" : "wide shot",
    };

    private static void Name(List<Seg> list)
    {
        foreach (var s in list)
        {
            s.Era = Era(s.Path);
            var motion = MotionWords(s.Motion);
            if (s.Slow) motion = "Slow " + motion.ToLowerInvariant();
            string name;
            if (s.B < 0)
            {
                // Most important first; drop from the end of the optional list until it fits six words.
                var angle = s.Angle == "eye-level" ? "" : s.Angle;
                var side = s.Side == "back" ? "from behind" : s.Side == "side" ? "profile" : "";
                var core = $"{motion}, {(angle.Length > 0 ? angle + " " : "")}{FramingWords(s.Framing, false)}";
                name = side.Length > 0 && Words(core) + Words(side) <= 6 ? $"{core}, {side}" : core;
                if (Words(name) > 6) name = $"{motion}, {(angle.Length > 0 ? angle + " " : "")}{FramingWords(s.Framing, true)}";
                if (Words(name) > 6) name = $"{MotionWords(s.Motion)}, {FramingWords(s.Framing, true)}";
            }
            else
            {
                var dir = s.Follow == "subject" ? "Follow subject, see other" : "Follow other, see subject";
                name = $"{dir}, {MotionWords(s.Motion).ToLowerInvariant()} {FramingWords(s.Framing, true)}";
                if (s.Motion == "static") name = $"{dir}, {FramingWords(s.Framing, true)}";
            }
            s.Name = name;
            s.Category = $"{(s.B < 0 ? "single" : "two-person")}/{s.Framing}/{s.Motion}";
            s.Id = $"{s.Path.Split('/')[^1]}-t{s.Timeline}-s{s.Spans[0].Shot.Index}";
        }
        // Disambiguate: era tag first, then a running number within the era.
        foreach (var g in list.GroupBy(s => s.Name).Where(g => g.Count() > 1).ToList())
            foreach (var s in g) s.Name += $" ({s.Era})";
        foreach (var g in list.GroupBy(s => s.Name).Where(g => g.Count() > 1).ToList())
        {
            var n = 1;
            foreach (var s in g) s.Name = s.Name[..^1] + $" {n++})";
        }
        foreach (var g in list.GroupBy(s => s.Id).Where(g => g.Count() > 1).ToList())
        {
            var n = 1;
            foreach (var s in g.Skip(1)) s.Id += $"-{++n}";
        }
    }

    private static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- output -----------------------------------------------------------------------------------------------

    private static double R(float v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Json(List<Seg> list)
    {
        var opts = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, opts))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("source", "Tests/CutsceneCamera: dotnet run -c Release -- curate (see docs/CUTSCENE_SHOTS.md)");
            w.WriteString("note", "References into the game's own cutscenes plus derived features only; the camera curves are read from the user's game install at runtime.");
            w.WriteStartArray("shots");
            foreach (var s in list)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteString("category", s.Category);
                w.WriteStartArray("tags");
                foreach (var t in Tags(s)) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteString("cutscene", s.Path);
                w.WriteNumber("timeline", s.Timeline);
                w.WriteNumber("firstShot", s.Spans[0].Shot.Index);
                w.WriteNumber("lastShot", s.Spans[^1].Shot.Index);
                w.WriteStartArray("shots");
                foreach (var sp in s.Spans)
                {
                    w.WriteStartObject();
                    w.WriteNumber("shot", sp.Shot.Index);
                    w.WriteNumber("from", sp.From);
                    w.WriteNumber("to", sp.To);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("startFrame", s.Start);
                w.WriteNumber("endFrame", s.End);
                w.WriteNumber("duration", R(s.Frames / (float)Fps));
                w.WriteNumber("cuts", s.Spans.Count - 1);
                w.WriteString("kind", s.B < 0 ? "single" : "two-person");
                w.WriteString("follow", s.B < 0 ? "subject" : s.Follow);
                if (s.B < 0) w.WriteNull("lookAt"); else w.WriteString("lookAt", s.Follow == "subject" ? "other" : "subject");
                w.WriteNumber("followActor", s.A);
                if (s.B < 0) w.WriteNull("lookActor"); else w.WriteNumber("lookActor", s.B);
                w.WriteBoolean("corpseSuitable", s.CorpseOk);
                w.WriteStartArray("attachPoints");
                foreach (var p in s.Points) w.WriteNumberValue(p);
                w.WriteEndArray();
                w.WriteStartObject("features");
                w.WriteString("framing", s.Framing);
                w.WriteString("angle", s.Angle);
                w.WriteString("side", s.Side);
                w.WriteString("motion", s.Motion);
                w.WriteBoolean("slow", s.Slow);
                w.WriteStartArray("motions");
                foreach (var m in s.Motions) w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteBoolean("roll", s.Roll);
                w.WriteNumber("maxRoll", R(s.MaxRoll));
                w.WriteNumber("fovMin", R(s.FovMin));
                w.WriteNumber("fovMax", R(s.FovMax));
                w.WriteNumber("distance", R(s.Distance));
                w.WriteNumber("quality", R(s.Score));
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static IEnumerable<string> Tags(Seg s)
    {
        yield return s.B < 0 ? "single" : "two-person";
        if (s.B >= 0) yield return s.Follow == "subject" ? "follow-subject" : "follow-other";
        yield return s.Framing;
        yield return s.Motion;
        if (s.Slow) yield return "slow";
        yield return s.Angle;
        yield return s.Side;
        if (s.Roll) yield return "roll";
        if (s.Spans.Count > 1) yield return "cuts";
        if (s.CorpseOk) yield return "corpse";
        yield return s.Era;
    }
}
