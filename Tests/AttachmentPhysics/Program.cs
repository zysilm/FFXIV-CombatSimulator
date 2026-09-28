using System.Numerics;
using System.Text.Json;
using CombatSimulator.Animation.Attachment;

var checks = new (string Name, Action Run)[]
{
    ("hanging garment retains its connections", Hanging),
    ("gravity follows world down after side fall", SideFall),
    ("floor contact and pickup", Pickup),
    ("zero slack pins retained opening", Pinned),
    ("frame rates 30/60/144/300 agree", FrameRates),
    ("teleport rebase and finite state", Teleport),
    ("settings serialization and sanitization", Settings),
    ("ring orientation reconstructs its authored frame", RingFrame),
    ("friction dissipates ground sliding", Friction),
    ("sleep wakes when attachment moves", SleepWake),
    ("moving body contact survives side fall and pickup", MovingCapsule),
    ("a moving collider wakes sleeping clothing", ColliderWake),
    ("layer contact separates overlapping garments", Layers),
    ("local connection offsets survive save and rebind", Offsets),
    ("large cage remains stable", LargeCage),
    ("frame crosses reference axis continuously and preserves bone twist", ContinuousFrame),
    ("weighted contacts preserve dominant normal and surface", WeightedContacts),
    ("body fitting preserves visible bind pose and rigid transport", BindFit),
    ("internal branch braces do not collide with the torso", InternalBraces),
    ("depenetration does not launch particles above sliding limit", ContactSpeed),
    ("floor resampling along the same plane does not wake clothing", FloorResample),
};
var failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
return failures == 0 ? 0 : 1;

static AttachmentSimulation Garment(AttachmentSettings? settings = null)
{
    var sim = new AttachmentSimulation(settings ?? new AttachmentSettings());
    sim.AddRing("neck", new Vector3(0, 1.5f, 0), Quaternion.Identity, 0.13f, 1f);
    sim.AddRing("hem", new Vector3(0, 1.1f, 0), Quaternion.Identity, 0.16f, -1);
    sim.Connect(0, 1);
    return sim;
}
static void Run(AttachmentSimulation sim, float seconds, int fps = 60)
{
    for (var i = 0; i < (int)(seconds * fps); i++) sim.Advance(1f / fps);
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void ContinuousFrame()
{
    var transport = new AttachmentFrame();
    var old = transport.Update(Quaternion.Identity, Vector3.UnitX);
    for (var i = 0; i < 1200; i++)
    {
        var angle = MathF.Sin(i / 60f * MathF.Tau) * 0.012f;
        var direction = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        var q = transport.Update(Quaternion.Identity, direction);
        Assert(MathF.Abs(Quaternion.Dot(q, old)) > 0.9999f, "reference-axis crossing flips frame");
        Assert(Vector3.Distance(Vector3.Transform(Vector3.UnitY, q), direction) < 0.0001f, "axis lost endpoint");
        old = q;
    }
    var twist = Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitY, old), 0.7f);
    var twisted = transport.Update(twist, Vector3.Transform(Vector3.UnitY, old));
    Assert(MathF.Abs(Quaternion.Dot(twisted, twist * old)) > 0.99999f, "bone twist was discarded");
    var antiparallel = new AttachmentFrame().Update(Quaternion.Identity, -Vector3.UnitY);
    Assert(Vector3.Distance(Vector3.Transform(Vector3.UnitY, antiparallel), -Vector3.UnitY) < 0.0001f, "180-degree initialization invalid");
}
static void WeightedContacts()
{
    var contact = typeof(AttachmentSimulation).GetMethod("Contact", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var p = new AttachmentSimulation.Particle();
    contact.Invoke(null, new object[] { p, Vector3.UnitX, 0.0001f, Vector3.Zero, 0.2f });
    contact.Invoke(null, new object[] { p, -Vector3.UnitX, 0.1f, Vector3.UnitY, 0.7f });
    Assert(p.ContactNormal.X < -0.099f, "tiny first contact overrode dominant opposite contact");
    Assert(p.SurfaceVelocity == Vector3.UnitY && p.ContactFriction == 0.7f, "wrong dominant surface");
    contact.Invoke(null, new object[] { p, Vector3.UnitZ, 0.10005f, Vector3.UnitZ, 0.5f });
    Assert(p.SurfaceVelocity == Vector3.UnitZ, "maximum contact depth confused with cumulative depth");
}
static void BindFit()
{
    var settings = new AttachmentSettings { SlipDistance = 0, SelfCollision = false };
    var sim = new AttachmentSimulation(settings);
    var center = new Vector3(0.05f, 1, 0);
    var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.12f);
    sim.AddRing("asymmetric shoulder", center, q, 0.06f, 0);
    sim.Capsules.Add(new(new Vector3(0, 0.5f, 0), new Vector3(0, 1.5f, 0), 0.13f, Vector3.Zero));
    sim.FitToBody();
    Assert(Vector3.Distance(sim.DrivenCenter(0), center) < 0.00001f, "proxy fitting moved visible bone");
    var resolve = typeof(AttachmentSimulation).GetMethod("ResolveRotation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var fitted = (Quaternion)resolve.Invoke(sim, new object[] { 0 })!;
    Assert(MathF.Abs(Quaternion.Dot(fitted, q)) > 0.99999f, "proxy fitting rotated visible bone");
    foreach (var p in sim.Particles)
    {
        Assert(new Vector2(p.Position.X, p.Position.Z).Length() >= 0.142f - 0.00001f, "fitted node penetrates capsule");
        Assert(p.Velocity == Vector3.Zero, "fitting injected velocity");
    }
    var carry = Quaternion.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f);
    var destination = new Vector3(3, 2, 1);
    sim.SetTarget(0, destination, carry * q); sim.ResetToTargets();
    Assert(Vector3.Distance(sim.DrivenCenter(0), destination) < 0.00001f, "bind pivot did not follow rotation");
    var moved = (Quaternion)resolve.Invoke(sim, new object[] { 0 })!;
    Assert(MathF.Abs(Quaternion.Dot(moved, carry * q)) > 0.99999f, "fitted orientation does not transport rigidly");
}
static void InternalBraces()
{
    var sim = Garment();
    var edges = (System.Collections.IEnumerable)typeof(AttachmentSimulation).GetField("edges", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sim)!;
    var surface = 0; var bridges = 0;
    foreach (var edge in edges)
    {
        var type = edge.GetType();
        var a = (int)type.GetField("A")!.GetValue(edge)!; var b = (int)type.GetField("B")!.GetValue(edge)!;
        var collides = (bool)type.GetField("Surface")!.GetValue(edge)!;
        if (a / 6 != b / 6) { bridges++; Assert(!collides, "internal brace treated as cloth surface"); }
        if (collides) surface++;
    }
    Assert(bridges > 0 && surface == 12, "surface collision was removed rather than classified");
}
static void ContactSpeed()
{
    var sim = Garment(new AttachmentSettings { SpeedLimit = 0.2f });
    sim.Capsules.Add(new(new Vector3(0, 0.8f, 0), new Vector3(0, 1.6f, 0), 0.24f, Vector3.Zero));
    for (var i = 0; i < 300; i++)
    {
        sim.Advance(1f / 60);
        Assert(sim.Particles.All(p => p.Velocity.Length() <= 0.20001f), "contact bypassed relative speed limit");
    }
    Assert(sim.RecoveryCount == 0, "collision required numerical recovery");
}
static void FloorResample()
{
    var sim = Garment();
    for (var i = 0; i < sim.Rings.Count; i++) sim.SetFloor(i, true, Vector3.Zero, Vector3.UnitY);
    Run(sim, 10); Assert(sim.Sleeping, "fixture did not sleep");
    sim.SetFloor(0, true, new Vector3(0.2f, 0, 0.3f), Vector3.UnitY);
    Assert(sim.Sleeping, "same plane woke sleeping garment");
    sim.SetFloor(0, true, new Vector3(0.2f, 0.03f, 0.3f), Vector3.UnitY);
    Assert(!sim.Sleeping, "changed floor height did not wake garment");
}
static void Hanging()
{
    var sim = Garment(); Run(sim, 5);
    Assert(sim.Center(0).Y < 1.49f, "garment never slips");
    foreach (var p in sim.Particles.Where(p => p.Range >= 0))
        Assert(Vector3.Distance(p.Target, p.Position) < p.Range + 0.03f, "permanent tether broke");
    Assert(sim.Center(1).Y > 0.3f, "hem detached from opening");
    Assert(sim.RecoveryCount == 0, "solver required non-finite recovery");
}
static void SideFall()
{
    var sim = Garment(new AttachmentSettings { SlipDistance = 0.4f, Asymmetry = 0 });
    var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
    // A horizontal torso: sliding toward the feet would now be horizontal. Gravity must still sag down.
    sim.SetTarget(0, new Vector3(0, 1.5f, 0), rot);
    sim.SetTarget(1, new Vector3(0.4f, 1.5f, 0), rot);
    sim.ResetToTargets(); Run(sim, 4);
    Assert(sim.Center(0).Y < 1.4f, "no lateral sag on horizontal body");
    Assert(MathF.Abs(sim.Center(0).X) < 0.3f, "garment is being scripted along body axis");
}
static void Pickup()
{
    var sim = Garment(new AttachmentSettings { SlipDistance = 0.6f });
    sim.SetTarget(0, new Vector3(0, 0.7f, 0), Quaternion.Identity);
    sim.SetTarget(1, new Vector3(0, 0.3f, 0), Quaternion.Identity);
    sim.ResetToTargets();
    sim.SetFloor(0, true, Vector3.Zero, Vector3.UnitY);
    sim.SetFloor(1, true, Vector3.Zero, Vector3.UnitY);
    Run(sim, 4);
    Assert(sim.Particles.Min(p => p.Position.Y) >= 0.011f, "floor penetration");
    var before = sim.Center(1).Y;
    for (var f = 0; f < 180; f++)
    {
        var rise = (f + 1) / 180f * 1.3f;
        sim.SetTarget(0, new Vector3(0, 0.7f + rise, 0), Quaternion.Identity);
        sim.SetTarget(1, new Vector3(0, 0.3f + rise, 0), Quaternion.Identity);
        sim.Advance(1f / 60);
    }
    Assert(sim.Center(1).Y > before + 0.6f, "grounded garment failed to follow retained connection");
}
static void Pinned()
{
    var sim = Garment(new AttachmentSettings { SlipDistance = 0, Firmness = 1 }); Run(sim, 3);
    Assert(sim.Particles.Take(6).Max(p => Vector3.Distance(p.Position, p.Target)) < 0.01f, "zero slack drifts");
}
static void FrameRates()
{
    var simulations = new[] { Garment(), Garment(), Garment(), Garment() };
    Run(simulations[0], 3, 30); Run(simulations[1], 3, 60); Run(simulations[2], 3, 144);
    Run(simulations[3], 3, 300);
    for (var i = 0; i < simulations[0].Particles.Count; i++)
        Assert(Vector3.Distance(simulations[0].Particles[i].Position, simulations[2].Particles[i].Position) < 0.003f &&
               Vector3.Distance(simulations[1].Particles[i].Position, simulations[2].Particles[i].Position) < 0.003f,
            "render cadence changed physics");
    Assert(Vector3.Distance(simulations[0].Center(0), simulations[3].Center(0)) < 0.003f, "300fps advances too fast");
}
static void Teleport()
{
    var sim = Garment(); Run(sim, 1);
    sim.SetTarget(0, new Vector3(100, 1.5f, 0), Quaternion.Identity);
    sim.SetTarget(1, new Vector3(100, 1.1f, 0), Quaternion.Identity);
    sim.Advance(1f / 60); Run(sim, 1);
    Assert(sim.Center(0).X > 99 && sim.RecoveryCount == 0, "teleport caused solver instability");
}
static void Settings()
{
    var settings = new AttachmentSettings { Template = GarmentTemplate.Coat };
    settings.Anchors["j_sako_l"] = 0.2f;
    var loaded = JsonSerializer.Deserialize<AttachmentSettings>(JsonSerializer.Serialize(settings))!;
    Assert(loaded.Anchors["j_sako_l"] == 0.2f && loaded.Template == settings.Template, "lost tuning on reload");
    loaded.SlipDistance = float.NaN; loaded.SpeedLimit = float.PositiveInfinity;
    var valid = loaded.Validated();
    Assert(float.IsFinite(valid.SlipDistance) && float.IsFinite(valid.SpeedLimit), "non-finite configuration accepted");
    var copy = settings.Copy(); copy.Anchors["j_sako_l"] = 2;
    Assert(settings.Anchors["j_sako_l"] == 0.2f, "undo snapshot shares mutable anchors");
}
static void RingFrame()
{
    var sim = new AttachmentSimulation(new());
    var q = Quaternion.CreateFromYawPitchRoll(1, 0.7f, -0.8f);
    sim.AddRing("test", Vector3.One, q, 0.2f, 0);
    Assert(MathF.Abs(Quaternion.Dot(sim.Rotation(0), q)) > 0.999f, "orientation flips the authored frame");
}
static void Friction()
{
    AttachmentSimulation Sliding(float friction)
    {
        var sim = new AttachmentSimulation(new AttachmentSettings { GroundFriction = friction, Damping = 0, SpeedLimit = 4 });
        sim.AddRing("cloth", new Vector3(0, 0.012f, 0), Quaternion.Identity, 0.15f, -1);
        sim.SetFloor(0, true, Vector3.Zero, Vector3.UnitY);
        foreach (var p in sim.Particles) p.Velocity = Vector3.UnitX;
        Run(sim, 1); return sim;
    }
    Assert(Sliding(0.8f).Center(0).X < Sliding(0).Center(0).X * 0.5f, "friction does not slow sliding");
}
static void SleepWake()
{
    var sim = new AttachmentSimulation(new AttachmentSettings { SlipDistance = 0, Firmness = 1, Damping = 3 });
    sim.AddRing("opening", Vector3.UnitY, Quaternion.Identity, 0.1f, 0);
    Run(sim, 5);
    Assert(sim.Sleeping, "settled cage never sleeps");
    sim.SetTarget(0, new Vector3(0.1f, 1, 0), Quaternion.Identity);
    sim.Advance(1f / 60);
    Assert(!sim.Sleeping && sim.Center(0).X > 0.05f, "moving attachment did not wake cage");
}

static void MovingCapsule()
{
    var sim = Garment(new AttachmentSettings { SlipDistance = 0.3f, Asymmetry = 0, BodyFriction = 0.35f });
    sim.SetFloor(0, true, Vector3.Zero, Vector3.UnitY);
    sim.SetFloor(1, true, Vector3.Zero, Vector3.UnitY);
    var previous = new Vector3(0, 1.4f, 0);
    for (var f = 0; f < 600; f++)
    {
        var angle = Math.Clamp(f / 180f, 0, 1) * MathF.PI / 2;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
        var origin = new Vector3(MathF.Max(0, f - 360) / 240f * 0.8f,
            1.4f - Math.Clamp(f / 180f, 0, 1) * 1.2f + MathF.Max(0, f - 360) / 240f, 0);
        sim.SetTarget(0, origin + Vector3.Transform(new Vector3(0, 0.1f, 0), q), q);
        sim.SetTarget(1, origin + Vector3.Transform(new Vector3(0, -0.3f, 0), q), q);
        var axis = Vector3.Transform(Vector3.UnitY * 0.35f, q);
        sim.Capsules.Clear();
        sim.Capsules.Add(new AttachmentSimulation.Capsule(origin - axis, origin + axis, 0.08f, (origin - previous) * 60));
        sim.Advance(1f / 60); previous = origin;
        Assert(sim.Particles.All(p => p.Position.Y >= 0.01f), $"floor penetration at frame {f}");
        Assert(sim.RecoveryCount == 0, $"non-finite state at frame {f}");
    }
    Assert(sim.Center(0).Y > 0.8f, "garment did not lift with body");
    Assert(sim.Particles.Take(6).Max(p => Vector3.Distance(p.Target, p.Position) - p.Range) < 0.08f,
        "permanent connections overstretched after carry");
}

static void ColliderWake()
{
    var sim = new AttachmentSimulation(new AttachmentSettings { SlipDistance = 0, Firmness = 1 });
    sim.AddRing("opening", Vector3.UnitY, Quaternion.Identity, 0.1f, 0);
    sim.Capsules.Add(new AttachmentSimulation.Capsule(new Vector3(2, 0, 0), new Vector3(2, 1, 0), 0.02f, Vector3.Zero));
    Run(sim, 5); Assert(sim.Sleeping, "setup did not sleep");
    sim.Capsules[0] = new AttachmentSimulation.Capsule(new Vector3(1, 0, 0), new Vector3(1, 1, 0), 0.02f, Vector3.Zero);
    sim.Advance(1f / 60); Assert(!sim.Sleeping, "collider movement ignored");
}

static void Layers()
{
    var sim = new AttachmentSimulation(new AttachmentSettings { SlipDistance = 0.3f, SpeedLimit = 4 });
    sim.AddRing("opening", Vector3.UnitY, Quaternion.Identity, 0.1f, 1);
    var obstacle = new AttachmentSimulation.ContactPoint(new Vector3(0.1f, 1, 0), Vector3.Zero, 0.04f);
    sim.OtherGarments.Add(obstacle);
    sim.Advance(1f / 60);
    Assert(Vector3.Distance(sim.Particles[0].Position, obstacle.Position) >= 0.05f, "layer remained interpenetrating");
}

static void Offsets()
{
    var settings = new AttachmentSettings { SlipDistance = 0, Firmness = 1 };
    settings.AnchorOffsets["neck"] = new AttachmentOffset { X = 0.1f, Z = -0.03f };
    var loaded = JsonSerializer.Deserialize<AttachmentSettings>(JsonSerializer.Serialize(settings))!;
    var sim = new AttachmentSimulation(loaded);
    sim.AddRing("neck", Vector3.UnitY, Quaternion.Identity, 0.1f, 0);
    Run(sim, 2);
    Assert(MathF.Abs(sim.Center(0).X - 0.1f) < 0.01f && MathF.Abs(sim.Center(0).Z + 0.03f) < 0.01f,
        "authored attachment offset lost");
    var copy = loaded.Copy(); copy.AnchorOffsets["neck"].X = 0.2f;
    Assert(loaded.AnchorOffsets["neck"].X == 0.1f, "offset undo shares objects");
}

static void LargeCage()
{
    var sim = new AttachmentSimulation(new AttachmentSettings { Template = GarmentTemplate.Coat });
    for (var i = 0; i < 24; i++)
    {
        sim.AddRing($"segment{i}", new Vector3(0, 2f - i * 0.055f, 0), Quaternion.Identity, 0.13f, i == 0 ? 0.6f : -1);
        if (i != 0) sim.Connect(i - 1, i);
        sim.SetFloor(i, true, Vector3.Zero, Vector3.UnitY);
    }
    sim.Capsules.Add(new AttachmentSimulation.Capsule(new Vector3(0, 0.7f, 0), new Vector3(0, 2.1f, 0), 0.08f, Vector3.Zero));
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Run(sim, 2);
    timer.Stop();
    Console.WriteLine($"  144-node cage: {timer.Elapsed.TotalMilliseconds / 120:F2} ms/render frame (60 FPS workload)");
    Assert(sim.RecoveryCount == 0 && sim.Particles.All(p => float.IsFinite(p.Position.Y)), "large cage unstable");
    Assert(sim.Particles.Take(6).Max(p => Vector3.Distance(p.Position, p.Target) - p.Range) < 0.04f,
        "heavy garment overpowered its connections");
}
