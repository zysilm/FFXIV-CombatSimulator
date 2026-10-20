using System.Numerics;
using System.Diagnostics;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;

int assertions = 0;
void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
var rest = new[] { Vector3.Zero, new Vector3(.08f, 0, 0), new Vector3(.14f, -.05f, 0), new Vector3(.18f, -.12f, .02f) };
var parents = new[] { -1, 0, 1, 2 };
FlexibleHairSolver Make() => new(parents, rest, .02f, new(0, 1, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero);
void Step(FlexibleHairSolver solver, Vector3 head, Quaternion rotation, HairContactPlane[]? planes = null, HairContactCapsule[]? body = null) =>
    solver.Step(1f / 60, head, rotation, .02f, 2.2f, planes ?? [], body ?? [], .005f);
var hair = Make();
for (int frame = 0; frame < 1200; frame++)
{
    Step(hair, new(0, 1, 0), Quaternion.Identity);
    Check(Vector3.Distance(hair.Positions[0], new(0, 1, 0)) < 1e-6f, "Scalp attachment moved");
    for (int i = 1; i < hair.Count; i++)
    {
        Check(FlexibleHairSolver.Finite(hair.Positions[i]), "Nonfinite guide");
        Check(MathF.Abs(Vector3.Distance(hair.Positions[i], hair.Positions[i - 1]) - Vector3.Distance(rest[i], rest[i - 1])) < .008f, "Guide stretching");
    }
}
Check(hair.Positions[1].X > .015f, "Persistent hairstyle constraint faded away");
var settledTip = hair.Positions[^1];
for (int i = 0; i < 240; i++) Step(hair, new(0, 1, 0), Quaternion.Identity);
Check(Vector3.Distance(settledTip, hair.Positions[^1]) < .003f, "Settled hair drifted");
Check(!hair.Awake, "Hair never settles");
Step(hair, new(.08f, 1, 0), Quaternion.Identity);
Check(hair.Awake, "Head motion failed to wake independent hair");
Check(Vector3.Distance(hair.Positions[^1], settledTip + new Vector3(.08f, 0, 0)) > .002f, "Hair rigidly follows head; no inertia");
for (int frame = 0; frame < 360; frame++)
{
    var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Sin(frame * .03f) * 1.2f);
    Step(hair, new(.08f, 1, 0), rotation);
    Check(hair.Positions.All(FlexibleHairSolver.Finite), "Rotating head diverged");
}
Step(hair, new(10, 1, 0), Quaternion.Identity);
Check(hair.Positions.All(p => Vector3.Distance(p, new Vector3(10, 1, 0)) < 1), "Teleport injected runaway impulse");
var groundHair = Make();
var ground = rest.Select(p => new HairContactPlane(true, new(p.X, .94f, p.Z), Vector3.UnitY)).ToArray();
for (int i = 0; i < 600; i++) Step(groundHair, new(0, 1, 0), Quaternion.Identity, ground);
Check(groundHair.Positions.Skip(1).All(p => p.Y >= .9449f), "Ground penetration");
var contactHair = Make();
var body = new[] { new HairContactCapsule(new(.12f, .9f, 0), new(.12f, 1, 0), .03f) };
for (int i = 0; i < 180; i++) Step(contactHair, new(0, 1, 0), Quaternion.Identity, body: body);
Check(contactHair.Positions.Skip(1).All(p => Vector3.Distance(p, body[0].Nearest(p)) >= .0349f), "Body penetration");
// Production Step must remain allocation-free after construction/JIT warmup.
var perfHair = Make();
for (int i = 0; i < 100; i++) Step(perfHair, new(0, 1, 0), Quaternion.Identity);
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 600; i++) Step(perfHair, new(MathF.Sin(i * .03f) * .03f, 1, 0), Quaternion.Identity);
Check(GC.GetAllocatedBytesForCurrentThread() == before, "Per-step managed allocation");
bool rejected = false;
try { _ = new FlexibleHairSolver([1, 0], [Vector3.Zero, Vector3.UnitY], .02f, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.Zero); }
catch (ArgumentException) { rejected = true; }
Check(rejected, "Invalid/cyclic tree accepted");
var braidCloud = Enumerable.Range(0, 128).Select(i => new Vector3(.005f * MathF.Sin(i), -i * .006f, .004f * MathF.Cos(i))).ToArray();
var braid = HairMeshEnvelope.Fit(braidCloud, Vector3.Zero, -Vector3.UnitY);
Check(braid.Tip.Y < -.7f && MathF.Abs(braid.Tip.X) < .03f, "Mesh braid replaced by a short guessed bone stub");
Check(braid.Radius >= .004f && braid.Radius < .015f, "Mesh braid width is incorrect");
// A broad, thin fringe begins above its cloud. Its width must not turn its
// physical guide sideways or inflate its head-contact shell.
var fringeCloud = Enumerable.Range(0, 256).Select(i => new Vector3(
    .003f + ((i % 16) / 15f - .5f) * .18f, -.01f - (i / 16) / 15f * .06f,
    (i % 3 - 1) * .001f)).ToArray();
var fringe = HairMeshEnvelope.Fit(fringeCloud, Vector3.Zero, -Vector3.UnitY);
Check(fringe.Tip.Y < -.06f && MathF.Abs(fringe.Tip.X) < .01f, "Fringe width became a sideways guide");
Check(fringe.Radius < .006f, "Thin fringe width became a thick collision sphere");
var tilt = Quaternion.CreateFromYawPitchRoll(.8f, .4f, -.7f);
var tiltedFringe = HairMeshEnvelope.Fit(fringeCloud.Select(p => Vector3.Transform(p, tilt)).ToArray(),
    Vector3.Zero, Vector3.Transform(-Vector3.UnitY, tilt));
Check(Vector3.Distance(tiltedFringe.Tip, Vector3.Transform(fringe.Tip, tilt)) < .001f,
    "Rooted fringe fitting depends on world axes");
Check(MathF.Abs(tiltedFringe.Radius - fringe.Radius) < .001f, "Fringe thickness depends on world axes");
var blendedSkin = new Dictionary<string, Matrix4x4>
{
    ["scalp"] = Matrix4x4.Identity,
    ["side"] = Matrix4x4.CreateTranslation(.08f, 0, 0),
};
var blendedSample = new HairMeshSample(new(0, -.04f, 0), [new("scalp", .4f), new("side", .6f)]);
Check(blendedSample.TryPose(blendedSkin, out var blend) && Vector3.Distance(blend, new(.048f, -.04f, 0)) < 1e-6f,
    "Scalp/hair blended card was fitted as attached to its dominant bone");
var missingSample = new HairMeshSample(Vector3.Zero, [new("unresolved", .4f), new("side", .6f)]);
Check(!missingSample.TryPose(blendedSkin, out _), "Unresolved skin influences silently distort guide fitting");
// A chord between two valid surface points may lie inside the approximate
// scalp. Edge contacts must preserve the rest profile just like point contacts.
var attachedFringe = new FlexibleHairSolver([-1, 0],
    [new(-.06f, .08f, 0), new(.06f, .08f, 0)], .02f,
    Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.Zero);
attachedFringe.ContactRadii[1] = .02f;
var faceEnvelope = new HairContactCapsule(Vector3.Zero, Vector3.Zero, .1f, true);
float fringeLengthError = 0;
for (int i = 0; i < 600; i++)
{
    Step(attachedFringe, Vector3.Zero, Quaternion.Identity, body: [faceEnvelope]);
    fringeLengthError = MathF.Max(fringeLengthError,
        MathF.Abs(Vector3.Distance(attachedFringe.Positions[0], attachedFringe.Positions[1]) - .12f));
}
Check(fringeLengthError < .001f, "Scalp edge contacts fight the rest attachment and stretch fringe");
Check(attachedFringe.Positions[1].Y <= .081f, "Scalp edge contacts lift the entire fringe away from its rest surface");
var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
for (int i = 0; i < 180; i++) Step(attachedFringe, Vector3.Zero, turned, body: [faceEnvelope]);
Check(attachedFringe.Positions.All(FlexibleHairSolver.Finite), "Turning attached fringe diverges");
var crossed = new FlexibleHairSolver([-1, 0], [new(-.2f, 0, 0), new(.2f, 0, 0)], .02f,
    new(0, 1, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero);
var torso = new HairContactCapsule(new(0, .9f, 0), new(0, 1.1f, 0), .03f);
for (int i = 0; i < 240; i++) Step(crossed, new(0, 1, 0), Quaternion.Identity, body: [torso]);
float closest = torso.ClosestGuideParameter(crossed.Positions[0], crossed.Positions[1]);
var closestPoint = Vector3.Lerp(crossed.Positions[0], crossed.Positions[1], closest);
Check(Vector3.Distance(closestPoint, torso.Nearest(closestPoint)) >= .034f, "A long guide segment crosses the body despite clear endpoints");
// A fluffy tuft's approximate centerline may begin inside the head envelope.
// It must not fight its fixed scalp attachment or keep bouncing at rest.
var grazing = new FlexibleHairSolver([-1, 0], [new(.08f, .05f, 0), new(.09f, -.08f, 0)], .02f,
    new(0, 1, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero);
grazing.ContactRadii[1] = .06f;
var scalp = new HairContactCapsule(new(0, 1, 0), new(0, 1.04f, 0), .08f, true);
float maximumGrazingMotion = 0, maximumGrazingLengthError = 0;
for (int i = 0; i < 1200; i++)
{
    var old = grazing.Positions[1];
    Step(grazing, new(0, 1, 0), Quaternion.Identity, body: [scalp]);
    if (i >= 900)
    {
        maximumGrazingMotion = MathF.Max(maximumGrazingMotion, Vector3.Distance(old, grazing.Positions[1]));
        maximumGrazingLengthError = MathF.Max(maximumGrazingLengthError, MathF.Abs(Vector3.Distance(grazing.Positions[0], grazing.Positions[1]) - Vector3.Distance(grazing.Rest[0], grazing.Rest[1])));
    }
}
Console.WriteLine($"Scalp-contact regression: late motion={maximumGrazingMotion * 1000:F3}mm/tick, length error={maximumGrazingLengthError * 1000:F3}mm.");
Check(maximumGrazingMotion < .001f, "Static scalp contact keeps oscillating");
var nearRoot = new FlexibleHairSolver([-1, 0], [Vector3.Zero, new(.2f, 0, 0)], .02f,
    new(0, 1, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero);
var rootObstacle = new HairContactCapsule(new(.015f, .9f, 0), new(.015f, 1.1f, 0), .01f);
float nearRootPeak = 0, nearRootLengthError = 0;
for (int i = 0; i < 180; i++)
{
    var old = nearRoot.Positions[1]; Step(nearRoot, new(0, 1, 0), Quaternion.Identity, body: [rootObstacle]);
    nearRootPeak = MathF.Max(nearRootPeak, Vector3.Distance(old, nearRoot.Positions[1]));
    nearRootLengthError = MathF.Max(nearRootLengthError, MathF.Abs(Vector3.Distance(nearRoot.Positions[0], nearRoot.Positions[1]) - .2f));
}
Console.WriteLine($"Near-root contact regression: peak motion={nearRootPeak * 1000:F3}mm/tick, peak length error={nearRootLengthError * 1000:F3}mm.");
Check(nearRootPeak < .04f, "Near-root contact amplifies endpoint movement");
Check(nearRootLengthError < .002f, "Near-root contact visibly stretches the guide");
// Exercise the maximum admitted tree: 256 roots with 256 virtual tips.
var largeParents = new int[512]; var largeRest = new Vector3[512];
for (int i = 0; i < 256; i++)
{
    largeParents[i] = -1; largeRest[i] = new((i % 16) * .004f, 0, (i / 16) * .004f);
    largeParents[i + 256] = i; largeRest[i + 256] = largeRest[i] + new Vector3(.08f, -.08f, 0);
}
var large = new FlexibleHairSolver(largeParents, largeRest, .02f, new(0, 1, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero);
var envelopes = Enumerable.Range(0, 12).Select(i => new HairContactCapsule(new(i * .05f, .5f, -.1f), new(i * .05f, .7f, -.1f), .04f)).ToArray();
for (int i = 0; i < 60; i++) Step(large, new(0, 1, 0), Quaternion.Identity, body: envelopes);
var stopwatch = new Stopwatch();
for (int i = 0; i < 60; i++) Step(large, new(MathF.Sin(i * .05f) * .01f, 1, 0), Quaternion.Identity, body: envelopes);
before = GC.GetAllocatedBytesForCurrentThread();
stopwatch.Start();
for (int i = 0; i < 300; i++) Step(large, new(MathF.Sin(i * .05f) * .01f, 1, 0), Quaternion.Identity, body: envelopes);
stopwatch.Stop();
long allocations = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocations == 0, $"Worst-size solver allocates per-step memory: {allocations} bytes");
Check(large.Positions.All(FlexibleHairSolver.Finite), "Maximum-size guide tree diverged");
Console.WriteLine($"Maximum-size solver: 512 points, 12 body envelopes, {stopwatch.Elapsed.TotalMilliseconds / 300:F3} ms/step (offline CPU only; excludes native terrain and pose writeback).");
// A sideways mesh face must collide independently of terrain and capsule envelopes.
var wallHair = Make();
var wallPlanes = rest.Select(p => new HairContactPlane(true, new(.1f, 1 + p.Y, p.Z), -Vector3.UnitX)).ToArray();
for (int i = 0; i < 600; i++)
    wallHair.Step(1f / 60, new(0, 1, 0), Quaternion.Identity, .02f, 2.2f, [], [], .005f, wallPlanes);
Check(wallHair.Positions.Skip(1).All(p => p.X <= .0951f), "Triangle-plane body contact replaced by a capsule or ignored");
Check(Vector3.Distance(wallHair.Positions[0], new(0, 1, 0)) < 1e-6f, "Mesh contact displaced scalp anchor");
for (int i = 0; i < 600; i++)
    wallHair.Step(1f / 60, new(0, 1, 0), Quaternion.Identity, .02f, 2.2f, [], [], .005f);
Check(!wallHair.Awake, "Hair failed to sleep after mesh contact removal");
wallHair.Step(1f / 60, new(0, 1, 0), Quaternion.Identity, .02f, 2.2f, [], [], .005f, wallPlanes);
Check(wallHair.Awake, "Changed mesh surface failed to wake sleeping hair");
before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 300; i++)
    wallHair.Step(1f / 60, new(0, 1, 0), Quaternion.Identity, .02f, 2.2f, [], [], .005f, wallPlanes);
Check(GC.GetAllocatedBytesForCurrentThread() == before, "Mesh-plane contact allocated per step");

// --- Swing: a 40 cm ponytail must swing back after the head stops, not just trail and stop. ---
var tail = new List<Vector3> { new(0, .05f, -.08f) }; var tailParents = new List<int> { -1 };
for (int i = 1; i <= 5; i++) { tail.Add(new Vector3(0, .05f - .075f * i, -.1f - .02f * i)); tailParents.Add(i - 1); }
(int Crossings, float Settle, float LengthError, bool Slept) Swing(float swingDamping)
{
    var s = new FlexibleHairSolver(tailParents.ToArray(), tail.ToArray(), .02f, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.Zero);
    for (int i = 0; i < 120; i++) s.Step(1f / 60, Vector3.Zero, Quaternion.Identity, .02f, 2.2f, [], [], .008f, default, swingDamping);
    var restTip = s.Positions[^1]; var lag = new List<float>(); float lengthError = 0;
    for (int i = 0; i < 900; i++)
    {
        float x = i < 6 ? .2f * i / 6 : .2f;
        s.Step(1f / 60, new(x, 0, 0), Quaternion.Identity, .02f, 2.2f, [], [], .008f, default, swingDamping);
        lag.Add(s.Positions[^1].X - x - restTip.X);
        for (int k = 1; k < s.Count; k++)
            lengthError = MathF.Max(lengthError, MathF.Abs(Vector3.Distance(s.Positions[k], s.Positions[k - 1]) - Vector3.Distance(tail[k], tail[k - 1])));
        Check(s.Positions.All(FlexibleHairSolver.Finite), "Underdamped hair diverged");
    }
    int crossings = 0;
    for (int i = 7; i < lag.Count; i++) if (MathF.Sign(lag[i]) != MathF.Sign(lag[i - 1]) && MathF.Abs(lag[i]) > .002f) crossings++;
    return (crossings, (lag.FindLastIndex(v => MathF.Abs(v) > .005f) + 1) / 60f, lengthError, !s.Awake);
}
var critical = Swing(1f); var swinging = Swing(.15f);
Check(critical.Crossings == 0, "Critical swing damping no longer reproduces the old non-swinging response");
Check(swinging.Crossings >= 2, $"Default swing damping does not swing back: {swinging.Crossings} crossings");
Check(swinging.Settle < 3f && swinging.Slept, "Underdamped hair never settles or sleeps");
Check(swinging.LengthError < .003f, "Underdamped hair stretched");
Console.WriteLine($"Swing: critical {critical.Crossings} crossings/{critical.Settle:F2}s; default 0.15 {swinging.Crossings} crossings/{swinging.Settle:F2}s, length error {swinging.LengthError * 1000:F2}mm.");

// --- Input timing: catch-up steps must see the head at their own time inside the frame. ---
(Vector3 P, Quaternion R) RunningHead(double t) => (new Vector3(.03f * MathF.Sin((float)t * 9f), 1.6f + .04f * MathF.Sin((float)t * 18f), 4f * (float)t),
    Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f * MathF.Sin((float)t * 2.5f)));
double Jitter(float fps, float noise, bool interpolate)
{
    var h0 = RunningHead(0);
    var s = new FlexibleHairSolver(tailParents.ToArray(), tail.ToArray(), .02f, h0.P, h0.R, Vector3.Zero, Vector3.Zero);
    float accumulator = 0; double t = 0; var tips = new List<Vector3>(); var random = new Random(1); var (lastP, lastR) = h0;
    for (int frame = 0; frame < (int)(fps * 6); frame++)
    {
        float dt = 1 / fps * (1 + noise * (float)(random.NextDouble() * 2 - 1)); t += dt;
        var (p, r) = RunningHead(t); accumulator += dt;
        for (int k = 0; accumulator >= 1f / 60 && k < 4; k++)
        {
            accumulator -= 1f / 60;
            float blend = interpolate ? HairInputTiming.StepBlend(accumulator, dt) : 1;
            s.Step(1f / 60, Vector3.Lerp(lastP, p, blend), Quaternion.Slerp(lastR, r, blend), .02f, 2.2f, [], [], .008f, default, .15f);
        }
        if (accumulator >= 1f / 60) accumulator = 0;
        lastP = p; lastR = r;
        // Same render re-rooting as ReadbackHairRig.
        float alpha = Math.Clamp(accumulator * 60, 0, 1);
        var delta = r * Quaternion.Inverse(s.InterpolatedHeadRotation(alpha));
        var tip = p + Vector3.Transform(s.InterpolatedPosition(s.Count - 1, alpha) - s.InterpolatedHead(alpha), delta);
        if (t > 1) tips.Add(Vector3.Transform(tip - p, Quaternion.Inverse(r)));
    }
    double sum = 0;
    for (int i = 1; i + 1 < tips.Count; i++) sum += ((tips[i + 1] - 2 * tips[i] + tips[i - 1]) * fps * fps).Length();
    return sum / (tips.Count - 2);
}
foreach (var (fps, noise) in new[] { (75f, 0f), (144f, 0f), (50f, 0f), (60f, .25f) })
{
    double naive = Jitter(fps, noise, false), timed = Jitter(fps, noise, true);
    Check(timed < naive * .3, $"Per-step input timing does not remove frame-beat jitter at {fps} fps ±{noise:P0}: {timed:F1} vs {naive:F1}");
    Console.WriteLine($"Frame-beat jitter {fps} fps ±{noise:P0}: frame-sampled {naive:F1}, step-timed {timed:F1} m/s^2.");
}

// --- Rendered body surface: outward normals from winding, inside points pushed back out. ---
BodyMeshGeometry Box(Vector3 half, bool reflected)
{
    var v = new Vector3[8];
    for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
    int[] f = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
    if (reflected) for (int i = 0; i < f.Length; i += 3) (f[i + 1], f[i + 2]) = (f[i + 2], f[i + 1]);
    return new BodyMeshGeometry(v, f);
}
foreach (var reflected in new[] { false, true })
{
    var region = new PosedSurfaceRegion("j_sebo_c", Box(new(.1f, .1f, .1f), reflected));
    var origin = new Vector3(0, .5f, 0); var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
    Check(region.TryContact(origin + Vector3.Transform(new Vector3(.08f, .01f, 0), turn), origin, turn, .1f, out _, out var inward, out var inside) &&
          inside < 0 && Vector3.Dot(inward, Vector3.Transform(Vector3.UnitX, turn)) > .99f, $"Inside point not reported inside with outward normal (reflected={reflected})");
    Check(region.TryContact(origin + Vector3.Transform(new Vector3(.13f, .01f, 0), turn), origin, turn, .1f, out _, out var outward, out var outside) &&
          MathF.Abs(outside - .03f) < 1e-4f && Vector3.Dot(outward, Vector3.Transform(Vector3.UnitX, turn)) > .99f, $"Outside distance/normal wrong (reflected={reflected})");
    Check(!region.TryContact(origin + new Vector3(0, .5f, 0), origin, turn, .1f, out _, out _, out _), "Region reported a surface beyond reach");
}
Console.WriteLine("PASS rendered surface region: posed queries, outward normals for both windings, reach culling.");

// Hair draped over a shoulder block, including a start where the strand is already inside it.
float SurfaceRun(Vector3 blockOrigin, Vector3 blockVelocity, int steps, out float worstDuring)
{
    var region = new PosedSurfaceRegion("j_sebo_c", Box(new(.15f, .05f, .15f), false));
    var s = Make();
    var points = new HairContactPlane[s.Count]; var segments = new HairContactPlane[s.Count];
    HairContactPlane Plane(Vector3 p, Vector3 origin) =>
        region.TryContact(p, origin, Quaternion.Identity, .1f, out var hit, out var n, out _) ? new(true, hit, n, blockVelocity) : default;
    float SignedWorst(Vector3 origin)
    {
        float worst = float.MaxValue;
        for (int i = 1; i < s.Count; i++)
        {
            if (region.TryContact(s.Positions[i], origin, Quaternion.Identity, .1f, out _, out _, out var d)) worst = MathF.Min(worst, d);
            if (region.TryContact((s.Positions[i] + s.Positions[i - 1]) * .5f, origin, Quaternion.Identity, .1f, out _, out _, out var m)) worst = MathF.Min(worst, m);
        }
        return worst;
    }
    worstDuring = float.MaxValue;
    for (int step = 0; step < steps; step++)
    {
        var origin = blockOrigin + blockVelocity * (step / 60f);
        for (int i = 1; i < s.Count; i++)
        {
            points[i] = Plane(s.Positions[i], origin);
            segments[i] = Plane((s.Positions[i] + s.Positions[i - 1]) * .5f, origin);
        }
        s.Step(1f / 60, new(0, 1, 0), Quaternion.Identity, .02f, 2.2f, [], [], .005f, points, .15f, 9.81f, segments);
        Check(s.Positions.All(FlexibleHairSolver.Finite), "Surface contact diverged");
        if (step > 10) worstDuring = MathF.Min(worstDuring, SignedWorst(blockOrigin + blockVelocity * ((step + 1) / 60f)));
    }
    return SignedWorst(blockOrigin + blockVelocity * (steps / 60f));
}
var draped = SurfaceRun(new(.12f, .87f, 0), Vector3.Zero, 600, out _);
Check(draped > .005f - .0015f, $"Hair rests inside the rendered shoulder: {draped * 1000:F1} mm");
// Only the tip starts embedded. A strand threaded through a solid (root above, tip below)
// is topologically trapped with inextensible segments; it rests there, it does not jitter.
var buried = SurfaceRun(new(.2f, .86f, 0), Vector3.Zero, 600, out _);
Check(buried > .005f - .0015f, $"Hair that starts inside the body is not pushed back out: {buried * 1000:F1} mm");
// The shoulder lifts 6 cm under resting hair (well below the fixed scalp root).
SurfaceRun(new(.12f, .81f, 0), new(0, .05f, 0), 72, out var carried);
Check(carried > -.004f, $"Rising body surface leaves hair inside it: {carried * 1000:F1} mm");
Console.WriteLine($"Surface contact: draped {draped * 1000:F1} mm, recovered from inside {buried * 1000:F1} mm, worst while carried {carried * 1000:F1} mm (shell 5 mm).");
Console.WriteLine($"PASS: {assertions} assertions; real production solver, curvature, inertia, lengths, head rotation, sleep/wake, teleport, contacts and zero step allocations.");
