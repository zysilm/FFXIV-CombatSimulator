using System.Numerics;
using System.Text.Json;
using CombatSimulator.Animation.Attachment;

static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
static AttachedGarmentMotion Run(AttachmentSettings settings, Quaternion frame, int fps = 60, float seconds = 10)
{
    var motion = new AttachedGarmentMotion();
    for (var i = 0; i < fps * seconds; i++) motion.Advance(1f / fps, frame, settings);
    return motion;
}
var settings = new AttachmentSettings().Validated();
var sideways = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
var tests = new (string, Action)[]
{
    ("upper clothing never slides toward neck during falls and recovery", () => {
        var m = new AttachedGarmentMotion();
        var previous = 0f;
        for (var i = 0; i < 3600; i++)
        {
            var frame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Sin(i * .01f) * MathF.PI);
            m.Advance(1f / 60, frame, settings, upperBody: true);
            Check(m.LocalSlip.Y <= 0 && -m.LocalSlip.Y >= previous - 1e-7f, "upper slide reversed toward neck");
            previous = -m.LocalSlip.Y;
            Check(previous <= .080001f && m.LocalSlip.X == 0 && m.LocalSlip.Z == 0, "upper top translated sideways or exceeded retained limit");
        }
    }),
    ("inverted upper clothing does not start sliding toward head", () => {
        var m = new AttachedGarmentMotion();
        var inverted = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        for (var i = 0; i < 600; i++) m.Advance(1f / 60, inverted, settings, upperBody: true);
        Check(m.LocalSlip == Vector3.Zero, "upward gravity moved retained top");
    }),
    ("shoulders collar and sleeves stay pinned despite legacy multipliers", () => {
        var m = new AttachedGarmentMotion();
        for (var i = 0; i < 600; i++) m.Advance(1f / 60, Quaternion.Identity, settings, upperBody: true);
        var raw = m.Offset(Quaternion.Identity, 1, 2, false, settings, upperBody: true);
        foreach (var bone in new[] { "j_kubi", "j_sako_l", "j_sako_r", "j_ude_a_l", "j_ude_b_r", "j_te_l", "j_te_r" })
            Check(raw * AttachedGarmentMotion.UpperBodyWeight(bone) == Vector3.Zero, $"unretained opening: {bone}");
        Check(raw.Length() <= .080001f, "legacy multiplier bypassed upper travel cap");
        Check(AttachedGarmentMotion.UpperBodyWeight("j_sebo_c") < AttachedGarmentMotion.UpperBodyWeight("j_sebo_b") &&
            AttachedGarmentMotion.UpperBodyWeight("j_sebo_b") < AttachedGarmentMotion.UpperBodyWeight("j_sebo_a") &&
            AttachedGarmentMotion.UpperBodyWeight("j_sebo_a") < AttachedGarmentMotion.UpperBodyWeight("j_kosi"), "no graded torso retention");
    }),
    ("upper profile respects smaller distance and zero slack", () => {
        foreach (var distance in new[] { 0f, .01f, .04f, 1.5f })
        {
            var s = settings.Copy(); s.SlipDistance = distance;
            var m = new AttachedGarmentMotion();
            for (var i = 0; i < 600; i++) m.Advance(1f / 60, Quaternion.Identity, s, upperBody: true);
            Check(m.Offset(Quaternion.Identity, 1, 2, true, s, upperBody: true).Length() <= MathF.Min(distance, .08f) + 1e-6f, "upper distance ignored");
        }
    }),
    ("upper slide rate is frame-rate independent before hitting bound", () => {
        var s = settings.Copy(); s.SpeedLimit = .05f;
        var values = new List<float>();
        foreach (var fps in new[] { 30, 60, 144, 300 })
        {
            var m = new AttachedGarmentMotion();
            for (var i = 0; i < fps; i++) m.Advance(1f / fps, Quaternion.Identity, s, upperBody: true);
            values.Add(m.LocalSlip.Y);
        }
        Check(values.Max() - values.Min() < 1e-6f, "upper slide frame-rate dependence");
    }),
    ("standing slides downward and stays bounded", () => {
        var m = Run(settings, Quaternion.Identity);
        Check(m.LocalSlip.Y < -.3f && m.LocalSlip.Length() <= settings.SlipDistance + 1e-6f, "sliding/range incorrect");
    }),
    ("side fall follows world gravity with tight lateral limit", () => {
        var m = Run(settings, sideways);
        var offset = m.Offset(sideways, 1, 1, false, settings);
        Check(offset.Y < -.02f && offset.Length() <= settings.LateralDistance + 1e-6f, "side fall departed from body or did not follow gravity");
    }),
    ("zero slack has zero drift after moving poses", () => {
        var s = new AttachmentSettings { SlipDistance = 0 };
        var m = new AttachedGarmentMotion();
        for (var i = 0; i < 10000; i++) m.Advance(1f / 60, Quaternion.CreateFromYawPitchRoll(i * .1f, i * .2f, i * .3f), s);
        Check(m.Offset(sideways, 1, 1, true, s) == Vector3.Zero, "zero-slack drift");
    }),
    ("stationary skirt has no sustained jitter", () => {
        var m = Run(settings, sideways, seconds: 30);
        var before = m.Offset(sideways, 1, .65f, true, settings);
        for (var i = 0; i < 3600; i++) m.Advance(1f / 60, sideways, settings);
        Check(Vector3.Distance(before, m.Offset(sideways, 1, .65f, true, settings)) < .000001f, "static jitter/drift");
    }),
    ("30/60/144/300 FPS agree before reaching bound", () => {
        var s = new AttachmentSettings { SlipDistance = 1.5f, BodyFriction = 0, SpeedLimit = .2f };
        var reference = Run(s, Quaternion.Identity, 60, 1).LocalSlip;
        foreach (var fps in new[] { 30, 144, 300 })
            Check(Vector3.Distance(reference, Run(s, Quaternion.Identity, fps, 1).LocalSlip) < .001f, $"frame-rate mismatch {fps}");
    }),
    ("pose cycling cannot accumulate distance", () => {
        var m = new AttachedGarmentMotion();
        for (var i = 0; i < 20000; i++)
        {
            var frame = Quaternion.CreateFromYawPitchRoll(i * .03f, MathF.Sin(i * .02f), MathF.Sin(i * .01f) * 2);
            m.Advance(1f / 60, frame, settings);
            var offset = m.Offset(frame, 1, .65f, true, settings);
            Check(float.IsFinite(offset.LengthSquared()) && offset.Length() <= settings.SlipDistance * .65f + 1e-6f, "pose cycling accumulated error");
        }
    }),
    ("body carrying and teleport do not leave world-space lag", () => {
        var m = Run(settings, Quaternion.Identity);
        var offset = m.Offset(sideways, 1, 1, false, settings);
        var source = new Vector3(1000, 50, -1000);
        var rendered = source + offset;
        Check(Vector3.Distance(rendered, source) < .351f, "teleport lag");
        Check(Vector3.Distance(offset, Vector3.Transform(m.LocalSlip, sideways)) < 1e-6f, "offset was not carried by live body frame");
    }),
    ("skirt columns receive coherent offsets without chain accumulation", () => {
        var m = Run(settings, sideways);
        var a = new Vector3(-.1f, 1, 0); var b = new Vector3(.1f, .4f, .1f);
        var delta = m.Offset(sideways, 1, .65f, true, settings);
        Check(Vector3.Distance((a + delta) - (b + delta), a - b) < 1e-6f, "shared skirt offset distorted segment");
        Check(m.Offset(sideways, 1, 0, true, settings) == Vector3.Zero, "zero multiplier not pinned");
    }),
    ("sway is bounded and can be disabled", () => {
        var m = Run(settings, sideways);
        Check(m.LocalSway.Length() <= settings.SwayDistance + 1e-6f, "unbounded sway");
        var s = new AttachmentSettings { SwayDistance = 0 };
        Check(Run(s, sideways).LocalSway == Vector3.Zero, "disabled sway active");
        s.Template = GarmentTemplate.Rigid; s.SwayDistance = .02f;
        Check(Run(s, sideways).LocalSway == Vector3.Zero, "rigid garment sways");
    }),
    ("legacy collision settings do not affect enhanced motion", () => {
        var other = settings.Copy(); other.SelfCollision = false; other.LayerCollision = false;
        other.Thickness = .04f; other.GroundFriction = 2; other.OpeningScale = 1.8f;
        Check(Run(settings, sideways).LocalSlip == Run(other, sideways).LocalSlip, "old collision settings still active");
    }),
    ("new settings survive serialization and sanitize invalid values", () => {
        var s = JsonSerializer.Deserialize<AttachmentSettings>(JsonSerializer.Serialize(new AttachmentSettings { SwayDistance = .006f, LateralDistance = .015f }))!.Validated();
        Check(s.SwayDistance == .006f && s.LateralDistance == .015f, "settings did not round trip");
        s.SwayDistance = float.NaN; s.LateralDistance = -1;
        s = s.Validated(); Check(s.SwayDistance == .008f && s.LateralDistance == 0, "bad settings not sanitized");
    }),
};
foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
