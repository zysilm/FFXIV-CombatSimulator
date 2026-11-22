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
        SidePreferences();
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
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.LowFullBody, Aspect = 16f / 9f, UserYaw = yaw, UserSize = 1f });
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
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = DeathShotProfile.LowFullBody, Aspect = 16f / 9f, UserYaw = yaw, UserSize = 1f });
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

        // Each shot of the family on a body lying on its back.
        var supine = Lying(0f);
        var sf = BodyFrame.From(supine, 0f);
        var points = All(supine);
        var headCentre = supine.Head + Vector3.Normalize(supine.Head - supine.Chest) * 0.09f;
        var pronePoints = Lying(MathF.PI);
        var pf = BodyFrame.From(pronePoints, 0f);
        static float OffAxis(Vector3 viewDirection, Vector3 axis) =>
            MathF.Acos(Math.Clamp(MathF.Abs(Vector3.Dot(Vector3.Normalize(new Vector3(viewDirection.X, 0f, viewDirection.Z)), axis)), 0f, 1f)) / Deg;

        var face = RunStatic(world, supine, DeathShotProfile.LowFace, 14f, 45f * Deg);
        var faceAway = Vector3.Normalize(face.Out.BaseEye - headCentre);
        var faceUp = MathF.Asin(faceAway.Y) / Deg;
        Check("Low on the face, supine: head in frame, the eye beside the head on the face's side of it, no more than 35 deg above it, above the ground",
            Reach(face.Out, headCentre) <= 1f && Vector3.Dot(sf.Front, faceAway) > 0.3f && faceUp <= 35.5f && MathF.Abs(Vector3.Dot(sf.Lateral, faceAway)) > 0.5f && face.MinClearance >= 0.0999f,
            $"{faceUp:F1} deg above the head, front . to-eye {Vector3.Dot(sf.Front, faceAway):F2}, {Vector3.Distance(face.Out.BaseEye, headCentre):F2} m");
        var faceStanding = RunStatic(world, standing, DeathShotProfile.LowFace, 12f, 0f);
        var standingHead = standing.Head + Vector3.UnitY * 0.09f;
        var fsAway = Vector3.Normalize(faceStanding.Out.BaseEye - standingHead);
        var fsOff = MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frame.FrontHorizontal), DeathShotMath.Azimuth(fsAway))) / Deg;
        Check("Low on the face, standing: 35 deg off the face, from 15 deg below it, 1.0 m",
            MathF.Abs(fsOff - 35f) < 2f && MathF.Abs(MathF.Asin(fsAway.Y) / Deg + 15f) < 1.5f && MathF.Abs(Vector3.Distance(faceStanding.Out.BaseEye, standingHead) - 1.0f) < 0.05f,
            $"off {fsOff:F1} deg, {MathF.Asin(fsAway.Y) / Deg:F1} deg, {Vector3.Distance(faceStanding.Out.BaseEye, standingHead):F2} m");
        var faceDown = RunStatic(world, pronePoints, DeathShotProfile.LowFace, 14f, 45f * Deg);
        var proneHead = pronePoints.Head + Vector3.Normalize(pronePoints.Head - pronePoints.Chest) * 0.09f;
        var sideways = MathF.Abs(Vector3.Dot(pf.Lateral, Vector3.Normalize(faceDown.Out.Eye - proneHead)));
        Check("Low on the face, face down: the eye goes beside the head (the profile), above the ground", sideways > 0.8f && faceDown.MinClearance >= 0.0999f && Reach(faceDown.Out, proneHead) <= 1f, $"|lateral . to-eye| {sideways:F2}");

        var hips = RunStatic(world, supine, DeathShotProfile.HipsToFace, 14f, 45f * Deg);
        var hipsView = Vector3.Normalize(hips.Out.BaseLookAt - hips.Out.BaseEye);
        var pastChest = Vector3.Dot(hips.Out.BaseEye - supine.Chest, -sf.LongAxis);
        Check("Hips up to the face, supine: chest and head in frame, the eye by the hips (toward the feet from the chest), looking up the torso toward the head",
            Reach(hips.Out, supine.Chest, headCentre) <= 1f && pastChest > 0.5f && Vector3.Dot(hipsView, sf.LongAxis) > 0.75f && hips.MinClearance >= 0.0999f,
            $"{pastChest:F2} m toward the feet from the chest, {Vector3.Dot(hips.Out.BaseEye - supine.Hips, -sf.LongAxis):F2} m past the hips, eye height {hips.Out.BaseEye.Y:F2}, view {OffAxis(hipsView, sf.LongAxis):F0} deg off the long axis");
        var hipsStanding = RunStatic(world, standing, DeathShotProfile.HipsToFace, 12f, 0f);
        var hsView = Vector3.Normalize(hipsStanding.Out.BaseLookAt - hipsStanding.Out.BaseEye);
        Check("Hips up to the face, standing: from in front of the hips or lower, looking up at the chest and head",
            Reach(hipsStanding.Out, standing.Chest, standingHead) <= 1f && hipsStanding.Out.BaseEye.Y < standing.Hips.Y + 0.1f && hsView.Y > 0.45f &&
            Vector3.Dot(hipsStanding.Out.BaseEye - standing.Hips, frame.FrontHorizontal) > 0.1f,
            $"eye height {hipsStanding.Out.BaseEye.Y:F2}, {Vector3.Dot(hipsStanding.Out.BaseEye - standing.Hips, frame.FrontHorizontal):F2} m in front, looking up {MathF.Asin(hsView.Y) / Deg:F0} deg");

        var feet = RunStatic(world, supine, DeathShotProfile.LowFeetSide, 14f, 45f * Deg);
        var feetAway = Vector3.Normalize(feet.Out.BaseEye - feet.Out.BaseLookAt);
        var feetView = -feetAway;
        Check("Low from the feet, supine: hips and chest in frame, the eye toward the feet end and to a side (42.5 deg off the axis), low",
            Reach(feet.Out, supine.Hips, supine.Chest) <= 1f && Vector3.Dot(feetAway, sf.LongAxis) < -0.5f && MathF.Abs(OffAxis(feetView, sf.LongAxis) - 42.5f) < 3f && feet.Out.BaseEye.Y < 0.4f && feet.MinClearance >= 0.0999f,
            $"{OffAxis(feetView, sf.LongAxis):F1} deg off the axis, eye height {feet.Out.BaseEye.Y:F2}");

        var behind = RunStatic(world, standing, DeathShotProfile.LowBehind, 12f, 180f * Deg);
        var behindAway = behind.Out.BaseEye - behind.Out.BaseLookAt;
        var behindOff = MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frame.FrontHorizontal), DeathShotMath.Azimuth(behindAway))) / Deg;
        var behindUp = -MathF.Asin(behindAway.Y / behindAway.Length()) / Deg;
        Check("Low from behind, standing: the old 'anchor A' shot (129 deg off the facing, looking up 25 deg, 1.35 m)",
            MathF.Abs(behindOff - 129f) < 2f && MathF.Abs(behindUp - 25f) < 1f && MathF.Abs(behindAway.Length() - 1.35f) < 0.04f && Reach(behind.Out, standing.Hips, standing.Chest) <= 1f,
            $"off {behindOff:F1} deg, up {behindUp:F1} deg, {behindAway.Length():F2} m");

        var full = RunStatic(world, supine, DeathShotProfile.LowFullBody, 14f, 45f * Deg, extra: points);
        var fullView = Vector3.Normalize(full.Out.BaseLookAt - full.Out.BaseEye);
        Check("Low full body, supine: the whole body in frame, from the head end 50 deg off the long axis (the body lies diagonally), low",
            Reach(full.Out, points) <= 1f && MathF.Abs(OffAxis(fullView, sf.LongAxis) - 50f) < 4f && Vector3.Dot(full.Out.BaseEye - full.Out.BaseLookAt, sf.LongAxis) > 0f &&
            full.Out.BaseEye.Y < 0.5f && full.MinClearance >= 0.0999f,
            $"{OffAxis(fullView, sf.LongAxis):F1} deg off the axis, eye height {full.Out.BaseEye.Y:F2}, {Vector3.Distance(full.Out.BaseEye, full.Out.BaseLookAt):F2} m, reach {Reach(full.Out, points):F2}");

        // Auto.
        var autoSide = Enumerable.Range(0, 8).Select(k => RunStatic(world, Lying(90f * Deg), DeathShotProfile.Auto, 3f, k * 45f * Deg).Out.Shot).ToList();
        var autoSupine = Enumerable.Range(0, 8).Select(k => RunStatic(world, supine, DeathShotProfile.Auto, 3f, k * 45f * Deg).Out.Shot).ToList();
        var autoProne = Enumerable.Range(0, 8).Select(k => RunStatic(world, pronePoints, DeathShotProfile.Auto, 3f, k * 45f * Deg).Out.Shot).ToList();
        Check("Auto, from 8 start azimuths: standing -> Low three-quarter; lying -> the shot of the pose whose side is nearest (several are used); on the belly never a shot of the face",
            Enumerable.Range(0, 8).All(k => RunStatic(world, standing, DeathShotProfile.Auto, 3f, k * 45f * Deg).Out.Shot == DeathShotProfile.LowThreeQuarter) &&
            autoSide.All(s => ShotRecipe.ForAuto(PoseClass.Side).Contains(ShotRecipe.Of(s))) && autoSide.Distinct().Count() >= 2 &&
            autoSupine.All(s => ShotRecipe.ForAuto(PoseClass.Supine).Contains(ShotRecipe.Of(s))) && autoSupine.Distinct().Count() >= 2 &&
            autoProne.All(s => s is not (DeathShotProfile.LowFace or DeathShotProfile.HipsToFace) && ShotRecipe.ForAuto(PoseClass.Prone).Contains(ShotRecipe.Of(s))),
            $"on a side: {string.Join(", ", autoSide.Distinct())}; on the back: {string.Join(", ", autoSupine.Distinct())}; on the belly: {string.Join(", ", autoProne.Distinct())}");
        var sideBody = Lying(90f * Deg);
        var sb = BodyFrame.From(sideBody, 0f);
        var onSide = RunStatic(world, sideBody, DeathShotProfile.LowThreeQuarter, 6f, 0f);
        var toEye = Vector3.Normalize(onSide.Out.BaseEye - onSide.Out.BaseLookAt);
        Check("Low three-quarter, on a side: on the body's front side, turned toward the head, at ground level",
            Vector3.Dot(toEye, sb.FrontHorizontal) > 0.5f && Vector3.Dot(toEye, sb.LongAxis) > 0.3f && onSide.Out.BaseEye.Y < 0.4f && onSide.MinClearance >= 0.0999f,
            $"front part {Vector3.Dot(toEye, sb.FrontHorizontal):F2}, head part {Vector3.Dot(toEye, sb.LongAxis):F2}, eye height {onSide.Out.BaseEye.Y:F2}");

        // Constraints.
        var wet = new FlatWorld { Water = 0.4f };
        Check("water: the eye stays 10 cm above the surface", RunStatic(wet, supine, DeathShotProfile.LowFullBody, 6f, 0f, extra: points).Out.Eye.Y >= 0.4999f);
        // A wall 0.7 m from the body on the side Low full body would pick first (start azimuth +90 deg = +X).
        var walled = new FlatWorld { WallX = 0.7f };
        var w = RunStatic(walled, supine, DeathShotProfile.LowFullBody, 8f, 90f * Deg, extra: points);
        Check("a wall on the chosen side: the eye ends on the open side", w.Out.Eye.X < 0.7f && Reach(w.Out, points) <= 1f, $"eye x {w.Out.Eye.X:F2}");
        // Walls close on both sides of a body on its back: Low full body has no clear side left.
        var corridor = new CorridorWorld();
        var cw = RunStatic(corridor, supine, DeathShotProfile.LowFullBody, 8f, 90f * Deg, extra: points);
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

    /// <summary>What a run of the solver over a moving body did: for the checks that a change is eased, not cut.</summary>
    private sealed class Trace
    {
        public DeathShotOutput Last;
        public int CutFrames;
        public float MaxViewRate, MaxEyeSpeed, MaxTurn;
        public readonly List<string> Events = new();
    }

    private static Trace Follow(IDeathShotWorld world, Func<float, BodyPoints> body, Func<float, DeathShotInput, DeathShotInput> configure, float seconds, Action<float, DeathShotOutput, BodyPoints>? each = null)
    {
        var solver = new DeathShotSolver(world);
        var first = body(0f);
        solver.Begin(first.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, first.Chest, 0.78f);
        var trace = new Trace();
        Vector3 lastDir = default, lastEye = default;
        for (var i = 0; i < (int)(seconds * 60f); i++)
        {
            var time = i / 60f;
            var p = body(time);
            var input = configure(time, new DeathShotInput { Dt = 1f / 60f, Points = p, Extra = All(p), Profile = DeathShotProfile.LowThreeQuarter, Aspect = 16f / 9f, UserSize = 1f });
            var o = solver.Step(input);
            var dir = Vector3.Normalize(o.LookAt - o.Eye);
            if (i > 0)
            {
                if (o.Cut) trace.CutFrames++;
                trace.MaxViewRate = MathF.Max(trace.MaxViewRate, MathF.Acos(Math.Clamp(Vector3.Dot(dir, lastDir), -1f, 1f)) * 60f / Deg);
                trace.MaxEyeSpeed = MathF.Max(trace.MaxEyeSpeed, Vector3.Distance(o.Eye, lastEye) * 60f);
            }
            if (o.SideChanged)
            {
                trace.MaxTurn = MathF.Max(trace.MaxTurn, o.SideTurn / Deg);
                trace.Events.Add($"{time:F1}s {o.Reason} -> {o.Shot} ({o.SideTurn / Deg:F0} deg)");
            }
            lastDir = dir;
            lastEye = o.Eye;
            trace.Last = o;
            each?.Invoke(time, o, p);
        }
        return trace;
    }

    /// <summary>A change of side or of shot is eased, never cut; the side in use is kept unless it is bad.</summary>
    private static void SoftChanges()
    {
        var world = new FlatWorld();
        // Standing, then falling face down over 1.5 s from t = 2 s, then lying.
        BodyPoints Falling(float t)
        {
            var k = DeathShotMath.SmoothStep((t - 2f) / 1.5f);
            return Map(Turn(Standing, Vector3.UnitX, MathF.PI / 2f * k), v => v + new Vector3(0f, 0.12f * k, 0f));
        }
        var fall = Follow(world, Falling, (_, input) => input with { Profile = DeathShotProfile.Auto }, 16f);
        Check("Auto over a fall from standing to face down: no cut frame, view <= 60 deg/s, eye <= 3 m/s, and it ends on a shot for a body on its belly (the side it had is kept if it still serves)",
            fall.CutFrames == 0 && fall.MaxViewRate <= 60.5f && fall.MaxEyeSpeed <= 3.01f && ShotRecipe.ForAuto(PoseClass.Prone).Contains(ShotRecipe.Of(fall.Last.Shot)),
            $"ends on {fall.Last.Shot}; changes: {(fall.Events.Count == 0 ? "none" : string.Join("; ", fall.Events))}; max view rate {fall.MaxViewRate:F1} deg/s, max eye speed {fall.MaxEyeSpeed:F2} m/s");

        // The dropdown changed while dead, through every shot of the family.
        var lying = Lying(90f * Deg);
        var order = DeathShotProfiles.Choices.Skip(1).ToArray();
        var tour = Follow(world, _ => lying, (t, input) => input with { Profile = order[Math.Min(order.Length - 1, (int)(t / 6f))] }, order.Length * 6f);
        Check("the dropdown changed while dead, through all six shots: every change eased (no cut frame), view <= 60 deg/s, eye <= 3 m/s",
            tour.Events.Count == order.Length - 1 && tour.CutFrames == 0 && tour.MaxViewRate <= 60.5f && tour.MaxEyeSpeed <= 3.01f && tour.Last.Shot == order[^1],
            $"{string.Join("; ", tour.Events)}; max view rate {tour.MaxViewRate:F1} deg/s, max eye speed {tour.MaxEyeSpeed:F2} m/s");

        // A body at rest that is moved and comes to rest turned: by 40 deg the side stays, by 150 deg it is changed (eased).
        BodyPoints Moved(float t, float yaw)
        {
            var k = DeathShotMath.SmoothStep((t - 6f) / 1f);
            return Map(Turn(lying, Vector3.UnitY, yaw * k), v => v + new Vector3(0.6f * k, 0f, 0f));
        }
        var little = Follow(world, t => Moved(t, 40f * Deg), (_, input) => input, 14f);
        var much = Follow(world, t => Moved(t, 150f * Deg), (_, input) => input, 16f);
        Check("a resting body moved and turned 40 deg: the side is kept; turned 150 deg: another side, eased (no cut frame)",
            little.Events.Count == 0 && much.Events.Count == 1 && much.CutFrames == 0 && little.CutFrames == 0 && much.MaxViewRate <= 60.5f,
            $"40 deg: {little.Events.Count} changes; 150 deg: {string.Join("; ", much.Events)}, max view rate {much.MaxViewRate:F1} deg/s");

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
            long held = 0, framed = 0;
            float minDown = float.MaxValue, maxDown = float.MinValue, maxLevel = 0f, eyeTravel = 0f, bodyTravel = 0f, offFront = 0f;
            Vector3 lastEye = default, lastChest = default;
            var sidesAtHold = -1;
            var sidesWhileHeld = 0;
            var trace = Follow(world, Scene, (t, input) => input with { Grabbed = t is >= 4f and < 16f, GrabbedFraming = framing }, 26f, (t, o, p) =>
            {
                if (t is >= 9f and < 16f)
                {
                    if (sidesAtHold < 0) sidesAtHold = o.SideChanges;
                    sidesWhileHeld = o.SideChanges - sidesAtHold;
                    held++;
                    var head = p.Head + Vector3.Normalize(p.Head - p.Chest) * 0.09f;
                    if (Reach(o, p.Chest, head) <= 1f) framed++;
                    var view = Vector3.Normalize(o.LookAt - o.Eye);
                    var down = -MathF.Asin(view.Y) / Deg;
                    minDown = MathF.Min(minDown, down);
                    maxDown = MathF.Max(maxDown, down);
                    maxLevel = MathF.Max(maxLevel, MathF.Abs(o.Eye.Y - (p.Chest.Y + head.Y) * 0.5f));
                    eyeTravel += Vector3.Distance(o.Eye, lastEye);
                    bodyTravel += Vector3.Distance(p.Chest, lastChest);
                    var front = BodyFrame.From(p, 0f).FrontHorizontal;
                    var away = o.Eye - o.LookAt;
                    offFront = MathF.Max(offFront, MathF.Abs(DeathShotMath.AngleDelta(DeathShotMath.Azimuth(front), DeathShotMath.Azimuth(away))) / Deg);
                }
                lastEye = o.Eye;
                lastChest = p.Chest;
            });
            var level = framing == DeathShotGrabbedFraming.Level;
            var shot = level ? DeathShotProfile.GrabbedLevel : DeathShotProfile.GrabbedAbove;
            var grabbedEvent = trace.Events.FirstOrDefault(e => e.Contains("grabbed")) ?? "";
            var releasedEvent = trace.Events.FirstOrDefault(e => e.Contains("released")) ?? "";
            Check($"grabbed, {(level ? "Level" : "Slightly above")}: eased into the held shot, upper body in frame while it swings, on the body's front, " +
                  (level ? "the view level and the eye at the upper body's height" : "looking down 15-20 deg"),
                grabbedEvent.Contains(shot.ToString()) && framed == held && offFront < 95f &&
                (level ? minDown > -6f && maxDown < 6f && maxLevel < 0.2f : minDown > 12f && maxDown < 22f),
                $"{grabbedEvent}; upper body in frame {100.0 * framed / held:F1}% of the held frames; view {minDown:F1}..{maxDown:F1} deg down; eye within {maxLevel:F2} m of the upper body's height; up to {offFront:F0} deg off the front");
            Check("  the side is not re-picked on the swings, the camera moves less than the body, no cut frame, view <= 60 deg/s",
                sidesWhileHeld == 0 && eyeTravel < bodyTravel * 0.7f && trace.CutFrames == 0 && trace.MaxViewRate <= 60.5f,
                $"{sidesWhileHeld} changes of side while held; the eye travelled {eyeTravel:F2} m, the chest {bodyTravel:F2} m; max view rate {trace.MaxViewRate:F1} deg/s");
            Check("  let go: back to the shot of the lying body once it lies still, eased",
                releasedEvent.Contains("LowThreeQuarter") && trace.Last.Shot == DeathShotProfile.LowThreeQuarter && !trace.Last.Grabbed && trace.Last.Holding,
                $"{releasedEvent}; all changes: {string.Join("; ", trace.Events)}");
        }
    }

    /// <summary>The option "Side preference", on bodies whose face is given (the recordings supply the real one).</summary>
    private static void SidePreferences()
    {
        var world = new FlatWorld();
        var supine = Lying(0f);
        var frame = BodyFrame.From(supine, 0f);
        // Left shoulder -> right shoulder, horizontal on a body on its back: the body's right.
        var right = Vector3.Normalize(new Vector3(frame.Lateral.X, 0f, frame.Lateral.Z));
        var up = Vector3.UnitY;

        // Which side of the body the eye ends on (+1 = its right), the note of the last choice, the eye.
        (int Side, string Note, Vector3 Eye) Ends(IDeathShotWorld w, BodyPoints body, DeathShotSide side, Vector3 face, float startAzimuth, DeathShotProfile profile = DeathShotProfile.LowThreeQuarter)
        {
            var solver = new DeathShotSolver(w);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(startAzimuth, 15f * Deg) * 5f, body.Chest, 0.78f);
            DeathShotOutput o = default;
            for (var i = 0; i < 60 * 8; i++)
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = All(body), Profile = profile, Aspect = 16f / 9f, UserSize = 1f, SidePreference = side, Face = face });
            var away = o.BaseEye - o.BaseLookAt;
            return (Vector3.Dot(away, right) > 0f ? 1 : -1, o.SideNote, o.BaseEye);
        }
        bool All8(DeathShotSide side, Vector3 face, int expected, BodyPoints? body = null) =>
            Enumerable.Range(0, 8).All(k => Ends(world, body ?? supine, side, face, k * 45f * Deg).Side == expected);

        var faceRight = Vector3.Normalize(up * 0.8f + right * 0.6f);
        var faceLeft = Vector3.Normalize(up * 0.8f - right * 0.6f);
        var nearestSides = Enumerable.Range(0, 8).Select(k => Ends(world, supine, DeathShotSide.Nearest, faceRight, k * 45f * Deg).Side).ToList();
        Check("Nearest: the side depends on where the camera starts (both sides are used over 8 start azimuths)", nearestSides.Contains(1) && nearestSides.Contains(-1));
        Check("Face side, on the back with the head turned: the eye is on the side the face points to, from every start azimuth",
            All8(DeathShotSide.FaceSide, faceRight, 1) && All8(DeathShotSide.FaceSide, faceLeft, -1), Ends(world, supine, DeathShotSide.FaceSide, faceRight, 0f).Note);
        Check("Behind the head: the other side", All8(DeathShotSide.BehindHead, faceRight, -1) && All8(DeathShotSide.BehindHead, faceLeft, 1), Ends(world, supine, DeathShotSide.BehindHead, faceRight, 0f).Note);
        Check("Body's left / Body's right: fixed to the body, whatever the face and the start",
            All8(DeathShotSide.BodyRight, faceLeft, 1) && All8(DeathShotSide.BodyLeft, faceRight, -1), Ends(world, supine, DeathShotSide.BodyLeft, faceRight, 0f).Note);

        // The face to the sky: nothing sideways to go by, so the nearest side, exactly as with Nearest.
        var same = true;
        for (var k = 0; k < 8; k++)
            same &= Vector3.Distance(Ends(world, supine, DeathShotSide.FaceSide, up, k * 45f * Deg).Eye, Ends(world, supine, DeathShotSide.Nearest, up, k * 45f * Deg).Eye) < 1e-5f;
        var skyNote = Ends(world, supine, DeathShotSide.FaceSide, up, 0f).Note;
        Check("Face side, the face straight up: the same shot as Nearest", same && skyNote.Contains("points up, nearest"), skyNote);

        // Face down with the head turned a little: the way it is turned names the side.
        var prone = Lying(MathF.PI);
        var proneRight = Vector3.Normalize(new Vector3(BodyFrame.From(prone, 0f).Lateral.X, 0f, BodyFrame.From(prone, 0f).Lateral.Z));
        var turned = Vector3.Normalize(-up + proneRight * 0.25f);
        var proneEnds = Enumerable.Range(0, 8).Select(k => Ends(world, prone, DeathShotSide.FaceSide, turned, k * 45f * Deg)).ToList();
        Check("Face side, face down with the head turned 14 deg: the eye is on the side the head is turned to (the visible profile)",
            proneEnds.All(e => Vector3.Dot(e.Eye - prone.Chest, proneRight) > 0f), proneEnds[0].Note);
        var buried = Ends(world, prone, DeathShotSide.FaceSide, -up, 0f).Note;
        Check("Face side, the face straight into the ground: nearest", buried.Contains("points down, nearest"), buried);

        // The preferred side walled off: the other side, and the note says so.
        var bodyLeftX = -right.X; // the body's left lies toward this x
        var walled = new FlatWorld { WallX = 0.5f * MathF.Sign(bodyLeftX) };
        var blocked = Ends(walled, supine, DeathShotSide.BodyLeft, faceRight, 0f);
        var blockedWhere = Ends(walled, supine, DeathShotSide.Nearest, faceRight, 0f);
        Check("the preferred side is walled off: the other side is taken and the note says the preference was not met",
            MathF.Sign(bodyLeftX) > 0f && blocked.Side == 1 && blocked.Note.Contains("not met"), $"{blocked.Note}; eye x {blocked.Eye.X:F2} (Nearest: {blockedWhere.Eye.X:F2})");

        // Low on the face has its sides from the face already: the preference changes nothing there but ties.
        var faceShot = Ends(world, supine, DeathShotSide.FaceSide, faceRight, 0f, DeathShotProfile.LowFace);
        Check("Low on the face with Face side: still a shot of the face (the preference only orders its sides)", faceShot.Note.StartsWith("face side", StringComparison.Ordinal), faceShot.Note);

        // Hold: a body at rest with the head turned right is moved and comes to rest with the face nearly at the sky,
        // a little the other way: the side stays. With the head clearly turned the other way: it changes, eased.
        Trace Moved(Vector3 after)
        {
            BodyPoints At(float t)
            {
                var k = DeathShotMath.SmoothStep((t - 6f) / 1f);
                return Map(supine, v => v + new Vector3(0f, 0f, 0.6f * k));
            }
            return Follow(world, At, (t, input) => input with { SidePreference = DeathShotSide.FaceSide, Face = t < 6f ? faceRight : after }, 16f);
        }
        var wavering = Moved(Vector3.Normalize(up - right * 0.2f));
        var turnedOver = Moved(faceLeft);
        Check("hold: the face nearly at the sky and 11 deg the other way does not send the camera across; the head clearly turned does (eased, no cut frame)",
            wavering.Events.Count == 0 && turnedOver.Events.Count == 1 && turnedOver.CutFrames == 0 && turnedOver.MaxViewRate <= 60.5f,
            $"wavering: {wavering.Events.Count} changes ({wavering.Last.SideNote}); turned: {string.Join("; ", turnedOver.Events)} ({turnedOver.Last.SideNote})");

        // A held body: the three-quarter on the side the face is turned to.
        var hanging = Map(Standing, v => v + new Vector3(0f, 0.45f, 0f));
        var standingRight = Vector3.Normalize(new Vector3(BodyFrame.From(hanging, 0f).Lateral.X, 0f, 0f));
        var lookingRight = Vector3.Normalize(BodyFrame.From(hanging, 0f).FrontHorizontal + standingRight * 0.7f);
        var heldSides = new List<float>();
        for (var k = 0; k < 8; k++)
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(hanging.Chest + DeathShotMath.OrbitDirection(k * 45f * Deg, 15f * Deg) * 5f, hanging.Chest, 0.78f);
            DeathShotOutput o = default;
            for (var i = 0; i < 60 * 8; i++)
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = hanging, Extra = All(hanging), Profile = DeathShotProfile.LowThreeQuarter, Aspect = 16f / 9f, UserSize = 1f,
                    Grabbed = i > 30, SidePreference = DeathShotSide.FaceSide, Face = lookingRight });
            heldSides.Add(Vector3.Dot(o.BaseEye - o.BaseLookAt, standingRight));
        }
        Check("grabbed with Face side: the three-quarter on the side the head is turned to, from every start azimuth", heldSides.All(s => s > 0.2f), $"{heldSides.Min():F2}..{heldSides.Max():F2} m toward that side");
    }

    /// <summary>Where each shot sits on a body lying on its back with the head to the right of someone looking down
    /// on it from above with the body's left side toward them: for the documentation.</summary>
    public static void Where()
    {
        var world = new FlatWorld();
        Console.WriteLine("body on its back, hips at the origin, head toward +along; 'toward the viewer' = the body's left side; metres, reference body (1.7 m)");
        Console.WriteLine($"{"shot",-22} {"along",7} {"left",7} {"height",7} {"to subject",11} {"elevation",10} {"off axis",9}  subject");
        foreach (var pose in new (string Name, BodyPoints Body)[] { ("on the back", Lying(0f)), ("on a side", Lying(90f * Deg)), ("face down", Lying(MathF.PI)), ("standing", Standing) })
        {
            Console.WriteLine($"-- {pose.Name}");
            var f = BodyFrame.From(pose.Body, 0f);
            // Left of the body as seen along its long axis from above.
            var left = Vector3.Cross(f.LongAxis, Vector3.UnitY);
            foreach (var profile in DeathShotProfiles.Choices.Skip(1))
            foreach (var start in new[] { 100f, -100f })
            {
                var r = RunStatic(world, pose.Body, profile, 8f, DeathShotMath.Azimuth(start > 0 ? left : -left), extra: All(pose.Body));
                var eye = r.Out.BaseEye;
                var away = eye - r.Out.BaseLookAt;
                var view = -Vector3.Normalize(away);
                var offAxis = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(new Vector3(view.X, 0f, view.Z)), f.LongAxis), -1f, 1f)) / Deg;
                Console.WriteLine($"{DeathShotProfiles.Label(profile) + (start > 0 ? "" : " (other side)"),-22} {Vector3.Dot(eye - pose.Body.Hips, f.LongAxis),7:F2} {Vector3.Dot(eye - pose.Body.Hips, left),7:F2} {eye.Y,7:F2} " +
                                  $"{away.Length(),11:F2} {MathF.Asin(away.Y / away.Length()) / Deg,10:F0} {offAxis,9:F0}  {ShotRecipe.Of(r.Out.Shot).Subject}{(r.Out.Shot != profile ? " (as " + r.Out.Shot + ")" : "")}");
            }
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
        DeathShotOutput Run(IDeathShotWorld world, DeathShotProfile profile, bool below, out float minEye)
        {
            var solver = new DeathShotSolver(world);
            solver.Begin(body.Chest + DeathShotMath.OrbitDirection(0f, 15f * Deg) * 5f, body.Chest, 0.78f);
            DeathShotOutput o = default;
            minEye = float.MaxValue;
            for (var i = 0; i < 60 * 12; i++)
            {
                o = solver.Step(new DeathShotInput { Dt = 1f / 60f, Points = body, Extra = extra, Profile = profile, Aspect = 16f / 9f, UserSize = 1f, AllowBelowGround = below });
                minEye = MathF.Min(minEye, o.Eye.Y);
            }
            return o;
        }

        // Low three-quarter on a body on the ground: the old shots' placement, under the ground, and the ground it
        // goes under is not "in the way" (no fallback, nothing pulled in).
        var open = new SolidGroundWorld();
        var low = Run(open, DeathShotProfile.LowThreeQuarter, true, out _);
        var away = low.BaseEye - low.BaseLookAt;
        var up = -MathF.Asin(away.Y / away.Length()) / Deg;
        Check("below ground allowed: Low three-quarter on a lying body looks up 22 deg from 1.25 m, eye under the ground, no fallback",
            MathF.Abs(up - 22f) < 0.5f && MathF.Abs(away.Length() - 1.25f) < 0.03f && low.BaseEye.Y < -0.1f && low.Fallbacks == 0 && !low.PulledIn && low.Shot == DeathShotProfile.LowThreeQuarter,
            $"up {up:F1} deg, {away.Length():F2} m, eye y {low.BaseEye.Y:F2}, fallbacks {low.Fallbacks}");

        // The same body with the option off, every shot of the family: above the ground.
        var offMin = float.MaxValue;
        foreach (var profile in DeathShotProfiles.Choices.Skip(1))
        {
            Run(open, profile, false, out var min);
            offMin = MathF.Min(offMin, min);
        }
        Check("option off, all six shots: the eye never under 10 cm", offMin >= 0.0999f, $"min eye y {offMin:F3}");

        // A wall still blocks a below-ground shot: put where the view of the open shot crosses it above the ground
        // (close to the body: further out the view is already under the ground, and under the wall), that side is
        // given up and the view from where the eye ends does not cross the wall above the ground.
        var wallX = low.Eye.X * 0.2f;
        var beside = Run(new SolidGroundWorld { WallX = wallX }, DeathShotProfile.LowThreeQuarter, true, out _);
        var walled = new SolidGroundWorld { WallX = wallX };
        var crosses = walled.Blocked(beside.GoalLookAt, beside.GoalEye, out _, out var stoppedBy) && MathF.Abs(stoppedBy.X) > 0.5f;
        Check("below ground allowed: a wall between still blocks (another side is taken, counted as a fallback)",
            MathF.Abs(low.Eye.X) > 0.3f && beside.Fallbacks >= 1 && !crosses && Vector3.Distance(beside.Eye, low.Eye) > 0.2f,
            $"eye {beside.Eye.X:F2}, {beside.Eye.Y:F2}, {beside.Eye.Z:F2} with the wall at x {wallX:F2} (without it {low.Eye.X:F2}, {low.Eye.Y:F2}, {low.Eye.Z:F2}), fallbacks {beside.Fallbacks}");
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
