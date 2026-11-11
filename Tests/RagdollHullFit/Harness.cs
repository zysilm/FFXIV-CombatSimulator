using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using CombatSimulator.Animation;
using Lumina;
using Fitter = CombatSimulator.Animation.StructuralMeshFitter;
using Sample = CombatSimulator.Animation.StructuralMeshFitter.Sample;

/// <summary>A collision volume in the reference pose, for either the built-in set or the fitted one.</summary>
internal sealed class Volume
{
    public string Bone = "";
    public string Parent = "";
    public Vector3 Joint;                   // where it hangs off its parent
    public float Swing = 0.5f;
    public Vector3 Center;
    public Quaternion Rotation = Quaternion.Identity;
    public float Radius, HalfLength;        // capsule
    public Vector3[]? Hull;                 // hull corners, relative to Center, in the Rotation frame
    public float Mass;

    public float Support(Vector3 direction)
    {
        if (Hull == null)
            return Vector3.Dot(Center, direction) + HalfLength * MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitY, Rotation), direction)) + Radius;
        var best = float.MinValue;
        foreach (var p in Hull) best = MathF.Max(best, Vector3.Dot(Center + Vector3.Transform(p, Rotation), direction));
        return best;
    }
}

internal static class Harness
{
    /// <param name="Verified">False where the offline skeleton and model do not agree and nothing can be concluded.</param>
    internal sealed record Body(string Label, int Race, int SourceRace, string[] Models, bool Naked = true, bool Verified = true, string? Skeleton = null);

    private static readonly string[] Fitted = { "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_asi_a_l", "j_asi_a_r" };
    private static readonly string[] Reported = { "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_asi_a_l" };
    private static readonly string[] ReportedNames = { "pelvis", "waist", "back", "chest", "thigh" };

    // The built-in volumes (RagdollController's bone table): radius, half length, mass, the bone
    // the capsule points at, the body it hangs from and the joint's swing limit.
    private static readonly (string Bone, float Radius, float Half, float Mass, string Toward, string Parent, float Swing)[] BuiltIn =
    {
        ("j_kosi", 0.105f, 0.06f, 8f, "j_sebo_a", "", 0f),
        ("j_sebo_a", 0.10f, 0.05f, 10f, "j_sebo_b", "j_kosi", 0.2f),
        ("j_sebo_b", 0.09f, 0.05f, 5f, "j_sebo_c", "j_sebo_a", 0.15f),
        ("j_sebo_c", 0.09f, 0.05f, 6f, "j_kubi", "j_sebo_b", 0.15f),
        ("j_asi_a_l", 0.045f, 0.12f, 10f, "j_asi_b_l", "j_kosi", 1.3f),
        ("j_asi_a_r", 0.045f, 0.12f, 10f, "j_asi_b_r", "j_kosi", 1.3f),
        // The rest of the body is the same in both sets; it is there so the body lies as a whole.
        ("j_kao", 0.08f, 0.04f, 3.5f, "j_kao+", "j_sebo_c", 0.35f),
        // Arms hang freely: with a tight shoulder the forearms prop the chest up like struts.
        ("j_ude_a_l", 0.025f, 0.08f, 2f, "j_ude_b_l", "j_sebo_c", 3.0f),
        ("j_ude_a_r", 0.025f, 0.08f, 2f, "j_ude_b_r", "j_sebo_c", 3.0f),
        ("j_ude_b_l", 0.025f, 0.07f, 1.2f, "j_te_l", "j_ude_a_l", 1.2f),
        ("j_ude_b_r", 0.025f, 0.07f, 1.2f, "j_te_r", "j_ude_a_r", 1.2f),
        ("j_asi_c_l", 0.035f, 0.19f, 3f, "j_asi_d_l", "j_asi_a_l", 1.2f),
        ("j_asi_c_r", 0.035f, 0.19f, 3f, "j_asi_d_r", "j_asi_a_r", 1.2f),
    };

    private static int failures;
    private static bool Trace;
    private static void Check(bool condition, string what)
    {
        if (condition) return;
        failures++;
        Console.WriteLine($"      FAIL: {what}");
    }

    /// <param name="Twist">Angle between pelvis and chest, relative to the reference pose, once settled (radians).</param>
    /// <param name="ArmSpeed">Fastest an arm moved after the first half second, m/s.</param>
    /// <param name="Drift">Furthest any body moved from where it started, with no gravity (m): what the volumes do to each other by merely overlapping.</param>
    private readonly record struct Lying(float[] Clearance, float[] Natural, float Speed, float Twist = 0f, float ArmSpeed = 0f, float Drift = 0f);

    // How many joints of the real rig each link of this one stands for: the clavicle, the neck and
    // the upper-knee stub are left out here. The ragdoll lets bodies up to two joints apart overlap.
    private static int Hops(string bone) => bone is "j_ude_a_l" or "j_ude_a_r" or "j_kao" or "j_asi_c_l" or "j_asi_c_r" ? 2 : 1;

    internal static int Run(GameData game, Func<string, ModelGeometryData?> load, string[] options)
    {
        var pool = new BufferPool();
        Trace = options.Contains("--trace");
        var only = options.FirstOrDefault(o => o.StartsWith("--only="))?[7..];
        var pbd = new RenderedRaceDeformation(game.GetFile("chara/xls/boneDeformer/human.pbd")!.Data);
        var results = new List<(Body Body, Lying[] Lying, int Fits)>();

        foreach (var body in BuildBodies(game))
        {
            if (only != null && !body.Label.Contains(only)) continue;
            var sklb = body.Skeleton != null ? File.ReadAllBytes(body.Skeleton)
                : game.GetFile($"chara/human/c{body.Race:D4}/skeleton/base/b0001/skl_c{body.Race:D4}b0001.sklb")?.Data;
            if (sklb == null) { Console.WriteLine($"{body.Label}: no skeleton"); continue; }
            var skeleton = Skeleton.Read(sklb);
            var targets = new HashSet<string>(Fitter.Targets.Select(t => t.Bone));
            var samples = new Dictionary<string, List<Sample>>();
            var loaded = 0;
            var watch = new Stopwatch();
            foreach (var path in body.Models)
            {
                var data = load(path);
                if (data == null) continue;
                loaded++;
                watch.Start();
                StructuralMeshSampler.Collect(data, uint.MaxValue, new bool[data.Shapes.Length],
                    body.Race == body.SourceRace ? null : names => StructuralMeshSampler.RaceDeformation(
                        pbd, (ushort)body.SourceRace, (ushort)body.Race, names, skeleton.Names, skeleton.Parents),
                    targets, samples);
                watch.Stop();
            }
            if (loaded < 2) continue;
            var sampleTime = watch.Elapsed.TotalMilliseconds;
            watch.Restart();

            Vector3 At(string name) => skeleton.Model[skeleton.Index(name)].Translation;
            var height = Fitter.ReferenceHeight(At("j_kao"), At("j_asi_e_l"), At("j_asi_e_r"));
            var forward = MathF.Sign(At("j_asi_e_l").Z - At("j_asi_d_l").Z) * Vector3.UnitZ;

            var fits = new Dictionary<string, Fitter.Fit>();
            var reasons = new List<string>();
            foreach (var target in Fitter.Targets)
            {
                int bone = skeleton.Index(target.Bone), child = skeleton.Index(target.Child);
                if (bone < 0 || child < 0 || !samples.TryGetValue(target.Bone, out var list)) { reasons.Add($"{target.Bone}: no samples"); continue; }
                if (Fitter.TryFit(target, height, skeleton.Model[bone], skeleton.Model[child].Translation, list, At("j_kubi") - At("j_kosi"), out var fit, out var reason))
                    fits[target.Bone] = fit;
                else reasons.Add($"{target.Bone}: {reason}");
            }
            foreach (var (left, right) in Fitter.Pairs)
                if (Fitter.ValidatePair(fits, left, right) is { } pairReason) reasons.Add(pairReason);
            var fitTime = watch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"== {body.Label}: height {height:F2} m, fitted {fits.Count}/{Fitter.Targets.Length} ({samples.Values.Sum(l => l.Count)} samples; read {sampleTime:F0} ms, fit {fitTime:F0} ms)" +
                              (reasons.Count > 0 ? $"  [{string.Join("; ", reasons)}]" : ""));
            if (body.Naked && body.Verified) Check(fits.Count == Fitter.Targets.Length, "a naked body must fit every segment");

            var oldVolumes = new List<Volume>();
            var newVolumes = new List<Volume>();
            foreach (var (bone, radius, half, mass, toward, parent, swing) in BuiltIn)
            {
                int index = skeleton.Index(bone);
                var origin = skeleton.Model[index].Translation;
                Matrix4x4.Decompose(skeleton.Model[index], out _, out var rotation, out _);
                // The head has no child body: it continues the neck.
                var toChild = toward.EndsWith('+') ? origin - At("j_kubi") : At(toward) - origin;
                var old = new Volume { Bone = bone, Parent = parent, Joint = origin, Swing = swing, Radius = radius, HalfLength = half, Mass = mass };
                if (toChild.Length() > 0.01f)
                {
                    var direction = Vector3.Normalize(toChild);
                    old.Center = origin + half * direction;
                    old.Rotation = Fitter.SectionRotation(direction, rotation);
                }
                else { old.Center = origin; old.Rotation = rotation; }
                oldVolumes.Add(old);

                if (!fits.TryGetValue(bone, out var fit) || fit.HullPoints == null) { newVolumes.Add(old); continue; }
                var axis = Vector3.Normalize(Vector3.Transform(fit.AxisBoneLocal, rotation));
                var section = Quaternion.Normalize(Fitter.SectionRotation(axis, rotation) *
                                                   Quaternion.CreateFromAxisAngle(Vector3.UnitY, fit.RollRadians));
                // The shape the ragdoll builds, through the same code.
                if (!StructuralHullShape.TryCreate(fit.HullPoints, 1f, pool, out var shape, out var center, out var cornerArray, out var edges))
                { Check(false, $"{bone}: hull shape was not created"); newVolumes.Add(old); continue; }
                var corners = cornerArray.ToList();
                Check(edges.Length % 2 == 0 && edges.All(e => corners.Any(c => Vector3.Distance(c, e) < 1e-5f)), $"{bone}: hull edges do not join its corners");
                // Euler: corners - edges + faces = 2 for any convex solid.
                Check(corners.Count - edges.Length / 2 + shape.FaceToVertexIndicesStart.Length == 2, $"{bone}: hull is not a closed solid");
                shape.Dispose(pool);
                newVolumes.Add(new Volume
                {
                    Bone = bone, Parent = parent, Joint = origin, Swing = swing, Mass = mass,
                    Center = origin + Vector3.Transform(center, section), Rotation = section, Hull = corners.ToArray(),
                });
                Check(corners.Count <= 80, $"{bone}: hull has {corners.Count} corners");
                CheckFacets(bone, corners, height);
            }

            // Skin carried by each fitted bone: every point goes to the bone that weighs most on it.
            var skin = new Dictionary<string, List<Vector3>>();
            foreach (var bone in Fitted)
                skin[bone] = samples.TryGetValue(bone, out var list)
                    ? list.Where(s => s.Influence >= 0.5f).Select(s => s.Position).ToList() : new();

            var lying = new Lying[6];
            var driftBefore = Lie(pool, skeleton, oldVolumes, skin, -forward, weightless: true).Drift;
            var driftAfter = Lie(pool, skeleton, newVolumes, skin, -forward, weightless: true).Drift;
            Console.WriteLine($"   weightless in the reference pose, furthest any body is pushed by the others: {driftBefore * 100:F1} -> {driftAfter * 100:F1} cm");
            if (body.Verified && body.Naked && fits.Count == Fitter.Targets.Length)
                Check(driftAfter < MathF.Max(0.01f, driftBefore + 0.005f), $"fitted volumes push each other apart: {driftAfter * 100:F1} cm");
            foreach (var (slot, name, direction) in new[] { (0, "supine", -forward), (2, "prone", forward), (4, "side", Vector3.UnitX) })
            {
                var before = lying[slot] = Lie(pool, skeleton, oldVolumes, skin, direction);
                var after = lying[slot + 1] = Lie(pool, skeleton, newVolumes, skin, direction);
                var line = $"   {name,-6} skin above ground, cm (built-in -> fitted | a rigid body at best):";
                for (var i = 0; i < Reported.Length; i++)
                    line += $"  {ReportedNames[i]} {before.Clearance[i] * 100:+0.0;-0.0}->{after.Clearance[i] * 100:+0.0;-0.0}|{after.Natural[i] * 100:0.0}";
                Console.WriteLine(line + $"   speed {before.Speed * 100:F1}->{after.Speed * 100:F1} cm/s, waist bend {before.Twist * 57.3f:F0}->{after.Twist * 57.3f:F0} deg, arm peak {before.ArmSpeed:F1}->{after.ArmSpeed:F1} m/s");
                if (!body.Verified || fits.Count < Fitter.Targets.Length) continue;

                if (slot == 4)
                {
                    // On its side a body balances on hip and shoulder and may roll either way, so
                    // there is no shape to hold it to: it must come to rest, and not dig in.
                    Check(after.Speed < 0.03f, $"{name}: fitted body still moving at {after.Speed * 100:F1} cm/s");
                    for (var i = 0; i < Reported.Length; i++)
                        Check(after.Clearance[i] > MathF.Min(-0.03f, before.Clearance[i] - 0.005f),
                            $"{name} {ReportedNames[i]}: sinks {-after.Clearance[i] * 100:F1} cm into the ground (built-in {-before.Clearance[i] * 100:F1})");
                    continue;
                }
                // Some bodies never quite stop in this rig with either set of volumes (face down on a
                // large chest the torso balances on the belly); the fitted one must not be much livelier.
                Check(after.Speed < MathF.Max(0.03f, before.Speed * 3f), $"{name}: fitted body still moving at {after.Speed * 100:F1} cm/s (built-in {before.Speed * 100:F1})");
                for (var i = 0; i < Reported.Length; i++)
                {
                    // A rigid body cannot do better than its own shape allows (the hollow of the
                    // lower back stays a hollow); it must come within 1.5 cm of that, and may sink
                    // by the give of soft tissue but no further.
                    // The buttocks are skinned partly to the thighs: the pelvis is down when either is.
                    var reached = i == 0 ? MathF.Min(after.Clearance[0], after.Clearance[4]) : after.Clearance[i];
                    var floats = reached - after.Natural[i];
                    if (body.Naked)
                    {
                        // Face down, the legs rest on the knees and shins, which keep their built-in volumes.
                        Check(floats < (slot == 0 ? 0.025f : 0.03f), $"{name} {ReportedNames[i]}: {floats * 100:F1} cm higher than the body's shape requires");
                        Check(after.Clearance[i] > -0.03f, $"{name} {ReportedNames[i]}: sinks {-after.Clearance[i] * 100:F1} cm into the ground");
                    }
                    else
                    {
                        // Clothed: whatever the garment does to the fit, no worse than the built-in volumes.
                        var builtInError = MathF.Max(before.Clearance[i] - before.Natural[i], -before.Clearance[i]);
                        var fittedError = MathF.Max(floats, -after.Clearance[i]);
                        Check(fittedError < MathF.Max(builtInError, 0.03f) + 0.005f,
                            $"{name} {ReportedNames[i]}: fitted is {fittedError * 100:F1} cm off, built-in {builtInError * 100:F1} cm");
                    }
                }
            }
            results.Add((body, lying, fits.Count));
        }

        Console.WriteLine();
        foreach (var (title, set) in new[]
                 {
                     ("naked bodies", results.Where(r => r.Body.Naked && r.Body.Verified && r.Fits == Fitter.Targets.Length).ToList()),
                     ("clothed bodies", results.Where(r => !r.Body.Naked && r.Fits == Fitter.Targets.Length).ToList()),
                 })
        {
            if (set.Count == 0) continue;
            Console.WriteLine($"Summary, {set.Count} {title} (built-in -> fitted), cm:");
            foreach (var (slot, name) in new[] { (0, "supine"), (2, "prone"), (4, "side") })
            {
                float Floating(Lying l, int i) => MathF.Max(0f, l.Clearance[i] - l.Natural[i]);
                float Sinking(Lying l, int i) => MathF.Max(0f, -l.Clearance[i]);
                var line = $"   {name}: floating above where its shape puts it";
                for (var i = 0; i < Reported.Length; i++)
                    line += $"  {ReportedNames[i]} {set.Average(r => Floating(r.Lying[slot], i)) * 100:F1}->{set.Average(r => Floating(r.Lying[slot + 1], i)) * 100:F1}";
                if (slot != 4) Console.WriteLine(line);
                line = $"   {name}: sunk into the ground                 ";
                for (var i = 0; i < Reported.Length; i++)
                    line += $"  {ReportedNames[i]} {set.Average(r => Sinking(r.Lying[slot], i)) * 100:F1}->{set.Average(r => Sinking(r.Lying[slot + 1], i)) * 100:F1}";
                Console.WriteLine(line);
            }
        }
        Check(results.Count(r => r.Body.Naked && r.Body.Verified && r.Fits == Fitter.Targets.Length) >= 12, "fewer than 12 naked bodies were verified");

        UnitChecks();
        Performance(pool);
        Console.WriteLine(failures == 0 ? "RagdollHullFit: all checks passed." : $"RagdollHullFit: {failures} check(s) FAILED.");
        return failures;
    }

    private static List<Body> BuildBodies(GameData game)
    {
        var bodies = new List<Body>();
        string[] Naked(int race) => new[] { "top", "dwn", "glv", "sho" }.Select(p => $"chara/equipment/e0000/model/c{race:D4}e0000_{p}.mdl").ToArray();
        // Races wear the Midlander models, reshaped by the game's race deformer.
        foreach (int race in new[] { 101, 301, 501, 701, 1301, 1701 }) bodies.Add(new($"vanilla c{race:D4}", race, 101, Naked(101)));
        foreach (int race in new[] { 201, 401, 601, 801, 1001, 1401, 1601, 1801 }) bodies.Add(new($"vanilla c{race:D4}", race, 201, Naked(201)));
        // Roegadyn and Hrothgar men and Lalafell: the offline skeleton and model do not agree in size,
        // so these only show that a mismatch is refused rather than fitted.
        foreach (int race in new[] { 901, 1501 }) bodies.Add(new($"vanilla c{race:D4} (unverifiable offline)", race, 101, Naked(101), Verified: false));
        foreach (int race in new[] { 1101, 1201 }) bodies.Add(new($"vanilla c{race:D4} (unverifiable offline)", race, race, Naked(race), Verified: false));

        const string mod = "C:/FFxivMods/hs-Rue+-2.2.7-y0f/files";
        if (Directory.Exists(mod))
        {
            string Top(string v) => $"{mod}/chest - smallclothes/{v}/chara/equipment/e0000/model/c0201e0000_top.mdl";
            string Legs(string v) => $"{mod}/legs - smallclothes/{v}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
            foreach (var (label, top, legs, race) in new[]
                     {
                         ("mod body small/slim", "small", "small - yanilla - a", 201), ("mod body large/thick", "large", "skull crushers - a", 201),
                         ("mod body buff", "buff - large", "small - watermelon crushers - a", 201), ("mod body large on c0801", "large", "skull crushers - a", 801),
                         ("mod body small on c1401", "small", "small - yanilla - a", 1401), ("mod body yiggle medium on c0601", "yiggle - medium", "skull crushers - b", 601),
                     })
                if (File.Exists(Top(top)) && File.Exists(Legs(legs)))
                    bodies.Add(new(label, race, 201, new[] { Top(top), Legs(legs), Naked(201)[2], Naked(201)[3] },
                        Skeleton: $"{mod}/files/yet another skeleton/animation_general usage/chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb"));
        }

        // Clothed: whatever the fit makes of gear, it must not be worse than the built-in volumes.
        var clothed = 0;
        for (int equipment = 1; equipment < 800 && clothed < 14; equipment += 23)
            foreach (int race in new[] { 201, 101 })
            {
                var models = new[] { "top", "dwn" }.Select(p => $"chara/equipment/e{equipment:D4}/model/c{race:D4}e{equipment:D4}_{p}.mdl").ToArray();
                if (!game.FileExists(models[0]) || !game.FileExists(models[1])) continue;
                bodies.Add(new($"gear e{equipment:D4} on c{race:D4}", race, race, models.Concat(Naked(race)[2..]).ToArray(), Naked: false));
                clothed++;
            }
        return bodies;
    }

    /// <summary>
    /// The side a body rests on must be a face of its hull, never a corner or a single edge: checked
    /// for all twelve directions around the segment, in the slice that sticks out furthest.
    /// </summary>
    private static void CheckFacets(string bone, List<Vector3> corners, float height)
    {
        for (var d = 0; d < 12; d++)
        {
            var angle = d * MathF.PI / 6f;
            var axis = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var support = corners.Max(c => Vector3.Dot(c, axis));
            var onFace = corners.Where(c => support - Vector3.Dot(c, axis) < 0.0005f).ToArray();
            var across = Vector3.Cross(axis, Vector3.UnitY);
            var width = onFace.Max(c => Vector3.Dot(c, across)) - onFace.Min(c => Vector3.Dot(c, across));
            var length = onFace.Max(c => c.Y) - onFace.Min(c => c.Y);
            Check(onFace.Length >= 4 && width > height * 0.008f && length > 0.01f,
                $"{bone}: the extreme at {d * 30} deg is not a face ({onFace.Length} corners, {width * 100:F1} x {length * 100:F1} cm)");
        }
    }

    /// <summary>
    /// Lays the body on flat ground with the given side down, lets it settle, and reports how far the
    /// lowest skin of each fitted bone ends up above the ground, next to where a rigid cast of the
    /// reference pose would have it.
    /// </summary>
    private static Lying Lie(BufferPool pool, Skeleton skeleton, List<Volume> volumes, Dictionary<string, List<Vector3>> skin, Vector3 down,
        bool weightless = false)
    {
        var noContact = new HashSet<(int, int)>();
        var startOverlap = new StartOverlapAllowance();
        var simulation = RagdollWorld.Create(pool, noContact, weightless ? 0f : 9.81f, startOverlap);
        simulation.Statics.Add(new StaticDescription(new Vector3(0, -0.5f, 0), simulation.Shapes.Add(new Box(50, 1, 50))));
        // Model space -> world with `down` pointing at the ground.
        var toWorld = MathF.Abs(down.X) > 0.5f
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, down.X > 0 ? -MathF.PI / 2 : MathF.PI / 2)
            : Quaternion.CreateFromAxisAngle(Vector3.UnitX, down.Z < 0 ? -MathF.PI / 2 : MathF.PI / 2);
        var lift = new Vector3(0, volumes.Max(v => v.Support(down)) + 0.02f, 0);
        Vector3 World(Vector3 model) => Vector3.Transform(model, toWorld) + lift;

        var handles = new Dictionary<string, BodyHandle>();
        var start = new Dictionary<string, RigidPose>();
        foreach (var volume in volumes)
        {
            var pose = new RigidPose(World(volume.Center), Quaternion.Normalize(toWorld * volume.Rotation));
            TypedIndex shape; BodyInertia inertia;
            if (volume.Hull == null)
            {
                var capsule = new Capsule(volume.Radius, volume.HalfLength * 2f);
                shape = simulation.Shapes.Add(capsule); inertia = capsule.ComputeInertia(volume.Mass);
            }
            else
            {
                var hull = new ConvexHull(volume.Hull, pool, out _);
                shape = simulation.Shapes.Add(hull); inertia = hull.ComputeInertia(volume.Mass);
            }
            handles[volume.Bone] = simulation.Bodies.Add(BodyDescription.CreateDynamic(pose, inertia,
                new CollidableDescription(shape, 0.04f), new BodyActivityDescription(-1f)));
            start[volume.Bone] = pose;
        }
        foreach (var volume in volumes)
            if (volume.Parent.Length > 0)
                RagdollWorld.Join(simulation, handles[volume.Parent], handles[volume.Bone], World(volume.Joint), volume.Swing);
        // Parent and grandparent do not collide with a body, exactly as in the ragdoll.
        foreach (var volume in volumes)
        {
            var hops = 0;
            for (var ancestor = volume; ancestor.Parent.Length > 0;)
            {
                hops += Hops(ancestor.Bone);
                if (hops > 2) break;
                ancestor = volumes.First(v => v.Bone == ancestor.Parent);
                int x = handles[volume.Bone].Value, y = handles[ancestor.Bone].Value;
                noContact.Add((Math.Min(x, y), Math.Max(x, y)));
            }
        }
        var armSpeed = 0f;
        if (weightless)
        {
            for (var step = 0; step < 120; step++) { startOverlap.BeginStep(); simulation.Timestep(1f / 60f); }
            if (Trace) Console.WriteLine("      weightless: " + string.Join("  ", volumes.Select(v => $"{v.Bone[2..]} {Vector3.Distance(simulation.Bodies.GetBodyReference(handles[v.Bone]).Pose.Position, start[v.Bone].Position) * 100:F1}")));
            var drift = volumes.Max(v => Vector3.Distance(simulation.Bodies.GetBodyReference(handles[v.Bone]).Pose.Position, start[v.Bone].Position));
            simulation.Dispose();
            return new Lying(Array.Empty<float>(), Array.Empty<float>(), 0f, Drift: drift);
        }

        // On its side a body first has to topple; give it the time.
        for (var step = 0; step < (MathF.Abs(down.X) > 0.5f ? 960 : 480); step++)
        {
            startOverlap.BeginStep();
            simulation.Timestep(1f / 60f);
            if (step >= 30)
                foreach (var arm in new[] { "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r" })
                    armSpeed = MathF.Max(armSpeed, simulation.Bodies.GetBodyReference(handles[arm]).Velocity.Linear.Length());
            if (Trace && step % 60 == 59)
                Console.WriteLine($"      t={(step + 1) / 60}s " + string.Join("  ", volumes.Select(v => v.Bone).Select(b => { var r = simulation.Bodies.GetBodyReference(handles[b]); return $"{b[2..]} v={r.Velocity.Linear.Length() * 100:F0} y={r.Pose.Position.Y * 100:F0}"; })));
        }

        // Where the reference pose, cast rigid and laid on the ground, would have each bone's skin.
        float Lowest(IEnumerable<float> heights)
        {
            var sorted = heights.OrderBy(h => h).ToList();
            return sorted.Count == 0 ? 0f : sorted[(int)(sorted.Count * 0.04f)];
        }
        var rigidFloor = Fitted.Where(b => skin[b].Count > 0).Min(b => Lowest(skin[b].Select(p => -Vector3.Dot(p, down))));
        var clearance = new float[Reported.Length];
        var natural = new float[Reported.Length];
        for (var i = 0; i < Reported.Length; i++)
        {
            var body = simulation.Bodies.GetBodyReference(handles[Reported[i]]);
            var from = start[Reported[i]];
            var delta = Quaternion.Normalize(body.Pose.Orientation * Quaternion.Inverse(from.Orientation));
            clearance[i] = Lowest(skin[Reported[i]].Select(p => (body.Pose.Position + Vector3.Transform(World(p) - from.Position, delta)).Y));
            // The legs swing down at the hip, so a thigh can always reach the ground.
            natural[i] = Reported[i].StartsWith("j_asi") ? 0f : Lowest(skin[Reported[i]].Select(p => -Vector3.Dot(p, down))) - rigidFloor;
        }
        var speed = 0f;
        foreach (var bone in Fitted)
            speed = MathF.Max(speed, simulation.Bodies.GetBodyReference(handles[bone]).Velocity.Linear.Length());
        Quaternion Turned(string bone) => Quaternion.Normalize(simulation.Bodies.GetBodyReference(handles[bone]).Pose.Orientation * Quaternion.Inverse(start[bone].Orientation));
        var between = Quaternion.Normalize(Quaternion.Inverse(Turned("j_kosi")) * Turned("j_sebo_c"));
        var twist = 2f * MathF.Acos(Math.Clamp(MathF.Abs(between.W), 0f, 1f));
        simulation.Dispose();
        return new Lying(clearance, natural, speed, twist, armSpeed);
    }

    /// <summary>Properties of the fit that do not depend on any particular body.</summary>
    private static void UnitChecks()
    {
        // A cylinder of radius 10 cm around a bone that runs 4 cm behind its axis, as a spine does.
        var samples = new List<Sample>();
        for (var ring = 0; ring <= 40; ring++)
            for (var step = 0; step < 72; step++)
            {
                var angle = step * MathF.PI / 36f;
                samples.Add(new Sample(new Vector3(MathF.Cos(angle) * 0.10f, 1f + ring * 0.005f, 0.04f + MathF.Sin(angle) * 0.10f), 1f, 1f));
            }
        var target = new Fitter.Target("j_kosi", "j_sebo_a", true, 30, HullSlices: 3);
        var bone = Matrix4x4.CreateTranslation(0, 1f, 0);
        var ok = Fitter.TryFit(target, 1.7f, bone, new Vector3(0, 1.2f, 0), samples, null, out var fit, out var reason);
        Check(ok && fit.HullPoints != null, $"synthetic cylinder was not fitted ({reason})");
        if (!ok || fit.HullPoints == null) return;

        float Extent(Vector3 axis) => fit.HullPoints.Max(p => Vector3.Dot(p, axis));
        // The hull follows the surface, not the bone: equally far from the cylinder's axis all round,
        // just inside it (the soft tissue allowance), although the bone sits well off centre.
        var inset = 1.7f * 0.006f;
        var radii = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Select(Extent).OrderBy(v => v).ToArray();
        Check(MathF.Abs((radii[0] + radii[3]) - (radii[1] + radii[2])) < 0.004f, "hull is not centred on the surface");
        Check(MathF.Abs((radii[0] + radii[3]) * 0.5f - (0.10f - inset)) < 0.004f && MathF.Abs((radii[1] + radii[2]) * 0.5f - (0.10f - inset)) < 0.004f,
            $"hull radius {(radii[1] + radii[2]) * 50:F1} cm, expected {(0.10f - inset) * 100:F1} cm");
        Check(MathF.Abs(radii[3] - radii[0] - 0.08f) < 0.006f || MathF.Abs(radii[2] - radii[1] - 0.08f) < 0.006f, "hull did not follow the surface off the bone");

        // Twice the body gives twice the hull.
        var doubled = samples.Select(s => new Sample(s.Position * 2f, 1f, 4f)).ToList();
        Check(Fitter.TryFit(target, 3.4f, Matrix4x4.CreateTranslation(0, 2f, 0), new Vector3(0, 2.4f, 0), doubled, null, out var big, out _) &&
              big.HullPoints != null && big.HullPoints.Length == fit.HullPoints.Length &&
              big.HullPoints.Zip(fit.HullPoints).All(p => Vector3.Distance(p.First, p.Second * 2f) < 0.004f), "the fit does not scale with the body");

        // A few stray vertices far outside do not move it.
        var noisy = new List<Sample>(samples);
        for (var i = 0; i < 12; i++) noisy.Add(new Sample(new Vector3(0.5f + i * 0.01f, 1.1f, 0.04f), 1f, 1f));
        Check(Fitter.TryFit(target, 1.7f, bone, new Vector3(0, 1.2f, 0), noisy, null, out var robust, out _) && robust.HullPoints != null &&
              MathF.Abs(robust.HullPoints.Max(p => p.Length()) - fit.HullPoints.Max(p => p.Length())) < 0.004f, "stray vertices moved the hull");

        // Dense and sparse halves of the same surface give the same hull as an even one.
        var uneven = new List<Sample>();
        foreach (var s in samples)
        {
            var copies = s.Position.Z > 0.04f ? 4 : 1;
            for (var c = 0; c < copies; c++) uneven.Add(new Sample(s.Position, 1f, 1f / copies));
        }
        Check(Fitter.TryFit(target, 1.7f, bone, new Vector3(0, 1.2f, 0), uneven, null, out var even, out _) && even.HullPoints != null &&
              even.HullPoints.Zip(fit.HullPoints).All(p => Vector3.Distance(p.First, p.Second) < 0.003f), "mesh density changed the hull");

        // Too little surface, or a size no body has, is refused rather than guessed at.
        Check(!Fitter.TryFit(target, 1.7f, bone, new Vector3(0, 1.2f, 0), samples.Take(10).ToList(), null, out _, out _), "ten samples were accepted");
        Check(!Fitter.TryFit(target, 1.7f, bone, new Vector3(0, 1.2f, 0), samples.Select(s => new Sample(s.Position * new Vector3(3, 1, 3), 1f, 1f)).ToList(), null, out _, out _), "an implausible size was accepted");
        Console.WriteLine("Synthetic checks: centring, scale, stray vertices, mesh density, refusals.");
    }

    private static void Performance(BufferPool pool)
    {
        // Cost of the shapes themselves: bodies of each kind resting on the ground, six per character.
        var corners = new List<Vector3>();
        for (var slice = 0; slice < 4; slice++)
            for (var d = 0; d < 12; d++)
            {
                var angle = d * MathF.PI / 6f + MathF.PI / 12f;
                var radius = slice is 1 or 2 ? 0.12f : 0.10f;
                corners.Add(new Vector3(MathF.Cos(angle) * radius, -0.10f + slice * 0.066f, MathF.Sin(angle) * radius * 1.3f));
            }
        double Time(bool hulls)
        {
            var random = new Random(3);
            var simulation = RagdollWorld.Create(pool);
            simulation.Statics.Add(new StaticDescription(new Vector3(0, -0.5f, 0), simulation.Shapes.Add(new Box(200, 1, 200))));
            for (var i = 0; i < 240; i++)
            {
                TypedIndex shape; BodyInertia inertia;
                if (hulls) { var hull = new ConvexHull(corners.ToArray(), pool, out _); shape = simulation.Shapes.Add(hull); inertia = hull.ComputeInertia(8f); }
                else { var capsule = new Capsule(0.105f, 0.12f); shape = simulation.Shapes.Add(capsule); inertia = capsule.ComputeInertia(8f); }
                simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(new Vector3(i % 16 * 0.6f, 0.2f, i / 16 * 0.6f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)random.NextDouble())),
                    inertia, new CollidableDescription(shape, 0.04f), new BodyActivityDescription(-1f)));
            }
            for (var step = 0; step < 120; step++) simulation.Timestep(1f / 60f);
            var watch = Stopwatch.StartNew();
            for (var step = 0; step < 600; step++) simulation.Timestep(1f / 60f);
            watch.Stop();
            simulation.Dispose();
            return watch.Elapsed.TotalMilliseconds / 600 / 40;
        }
        Time(true);
        double capsules = Time(false), hulls = Time(true);
        Console.WriteLine($"Cost per physics step per character (six such bodies on the ground, awake): capsules {capsules * 1000:F1} us, hulls {hulls * 1000:F1} us");
        Check(hulls < capsules * 4 + 0.02, $"hulls cost {hulls * 1000:F0} us per character per step, capsules {capsules * 1000:F0} us");
    }
}
