using System.Numerics;
using System.Diagnostics;
using CombatSimulator.Animation.Hair;

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
Console.WriteLine($"PASS: {assertions} assertions; real production solver, curvature, inertia, lengths, head rotation, sleep/wake, teleport, contacts and zero step allocations.");
