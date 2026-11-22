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
        Console.WriteLine(failures == 0 ? "unit checks: all ok" : $"unit checks: {failures} FAILED");
        return failures;
    }

    /// <summary>What the plugin feeds from the player during a shot (DeathShotDirector): the left/right orbit as
    /// <see cref="DeathShotInput.UserYaw"/>, the wheel as <see cref="DeathShotInput.UserSize"/>.</summary>
    private static void PlayerHand()
    {
        var world = new FlatWorld();
        var body = Lying(90f * Deg);
        var extra = All(body);

        // Orbit: the shot turns by what the player turned, at the solver's own pace, without re-picking a side,
        // leaving the ground or losing the body.
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, body.Chest, 0.78f);
            DeathShotOutput o = default;
            Vector3 lastDir = default;
            float before = 0f, maxRate = 0f, minClear = float.MaxValue;
            var sides = 0;
            for (var i = 0; i < 60 * 20; i++)
            {
                var yaw = i < 60 * 10 ? 0f : 60f * Deg;
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.GroundProfile, Aspect = 16f / 9f, UserYaw = yaw, UserSize = 1f });
                if (i == 60 * 10 - 1)
                {
                    before = DeathShotMath.Azimuth(o.BaseEye - o.BaseLookAt);
                    sides = o.SideChanges;
                }
                var dir = Vector3.Normalize(o.LookAt - o.Eye);
                if (i > 0 && !o.Cut) maxRate = MathF.Max(maxRate, MathF.Acos(Math.Clamp(Vector3.Dot(dir, lastDir), -1f, 1f)) * 60f / Deg);
                lastDir = dir;
                minClear = MathF.Min(minClear, o.Eye.Y);
            }
            var turned = DeathShotMath.AngleDelta(before, DeathShotMath.Azimuth(o.BaseEye - o.BaseLookAt)) / Deg;
            Check("player orbit of 60 deg: the shot turns 60 deg about the body, same side choice, view <= 60 deg/s, eye >= 10 cm up, body in frame",
                MathF.Abs(turned - 60f) < 2f && o.SideChanges == sides && maxRate <= 60.5f && minClear >= 0.0999f && Reach(o, extra) <= 1f,
                $"turned {turned:F1} deg, max view rate {maxRate:F1} deg/s, min eye height {minClear:F3} m, reach {Reach(o, extra):F2}, side changes {o.SideChanges - sides}");
        }

        // A scene's absolute azimuth (the plugin turns the side in use onto the yaw the scene set: UserYaw = that yaw
        // minus DeathShotSolver.SideAzimuth): the eye ends at that azimuth about the body.
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, body.Chest, 0.78f);
            const float asked = 200f * Deg;
            DeathShotOutput o = default;
            for (var i = 0; i < 60 * 20; i++)
            {
                var yaw = i < 60 * 10 ? 0f : DeathShotMath.AngleDelta(solver.SideAzimuth, asked);
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.GroundProfile, Aspect = 16f / 9f, UserYaw = yaw, UserSize = 1f });
            }
            var off = DeathShotMath.AngleDelta(asked, DeathShotMath.Azimuth(o.BaseEye - o.BaseLookAt)) / Deg;
            Check("an absolute azimuth asked through the side in use: the eye ends there", MathF.Abs(off) < 2f, $"off by {off:F1} deg");
        }

        // Wheel: the frame at the subject grows and shrinks with the size.
        {
            float At(float size)
            {
                var solver = new DeathShotSolver(world);
                solver.Begin(Standing.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, Standing.Chest, 0.78f);
                DeathShotOutput o = default;
                for (var i = 0; i < 60 * 12; i++)
                    o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = Standing, Profile = DeathShotProfile.LowThreeQuarter, Aspect = 16f / 9f, UserSize = size });
                return Vector3.Distance(o.BaseEye, o.BaseLookAt);
            }
            float one = At(1f), wide = At(2f), tight = At(0.5f);
            Check("player wheel: size 2 is twice as far, size 0.5 half as far", MathF.Abs(wide / one - 2f) < 0.1f && MathF.Abs(tight / one - 0.5f) < 0.05f,
                $"{one:F2} m, x2 {wide:F2} m, x0.5 {tight:F2} m");
        }
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

    private static void SolverStatic()
    {
        var world = new FlatWorld();
        var standing = Standing;
        var frame = BodyFrame.From(standing, 0f);

        // Low three-quarter on a standing body, from every start azimuth: the old presets.
        bool inRange = true, framed = true, rate = true, clear = true;
        var detail = "";
        for (var k = 0; k < 8; k++)
        {
            var r = RunStatic(world, standing, DeathShotProfile.LowThreeQuarter, 12f, k * 45f * Deg);
            // Before the push-in: the damped pose without the built-in motion.
            var away = r.Out.BaseEye - r.Out.BaseLookAt;
            var d = away.Length();
            var off = MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frame.FrontHorizontal), DeathShotMath.Azimuth(away))) / Deg;
            var up = -MathF.Asin(away.Y / d) / Deg;
            inRange &= off is >= 39f and <= 46f && up is >= 19f and <= 26f && d is >= 1.0f and <= 1.35f;
            framed &= Reach(r.Out, standing.Hips, standing.Chest) <= 1f;
            rate &= r.MaxViewRate <= 60.5f;
            clear &= r.MinClearance >= 0.0999f;
            if (k == 0) detail = $"off {off:F1} deg, up {up:F1} deg, {d:F2} m; max view rate {r.MaxViewRate:F1} deg/s";
        }
        Check("Low three-quarter, standing, 8 start azimuths: 39-46 deg off the front, 19-26 deg up, 1.0-1.35 m", inRange, detail);
        Check("  hips and chest in frame, view <= 60 deg/s all the way in, eye >= 10 cm above ground", framed && rate && clear);

        // A body at rest gets the push-in: 12 % closer after 1 + 8 s, and then nothing moves.
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(standing.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, standing.Chest, 0.78f);
            DeathShotOutput o = default, before = default;
            var moved = 0f;
            for (var i = 0; i < 60 * 16; i++)
            {
                var prev = o;
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = Lying(90f * Deg), Profile = DeathShotProfile.LowThreeQuarter, Aspect = 16f / 9f, UserSize = 1f });
                if (i == 60 * 5) before = o;
                if (i > 60 * 14) moved = MathF.Max(moved, Vector3.Distance(o.Eye, prev.Eye));
            }
            var ratio = Vector3.Distance(o.Eye, o.LookAt) / Vector3.Distance(o.BaseEye, o.BaseLookAt);
            Check("settled body: push-in of 12 % over 8 s, then the camera is still", o.Holding && MathF.Abs(ratio - 0.88f) < 0.01f && moved < 1e-5f, $"ratio {ratio:F3}, movement after {moved * 1000f:F3} mm");
        }

        // Each profile on a body lying on its back.
        var supine = Lying(0f);
        var sf = BodyFrame.From(supine, 0f);
        var points = All(supine);
        var face = RunStatic(world, supine, DeathShotProfile.Face, 14f, 45f * Deg);
        var headCentre = supine.Head + Vector3.Normalize(supine.Head - supine.Chest) * 0.09f;
        Check("Face, supine: head in frame, the eye is in front of the face, above the ground",
            Reach(face.Out, headCentre) <= 1f && Vector3.Dot(sf.Front, Vector3.Normalize(face.Out.Eye - headCentre)) > 0.5f && face.MinClearance >= 0.0999f,
            $"front . to-eye {Vector3.Dot(sf.Front, Vector3.Normalize(face.Out.Eye - headCentre)):F2}");
        var pronePoints = Lying(MathF.PI);
        var pf = BodyFrame.From(pronePoints, 0f);
        var faceDown = RunStatic(world, pronePoints, DeathShotProfile.Face, 14f, 45f * Deg);
        var proneHead = pronePoints.Head + Vector3.Normalize(pronePoints.Head - pronePoints.Chest) * 0.09f;
        var sideways = MathF.Abs(Vector3.Dot(pf.Lateral, Vector3.Normalize(faceDown.Out.Eye - proneHead)));
        Check("Face, face down: the eye goes beside the head (the profile), above the ground", sideways > 0.8f && faceDown.MinClearance >= 0.0999f && Reach(faceDown.Out, proneHead) <= 1f, $"|lateral . to-eye| {sideways:F2}");

        var along = RunStatic(world, supine, DeathShotProfile.AlongBody, 14f, 45f * Deg);
        var beyondFeet = Vector3.Dot(along.Out.Eye - supine.FeetMid, -sf.LongAxis);
        Check("Along the body: whole body in frame, eye beyond the feet looking toward the head, 0.15-0.30 m up",
            Reach(along.Out, points) <= 1f && beyondFeet > 0.3f && Vector3.Dot(Vector3.Normalize(along.Out.LookAt - along.Out.Eye), sf.LongAxis) > 0.9f && along.Out.Eye.Y is >= 0.15f and <= 0.30f,
            $"{beyondFeet:F2} m beyond the feet, eye height {along.Out.Eye.Y:F2}");
        var over = RunStatic(world, supine, DeathShotProfile.Overhead, 6f, 45f * Deg);
        var overAway = over.Out.BaseEye - over.Out.BaseLookAt;
        var overElevation = MathF.Asin(overAway.Y / overAway.Length()) / Deg;
        var across = MathF.Abs(Vector3.Dot(Vector3.Normalize(new Vector3(overAway.X, 0f, overAway.Z)), sf.LongAxis));
        Check("Overhead: whole body in frame from 75-80 deg, across the long axis", Reach(over.Out, points) <= 1f && overElevation is >= 75f and <= 80f && across < 0.1f, $"{overElevation:F1} deg, along-axis part {across:F2}");
        var low = RunStatic(world, supine, DeathShotProfile.GroundProfile, 6f, 45f * Deg);
        var lowDir = Vector3.Normalize(low.Out.BaseLookAt - low.Out.BaseEye);
        var lookingUp = MathF.Asin(lowDir.Y) / Deg;
        Check("Ground profile: whole body in frame, eye 0.10-0.15 m up, looking up 12-16 deg, across the long axis",
            Reach(low.Out, points) <= 1f && low.Out.BaseEye.Y is >= 0.0999f and <= 0.15f && lookingUp is >= 12f and <= 16f && MathF.Abs(Vector3.Dot(lowDir, sf.LongAxis)) < 0.1f,
            $"eye height {low.Out.BaseEye.Y:F2}, looking up {lookingUp:F1} deg");

        // Auto.
        Check("Auto: standing and on a side -> Low three-quarter; on the back -> Face or Low three-quarter; on the belly -> Along the body or Ground profile, never Face",
            RunStatic(world, standing, DeathShotProfile.Auto, 3f, 0f).Out.Shot == DeathShotProfile.LowThreeQuarter &&
            RunStatic(world, Lying(90f * Deg), DeathShotProfile.Auto, 3f, 0f).Out.Shot == DeathShotProfile.LowThreeQuarter &&
            RunStatic(world, supine, DeathShotProfile.Auto, 3f, 0f).Out.Shot is DeathShotProfile.Face or DeathShotProfile.LowThreeQuarter &&
            Enumerable.Range(0, 8).All(k => RunStatic(world, pronePoints, DeathShotProfile.Auto, 3f, k * 45f * Deg).Out.Shot is DeathShotProfile.AlongBody or DeathShotProfile.GroundProfile));
        var sideBody = Lying(90f * Deg);
        var sb = BodyFrame.From(sideBody, 0f);
        var onSide = RunStatic(world, sideBody, DeathShotProfile.LowThreeQuarter, 6f, 0f);
        var toEye = Vector3.Normalize(onSide.Out.BaseEye - onSide.Out.BaseLookAt);
        Check("Low three-quarter, on a side: on the body's front side, turned toward the head, at ground level",
            Vector3.Dot(toEye, sb.FrontHorizontal) > 0.5f && Vector3.Dot(toEye, sb.LongAxis) > 0.3f && onSide.Out.BaseEye.Y < 0.4f && onSide.MinClearance >= 0.0999f,
            $"front part {Vector3.Dot(toEye, sb.FrontHorizontal):F2}, head part {Vector3.Dot(toEye, sb.LongAxis):F2}, eye height {onSide.Out.BaseEye.Y:F2}");

        // Constraints.
        var wet = new FlatWorld { Water = 0.4f };
        Check("water: the eye stays 10 cm above the surface", RunStatic(wet, supine, DeathShotProfile.GroundProfile, 6f, 0f).Out.Eye.Y >= 0.4999f);
        // A wall 0.7 m from the body on the side Ground profile would pick first (start azimuth +90 deg = +X).
        var walled = new FlatWorld { WallX = 0.7f };
        var w = RunStatic(walled, supine, DeathShotProfile.GroundProfile, 6f, 90f * Deg);
        Check("a wall on the chosen side: the eye ends on the open side", w.Out.Eye.X < 0.7f && Reach(w.Out, points) <= 1f, $"eye x {w.Out.Eye.X:F2}");
        // Walls close on both sides of a body on its back: Ground profile has no clear side left.
        var corridor = new CorridorWorld();
        var cw = RunStatic(corridor, supine, DeathShotProfile.GroundProfile, 6f, 90f * Deg);
        Check("no clear side at all: a fallback is used and counted", cw.Out.Fallbacks > 0 && MathF.Abs(cw.Out.Eye.X) < 0.7f, $"shot {cw.Out.Shot}, eye x {cw.Out.Eye.X:F2}, fallbacks {cw.Out.Fallbacks}");

        // The same scene turned and moved gives the same shot, turned and moved.
        var yaw = 1.1f;
        var move = new Vector3(40f, 0f, -12f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var a = RunStatic(world, supine, DeathShotProfile.Auto, 6f, 45f * Deg);
        var b = RunStatic(world, Map(supine, v => Vector3.Transform(v, q) + move), DeathShotProfile.Auto, 6f, 45f * Deg + yaw);
        var error = Vector3.Distance(Vector3.Transform(a.Out.Eye, q) + move, b.Out.Eye) + Vector3.Distance(Vector3.Transform(a.Out.LookAt, q) + move, b.Out.LookAt);
        Check("turning and moving the scene turns and moves the shot", error < 2e-3f, $"difference {error * 1000f:F2} mm");

        // A bigger body gets a proportionally bigger shot.
        var big = Map(standing, v => v * 2f);
        var bigShot = RunStatic(world, big, DeathShotProfile.LowThreeQuarter, 12f, 0f);
        var bigDistance = Vector3.Distance(bigShot.Out.BaseEye, bigShot.Out.BaseLookAt);
        Check("a body twice the size: the shot is twice as far", bigDistance is > 2.2f and < 2.7f && MathF.Abs(bigShot.BodyScale - 2f) < 0.05f, $"{bigDistance:F2} m, scale {bigShot.BodyScale:F2}");

        // No start camera: the first frame is the shot itself.
        var cold = RunStatic(world, standing, DeathShotProfile.LowThreeQuarter, 1f / 60f, 0f, begin: false);
        Check("without a start camera the first frame is the solved shot", Vector3.Distance(cold.Out.Eye, cold.Out.GoalEye) < 1e-4f);
    }

    /// <summary>Flat ground between two walls at x = ±0.7.</summary>
    private sealed class CorridorWorld : IDeathShotWorld
    {
        public float? GroundBelow(Vector3 from) => 0f;
        public float? WaterBelow(Vector3 from) => null;
        public bool Blocked(Vector3 from, Vector3 to, out float distance)
        {
            distance = 0f;
            foreach (var wall in new[] { 0.7f, -0.7f })
            {
                if (from.X < wall == to.X < wall) continue;
                distance = Vector3.Distance(from, to) * MathF.Abs((wall - from.X) / (to.X - from.X));
                return true;
            }
            return false;
        }
    }
}
