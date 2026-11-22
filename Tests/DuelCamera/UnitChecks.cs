using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;

namespace DuelCamera;

/// <summary>Unit checks of the duel camera's solver and of the opponent picker on small hand-made cases.</summary>
public static class UnitChecks
{
    private const float Dt = 1f / 60f;
    private const float Deg = DeathShotMath.Deg;
    private static int failures, checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        Console.WriteLine($"  FAIL  {what}");
    }

    private static DuelSubject Body(float x, float z, float height = 1.5f, float radius = 0.4f, float y = 0f) =>
        new(new Vector3(x, y, z), new Vector3(x, y + height, z), radius);

    private sealed class Rig
    {
        public readonly TestWorld World = new();
        public readonly DuelCameraSolver Solver;
        public DuelSubject Player = Body(0f, -3f);
        public DuelSubject? Opponent = Body(0f, 0f);
        public uint Id = 1;
        public DuelFraming Framing = DuelFraming.Standard;
        public DuelOutput Out;
        public float Time;

        public Rig(float startAzimuth = MathF.PI)
        {
            Solver = new DuelCameraSolver(World);
            var chest = Player.At(0.74f);
            Solver.Begin(chest + DeathShotMath.OrbitDirection(startAzimuth, 15f * Deg) * 5f, chest, 0.78f);
        }

        public DuelOutput Step(float yaw = 0f, float pitch = 0f, float wheel = 0f)
        {
            Out = Solver.Step(new DuelInput
            {
                Dt = Dt, Player = Player, Opponent = Opponent, OpponentId = Id, Aspect = Runner.Aspect, Framing = Framing,
                YawInput = yaw, PitchInput = pitch, WheelInput = wheel,
            });
            Time += Dt;
            return Out;
        }

        public DuelOutput Run(float seconds, Action<Rig>? each = null)
        {
            for (var i = 0; i < (int)MathF.Round(seconds / Dt); i++)
            {
                Step();
                each?.Invoke(this);
            }
            return Out;
        }

        public Vector3 Ndc(Vector3 point) => DuelView.Project(Out.Eye, Vector3.Normalize(Out.LookAt - Out.Eye), Out.Fov, Runner.Aspect, point);
        public bool HeadsIn => Metrics.Inside(Ndc(Player.Head)) && (Opponent is not { } o || Metrics.Inside(Ndc(o.Head)));
        public float EyeDistance => Vector3.Distance(Out.Eye, Player.At(0.74f));
        public float EyeAzimuth => DeathShotMath.Azimuth(Out.Eye - Player.At(0.74f));
        public float EyeElevation => MathF.Asin(Math.Clamp((Out.Eye.Y - Player.At(0.74f).Y) / MathF.Max(EyeDistance, 1e-3f), -1f, 1f));
    }

    public static int Run()
    {
        failures = checks = 0;
        Console.WriteLine("== unit checks");
        Picker();
        Placement();
        Hand();
        Beats();
        Worlds();
        Edges();
        Console.WriteLine(failures == 0 ? $"  {checks} checks pass" : $"  {failures} of {checks} checks FAIL");
        return failures;
    }

    private static void Picker()
    {
        var picker = new DuelOpponentPicker();
        var player = Vector3.Zero;
        var list = new List<DuelCandidate>
        {
            new(1, new Vector3(0f, 0f, 6f), true, false, false, false),
            new(2, new Vector3(0f, 0f, 4f), true, false, false, false),
        };
        Check(picker.Pick(list, player, Dt, out var why) == 2 && why == "first", "picker: the nearest first");
        // A little nearer is not enough.
        list[0] = list[0] with { Position = new Vector3(0f, 0f, 3f) };
        for (var i = 0; i < 300; i++) picker.Pick(list, player, Dt, out _);
        Check(picker.Current == 2, "picker: a slightly nearer enemy does not take over");
        // Attacking the player: clearly better, but only after the hold.
        list[0] = list[0] with { AttackingPlayer = true };
        var switchedAt = -1f;
        for (var i = 0; i < 120 && switchedAt < 0f; i++)
            if (picker.Pick(list, player, Dt, out _) == 1) switchedAt = (i + 1) * Dt;
        Check(switchedAt >= DuelOpponentPicker.SwitchHold - 0.02f && switchedAt <= DuelOpponentPicker.SwitchHold + 0.05f,
            $"picker: an attacker takes over after the hold ({switchedAt:0.00} s)");
        // A better one that does not stay better does not take over.
        list[1] = list[1] with { Striking = true, Position = new Vector3(0f, 0f, 1f) };
        for (var i = 0; i < 30; i++) picker.Pick(list, player, Dt, out _);
        list[1] = list[1] with { Striking = false, Position = new Vector3(0f, 0f, 4f) };
        for (var i = 0; i < 120; i++) picker.Pick(list, player, Dt, out _);
        Check(picker.Current == 1, "picker: half a second of being better is not enough");
        // Down: held, then the next; then nobody.
        list[0] = list[0] with { Alive = false };
        var heldFor = 0f;
        while (picker.Pick(list, player, Dt, out why) == 1 && heldFor < 5f) heldFor += Dt;
        Check(MathF.Abs(heldFor - DuelOpponentPicker.DownHold) < 0.05f && picker.Current == 2 && why == "the last one is down",
            $"picker: stays {heldFor:0.00} s on the one that went down, then the next");
        list[1] = list[1] with { Alive = false };
        for (var i = 0; i < 120; i++) picker.Pick(list, player, Dt, out _);
        Check(picker.Current == 0, "picker: nobody left");
        list.Clear();
        list.Add(new DuelCandidate(7, new Vector3(0f, 0f, 60f), true, true, false, false));
        Check(picker.Pick(list, player, Dt, out _) == 0, "picker: out of range is nobody");
    }

    private static void Placement()
    {
        Check(MathF.Abs(DuelCameraSolver.SideAngle(1f) - DuelCameraSolver.SideAngleNear) < 1e-4f &&
              MathF.Abs(DuelCameraSolver.SideAngle(30f) - DuelCameraSolver.SideAngleFar) < 1e-4f &&
              DuelCameraSolver.SideAngle(5f) > DuelCameraSolver.SideAngle(9f), "side angle: wide when close, narrow when far");

        // First frame: the shot starts from the camera it was given.
        var rig = new Rig();
        var chest = rig.Player.At(0.74f);
        var beginEye = chest + DeathShotMath.OrbitDirection(MathF.PI, 15f * Deg) * 5f;
        rig.Step();
        var view = Vector3.Normalize(rig.Out.LookAt - rig.Out.Eye);
        Check(Vector3.Distance(rig.Out.Eye, beginEye) < 0.15f && Vector3.Dot(view, Vector3.Normalize(chest - beginEye)) > MathF.Cos(3f * Deg) && MathF.Abs(rig.Out.Fov - 0.78f) < 0.02f,
            $"first frame continues the camera it starts from (eye {Vector3.Distance(rig.Out.Eye, beginEye):0.000} m off)");

        // Settled two-shot.
        rig.Run(4f);
        var off = DeathShotMath.AngleDelta(MathF.PI, rig.EyeAzimuth);
        var want = DuelCameraSolver.SideAngle(3f);
        Check(MathF.Abs(MathF.Abs(off) - want) < 2f * Deg, $"settled: the eye sits {MathF.Abs(off) / Deg:0.0}° off behind the player (wanted {want / Deg:0.0}°)");
        Check(MathF.Abs(rig.EyeElevation - DuelCameraSolver.BaseElevation) < 1.5f * Deg, $"settled: elevation {rig.EyeElevation / Deg:0.0}°");
        var p = rig.Ndc(rig.Player.At(0.7f));
        var o = rig.Ndc(rig.Opponent!.Value.At(0.7f));
        Check(rig.HeadsIn && p.X * o.X < 0f && MathF.Abs(p.X - o.X) > 0.35f, $"settled: one fighter on each side of the picture ({p.X:0.00} and {o.X:0.00})");
        Check(p.Z < o.Z, "settled: the player is the nearer one");
        Check(MathF.Abs(DeathShotMath.AngleDelta(rig.Out.Yaw, DeathShotMath.Azimuth(rig.Out.Eye - rig.Out.LookAt))) < 1e-3f, "the yaw reported is the rendered one");
        // The yaw the player moves by: "forward" goes at the opponent, and it stays within the lean of the picture.
        var forwardMove = Scenarios.MoveAxes(rig.Out.MoveYaw).Forward;
        var offOpponent = MathF.Acos(Math.Clamp(Vector3.Dot(forwardMove, Vector3.UnitZ), -1f, 1f));
        var offView = MathF.Abs(DeathShotMath.AngleDelta(rig.Out.Yaw, rig.Out.MoveYaw));
        Check(offOpponent < 2f * Deg && offView <= DuelCameraSolver.DefaultMoveLean + 1e-3f && offView > 5f * Deg,
            $"movement yaw: forward is {offOpponent / Deg:0.0}° off the opponent, {offView / Deg:0.0}° off the rendered yaw");
        var standard = rig.EyeDistance;
        Check(MathF.Abs(standard - DuelCameraSolver.RecipeFor(DuelFraming.Standard).Near * 1.0f) < 0.5f, $"settled: {standard:0.00} m from the player");

        // Rotating and moving the whole scene changes nothing.
        var turned = new Rig(MathF.PI + 73f * Deg);
        var rot = Matrix4x4.CreateRotationY(73f * Deg);
        var shift = new Vector3(40f, 3f, -25f);
        DuelSubject Move(DuelSubject s) => new(Vector3.Transform(s.Feet, rot) + shift, Vector3.Transform(s.Head, rot) + shift, s.Radius);
        turned.World.Ground = (_, _) => 3f;
        turned.Player = Move(rig.Player);
        turned.Opponent = Move(rig.Opponent.Value);
        var c2 = turned.Player.At(0.74f);
        turned.Solver.Begin(c2 + DeathShotMath.OrbitDirection(MathF.PI + 73f * Deg, 15f * Deg) * 5f, c2, 0.78f);
        turned.Run(4f + Dt);
        Check(Vector3.Distance(turned.Out.Eye, Vector3.Transform(rig.Out.Eye, rot) + shift) < 0.02f, "rotation and translation of the scene carry the camera with them");

        // Tight < Standard < Wide; a small body brings the camera in.
        var tight = new Rig { Framing = DuelFraming.Tight };
        tight.Run(4f);
        var wide = new Rig { Framing = DuelFraming.Wide };
        wide.Run(4f);
        Check(tight.EyeDistance < standard - 0.2f && wide.EyeDistance > standard + 0.2f, $"framings: {tight.EyeDistance:0.00} / {standard:0.00} / {wide.EyeDistance:0.00} m");
        var small = new Rig { Player = Body(0f, -3f, 0.8f, 0.25f) };
        small.Run(4f);
        Check(small.EyeDistance < standard * 0.75f && small.HeadsIn, $"a small player: {small.EyeDistance:0.00} m");

        // The fighters cross over: the side is picked again, the eye does not cross the line.
        var cross = new Rig();
        cross.Run(3f);
        var sideBefore = MathF.Sign(cross.Out.Eye.X);
        cross.Player = Body(0f, 3f);
        var reanchored = false;
        var worst = float.NegativeInfinity;
        cross.Run(3f, r =>
        {
            reanchored |= r.Out.Reanchored && r.Out.Reason == "crossed";
            worst = MathF.Max(worst, -sideBefore * r.Out.Eye.X);
        });
        Check(reanchored && cross.Out.SideChanges == 0 && worst < 0f && cross.HeadsIn,
            $"cross-over: side picked again, the eye stays on its side of the line (picked again {reanchored}, crossings asked {cross.Out.SideChanges}, nearest to the line {-worst:0.00} m, heads in {cross.HeadsIn})");
        var offAfter = DeathShotMath.AngleDelta(0f, cross.EyeAzimuth);
        Check(MathF.Abs(MathF.Abs(offAfter) - want) < 3f * Deg, $"cross-over: behind the player again ({MathF.Abs(offAfter) / Deg:0.0}° off)");

        // Nobody: the azimuth is kept while the player runs.
        var alone = new Rig { Opponent = null };
        alone.Run(1f);
        var az = alone.EyeAzimuth;
        for (var i = 0; i < 180; i++)
        {
            alone.Player = Body(i * 0.1f, -3f + i * 0.05f);
            alone.Step();
        }
        Check(MathF.Abs(DeathShotMath.AngleDelta(alone.Out.Yaw, alone.Out.MoveYaw)) < 1e-3f, "nobody: the player moves by the rendered yaw");
        Check(!alone.Out.TwoShot && MathF.Abs(DeathShotMath.AngleDelta(az, alone.EyeAzimuth)) < 8f * Deg && Metrics.Inside(alone.Ndc(alone.Player.Head)),
            "nobody: the camera keeps its azimuth and the player in the picture");
        for (var i = 0; i < 30; i++) alone.Step(yaw: 90f * Deg / 30f);
        alone.Run(1.5f);
        Check(MathF.Abs(DeathShotMath.AngleDelta(az + 90f * Deg, alone.EyeAzimuth)) < 4f * Deg, "nobody: the player turns the camera by what they drag");
    }

    private static void Hand()
    {
        var rig = new Rig();
        rig.Run(3f);
        var side = rig.Out.Side;
        var rest = rig.EyeAzimuth;
        for (var i = 0; i < 30; i++) rig.Step(yaw: side * 60f * Deg / 30f);
        rig.Run(0.4f);
        var moved = MathF.Abs(DeathShotMath.AngleDelta(rest, rig.EyeAzimuth));
        Check(moved > 40f * Deg && rig.HeadsIn, $"hand: dragging 60° turns the camera ({moved / Deg:0}°), heads stay in");
        Check(MathF.Abs(DeathShotMath.AngleDelta(rig.Out.Yaw, rig.Out.MoveYaw)) < 2f * Deg, "hand: with the camera turned by the player, movement follows the picture");
        rig.Run(4.5f);
        Check(MathF.Abs(rig.Out.UserYaw) < 3f * Deg && MathF.Abs(DeathShotMath.AngleDelta(rest, rig.EyeAzimuth)) < 4f * Deg && rig.Out.SideChanges == 0,
            "hand: the offset goes back on its own");
        for (var i = 0; i < 30; i++) rig.Step(yaw: -side * 110f * Deg / 30f);
        var crossed = rig.Out.SideChanges == 1 && rig.Out.Side == -side;
        rig.Run(5f);
        var off = DeathShotMath.AngleDelta(MathF.PI, rig.EyeAzimuth);
        Check(crossed && MathF.Sign(off) == -side && MathF.Abs(MathF.Abs(off) - DuelCameraSolver.SideAngle(3f)) < 3f * Deg,
            "hand: dragged across the line, the camera stays on the other side");

        var before = rig.EyeDistance;
        rig.Step(wheel: 3f);
        rig.Run(3f);
        Check(MathF.Abs(rig.EyeDistance / before - MathF.Exp(-0.3f)) < 0.04f, $"hand: three notches in → ×{rig.EyeDistance / before:0.00}");
        var elevation = rig.EyeElevation;
        for (var i = 0; i < 30; i++) rig.Step(pitch: 20f * Deg / 30f);
        rig.Run(2f);
        Check(MathF.Abs(rig.EyeElevation - elevation - 20f * Deg) < 2f * Deg && rig.HeadsIn, $"hand: pitch +20° → {(rig.EyeElevation - elevation) / Deg:0.0}°");
        for (var i = 0; i < 60; i++) rig.Step(pitch: 2f * Deg);
        rig.Run(2f);
        Check(rig.EyeElevation < DuelCameraSolver.BaseElevation + DuelCameraSolver.MaxUserPitch + 1f * Deg, "hand: the pitch is limited");
    }

    private static void Beats()
    {
        var rig = new Rig();
        rig.Run(4f);
        var restDistance = rig.EyeDistance;
        var restFov = rig.Out.Fov;
        Check(rig.Solver.Beat(DuelBeat.HitGiven), "beat: taken");
        Check(!rig.Solver.Beat(DuelBeat.Threat), "beat: a weaker one during it is refused");
        float peakAt = -1f, peak = 0f, closest = restDistance, narrowest = restFov;
        var headsIn = true;
        var start = rig.Time;
        rig.Run(1.6f, r =>
        {
            if (r.Out.Beat >= 0.9f && peakAt < 0f) peakAt = r.Time - start;
            if (r.Out.Beat > peak)
            {
                peak = r.Out.Beat;

            }
            closest = MathF.Min(closest, r.EyeDistance);
            narrowest = MathF.Min(narrowest, r.Out.Fov);
            headsIn &= r.HeadsIn;
        });
        Check(peak > 0.85f && peakAt < 0.3f, $"beat: reaches 90 % after {peakAt:0.00} s (peak {peak:0.00})");
        Check(closest < restDistance * 0.85f && narrowest < restFov * 0.9f, $"beat: pushes in to {closest / restDistance:0.00} of the distance, lens ×{narrowest / restFov:0.00}");
        Check(rig.Out.Beat < 0.03f && MathF.Abs(rig.EyeDistance - restDistance) < 0.05f, $"beat: gone after 1.6 s ({rig.Out.Beat:0.000})");
        Check(headsIn, "beat: both heads stay in the picture");
        rig.Solver.Beat(DuelBeat.Guard);
        rig.Run(0.8f);
        Check(!rig.Solver.Beat(DuelBeat.HitTaken), "beat: the next one waits for the gap");
        Check(rig.Solver.Beat(DuelBeat.Finish), "beat: the finishing blow is always taken");

        // Fighters far apart on the picture's edges: the push-in gives way to keeping both heads.
        var far = new Rig { Opponent = Body(6f, -3f) };
        far.Run(4f);
        far.Solver.Beat(DuelBeat.HitGiven);
        var kept = true;
        far.Run(1.2f, r => kept &= r.HeadsIn);
        Check(kept, "beat: never pushes a head out of the picture");
    }

    private static void Worlds()
    {
        // A wall a metre to the camera's side: the eye stays in front of it, then the camera changes side.
        var rig = new Rig();
        rig.Run(3f);
        var side = MathF.Sign(rig.Out.Eye.X);
        rig.World.Walls.Add(new Wall(new Vector2(side * 1f, -40f), new Vector2(side * 1f, 40f), 8f));
        var beyond = false;
        var changedAt = -1f;
        var start = rig.Time;
        rig.Run(4f, r =>
        {
            beyond |= side * r.Out.Eye.X > 1f;
            if (changedAt < 0f && r.Out.SideChanged && r.Out.Reason == "blocked") changedAt = r.Time - start;
        });
        Check(!beyond, "wall: the eye never goes through it");
        Check(changedAt > 0.4f && changedAt < 1.5f && MathF.Sign(rig.Out.Eye.X) == -side && rig.HeadsIn, $"wall: the camera changes side after {changedAt:0.00} s");
        Check(rig.Out.Pull > 0.98f, "wall: on the free side the eye is at its distance again");

        // Walls on both sides: no way out, the eye stays pulled in and nothing flips back and forth.
        var corridor = new Rig();
        corridor.World.Walls.Add(new Wall(new Vector2(1f, -40f), new Vector2(1f, 40f), 8f));
        corridor.World.Walls.Add(new Wall(new Vector2(-1f, -40f), new Vector2(-1f, 40f), 8f));
        var out2 = false;
        corridor.Run(6f, r => out2 |= MathF.Abs(r.Out.Eye.X) > 1f);
        Check(!out2 && corridor.Out.SideChanges == 0 && corridor.HeadsIn, "corridor: the eye stays inside, no change of side");

        // Rising ground under the eye.
        var bank = new Rig();
        bank.World.Ground = (x, _) => MathF.Max(0f, MathF.Abs(x) - 1f) * 1.2f;
        var lowest = float.PositiveInfinity;
        bank.Run(4f, r => lowest = MathF.Min(lowest, r.Out.Eye.Y - r.World.Ground(r.Out.Eye.X, r.Out.Eye.Z)));
        var settled = bank.Out.Eye.Y - bank.World.Ground(bank.Out.Eye.X, bank.Out.Eye.Z);
        Check(lowest >= DuelCameraSolver.GroundHardFloor - 1e-3f && settled >= DuelCameraSolver.GroundClearance - 0.02f, $"ground: lowest {lowest:0.00} m above it, settled {settled:0.00} m");
        Check(bank.HeadsIn, "ground: the lifted eye still frames both heads");

        // Water.
        var water = new Rig();
        water.World.Water = 2.2f;
        water.Run(4f);
        Check(water.Out.Eye.Y >= 2.2f + DuelCameraSolver.WaterClearance - 0.02f, $"water: the eye stays {water.Out.Eye.Y - 2.2f:0.00} m above it");
    }

    private static void Edges()
    {
        // The player is teleported: a cut, and the camera goes with them.
        var rig = new Rig();
        rig.Run(3f);
        var rel = rig.Out.Eye - rig.Player.Feet;
        rig.Player = Body(30f, 20f);
        rig.Opponent = Body(30f, 23f);
        rig.Step();
        Check(rig.Out.Cut && Vector3.Distance(rig.Out.Eye - rig.Player.Feet, rel) < 0.6f, "teleport: a cut, the camera keeps its place about the player");

        // The opponent walks into the camera.
        var into = new Rig();
        into.Run(3f);
        var eye = into.Out.Eye;
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < 120; i++)
        {
            var k = i / 119f;
            into.Opponent = Body(eye.X * k, eye.Z * k);
            into.Step();
            var d = new Vector2(into.Out.Eye.X - eye.X * k, into.Out.Eye.Z - eye.Z * k).Length();
            nearest = MathF.Min(nearest, d);
        }
        Check(nearest >= 0.4f + DuelCameraSolver.BodyClearance - 0.02f, $"the eye stays outside the opponent ({nearest:0.00} m from its axis)");

        // The fighters on the same spot, and input that is not a number.
        var same = new Rig { Opponent = Body(0f, -3f) };
        same.Run(1f);
        var finite = float.IsFinite(same.Out.Eye.LengthSquared()) && float.IsFinite(same.Out.LookAt.LengthSquared()) && Vector3.Distance(same.Out.Eye, same.Out.LookAt) > 0.5f;
        same.Player = new DuelSubject(new Vector3(float.NaN, 0f, 0f), Vector3.Zero, 0.4f);
        var held = same.Out;
        same.Step();
        Check(finite && same.Out.Eye == held.Eye, "degenerate input: finite output, the last pose is kept");

        // The view never turns faster than its cap.
        var whip = new Rig();
        whip.Run(2f);
        var last = Vector3.Normalize(whip.Out.LookAt - whip.Out.Eye);
        var fastest = 0f;
        for (var i = 0; i < 240; i++)
        {
            var a = i * 0.35f;
            whip.Opponent = Body(MathF.Sin(a) * 2f, -3f + MathF.Cos(a) * 2f);
            whip.Step();
            var v = Vector3.Normalize(whip.Out.LookAt - whip.Out.Eye);
            fastest = MathF.Max(fastest, MathF.Acos(Math.Clamp(Vector3.Dot(v, last), -1f, 1f)) / Dt);
            last = v;
        }
        Check(fastest <= DuelCameraSolver.ViewCap * 1.02f, $"view speed capped ({fastest / Deg:0}°/s with the opponent whirling round the player)");
    }
}
