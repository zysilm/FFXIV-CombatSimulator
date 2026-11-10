using System.Numerics;
using CombatSimulator.Animation;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

const float G = 9.8f;
const float Dt = 1f / 60f;

// One rigid body of a given height and relative density, under gravity and the water forces only.
// Returns the track of (time, centre height, vertical speed).
static List<(float T, float Y, float V)> Float(float startY, float startSpeed, float height, float density, float seconds, float level = 0f)
{
    var y = startY; var linear = new Vector3(0, startSpeed, 0); var angular = Vector3.Zero;
    var track = new List<(float, float, float)>();
    for (var i = 0; i < (int)(seconds / Dt); i++)
    {
        var t = i * Dt;
        linear.Y -= G * Dt;
        var (surface, surfaceSpeed) = WaterBuoyancy.Surface(level, 3f, -2f, t, G);
        WaterBuoyancy.Apply(ref linear, ref angular, WaterBuoyancy.Submerged(y - height / 2, y + height / 2, surface), density, surfaceSpeed, G, Dt);
        y += linear.Y * Dt;
        track.Add((t, y, linear.Y));
    }
    return track;
}

static float SignedVolume(List<Vector3> triangles)
{
    var volume = 0f;
    for (var i = 0; i + 2 < triangles.Count; i += 3)
        volume += Vector3.Dot(triangles[i], Vector3.Cross(triangles[i + 1], triangles[i + 2])) / 6f;
    return volume;
}

var cases = new (string, Action)[]
{
    ("water: a body lighter than water floats with that share of it under the surface", () => {
        var track = Float(0.5f, 0, 0.3f, 0.78f, 20);
        var late = track.Where(p => p.T > 12).ToList();
        var meanSubmerged = late.Average(p => (0 - (p.Y - .15f)) / .3f);
        Check(MathF.Abs(meanSubmerged - .78f) < .06f, $"floats {meanSubmerged:P0} submerged, expected 78%");
        var bob = late.Max(p => p.Y) - late.Min(p => p.Y);
        Check(bob > .005f, "floating body does not ride the swell at all");
        Check(bob < 2.5f * 2 * WaterBuoyancy.MaxWaveHeight, $"bobbing {bob * 100:F1} cm is larger than the swell allows");
        Check(late.Max(p => MathF.Abs(p.V)) < .5f, "floating body never calms down");
        Console.WriteLine($"      density 0.78 floats {meanSubmerged:P0} submerged, bobbing {bob * 100:F1} cm on a {2 * WaterBuoyancy.MaxWaveHeight * 100:F1} cm swell");
    }),
    ("water: a body denser than water sinks at a bounded speed and never comes back up", () => {
        var track = Float(0.5f, 0, 0.3f, 1.09f, 10);
        Check(track[^1].Y < -1f, "dense body did not sink");
        Check(track.Where(p => p.T > 2).All(p => p.V < 0), "dense body rose");
        var terminal = track.Where(p => p.T > 6).Max(p => MathF.Abs(p.V));
        Check(terminal < 1f, $"sinking speed {terminal:F2} m/s not held by drag");
    }),
    ("water: a fall into water is stopped, then the body comes up and settles", () => {
        var track = Float(5f, 0, 0.3f, 0.78f, 25);
        var entry = track.First(p => p.Y < .15f);
        Check(entry.V < -8f, $"fixture: entry speed {entry.V:F1}");
        var deepest = track.Min(p => p.Y);
        Check(deepest > -3f && deepest < -.3f, $"went {-deepest:F2} m under");
        Check(track.Max(p => p.Y > 0 && p.T > entry.T + .5f ? p.Y : 0) < .6f, "shot back out of the water");
        var late = track.Where(p => p.T > 18).ToList();
        Check(late.All(p => MathF.Abs(p.Y) < .25f) && late.Max(p => MathF.Abs(p.V)) < .5f, "did not settle at the surface");
        Console.WriteLine($"      5 m fall: enters at {-entry.V:F1} m/s, deepest {-deepest:F2} m, floating again by {track.First(p => p.T > entry.T + 1 && MathF.Abs(p.Y) < .2f).T - entry.T:F1} s");
    }),
    ("water: a body released under water rises gradually to the surface", () => {
        var track = Float(-3f, 0, 0.3f, 0.78f, 20);
        var reached = track.FirstOrDefault(p => p.Y > -.2f);
        Check(reached.T > 1.5f && reached.T < 12f, $"reached the surface after {reached.T:F1} s");
        Check(track.Max(p => p.V) < 1.5f, $"rose at {track.Max(p => p.V):F2} m/s");
        Check(track.Where(p => p.T < reached.T).All(p => p.V > -.05f), "sank on the way up");
        Console.WriteLine($"      released 3 m down: surfaces after {reached.T:F1} s, fastest rise {track.Max(p => p.V):F2} m/s");
    }),
    ("water: a whole human body is just lighter than water, chest lightest, shins heaviest", () => {
        // Mass shares after Winter's anthropometric table, by the bone that carries them.
        var parts = new (string Bone, float Mass)[]
        {
            ("j_sebo_a", .139f), ("j_sebo_b", .108f), ("j_sebo_c", .108f), ("j_kosi", .142f), ("j_kao", .081f),
            ("j_asi_a_l", .100f), ("j_asi_a_r", .100f), ("j_asi_b_l", .061f), ("j_asi_b_r", .061f),
            ("j_ude_a_l", .028f), ("j_ude_a_r", .028f), ("j_ude_b_l", .022f), ("j_ude_b_r", .022f),
        };
        var density = parts.Sum(p => p.Mass) / parts.Sum(p => p.Mass / WaterBuoyancy.RelativeDensity(p.Bone));
        Check(density > .90f && density < .99f, $"whole-body density {density:F3}");
        Check(WaterBuoyancy.RelativeDensity("j_sebo_b") < WaterBuoyancy.RelativeDensity("j_kosi") && WaterBuoyancy.RelativeDensity("j_kosi") < WaterBuoyancy.RelativeDensity("j_asi_b_l"), "density order");
        Check(WaterBuoyancy.RelativeDensity("") == 1 && WaterBuoyancy.RelativeDensity("n_unknown") == 1, "unknown bone density");
        Console.WriteLine($"      whole-body relative density {density:F3}");
    }),
    ("water: the surface stays within its swell, and its reported speed is its real rate of change", () => {
        for (var i = 0; i < 2000; i++)
        {
            var x = MathF.Sin(i * .37f) * 40; var z = MathF.Cos(i * .11f) * 40; var t = i * .013f;
            var (h, v) = WaterBuoyancy.Surface(12f, x, z, t, G);
            Check(MathF.Abs(h - 12f) <= WaterBuoyancy.MaxWaveHeight + 1e-5f, "surface left its swell");
            var numeric = (WaterBuoyancy.Surface(12f, x, z, t + 1e-3f, G).Height - WaterBuoyancy.Surface(12f, x, z, t - 1e-3f, G).Height) / 2e-3f;
            Check(MathF.Abs(numeric - v) < 2e-3f, $"surface speed {v} vs measured {numeric}");
        }
        Check(WaterBuoyancy.MaxWaveHeight is > .01f and < .08f, "swell height out of the range the game's water can hide");
    }),
    ("water: drag only ever slows a body relative to the water; nothing happens out of the water", () => {
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var linear = new Vector3((float)random.NextDouble() * 40 - 20, (float)random.NextDouble() * 40 - 20, (float)random.NextDouble() * 40 - 20);
            var angular = new Vector3((float)random.NextDouble() * 30 - 15);
            var water = (float)random.NextDouble() - .5f;
            var before = (linear - new Vector3(0, water, 0)).Length(); var spin = angular.Length();
            var l = linear; var a = angular;
            WaterBuoyancy.Apply(ref l, ref a, (float)random.NextDouble(), 1f, water, 0f, Dt); // no gravity: drag alone
            Check((l - new Vector3(0, water, 0)).Length() <= before + 1e-4f && a.Length() <= spin + 1e-4f, "drag added speed");
            Check(float.IsFinite(l.LengthSquared()) && float.IsFinite(a.LengthSquared()), "drag produced a non-finite velocity");
            var dry = linear; var drySpin = angular;
            WaterBuoyancy.Apply(ref dry, ref drySpin, 0f, .78f, water, G, Dt);
            Check(dry == linear && drySpin == angular, "a dry body was touched");
        }
        Check(WaterBuoyancy.Submerged(1, 2, .5f) == 0 && WaterBuoyancy.Submerged(1, 2, 3) == 1 && MathF.Abs(WaterBuoyancy.Submerged(1, 2, 1.25f) - .25f) < 1e-6f, "submerged share");
        Check(WaterBuoyancy.Submerged(float.NaN, 2, 1) == 0 && WaterBuoyancy.Submerged(1, 1, 2) == 1, "submerged edge cases");
    }),
    ("ground: materials are judged the way a ground raycast judges them", () => {
        Check(CollisionMeshGeometry.IsGround(0x7000) && !CollisionMeshGeometry.IsWater(0x7000), "default material is ground");
        foreach (var water in new ulong[] { 0xB400, 0xB800, 0xBC00 })
            Check(!CollisionMeshGeometry.IsGround(water) && CollisionMeshGeometry.IsWater(water), $"water material {water:X}");
        Check(CollisionMeshGeometry.EffectiveMaterial(0x7000, 0xFFFF, 0) == 0x7000, "unmasked collider changed its triangles");
        Check(CollisionMeshGeometry.EffectiveMaterial(0x7011, 0xB400, 0xFF00) == 0xB411, "collider mask not applied");
        Check(!CollisionMeshGeometry.IsGround(CollisionMeshGeometry.EffectiveMaterial(0x7000, 0xB400, 0xFF00)), "a collider turned to water still counted as ground");
    }),
    ("ground: box, cylinder and sphere colliders become closed, outward-facing triangle shells of the right size", () => {
        // Scale 2 x 0.5 x 3, turned and moved: volumes scale by the determinant (3), whatever the pose.
        var turn = Matrix4x4.CreateFromYawPitchRoll(.8f, -.3f, 1.4f);
        var row0 = new Vector3(turn.M11, turn.M12, turn.M13) * 2f; var row1 = new Vector3(turn.M21, turn.M22, turn.M23) * .5f;
        var row2 = new Vector3(turn.M31, turn.M32, turn.M33) * 3f; var row3 = new Vector3(100, -20, 35);

        var box = new List<Vector3>(); CollisionMeshGeometry.AppendBox(box, row0, row1, row2, row3);
        Check(box.Count == 36, "box triangle count");
        Check(MathF.Abs(SignedVolume(box) - 8f * 3f) < .01f, $"box volume {SignedVolume(box)}");

        var cylinder = new List<Vector3>(); CollisionMeshGeometry.AppendCylinder(cylinder, row0, row1, row2, row3);
        var cylinderVolume = SignedVolume(cylinder) / 3f;
        Check(cylinderVolume > 2 * MathF.PI * .96f && cylinderVolume <= 2 * MathF.PI + 1e-3f, $"cylinder volume {cylinderVolume}");

        var sphere = new List<Vector3>(); CollisionMeshGeometry.AppendSphere(sphere, row0, row1, row2, row3);
        var sphereVolume = SignedVolume(sphere) / 3f;
        Check(sphereVolume > 4f / 3f * MathF.PI * .8f && sphereVolume <= 4f / 3f * MathF.PI + 1e-3f, $"sphere volume {sphereVolume}");

        foreach (var shell in new[] { box, cylinder, sphere })
            foreach (var v in shell)
            {
                var local = v - row3;
                Check(MathF.Abs(Vector3.Dot(local, Vector3.Normalize(row0))) <= 2.001f && MathF.Abs(Vector3.Dot(local, Vector3.Normalize(row1))) <= .501f && MathF.Abs(Vector3.Dot(local, Vector3.Normalize(row2))) <= 3.001f, "vertex outside the collider");
            }
    }),
    ("ground: only triangles reaching into the patch are taken", () => {
        var min = new Vector3(-6, -8, -6); var max = new Vector3(6, 8, 6);
        Check(CollisionMeshGeometry.Overlaps(new Vector3(5, 0, 5), new Vector3(9, 0, 5), new Vector3(5, 0, 9), min, max), "triangle crossing the edge dropped");
        Check(!CollisionMeshGeometry.Overlaps(new Vector3(7, 0, 0), new Vector3(9, 0, 0), new Vector3(7, 0, 2), min, max), "triangle beside the patch kept");
        Check(!CollisionMeshGeometry.Overlaps(new Vector3(0, 9, 0), new Vector3(1, 12, 0), new Vector3(0, 9, 1), min, max), "triangle above the patch kept");
        Check(CollisionMeshGeometry.Overlaps(new Vector3(-100), new Vector3(100), min, max) && !CollisionMeshGeometry.Overlaps(new Vector3(7), new Vector3(9), min, max), "box overlap");
        Check(CollisionMeshGeometry.Transform(new Vector3(1, 2, 3), Vector3.UnitX * 2, Vector3.UnitY, Vector3.UnitZ, new Vector3(10, 0, 0)) == new Vector3(12, 2, 3), "transform");
    }),
};

var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;
