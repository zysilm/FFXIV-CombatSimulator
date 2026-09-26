using System.Numerics;
using CombatSimulator.Animation.Attachment;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static RetainedSkirtPanel Panel() => new(new[] {
    new RetainedSkirtPanel.Segment(Vector3.Zero, new Vector3(0, -.3f, 0)),
    new RetainedSkirtPanel.Segment(new Vector3(0, -.3f, 0), new Vector3(0, -.6f, 0)) }, Vector3.UnitZ, .025f);
static void Step(RetainedSkirtPanel panel, Vector3 gravity, RetainedSkirtPanel.Capsule[] caps, int frames = 1200, int fps = 60)
{
    for (var i = 0; i < frames; i++) panel.Advance(1f / fps, gravity, caps, 1.5f, .006f, 24, 1.5f);
}
var leg = new[] { new RetainedSkirtPanel.Capsule(new Vector3(.03f, -.15f, 0), new Vector3(.03f, -.65f, 0), .075f) };
var cases = new (string, Action)[]
{
    ("both clothing slots retain three metre slip settings", () => {
        foreach (var template in new[] { GarmentTemplate.Top, GarmentTemplate.Trousers })
        {
            var s = new AttachmentSettings { Template = template, SlipDistance = 3 };
            var saved = System.Text.Json.JsonSerializer.Deserialize<AttachmentSettings>(System.Text.Json.JsonSerializer.Serialize(s))!.Validated();
            Check(saved.SlipDistance == 3, "three metre distance truncated");
            s.SlipDistance = 4; Check(s.Validated().SlipDistance == 3, "distance range exceeded");
        }
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AttachmentSettings>("{\"BodyFitEnabled\":true,\"HipHalfWidth\":0.4,\"SlipDistance\":2.5}")!;
        Check(legacy.Validated().SlipDistance == 2.5f && !System.Text.Json.JsonSerializer.Serialize(legacy).Contains("BodyFit"), "removed settings broke migration");
    }),
    ("live skirt limit clamps existing contact without rebind", () => {
        var p = Panel(); Step(p, -Vector3.UnitY * 9.81f, leg);
        Check(p.Angle > .1f, "initial contact inactive");
        p.Advance(1f / 60, -Vector3.UnitY * 9.81f, leg, 0, .006f, 24, 1.5f);
        Check(p.Angle == 0 && p.ResidualPenetration > 0, "zero limit ignored");
        Step(p, -Vector3.UnitY * 9.81f, leg);
        Check(p.Angle > .1f && p.ResidualPenetration < .0003f, "restored limit ignored");
    }),
    ("floor contact opens the skirt without moving the seam", () => {
        var p = Panel(); p.Ground = new Plane(Vector3.UnitY, .3f);
        Step(p, -Vector3.UnitY * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>());
        Check(p.Angle > 1 && p.ResidualPenetration < .0003f, "floor contact unresolved");
        var angle = p.Angle;
        Step(p, -Vector3.UnitY * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>());
        Check(MathF.Abs(p.Angle - angle) < 1e-5f, "floor contact oscillates");
    }),
    ("conflicting floor and leg contacts settle with bounded residual", () => {
        var p = Panel(); p.Ground = new Plane(Vector3.UnitY, .05f);
        Step(p, -Vector3.UnitY * 9.81f, leg);
        var angle = p.Angle;
        Step(p, -Vector3.UnitY * 9.81f, leg);
        Check(float.IsFinite(p.Angle) && MathF.Abs(p.Angle - angle) < 1e-5f && p.Angle <= 1.5f, "conflicting floor contact oscillates");
    }),
    ("skirt uses gravity torque, not common translation", () => {
        var p = Panel(); Step(p, Vector3.UnitX * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>());
        Check(p.Angle > .1f && p.Angle <= 1.5f, "gravity did not turn the panel");
        Check(Vector3.Transform(Vector3.Zero, p.Rotation) == Vector3.Zero, "waist pivot moved");
        Check(MathF.Abs(Vector3.Transform(new Vector3(0, -.6f, 0), p.Rotation).Length() - .6f) < 1e-6f, "panel length changed");
    }),
    ("leg contact clears the entire sampled panel strips", () => {
        var p = Panel(); Step(p, -Vector3.UnitY * 9.81f, leg);
        Check(p.Angle > .1f && p.ResidualPenetration < .0003f, $"unresolved leg penetration {p.ResidualPenetration}");
        Check(p.AngularVelocity == 0, "collision injected a velocity");
    }),
    ("static leg contact does not keep oscillating", () => {
        var p = Panel(); Step(p, -Vector3.UnitY * 9.81f, leg);
        var angle = p.Angle; var jitter = 0f;
        for (var i = 0; i < 1800; i++)
        {
            p.Advance(1f / 60, -Vector3.UnitY * 9.81f, leg, 1.5f, .006f, 24, 1.5f);
            jitter = MathF.Max(jitter, MathF.Abs(p.Angle - angle)); angle = p.Angle;
        }
        Check(jitter < .00001f, $"contact angular jitter {jitter}");
    }),
    ("submillimetre leg noise does not chatter", () => {
        var p = Panel(); Step(p, -Vector3.UnitY * 9.81f, leg);
        var angle = p.Angle;
        for (var i = 0; i < 1000; i++)
        {
            var delta = Vector3.UnitX * (MathF.Sin(i) * .0001f);
            p.Advance(1f / 60, -Vector3.UnitY * 9.81f,
                new[] { new RetainedSkirtPanel.Capsule(leg[0].A + delta, leg[0].B + delta, leg[0].Radius) }, 1.5f, .006f, 24, 1.5f);
            Check(MathF.Abs(p.Angle - angle) < .00001f, "noise generated contact chatter");
        }
    }),
    ("moving leg pushes the skirt and releases without impulse", () => {
        var p = Panel(); Step(p, -Vector3.UnitY * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>());
        p.Advance(1f / 60, -Vector3.UnitY * 9.81f, leg, 1.5f, .006f, 24, 1.5f);
        var angle = p.Angle;
        Check(angle > .1f && p.AngularVelocity == 0, "moving leg launched the panel");
        p.Advance(1f / 60, -Vector3.UnitY * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>(), 1.5f, .006f, 24, 1.5f);
        Check(MathF.Abs(p.Angle - angle) <= .026f, "release snapped skirt closed");
        Step(p, -Vector3.UnitY * 9.81f, Array.Empty<RetainedSkirtPanel.Capsule>());
        Check(p.Angle < .001f, "released skirt did not settle");
    }),
    ("impossible collision remains bounded and reports residual", () => {
        var p = Panel(); var cap = new[] { new RetainedSkirtPanel.Capsule(Vector3.Zero, Vector3.Zero, 2) };
        Step(p, Vector3.UnitX * 9.81f, cap);
        var angle = p.Angle;
        Step(p, Vector3.UnitX * 9.81f, cap);
        Check(float.IsFinite(p.Angle) && p.Angle <= 1.5f && MathF.Abs(p.Angle - angle) < 1e-6f && p.ResidualPenetration > 1, "conflicting contacts unstable or hidden");
    }),
    ("two legs can obstruct larger opening angles too", () => {
        var caps = new[] { leg[0], new RetainedSkirtPanel.Capsule(new Vector3(.58f, -.1f, 0), new Vector3(.58f, -.35f, 0), .055f) };
        var p = Panel(); Step(p, Vector3.UnitX * 9.81f, caps);
        Check(p.ResidualPenetration <= .0003f, $"second leg penetrated after opening {p.ResidualPenetration}");
    }),
    ("30/60/144/300 FPS settle to same skirt pose", () => {
        var angles = new List<float>();
        foreach (var fps in new[] { 30, 60, 144, 300 })
        { var p = Panel(); Step(p, Vector3.UnitX * 9.81f, leg, fps * 20, fps); angles.Add(p.Angle); }
        Check(angles.Max() - angles.Min() < .001f, "frame-rate dependent contact equilibrium");
    }),
    ("pose cycling keeps seam and segment lengths intact", () => {
        var p = Panel();
        for (var i = 0; i < 4000; i++)
        {
            var body = Quaternion.CreateFromYawPitchRoll(i * .003f, MathF.Sin(i * .004f) * 2, MathF.Sin(i * .009f) * 2);
            p.Advance(1f / 60, Vector3.Transform(-Vector3.UnitY * 9.81f, Quaternion.Inverse(body)), leg, 1.5f, .006f, 24, 1.5f);
            var world = body * p.Rotation;
            Check(MathF.Abs(Vector3.Transform(new Vector3(0, -.6f, 0), world).Length() - .6f) < 1e-5f, "pose accumulated stretch");
            Check(p.Angle >= 0 && p.Angle <= 1.5f && float.IsFinite(p.AngularVelocity), "pose cycling unstable");
        }
    }),
    ("trouser distance controls visible waist travel", () => {
        float Travel(float distance) {
            var s = new AttachmentSettings { SlipDistance = distance, Template = GarmentTemplate.Trousers };
            var m = new AttachedGarmentMotion();
            for (var i = 0; i < 600; i++) m.AdvanceTrousers(1f / 60, Quaternion.Identity, s);
            return m.TrouserOffset("j_kosi", -Vector3.UnitY, .5f, 1, 1, s).Length();
        }
        Check(Travel(0) == 0, "zero distance moved");
        Check(MathF.Abs(Travel(.05f) - .05f) < .001f, "small distance ignored");
        Check(Travel(.2f) > .19f && Travel(1.5f) <= .22501f, "waist locked or anatomical cap ignored");
    }),
    ("bent legs follow separate paths and inversion retains progress", () => {
        var s = new AttachmentSettings { SlipDistance = .3f, Template = GarmentTemplate.Trousers };
        var m = new AttachedGarmentMotion();
        for (var i = 0; i < 600; i++) m.AdvanceTrousers(1f / 60, Quaternion.Identity, s);
        var before = m.LocalSlip;
        for (var i = 0; i < 600; i++) m.AdvanceTrousers(1f / 60, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI), s);
        Check(Vector3.Distance(before, m.LocalSlip) < 1e-5f, "inversion reset slide");
        var thigh = m.TrouserOffset("j_asi_a_l", Vector3.UnitX, .5f, 1, 1, s);
        var knee = m.TrouserOffset("j_asi_b_l", -Vector3.UnitY, .4f, 1, 1, s);
        Check(thigh.X > .2f && thigh.Y == 0 && knee.Y < -.1f && knee.X == 0, "wrong segment direction");
        Check(m.TrouserOffset("j_kosi", Vector3.Zero, 0, 1, 1, s) == Vector3.Zero, "degenerate path moved");
    }),
    ("trouser cuffs and transverse position are retained", () => {
        var s = new AttachmentSettings { SlipDistance = 1.5f, Template = GarmentTemplate.Trousers };
        var m = new AttachedGarmentMotion();
        for (var i = 0; i < 600; i++) m.AdvanceTrousers(1f / 60, Quaternion.Identity, s);
        foreach (var direction in new[] { Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, Vector3.Normalize(new Vector3(1, 2, 3)) })
        {
            foreach (var pinned in new[] { "j_asi_d_l", "j_sk_a_l" })
                Check(m.TrouserOffset(pinned, direction, .4f, 1, 2, s) == Vector3.Zero, "waist/cuff/skirt translated");
            var offset = m.TrouserOffset("j_asi_b_l", direction, .4f, 1, 2, s);
            Check(Vector3.Cross(offset, direction).Length() < 1e-6f && offset.Length() <= .260001f && Vector3.Dot(offset, direction) >= 0, "pants moved across leg or exceeded anatomical cap");
        }
    }),
};
var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;
