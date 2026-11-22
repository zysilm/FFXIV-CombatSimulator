using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>
/// The metric run: every shot (without input, with "Allow below ground", with the player orbiting, and as the
/// framing of a held body) and the two baselines over every trajectory from eight start azimuths; the table of
/// docs/DEATH_SHOTS.md §8 and its gates.
/// </summary>
internal static class MetricRun
{
    private const float Deg = MathF.PI / 180f;
    private static bool Verbose;
    private const string NoOldSource = "Close, bodies only at the switch";

    private sealed class Column
    {
        public string Name = "";
        public bool IsNew, Hidden;
        /// <summary>Run with "Allow below ground" on: the clearance gates do not apply, the depth is reported instead.</summary>
        public bool Below;
        /// <summary>Run with the player orbiting (<see cref="Runners.Orbit"/>): the caps on the view's turn and the
        /// smoothness comparison are about the program's own moves and are not gated here.</summary>
        public bool Orbit;
        /// <summary>Run with a remembered angle (from the facing at death, radians): the shot turns to it at the
        /// start, which is a move of the program's own that is not under the 60 deg/s cap.</summary>
        public float? Recall;
        /// <summary>The column's own subject is the head-to-feet chain (the Directed camera's anchors: no arms).</summary>
        public bool OwnIsChain;
        public ShotSubject Subject;
        /// <summary>The shot is placed by the body's lowest point (the ground-line shots): measure where that point sits.</summary>
        public ShotRecipe? GroundLine;
        public Func<Trajectory, RecordedWorld, StartCamera, (Cam[] Cams, float Scale)> Run = null!;
        public readonly Acc A = new();
    }

    /// <summary>Everything measured for one column, pooled over its runs.</summary>
    private sealed class Acc
    {
        public long ChainIn, RestFrames, OwnInRest, Frames, OwnIn, OwnSafe, HipsChestIn, HeadIn, WholeIn, HeldIn, Below5, TriFrames, TriBlocked, PulledIn, CutFrames;
        public float MinClearance = float.MaxValue;
        public readonly List<float> Size = new(), EyeSpeed = new(), ViewSpeed = new(), Jerk = new(), ViewAccel = new(), FovRate = new();
        public readonly List<float> Travel = new(), EyeJump = new(), LookJump = new(), SettleLag = new(), Drift = new(), DistanceRange = new();
        public int Pops, OldStarts, OldLimited, Runs, SettleWindows, CameraNeverSettled, ShotChanges;
        public readonly List<float> JumpSlow = new(), JumpFast = new(), OldRaw = new(), OldRawPlayer = new(), OldUp = new(), OldDistance = new();
        /// <summary>The shot's azimuth against the start camera's plus the input (degrees, the largest of the run), and
        /// the rendered eye's azimuth about the rendered look-at point against the shot's: over the whole run, and
        /// with the body at rest.</summary>
        public float AzimuthOff, RenderedOff, RenderedOffAtRest;
        /// <summary>Recall columns: how far from facing + remembered the shot's azimuth is at the end of the run (runs
        /// longer than 3 s), how far the turns were, how long they took, how fast they went at most.</summary>
        public float RecallEndOff, RecallFastest;
        public readonly List<float> RecallTurn = new(), RecallTime = new();
        /// <summary>Below-ground columns: frames with the eye under the ground, how deep, and where Low close sits on
        /// a lying body at rest (looking-up angle, distance for the reference body).</summary>
        public long BelowGround, BelowGroundAtRest, LyingFrames;
        public readonly List<float> Depth = new(), LyingUp = new(), LyingDistance = new();
        /// <summary>Ground-line columns: the subject's lowest projected point above the frame's bottom edge (fraction of
        /// the frame's height; negative = below the edge), with the body at rest and while it falls.</summary>
        public readonly List<float> LowRest = new(), LowFall = new();

        public void Merge(Acc o)
        {
            BelowGround += o.BelowGround; BelowGroundAtRest += o.BelowGroundAtRest; LyingFrames += o.LyingFrames;
            Depth.AddRange(o.Depth); LyingUp.AddRange(o.LyingUp); LyingDistance.AddRange(o.LyingDistance);
            LowRest.AddRange(o.LowRest); LowFall.AddRange(o.LowFall);
            ChainIn += o.ChainIn; RestFrames += o.RestFrames; OwnInRest += o.OwnInRest; Frames += o.Frames; OwnIn += o.OwnIn; OwnSafe += o.OwnSafe; HipsChestIn += o.HipsChestIn; HeadIn += o.HeadIn; WholeIn += o.WholeIn; HeldIn += o.HeldIn;
            Below5 += o.Below5; TriFrames += o.TriFrames; TriBlocked += o.TriBlocked;
            PulledIn += o.PulledIn; CutFrames += o.CutFrames;
            MinClearance = MathF.Min(MinClearance, o.MinClearance);
            RecallEndOff = MathF.Max(RecallEndOff, o.RecallEndOff); RecallFastest = MathF.Max(RecallFastest, o.RecallFastest);
            RecallTurn.AddRange(o.RecallTurn); RecallTime.AddRange(o.RecallTime);
            AzimuthOff = MathF.Max(AzimuthOff, o.AzimuthOff); RenderedOff = MathF.Max(RenderedOff, o.RenderedOff); RenderedOffAtRest = MathF.Max(RenderedOffAtRest, o.RenderedOffAtRest);
            Size.AddRange(o.Size); EyeSpeed.AddRange(o.EyeSpeed); ViewSpeed.AddRange(o.ViewSpeed); Jerk.AddRange(o.Jerk); ViewAccel.AddRange(o.ViewAccel); FovRate.AddRange(o.FovRate);
            Travel.AddRange(o.Travel); EyeJump.AddRange(o.EyeJump); LookJump.AddRange(o.LookJump);
            SettleLag.AddRange(o.SettleLag); Drift.AddRange(o.Drift); DistanceRange.AddRange(o.DistanceRange);
            Pops += o.Pops; OldStarts += o.OldStarts; OldLimited += o.OldLimited; Runs += o.Runs; SettleWindows += o.SettleWindows; CameraNeverSettled += o.CameraNeverSettled; ShotChanges += o.ShotChanges;
            JumpSlow.AddRange(o.JumpSlow); JumpFast.AddRange(o.JumpFast); OldRaw.AddRange(o.OldRaw); OldRawPlayer.AddRange(o.OldRawPlayer); OldUp.AddRange(o.OldUp); OldDistance.AddRange(o.OldDistance);
        }
    }

    private static string Short(DeathShotProfile p) => p switch
    {
        DeathShotProfile.LowClose => "Close", DeathShotProfile.LowFace => "Face", DeathShotProfile.LowFullBody => "Full",
        DeathShotProfile.MidClose => "Mid", DeathShotProfile.MidFullBody => "Mid full", DeathShotProfile.HighClose => "High", DeathShotProfile.HighFullBody => "High full",
        DeathShotProfile.GroundLineClose => "GL close", DeathShotProfile.GroundLineFullBody => "GL full", _ => p.ToString(),
    };

    public static int Run(List<Trajectory> all, bool verbose)
    {
        Verbose = verbose;
        var usable = all.Where(t => t.Humanoid && t.Frames.Length >= 2).ToList();
        var columns = new List<Column>();
        foreach (var profile in DeathShotProfiles.Choices)
        {
            var p = profile;
            columns.Add(new Column
            {
                Name = Short(p), IsNew = true, Subject = ShotRecipe.Of(p).Subject,
                GroundLine = ShotRecipe.Of(p).Anchor == ShotAnchor.Lowest ? ShotRecipe.Of(p) : null,
                Run = (t, w, s) => { var c = Runners.New(t, w, s, p, out var scale); return (c, scale); },
            });
        }
        // The option "Allow below ground": the low shots (the others keep the eye above the ground either way).
        foreach (var profile in DeathShotProfiles.Choices.Where(p => DeathShotSolver.GoesBelow(ShotRecipe.Of(p))))
        {
            var p = profile;
            columns.Add(new Column
            {
                Name = Short(p) + " bg", IsNew = true, Below = true, Subject = ShotRecipe.Of(p).Subject,
                Run = (t, w, s) => { var c = Runners.New(t, w, s, p, out var scale, belowGround: true); return (c, scale); },
            });
        }
        // The player's hand: left and right about the body all through the death.
        foreach (var profile in new[] { DeathShotProfile.LowClose, DeathShotProfile.LowFullBody })
        {
            var p = profile;
            columns.Add(new Column
            {
                Name = Short(p) + " orbit", IsNew = true, Orbit = true, Subject = ShotRecipe.Of(p).Subject,
                Run = (t, w, s) => { var c = Runners.New(t, w, s, p, out var scale, yaw: Runners.Orbit); return (c, scale); },
            });
        }
        // A remembered angle (140 deg from the facing at death, behind and to one side): the shot turns to it.
        columns.Add(new Column
        {
            Name = "Close recall", IsNew = true, Recall = 140f * Deg, Subject = ShotSubject.HipsToChest,
            Run = (t, w, s) => { var c = Runners.New(t, w, s, DeathShotProfile.LowClose, out var scale, remembered: 140f * Deg); return (c, scale); },
        });
        // The framings of a held body, on the recorded (falling) bodies.
        foreach (var framing in new[] { DeathShotGrabbedFraming.Level, DeathShotGrabbedFraming.SlightlyAbove })
        {
            var g = framing;
            columns.Add(new Column
            {
                Name = g == DeathShotGrabbedFraming.Level ? "Grab level" : "Grab above", IsNew = true, Subject = ShotSubject.HeldBody,
                Run = (t, w, s) => { var c = Runners.New(t, w, s, DeathShotProfile.LowClose, out var scale, grabbed: g); return (c, scale); },
            });
        }
        columns.Add(new Column
        {
            Name = NoOldSource, IsNew = true, Hidden = true, Subject = ShotSubject.HipsToChest,
            Run = (t, w, s) => { var c = Runners.New(t, w, s, DeathShotProfile.LowClose, out var scale, oldSourceAtSwitch: false); return (c, scale); },
        });
        columns.Add(new Column { Name = "Directed", Subject = ShotSubject.WholeBody, OwnIsChain = true, Run = (t, w, s) => (Runners.Directed(t, w, s), 1f) });
        foreach (var shot in Runners.OldShots)
        {
            var o = shot;
            columns.Add(new Column { Name = "DC-" + o.Name, Subject = ShotSubject.HipsToChest, Run = (t, _, s) => (Runners.OldDeathCam(t, s, o), 1f) });
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new ConcurrentBag<(int Column, Acc A)>();
        Parallel.ForEach(usable, t =>
        {
            var world = new RecordedWorld(t);
            for (var c = 0; c < columns.Count; c++)
            {
                var acc = new Acc();
                for (var k = 0; k < 8; k++)
                {
                    var start = StartCamera.For(t, k);
                    var (cams, scale) = columns[c].Run(t, world, start);
                    Measure(t, world, start, cams, scale, columns[c], acc);
                }
                parts.Add((c, acc));
            }
        });
        foreach (var (c, a) in parts.OrderBy(p => p.Column)) columns[c].A.Merge(a);
        var hidden = columns.First(c => c.Hidden).A;
        columns.RemoveAll(c => c.Hidden);
        Console.WriteLine($"metric run: {usable.Count} trajectories x 8 start azimuths x {columns.Count} columns, {columns[0].A.Frames} frames per column, {clock.Elapsed.TotalSeconds:F1} s");

        PrintTable(columns);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ragdoll start without the bones on the switch frame (the solver carries each point on at its speed): jump max {(hidden.JumpFast.Count > 0 ? hidden.JumpFast.Max() * 100f : 0f):F2} cm at 20 fps or better ({hidden.JumpFast.Count} runs), {(hidden.JumpSlow.Count > 0 ? hidden.JumpSlow.Max() * 100f : 0f):F2} cm in the recordings at 10 fps ({hidden.JumpSlow.Count} runs)"));
        return Gates(columns);
    }

    /// <summary>
    /// One hash per run (trajectory × shot × start azimuth, default options, no input) over every output pose (eye,
    /// look-at, FoV, bit for bit). <paramref name="save"/> writes the list; <paramref name="against"/> compares with
    /// a saved one: a change that is meant to leave the shots alone must reproduce every run of the saved list.
    /// </summary>
    public static int Hash(List<Trajectory> all, string? save, string? against)
    {
        var usable = all.Where(t => t.Humanoid && t.Frames.Length >= 2).ToList();
        var profiles = DeathShotProfiles.Choices;
        var lines = new string[usable.Count * 8 * profiles.Length];
        Parallel.For(0, usable.Count, i =>
        {
            var t = usable[i];
            var world = new RecordedWorld(t);
            for (var pi = 0; pi < profiles.Length; pi++)
            for (var k = 0; k < 8; k++)
            {
                var h = 14695981039346656037UL;
                void Mix(float v) { h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL; }
                var cams = Runners.New(t, world, StartCamera.For(t, k), profiles[pi], out _);
                foreach (var c in cams)
                {
                    Mix(c.Eye.X); Mix(c.Eye.Y); Mix(c.Eye.Z); Mix(c.LookAt.X); Mix(c.LookAt.Y); Mix(c.LookAt.Z); Mix(c.Fov);
                }
                lines[(i * profiles.Length + pi) * 8 + k] = $"{t.Name}/{profiles[pi]}|{k}|{h:X16}|{cams.Length}";
            }
        });
        if (save != null)
        {
            File.WriteAllLines(save, lines);
            Console.WriteLine($"{lines.Length} runs (every shot) saved to {save}");
            return 0;
        }
        if (against == null)
        {
            foreach (var l in lines) Console.WriteLine(l);
            return 0;
        }
        var old = File.ReadAllLines(against).Select(l => l.Split('|')).ToDictionary(p => p[0] + "|" + p[1]);
        int compared = 0, identical = 0;
        foreach (var l in lines)
        {
            var p = l.Split('|');
            if (!old.TryGetValue(p[0] + "|" + p[1], out var o)) continue;
            compared++;
            if (o[2] == p[2]) identical++;
            else Console.WriteLine($"  DIFFERENT {p[0]} start {p[1]}");
        }
        Console.WriteLine($"runs in both lists: {compared} of {lines.Length}, bit-identical {identical}.");
        var ok = compared > 0 && identical == compared;
        Console.WriteLine(ok ? "== HASH PASS" : "== HASH FAIL");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ one run

    private static void Measure(Trajectory t, RecordedWorld world, StartCamera start, Cam[] cams, float scale, Column column, Acc a)
    {
        a.Runs++;
        var n = cams.Length;
        var whole = new List<Vector3>(24);
        int kneeL = t.BoneIndex("j_asi_b_l"), kneeR = t.BoneIndex("j_asi_b_r");
        var settle = new SettleDetector();
        var wasPhysics = false;
        var bodySettled = false;
        var windowStart = -1;        // frame the body settled
        var cameraSettledAt = -1;
        var quiet = 0f;
        Vector3 restEye = default;
        float drift = 0f, minDistance = float.MaxValue, maxDistance = float.MinValue;
        var travel = 0f;
        Vector3 prevEye = default, prevDir = default, prevVel = default, prevAcc = default;
        float prevViewSpeed = 0f, prevFov = 0f;
        var history = 0; // consecutive frames without a cut, for the differences
        var lostAtRest = 0;
        var reported = false;
        void CloseWindow(float time)
        {
            if (windowStart < 0) return;
            a.SettleWindows++;
            if (cameraSettledAt >= 0)
            {
                a.SettleLag.Add(t.Frames[cameraSettledAt].Time - t.Frames[windowStart].Time);
                a.Drift.Add(drift);
                a.DistanceRange.Add(maxDistance - minDistance);
            }
            else if (time - t.Frames[windowStart].Time > 4f) a.CameraNeverSettled++;
            windowStart = cameraSettledAt = -1;
        }

        for (var i = 0; i < n; i++)
        {
            var f = t.Frames[i];
            var cam = cams[i];
            var dt = MathF.Max(f.Dt, 1e-4f);
            var p = t.Points(f.Runtime);
            var toLook = cam.LookAt - cam.Eye;
            var dir = toLook.LengthSquared() > 1e-10f ? Vector3.Normalize(toLook) : Vector3.UnitZ;
            DynamicCameraSolver.BasisFromForward(dir, out var right, out var up);
            var tanV = MathF.Tan(cam.Fov * 0.5f);
            var lens = new DynamicCameraSolver.Lens(tanV * Runners.Aspect, tanV);
            a.Frames++;

            // framing
            var ground = world.FieldHeight(p.Hips.X, p.Hips.Z);
            var headAxis = p.Head - p.Chest;
            headAxis = headAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(headAxis) : Vector3.UnitY;
            var headCentre = p.Head + headAxis * (0.09f * scale);
            whole.Clear();
            foreach (var b in f.Runtime) whole.Add(new Vector3(b.X, MathF.Max(b.Y, ground + 0.03f), b.Z));
            var top = p.Head + headAxis * (0.18f * scale);
            whole.Add(new Vector3(top.X, MathF.Max(top.Y, ground + 0.03f), top.Z));
            var hipsChest = Span(stackalloc[] { p.Hips, p.Chest }, cam.Eye, dir, right, up, lens);
            var head = Span(stackalloc[] { headCentre }, cam.Eye, dir, right, up, lens);
            var body = Span(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(whole), cam.Eye, dir, right, up, lens);
            // A held body: the top of the head and the chest.
            var held = Span(stackalloc[] { top, p.Chest }, cam.Eye, dir, right, up, lens);
            if (hipsChest.Reach <= 1f) a.HipsChestIn++;
            if (head.Reach <= 1f) a.HeadIn++;
            if (body.Reach <= 1f) a.WholeIn++;
            if (held.Reach <= 1f) a.HeldIn++;
            // The Directed camera's own anchors: head top, head, chest, hips, knees, feet (no arms).
            Vector3 Lifted(Vector3 v) => new(v.X, MathF.Max(v.Y, ground + 0.03f), v.Z);
            var chain = Span(stackalloc[]
            {
                Lifted(top), Lifted(p.Head), Lifted(p.Chest), Lifted(p.Hips), Lifted(f.Runtime[kneeL]), Lifted(f.Runtime[kneeR]), Lifted(p.FootL), Lifted(p.FootR),
            }, cam.Eye, dir, right, up, lens);
            if (chain.Reach <= 1f) a.ChainIn++;
            var subject = column.Subject;
            var own = column.OwnIsChain ? chain : subject switch { ShotSubject.HipsToChest => hipsChest, ShotSubject.Head => head, ShotSubject.HeldBody => held, _ => body };
            if (own.Reach <= 1f) a.OwnIn++;
            if (bodySettled) { a.RestFrames++; if (own.Reach <= 1f) a.OwnInRest++; else lostAtRest++; }
            if (column.GroundLine is { } line)
            {
                // The lowest point as the shot takes it: each subject point less its radius, nothing under the ground.
                var lowest = float.MaxValue;
                void Low(Vector3 v, float radius)
                {
                    var q = new Vector3(v.X, MathF.Max(v.Y - radius, MathF.Min(v.Y, ground + 0.03f)), v.Z);
                    var ndc = DynamicCameraSolver.Project(q, cam.Eye, dir, right, up, lens);
                    if (ndc.Z > 0.05f) lowest = MathF.Min(lowest, (ndc.Y + 1f) * 0.5f);
                }
                if (line.Subject == ShotSubject.HipsToChest)
                {
                    Low(p.Hips, 0.16f * scale);
                    Low(p.Chest, 0.16f * scale);
                }
                else
                {
                    foreach (var b in whole) Low(b, 0.10f * scale); // (lifted out of the ground, as the shot takes them)
                    // (the head top is in the lifted list)
                }
                if (lowest != float.MaxValue) (bodySettled ? a.LowRest : a.LowFall).Add(lowest);
            }
            if (own.Reach <= 0.92f) a.OwnSafe++;
            // Subject size: how much of the frame its extent takes, the larger of the two axes; a single point
            // (the head) by the head's size at its depth.
            a.Size.Add(subject == ShotSubject.Head ? 0.24f * scale / (2f * MathF.Max(0.05f, head.Depth) * tanV) : MathF.Max(own.SpanX, own.SpanY) * 0.5f);

            // the azimuth: the shot's against the start camera's plus the hand's; the rendered eye's against the shot's
            if (cam.HasDiagnostics)
            {
                a.AzimuthOff = MathF.Max(a.AzimuthOff, MathF.Abs(DeathShotMath.AngleDelta(start.Azimuth + cam.RecallOffset + cam.UserYaw, cam.Azimuth)) / Deg);
                if (i > 0 && cams[i - 1].Recalling)
                    a.RecallFastest = MathF.Max(a.RecallFastest, MathF.Abs(cam.RecallOffset - cams[i - 1].RecallOffset) / MathF.Max(MathF.Min(f.Dt, 0.1f), 1e-4f) / Deg);
                var away = cam.Eye - cam.LookAt;
                if (away.X * away.X + away.Z * away.Z > 1e-4f)
                {
                    var off = MathF.Abs(DeathShotMath.AngleDelta(cam.Azimuth, DeathShotMath.Azimuth(away))) / Deg;
                    a.RenderedOff = MathF.Max(a.RenderedOff, off);
                    if (cameraSettledAt >= 0) a.RenderedOffAtRest = MathF.Max(a.RenderedOffAtRest, off);
                    if (off > 5f && Verbose && !reported)
                    {
                        reported = true;
                        lock (Console.Out) Console.WriteLine($"    {column.Name} {t.Name} start {start.Azimuth / Deg:F0}: rendered azimuth {off:F1} deg off at {f.Time:F1} s, eye-to-look {away.Length():F2} m{(cam.PulledIn ? ", pulled in" : "")}{(f.PhysicsStarted ? ", ragdoll" : "")}");
                    }
                }
            }

            // clearance and blocked view
            var under = world.GroundBelow(cam.Eye + new Vector3(0f, DeathShotSolver.GroundRayLift, 0f)) ?? ground;
            var clearance = cam.Eye.Y - under;
            a.MinClearance = MathF.Min(a.MinClearance, clearance);
            if (clearance < 0.05f) a.Below5++;
            if (clearance < 0f)
            {
                a.BelowGround++;
                if (bodySettled) a.BelowGroundAtRest++;
                a.Depth.Add(-clearance);
            }
            // Low close on a lying body at rest: where the shot aims (the old presets looked up 19-26 deg from
            // 1.0-1.35 m; with the eye kept above the ground that cannot be had on a body on the ground).
            if (bodySettled && cam.HasDiagnostics && cam.Shot == DeathShotProfile.LowClose && PoseClassifier.IsLying(cam.Pose) && !cam.PulledIn)
            {
                var aim = cam.GoalEye - cam.GoalLookAt;
                var reach = aim.Length();
                if (reach > 1e-3f)
                {
                    a.LyingFrames++;
                    a.LyingUp.Add(-MathF.Asin(Math.Clamp(aim.Y / reach, -1f, 1f)) / Deg);
                    a.LyingDistance.Add(reach / scale);
                }
            }
            if (world.HasTriangles)
            {
                a.TriFrames++;
                var centre = subject switch { ShotSubject.HipsToChest => (p.Hips + p.Chest) * 0.5f, ShotSubject.Head => headCentre, ShotSubject.HeldBody => p.Chest, _ => p.Hips };
                if (world.BlockedByTriangles(cam.Eye, centre)) a.TriBlocked++;
            }
            if (cam.PulledIn) a.PulledIn++;

            // motion
            if (cam.Cut && i > 0) { a.CutFrames++; history = 0; }
            if (i > 0 && history >= 1)
            {
                var vel = (cam.Eye - prevEye) / dt;
                a.EyeSpeed.Add(vel.Length());
                var viewSpeed = MathF.Acos(Math.Clamp(Vector3.Dot(dir, prevDir), -1f, 1f)) / dt / Deg;
                a.ViewSpeed.Add(viewSpeed);
                a.FovRate.Add(MathF.Abs(cam.Fov - prevFov) / dt);
                var flatNow = MathF.Atan2(-dir.X, -dir.Z);
                var flatPrev = MathF.Atan2(-prevDir.X, -prevDir.Z);
                travel += MathF.Abs(DeathShotMath.AngleDelta(flatPrev, flatNow)) / Deg;
                if (history >= 2)
                {
                    var acc = (vel - prevVel) / dt;
                    a.ViewAccel.Add(MathF.Abs(viewSpeed - prevViewSpeed) / dt);
                    if (history >= 3) a.Jerk.Add(((acc - prevAcc) / dt).Length());
                    prevAcc = acc;
                }
                prevVel = vel;
                prevViewSpeed = viewSpeed;
            }
            // the jump at ragdoll start: the change of the frame-to-frame step where the position source switches
            if (f.PhysicsStarted && !wasPhysics && i >= 2 && !cam.Cut)
            {
                // The same run with this one frame still read from the bones: the difference is what the change of
                // source did to the picture (the body's own motion is in both).
                var control = column.Run(t.WithoutSwitchAt(i), world, start).Cams[i];
                a.EyeJump.Add(Vector3.Distance(cam.Eye, control.Eye));
                var controlDir = Vector3.Normalize(control.LookAt - control.Eye);
                a.LookJump.Add(Vector3.Distance(cam.Eye + dir, control.Eye + controlDir));
                (f.Dt > 0.05f ? a.JumpSlow : a.JumpFast).Add(MathF.Max(a.EyeJump[^1], a.LookJump[^1]));
            }
            prevEye = cam.Eye;
            prevDir = dir;
            prevFov = cam.Fov;
            history++;

            // settle: the body (same detector as the solver), then the camera
            if (f.PhysicsStarted && !wasPhysics) { settle.Reset(); bodySettled = false; }
            wasPhysics = f.PhysicsStarted;
            var settledNow = f.PhysicsStarted && settle.Update(p, f.Dt);
            if (settledNow && !bodySettled)
            {
                windowStart = i;
                cameraSettledAt = -1;
                quiet = 0f;
            }
            else if (!settledNow && bodySettled)
            {
                CloseWindow(f.Time);
            }
            bodySettled = settledNow;
            if (windowStart >= 0 && i > windowStart)
            {
                if (cameraSettledAt < 0)
                {
                    var speed = (cam.BaseEye - cams[i - 1].BaseEye).Length() / dt;
                    quiet = speed < 0.002f ? quiet + dt : 0f;
                    if (quiet >= 0.5f)
                    {
                        cameraSettledAt = i;
                        restEye = cam.BaseEye;
                        drift = 0f;
                        minDistance = float.MaxValue;
                        maxDistance = float.MinValue;
                    }
                }
                if (cameraSettledAt >= 0)
                {
                    drift = MathF.Max(drift, Vector3.Distance(cam.BaseEye, restEye));
                    var d = Vector3.Distance(cam.BaseEye, p.Hips);
                    minDistance = MathF.Min(minDistance, d);
                    maxDistance = MathF.Max(maxDistance, d);
                }
            }

            // the old presets: where Low close aims on a standing or kneeling body, on the first frame
            if (i == 0 && cam.HasDiagnostics && cam.Shot == DeathShotProfile.LowClose && cam.Pose is PoseClass.Standing or PoseClass.Kneeling && column.Name == "Close")
            {
                var away = cam.GoalEye - cam.GoalLookAt;
                var d = away.Length();
                a.OldStarts++;
                // Where the ground stops the eye (a body already low, a bank on that side) the shot gives way by design.
                var upGoal = -MathF.Asin(Math.Clamp(away.Y / d, -1f, 1f));
                if (MathF.Abs(upGoal + ShotRecipe.LowClose.Elevation) > 0.5f * Deg || cam.PulledIn) { a.OldLimited++; continue; }
                a.OldUp.Add(upGoal / Deg);
                a.OldDistance.Add(d / scale);
                a.OldRaw.Add(d);
                if (t.IsPlayer) a.OldRawPlayer.Add(d);
            }
        }
        CloseWindow(t.Frames[^1].Time);
        a.Travel.Add(travel);
        if (column.Recall is { } remembered && t.Frames[^1].Time > 3f)
        {
            a.RecallEndOff = MathF.Max(a.RecallEndOff, MathF.Abs(DeathShotMath.AngleDelta(t.Facing + remembered, cams[^1].Azimuth)) / Deg);
            a.RecallTurn.Add(MathF.Abs(cams[^1].RecallOffset) / Deg);
            var done = Array.FindIndex(cams, c => !c.Recalling);
            if (done >= 0) a.RecallTime.Add(t.Frames[done].Time);
        }
        if (cams[^1].HasDiagnostics)
        {
            a.Pops += cams[^1].Pops;
            a.ShotChanges += cams[^1].ShotChanges;
            if (lostAtRest > 30 && Verbose)
                lock (Console.Out) Console.WriteLine($"    {column.Name} {t.Name} start {start.Azimuth / Deg:F0}: subject out of frame on {lostAtRest} frames with the body at rest");
        }
    }

    private readonly record struct Extent(float Reach, float SpanX, float SpanY, float Depth);

    /// <summary>How far the points reach toward the frame edge (1 = the edge; above 1 or behind the camera = out), and
    /// their extent across and up the frame as a fraction of it.</summary>
    private static Extent Span(ReadOnlySpan<Vector3> points, Vector3 eye, Vector3 fwd, Vector3 right, Vector3 up, DynamicCameraSolver.Lens lens)
    {
        float reach = 0f, minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, depth = 0f;
        foreach (var p in points)
        {
            var ndc = DynamicCameraSolver.Project(p, eye, fwd, right, up, lens);
            if (ndc.Z < 0.05f) { reach = float.MaxValue; continue; }
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(ndc.X), MathF.Abs(ndc.Y)));
            minX = MathF.Min(minX, ndc.X); maxX = MathF.Max(maxX, ndc.X);
            minY = MathF.Min(minY, ndc.Y); maxY = MathF.Max(maxY, ndc.Y);
            depth = ndc.Z;
        }
        return new Extent(reach, MathF.Max(0f, maxX - minX), MathF.Max(0f, maxY - minY), depth);
    }

    // ------------------------------------------------------------------ report

    private static void PrintTable(List<Column> columns)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(string Label, Func<Column, string> Value)>
        {
            ("own subject in frame %", c => Pct(c.A.OwnIn, c.A.Frames)),
            ("own subject in frame %, body at rest", c => Pct(c.A.OwnInRest, c.A.RestFrames)),
            ("own subject in safe frame (0.92) %", c => Pct(c.A.OwnSafe, c.A.Frames)),
            ("hips+chest in frame %", c => Pct(c.A.HipsChestIn, c.A.Frames)),
            ("head in frame %", c => Pct(c.A.HeadIn, c.A.Frames)),
            ("head top and chest in frame %", c => Pct(c.A.HeldIn, c.A.Frames)),
            ("head-to-feet chain in frame % (no arms)", c => Pct(c.A.ChainIn, c.A.Frames)),
            ("whole body in frame % (with arms)", c => Pct(c.A.WholeIn, c.A.Frames)),
            ("min eye clearance m", c => c.A.MinClearance.ToString("F3", inv)),
            ("frames below 5 cm clearance", c => c.A.Below5.ToString(inv)),
            ("eye below ground % of frames (body at rest)", c => $"{Pct(c.A.BelowGround, c.A.Frames)} ({Pct(c.A.BelowGroundAtRest, c.A.RestFrames)})"),
            ("eye depth below ground median / max m", c => c.A.Depth.Count == 0 ? "-" : $"{Stats.P(c.A.Depth, .5f):F2}/{c.A.Depth.Max():F2}"),
            ("Low close, lying at rest: looking up deg p5..p95", c => c.A.LyingUp.Count == 0 ? "-" : $"{Stats.P(c.A.LyingUp, .05f):F1}..{Stats.P(c.A.LyingUp, .95f):F1}"),
            ("ground line: lowest pt at rest med/p95dev/min", c => c.A.LowRest.Count == 0 ? "-" : LowStats(c.A.LowRest, c.GroundLine!.AnchorAt)),
            ("  while falling", c => c.A.LowFall.Count == 0 ? "-" : LowStats(c.A.LowFall, c.GroundLine!.AnchorAt)),
            ("  distance m (reference body) p5..p95", c => c.A.LyingDistance.Count == 0 ? "-" : $"{Stats.P(c.A.LyingDistance, .05f):F2}..{Stats.P(c.A.LyingDistance, .95f):F2}"),
            ("view blocked by ground tris % (player)", c => Pct(c.A.TriBlocked, c.A.TriFrames)),
            ("subject size p5 / p95 (frame fraction)", c => $"{Stats.P(c.A.Size, .05f):F2}/{Stats.P(c.A.Size, .95f):F2}"),
            ("eye speed p99 / max m/s", c => $"{Stats.P(c.A.EyeSpeed, .99f):F2}/{c.A.EyeSpeed.Max():F2}"),
            ("view angular speed p99 / max deg/s", c => $"{Stats.P(c.A.ViewSpeed, .99f):F1}/{c.A.ViewSpeed.Max():F1}"),
            ("view angular accel p99 deg/s2", c => $"{Stats.P(c.A.ViewAccel, .99f):F0}"),
            ("eye jerk p99 m/s3", c => $"{Stats.P(c.A.Jerk, .99f):F0}"),
            ("FoV rate max rad/s", c => $"{c.A.FovRate.Max():F2}"),
            ("shot azimuth off (start camera + hand) max deg", c => c.IsNew ? c.A.AzimuthOff.ToString("F4", inv) : "-"),
            ("rendered eye azimuth off the shot's max deg", c => c.IsNew ? c.A.RenderedOff.ToString("F2", inv) : "-"),
            ("  once the camera has settled", c => c.IsNew ? c.A.RenderedOffAtRest.ToString("F3", inv) : "-"),
            ("view yaw travel per death deg mean / max", c => $"{c.A.Travel.Average():F0}/{c.A.Travel.Max():F0}"),
            ("cut frames (not eye-in-terrain)", c => c.IsNew ? (c.A.CutFrames - c.A.Pops).ToString(inv) : "-"),
            ("eye found inside terrain, put on top (frames)", c => c.IsNew ? c.A.Pops.ToString(inv) : "-"),
            ("ragdoll-start eye jump max cm", c => c.A.EyeJump.Count == 0 ? "-" : $"{c.A.EyeJump.Max() * 100f:F2}"),
            ("ragdoll-start view jump max cm at 1 m", c => c.A.LookJump.Count == 0 ? "-" : $"{c.A.LookJump.Max() * 100f:F2}"),
            ("body settled -> camera settled s median / p95", c => c.A.SettleLag.Count == 0 ? "-" : $"{Stats.P(c.A.SettleLag, .5f):F1}/{Stats.P(c.A.SettleLag, .95f):F1}"),
            ("camera never settled (windows > 4 s)", c => $"{c.A.CameraNeverSettled}/{c.A.SettleWindows}"),
            ("drift after camera settled max mm", c => c.A.Drift.Count == 0 ? "-" : $"{c.A.Drift.Max() * 1000f:F1}"),
            ("settled distance range max cm", c => c.A.DistanceRange.Count == 0 ? "-" : $"{c.A.DistanceRange.Max() * 100f:F1}"),
            ("frames pulled in %", c => c.IsNew ? Pct(c.A.PulledIn, c.A.Frames) : "-"),
        };
        var width = rows.Max(r => r.Label.Length);
        var text = new StringBuilder();
        text.Append("| ").Append("metric".PadRight(width)).Append(" |");
        foreach (var c in columns) text.Append(' ').Append(c.Name.PadLeft(12)).Append(" |");
        text.AppendLine();
        text.Append("|").Append(new string('-', width + 2)).Append("|");
        foreach (var _ in columns) text.Append(new string('-', 13)).Append(":|");
        text.AppendLine();
        foreach (var (label, value) in rows)
        {
            text.Append("| ").Append(label.PadRight(width)).Append(" |");
            foreach (var c in columns) text.Append(' ').Append(value(c).PadLeft(12)).Append(" |");
            text.AppendLine();
        }
        Console.Write(text.ToString());
        var low = columns[0].A;
        if (low.OldUp.Count > 0)
            Console.WriteLine(string.Create(inv, $"old presets (Low close, standing/kneeling at the start: {low.OldStarts} runs, {low.OldLimited} of them limited by the ground or a wall and left out): " +
                              $"looking up {low.OldUp.Min():F1}..{low.OldUp.Max():F1} deg, distance {low.OldDistance.Min():F2}..{low.OldDistance.Max():F2} m for the reference body ({low.OldRaw.Min():F2}..{low.OldRaw.Max():F2} m as built: {Stats.P(low.OldRawPlayer, .5f):F2} m on the player's own body)"));
    }

    /// <summary>Where the lowest point sits: median, p95 of its distance from the shot's line, lowest.</summary>
    private static string LowStats(List<float> v, float at) =>
        string.Create(CultureInfo.InvariantCulture, $"{Stats.P(v, .5f):F3}/{Stats.P(v.Select(x => MathF.Abs(x - at)).ToList(), .95f):F3}/{v.Min():F2}");

    private static string Pct(long part, long total) => total == 0 ? "-" : (100.0 * part / total).ToString("F1", CultureInfo.InvariantCulture);

    private static int Gates(List<Column> columns)
    {
        var failures = 0;
        void Gate(string name, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!ok) failures++;
        }
        var directed = columns.First(c => c.Name == "Directed").A;
        var old = new Acc();
        foreach (var c in columns.Where(c => c.Name.StartsWith("DC-", StringComparison.Ordinal))) old.Merge(c.A);
        double In(Acc a, ShotSubject s) => 100.0 * (s switch { ShotSubject.HipsToChest => a.HipsChestIn, ShotSubject.Head => a.HeadIn, ShotSubject.HeldBody => a.HeldIn, _ => a.WholeIn }) / a.Frames;
        Console.WriteLine("gates (baselines: Directed = the Directed camera's death framing; Old = the three old Death Cam shots pooled):");
        foreach (var c in columns.Where(c => c.IsNew))
        {
            var a = c.A;
            Console.WriteLine($" {c.Name}");
            if (c.Below)
            {
                // Under the ground on purpose: no clearance gate. How deep, how often, and what it buys.
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  below ground allowed: eye under the ground on {Pct(a.BelowGround, a.Frames)}% of frames ({Pct(a.BelowGroundAtRest, a.RestFrames)}% with the body at rest), " +
                    $"depth median {(a.Depth.Count > 0 ? Stats.P(a.Depth, .5f) : 0f):F2} m, max {(a.Depth.Count > 0 ? a.Depth.Max() : 0f):F2} m; eye found inside terrain {a.Pops} time(s)"));
                if (c.Name.StartsWith("Close", StringComparison.Ordinal))
                    Gate("old presets on a lying body at rest: looking up 19-26 deg from 1.0-1.35 m (reference body)",
                        a.LyingUp.Count > 0 && a.LyingUp.Min() >= 19f && a.LyingUp.Max() <= 26f && a.LyingDistance.Min() >= 1.0f && a.LyingDistance.Max() <= 1.35f,
                        a.LyingUp.Count == 0 ? "no lying frames" : $"up {a.LyingUp.Min():F1}..{a.LyingUp.Max():F1} deg, distance {a.LyingDistance.Min():F2}..{a.LyingDistance.Max():F2} m over {a.LyingFrames} frames");
            }
            else
                Gate("frames below 5 cm clearance = 0", a.Below5 == 0, $"{a.Below5} (min clearance {a.MinClearance:F3} m)");
            Gate("no cuts: every change is eased (the only jumps left: the eye found inside terrain)", a.CutFrames - a.Pops == 0,
                $"{a.CutFrames - a.Pops} cut frames; eye in terrain {a.Pops}; changes of shot {a.ShotChanges}");
            Gate(c.Orbit
                    ? "the azimuth is the player's: the shot's azimuth = the start camera's + the hand's on every frame (one to one)"
                    : c.Recall.HasValue
                        ? "no azimuth change without input other than the turn to the remembered angle: the shot's azimuth = the start camera's + that turn on every frame"
                        : "no azimuth change without input (unless a remembered angle is being eased to: none here): the shot's azimuth = the start camera's on every frame, through fall, ragdoll and settle",
                a.AzimuthOff <= 1e-3f && (c.Recall.HasValue || a.RecallFastest == 0f), $"off by at most {a.AzimuthOff:F5} deg over {a.Frames} frames");
            if (c.Recall.HasValue)
                Gate("the turn ends on facing at death + the remembered angle, the shorter way round, at no more than 150 deg/s",
                    a.RecallTurn.Count > 0 && a.RecallEndOff < 1e-3f && a.RecallTurn.Max() <= 180.01f && a.RecallFastest <= 151f,
                    a.RecallTurn.Count == 0 ? "no run" : $"{a.RecallTurn.Count} deaths: off by at most {a.RecallEndOff:F5} deg at the end; turns of {a.RecallTurn.Min():F0}..{a.RecallTurn.Max():F0} deg (median {Stats.P(a.RecallTurn, .5f):F0}), done after {a.RecallTime.Min():F2}..{a.RecallTime.Max():F2} s, fastest {a.RecallFastest:F0} deg/s");
            if (c.GroundLine is { } line)
            {
                // The lowest point on its line (a fraction of the frame's height above the bottom edge), with the body at rest.
                var median = a.LowRest.Count > 0 ? Stats.P(a.LowRest, .5f) : float.NaN;
                var dev = a.LowRest.Count > 0 ? Stats.P(a.LowRest.Select(x => MathF.Abs(x - line.AnchorAt)).ToList(), .95f) : float.NaN;
                Gate($"the body's lowest point on the bottom edge at rest ({line.AnchorAt:F2} of the frame's height): median within 0.02, p95 off it <= 0.10",
                    MathF.Abs(median - line.AnchorAt) <= 0.02f && dev <= 0.10f,
                    string.Create(CultureInfo.InvariantCulture, $"median {median:F3}, p95 off {dev:F3}, lowest {(a.LowRest.Count > 0 ? a.LowRest.Min() : 0f):F2}; while falling median {(a.LowFall.Count > 0 ? Stats.P(a.LowFall, .5f) : 0f):F3}, p95 off {(a.LowFall.Count > 0 ? Stats.P(a.LowFall.Select(x => MathF.Abs(x - line.AnchorAt)).ToList(), .95f) : 0f):F3}"));
            }            // (The rendered eye can trail the shot's azimuth while the caps on its speed and on the view's turn hold
            // it back in a fast fall; once it has settled it is on it.)
            if (!c.Orbit)
                Gate("the rendered eye is on the shot's azimuth once the camera has settled on a body at rest (< 0.1 deg)", a.RenderedOffAtRest < 0.1f,
                    $"settled {a.RenderedOffAtRest:F3} deg; over all frames (the speed caps at work) at most {a.RenderedOff:F2} deg");
            else
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  rendered eye azimuth against the hand's: at most {a.RenderedOff:F2} deg off; view up to {a.ViewSpeed.Max():F0} deg/s, eye up to {a.EyeSpeed.Max():F2} m/s (the hand's own speed is not capped)"));
            if (!c.Orbit && !c.Recall.HasValue)
                Gate("view angular speed <= 60 deg/s outside cuts", a.ViewSpeed.Max() <= 60.5f, $"max {a.ViewSpeed.Max():F1}");
            Gate("ragdoll-start jump < 2 cm", a.EyeJump.Max() < 0.02f && a.LookJump.Max() < 0.02f, $"eye {a.EyeJump.Max() * 100f:F2} cm, view {a.LookJump.Max() * 100f:F2} cm at 1 m");
            // (A held body is followed on springs four times slower: the last of the way in is slower than the
            // 2 mm/s that counts as "settled" here, so its limit is 2 mm.)
            var driftLimit = c.Subject == ShotSubject.HeldBody ? 0.002f : 0.001f;
            if (!c.Orbit)
                Gate($"drift after camera settled = 0 (< {driftLimit * 1000f:F0} mm)", a.Drift.Count > 0 && a.Drift.Max() < driftLimit, a.Drift.Count == 0 ? "no settled window" : $"max {a.Drift.Max() * 1000f:F2} mm");
            // framing: the shot's own subject, like for like
            if (c.Subject == ShotSubject.WholeBody)
            {
                // Whole body: ours counts the arms too; the baselines are given the easier head-to-feet chain.
                double Chain(Acc x) => 100.0 * x.ChainIn / x.Frames;
                Gate("framing not worse than both baselines", In(a, ShotSubject.WholeBody) >= Chain(directed) - 0.5 && In(a, ShotSubject.WholeBody) >= Chain(old) - 0.5,
                    $"whole body (with arms) in frame {In(a, ShotSubject.WholeBody):F1}% vs head-to-feet chain: Directed {Chain(directed):F1}%, Old {Chain(old):F1}%");
            }
            else
                Gate("framing not worse than both baselines", In(a, c.Subject) >= In(directed, c.Subject) - 0.5 && In(a, c.Subject) >= In(old, c.Subject) - 0.5,
                    $"{c.Subject} in frame {In(a, c.Subject):F1}% vs Directed {In(directed, c.Subject):F1}%, Old {In(old, c.Subject):F1}%");
            if (!c.Below)
                Gate("clearance not worse than both baselines", a.Below5 <= directed.Below5 && a.Below5 <= old.Below5 && a.MinClearance >= MathF.Min(0.05f, MathF.Min(directed.MinClearance, old.MinClearance)),
                    $"below 5 cm: {a.Below5} vs Directed {directed.Below5}, Old {old.Below5}; min {a.MinClearance:F3} vs {directed.MinClearance:F3}, {old.MinClearance:F3} m");
            var jerk = Stats.P(a.Jerk, .99f);
            var accel = Stats.P(a.ViewAccel, .99f);
            if (!c.Orbit && !c.Recall.HasValue)
                Gate("smoothness not worse than both baselines (eye jerk p99, view angular acceleration p99)",
                    jerk <= Stats.P(directed.Jerk, .99f) * 1.05f && jerk <= Stats.P(old.Jerk, .99f) * 1.05f &&
                    accel <= Stats.P(directed.ViewAccel, .99f) * 1.05f && accel <= Stats.P(old.ViewAccel, .99f) * 1.05f,
                    $"jerk {jerk:F0} vs Directed {Stats.P(directed.Jerk, .99f):F0}, Old {Stats.P(old.Jerk, .99f):F0} m/s3; accel {accel:F0} vs {Stats.P(directed.ViewAccel, .99f):F0}, {Stats.P(old.ViewAccel, .99f):F0} deg/s2");
            else
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  with the {(c.Orbit ? "hand moving" : $"turn to the remembered angle (view up to {a.ViewSpeed.Max():F0} deg/s)")}: eye jerk p99 {jerk:F0} m/s3, view angular acceleration p99 {accel:F0} deg/s2 (Directed {Stats.P(directed.Jerk, .99f):F0} / {Stats.P(directed.ViewAccel, .99f):F0}, Old {Stats.P(old.Jerk, .99f):F0} / {Stats.P(old.ViewAccel, .99f):F0})"));
            if (c.Name == "Close")
                Gate("old presets' height and distance on a standing or kneeling body (19-26 deg up, 1.0-1.35 m)",
                    a.OldUp.Count > 0 && a.OldUp.Min() >= 19f && a.OldUp.Max() <= 26f && a.OldDistance.Min() >= 1.0f && a.OldDistance.Max() <= 1.35f &&
                    a.OldRawPlayer.Count > 0 && a.OldRawPlayer.Min() >= 1.0f && a.OldRawPlayer.Max() <= 1.35f,
                    a.OldUp.Count == 0 ? "no standing start" : $"up {a.OldUp.Min():F1}..{a.OldUp.Max():F1}, distance {a.OldDistance.Min():F2}..{a.OldDistance.Max():F2} m for the reference body, " +
                                                                 $"{a.OldRawPlayer.Min():F2}..{a.OldRawPlayer.Max():F2} m on the player's own body; {a.OldLimited} of {a.OldStarts} standing starts give way to the ground or a wall and are not counted");
        }
        return failures;
    }
}
