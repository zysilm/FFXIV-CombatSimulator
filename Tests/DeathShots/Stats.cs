using System.Globalization;
using System.Numerics;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>What the exported trajectories contain (the P0 data report): durations, the pose class at the start,
/// when the ragdoll starts and when the body has settled, the size of the position jump where the source changes
/// from bones to rigid bodies, and the check that the body frame's front agrees with the recorded facing.</summary>
internal static class Stats
{
    public static void Run(List<Trajectory> all)
    {
        var inv = CultureInfo.InvariantCulture;
        var usable = all.Where(t => t.Humanoid && t.Frames.Length >= 2).ToList();
        Console.WriteLine($"trajectories: {all.Count} ({all.Count(t => t.IsPlayer)} player, {all.Count(t => !t.IsPlayer)} npc), humanoid {all.Count(t => t.Humanoid)}, " +
                          $"npc humanoid {all.Count(t => !t.IsPlayer && t.Humanoid)}");
        var seconds = all.Select(t => t.Seconds).OrderBy(x => x).ToArray();
        Console.WriteLine(string.Create(inv, $"duration s: min {seconds[0]:F1}, p25 {P(seconds, .25f):F1}, median {P(seconds, .5f):F1}, p75 {P(seconds, .75f):F1}, max {seconds[^1]:F1}; " +
                          $"under 1 s: {seconds.Count(s => s < 1f)}, under 5 s: {seconds.Count(s => s < 5f)}, never reached physics: {all.Count(t => !t.Frames.Any(f => f.PhysicsStarted))}"));
        var frames = all.Sum(t => t.Frames.Length);
        Console.WriteLine($"frames: {frames}; without a pose: {all.Sum(t => t.Frames.Count(f => f.PoseSource == 0))}; pre-physics with a repeated (unchanged) pose: {all.Sum(t => t.Frames.Count(f => !f.PhysicsStarted && !f.PoseFresh))}");
        var bodies = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var withPhysics = 0;
        foreach (var t in all)
        {
            var f = t.Frames.LastOrDefault(x => x.PhysicsStarted);
            if (f == null) continue;
            withPhysics++;
            for (var b = 0; b < t.Bones.Length; b++)
                if (!float.IsNaN(f.Body[b].X)) bodies[t.Bones[b]] = bodies.GetValueOrDefault(t.Bones[b]) + 1;
        }
        Console.WriteLine($"recordings with physics: {withPhysics}; bones with a rigid body (count of recordings): " + string.Join(", ", bodies.Select(b => $"{b.Key} {b.Value}")));

        var startClass = new SortedDictionary<string, int>();
        var physClass = new SortedDictionary<string, int>();
        var settleClass = new SortedDictionary<string, int>();
        var endClass = new SortedDictionary<string, int>();
        var physAt = new List<float>();
        var settleAfter = new List<float>();
        var animSettle = new List<float>();
        var jumps = new SortedDictionary<string, List<float>>(StringComparer.Ordinal);
        var hips = new List<float>();
        var torso = new List<float>();
        var frontAgree = new List<float>();
        var scales = new List<float>();
        var playerScales = new List<float>();
        int neverSettled = 0, resettled = 0, settledBeforePhysics = 0;
        foreach (var t in usable)
        {
            var world = new RecordedWorld(t);
            var classifier = new PoseClassifier();
            var settle = new SettleDetector();
            var firstPoints = t.Points(t.Frames[0].Runtime);
            var first = BodyFrame.From(firstPoints, t.Frames[0].GroundY);
            // The solver's body scale: spine length against the reference body.
            var bodyScale = Math.Clamp((Vector3.Distance(firstPoints.Chest, firstPoints.Hips) + Vector3.Distance(firstPoints.Head, firstPoints.Chest)) / DeathShotSolver.ReferenceSpine, 0.4f, 4f);
            classifier.Scale = bodyScale;
            scales.Add(bodyScale);
            if (t.IsPlayer) playerScales.Add(bodyScale);
            if (first.Axis.Y > 0.9f)
            {
                hips.Add(first.HipsHeight);
                torso.Add(Vector3.Distance(first.Chest, first.Hips));
                frontAgree.Add(Vector3.Dot(first.FrontHorizontal, new Vector3(MathF.Sin(t.Facing), 0f, MathF.Cos(t.Facing))));
            }
            var wasPhysics = false;
            var wasSettled = false;
            var physicsTime = -1f;
            var settles = 0;
            var c = PoseClass.Standing;
            for (var i = 0; i < t.Frames.Length; i++)
            {
                var f = t.Frames[i];
                var points = t.Points(f.Runtime);
                var ground = world.HasGround ? world.FieldHeight(points.Hips.X, points.Hips.Z) : f.GroundY;
                c = classifier.Update(BodyFrame.From(points, ground), f.Dt);
                if (i == 0) Count(startClass, c);
                if (f.PhysicsStarted && !wasPhysics)
                {
                    wasPhysics = true;
                    physicsTime = f.Time;
                    physAt.Add(f.Time);
                    Count(physClass, c);
                    settle.Reset();
                    if (wasSettled) settledBeforePhysics++;
                    wasSettled = false;
                    for (var b = 0; b < t.Bones.Length; b++)
                    {
                        if (float.IsNaN(f.Body[b].X)) continue;
                        if (!jumps.TryGetValue(t.Bones[b], out var list)) jumps[t.Bones[b]] = list = new List<float>();
                        list.Add(Vector3.Distance(f.Body[b], t.Frames[Math.Max(0, i - 1)].Bone[b]));
                    }
                }
                var s = settle.Update(points, f.Dt);
                if (s && !wasSettled)
                {
                    if (wasPhysics)
                    {
                        settles++;
                        if (settles == 1) { settleAfter.Add(f.Time - physicsTime); Count(settleClass, c); }
                    }
                    else animSettle.Add(f.Time);
                }
                wasSettled = s;
            }
            if (wasPhysics)
            {
                Count(endClass, c);
                if (settles == 0) neverSettled++;
                if (settles > 1) resettled++;
            }
        }
        Console.WriteLine(string.Create(inv, $"standing first frames: {hips.Count} of {usable.Count}; hips height {hips.Min():F2}..{hips.Max():F2} m (median {P(hips, .5f):F2}); hips-chest {torso.Min():F2}..{torso.Max():F2} m; " +
                          $"front dot recorded facing: min {frontAgree.Min():F3}, median {P(frontAgree, .5f):F3} (1 = the body frame's front is the facing)"));
        Console.WriteLine(string.Create(inv, $"front against the recorded facing on those frames: {frontAgree.Count(x => x > 0.5f)} within 60 deg, {frontAgree.Count(x => x < 0f)} more than 90 deg off (a death can start mid-turn or mid-stagger; the facing is the root's)"));
        Console.WriteLine(string.Create(inv, $"body scale (spine length / {DeathShotSolver.ReferenceSpine} m): {scales.Min():F2}..{scales.Max():F2}, median {P(scales, .5f):F2}; the player's recordings {(playerScales.Count > 0 ? playerScales.Min() : 0):F2}..{(playerScales.Count > 0 ? playerScales.Max() : 0):F2}"));
        Console.WriteLine("pose class at the first frame: " + Show(startClass));
        Console.WriteLine("pose class when the ragdoll starts: " + Show(physClass));
        Console.WriteLine("pose class when the body first settles after the ragdoll started: " + Show(settleClass));
        Console.WriteLine("pose class at the last frame (recordings with physics): " + Show(endClass));
        Console.WriteLine(string.Create(inv, $"ragdoll start s: min {physAt.Min():F2}, median {P(physAt, .5f):F2}, max {physAt.Max():F2}"));
        if (settleAfter.Count > 0)
            Console.WriteLine(string.Create(inv, $"ragdoll start to first settle s: min {settleAfter.Min():F1}, median {P(settleAfter, .5f):F1}, p90 {P(settleAfter, .9f):F1}, max {settleAfter.Max():F1}; never settled within the recording: {neverSettled}; moved again and settled again: {resettled}"));
        Console.WriteLine(string.Create(inv, $"the death animation alone came to rest before the ragdoll started in {settledBeforePhysics} recordings (at {(animSettle.Count > 0 ? animSettle.Min() : 0):F1}..{(animSettle.Count > 0 ? animSettle.Max() : 0):F1} s)"));
        Console.WriteLine("position jump where the source changes from bone origin to rigid body centre, m (median / max): " +
                          string.Join(", ", jumps.Select(j => string.Create(inv, $"{j.Key} {P(j.Value, .5f):F3}/{j.Value.Max():F3}"))));
    }

    private static void Count(SortedDictionary<string, int> d, PoseClass c) => d[c.ToString()] = d.GetValueOrDefault(c.ToString()) + 1;
    private static string Show(SortedDictionary<string, int> d) => string.Join(", ", d.Select(p => $"{p.Key} {p.Value}"));

    public static float P(IEnumerable<float> values, float q)
    {
        var a = values.OrderBy(x => x).ToArray();
        if (a.Length == 0) return float.NaN;
        return a[Math.Clamp((int)MathF.Round(q * (a.Length - 1)), 0, a.Length - 1)];
    }
}
