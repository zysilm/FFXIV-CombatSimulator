using System.Globalization;
using System.Numerics;
using System.Text;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>
/// Death guidance, phase P0: the tables and contact sheets over the "what if" re-simulations that
/// Tests/RagdollReplay --what-if writes (one folder per profile and start point: &lt;root&gt;/&lt;profile&gt;/s&lt;percent&gt;/, a
/// trajectory and a ".wi" file of simulation numbers per recording).
///   guidance &lt;root&gt; [--profiles a,b,c]       the metric table per start class, the first profile against the others,
///                                            the gates, and the library start state against the pose classes
///   guidance-validate &lt;root&gt; --traj &lt;folder&gt;  how far the "recorded" what-if is from the recordings themselves
///   guidance-sheets &lt;root&gt; [n] [--zoom]       stick-figure sheets, the profiles beside each other, into &lt;root&gt;/sheets
///                                            (--zoom: twice the size, each body centred, the first profile in grey underneath)
/// Start classes (at the hand-off pose, thresholds of the death-shot pose classes: torso within 53° of vertical =
/// upright, hips at 0.60 m × body scale): "mid-fall" = the pelvis moves faster than 0.5 m/s at hand-off, or the body
/// is not upright but its hips are still above 0.45 m × scale; otherwise upright with high hips = "standing", upright
/// with low hips = "kneeling", the rest = "lying".
/// </summary>
internal static class Guidance
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>The folders compared, the baseline first (--profiles a,b,c replaces them).</summary>
    public static string[] Profiles = { "limp", "hold-legs", "hold-pose", "slow-slump" };
    private static readonly string[] Classes = { "standing", "mid-fall", "lying", "kneeling" };

    /// <summary>One what-if run: its trajectory, its simulation numbers, and what this tool derives from them.</summary>
    public sealed class Run
    {
        public string Name = "", Profile = "";
        public int Percent;
        public Trajectory T = null!;
        public Dictionary<string, string> Wi = new();
        public int HandOff = -1;          // index of the hand-off frame (the last one not flagged as physics)
        public string StartClass = "";
        public PoseClass StartPose, EndPose;
        public float Scale = 1f;
        public float SettleAt = -1f;      // seconds of physics to the first settle; -1 = never
        public int MovedAgain;
        public float[] HipHeight = new float[5];   // start, 0.5 s, 1 s, 2 s, end
        public float[] KneeChange = new float[4];  // 0.5 s, 1 s, 2 s, end: largest change of a knee's bend, degrees
        public float Penetration, Float;
        public float FallAngle = float.NaN, Drift = float.NaN, EndsDrift = float.NaN;
        public string LibraryClass = "";
        public float Seconds;

        public float[] Series(string key)
            => Wi.TryGetValue(key, out var text) && text.Length > 0
                ? text.Split(',').Select(v => float.Parse(v, Inv)).ToArray()
                : Array.Empty<float>();

        public float Number(string key) => Wi.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, Inv, out var v) ? v : float.NaN;

        /// <summary>The positions of a frame as physics has them: the rigid bodies once there are any.</summary>
        public Vector3[] Positions(int frame)
        {
            var f = T.Frames[frame];
            var p = new Vector3[T.Bones.Length];
            for (var b = 0; b < p.Length; b++) p[b] = !float.IsNaN(f.Body[b].X) && frame >= HandOff ? f.Body[b] : f.Bone[b];
            return p;
        }

        public float PhysicsTime(int frame) => T.Frames[frame].Time - T.Frames[HandOff].Time;

        public int FrameAt(float physicsSeconds)
        {
            for (var i = HandOff; i < T.Frames.Length; i++)
                if (PhysicsTime(i) >= physicsSeconds - 1e-4f) return i;
            return T.Frames.Length - 1;
        }
    }

    public static Dictionary<string, string> ReadWi(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var at = line.IndexOf('=');
            if (at > 0) d[line[..at]] = line[(at + 1)..];
        }
        return d;
    }

    public static List<Run> Load(string root, IEnumerable<string> profiles, Func<string, bool>? nameFilter = null)
    {
        var runs = new List<Run>();
        foreach (var profile in profiles)
        {
            var folder = Path.Combine(root, profile);
            if (!Directory.Exists(folder)) continue;
            foreach (var start in Directory.GetDirectories(folder, "s*"))
            {
                if (!int.TryParse(Path.GetFileName(start)[1..], out var percent)) continue;
                foreach (var file in Directory.GetFiles(start, "*.wi").OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (nameFilter != null && !nameFilter(name)) continue;
                    var traj = Path.Combine(start, name + ".dstraj");
                    if (!File.Exists(traj)) continue;
                    var run = new Run { Name = name, Profile = profile, Percent = percent, T = Trajectory.Read(traj), Wi = ReadWi(file) };
                    if (Derive(run)) runs.Add(run);
                }
            }
        }
        return runs;
    }

    private static float Knee(Trajectory t, Vector3[] bone, string side)
    {
        var a = bone[t.BoneIndex("j_asi_a" + side)];
        var b = bone[t.BoneIndex("j_asi_b" + side)];
        var c = bone[t.BoneIndex("j_asi_c" + side)];
        var d = bone[t.BoneIndex("j_asi_d" + side)];
        var thigh = b - a;
        var shin = d - c;
        if (thigh.LengthSquared() < 1e-8f || shin.LengthSquared() < 1e-8f) return float.NaN;
        return MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(thigh), Vector3.Normalize(shin)), -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>Everything read off the trajectory. False when the run never reached physics or is not a humanoid.</summary>
    private static bool Derive(Run run)
    {
        var t = run.T;
        if (!t.Humanoid) return false;
        var first = Array.FindIndex(t.Frames, f => f.PhysicsStarted);
        if (first < 1) return false;
        run.HandOff = first - 1;
        var world = new RecordedWorld(t);
        float Ground(Vector3 p) => world.HasGround ? world.FieldHeight(p.X, p.Z) : t.Frames[run.HandOff].GroundY;

        var start = run.Positions(run.HandOff);
        var startPoints = t.Points(start);
        // The body scale as the solver takes it: spine length of the bones against the reference body.
        var bones = t.Points(t.Frames[run.HandOff].Bone);
        run.Scale = Math.Clamp((Vector3.Distance(bones.Chest, bones.Hips) + Vector3.Distance(bones.Head, bones.Chest)) / DeathShotSolver.ReferenceSpine, 0.4f, 4f);
        var frame = BodyFrame.From(bones, Ground(bones.Hips));
        run.StartPose = PoseClassifier.Raw(frame, run.Scale);
        var velocity = run.Series("pelvisVelocity");
        var speed = velocity.Length == 3 ? new Vector3(velocity[0], velocity[1], velocity[2]).Length() : 0f;
        var upright = run.StartPose is PoseClass.Standing or PoseClass.Kneeling;
        run.StartClass = speed > 0.5f || (!upright && frame.HipsHeight > 0.45f * run.Scale) ? "mid-fall"
            : run.StartPose == PoseClass.Standing ? "standing"
            : run.StartPose == PoseClass.Kneeling ? "kneeling"
            : "lying";

        var classifier = new PoseClassifier { Scale = run.Scale };
        var settle = new SettleDetector();
        var wasSettled = false;
        var deepest = 0f;
        var startKnee = new[] { Knee(t, t.Frames[run.HandOff].Bone, "_l"), Knee(t, t.Frames[run.HandOff].Bone, "_r") };
        var sampleAt = new[] { 0.5f, 1f, 2f };
        var sampled = 0;
        run.HipHeight[0] = start[t.BoneIndex("j_kosi")].Y - Ground(start[t.BoneIndex("j_kosi")]);
        for (var i = run.HandOff; i < t.Frames.Length; i++)
        {
            var f = t.Frames[i];
            var positions = run.Positions(i);
            var points = t.Points(positions);
            var bonePoints = t.Points(f.Bone);
            run.EndPose = classifier.Update(BodyFrame.From(bonePoints, Ground(bonePoints.Hips)), f.Dt);
            var settled = settle.Update(points, f.Dt);
            if (settled && !wasSettled)
            {
                if (run.SettleAt < 0f) run.SettleAt = run.PhysicsTime(i);
            }
            else if (!settled && wasSettled) run.MovedAgain++;
            wasSettled = settled;
            if (i > run.HandOff)
                foreach (var p in f.Bone)
                    if (!float.IsNaN(p.X)) deepest = MathF.Max(deepest, Ground(p) - p.Y);
            var time = run.PhysicsTime(i);
            while (sampled < sampleAt.Length && time >= sampleAt[sampled] - 1e-4f)
            {
                Sample(sampled);
                sampled++;
            }
            if (i == t.Frames.Length - 1)
            {
                for (; sampled < sampleAt.Length; sampled++) Sample(sampled);
                Sample(3);
                var lowest = float.MaxValue;
                foreach (var p in positions)
                    if (!float.IsNaN(p.X)) lowest = MathF.Min(lowest, p.Y - Ground(p));
                run.Float = lowest;
            }

            void Sample(int slot)
            {
                run.HipHeight[slot + 1] = positions[t.BoneIndex("j_kosi")].Y - Ground(positions[t.BoneIndex("j_kosi")]);
                var l = MathF.Abs(Knee(t, f.Bone, "_l") - startKnee[0]);
                var r = MathF.Abs(Knee(t, f.Bone, "_r") - startKnee[1]);
                run.KneeChange[slot] = float.IsNaN(l) ? r : float.IsNaN(r) ? l : MathF.Max(l, r);
            }
        }
        run.Penetration = deepest;
        {
            var last = run.Positions(t.Frames.Length - 1);
            float sum = 0f, ends = 0f;
            int n = 0, m = 0;
            for (var b = 0; b < last.Length; b++)
            {
                if (float.IsNaN(last[b].X) || float.IsNaN(start[b].X)) continue;
                var d = Vector3.Distance(last[b], start[b]);
                sum += d; n++;
                if (t.Bones[b] is "j_te_l" or "j_te_r" or "j_asi_d_l" or "j_asi_d_r") { ends += d; m++; }
            }
            run.Drift = n > 0 ? sum / n : float.NaN;
            run.EndsDrift = m > 0 ? ends / m : float.NaN;
            var log = run.Wi.GetValueOrDefault("guidanceLog") ?? "";
            var at = log.IndexOf("(start ", StringComparison.Ordinal);
            if (at >= 0) run.LibraryClass = log[(at + 7)..].Split(',')[0];
        }
        run.Seconds = run.PhysicsTime(t.Frames.Length - 1);

        // Which way the body went, against the way the chest was moving at hand-off (horizontal).
        if (run.HandOff >= 1)
        {
            var chest = t.BoneIndex("j_sebo_c");
            var before = t.Frames[run.HandOff - 1];
            var at = t.Frames[run.HandOff];
            var dt = at.Time - before.Time;
            if (dt > 1e-4f)
            {
                var v = (at.Bone[chest] - before.Bone[chest]) / dt;
                v.Y = 0f;
                var moved = run.Positions(t.Frames.Length - 1)[chest] - start[chest];
                moved.Y = 0f;
                if (v.Length() >= 0.3f && moved.Length() >= 0.15f)
                    run.FallAngle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(v), Vector3.Normalize(moved)), -1f, 1f)) * 180f / MathF.PI;
            }
        }
        return true;
    }

    private static float Median(IEnumerable<float> values) => Percentile(values, 0.5f);

    private static float Percentile(IEnumerable<float> values, float p)
    {
        var sorted = values.Where(v => !float.IsNaN(v)).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return float.NaN;
        return sorted[Math.Clamp((int)MathF.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    private static string F(float v, string format = "F1") => float.IsNaN(v) ? "–" : v.ToString(format, Inv);

    // ---- the table ----

    public static int Table(string root, int onlyStart = -1)
    {
        var runs = Load(root, Profiles);
        if (onlyStart >= 0) runs = runs.Where(r => r.Percent == onlyStart).ToList();
        if (runs.Count == 0)
        {
            Console.Error.WriteLine($"no what-if runs under {root}");
            return 2;
        }
        // A run counts only where every profile ran the same recording from the same start; its class is the Limp run's.
        var byKey = runs.GroupBy(r => (r.Name, r.Percent)).Where(g => Profiles.All(p => g.Any(r => r.Profile == p))).ToList();
        var classOf = byKey.ToDictionary(g => g.Key, g => g.First(r => r.Profile == Profiles[0]).StartClass);
        Console.WriteLine($"what-if runs: {runs.Count} in {root}; complete sets (all {Profiles.Length} profiles): {byKey.Count} " +
                          $"from {byKey.Select(g => g.Key.Name).Distinct().Count()} recordings");
        Console.WriteLine("start classes by start point (percent of the recorded animation): " + string.Join("; ",
            byKey.GroupBy(g => g.Key.Percent).OrderBy(g => g.Key).Select(g => $"{g.Key}%: " + string.Join(", ",
                Classes.Select(c => $"{c} {g.Count(x => classOf[x.Key] == c)}")))));
        var problems = runs.Where(r => r.Number("problems") > 0).ToList();
        Console.WriteLine($"runs in which the controller logged a warning or an error: {problems.Count}" +
                          (problems.Count > 0 ? "; first: " + problems[0].Name + " " + problems[0].Profile + " s" + problems[0].Percent + ": " + problems[0].Wi.GetValueOrDefault("firstProblem") : ""));
        // The library's start state (from its guidance log line) against this tool's class and against the plugin's
        // pose class of the same hand-off pose (PoseClassifier.Raw: upright, hips high).
        var measured = runs.Where(r => r.LibraryClass.Length > 0).GroupBy(r => (r.Name, r.Percent)).Select(g => g.First()).ToList();
        if (measured.Count > 0)
        {
            string Tool(string c) => c switch { "Standing" => "standing", "Kneeling" => "kneeling", "Falling" => "mid-fall", _ => "lying" };
            bool Pose(Run r) => r.LibraryClass switch
            {
                "Standing" => r.StartPose == PoseClass.Standing,
                "Kneeling" => r.StartPose == PoseClass.Kneeling,
                "Lying" => r.StartPose is PoseClass.Side or PoseClass.Supine or PoseClass.Prone,
                _ => true, // falling is decided by speed, which the pose class does not have
            };
            Console.WriteLine($"library start state on {measured.Count} hand-off poses of {measured.Select(r => r.Name).Distinct().Count()} recordings: " +
                              $"same class as this tool {measured.Count(r => Tool(r.LibraryClass) == r.StartClass)}; " +
                              $"consistent with the plugin's pose class (upright / hips high) {measured.Count(Pose)}; " +
                              "differences: " + string.Join(", ", measured.Where(r => Tool(r.LibraryClass) != r.StartClass)
                                  .GroupBy(r => $"library {r.LibraryClass} / tool {r.StartClass}").Select(g => $"{g.Key} {g.Count()}").DefaultIfEmpty("none")));
        }
        Console.WriteLine();

        foreach (var c in Classes)
        {
            var sets = byKey.Where(g => classOf[g.Key] == c).ToList();
            Console.WriteLine($"### Start class: {c} ({sets.Count} runs per profile, {sets.Select(g => g.Key.Name).Distinct().Count()} recordings)");
            if (sets.Count == 0)
            {
                Console.WriteLine("no sample.");
                Console.WriteLine();
                continue;
            }
            var columns = Profiles.Select(p => sets.Select(g => g.First(r => r.Profile == p)).ToList()).ToArray();
            Console.WriteLine("| metric | " + string.Join(" | ", Profiles) + " |");
            Console.WriteLine("|---|" + string.Concat(Enumerable.Repeat("---:|", Profiles.Length)));
            void Row(string title, Func<List<Run>, string> cell) => Console.WriteLine($"| {title} | " + string.Join(" | ", columns.Select(cell)) + " |");
            string At(List<Run> rs, string key, int slot) => F(Median(rs.Select(r => r.Series(key) is { Length: > 0 } s && slot < s.Length ? s[slot] : float.NaN)));
            string Four(List<Run> rs, string key) => $"{At(rs, key, 0)} / {At(rs, key, 1)} / {At(rs, key, 2)} / {At(rs, key, 4)}";

            Row("leg joints (hip, knee, ankle): largest turn from the hand-off pose, ° — 0.5 s / 1 s / 2 s / end (median)", rs => Four(rs, "legsDevMax"));
            Row("same, mean over the leg joints", rs => Four(rs, "legsDevMean"));
            Row("knee joints: largest turn, °", rs => Four(rs, "kneesDevMax"));
            Row("hip joints: largest turn, °", rs => Four(rs, "hipsDevMax"));
            Row("spine joints: largest turn, °", rs => Four(rs, "torsoDevMax"));
            Row("arm joints: mean turn, °", rs => Four(rs, "armsDevMean"));
            Row("head and neck: largest turn, °", rs => Four(rs, "headneckDevMax"));
            Row("lower leg (body): moved, m — 0.5 s / 1 s / 2 s / end", rs => string.Join(" / ", new[] { 0, 1, 2, 4 }.Select(i => F(Median(rs.Select(r => S(r, "calfMove", i))), "F2"))));
            Row("lower leg (body): turned, °", rs => Four(rs, "calfTurn"));
            Row("lower legs about where they were at the end (moved < 10 cm and turned < 20°): runs", rs => rs.Count(r => S(r, "calfMove", 4) < 0.10f && S(r, "calfTurn", 4) < 20f).ToString(Inv));
            Row("leg joints kept their angles (mean turn ≤ 10°): runs at 2 s / at the end", rs => $"{rs.Count(r => S(r, "legsDevMean", 2) <= 10f)} / {rs.Count(r => S(r, "legsDevMean", 4) <= 10f)}");
            Row("knee bend changed by, ° (from bone positions) — 0.5 s / 1 s / 2 s / end", rs => string.Join(" / ", Enumerable.Range(0, 4).Select(i => F(Median(rs.Select(r => r.KneeChange[i]))))));
            Row("hips above ground, m — start / 0.5 s / 1 s / 2 s / end", rs => string.Join(" / ", Enumerable.Range(0, 5).Select(i => F(Median(rs.Select(r => r.HipHeight[i])), "F2"))));
            Row("pose class at the end: standing / kneeling / side / supine / prone", rs =>
                $"{rs.Count(r => r.EndPose == PoseClass.Standing)} / {rs.Count(r => r.EndPose == PoseClass.Kneeling)} / {rs.Count(r => r.EndPose == PoseClass.Side)} / {rs.Count(r => r.EndPose == PoseClass.Supine)} / {rs.Count(r => r.EndPose == PoseClass.Prone)}");
            Row("started on a side → ended prone or supine (of those started on a side)", rs =>
                $"{rs.Count(r => r.StartPose == PoseClass.Side && r.EndPose is PoseClass.Prone or PoseClass.Supine && r.StartPose == PoseClass.Side)} of {rs.Count(r => r.StartPose == PoseClass.Side)}");
            Row("pose class at the end differs from the hand-off class", rs => $"{rs.Count(r => r.EndPose != r.StartPose)}");
            Row("time to first settle, s — median / p90 (7 points under 5 cm/s for 0.8 s)", rs =>
                $"{F(Median(rs.Where(r => r.SettleAt >= 0).Select(r => r.SettleAt)))} / {F(Percentile(rs.Where(r => r.SettleAt >= 0).Select(r => r.SettleAt), 0.9f))}");
            Row("never settled in the run", rs => rs.Count(r => r.SettleAt < 0).ToString(Inv));
            Row("moved again after settling (runs / times)", rs => $"{rs.Count(r => r.MovedAgain > 0)} / {rs.Sum(r => r.MovedAgain)}");
            Row("simulation at rest (island asleep, no step), s — median; never", rs =>
                $"{F(Median(rs.Where(r => r.Number("restTick") >= 0).Select(r => r.Number("restTick") / 60f)))}; {rs.Count(r => r.Number("restTick") < 0)}");
            Row("deepest bone origin below the ground, cm — median / max", rs => $"{F(Median(rs.Select(r => r.Penetration * 100f)))} / {F(rs.Max(r => r.Penetration) * 100f)}");
            Row("lowest body centre above the ground at the end, cm — median / max", rs => $"{F(Median(rs.Select(r => r.Float * 100f)))} / {F(rs.Max(r => r.Float) * 100f)}");
            Row("joint-limit stress at the end: bones at ≥ 98 % of a swing limit per run, mean; largest stress", rs =>
                $"{F(rs.Average(r => r.Number("stressPinned")), "F2")}; {F(rs.Max(r => r.Number("stressMax")), "F2")}");
            Row("runs with 5 or more kinetic-energy spikes", rs => rs.Count(r => r.Number("energySpikes") >= 5).ToString(Inv));
            Row("kinetic-energy spikes per run (a rise > 25 % and > 0.05 J between fixed steps, after 0.5 s; the skeleton's own bodies) — mean / max", rs =>
                $"{F(rs.Average(r => r.Number("energySpikes")), "F2")} / {F(rs.Max(r => r.Number("energySpikes")), "F0")}");
            Row("same, after 4 s — mean / runs with any", rs => $"{F(rs.Average(r => r.Number("energySpikesLate")), "F2")} / {rs.Count(r => r.Number("energySpikesLate") > 0)}");
            Row("largest angular speed of a body, rad/s — median / max", rs => $"{F(Median(rs.Select(r => r.Number("maxAngularSpeed"))))} / {F(rs.Max(r => r.Number("maxAngularSpeed")))}");
            Row("same, after 4 s — median / max", rs => $"{F(Median(rs.Select(r => r.Number("maxAngularSpeedLate"))), "F2")} / {F(rs.Max(r => r.Number("maxAngularSpeedLate")))}");
            Row("largest kinetic energy after 4 s, J — median / max", rs => $"{F(Median(rs.Select(r => r.Number("maxEnergyLate"))), "F3")} / {F(rs.Max(r => r.Number("maxEnergyLate")), "F2")}");
            Row("fall direction against the chest's hand-off velocity, ° — median (runs with ≥ 0.3 m/s)", rs =>
                $"{F(Median(rs.Select(r => r.FallAngle)))} ({rs.Count(r => !float.IsNaN(r.FallAngle))})");
            Row("limb drift: mean distance of the 22 bodies from where they were at hand-off, m — median / p90", rs =>
                $"{F(Median(rs.Select(r => r.Drift)), "F3")} / {F(Percentile(rs.Select(r => r.Drift), 0.9f), "F3")}");
            Row("hands and feet: mean distance from where they were at hand-off, m — median", rs => F(Median(rs.Select(r => r.EndsDrift)), "F3"));
            // The gates: no more runs with five or more energy spikes, and no more runs that moved again after settling, than the baseline.
            var baseSpikes = columns[0].Count(r => r.Number("energySpikes") >= 5);
            var baseMoved = columns[0].Count(r => r.MovedAgain > 0);
            Row("GATE: runs with ≥ 5 energy spikes and runs that moved again, not worse than the first column", rs =>
                rs.Count(r => r.Number("energySpikes") >= 5) <= baseSpikes && rs.Count(r => r.MovedAgain > 0) <= baseMoved ? "pass" : "FAIL");
            Console.WriteLine();
        }
        return 0;
    }

    private static float S(Run r, string key, int slot)
    {
        var s = r.Series(key);
        return slot < s.Length ? s[slot] : float.NaN;
    }

    // ---- how far the tool is from the recordings ----

    public static int Validate(string root, string recordedTrajectories)
    {
        var runs = Load(root, new[] { "recorded" }).Where(r => r.Percent == 100).ToList();
        if (runs.Count == 0)
        {
            Console.Error.WriteLine($"no 'recorded' what-if runs at 100% under {root}");
            return 2;
        }
        var rows = new List<(Run Run, bool Clean, float Seconds, float First, float At1, float At3, float End, float EndAll, bool SameClass, PoseClass Rec, PoseClass WhatIf, float RecMoved)>();
        var noPhysics = 0;
        foreach (var run in runs)
        {
            var path = Path.Combine(recordedTrajectories, run.Name + ".dstraj");
            if (!File.Exists(path)) continue;
            var rec = Trajectory.Read(path);
            var recFirst = Array.FindIndex(rec.Frames, f => f.PhysicsStarted);
            if (recFirst < 1) { noPhysics++; continue; }
            var recRun = new Run { Name = run.Name, Profile = "recording", Percent = 100, T = rec, Wi = run.Wi };
            if (!Derive(recRun)) continue;
            var hips = rec.BoneIndex("j_kosi");
            var n = Math.Min(rec.Frames.Length - recRun.HandOff, run.T.Frames.Length - run.HandOff);
            if (n < 2) continue;
            float Error(int offset, bool all)
            {
                offset = Math.Min(offset, n - 1);
                var a = recRun.Positions(recRun.HandOff + offset);
                var b = run.Positions(run.HandOff + offset);
                if (!all) return Vector3.Distance(a[hips], b[hips]);
                var sum = 0f;
                var count = 0;
                for (var i = 0; i < a.Length; i++)
                    if (!float.IsNaN(a[i].X) && !float.IsNaN(b[i].X)) { sum += Vector3.Distance(a[i], b[i]); count++; }
                return count > 0 ? sum / count : float.NaN;
            }
            int OffsetAt(float seconds)
            {
                for (var i = 0; i < n; i++)
                    if (recRun.PhysicsTime(recRun.HandOff + i) >= seconds) return i;
                return n - 1;
            }
            // The classes at the same frame: the last one both have.
            var recClass = ClassAt(recRun, recRun.HandOff + n - 1);
            var whatIfClass = ClassAt(run, run.HandOff + n - 1);
            var clean = run.Number("recordedColliders") <= 0 && run.Number("recordedGrabs") <= 0 && run.Number("recordedOtherCalls") <= 0;
            var moved = Vector3.Distance(recRun.Positions(recRun.HandOff + n - 1)[hips], recRun.Positions(recRun.HandOff)[hips]);
            rows.Add((run, clean, recRun.PhysicsTime(recRun.HandOff + n - 1), Error(1, false), Error(OffsetAt(1f), false), Error(OffsetAt(3f), false),
                Error(n - 1, false), Error(n - 1, true), recClass == whatIfClass, recClass, whatIfClass, moved));
        }
        Console.WriteLine($"'recorded' what-if (the recording's own start frame, guidance settings and frame times; no other characters, grabs, hair or mesh volumes) against the recordings: " +
                          $"{rows.Count} recordings with physics compared ({noPhysics} never started physics)");
        void Group(string title, Func<(Run Run, bool Clean, float Seconds, float First, float At1, float At3, float End, float EndAll, bool SameClass, PoseClass Rec, PoseClass WhatIf, float RecMoved), bool> pick)
        {
            var g = rows.Where(pick).ToList();
            if (g.Count == 0) { Console.WriteLine($"{title}: none"); return; }
            Console.WriteLine($"{title}: {g.Count} recordings, physics {F(Median(g.Select(r => r.Seconds)))} s (median)");
            Console.WriteLine($"  final pose class agrees: {g.Count(r => r.SameClass)} of {g.Count} ({100f * g.Count(r => r.SameClass) / g.Count:F0} %)");
            Console.WriteLine($"  pelvis position error, m — first physics frame: median {F(Median(g.Select(r => r.First)), "F3")}, max {F(g.Max(r => r.First), "F3")}; " +
                              $"at 1 s: median {F(Median(g.Select(r => r.At1)), "F3")}, p90 {F(Percentile(g.Select(r => r.At1), 0.9f), "F3")}, under 1 cm: {g.Count(r => r.At1 < 0.01f)}, under 5 cm: {g.Count(r => r.At1 < 0.05f)}; " +
                              $"at 3 s: median {F(Median(g.Select(r => r.At3)), "F3")}, p90 {F(Percentile(g.Select(r => r.At3), 0.9f), "F3")}, under 1 cm: {g.Count(r => r.At3 < 0.01f)}, under 5 cm: {g.Count(r => r.At3 < 0.05f)}; " +
                              $"at the end: median {F(Median(g.Select(r => r.End)), "F3")}, p90 {F(Percentile(g.Select(r => r.End), 0.9f), "F3")}, max {F(g.Max(r => r.End), "F3")}");
            Console.WriteLine($"  mean error over the 22 bones at the end, m: median {F(Median(g.Select(r => r.EndAll)), "F3")}, p90 {F(Percentile(g.Select(r => r.EndAll), 0.9f), "F3")}; " +
                              $"end error under 5 cm: {g.Count(r => r.End < 0.05f)}, under 20 cm: {g.Count(r => r.End < 0.20f)}, over 50 cm: {g.Count(r => r.End > 0.5f)}; " +
                              $"for scale, the recorded pelvis itself moved {F(Median(g.Select(r => r.RecMoved)), "F2")} m (median) from hand-off to the end");
            var confusions = g.Where(r => !r.SameClass).GroupBy(r => $"{r.Rec}→{r.WhatIf}").OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}");
            Console.WriteLine("  class differences (recording→what-if): " + string.Join(", ", confusions.DefaultIfEmpty("none")));
        }
        Group("all", _ => true);
        Group("no other character's collider, no grab or other outside call in the recording", r => r.Clean);
        Group("with colliders or outside calls", r => !r.Clean);
        Group("physics ran at least 3 s", r => r.Seconds >= 3f);
        Group("physics ran at least 3 s, no colliders or outside calls", r => r.Seconds >= 3f && r.Clean);
        Group("the player's recordings", r => r.Run.T.IsPlayer);
        Console.WriteLine("worst ten by pelvis error at the end:");
        foreach (var r in rows.OrderByDescending(r => r.End).Take(10))
            Console.WriteLine($"  {r.Run.Name}: {F(r.End, "F2")} m after {F(r.Seconds)} s, classes {r.Rec}/{r.WhatIf}, colliders {r.Run.Wi.GetValueOrDefault("recordedColliders")}, grabs {r.Run.Wi.GetValueOrDefault("recordedGrabs")}, other calls {r.Run.Wi.GetValueOrDefault("recordedOtherCalls")}, " +
                              $"rays answered exactly {r.Run.Wi.GetValueOrDefault("exactRays")} / from the field {r.Run.Wi.GetValueOrDefault("fieldRays")}");
        return 0;
    }

    private static PoseClass ClassAt(Run run, int lastFrame)
    {
        var t = run.T;
        var world = new RecordedWorld(t);
        var classifier = new PoseClassifier { Scale = run.Scale };
        var c = PoseClass.Standing;
        for (var i = run.HandOff; i <= lastFrame && i < t.Frames.Length; i++)
        {
            var points = t.Points(t.Frames[i].Bone);
            var ground = world.HasGround ? world.FieldHeight(points.Hips.X, points.Hips.Z) : t.Frames[i].GroundY;
            c = classifier.Update(BodyFrame.From(points, ground), t.Frames[i].Dt);
        }
        return c;
    }

    // ---- contact sheets ----

    /// <summary>
    /// One PNG per picked run set: the four profiles as rows of a side view (looking across the direction the Limp
    /// body ends up in, 3.6 m × 2 m, the ground as a line) and again as rows of a top view; the columns are the
    /// hand-off pose and 0.5 s, 1 s, 2 s, 4 s of physics and the end. The view is fixed on where the hips were at
    /// hand-off, so what moves in the picture moved in the world. Bones only.
    /// </summary>
    public static int SheetsRun(string root, int count, bool zoom = false)
    {
        var runs = Load(root, Profiles);
        var byKey = runs.GroupBy(r => (r.Name, r.Percent)).Where(g => Profiles.All(p => g.Any(r => r.Profile == p))).ToList();
        if (byKey.Count == 0)
        {
            Console.Error.WriteLine($"no complete what-if sets under {root}");
            return 2;
        }
        // Spread over the start classes; within one, the player's recordings first, the recorded start before the others, then by name. One sheet per recording.
        var picks = new List<IGrouping<(string Name, int Percent), Run>>();
        var used = new HashSet<string>();
        // A third lying, a third kneeling, the rest standing and mid-fall (for six: 2, 2, 1, 1).
        var third = Math.Max(1, count / 3);
        foreach (var (c, perClass) in new[] { ("lying", third), ("kneeling", third), ("standing", Math.Max(1, (count - 2 * third + 1) / 2)), ("mid-fall", Math.Max(1, (count - 2 * third) / 2)) })
        {
            var ofClass = byKey.Where(g => g.First(r => r.Profile == Profiles[0]).StartClass == c)
                .OrderByDescending(g => g.First().T.IsPlayer).ThenByDescending(g => g.Key.Percent == 100).ThenBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.Percent);
            var taken = 0;
            foreach (var g in ofClass)
            {
                if (taken >= perClass) break;
                if (!used.Add(g.Key.Name)) continue;
                picks.Add(g);
                taken++;
            }
        }
        var folder = Path.Combine(root, zoom ? "sheets-zoom" : "sheets");
        Directory.CreateDirectory(folder);
        var legend = new StringBuilder();
        legend.AppendLine($"rows 1-{Profiles.Length}: side view of {string.Join(", ", Profiles)}; rows {Profiles.Length + 1}-{Profiles.Length * 2}: the same from above");
        legend.AppendLine("columns: hand-off pose, then 0.5 s, 1 s, 2 s, 4 s of physics and the end of the run");
        legend.AppendLine(zoom
            ? "zoomed (2 m across a cell, twice the size), every cell centred on that body's own hips: the pose itself. Grey underneath = the first profile's body at the same moment, hips on hips. Spine white, left limbs blue, right limbs red, head a ring; grey line = the ground under the hips"
            : "spine white, left limbs blue, right limbs red, head a ring; grey line = the ground under the hips at hand-off; yellow cross = where the hips were at hand-off");
        var moments = new[] { 0f, 0.5f, 1f, 2f, 4f, float.MaxValue };
        var cellW = Sheets.CellW * (zoom ? 2 : 1);
        var cellH = Sheets.CellH * (zoom ? 2 : 1);
        foreach (var set in picks)
        {
            var limp = set.First(r => r.Profile == Profiles[0]);
            var t0 = limp.T;
            var hips0 = limp.Positions(limp.HandOff)[t0.BoneIndex("j_kosi")];
            var world = new RecordedWorld(t0);
            float Ground(Vector3 p) => world.HasGround ? world.FieldHeight(p.X, p.Z) : t0.Frames[limp.HandOff].GroundY;
            // Across the screen: where the first profile's chest ends up, seen from the hand-off hips (else its facing).
            var end = limp.Positions(t0.Frames.Length - 1)[t0.BoneIndex("j_sebo_c")] - hips0;
            end.Y = 0f;
            var u = end.Length() > 0.15f ? Vector3.Normalize(end) : new Vector3(MathF.Sin(t0.Facing), 0f, MathF.Cos(t0.Facing));
            var w = new Vector3(-u.Z, 0f, u.X);
            var image = new Sheets.Image((cellW + Sheets.Pad) * moments.Length + Sheets.Pad, (cellH + Sheets.Pad) * Profiles.Length * 2 + Sheets.Pad) { ClipW = cellW, ClipH = cellH };
            var scale = cellW / (zoom ? 2.0f : 3.6f);
            for (var p = 0; p < Profiles.Length; p++)
            {
                var run = set.First(r => r.Profile == Profiles[p]);
                for (var c = 0; c < moments.Length; c++)
                {
                    var frame = moments[c] == float.MaxValue ? run.T.Frames.Length - 1 : run.FrameAt(moments[c]);
                    var positions = run.T.Frames[frame].Bone; // joint origins: the stick figure's own joints
                    var limpFrame = moments[c] == float.MaxValue ? limp.T.Frames.Length - 1 : limp.FrameAt(moments[c]);
                    var limpPositions = limp.T.Frames[limpFrame].Bone;
                    for (var view = 0; view < 2; view++)
                    {
                        var x0 = Sheets.Pad + c * (cellW + Sheets.Pad);
                        var y0 = Sheets.Pad + (p + view * Profiles.Length) * (cellH + Sheets.Pad);
                        image.Fill(x0, y0, cellW, cellH, view == 0 ? 0xFF202428 : 0xFF181C20);
                        void Draw(Trajectory t, Vector3[] bones, bool ghost)
                        {
                            // Zoomed: centred on this body's own hips. Otherwise: fixed on the hand-off hips.
                            var centre = zoom ? bones[t.BoneIndex("j_kosi")] : hips0;
                            var ground = Ground(centre);
                            (float, float) Map(Vector3 q)
                            {
                                var d = q - centre;
                                var sx = x0 + cellW * 0.5f + Vector3.Dot(d, u) * scale;
                                var sy = view == 0
                                    ? y0 + cellH - 12f - (q.Y - ground) * scale
                                    : y0 + cellH * 0.5f - Vector3.Dot(d, w) * scale;
                                return (sx, sy);
                            }
                            if (!ghost)
                            {
                                if (view == 0) image.Line(x0, y0 + cellH - 12f, x0 + cellW, y0 + cellH - 12f, 0xFF55606A, x0, y0);
                                if (!zoom)
                                {
                                    var (cx, cy) = Map(view == 0 ? new Vector3(hips0.X, ground, hips0.Z) : hips0);
                                    image.Line(cx - 4, cy, cx + 4, cy, 0xFFE0C040, x0, y0);
                                    image.Line(cx, cy - 4, cx, cy + 4, 0xFFE0C040, x0, y0);
                                }
                            }
                            foreach (var (a, b, color) in Sheets.Segments)
                            {
                                var (ax, ay) = Map(bones[t.BoneIndex(a)]);
                                var (bx, by) = Map(bones[t.BoneIndex(b)]);
                                image.Line(ax, ay, bx, by, ghost ? 0xFF707880 : Sheets.Colors[color], x0, y0);
                            }
                            var head = bones[t.BoneIndex("j_kao")];
                            var neck = bones[t.BoneIndex("j_kubi")];
                            var (hx, hy) = Map(head + (head - neck) * 0.6f);
                            image.Ring(hx, hy, 0.11f * scale, ghost ? 0xFF707880 : 0xFFF0F0F0, x0, y0);
                        }
                        if (zoom && p > 0) Draw(limp.T, limpPositions, true);
                        Draw(run.T, positions, false);
                        image.Box(x0, y0, cellW, cellH, 0xFF606870);
                    }
                }
            }
            var file = Path.Combine(folder, $"{limp.StartClass}-{set.Key.Name}-s{set.Key.Percent}.png");
            image.Save(file);
            legend.AppendLine($"{Path.GetFileName(file)}: start class {limp.StartClass} ({limp.StartPose}), physics started {t0.Frames[limp.HandOff].Time:F2} s after death " +
                              $"({set.Key.Percent} % of the recorded animation), run {limp.Seconds:F1} s; end classes: " +
                              string.Join(", ", Profiles.Select(p => $"{p} {set.First(r => r.Profile == p).EndPose}")));
            Console.WriteLine(file);
        }
        File.WriteAllText(Path.Combine(folder, "legend.txt"), legend.ToString());
        Console.WriteLine(Path.Combine(folder, "legend.txt"));
        return 0;
    }
}
