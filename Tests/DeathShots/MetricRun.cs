using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>
/// The metric run: every profile, Auto and the two baselines over every trajectory from eight start azimuths; the
/// table of docs/DEATH_SHOTS.md §6 and its gates.
/// </summary>
internal static class MetricRun
{
    private const float Deg = MathF.PI / 180f;
    private static bool Verbose;
    private const string NoOldSource = "Auto, bodies only at the switch";

    private sealed class Column
    {
        public string Name = "";
        public bool IsNew, Hidden;
        /// <summary>The column's own subject is the head-to-feet chain (the Directed camera's anchors: no arms).</summary>
        public bool OwnIsChain;
        public ShotSubject? Subject; // null: per frame (Auto)
        public Func<Trajectory, RecordedWorld, StartCamera, (Cam[] Cams, float Scale)> Run = null!;
        public readonly Acc A = new();
    }

    /// <summary>Everything measured for one column, pooled over its runs.</summary>
    private sealed class Acc
    {
        public long ChainIn, RestFrames, OwnInRest, FaceHeld, FaceHeldVisible, Frames, OwnIn, OwnSafe, HipsChestIn, HeadIn, WholeIn, FaceClaimed, FaceVisible, Below5, TriFrames, TriBlocked, PulledIn, CutFrames;
        public float MinClearance = float.MaxValue;
        public readonly List<float> Size = new(), EyeSpeed = new(), ViewSpeed = new(), Jerk = new(), ViewAccel = new(), FovRate = new();
        public readonly List<float> SideChangesUnmoved = new(), SideChanges = new(), Cuts = new(), Travel = new(), EyeJump = new(), LookJump = new(), SettleLag = new(), Drift = new(), DistanceRange = new();
        public int Pops, OldStarts, OldLimited, Runs, RunsWithFallback, SettleWindows, CameraNeverSettled;
        public readonly List<float> JumpSlow = new(), JumpFast = new(), OldRaw = new(), OldRawPlayer = new(), OldOff = new(), OldUp = new(), OldDistance = new();
        public readonly SortedDictionary<string, long> ShotFrames = new();

        public void Merge(Acc o)
        {
            ChainIn += o.ChainIn; RestFrames += o.RestFrames; OwnInRest += o.OwnInRest; FaceHeld += o.FaceHeld; FaceHeldVisible += o.FaceHeldVisible; Frames += o.Frames; OwnIn += o.OwnIn; OwnSafe += o.OwnSafe; HipsChestIn += o.HipsChestIn; HeadIn += o.HeadIn; WholeIn += o.WholeIn;
            FaceClaimed += o.FaceClaimed; FaceVisible += o.FaceVisible; Below5 += o.Below5; TriFrames += o.TriFrames; TriBlocked += o.TriBlocked;
            PulledIn += o.PulledIn; CutFrames += o.CutFrames;
            MinClearance = MathF.Min(MinClearance, o.MinClearance);
            Size.AddRange(o.Size); EyeSpeed.AddRange(o.EyeSpeed); ViewSpeed.AddRange(o.ViewSpeed); Jerk.AddRange(o.Jerk); ViewAccel.AddRange(o.ViewAccel); FovRate.AddRange(o.FovRate);
            SideChanges.AddRange(o.SideChanges); SideChangesUnmoved.AddRange(o.SideChangesUnmoved); Cuts.AddRange(o.Cuts); Travel.AddRange(o.Travel); EyeJump.AddRange(o.EyeJump); LookJump.AddRange(o.LookJump);
            SettleLag.AddRange(o.SettleLag); Drift.AddRange(o.Drift); DistanceRange.AddRange(o.DistanceRange);
            Pops += o.Pops; OldStarts += o.OldStarts; OldLimited += o.OldLimited; Runs += o.Runs; RunsWithFallback += o.RunsWithFallback; SettleWindows += o.SettleWindows; CameraNeverSettled += o.CameraNeverSettled;
            JumpSlow.AddRange(o.JumpSlow); JumpFast.AddRange(o.JumpFast); OldRaw.AddRange(o.OldRaw); OldRawPlayer.AddRange(o.OldRawPlayer); OldOff.AddRange(o.OldOff); OldUp.AddRange(o.OldUp); OldDistance.AddRange(o.OldDistance);
            foreach (var p in o.ShotFrames) ShotFrames[p.Key] = ShotFrames.GetValueOrDefault(p.Key) + p.Value;
        }
    }

    public static int Run(List<Trajectory> all, bool verbose)
    {
        Verbose = verbose;
        var usable = all.Where(t => t.Humanoid && t.Frames.Length >= 2).ToList();
        var columns = new List<Column>();
        foreach (var profile in new[] { DeathShotProfile.LowThreeQuarter, DeathShotProfile.Face, DeathShotProfile.AlongBody, DeathShotProfile.Overhead, DeathShotProfile.GroundProfile, DeathShotProfile.Auto })
        {
            var p = profile;
            columns.Add(new Column
            {
                Name = p switch { DeathShotProfile.LowThreeQuarter => "Low3/4", DeathShotProfile.AlongBody => "Along", DeathShotProfile.GroundProfile => "Ground", _ => p.ToString() },
                IsNew = true, Subject = p == DeathShotProfile.Auto ? null : ShotRecipe.Of(p).Subject,
                Run = (t, w, s) => { var c = Runners.New(t, w, s, p, out var scale); return (c, scale); },
            });
        }
        columns.Add(new Column
        {
            Name = NoOldSource, IsNew = true, Hidden = true,
            Run = (t, w, s) => { var c = Runners.New(t, w, s, DeathShotProfile.Auto, out var scale, oldSourceAtSwitch: false); return (c, scale); },
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
            if (hipsChest.Reach <= 1f) a.HipsChestIn++;
            if (head.Reach <= 1f) a.HeadIn++;
            if (body.Reach <= 1f) a.WholeIn++;
            // The Directed camera's own anchors: head top, head, chest, hips, knees, feet (no arms).
            Vector3 Lifted(Vector3 v) => new(v.X, MathF.Max(v.Y, ground + 0.03f), v.Z);
            var chain = Span(stackalloc[]
            {
                Lifted(top), Lifted(p.Head), Lifted(p.Chest), Lifted(p.Hips), Lifted(f.Runtime[kneeL]), Lifted(f.Runtime[kneeR]), Lifted(p.FootL), Lifted(p.FootR),
            }, cam.Eye, dir, right, up, lens);
            if (chain.Reach <= 1f) a.ChainIn++;
            var subject = column.Subject ?? ShotRecipe.Of(cam.Shot).Subject;
            var own = column.OwnIsChain ? chain : subject switch { ShotSubject.HipsToChest => hipsChest, ShotSubject.Head => head, _ => body };
            if (own.Reach <= 1f) a.OwnIn++;
            if (bodySettled) { a.RestFrames++; if (own.Reach <= 1f) a.OwnInRest++; }
            if (own.Reach <= 0.92f) a.OwnSafe++;
            // Subject size: how much of the frame its extent takes, the larger of the two axes; a single point
            // (the head) by the head's size at its depth.
            a.Size.Add(subject == ShotSubject.Head ? 0.24f * scale / (2f * MathF.Max(0.05f, head.Depth) * tanV) : MathF.Max(own.SpanX, own.SpanY) * 0.5f);
            var frame = BodyFrame.From(p, ground);
            var shotName = cam.HasDiagnostics ? cam.Shot.ToString() : column.Name;
            a.ShotFrames[shotName] = a.ShotFrames.GetValueOrDefault(shotName) + 1;
            if (cam.HasDiagnostics ? cam.Shot == DeathShotProfile.Face : false)
            {
                a.FaceClaimed++;
                if (cam.Holding) a.FaceHeld++;
                var toEye = cam.Eye - headCentre;
                if (head.Reach <= 1f && toEye.LengthSquared() > 1e-6f && Vector3.Dot(frame.Front, Vector3.Normalize(toEye)) > MathF.Cos(80f * Deg) &&
                    !world.Blocked(headCentre, cam.Eye, out _))
                {
                    a.FaceVisible++;
                    if (cam.Holding) a.FaceHeldVisible++;
                }
            }

            // clearance and blocked view
            var under = world.GroundBelow(cam.Eye + new Vector3(0f, DeathShotSolver.GroundRayLift, 0f)) ?? ground;
            var clearance = cam.Eye.Y - under;
            a.MinClearance = MathF.Min(a.MinClearance, clearance);
            if (clearance < 0.05f) a.Below5++;
            if (world.HasTriangles)
            {
                a.TriFrames++;
                var centre = subject switch { ShotSubject.HipsToChest => (p.Hips + p.Chest) * 0.5f, ShotSubject.Head => headCentre, _ => p.Hips };
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

            // the old presets: where Low three-quarter aims on a standing or kneeling body, at the moment the side is chosen
            if (i == 0 && cam.HasDiagnostics && cam.Shot == DeathShotProfile.LowThreeQuarter && cam.Pose is PoseClass.Standing or PoseClass.Kneeling && column.Name == "Low3/4")
            {
                var away = cam.GoalEye - cam.GoalLookAt;
                var d = away.Length();
                a.OldStarts++;
                // Where the ground stops the eye (a body already low, a bank on that side) the shot gives way by design.
                var upGoal = -MathF.Asin(Math.Clamp(away.Y / d, -1f, 1f));
                if (MathF.Abs(upGoal + ShotRecipe.LowThreeQuarter.Elevation) > 0.5f * Deg) { a.OldLimited++; continue; }
                a.OldOff.Add(MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frame.FrontHorizontal), DeathShotMath.Azimuth(away))) / Deg);
                a.OldUp.Add(-MathF.Asin(Math.Clamp(away.Y / d, -1f, 1f)) / Deg);
                a.OldDistance.Add(d / scale);
                a.OldRaw.Add(d);
                if (t.IsPlayer) a.OldRawPlayer.Add(d);
            }
        }
        CloseWindow(t.Frames[^1].Time);
        a.Travel.Add(travel);
        if (cams[^1].HasDiagnostics)
        {
            a.SideChanges.Add(cams[^1].SideChanges);
            a.SideChangesUnmoved.Add(cams[^1].SideChanges - cams[^1].SideChangesAfterMove);
            a.Cuts.Add(cams[^1].Cuts);
            a.Pops += cams[^1].Pops;
            if (cams[^1].Fallbacks > 0) a.RunsWithFallback++;
            if (cams[^1].SideChanges > 1 && Verbose)
                lock (Console.Out) Console.WriteLine($"    {column.Name} {t.Name}: " + string.Join("; ", cams.Where(c => c.Event != null).Select(c => c.Event)));
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
        var rows = new List<(string Label, Func<Acc, string> Value)>
        {
            ("own subject in frame %", a => Pct(a.OwnIn, a.Frames)),
            ("own subject in frame %, body at rest", a => Pct(a.OwnInRest, a.RestFrames)),
            ("own subject in safe frame (0.92) %", a => Pct(a.OwnSafe, a.Frames)),
            ("hips+chest in frame %", a => Pct(a.HipsChestIn, a.Frames)),
            ("head in frame %", a => Pct(a.HeadIn, a.Frames)),
            ("head-to-feet chain in frame % (no arms)", a => Pct(a.ChainIn, a.Frames)),
            ("whole body in frame % (with arms)", a => Pct(a.WholeIn, a.Frames)),
            ("face visible % (Face frames)", a => a.FaceClaimed == 0 ? "-" : Pct(a.FaceVisible, a.FaceClaimed)),
            ("face visible % (Face frames, body at rest)", a => a.FaceHeld == 0 ? "-" : Pct(a.FaceHeldVisible, a.FaceHeld)),
            ("min eye clearance m", a => a.MinClearance.ToString("F3", inv)),
            ("frames below 5 cm clearance", a => a.Below5.ToString(inv)),
            ("view blocked by ground tris % (player)", a => Pct(a.TriBlocked, a.TriFrames)),
            ("subject size p5 / p95 (frame fraction)", a => $"{Stats.P(a.Size, .05f):F2}/{Stats.P(a.Size, .95f):F2}"),
            ("eye speed p99 / max m/s", a => $"{Stats.P(a.EyeSpeed, .99f):F2}/{a.EyeSpeed.Max():F2}"),
            ("view angular speed p99 / max deg/s", a => $"{Stats.P(a.ViewSpeed, .99f):F1}/{a.ViewSpeed.Max():F1}"),
            ("view angular accel p99 deg/s2", a => $"{Stats.P(a.ViewAccel, .99f):F0}"),
            ("eye jerk p99 m/s3", a => $"{Stats.P(a.Jerk, .99f):F0}"),
            ("FoV rate max rad/s", a => $"{a.FovRate.Max():F2}"),
            ("side changes per death mean / max", a => a.SideChanges.Count == 0 ? "-" : $"{a.SideChanges.Average():F2}/{a.SideChanges.Max():F0}"),
            ("  of those, not after the body was moved: max", a => a.SideChanges.Count == 0 ? "-" : $"{a.SideChangesUnmoved.Max():F0}"),
            ("cuts per death mean / max", a => a.Cuts.Count == 0 ? "-" : $"{a.Cuts.Average():F2}/{a.Cuts.Max():F0}"),
            ("eye found inside terrain, put on top (frames)", a => a.SideChanges.Count == 0 ? "-" : a.Pops.ToString(inv)),
            ("azimuth travel deg mean / max", a => $"{a.Travel.Average():F0}/{a.Travel.Max():F0}"),
            ("ragdoll-start eye jump max cm", a => a.EyeJump.Count == 0 ? "-" : $"{a.EyeJump.Max() * 100f:F2}"),
            ("ragdoll-start view jump max cm at 1 m", a => a.LookJump.Count == 0 ? "-" : $"{a.LookJump.Max() * 100f:F2}"),
            ("body settled -> camera settled s median / p95", a => a.SettleLag.Count == 0 ? "-" : $"{Stats.P(a.SettleLag, .5f):F1}/{Stats.P(a.SettleLag, .95f):F1}"),
            ("camera never settled (windows > 4 s)", a => $"{a.CameraNeverSettled}/{a.SettleWindows}"),
            ("drift after camera settled max mm", a => a.Drift.Count == 0 ? "-" : $"{a.Drift.Max() * 1000f:F1}"),
            ("settled distance range max cm", a => a.DistanceRange.Count == 0 ? "-" : $"{a.DistanceRange.Max() * 100f:F1}"),
            ("deaths with a fallback %", a => a.SideChanges.Count == 0 ? "-" : Pct(a.RunsWithFallback, a.Runs)),
            ("frames pulled in %", a => a.SideChanges.Count == 0 ? "-" : Pct(a.PulledIn, a.Frames)),
        };
        var width = rows.Max(r => r.Label.Length);
        var text = new StringBuilder();
        text.Append("| ").Append("metric".PadRight(width)).Append(" |");
        foreach (var c in columns) text.Append(' ').Append(c.Name.PadLeft(11)).Append(" |");
        text.AppendLine();
        text.Append("|").Append(new string('-', width + 2)).Append("|");
        foreach (var _ in columns) text.Append(new string('-', 12)).Append(":|");
        text.AppendLine();
        foreach (var (label, value) in rows)
        {
            text.Append("| ").Append(label.PadRight(width)).Append(" |");
            foreach (var c in columns) text.Append(' ').Append(value(c.A).PadLeft(11)).Append(" |");
            text.AppendLine();
        }
        Console.Write(text.ToString());
        var auto = columns.First(c => c.Name == "Auto").A;
        Console.WriteLine("Auto used: " + string.Join(", ", auto.ShotFrames.Select(p => $"{p.Key} {100.0 * p.Value / auto.Frames:F1}%")));
        var low = columns[0].A;
        if (low.OldOff.Count > 0)
            Console.WriteLine(string.Create(inv, $"old presets (Low three-quarter, standing/kneeling at the choice: {low.OldStarts} runs, {low.OldLimited} of them limited by the ground and left out): off the front {low.OldOff.Min():F1}..{low.OldOff.Max():F1} deg, " +
                              $"looking up {low.OldUp.Min():F1}..{low.OldUp.Max():F1} deg, distance {low.OldDistance.Min():F2}..{low.OldDistance.Max():F2} m for the reference body ({low.OldRaw.Min():F2}..{low.OldRaw.Max():F2} m as built: {Stats.P(low.OldRawPlayer, .5f):F2} m on the player's own body)"));
    }

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
        double Own(Acc a) => 100.0 * a.OwnIn / a.Frames;
        double In(Acc a, ShotSubject s) => 100.0 * (s switch { ShotSubject.HipsToChest => a.HipsChestIn, ShotSubject.Head => a.HeadIn, _ => a.WholeIn }) / a.Frames;
        Console.WriteLine("gates (baselines: Directed = the current death framing; Old = the three old Death Cam shots pooled):");
        foreach (var c in columns.Where(c => c.IsNew))
        {
            var a = c.A;
            Console.WriteLine($" {c.Name}");
            Gate("frames below 5 cm clearance = 0", a.Below5 == 0, $"{a.Below5} (min clearance {a.MinClearance:F3} m)");
            Gate("view angular speed <= 60 deg/s outside cuts", a.ViewSpeed.Max() <= 60.5f, $"max {a.ViewSpeed.Max():F1}");
            Gate("side changes <= 1 per death (a settled body that is moved and comes to rest turned may get one more each time)", a.SideChangesUnmoved.Max() <= 1f,
                $"max {a.SideChangesUnmoved.Max():F0}; counting the changes after a move: max {a.SideChanges.Max():F0}, deaths with more than one {a.SideChanges.Count(x => x > 1f)}/{a.SideChanges.Count}");
            Gate("ragdoll-start jump < 2 cm", a.EyeJump.Max() < 0.02f && a.LookJump.Max() < 0.02f, $"eye {a.EyeJump.Max() * 100f:F2} cm, view {a.LookJump.Max() * 100f:F2} cm at 1 m");
            Gate("drift after camera settled = 0 (< 1 mm)", a.Drift.Count > 0 && a.Drift.Max() < 0.001f, a.Drift.Count == 0 ? "no settled window" : $"max {a.Drift.Max() * 1000f:F2} mm");
            // framing: the profile's own subject, like for like where the subject is fixed
            if (c.Subject == ShotSubject.WholeBody)
            {
                // Whole body: ours counts the arms too; the baselines are given the easier head-to-feet chain.
                double Chain(Acc x) => 100.0 * x.ChainIn / x.Frames;
                Gate("framing not worse than both baselines", In(a, ShotSubject.WholeBody) >= Chain(directed) - 0.5 && In(a, ShotSubject.WholeBody) >= Chain(old) - 0.5,
                    $"whole body (with arms) in frame {In(a, ShotSubject.WholeBody):F1}% vs head-to-feet chain: Directed {Chain(directed):F1}%, Old {Chain(old):F1}%");
            }
            else if (c.Subject is { } s)
                Gate("framing not worse than both baselines", In(a, s) >= In(directed, s) - 0.5 && In(a, s) >= In(old, s) - 0.5,
                    $"{s} in frame {In(a, s):F1}% vs Directed {In(directed, s):F1}%, Old {In(old, s):F1}%");
            else
                Gate("framing not worse than both baselines", Own(a) >= Own(directed) - 0.5 && Own(a) >= Own(old) - 0.5,
                    $"own subject in frame {Own(a):F1}% vs Directed (its head-to-feet chain) {Own(directed):F1}%, Old (hips+chest) {Own(old):F1}%");
            Gate("clearance not worse than both baselines", a.Below5 <= directed.Below5 && a.Below5 <= old.Below5 && a.MinClearance >= MathF.Min(0.05f, MathF.Min(directed.MinClearance, old.MinClearance)),
                $"below 5 cm: {a.Below5} vs Directed {directed.Below5}, Old {old.Below5}; min {a.MinClearance:F3} vs {directed.MinClearance:F3}, {old.MinClearance:F3} m");
            var jerk = Stats.P(a.Jerk, .99f);
            var accel = Stats.P(a.ViewAccel, .99f);
            Gate("smoothness not worse than both baselines (eye jerk p99, view angular acceleration p99)",
                jerk <= Stats.P(directed.Jerk, .99f) * 1.05f && jerk <= Stats.P(old.Jerk, .99f) * 1.05f &&
                accel <= Stats.P(directed.ViewAccel, .99f) * 1.05f && accel <= Stats.P(old.ViewAccel, .99f) * 1.05f,
                $"jerk {jerk:F0} vs Directed {Stats.P(directed.Jerk, .99f):F0}, Old {Stats.P(old.Jerk, .99f):F0} m/s3; accel {accel:F0} vs {Stats.P(directed.ViewAccel, .99f):F0}, {Stats.P(old.ViewAccel, .99f):F0} deg/s2");
            if (c.Name == "Low3/4")
                Gate("old presets reproduced (39-46 deg off, 19-26 deg up, 1.0-1.35 m)",
                    a.OldOff.Count > 0 && a.OldOff.Min() >= 39f && a.OldOff.Max() <= 46f && a.OldUp.Min() >= 19f && a.OldUp.Max() <= 26f && a.OldDistance.Min() >= 1.0f && a.OldDistance.Max() <= 1.35f &&
                    a.OldRawPlayer.Count > 0 && a.OldRawPlayer.Min() >= 1.0f && a.OldRawPlayer.Max() <= 1.35f,
                    a.OldOff.Count == 0 ? "no standing start" : $"off {a.OldOff.Min():F1}..{a.OldOff.Max():F1}, up {a.OldUp.Min():F1}..{a.OldUp.Max():F1}, distance {a.OldDistance.Min():F2}..{a.OldDistance.Max():F2} m for the reference body, " +
                                                                  $"{a.OldRawPlayer.Min():F2}..{a.OldRawPlayer.Max():F2} m on the player's own body; {a.OldLimited} of {a.OldStarts} standing starts give way to the ground and are not counted");
            if (c.Name is "Face")
                Gate("face visible on Face frames once the body rests >= 90 %", a.FaceHeld > 0 && 100.0 * a.FaceHeldVisible / a.FaceHeld >= 90.0,
                    Pct(a.FaceHeldVisible, a.FaceHeld) + "% at rest (" + Pct(a.FaceVisible, a.FaceClaimed) + "% over all Face frames: the side is not re-chosen while the body falls)");
        }
        return failures;
    }
}
