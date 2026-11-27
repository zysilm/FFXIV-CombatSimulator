// Offline unit checks of the death replay's game-free parts (docs/DEATH_REPLAY.md §9).
//   dotnet run -c Release        exit code 0 = all pass
using System.Globalization;
using System.Numerics;
using CombatSimulator.Replay.Model;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var failures = 0;
var checks = 0;
void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
}

static float AngleBetween(Quaternion a, Quaternion b) =>
    2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0f, 1f));

static RootPose Root(Vector3 at) => new()
{
    SkeletonPosition = at, SkeletonRotation = Quaternion.Identity, SkeletonScale = Vector3.One,
    DrawPosition = at, DrawRotation = Quaternion.Identity,
};

// ---- 1. the ring buffer ----
{
    var track = new PoseTrack(capacity: 10, boneCount: 2);
    for (var i = 0; i < 25; i++)
    {
        var slot = track.Append(i * 0.1, Root(new Vector3(i, 0, 0)));
        track.BoneSlot(slot).Clear();
    }
    Check("ring: count capped at capacity", track.Count == 10, $"{track.Count}");
    Check("ring: oldest overwritten", Math.Abs(track.OldestTime - 1.5) < 1e-9 && Math.Abs(track.NewestTime - 2.4) < 1e-9,
        $"{track.OldestTime} .. {track.NewestTime}");
    var ordered = true;
    for (var i = 1; i < track.Count; i++) ordered &= track.TimeAt(i) > track.TimeAt(i - 1);
    Check("ring: samples in time order after wrapping", ordered);
    Check("ring: a time not later than the newest is refused", track.Append(2.4, Root(Vector3.Zero)) == -1 && track.Count == 10);
    Check("ring: a time that is not finite is refused", track.Append(double.NaN, Root(Vector3.Zero)) == -1);
    track.Clear();
    Check("ring: clear", track.Count == 0 && double.IsNaN(track.NewestTime));
    Check("ring: memory of a 20 s human track",
        PoseTrack.BytesFor(616, 120) is > 1_000_000 and < 1_200_000, $"{PoseTrack.BytesFor(616, 120)} B");
}

// ---- 2. extraction: the stretch a replay plays ----
{
    Check("window: the last N seconds up to the end",
        ReplayWindow.For(0, 20, 20, 8, 1, out var s, out var e) && Math.Abs(s - 12) < 1e-9 && Math.Abs(e - 20) < 1e-9, $"{s}..{e}");
    Check("window: clipped at the oldest sample",
        ReplayWindow.For(15, 20, 20, 8, 1, out s, out e) && Math.Abs(s - 15) < 1e-9 && Math.Abs(e - 20) < 1e-9, $"{s}..{e}");
    Check("window: an end past the newest is clamped",
        ReplayWindow.For(0, 20, 30, 8, 1, out s, out e) && Math.Abs(e - 20) < 1e-9 && Math.Abs(s - 12) < 1e-9);
    Check("window: too little recorded", !ReplayWindow.For(19.5, 20, 20, 8, 1, out _, out _));
    Check("window: nothing recorded", !ReplayWindow.For(double.NaN, double.NaN, 20, 8, 1, out _, out _));
}

// ---- 3. interpolation and the compact bone form ----
{
    var track = new PoseTrack(capacity: 8, boneCount: 2);
    var qa = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0f);
    var qb = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
    var s0 = track.Append(1.0, Root(new Vector3(0, 0, 0)));
    PoseQuant.Pack(track.BoneSlot(s0).Slice(0, 7), new Vector3(0, 1, 0), qa);
    PoseQuant.Pack(track.BoneSlot(s0).Slice(7, 7), new Vector3(0.5f, 1.5f, -0.25f), qa);
    var s1 = track.Append(2.0, Root(new Vector3(2, 0, 0)));
    PoseQuant.Pack(track.BoneSlot(s1).Slice(0, 7), new Vector3(1, 1, 0), qb);
    // stored as -q (the same rotation on the other hemisphere): interpolation must take the shorter arc
    PoseQuant.Pack(track.BoneSlot(s1).Slice(7, 7), new Vector3(0.5f, 1.5f, -0.25f), Quaternion.Negate(qb));
    var pos = new Vector3[2];
    var rot = new Quaternion[2];
    Check("interp: sample", track.Sample(1.25, pos, rot, out var root));
    Check("interp: position a quarter of the way", Vector3.Distance(pos[0], new Vector3(0.25f, 1, 0)) < 2e-3f, $"{pos[0]}");
    Check("interp: root a quarter of the way", Vector3.Distance(root.SkeletonPosition, new Vector3(0.5f, 0, 0)) < 1e-5f);
    var expect = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.15f);
    Check("interp: rotation a quarter of the way", AngleBetween(rot[0], expect) < 0.01f, $"{AngleBetween(rot[0], expect)} rad");
    Check("interp: shorter arc across hemispheres", AngleBetween(rot[1], expect) < 0.01f, $"{AngleBetween(rot[1], expect)} rad");
    track.Sample(0.0, pos, rot, out _);
    Check("interp: before the first sample holds it", Vector3.Distance(pos[0], new Vector3(0, 1, 0)) < 1e-3f);
    track.Sample(9.0, pos, rot, out _);
    Check("interp: after the last sample holds it", Vector3.Distance(pos[0], new Vector3(1, 1, 0)) < 1e-3f);
    Check("interp: exact sample time", track.Sample(2.0, pos, rot, out _) && Vector3.Distance(pos[0], new Vector3(1, 1, 0)) < 1e-3f);

    // quantisation error over a body's range of positions and random rotations
    var rng = new Random(7);
    float worstP = 0f, worstR = 0f;
    Span<short> buf = stackalloc short[7];
    for (var i = 0; i < 20000; i++)
    {
        var p = new Vector3((float)(rng.NextDouble() * 4 - 2), (float)(rng.NextDouble() * 4 - 2), (float)(rng.NextDouble() * 4 - 2));
        var q = Quaternion.Normalize(new Quaternion((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f,
            (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
        PoseQuant.Pack(buf, p, q);
        PoseQuant.Unpack(buf, out var p2, out var q2);
        worstP = MathF.Max(worstP, Vector3.Distance(p, p2));
        worstR = MathF.Max(worstR, AngleBetween(q, q2));
    }
    Check("quant: position within 2 mm over ±2 m", worstP < 0.002f, $"{worstP * 1000:0.000} mm");
    Check("quant: rotation within 0.1° (float acos limits the measurement)", worstR < 0.1f * MathF.PI / 180f, $"{worstR * 180 / MathF.PI:0.0000}°");
    Console.WriteLine($"quantisation: worst position {worstP * 1000:0.000} mm, worst rotation {worstR * 180 / MathF.PI:0.0000}°");
    PoseQuant.Pack(buf, new Vector3(float.NaN, 1, 1), new Quaternion(0, 0, 0, 0));
    PoseQuant.Unpack(buf, out var pn, out var qn);
    Check("quant: garbage in, finite out", float.IsFinite(pn.X) && qn == Quaternion.Identity);
}

// ---- 4. shots relative to the body at death ----
{
    var rng = new Random(11);
    float worstEye = 0f, worstDir = 0f;
    for (var i = 0; i < 2000; i++)
    {
        var frame = new ReplayFrame(new Vector3((float)rng.NextDouble() * 200 - 100, (float)rng.NextDouble() * 20, (float)rng.NextDouble() * 200 - 100),
            (float)(rng.NextDouble() * 2 * Math.PI - Math.PI));
        var eye = frame.Origin + new Vector3((float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 4 - 1, (float)rng.NextDouble() * 10 - 5);
        var fwd = ReplayMath.Forward((float)(rng.NextDouble() * 6.2 - 3.1), (float)(rng.NextDouble() * 2.8 - 1.4));
        var shot = ReplayMath.ShotFromView(frame, new ReplayView(eye, fwd, 0.8f));
        var back = ReplayMath.ViewFromShot(frame, shot);
        worstEye = MathF.Max(worstEye, Vector3.Distance(eye, back.Eye));
        worstDir = MathF.Max(worstDir, Vector3.Distance(fwd, back.Forward));
    }
    Check("shot: world → shot → world (eye)", worstEye < 1e-3f, $"{worstEye} m");
    Check("shot: world → shot → world (direction)", worstDir < 1e-4f, $"{worstDir}");

    // The same shot on two deaths: the picture relative to the body is the same.
    var a = new ReplayFrame(new Vector3(10, 0, 5), 0f);
    var b = new ReplayFrame(new Vector3(-40, 3, 70), MathF.PI / 2f);
    var behindRight = new DeathReplayShot { OffsetX = -1f, OffsetY = 1.5f, OffsetZ = -3f, Yaw = 0.2f, Pitch = -0.3f, Fov = 0.7f };
    var va = ReplayMath.ViewFromShot(a, behindRight);
    var vb = ReplayMath.ViewFromShot(b, behindRight);
    Check("shot: facing +Z, behind is -Z", Math.Abs(va.Eye.Z - 2f) < 1e-5f && Math.Abs(va.Eye.X - 9f) < 1e-5f, $"{va.Eye}");
    // facing +X (azimuth 90°): local -Z (behind) is world -X, local -X (to the right) is world +Z
    Check("shot: facing +X, behind is -X", Vector3.Distance(vb.Eye, new Vector3(-43f, 4.5f, 71f)) < 1e-4f, $"{vb.Eye}");
    Check("shot: the view turns with the facing",
        MathF.Abs(ReplayMath.WrapAngle(ReplayMath.Azimuth(vb.Forward) - ReplayMath.Azimuth(va.Forward) - MathF.PI / 2f)) < 1e-4f);
    Check("shot: pitch kept", MathF.Abs(ReplayMath.PitchOf(va.Forward) + 0.3f) < 1e-5f && MathF.Abs(ReplayMath.PitchOf(vb.Forward) + 0.3f) < 1e-5f);
    Check("shot: lens kept", va.Fov == 0.7f);
    var wild = ReplayMath.ViewFromShot(a, new DeathReplayShot { Pitch = 3f, Fov = float.NaN });
    Check("shot: pitch and lens clamped", ReplayMath.PitchOf(wild.Forward) <= ReplayMath.MaxPitch + 1e-4f && wild.Fov == ReplayMath.DefaultFov && wild.IsFinite);
    // the turn convention: +Z turned by the facing points along the facing
    var turned = ReplayMath.RotateY(Vector3.UnitZ, 0.9f);
    Check("frame: +Z turned by the facing is the facing", MathF.Abs(ReplayMath.Azimuth(turned) - 0.9f) < 1e-5f);
}

// ---- 5. shot timing ----
{
    Check("timing: no shots", ShotSchedule.IndexAt(1f, 8f, 0, false) == -1);
    Check("timing: equal split", ShotSchedule.SegmentLength(8f, 4) == 2f);
    int[] expected = { 0, 0, 1, 1, 2, 2, 3, 3 };
    var ok = true;
    for (var i = 0; i < 8; i++) ok &= ShotSchedule.IndexAt(i + 0.5f, 8f, 4, false) == expected[i];
    Check("timing: shots in order over one pass", ok);
    Check("timing: boundaries", ShotSchedule.IndexAt(1.999f, 8f, 4, false) == 0 && ShotSchedule.IndexAt(2f, 8f, 4, false) == 1);
    Check("timing: the end of a pass holds the last shot", ShotSchedule.IndexAt(8f, 8f, 4, false) == 3 && ShotSchedule.IndexAt(50f, 8f, 4, false) == 3);
    Check("timing: before the start, the first shot", ShotSchedule.IndexAt(-1f, 8f, 4, false) == 0);
    // loop: the shots keep cycling in order across the passes
    var cycle = true;
    for (var pass = 0; pass < 5; pass++)
        for (var i = 0; i < 4; i++)
            cycle &= ShotSchedule.IndexAt(pass * 8f + i * 2f + 0.3f, 8f, 4, true) == i;
    Check("timing: loop cycles the shots", cycle);
    Check("timing: loop at the pass boundary starts over", ShotSchedule.IndexAt(8f, 8f, 4, true) == 0 && ShotSchedule.IndexAt(16f, 8f, 4, true) == 0);
    Check("timing: wrap", MathF.Abs(ShotSchedule.Wrap(19f, 8f) - 3f) < 1e-5f && ShotSchedule.Wrap(8f, 8f) == 0f);
    // three shots over 7 s: 2.333 s each
    Check("timing: uneven split", ShotSchedule.IndexAt(2.3f, 7f, 3, false) == 0 && ShotSchedule.IndexAt(2.4f, 7f, 3, false) == 1 &&
                                  ShotSchedule.IndexAt(4.7f, 7f, 3, false) == 2);
    Check("timing: one shot is the whole replay", ShotSchedule.IndexAt(7.9f, 8f, 1, true) == 0);
}

// ---- 6. the free camera's step ----
{
    var fwd = ReplayMath.Forward(0f, 0f); // +Z
    var right = ReplayMath.Right(fwd);
    Check("fly: right is perpendicular and horizontal", MathF.Abs(Vector3.Dot(right, fwd)) < 1e-6f && right.Y == 0f && MathF.Abs(right.Length() - 1f) < 1e-5f);
    Check("fly: right of +Z is -X (right-handed, +Y up)", Vector3.Distance(right, -Vector3.UnitX) < 1e-6f, $"{right}");
    var moved = ReplayMath.FlyStep(Vector3.Zero, fwd, 1f, 0f, 0f, 3f, 0.5f);
    Check("fly: forward", Vector3.Distance(moved, new Vector3(0, 0, 1.5f)) < 1e-5f, $"{moved}");
    var diagonal = ReplayMath.FlyStep(Vector3.Zero, fwd, 1f, 1f, 1f, 3f, 1f);
    Check("fly: a diagonal is not faster", MathF.Abs(diagonal.Length() - 3f) < 1e-4f, $"{diagonal.Length()}");
    var up = ReplayMath.FlyStep(Vector3.Zero, ReplayMath.Forward(1f, 0.5f), 0f, 0f, 1f, 2f, 1f);
    Check("fly: up is straight up", Vector3.Distance(up, new Vector3(0, 2, 0)) < 1e-5f);
    Check("fly: no input, no move", ReplayMath.FlyStep(Vector3.One, fwd, 0, 0, 0, 3f, 1f) == Vector3.One);
    Check("fly: bad dt ignored", ReplayMath.FlyStep(Vector3.One, fwd, 1, 0, 0, 3f, float.NaN) == Vector3.One);
}

Console.WriteLine(failures == 0 ? $"all {checks} checks passed" : $"{failures} of {checks} checks FAILED");
return failures == 0 ? 0 : 1;
