using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>One run, printed twice a second: for looking into what a shot did on one recording.</summary>
internal static class Probe
{
    public static void Run(List<Trajectory> all, string name, string profileName, int azimuth)
    {
        var t = all.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (t == null || !Enum.TryParse<DeathShotProfile>(profileName, true, out var profile))
        {
            Console.Error.WriteLine("usage: probe <part of a recording name> <LowClose|LowFace|LowFullBody> [start azimuth 0..7]");
            return;
        }
        var world = new RecordedWorld(t);
        var cams = Runners.New(t, world, StartCamera.For(t, azimuth), profile, out var scale);
        Console.WriteLine($"{t.Name}: {t.Frames.Length} frames, {t.Seconds:F1} s, body scale {scale:F2}, exact ground triangles {t.Triangles.Length / 3}");
        var next = 0f;
        for (var i = 0; i < cams.Length; i++)
        {
            var f = t.Frames[i];
            var c = cams[i];
            if (c.Event == null && !c.Cut && f.Time < next) continue;
            next = f.Time + 0.5f;
            var dir = Vector3.Normalize(c.LookAt - c.Eye);
            DynamicCameraSolver.BasisFromForward(dir, out var right, out var up);
            var tanV = MathF.Tan(c.Fov * 0.5f);
            var lens = new DynamicCameraSolver.Lens(tanV * Runners.Aspect, tanV);
            var p = t.Points(f.Runtime);
            var ground = world.FieldHeight(p.Hips.X, p.Hips.Z);
            float Reach(Vector3 eye, Vector3 fwd, DynamicCameraSolver.Lens l)
            {
                DynamicCameraSolver.BasisFromForward(fwd, out var r, out var u);
                var most = 0f;
                foreach (var b in f.Runtime)
                {
                    var ndc = DynamicCameraSolver.Project(new Vector3(b.X, MathF.Max(b.Y, ground + 0.03f), b.Z), eye, fwd, r, u, l);
                    most = ndc.Z < 0.05f ? float.MaxValue : MathF.Max(most, MathF.Max(MathF.Abs(ndc.X), MathF.Abs(ndc.Y)));
                }
                return most;
            }
            var reach = Reach(c.Eye, dir, lens);
            var goalTan = MathF.Tan(ShotRecipe.Of(c.Shot).Fov * 0.5f);
            var goalReach = Reach(c.GoalEye, Vector3.Normalize(c.GoalLookAt - c.GoalEye), new DynamicCameraSolver.Lens(goalTan * Runners.Aspect, goalTan));
            var away = c.Eye - c.LookAt;
            Console.WriteLine($"  t={f.Time,5:F1} {(f.PhysicsStarted ? "ragdoll" : "anim   ")} {c.Pose,-8} {c.Shot,-15} hold={(c.Holding ? 1 : 0)} " +
                              $"az {DeathShotMath.Azimuth(away) * 57.3f,6:F1} dist {away.Length(),5:F2} elev {MathF.Asin(away.Y / away.Length()) * 57.3f,6:F1} clear {c.Eye.Y - (world.GroundBelow(c.Eye + new Vector3(0f, 1.5f, 0f)) ?? ground),5:F2} " +
                              $"whole-body reach {(reach > 99f ? "behind" : reach.ToString("F2"))} (goal {(goalReach > 99f ? "behind" : goalReach.ToString("F2"))}, {Vector3.Distance(c.GoalEye, c.GoalLookAt):F2} m) hips above ground {p.Hips.Y - ground:F2} goal off {Vector3.Distance(c.Eye, c.GoalEye):F2}" +
                              $"{(c.PulledIn ? " pulled-in" : "")}{(c.Cut ? " CUT" : "")}{(c.Event != null ? " <" + c.Event + ">" : "")}");
        }
    }
}
