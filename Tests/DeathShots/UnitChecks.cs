using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>Unit checks on synthetic poses: body frame, pose classes (hysteresis, dwell), settle detector, springs,
/// and the solver on bodies that do not move (each profile lands where its recipe says).</summary>
internal static class UnitChecks
{
    private const float Deg = MathF.PI / 180f;
    private static int failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    /// <summary>Flat ground at y = 0, optionally a wall: the half space x &gt; WallX.</summary>
    private sealed class FlatWorld : IDeathShotWorld
    {
        public float WallX = float.PositiveInfinity;
        public float? Water;
        public float? GroundBelow(Vector3 from) => 0f;
        public float? WaterBelow(Vector3 from) => Water;
        public bool Blocked(Vector3 from, Vector3 to, out float distance)
        {
            distance = 0f;
            if (from.X < WallX == to.X < WallX) return false;
            distance = Vector3.Distance(from, to) * MathF.Abs((WallX - from.X) / (to.X - from.X));
            return true;
        }
    }

    /// <summary>The reference body standing, facing +Z (its right side is at −X), feet on y = 0: hips → chest → head
    /// measure <see cref="DeathShotSolver.ReferenceSpine"/>.</summary>
    private static BodyPoints Standing => new(
        Hips: new(0f, 1.00f, 0f), Chest: new(0f, 1.24f, 0f), Head: new(0f, 1.46f, 0f),
        ShoulderL: new(0.03f, 1.31f, 0f), ShoulderR: new(-0.03f, 1.31f, 0f), FootL: new(0.1f, 0.05f, 0f), FootR: new(-0.1f, 0.05f, 0f));

    private static BodyPoints Map(BodyPoints p, Func<Vector3, Vector3> f) =>
        new(f(p.Hips), f(p.Chest), f(p.Head), f(p.ShoulderL), f(p.ShoulderR), f(p.FootL), f(p.FootR));

    private static BodyPoints Turn(BodyPoints p, Vector3 axis, float angle, Vector3 about = default)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        return Map(p, v => about + Vector3.Transform(v - about, q));
    }

    /// <summary>The standing body laid down: on its back (head toward −Z), then rolled about its own axis by
    /// <paramref name="roll"/> (0 = on the back, 90° = on a side, 180° = on the belly), resting 0.12 m above y = 0.</summary>
    private static BodyPoints Lying(float roll)
    {
        var p = Turn(Standing, Vector3.UnitX, -MathF.PI / 2f);
        p = Turn(p, Vector3.UnitZ, roll, new Vector3(0f, 0f, 0f));
        return Map(p, v => v + new Vector3(0f, 0.12f, 0f));
    }

    private static BodyPoints Kneeling
    {
        get
        {
            var s = Standing;
            var down = new Vector3(0f, -0.5f, 0f);
            return new BodyPoints(s.Hips + down, s.Chest + down, s.Head + down, s.ShoulderL + down, s.ShoulderR + down,
                new Vector3(0.1f, 0.05f, -0.45f), new Vector3(-0.1f, 0.05f, -0.45f));
        }
    }

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("unit checks");
        BodyFrames();
        Classes();
        SettleAndSprings();
        SolverStatic();
        PlayerHand();
        BelowGround();
        SoftChanges();
        Grabbed();
        Remembered();
        Console.WriteLine(failures == 0 ? "unit checks: all ok" : $"unit checks: {failures} FAILED");
        return failures;
    }

    private static void BodyFrames()
    {
        var f = BodyFrame.From(Standing, 0f);
        Check("standing: axis up, front +Z, lateral toward the right shoulder (-X)", f.Valid && f.Axis.Y > 0.99f && f.Front.Z > 0.99f && f.Lateral.X < -0.99f,
            $"axis {f.Axis}, front {f.Front}, lateral {f.Lateral}");
        var worst = 1f;
        for (var k = 0; k < 12; k++)
        {
            var yaw = k * 30f * Deg;
            var turned = BodyFrame.From(Turn(Standing, Vector3.UnitY, yaw), 0f);
            worst = MathF.Min(worst, Vector3.Dot(turned.FrontHorizontal, new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw))));
        }
        Check("standing, turned 0..330 deg: the front follows the facing", worst > 0.999f, $"worst dot {worst:F4}");
        var supine = BodyFrame.From(Lying(0f), 0f);
        var prone = BodyFrame.From(Lying(MathF.PI), 0f);
        var side = BodyFrame.From(Lying(MathF.PI / 2f), 0f);
        Check("lying on the back: front up; on the belly: front down; on a side: front level",
            supine.Front.Y > 0.99f && prone.Front.Y < -0.99f && MathF.Abs(side.Front.Y) < 0.02f, $"{supine.Front.Y:F2}, {prone.Front.Y:F2}, {side.Front.Y:F2}");
        worst = 1f;
        for (var k = 0; k < 24; k++)
            worst = MathF.Min(worst, Vector3.Dot(BodyFrame.From(Lying(k * 15f * Deg), 0f).LongAxis, supine.LongAxis));
        Check("long axis (feet -> head, horizontal) does not change when the body rolls about itself", worst > 0.995f && supine.LongAxis.Z < -0.99f && supine.LongAxisDefined,
            $"worst dot {worst:F4}, axis {supine.LongAxis}");
        Check("standing: no long axis of its own (falls back to the front)", !f.LongAxisDefined && Vector3.Dot(f.LongAxis, f.FrontHorizontal) > 0.99f);
    }

    private static void Classes()
    {
        Check("raw classes: standing, kneeling, supine, prone, side",
            PoseClassifier.Raw(BodyFrame.From(Standing, 0f)) == PoseClass.Standing &&
            PoseClassifier.Raw(BodyFrame.From(Kneeling, 0f)) == PoseClass.Kneeling &&
            PoseClassifier.Raw(BodyFrame.From(Lying(0f), 0f)) == PoseClass.Supine &&
            PoseClassifier.Raw(BodyFrame.From(Lying(MathF.PI), 0f)) == PoseClass.Prone &&
            PoseClassifier.Raw(BodyFrame.From(Lying(MathF.PI / 2f), 0f)) == PoseClass.Side &&
            PoseClassifier.Raw(BodyFrame.From(Lying(-MathF.PI / 2f), 0f)) == PoseClass.Side);
        var small = Map(Standing, v => v * 0.47f);
        Check("a small body (hips at 0.45 m) stands, with the hips thresholds scaled", PoseClassifier.Raw(BodyFrame.From(small, 0f), 0.47f) == PoseClass.Standing &&
                                                                                 PoseClassifier.Raw(BodyFrame.From(small, 0f)) == PoseClass.Kneeling);

        // On the supine/side boundary (front.Y = 0.5 at a roll of 60 deg), rocking +-4 deg every frame.
        var c = new PoseClassifier();
        var changes = 0;
        var last = c.Update(BodyFrame.From(Lying(40f * Deg), 0f), 1f / 60f);
        for (var i = 0; i < 600; i++)
        {
            var now = c.Update(BodyFrame.From(Lying((60f + (i % 2 == 0 ? 4f : -4f)) * Deg), 0f), 1f / 60f);
            if (now != last) changes++;
            last = now;
        }
        Check("rocking +-4 deg on the supine/side boundary for 10 s: the class does not flip", changes == 0, $"{changes} changes, class {last}");

        // Flipping between the back and a side every 0.3 s: a class is kept at least 0.75 s.
        c = new PoseClassifier();
        last = c.Update(BodyFrame.From(Lying(0f), 0f), 1f / 60f);
        changes = 0;
        float since = 0f, shortest = float.MaxValue;
        for (var i = 0; i < 600; i++)
        {
            var roll = (i / 18) % 2 == 0 ? 0f : 90f * Deg;
            var now = c.Update(BodyFrame.From(Lying(roll), 0f), 1f / 60f);
            since += 1f / 60f;
            if (now != last) { changes++; shortest = MathF.Min(shortest, since); since = 0f; }
            last = now;
        }
        Check("pose flipping every 0.3 s: every class is held at least 0.75 s", changes > 0 && shortest >= PoseClassifier.MinDwell - 1e-3f, $"{changes} changes, shortest dwell {shortest:F2} s");

        c = new PoseClassifier();
        var sequence = new List<PoseClass>();
        for (var i = 0; i <= 240; i++)
        {
            // falls backward over 2 s, then lies
            var t = Math.Clamp(i / 120f, 0f, 1f);
            var p = Map(Turn(Standing, Vector3.UnitX, -MathF.PI / 2f * t), v => v + new Vector3(0f, 0.12f * t, 0f));
            var now = c.Update(BodyFrame.From(p, 0f), 1f / 60f);
            if (sequence.Count == 0 || sequence[^1] != now) sequence.Add(now);
        }
        Check("falling backward: standing, then supine, nothing in between flickers", sequence.Count is 2 or 3 && sequence[0] == PoseClass.Standing && sequence[^1] == PoseClass.Supine,
            string.Join(" -> ", sequence));
    }

    private static void SettleAndSprings()
    {
        var s = new SettleDetector();
        var p = Lying(0f);
        var at = -1f;
        for (var i = 0; i < 120; i++)
            if (s.Update(p, 1f / 60f) && at < 0f) at = i / 60f;
        Check("a still body settles after 0.8 s", at is > 0.75f and < 0.9f, $"{at:F2} s");
        var nudged = Map(p, v => v + new Vector3(0.1f, 0f, 0f));
        Check("moved 0.10 m: still settled; 0.30 m: moving again", s.Update(nudged, 1f / 60f) && !s.Update(Map(p, v => v + new Vector3(0.3f, 0f, 0f)), 1f / 60f));
        s.Reset();
        var creeping = false;
        for (var i = 0; i < 120; i++) creeping |= s.Update(Map(p, v => v + new Vector3(i * 0.1f / 60f, 0f, 0f)), 1f / 60f);
        Check("a body creeping at 0.10 m/s never settles", !creeping);

        float x = 0f, v0 = 0f, peak = 0f, over = 0f;
        for (var i = 0; i < 600; i++)
        {
            var before = x;
            x = DeathShotMath.Spring(x, 10f, ref v0, 0.6f, 3f, 1f / 60f);
            peak = MathF.Max(peak, (x - before) * 60f);
            over = MathF.Max(over, x - 10f);
        }
        Check("spring: arrives, never overshoots, keeps under its speed cap", MathF.Abs(x - 10f) < 1e-3f && over <= 1e-4f && peak <= 3.01f, $"x {x:F4}, peak speed {peak:F2}, overshoot {over:F5}");
        Check("angles wrap the short way", MathF.Abs(DeathShotMath.AngleDelta(170f * Deg, -170f * Deg) - 20f * Deg) < 1e-5f);
    }

    private sealed record Landed(DeathShotOutput Out, float MaxViewRate, float MinClearance, int Frames, float BodyScale);

    private static Landed RunStatic(IDeathShotWorld world, BodyPoints p, DeathShotProfile profile, float seconds, float startAzimuth, bool begin = true, Vector3[]? extra = null)
    {
        var solver = new DeathShotSolver(world);
        var chest = p.Chest;
        if (begin) solver.Begin(chest + DeathShotMath.OrbitDirection(startAzimuth, 15f * Deg) * 5f, chest, 0.78f);
        DeathShotOutput o = default;
        Vector3 lastDir = default;
        float maxRate = 0f, minClear = float.MaxValue;
        var n = (int)(seconds * 60f);
        for (var i = 0; i < n; i++)
        {
            o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = p, Extra = extra, Profile = profile, Aspect = 16f / 9f, UserSize = 1f });
            var dir = Vector3.Normalize(o.LookAt - o.Eye);
            if (i > 0 && !o.Cut) maxRate = MathF.Max(maxRate, MathF.Acos(Math.Clamp(Vector3.Dot(dir, lastDir), -1f, 1f)) * 60f / Deg);
            lastDir = dir;
            minClear = MathF.Min(minClear, o.Eye.Y - (world.GroundBelow(o.Eye) ?? 0f));
        }
        return new Landed(o, maxRate, minClear, n, solver.BodyScale);
    }

    private static float Reach(DeathShotOutput o, params Vector3[] points)
    {
        var dir = Vector3.Normalize(o.LookAt - o.Eye);
        DynamicCameraSolver.BasisFromForward(dir, out var right, out var up);
        var tanV = MathF.Tan(o.Fov * 0.5f);
        var lens = new DynamicCameraSolver.Lens(tanV * 16f / 9f, tanV);
        var reach = 0f;
        foreach (var p in points)
        {
            var ndc = DynamicCameraSolver.Project(p, o.Eye, dir, right, up, lens);
            reach = ndc.Z <= 0.05f ? float.MaxValue : MathF.Max(reach, MathF.Max(MathF.Abs(ndc.X), MathF.Abs(ndc.Y)));
        }
        return reach;
    }

    private static Vector3[] All(BodyPoints p) => new[] { p.Hips, p.Chest, p.Head, p.ShoulderL, p.ShoulderR, p.FootL, p.FootR };

    /// <summary>Azimuth of the eye about the look-at point as rendered, degrees off <paramref name="azimuth"/>.</summary>
    private static float RenderedOff(in DeathShotOutput o, float azimuth) =>
        MathF.Abs(DeathShotMath.AngleDelta(azimuth, DeathShotMath.Azimuth(o.Eye - o.LookAt))) / Deg;

    private static Vector3 HeadCentre(BodyPoints p) => p.Head + Vector3.Normalize(p.Head - p.Chest) * 0.09f;
    private static Vector3 HeadTop(BodyPoints p) => p.Head + Vector3.Normalize(p.Head - p.Chest) * 0.18f;

    /// <summary>The middle of the thighs: from the hips toward the feet, 0.30 m (reference body).</summary>
    private static Vector3 MidThighs(BodyPoints p)
    {
        Vector3 One(Vector3 foot) => p.Hips + Vector3.Normalize(foot - p.Hips) * 0.30f;
        return (One(p.FootL) + One(p.FootR)) * 0.5f;
    }

    /// <summary>Where a point sits up the frame: 0 = the bottom edge, 1 = the top edge.</summary>
    private static float UpFrame(DeathShotOutput o, Vector3 point)
    {
        var dir = Vector3.Normalize(o.LookAt - o.Eye);
        DynamicCameraSolver.BasisFromForward(dir, out var right, out var up);
        var tanV = MathF.Tan(o.Fov * 0.5f);
        var ndc = DynamicCameraSolver.Project(point, o.Eye, dir, right, up, new DynamicCameraSolver.Lens(tanV * 16f / 9f, tanV));
        return (ndc.Y + 1f) * 0.5f;
    }

    private static void SolverStatic()
    {
        var world = new FlatWorld();
        var standing = Standing;
        var supine = Lying(0f);
        var points = All(supine);

        // Low close on a standing body, from every start azimuth: the old presets' height and distance, on the
        // azimuth the camera had.
        bool inRange = true, framed = true, rate = true, clear = true, kept = true;
        var detail = "";
        for (var k = 0; k < 8; k++)
        {
            var r = RunStatic(world, standing, DeathShotProfile.LowClose, 12f, k * 45f * Deg);
            // Before the push-in: the damped pose without the built-in motion.
            var away = r.Out.BaseEye - r.Out.BaseLookAt;
            var d = away.Length();
            var up = -MathF.Asin(away.Y / d) / Deg;
            inRange &= up is >= 19f and <= 26f && d is >= 1.0f and <= 1.35f;
            kept &= MathF.Abs(DeathShotMath.AngleDelta(k * 45f * Deg, r.Out.Azimuth)) < 1e-5f && RenderedOff(r.Out, k * 45f * Deg) < 0.05f;
            framed &= Reach(r.Out, standing.Hips, standing.Chest) <= 1f;
            rate &= r.MaxViewRate <= 60.5f;
            clear &= r.MinClearance >= 0.0999f;
            if (k == 1) detail = $"start azimuth 45 deg: ends at {DeathShotMath.Azimuth(away) / Deg:F2} deg, up {up:F1} deg, {d:F2} m; max view rate {r.MaxViewRate:F1} deg/s";
        }
        Check("Low close, standing, 8 start azimuths: the eye stays on the azimuth the camera had; looking up 19-26 deg from 1.0-1.35 m", inRange && kept, detail);
        Check("  hips and chest in frame, view <= 60 deg/s all the way in, eye >= 10 cm above ground", framed && rate && clear);

        // A body at rest gets the push-in: 12 % closer after 1 + 8 s, and then nothing moves.
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(standing.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, standing.Chest, 0.78f);
            DeathShotOutput o = default;
            var moved = 0f;
            for (var i = 0; i < 60 * 16; i++)
            {
                var prev = o;
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = Lying(90f * Deg), Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f });
                if (i > 60 * 14) moved = MathF.Max(moved, Vector3.Distance(o.Eye, prev.Eye));
            }
            var ratio = Vector3.Distance(o.Eye, o.LookAt) / Vector3.Distance(o.BaseEye, o.BaseLookAt);
            Check("settled body: push-in of 12 % over 8 s, then the camera is still", o.Holding && MathF.Abs(ratio - 0.88f) < 0.01f && moved < 1e-5f, $"ratio {ratio:F3}, movement after {moved * 1000f:F3} mm");
        }

        // The other two shots: subject, height, distance.
        var faceStanding = RunStatic(world, standing, DeathShotProfile.LowFace, 12f, 45f * Deg);
        var fsAway = faceStanding.Out.BaseEye - HeadCentre(standing);
        Check("Low on the face, standing: the head, from 15 deg below it, 1.0 m, on the camera's azimuth",
            MathF.Abs(MathF.Asin(fsAway.Y / fsAway.Length()) / Deg + 15f) < 1.5f && MathF.Abs(fsAway.Length() - 1.0f) < 0.05f && Reach(faceStanding.Out, HeadCentre(standing)) <= 1f &&
            RenderedOff(faceStanding.Out, 45f * Deg) < 0.05f,
            $"{MathF.Asin(fsAway.Y / fsAway.Length()) / Deg:F1} deg, {fsAway.Length():F2} m");
        var face = RunStatic(world, supine, DeathShotProfile.LowFace, 14f, 45f * Deg);
        Check("Low on the face, lying: head in frame, close, at ground level (the eye cannot go below the head), eye >= 10 cm up",
            Reach(face.Out, HeadCentre(supine)) <= 1f && Vector3.Distance(face.Out.BaseEye, HeadCentre(supine)) < 1.2f && face.Out.BaseEye.Y < 0.4f && face.MinClearance >= 0.0999f,
            $"{Vector3.Distance(face.Out.BaseEye, HeadCentre(supine)):F2} m, eye height {face.Out.BaseEye.Y:F2}");
        bool fullIn = true, fullLow = true, fullKept = true;
        float fullNear = float.MaxValue, fullFar = 0f;
        for (var k = 0; k < 8; k++)
        {
            var full = RunStatic(world, supine, DeathShotProfile.LowFullBody, 14f, k * 45f * Deg, extra: points);
            fullIn &= Reach(full.Out, points) <= 1f;
            fullLow &= full.Out.BaseEye.Y < 0.5f && full.MinClearance >= 0.0999f;
            fullKept &= RenderedOff(full.Out, k * 45f * Deg) < 0.05f;
            var d = Vector3.Distance(full.Out.BaseEye, full.Out.BaseLookAt);
            fullNear = MathF.Min(fullNear, d);
            fullFar = MathF.Max(fullFar, d);
        }
        Check("Low full body, lying, 8 start azimuths: the whole body in frame from each of them, low, farther than Low close, the azimuth kept",
            fullIn && fullLow && fullKept && fullNear >= 1.19f && fullFar > 1.6f, $"{fullNear:F2}..{fullFar:F2} m (along the body it is seen foreshortened and closer, across it farther)");

        // Every shot on every pose from every start azimuth: the azimuth is never changed, the subject is in frame.
        {
            var poses = new (string Name, BodyPoints Body)[] { ("standing", standing), ("kneeling", Kneeling), ("on the back", supine), ("on a side", Lying(90f * Deg)), ("face down", Lying(MathF.PI)) };
            var worstAzimuth = 0f;
            var worstRendered = 0f;
            var lost = "";
            foreach (var pose in poses)
            foreach (var profile in DeathShotProfiles.Choices)
            for (var k = 0; k < 8; k++)
            {
                var solver = new DeathShotSolver(world);
                var start = k * 45f * Deg;
                solver.Begin(pose.Body.Chest + DeathShotMath.OrbitDirection(start, 15f * Deg) * 5f, pose.Body.Chest, 0.78f);
                var extra = All(pose.Body);
                DeathShotOutput o = default;
                for (var i = 0; i < 60 * 8; i++)
                {
                    o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = pose.Body, Extra = extra, Profile = profile, Aspect = 16f / 9f, UserSize = 1f });
                    worstAzimuth = MathF.Max(worstAzimuth, MathF.Abs(DeathShotMath.AngleDelta(start, o.Azimuth)) / Deg);
                    worstRendered = MathF.Max(worstRendered, RenderedOff(o, start));
                }
                var subject = ShotRecipe.Of(profile).Subject switch
                {
                    ShotSubject.HipsToChest => new[] { pose.Body.Hips, pose.Body.Chest },
                    ShotSubject.Head => new[] { HeadCentre(pose.Body) },
                    _ => extra,
                };
                if (Reach(o, subject) > 1f) lost += $" {profile}/{pose.Name}/{k * 45}";
            }
            Check("all three shots x five poses x 8 start azimuths, no input: the shot's azimuth is the start camera's on every frame (0 deg change), the rendered one too, subject in frame",
                worstAzimuth < 1e-4f && worstRendered < 0.05f && lost.Length == 0,
                $"shot azimuth changed by at most {worstAzimuth:F5} deg, rendered eye azimuth off by at most {worstRendered:F3} deg{(lost.Length > 0 ? "; out of frame:" + lost : "")}");
        }

        // Constraints.
        var wet = new FlatWorld { Water = 0.4f };
        Check("water: the eye stays 10 cm above the surface", RunStatic(wet, supine, DeathShotProfile.LowFullBody, 6f, 0f, extra: points).Out.Eye.Y >= 0.4999f);

        // A wall 0.7 m from the body on the side the camera is on (start azimuth +90 deg = +X): the side is not
        // changed; the eye comes in front of the wall on the same line and the lens widens. The wall gone: back out.
        {
            var walled = new FlatWorld { WallX = 0.7f };
            var solver = new DeathShotSolver(walled);
            solver.Begin(supine.Chest + DeathShotMath.OrbitDirection(90f * Deg, 15f * Deg) * 5f, supine.Chest, 0.78f);
            DeathShotOutput o = default, blocked = default;
            var cuts = 0;
            var worst = 0f;
            float maxRate = 0f, maxSpeed = 0f;
            Vector3 lastDir = default, lastEye = default;
            for (var i = 0; i < 60 * 20; i++)
            {
                if (i == 60 * 10) { blocked = o; walled.WallX = float.PositiveInfinity; }
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = supine, Extra = points, Profile = DeathShotProfile.LowFullBody, Aspect = 16f / 9f, UserSize = 1f });
                if (o.Cut && i > 0) cuts++;
                worst = MathF.Max(worst, MathF.Abs(DeathShotMath.AngleDelta(90f * Deg, o.Azimuth)) / Deg);
                var dir = Vector3.Normalize(o.LookAt - o.Eye);
                if (i > 0)
                {
                    maxRate = MathF.Max(maxRate, MathF.Acos(Math.Clamp(Vector3.Dot(dir, lastDir), -1f, 1f)) * 60f / Deg);
                    maxSpeed = MathF.Max(maxSpeed, Vector3.Distance(o.Eye, lastEye) * 60f);
                }
                lastDir = dir;
                lastEye = o.Eye;
            }
            var open = RunStatic(world, supine, DeathShotProfile.LowFullBody, 10f, 90f * Deg, extra: points).Out;
            Check("a wall between the body and the camera: the azimuth is not changed; the eye is pulled in front of the wall and the lens widened (up to x1.35)",
                blocked.PulledIn && blocked.Eye.X < 0.7f && blocked.Eye.X > 0.3f && worst < 1e-4f && blocked.Fov > open.Fov * 1.05f && blocked.Fov <= ShotRecipe.LowFullBody.Fov * DeathShotSolver.MaxFovWiden + 1e-4f,
                $"eye x {blocked.Eye.X:F2} with the wall at 0.70 (open: {open.Eye.X:F2}), lens {blocked.Fov / Deg:F1} deg (open {open.Fov / Deg:F1}), azimuth changed {worst:F5} deg");
            Check("  the wall gone: eased back out to the open shot, no cut frame, view <= 60 deg/s, eye <= 3 m/s",
                !o.PulledIn && Vector3.Distance(o.BaseEye, open.BaseEye) < 0.02f && MathF.Abs(o.Fov - open.Fov) < 1e-3f && cuts == 0 && maxRate <= 60.5f && maxSpeed <= 3.01f,
                $"{Vector3.Distance(o.BaseEye, open.BaseEye) * 100f:F1} cm from the open shot, max view rate {maxRate:F1} deg/s, max eye speed {maxSpeed:F2} m/s");
        }

        // The same scene turned and moved gives the same shot, turned and moved.
        var yaw = 1.1f;
        var move = new Vector3(40f, 0f, -12f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var a = RunStatic(world, supine, DeathShotProfile.LowClose, 6f, 45f * Deg);
        var b = RunStatic(world, Map(supine, v => Vector3.Transform(v, q) + move), DeathShotProfile.LowClose, 6f, 45f * Deg + yaw);
        var error = Vector3.Distance(Vector3.Transform(a.Out.Eye, q) + move, b.Out.Eye) + Vector3.Distance(Vector3.Transform(a.Out.LookAt, q) + move, b.Out.LookAt);
        Check("turning and moving the scene turns and moves the shot", error < 2e-3f, $"difference {error * 1000f:F2} mm");

        // A bigger body gets a proportionally bigger shot.
        var big = Map(standing, v => v * 2f);
        var bigShot = RunStatic(world, big, DeathShotProfile.LowClose, 12f, 0f);
        var bigDistance = Vector3.Distance(bigShot.Out.BaseEye, bigShot.Out.BaseLookAt);
        Check("a body twice the size: the shot is twice as far", bigDistance is > 2.2f and < 2.7f && MathF.Abs(bigShot.BodyScale - 2f) < 0.05f, $"{bigDistance:F2} m, scale {bigShot.BodyScale:F2}");

        // No start camera: the first frame is the shot itself, from the body's front.
        var cold = RunStatic(world, standing, DeathShotProfile.LowClose, 1f / 60f, 0f, begin: false);
        Check("without a start camera the first frame is the solved shot (from the body's front)", Vector3.Distance(cold.Out.Eye, cold.Out.GoalEye) < 1e-4f && MathF.Abs(cold.Out.Azimuth) < 1e-4f);
    }

    /// <summary>What the plugin feeds from the player during a shot (DeathShotDirector): the left/right orbit as
    /// <see cref="DeathShotInput.UserYaw"/>, the wheel as <see cref="DeathShotInput.UserSize"/>.</summary>
    private static void PlayerHand()
    {
        var world = new FlatWorld();
        var body = Lying(90f * Deg);
        var extra = All(body);

        // Orbit: 4 s without input, then the player drags 120 deg in one second (120 deg/s: twice the cap the
        // program's own moves have), then lets go. The camera is on the asked azimuth on every frame, and stops with
        // the hand.
        foreach (var profile in DeathShotProfiles.Choices)
        {
            var solver = new DeathShotSolver(world);
            const float start = 30f * Deg;
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(start, 15f * Deg) * 5f, body.Chest, 0.78f);
            DeathShotOutput o = default;
            float worstShot = 0f, worstRendered = 0f, minClear = float.MaxValue, fastest = 0f, worstReach = 0f;
            var cuts = 0;
            Vector3 lastEye = default;
            for (var i = 0; i < 60 * 9; i++)
            {
                var t = i / 60f;
                var yaw = 120f * Deg * Math.Clamp(t - 4f, 0f, 1f);
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = profile, Aspect = 16f / 9f, UserYaw = yaw, UserSize = 1f });
                if (o.Cut && i > 0) cuts++;
                worstShot = MathF.Max(worstShot, MathF.Abs(DeathShotMath.AngleDelta(start + yaw, o.Azimuth)) / Deg);
                if (t >= 3.5f) worstRendered = MathF.Max(worstRendered, RenderedOff(o, start + yaw));
                if (t is > 4f and <= 5f) fastest = MathF.Max(fastest, MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(lastEye - o.LookAt), DeathShotMath.Azimuth(o.Eye - o.LookAt))) * 60f / Deg);
                if (t >= 4f) worstReach = MathF.Max(worstReach, Reach(o, ShotRecipe.Of(profile).Subject == ShotSubject.WholeBody ? extra : ShotRecipe.Of(profile).Subject == ShotSubject.Head ? new[] { HeadCentre(body) } : new[] { body.Hips, body.Chest }));
                minClear = MathF.Min(minClear, o.Eye.Y);
                lastEye = o.Eye;
            }
            Check($"{DeathShotProfiles.Label(profile)}: the player drags 120 deg in 1 s: the shot's azimuth is the hand's on every frame (one to one, {fastest:F0} deg/s rendered), no cut, eye >= 10 cm up, subject in frame",
                worstShot < 1e-3f && worstRendered < 1.0f && fastest > 110f && cuts == 0 && minClear >= 0.0999f && worstReach <= 1f,
                $"shot azimuth off the hand by at most {worstShot:F5} deg, the rendered eye by at most {worstRendered:F2} deg; reach {worstReach:F2}; min eye height {minClear:F3} m");
        }

        // No input, nothing turns: a still body for 20 s.
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(200f * Deg, 15f * Deg) * 5f, body.Chest, 0.78f);
            var worst = 0f;
            for (var i = 0; i < 60 * 20; i++)
            {
                var o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f });
                worst = MathF.Max(worst, RenderedOff(o, 200f * Deg));
            }
            Check("no input for 20 s (entry, settle, push-in): the eye never leaves the start camera's azimuth", worst < 0.05f, $"at most {worst:F4} deg off");
        }

        // Wheel: the frame at the subject grows and shrinks with the size.
        {
            float At(float size)
            {
                var solver = new DeathShotSolver(world);
                solver.Begin(Standing.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, Standing.Chest, 0.78f);
                DeathShotOutput o = default;
                for (var i = 0; i < 60 * 12; i++)
                    o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = Standing, Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = size });
                return Vector3.Distance(o.BaseEye, o.BaseLookAt);
            }
            float one = At(1f), wide = At(2f), tight = At(0.5f);
            Check("player wheel: size 2 is twice as far, size 0.5 half as far", MathF.Abs(wide / one - 2f) < 0.1f && MathF.Abs(tight / one - 0.5f) < 0.05f,
                $"{one:F2} m, x2 {wide:F2} m, x0.5 {tight:F2} m");
        }
    }

    /// <summary>What a run of the solver over a moving body did: for the checks that a change is eased, not cut, and
    /// that nothing but the player's hand turns the camera about the body.</summary>
    private sealed class Trace
    {
        public DeathShotOutput Last;
        public int CutFrames;
        public float MaxViewRate, MaxEyeSpeed;
        /// <summary>The shot's azimuth against the start camera's plus the input, degrees; and the rendered eye's.</summary>
        public float MaxAzimuthOff, MaxRenderedOff;
        public readonly List<string> Events = new();
    }

    private const float FollowStart = 30f * Deg;

    private static Trace Follow(IDeathShotWorld world, Func<float, BodyPoints> body, Func<float, DeathShotInput, DeathShotInput> configure, float seconds, Action<float, DeathShotOutput, BodyPoints>? each = null)
    {
        var solver = new DeathShotSolver(world);
        var first = body(0f);
        solver.Begin(first.Chest + DeathShotMath.OrbitDirection(FollowStart, 15f * Deg) * 5f, first.Chest, 0.78f);
        var trace = new Trace();
        Vector3 lastDir = default, lastEye = default;
        for (var i = 0; i < (int)(seconds * 60f); i++)
        {
            var time = i / 60f;
            var p = body(time);
            var input = configure(time, new DeathShotInput { Dt = 1f / 60f, Points = p, Extra = All(p), Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f });
            var o = solver.Step(input);
            var dir = Vector3.Normalize(o.LookAt - o.Eye);
            if (i > 0)
            {
                if (o.Cut) trace.CutFrames++;
                trace.MaxViewRate = MathF.Max(trace.MaxViewRate, MathF.Acos(Math.Clamp(Vector3.Dot(dir, lastDir), -1f, 1f)) * 60f / Deg);
                trace.MaxEyeSpeed = MathF.Max(trace.MaxEyeSpeed, Vector3.Distance(o.Eye, lastEye) * 60f);
            }
            trace.MaxAzimuthOff = MathF.Max(trace.MaxAzimuthOff, MathF.Abs(DeathShotMath.AngleDelta(FollowStart + input.UserYaw, o.Azimuth)) / Deg);
            trace.MaxRenderedOff = MathF.Max(trace.MaxRenderedOff, RenderedOff(o, FollowStart + input.UserYaw));
            if (o.ShotChanged) trace.Events.Add($"{time:F1}s {o.Reason} -> {o.Shot}");
            lastDir = dir;
            lastEye = o.Eye;
            trace.Last = o;
            each?.Invoke(time, o, p);
        }
        return trace;
    }

    /// <summary>A change of shot is eased, never cut; and through a fall, a ragdoll, a move of the body, nothing turns
    /// the camera about it.</summary>
    private static void SoftChanges()
    {
        var world = new FlatWorld();
        // Standing, then falling face down over 1.5 s from t = 2 s, then lying.
        BodyPoints Falling(float t)
        {
            var k = DeathShotMath.SmoothStep((t - 2f) / 1.5f);
            return Map(Turn(Standing, Vector3.UnitX, MathF.PI / 2f * k), v => v + new Vector3(0f, 0.12f * k, 0f));
        }
        foreach (var profile in DeathShotProfiles.Choices)
        {
            var fall = Follow(world, Falling, (_, input) => input with { Profile = profile }, 16f);
            Check($"{DeathShotProfiles.Label(profile)} over a fall from standing to face down, no input: the azimuth never changes, no cut frame, view <= 60 deg/s, eye <= 3 m/s, the shot holds once the body rests",
                fall.MaxAzimuthOff < 1e-4f && fall.CutFrames == 0 && fall.MaxViewRate <= 60.5f && fall.MaxEyeSpeed <= 3.01f && fall.Events.Count == 0 && fall.Last.Holding,
                $"shot azimuth changed {fall.MaxAzimuthOff:F5} deg (the rendered eye strays at most {fall.MaxRenderedOff:F2} deg while the caps hold it back); max view rate {fall.MaxViewRate:F1} deg/s, max eye speed {fall.MaxEyeSpeed:F2} m/s");
        }

        // The dropdown changed while dead, through the three shots and back.
        var lying = Lying(90f * Deg);
        var order = new[] { DeathShotProfile.LowClose, DeathShotProfile.LowFace, DeathShotProfile.LowFullBody, DeathShotProfile.LowClose };
        var tour = Follow(world, _ => lying, (t, input) => input with { Profile = order[Math.Min(order.Length - 1, (int)(t / 6f))] }, order.Length * 6f);
        Check("the dropdown changed while dead, through the three shots and back: every change eased (no cut frame), view <= 60 deg/s, eye <= 3 m/s, the azimuth untouched",
            tour.Events.Count == order.Length - 1 && tour.CutFrames == 0 && tour.MaxViewRate <= 60.5f && tour.MaxEyeSpeed <= 3.01f && tour.Last.Shot == order[^1] && tour.MaxAzimuthOff < 1e-4f && tour.MaxRenderedOff < 0.5f,
            $"{string.Join("; ", tour.Events)}; max view rate {tour.MaxViewRate:F1} deg/s, max eye speed {tour.MaxEyeSpeed:F2} m/s, rendered azimuth off by at most {tour.MaxRenderedOff:F2} deg");

        // A body at rest that is moved and comes to rest turned by 150 deg: the camera follows the body, on the same
        // azimuth (it does not go round to the body's front again).
        BodyPoints Moved(float t)
        {
            var k = DeathShotMath.SmoothStep((t - 6f) / 1f);
            return Map(Turn(lying, Vector3.UnitY, 150f * Deg * k), v => v + new Vector3(0.6f * k, 0f, 0f));
        }
        var much = Follow(world, Moved, (_, input) => input, 16f);
        Check("a resting body moved and turned 150 deg: the camera goes with it on the same azimuth, no change of shot, no cut frame",
            much.Events.Count == 0 && much.CutFrames == 0 && much.MaxAzimuthOff < 1e-4f && much.MaxViewRate <= 60.5f && Reach(much.Last, Moved(16f).Hips, Moved(16f).Chest) <= 1f,
            $"shot azimuth changed {much.MaxAzimuthOff:F5} deg, max view rate {much.MaxViewRate:F1} deg/s");

        // Below the ground and back above it, with the option switched off under way: the eye comes up through the ground, no jump.
        var solid = new SolidGroundWorld();
        var maxStep = 0f;
        Vector3 previous = default;
        var up = Follow(solid, _ => lying, (t, input) => input with { AllowBelowGround = t < 6f }, 12f, (t, o, _) =>
        {
            if (t > 6f) maxStep = MathF.Max(maxStep, Vector3.Distance(o.Eye, previous));
            previous = o.Eye;
        });
        Check("below ground, then the option switched off: the eye comes up through the ground at no more than 3 m/s, no cut frame, and ends above it",
            up.CutFrames == 0 && maxStep <= 3.01f / 60f && up.Last.Eye.Y >= 0.0999f, $"largest step {maxStep * 100f:F1} cm in a frame, eye height at the end {up.Last.Eye.Y:F2} m");
    }

    /// <summary>A body that is caught, held up swinging, and let go (the recordings have no grabs).</summary>
    private static void Grabbed()
    {
        var world = new FlatWorld();
        var lying = Lying(90f * Deg);
        // Hanging by the neck region 0.45 m off the ground, upright, facing +Z: swings ±20 deg at 0.5 Hz about a
        // point above the head and twists ±30 deg at 0.3 Hz.
        BodyPoints Hanging(float t)
        {
            var lifted = Map(Standing, v => v + new Vector3(0f, 0.45f, 0f));
            var pivot = new Vector3(0f, 2.3f, 0f);
            var swung = Turn(lifted, Vector3.UnitX, 20f * Deg * MathF.Sin(MathF.Tau * 0.5f * t), pivot);
            return Turn(swung, Vector3.UnitY, 30f * Deg * MathF.Sin(MathF.Tau * 0.3f * t));
        }
        // 0-4 s lying; caught at 4 s and lifted over 1 s; held until 16 s; let go, down over 0.8 s; lying again.
        BodyPoints Scene(float t)
        {
            if (t < 4f) return lying;
            if (t < 16f) return BodyPoints.Lerp(lying, Hanging(t), DeathShotMath.SmoothStep((t - 4f) / 1f));
            return BodyPoints.Lerp(Hanging(16f), lying, DeathShotMath.SmoothStep((t - 16f) / 0.8f));
        }
        foreach (var framing in new[] { DeathShotGrabbedFraming.Level, DeathShotGrabbedFraming.SlightlyAbove })
        {
            var level = framing == DeathShotGrabbedFraming.Level;
            var shot = level ? DeathShotProfile.GrabbedLevel : DeathShotProfile.GrabbedAbove;

            // A body hanging still: the distance of the upper body (as before b2f76d7: Level 1.515 m, Slightly above
            // 1.545 m on this body), the head near the top of the frame.
            {
                var still = Map(Standing, v => v + new Vector3(0f, 0.45f, 0f));
                var solver = new DeathShotSolver(world);
                solver.Begin(still.Chest + DeathShotMath.OrbitDirection(FollowStart, 15f * Deg) * 5f, still.Chest, 0.78f);
                DeathShotOutput o = default;
                for (var i = 0; i < 60 * 8; i++)
                    o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = still, Extra = All(still), Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f, Grabbed = true, GrabbedFraming = framing });
                // (Before the push-in of a body at rest.)
                var basePose = o;
                basePose.Eye = o.BaseEye;
                basePose.LookAt = o.BaseLookAt;
                float top = UpFrame(basePose, HeadTop(still)), thigh = UpFrame(basePose, MidThighs(still)), hips = UpFrame(basePose, still.Hips), chest = UpFrame(basePose, still.Chest);
                var topPushed = UpFrame(o, HeadTop(still));
                var distanceNow = Vector3.Distance(o.BaseEye, o.BaseLookAt);
                var wantDistance = level ? 1.515f : 1.545f;
                // How far down the body the frame's lower edge reaches (on the line hips → mid-thigh → feet).
                var feet = (still.FootL + still.FootR) * 0.5f;
                var downTo = UpFrame(basePose, feet) >= 0f ? "the feet" : thigh < 0f ? hips < 0f ? "above the hips" : "between the hips and mid-thigh" : "just below mid-thigh";
                Check($"grabbed, {(level ? "Level" : "Slightly above")}, a body hanging still: the distance of before b2f76d7 ({wantDistance:F3} m), headroom 8-10 % of the frame's height (also after the push-in), the chest about the middle of the frame",
                    o.Shot == shot && MathF.Abs(distanceNow - wantDistance) < 0.002f && 1f - top is >= 0.08f and <= 0.10f && 1f - topPushed is >= 0.03f and <= 0.10f &&
                    chest is > 0.4f and < 0.85f && MathF.Abs(DeathShotMath.AngleDelta(FollowStart, o.Azimuth)) < 1e-5f,
                    $"{distanceNow:F3} m away, FoV {o.Fov:F2}; head top at {top * 100f:F1} % of the frame's height (headroom {100f - top * 100f:F1} %; {100f - topPushed * 100f:F1} % after the push-in), " +
                    $"chest at {chest * 100f:F1} %, hips at {hips * 100f:F1} %, mid-thigh at {thigh * 100f:F1} %: the frame reaches {downTo} " +
                    $"({hips * Vector3.Dot(still.Hips - o.BaseEye, Vector3.Normalize(o.BaseLookAt - o.BaseEye)) * 2f * MathF.Tan(o.Fov * 0.5f):F2} m of the frame's height below the hips)");
            }

            long held = 0, headIn = 0, hipsIn = 0;
            float minDown = float.MaxValue, maxDown = float.MinValue, eyeTravel = 0f, bodyTravel = 0f, minHeadroom = float.MaxValue, maxHeadroom = 0f;
            Vector3 lastEye = default, lastChest = default;
            var trace = Follow(world, Scene, (t, input) => input with { Grabbed = t is >= 4f and < 16f, GrabbedFraming = framing }, 26f, (t, o, p) =>
            {
                if (t is >= 9f and < 16f)
                {
                    held++;
                    if (Reach(o, HeadTop(p), HeadCentre(p), p.Chest) <= 1f) headIn++;
                    if (Reach(o, p.Hips) <= 1f) hipsIn++;
                    var headroom = 1f - UpFrame(o, HeadTop(p));
                    minHeadroom = MathF.Min(minHeadroom, headroom);
                    maxHeadroom = MathF.Max(maxHeadroom, headroom);
                    var view = Vector3.Normalize(o.LookAt - o.Eye);
                    var down = -MathF.Asin(view.Y) / Deg;
                    minDown = MathF.Min(minDown, down);
                    maxDown = MathF.Max(maxDown, down);
                    eyeTravel += Vector3.Distance(o.Eye, lastEye);
                    bodyTravel += Vector3.Distance(p.Chest, lastChest);
                }
                lastEye = o.Eye;
                lastChest = p.Chest;
            });
            var grabbedEvent = trace.Events.FirstOrDefault(e => e.Contains("grabbed")) ?? "";
            var releasedEvent = trace.Events.FirstOrDefault(e => e.Contains("released")) ?? "";
            Check($"  caught, lifted, swinging ±20 deg and twisting ±30 deg: eased into the held framing; head top and chest in frame on every held frame, " +
                  (level ? "the view level" : "looking down 12-22 deg"),
                grabbedEvent.Contains(shot.ToString()) && headIn == held && minHeadroom > 0f && (level ? minDown > -6f && maxDown < 6f : minDown > 12f && maxDown < 22f),
                $"{grabbedEvent}; head top and chest in frame {100.0 * headIn / held:F1}% of the held frames, the hips {100.0 * hipsIn / held:F1}%; headroom {minHeadroom * 100f:F1}..{maxHeadroom * 100f:F1} % of the frame's height as it swings; view {minDown:F1}..{maxDown:F1} deg down");
            Check("  the azimuth is not touched by the grab, the swings or the release; the camera moves less than the body; no cut frame, view <= 60 deg/s",
                trace.MaxAzimuthOff < 1e-4f && eyeTravel < bodyTravel * 0.7f && trace.CutFrames == 0 && trace.MaxViewRate <= 60.5f,
                $"shot azimuth changed {trace.MaxAzimuthOff:F5} deg; the eye travelled {eyeTravel:F2} m, the chest {bodyTravel:F2} m; max view rate {trace.MaxViewRate:F1} deg/s");
            Check("  let go: back to the shot of the lying body once it lies still, eased",
                releasedEvent.Contains("LowClose") && trace.Last.Shot == DeathShotProfile.LowClose && !trace.Last.Grabbed && trace.Last.Holding && trace.Events.Count == 2,
                $"{string.Join("; ", trace.Events)}");
        }

        // The player's hand while held: one to one, like everywhere else.
        var orbit = Follow(world, Scene, (t, input) => input with { Grabbed = t is >= 4f and < 16f, UserYaw = 90f * Deg * Math.Clamp((t - 9f) / 1.5f, 0f, 1f) }, 14f);
        Check("grabbed, the player drags 90 deg in 1.5 s: the azimuth is the hand's on every frame, no cut frame",
            orbit.MaxAzimuthOff < 1e-3f && orbit.CutFrames == 0, $"shot azimuth off the hand by at most {orbit.MaxAzimuthOff:F5} deg, the rendered eye by at most {orbit.MaxRenderedOff:F2} deg");
    }

    /// <summary>Where each shot sits (distance, elevation, eye height) on the reference body in each pose: for the
    /// documentation. The azimuth is whatever the camera had, so it is not listed.</summary>
    public static void Where()
    {
        Console.WriteLine("reference body (1.7 m), flat ground; the camera across the body (start azimuth 90 deg off its long axis / its front)");
        Console.WriteLine($"{"shot",-32} {"m",5} {"elevation deg",14} {"eye height m",13}  subject");
        foreach (var pose in new (string Name, BodyPoints Body)[] { ("on the back", Lying(0f)), ("on a side", Lying(90f * Deg)), ("face down", Lying(MathF.PI)), ("standing", Standing) })
        {
            Console.WriteLine($"-- {pose.Name}");
            foreach (var below in new[] { false, true })
            foreach (var profile in DeathShotProfiles.Choices)
            {
                var solver = new DeathShotSolver(new SolidGroundWorld());
                solver.Begin(pose.Body.Chest + DeathShotMath.OrbitDirection(90f * Deg, 15f * Deg) * 5f, pose.Body.Chest, 0.78f);
                DeathShotOutput o = default;
                for (var i = 0; i < 60 * 8; i++)
                    o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = pose.Body, Extra = All(pose.Body), Profile = profile, Aspect = 16f / 9f, UserSize = 1f, AllowBelowGround = below });
                var away = o.BaseEye - o.BaseLookAt;
                Console.WriteLine($"{DeathShotProfiles.Label(profile) + (below ? " (below ground)" : ""),-32} {away.Length(),5:F2} {MathF.Asin(away.Y / away.Length()) / Deg,14:F0} {o.BaseEye.Y,13:F2}  {ShotRecipe.Of(o.Shot).Subject}");
            }
        }
    }

    /// <summary>The remembered direction: a shot given one turns to it from the camera's own azimuth
    /// (DeathShotSolver.Begin; the plugin gives the body's facing at death + the angle the player left the last
    /// death's shot on).</summary>
    private static void Remembered()
    {
        var world = new FlatWorld();
        var body = Lying(90f * Deg);
        var extra = All(body);
        // One run: start azimuth, remembered azimuth (none = null), the player's orbit over time.
        (List<DeathShotOutput> Frames, int Cuts) Run(float start, float? target, Func<float, float>? yaw = null, float seconds = 6f, BodyPoints? other = null)
        {
            var p = other ?? body;
            var solver = new DeathShotSolver(world);
            solver.Begin(p.Chest + DeathShotMath.OrbitDirection(start, 15f * Deg) * 5f, p.Chest, 0.78f, target);
            var frames = new List<DeathShotOutput>();
            var cuts = 0;
            for (var i = 0; i < (int)(seconds * 60f); i++)
            {
                var o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = p, Extra = other == null ? extra : All(p), Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f, UserYaw = yaw?.Invoke(i / 60f) ?? 0f });
                if (o.Cut && i > 0) cuts++;
                frames.Add(o);
            }
            return (frames, cuts);
        }
        static float Travel(List<DeathShotOutput> f) => f.Zip(f.Skip(1), (a, b) => MathF.Abs(DeathShotMath.AngleDelta(a.Azimuth, b.Azimuth))).Sum() / Deg;
        static float Fastest(List<DeathShotOutput> f) => f.Zip(f.Skip(1), (a, b) => MathF.Abs(DeathShotMath.AngleDelta(a.Azimuth, b.Azimuth))).Max() * 60f / Deg;

        // 140 deg to go: reaches it, on a smooth profile, in about a second and a half, and stays.
        {
            var (f, cuts) = Run(30f * Deg, 170f * Deg);
            var arrived = f.FindIndex(o => !o.Recalling) / 60f;
            var last = f[^1];
            var firstStep = MathF.Abs(DeathShotMath.AngleDelta(f[0].Azimuth, f[1].Azimuth)) * 60f / Deg;
            Check("a remembered direction 140 deg from the camera: the shot turns to it (exactly), eased from rest to rest in 1.2-1.6 s, never faster than 150 deg/s, no cut frame, the rendered eye on it at the end",
                MathF.Abs(DeathShotMath.AngleDelta(170f * Deg, last.Azimuth)) < 1e-5f && RenderedOff(last, 170f * Deg) < 0.05f && arrived is >= 1.2f and <= 1.6f && Fastest(f) <= 151f && firstStep < 5f &&
                MathF.Abs(Travel(f) - 140f) < 0.01f && cuts == 0 && Reach(last, body.Hips, body.Chest) <= 1f,
                $"arrived after {arrived:F2} s, fastest {Fastest(f):F0} deg/s (first frame {firstStep:F1} deg/s), turned {Travel(f):F1} deg in all");
        }

        // The shorter way round, across the +-180 deg seam, and the longest turn there is.
        {
            var (f, _) = Run(170f * Deg, -170f * Deg);
            var (half, _) = Run(0f, 179f * Deg);
            Check("from 170 deg to -170 deg: 20 deg the short way (not 340); a turn of 179 deg takes longer instead of going faster than 150 deg/s",
                MathF.Abs(Travel(f) - 20f) < 0.01f && MathF.Abs(DeathShotMath.AngleDelta(-170f * Deg, f[^1].Azimuth)) < 1e-5f &&
                MathF.Abs(Travel(half) - 179f) < 0.01f && Fastest(half) <= 151f && half.FindIndex(o => !o.Recalling) / 60f is > 1.7f and < 1.9f,
                $"turned {Travel(f):F1} deg; the 179 deg turn: {half.FindIndex(o => !o.Recalling) / 60f:F2} s, fastest {Fastest(half):F0} deg/s");
        }

        // The player's hand during the turn: it ends the turn where it is, and the azimuth is theirs from there.
        {
            // From 0.5 s the player drags 30 deg over half a second, then holds still.
            float Hand(float t) => 30f * Deg * Math.Clamp((t - 0.5f) / 0.5f, 0f, 1f);
            var (f, cuts) = Run(30f * Deg, 170f * Deg, Hand);
            var cancelled = f.FindIndex(o => !o.Recalling);
            var offsetThen = f[cancelled].RecallOffset;
            var worst = 0f;
            var jump = 0f;
            for (var i = cancelled; i < f.Count; i++)
            {
                worst = MathF.Max(worst, MathF.Abs(DeathShotMath.AngleDelta(30f * Deg + offsetThen + Hand(i / 60f), f[i].Azimuth)) / Deg);
                jump = MathF.Max(jump, MathF.Abs(DeathShotMath.AngleDelta(f[i - 1].Azimuth, f[i].Azimuth)) / Deg - MathF.Abs(Hand(i / 60f) - Hand((i - 1) / 60f)) / Deg);
            }
            var still = MathF.Abs(DeathShotMath.AngleDelta(f[^1].Azimuth, f[90].Azimuth)) / Deg;
            Check("the player drags during the turn: the turn stops there (it does not go on to the remembered direction), nothing jumps, and from then on the azimuth moves with the hand alone",
                cancelled / 60f is > 0.5f and < 0.6f && worst < 1e-3f && jump < 1e-3f && still < 1e-3f && cuts == 0 && MathF.Abs(DeathShotMath.AngleDelta(170f * Deg, f[^1].Azimuth)) > 20f * Deg,
                $"stopped at {cancelled / 60f:F2} s, {offsetThen / Deg:F1} deg of the 140 done; after that off the hand by at most {worst:F5} deg; 1 s after the hand stopped the camera had moved {still:F5} deg");
            // A hand that barely moves (under 1 deg in all) does not stop it.
            var (g, _) = Run(30f * Deg, 170f * Deg, t => 0.5f * Deg * MathF.Sin(t * 20f));
            Check("a hand that moves less than 1 deg does not stop the turn", MathF.Abs(g.Last(o => o.Recalling).RecallOffset / Deg - 140f) < 5f && !g[^1].Recalling && MathF.Abs(g[^1].RecallOffset / Deg - 140f) < 1e-3f);
        }

        // Nothing remembered: the shot of before, bit for bit; and a remembered direction equal to the camera's own
        // changes nothing either.
        {
            var (a, _) = Run(30f * Deg, null);
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(30f * Deg, 15f * Deg) * 5f, body.Chest, 0.78f);
            var same = true;
            foreach (var o in a)
            {
                var b = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.LowClose, Aspect = 16f / 9f, UserSize = 1f });
                same &= o.Eye == b.Eye && o.LookAt == b.LookAt && o.Fov == b.Fov && o.RecallOffset == 0f && !o.Recalling;
            }
            var (c, _) = Run(30f * Deg, 30f * Deg);
            Check("nothing remembered: the same output as a shot started the old way, bit for bit, and the azimuth never changes; a remembered direction equal to the camera's: no turn",
                same && Travel(a) < 1e-4f && Travel(c) < 1e-3f);
        }

        // The same angle from the facing gives the same picture on a body that faced another way: what the plugin
        // stores is relative to the facing at death.
        {
            const float angle = 140f * Deg;
            var turned = Turn(body, Vector3.UnitY, 75f * Deg);
            var (a, _) = Run(30f * Deg, 0f + angle);
            var (b, _) = Run(200f * Deg, 75f * Deg + angle, other: turned);
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 75f * Deg);
            var error = Vector3.Distance(Vector3.Transform(a[^1].Eye, q), b[^1].Eye) + Vector3.Distance(Vector3.Transform(a[^1].LookAt, q), b[^1].LookAt);
            Check("facing + the same remembered angle on a body that faced 75 deg another way, from another start camera: the same picture, turned with the body", error < 2e-3f, $"difference {error * 1000f:F2} mm");
        }
    }

    /// <summary>Flat ground at y = 0 that stops rays the way the game's does (a ray that goes under it is stopped where
    /// it crosses), and optionally a wall: the plane x = WallX, from the ground up.</summary>
    private sealed class SolidGroundWorld : IDeathShotWorld
    {
        public float WallX = float.PositiveInfinity;
        public float? GroundBelow(Vector3 from) => 0f;
        public float? WaterBelow(Vector3 from) => null;
        public bool Blocked(Vector3 from, Vector3 to, out float distance) => Blocked(from, to, out distance, out _);
        public bool Blocked(Vector3 from, Vector3 to, out float distance, out Vector3 normal)
        {
            distance = float.MaxValue;
            normal = default;
            var length = Vector3.Distance(from, to);
            if (from.Y > 0f && to.Y < 0f)
            {
                distance = length * from.Y / (from.Y - to.Y);
                normal = Vector3.UnitY;
            }
            if (from.X < WallX != to.X < WallX)
            {
                var t = MathF.Abs((WallX - from.X) / (to.X - from.X));
                if ((from + (to - from) * t).Y >= 0f && length * t < distance)
                {
                    distance = length * t;
                    normal = new Vector3(from.X < WallX ? -1f : 1f, 0f, 0f);
                }
            }
            if (distance == float.MaxValue) { distance = 0f; return false; }
            return true;
        }
    }

    /// <summary>The option "Allow below ground".</summary>
    private static void BelowGround()
    {
        var body = Lying(90f * Deg);
        var extra = All(body);
        const float start = 90f * Deg;
        DeathShotOutput Run(IDeathShotWorld world, DeathShotProfile profile, bool below, out float minEye)
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(start, 15f * Deg) * 5f, body.Chest, 0.78f);
            DeathShotOutput o = default;
            minEye = float.MaxValue;
            for (var i = 0; i < 60 * 12; i++)
            {
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = profile, Aspect = 16f / 9f, UserSize = 1f, AllowBelowGround = below });
                minEye = MathF.Min(minEye, o.Eye.Y);
            }
            return o;
        }

        // Low close on a body on the ground: the old shots' placement, under the ground, and the ground it goes
        // under is not "in the way" (nothing pulled in).
        var open = new SolidGroundWorld();
        var low = Run(open, DeathShotProfile.LowClose, true, out _);
        var away = low.BaseEye - low.BaseLookAt;
        var up = -MathF.Asin(away.Y / away.Length()) / Deg;
        Check("below ground allowed: Low close on a lying body looks up 22 deg from 1.25 m, eye under the ground, not pulled in, on the camera's azimuth",
            MathF.Abs(up - 22f) < 0.5f && MathF.Abs(away.Length() - 1.25f) < 0.03f && low.BaseEye.Y < -0.1f && !low.PulledIn && low.Shot == DeathShotProfile.LowClose && RenderedOff(low, start) < 0.05f,
            $"up {up:F1} deg, {away.Length():F2} m, eye y {low.BaseEye.Y:F2}");

        // The same body with the option off, every shot of the dropdown: above the ground.
        var offMin = float.MaxValue;
        foreach (var profile in DeathShotProfiles.Choices)
        {
            Run(open, profile, false, out var min);
            offMin = MathF.Min(offMin, min);
        }
        Check("option off, all three shots: the eye never under 10 cm", offMin >= 0.0999f, $"min eye y {offMin:F3}");

        // A wall still blocks a below-ground shot: put where the view of the open shot crosses it above the ground
        // (close to the body: further out the view is already under the ground, and under the wall). The eye comes
        // in front of the wall on the same line; the azimuth stays.
        var wallX = low.Eye.X * 0.2f;
        var beside = Run(new SolidGroundWorld { WallX = wallX }, DeathShotProfile.LowClose, true, out _);
        Check("below ground allowed: a wall between still blocks (the eye is pulled in to its least distance, the wall being nearer than that; the azimuth stays)",
            MathF.Abs(low.Eye.X) > 0.3f && beside.PulledIn && Vector3.Distance(beside.BaseEye, beside.BaseLookAt) < 0.5f && RenderedOff(beside, start) < 0.05f,
            $"eye {beside.Eye.X:F2}, {beside.Eye.Y:F2}, {beside.Eye.Z:F2} with the wall at x {wallX:F2} (without it {low.Eye.X:F2}, {low.Eye.Y:F2}, {low.Eye.Z:F2}), {Vector3.Distance(beside.BaseEye, beside.BaseLookAt):F2} m from the subject");
    }
}
