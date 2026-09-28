using System.Collections;
using System.Numerics;
using System.Reflection;
using CombatSimulator.Animation.Attachment;

// Diagnostic experiment, not acceptance tests: the inputs approximate a human skeleton.
// Template radii, tethers and connectivity mirror CreateRealAttachment. No game process is called.
// Reflection alters ONLY test instances, to isolate production contact paths without editing the DLL.
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
var failures = 0;
Console.WriteLine("Synthetic attachment diagnostics; metres, degrees, seconds. Production solver linked unchanged.");
Console.WriteLine("case,initial_point_penetrations,initial_edge_penetrations,first_projection_cm,max_center_displacement_cm,max_tether_excess_cm,last2s_center_jitter_mm_rms,last2s_rotation_deg_rms,max_particle_speed,recoveries,sleeping");
foreach (var kind in new[] { "top", "skirt" })
foreach (var posture in new[] { "standing", "side", "side-ground" })
foreach (var mode in new[] { "baseline", "no-collisions", "no-edge-contact", "no-bridge-contact", "no-self-contact" })
{
    var sim = Fixture(kind, posture, mode);
    var centers = sim.Rings.Select((r, i) => sim.Center(i)).ToArray();
    var initialPenetration = sim.Particles.Count(p => sim.Capsules.Any(c => Depth(p.Position, c, 0.012f) > 0.001f));
    var edgePenetration = Edges(sim).Count(e => Read<bool>(e, "Surface") && !Read<bool>(e, "Bend") && Enumerable.Range(0, 21).Any(k =>
        sim.Capsules.Any(c => Depth(Vector3.Lerp(sim.Particles[Read<int>(e, "A")].Position,
            sim.Particles[Read<int>(e, "B")].Position, k / 20f), c, 0.012f) > 0.001f)));

    var projectionFixture = Fixture(kind, posture, mode);
    var stepCaps = (List<AttachmentSimulation.Capsule>)typeof(AttachmentSimulation).GetField("stepCapsules", Private)!.GetValue(projectionFixture)!;
    stepCaps.Clear(); stepCaps.AddRange(projectionFixture.Capsules);
    foreach (var p in projectionFixture.Particles) p.BeforeStep = p.Position;
    typeof(AttachmentSimulation).GetMethod("Collide", Private)!.Invoke(projectionFixture, new object[] { 0.012f });
    typeof(AttachmentSimulation).GetMethod("CollideEdges", Private)!.Invoke(projectionFixture, new object[] { 0.012f });
    var projection = centers.Select((p, i) => Vector3.Distance(p, projectionFixture.Center(i))).Max();

    var previous = centers.ToArray();
    var rotations = sim.Rings.Select((r, i) => sim.Rotation(i)).ToArray();
    double jitter = 0, angular = 0; int samples = 0;
    float maxDisplacement = 0, maxTether = 0, maxSpeed = 0;
    for (var frame = 0; frame < 600; frame++)
    {
        sim.Advance(1f / 60);
        for (var i = 0; i < sim.Rings.Count; i++)
        {
            var center = sim.Center(i); var q = sim.Rotation(i);
            maxDisplacement = MathF.Max(maxDisplacement, Vector3.Distance(center, centers[i]));
            if (frame >= 480)
            {
                jitter += Vector3.DistanceSquared(center, previous[i]);
                var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(q, rotations[i])), 0, 1)) * 180 / MathF.PI;
                angular += angle * angle; samples++;
            }
            previous[i] = center; rotations[i] = q;
        }
        foreach (var p in sim.Particles)
        {
            if (p.Range >= 0) maxTether = MathF.Max(maxTether, Vector3.Distance(p.Position, p.Target) - p.Range);
            maxSpeed = MathF.Max(maxSpeed, p.Velocity.Length());
        }
    }
    Console.WriteLine(FormattableString.Invariant($"{kind}/{posture}/{mode},{initialPenetration},{edgePenetration},{projection * 100:F2},{maxDisplacement * 100:F2},{maxTether * 100:F2},{Math.Sqrt(jitter / samples) * 1000:F3},{Math.Sqrt(angular / samples):F3},{maxSpeed:F2},{sim.RecoveryCount},{sim.Sleeping}"));
    if (mode == "baseline" && (initialPenetration != 0 || edgePenetration != 0 ||
        maxSpeed > 0.6001f || sim.RecoveryCount != 0 || Math.Sqrt(jitter / samples) > 0.001f))
    { failures++; Console.WriteLine("FAIL fitted baseline penetration/speed/stability regression"); }
}
CoordinateRoundTrip();
ContactNormalProbe();
FrameContinuityProbe();
CoupledLayersProbe();
return failures == 0 ? 0 : 1;

static T Read<T>(object value, string name) => (T)value.GetType().GetField(name)!.GetValue(value)!;
static IEnumerable<object> Edges(AttachmentSimulation sim) =>
    ((IEnumerable)typeof(AttachmentSimulation).GetField("edges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sim)!).Cast<object>();
static float Depth(Vector3 p, AttachmentSimulation.Capsule c, float thickness)
{
    var axis = c.B - c.A;
    var t = Math.Clamp(Vector3.Dot(p - c.A, axis) / MathF.Max(1e-7f, axis.LengthSquared()), 0, 1);
    return c.Radius + thickness - Vector3.Distance(p, c.A + t * axis);
}

static AttachmentSimulation Fixture(string kind, string posture, string mode)
{
    var settings = new AttachmentSettings { Template = kind == "top" ? GarmentTemplate.Top : GarmentTemplate.Skirt,
        SelfCollision = mode != "no-self-contact" && mode != "no-collisions" };
    var sim = new AttachmentSimulation(settings);
    var points = new Dictionary<string, Vector3>
    {
        ["j_kosi"] = new(0, 1, 0), ["j_sebo_a"] = new(0, 1.16f, 0), ["j_sebo_b"] = new(0, 1.32f, 0),
        ["j_sebo_c"] = new(0, 1.48f, 0), ["j_kubi"] = new(0, 1.6f, 0), ["j_kao"] = new(0, 1.73f, 0),
    };
    foreach (var side in new[] { "l", "r" })
    {
        var s = side == "l" ? 1 : -1;
        points[$"j_sako_{side}"] = new(s * 0.07f, 1.5f, 0);
        points[$"j_ude_a_{side}"] = new(s * 0.2f, 1.47f, 0);
        points[$"j_ude_b_{side}"] = new(s * 0.31f, 1.22f, 0);
        points[$"j_te_{side}"] = new(s * 0.38f, 0.98f, 0);
        points[$"j_oya_a_{side}"] = new(s * 0.4f, 0.93f, 0.015f);
        points[$"j_asi_a_{side}"] = new(s * 0.09f, 0.96f, 0);
        points[$"j_asi_b_{side}"] = new(s * 0.1f, 0.53f, 0);
        points[$"j_asi_d_{side}"] = new(s * 0.1f, 0.13f, 0);
        points[$"j_asi_e_{side}"] = new(s * 0.1f, 0.07f, 0.12f);
    }
    for (var column = 0; column < 6; column++)
        for (var row = 0; row < 3; row++)
        {
            var angle = column * MathF.Tau / 6;
            var r = 0.14f + row * 0.035f;
            points[$"sk{column}_{row}"] = new(MathF.Cos(angle) * r, 0.98f - row * 0.16f, MathF.Sin(angle) * r);
        }
    var root = posture != "standing" ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) : Quaternion.Identity;
    Vector3 World(Vector3 p)
    {
        if (posture == "standing") return p;
        var w = Vector3.Transform(p - Vector3.UnitY, root) + new Vector3(0, posture == "side" ? 0.46f : 0.12f, 0);
        if (posture == "side-ground") w.Y = MathF.Max(0.12f, w.Y);
        return w;
    }
    var rings = new Dictionary<string, int>();
    void Add(string name, string toward, float radius, float attachment, string? parent = null)
    {
        var center = World(points[name]);
        var y = Vector3.Normalize(World(points[toward]) - center);
        var frame = new AttachmentFrame().Update(root, y);
        var i = sim.AddRing(name, center, frame, radius * settings.OpeningScale + settings.Thickness, attachment);
        rings[name] = i;
        if (parent != null) sim.Connect(rings[parent], i);
    }
    if (kind == "top")
    {
        Add("j_kosi", "j_sebo_a", 0.12f, -1);
        Add("j_sebo_a", "j_sebo_b", 0.115f, -1, "j_kosi");
        Add("j_sebo_b", "j_sebo_c", 0.13f, -1, "j_sebo_a");
        Add("j_sebo_c", "j_kubi", 0.13f, -1, "j_sebo_b");
        Add("j_kubi", "j_kao", 0.065f, 0.7f, "j_sebo_c");
        foreach (var side in new[] { "l", "r" })
        {
            Add($"j_sako_{side}", $"j_ude_a_{side}", 0.065f, 1, "j_sebo_c");
            Add($"j_ude_a_{side}", $"j_ude_b_{side}", 0.055f, -1, $"j_sako_{side}");
            Add($"j_ude_b_{side}", $"j_te_{side}", 0.043f, -1, $"j_ude_a_{side}");
            Add($"j_te_{side}", $"j_oya_a_{side}", 0.038f, 0.45f, $"j_ude_b_{side}");
        }
    }
    else
    {
        Add("j_kosi", "j_sebo_a", 0.13f, 0.65f);
        for (var column = 0; column < 6; column++)
            for (var row = 0; row < 3; row++)
            {
                var parent = row == 0 ? "j_kosi" : $"sk{column}_{row - 1}";
                Add($"sk{column}_{row}", parent, 0.024f, -1, parent);
            }
    }
    if (mode != "no-collisions")
    {
        void Cap(string a, string b, float radius) => sim.Capsules.Add(new AttachmentSimulation.Capsule(World(points[a]), World(points[b]), radius, Vector3.Zero));
        Cap("j_kosi", "j_sebo_a", 0.105f); Cap("j_sebo_a", "j_sebo_b", 0.1f);
        Cap("j_sebo_b", "j_sebo_c", 0.115f); Cap("j_sebo_c", "j_kubi", 0.1f);
        Cap("j_kubi", "j_kao", 0.06f); Cap("j_kao", "j_kao", 0.095f);
        foreach (var side in new[] { "l", "r" })
        {
            Cap($"j_sako_{side}", $"j_ude_a_{side}", 0.06f);
            Cap($"j_ude_a_{side}", $"j_ude_b_{side}", 0.05f);
            Cap($"j_ude_b_{side}", $"j_te_{side}", 0.038f);
            Cap($"j_asi_a_{side}", $"j_asi_b_{side}", 0.075f);
            Cap($"j_asi_b_{side}", $"j_asi_d_{side}", 0.055f);
            Cap($"j_asi_d_{side}", $"j_asi_e_{side}", 0.05f);
        }
    }
    sim.FitToBody();
    if (mode != "no-collisions")
        for (var i = 0; i < sim.Rings.Count; i++) sim.SetFloor(i, true, Vector3.Zero, Vector3.UnitY);
    if (mode is "no-edge-contact" or "no-bridge-contact")
        foreach (var e in Edges(sim))
            if (mode == "no-edge-contact" || Read<int>(e, "A") / 6 != Read<int>(e, "B") / 6)
                e.GetType().GetField("Bend")!.SetValue(e, true); // compliance/rest/lambda unchanged
    return sim;
}

static void CoordinateRoundTrip()
{
    var max = 0f;
    for (var i = 0; i < 100; i++)
    {
        var root = Quaternion.CreateFromYawPitchRoll(i * 0.1f, i * 0.07f, i * 0.03f);
        var origin = new Vector3(15, 7, -100);
        var model = new Vector3(0.1f, 1.5f, -0.12f);
        var center = origin + Vector3.Transform(new Vector3(0, 1, 0), root);
        var frame = Quaternion.CreateFromYawPitchRoll(i * 0.02f, i * 0.01f, -i * 0.03f);
        var delta = Quaternion.Normalize(frame * Quaternion.Inverse(frame));
        var world = origin + Vector3.Transform(model, root);
        var back = Vector3.Transform(center + Vector3.Transform(world - center, delta) - origin, Quaternion.Inverse(root));
        max = MathF.Max(max, Vector3.Distance(back, model));
    }
    Console.WriteLine($"Zero-deformation render position round-trip max error: {max:E3} m (unit-scale algebra only)");
}

static void ContactNormalProbe()
{
    var p = new AttachmentSimulation.Particle();
    var contact = typeof(AttachmentSimulation).GetMethod("Contact", BindingFlags.Static | BindingFlags.NonPublic)!;
    contact.Invoke(null, new object[] { p, Vector3.UnitX, 0.0001f, Vector3.Zero, 0.45f });
    contact.Invoke(null, new object[] { p, -Vector3.UnitX, 0.1f, Vector3.UnitY, 0.7f });
    Console.WriteLine($"Opposite contacts (+X 0.1mm, -X 100mm): resultant normal X={p.ContactNormal.X:F3}; physically dominant direction is -X");
}

static Quaternion LegacyFrame(float angle)
{
    var y = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0));
    var hint = Vector3.UnitX;
    var x = hint - y * Vector3.Dot(hint, y);
    if (x.LengthSquared() < 0.0001f)
        x = Vector3.Cross(y, MathF.Abs(y.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ);
    x = Vector3.Normalize(x); var z = Vector3.Normalize(Vector3.Cross(x, y));
    return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
        x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1)));
}

static void FrameContinuityProbe()
{
    var before = LegacyFrame(-0.012f); var after = LegacyFrame(0.012f);
    var jump = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(before, after)), 0, 1)) * 180 / MathF.PI;
    Console.WriteLine($"Projected-X garment frame: bone direction changes {0.024f * 180 / MathF.PI:F3} degrees, frame rotates {jump:F3} degrees");
    foreach (var smooth in new[] { false, true })
    {
        var sim = new AttachmentSimulation(new AttachmentSettings { SelfCollision = false });
        var center = Vector3.UnitY;
        var transport = new AttachmentFrame();
        var initial = smooth ? transport.Update(Quaternion.Identity, Vector3.UnitX) : LegacyFrame(0);
        sim.AddRing("clavicle", center, initial, 0.085f, 1);
        var previous = sim.Particles.Select(p => p.Target).ToArray();
        float targetJump = 0, speed = 0, visualJump = 0;
        var oldVisual = sim.Rotation(0) * Quaternion.Inverse(initial);
        for (var frame = 0; frame < 600; frame++)
        {
            var angle = MathF.Sin(frame / 60f * MathF.Tau) * 0.012f;
            var q = smooth ? transport.Update(Quaternion.Identity, new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0)) : LegacyFrame(angle);
            sim.SetTarget(0, center, q);
            for (var i = 0; i < previous.Length; i++)
            {
                targetJump = MathF.Max(targetJump, Vector3.Distance(previous[i], sim.Particles[i].Target));
                previous[i] = sim.Particles[i].Target;
            }
            sim.Advance(1f / 60);
            foreach (var p in sim.Particles) speed = MathF.Max(speed, p.Velocity.Length());
            var visual = Quaternion.Normalize(sim.Rotation(0) * Quaternion.Inverse(q));
            var v = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(oldVisual, visual)), 0, 1)) * 180 / MathF.PI;
            visualJump = MathF.Max(visualJump, v); oldVisual = visual;
        }
        Console.WriteLine($"Frame input {(smooth ? "production transported frame" : "legacy projected-X")}: max particle target jump={targetJump * 100:F2}cm, max speed={speed:F2}m/s, max rendered rotation step={visualJump:F2}deg");
    }
}

static void CoupledLayersProbe()
{
    foreach (var contact in new[] { false, true })
    {
        var top = Fixture("top", "side-ground", "baseline");
        var skirt = Fixture("skirt", "side-ground", "baseline");
        var states = new[] { top, skirt };
        var previous = states.Select(s => s.Rings.Select((r, i) => s.Center(i)).ToArray()).ToArray();
        var sum = new double[2]; var samples = new int[2]; var speed = new float[2];
        for (var frame = 0; frame < 600; frame++)
        {
            // Same common snapshot rule as CaptureRealAttachmentLayers in the plugin.
            top.OtherGarments.Clear(); skirt.OtherGarments.Clear();
            if (contact)
            {
                foreach (var p in top.Particles) skirt.OtherGarments.Add(new(p.Position, p.Velocity, 0.012f));
                foreach (var p in skirt.Particles) top.OtherGarments.Add(new(p.Position, p.Velocity, 0.012f));
            }
            for (var k = 0; k < 2; k++)
            {
                var sim = states[k]; sim.Advance(1f / 60);
                for (var i = 0; i < sim.Rings.Count; i++)
                {
                    var p = sim.Center(i);
                    if (frame >= 480) { sum[k] += Vector3.DistanceSquared(p, previous[k][i]); samples[k]++; }
                    previous[k][i] = p;
                }
                foreach (var p in sim.Particles) speed[k] = MathF.Max(speed[k], p.Velocity.Length());
            }
        }
        Console.WriteLine($"Coupled side-ground, layer contact={contact}: top/skirt late center jitter={Math.Sqrt(sum[0] / samples[0]) * 1000:F3}/{Math.Sqrt(sum[1] / samples[1]) * 1000:F3}mm RMS/frame; max speed={speed[0]:F2}/{speed[1]:F2}m/s; sleeping={top.Sleeping}/{skirt.Sleeping}");
    }
}
