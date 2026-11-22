// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using AdvancedRagdoll;
using AdvancedRagdoll.Hosting;
using AdvancedRagdoll.Recording;
using CombatSimulator.Animation;

namespace RagdollReplay;

/// <summary>
/// "What if" re-simulation (death guidance, phase P0; README.md):
///   RagdollReplay &lt;file.rgdrec | folder&gt; --what-if &lt;recorded|limp|knee-buckle|hold-legs|hold-pose|slow-slump|auto&gt;
///       [--start-at 0|25|50|75|100] [--out folder] [--seconds 12] [--recorded-dt] [--only text]
///       [--set Field=value ...] [--label name]
/// It is not a replay: nothing is compared bit for bit. From a recording it takes the character (skeleton, scale,
/// hull fits, soft-tissue fits, settings, what the controller carried into the activation) and the death animation
/// (the pose of every frame before physics started), starts physics at a chosen point of that animation with a
/// chosen guidance profile, and lets the real controller run on from there. The ground is rebuilt from the recorded
/// ray hits and ground triangles (a ray the recording cast is answered as the game answered it; any other from the
/// height field of all recorded hits). Not restored: other characters' colliders, grabs and every other call from
/// outside, hair, mesh volumes, weapon strikes. The random source is seeded per recording.
/// Per run it writes a pose trajectory (the format Tests/DeathShots reads, by TrajectoryExport) and a small text file
/// of numbers only the simulation knows (joint angles, energy, rest; see <see cref="PhysicsNumbers"/>) into
/// &lt;out&gt;/&lt;profile&gt;/s&lt;start&gt;/. Tests/DeathShots "guidance" turns those folders into the tables.
/// </summary>
internal static class WhatIf
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The profiles; the library's name of each (GuidedCollapseOptions.TensionProfile), null = none.</summary>
    private static readonly Dictionary<string, string?> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["recorded"] = null,     // the recording's own guidance settings and pending state, untouched
        ["limp"] = null,         // nothing is guided (the baseline)
        ["hold-legs"] = "HoldLegs",
        ["hold-pose"] = "HoldPose",
        ["slow-slump"] = "SlowSlump",
        ["knee-buckle"] = "KneeBuckle",
        ["auto"] = "Auto",
    };

    public static int Run(string[] args)
    {
        var profile = Value(args, "--what-if") ?? "";
        if (!Profiles.ContainsKey(profile))
        {
            Console.Error.WriteLine("--what-if needs one of: " + string.Join(", ", Profiles.Keys));
            return 2;
        }
        var startPercent = int.Parse(Value(args, "--start-at") ?? "100", Inv);
        var outRoot = Value(args, "--out") ?? Path.Combine(Path.GetTempPath(), "DeathGuidance", "whatif");
        var seconds = float.Parse(Value(args, "--seconds") ?? "12", Inv);
        var recordedDt = args.Contains("--recorded-dt");
        var only = Value(args, "--only");
        // The library's guidance numbers (PoseHoldTuning), by field name: --set HoldLegsTension=3 --set GroundHold=false ...
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--set" && args[i + 1].Split('=') is { Length: 2 } pair) SetTuning(pair[0], pair[1]);

        var target = args[0];
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*" + RagdollRecordingFormat.FileExtension).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : new[] { target };
        if (only != null) files = files.Where(f => Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase)).ToArray();
        // --label: the folder of a variant (a profile with other numbers), so the tables can show it beside the others.
        var folder = Path.Combine(outRoot, (Value(args, "--label") ?? profile).ToLowerInvariant(), "s" + startPercent.ToString(Inv));
        Directory.CreateDirectory(folder);
        int done = 0, skipped = 0, failed = 0;
        foreach (var file in files)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var note = RunOne(file, profile, startPercent, seconds, recordedDt, folder);
                if (note.StartsWith("SKIPPED", StringComparison.Ordinal)) skipped++; else done++;
                Console.WriteLine($"{Path.GetFileName(file)} ({clock.Elapsed.TotalSeconds.ToString("F1", Inv)} s): {note}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{Path.GetFileName(file)}: WHAT-IF ERROR: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
        Console.WriteLine($"== what-if {profile} at {startPercent}%: {done} run(s), {skipped} skipped, {failed} failed -> {folder}");
        return failed == 0 ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    // The death guidance arrives with AdvancedRagdoll 0.1.0-beta.5; its members are reached by name so
    // this project still builds against a library without it.
    private static void SetTuning(string field, string value)
    {
        var type = typeof(Ragdoll).Assembly.GetType("AdvancedRagdoll.PoseHoldTuning")
                   ?? throw new ReplayUnsupportedException("this AdvancedRagdoll has no death guidance (PoseHoldTuning)");
        var f = type.GetField(field, BindingFlags.Public | BindingFlags.Static)
                ?? throw new ReplayUnsupportedException($"PoseHoldTuning.{field} not found");
        f.SetValue(null, f.FieldType == typeof(bool) ? bool.Parse(value) : float.Parse(value, Inv));
    }

    internal static void SetTensionProfile(GuidedCollapseOptions options, string? libraryName)
    {
        var property = typeof(GuidedCollapseOptions).GetProperty("TensionProfile");
        if (property == null)
        {
            if (libraryName == null) return;
            throw new ReplayUnsupportedException("this AdvancedRagdoll has no tension profiles (GuidedCollapseOptions.TensionProfile)");
        }
        property.SetValue(options, Enum.Parse(property.PropertyType, libraryName ?? "None"));
    }

    private static string RunOne(string path, string profile, int startPercent, float seconds, bool recordedDt, string folder)
    {
        var source = new ReplaySource(path);
        var header = source.Header;
        if (header[2].AsBool) return "SKIPPED: recorded with the private module";
        var name = Path.GetFileNameWithoutExtension(path);

        // The recording's frames: dt, and the target's pose before physics (every such frame has one).
        var actor = (nint)source.Activate[0].AsULong;
        var frames = new List<(float Dt, bool Physics, int Index)>();
        var preFramePose = new Dictionary<int, int>(); // frame number (from 0) -> record index of its PreFrame pose
        var initPose = -1;
        int colliderActors = 0, grabs = 0, otherCalls = 0;
        for (var i = source.ActivateIndex + 1; i < source.Records.Count; i++)
        {
            var r = source.Records[i];
            switch (r.Type)
            {
                case RecordType.RenderFrame:
                    frames.Add((r[1].AsFloat, r[3].AsBool, i));
                    break;
                case RecordType.SkeletonPose when (nint)r[0].AsULong == actor && frames.Count > 0:
                    if (r[1].AsInt == (int)PosePurpose.PreFrame) preFramePose.TryAdd(frames.Count - 1, i);
                    else if (r[1].AsInt == (int)PosePurpose.Init && initPose < 0) initPose = i;
                    break;
                case RecordType.InitEnd:
                    colliderActors = Math.Max(colliderActors, r[13].AsInt);
                    break;
                case RecordType.ApiCall when r[0].AsInt == 0:
                    if (r[1].AsString is nameof(RagdollController.CreateGrabConstraint) or nameof(RagdollController.CreateGrabConstraintAt)) grabs++;
                    else if (r[1].AsString != nameof(RagdollController.Deactivate)) otherCalls++;
                    break;
            }
        }
        var preFrames = frames.Count(f => !f.Physics);
        var recordedPhysics = frames.Count > preFrames;
        if (preFrames < 1 || preFramePose.Count == 0) return "SKIPPED: no pose before physics";
        // The frame physics starts in (from 0): the last frame not flagged as physics is the recorded start.
        var startFrame = Math.Clamp((int)MathF.Round(startPercent / 100f * preFrames) - 1, 0, preFrames - 1);
        while (startFrame > 0 && !preFramePose.ContainsKey(startFrame)) startFrame--;
        if (!preFramePose.ContainsKey(startFrame)) return "SKIPPED: no pose at the start frame";
        float before = 0f, upTo = 0f;
        for (var i = 0; i <= startFrame; i++) { before = upTo; upTo += frames[i].Dt; }
        var delay = (before + upTo) * 0.5f;

        var skeletons = new WhatIfSkeletons(source, actor, preFramePose, initPose, startFrame);
        var log = new ReplayLog();
        var clock = new WhatIfClock(source.StopwatchFrequency);
        var meshes = new ReplayMeshSource(source);
        var world = new WhatIfWorld(source);
        var libraryProfile = Profiles[profile];
        var keepRecorded = profile.Equals("recorded", StringComparison.OrdinalIgnoreCase);
        var host = new RagdollHostServices
        {
            Skeletons = skeletons,
            World = world,
            Actors = new WhatIfActors(source, skeletons),
            Colliders = new WhatIfColliders(),
            Settings = new WhatIfSettings(new ReplaySettings(source), libraryProfile, keepRecorded),
            Log = log,
            Clock = clock,
            Random = new WhatIfRandom(name),
            Meshes = meshes,
            Build = CombatSimulator.Animation.Hosting.FfxivRagdollHost.ThisBuild,
        };
        var (firstPose, firstPoseIndex) = source.Peek(RecordType.SkeletonPose, r => (nint)r[0].AsULong == actor);
        if (firstPose != null) skeletons.Load(firstPose, firstPoseIndex);

        var controller = new RagdollController(host);
        controller.RunAnimatedHair = false;
        controller.GrabRigid = source.Activate[12].AsBool;
        var carried = ReplayDriver.CarriedState(source, meshes);
        // No other characters, so nothing of theirs is carried in; a profile run starts without a pending collapse.
        carried.TraversalRequests.Clear();
        carried.StrikeColliders.Clear();
        carried.StruckThisWindow.Clear();
        carried.LightweightColliders.Clear();
        if (!keepRecorded)
        {
            carried.PendingDirectedCollapse = false;
            carried.PendingEntryConditionedKneePowerLoss = false;
            carried.PendingKneePowerLoss = false;
        }
        controller.RestoreCarriedState(carried);

        var recordingFolder = Path.Combine(Path.GetTempPath(), "AdvancedRagdollReplay", "out", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recordingFolder);
        RagdollRecordingSession.SecondsLimit = double.PositiveInfinity;
        RagdollRecordingSession.Configure(recordingFolder, log);
        RagdollRecordingSession.Start();
        string? produced = null;
        var stress = new List<Ragdoll.JointLimitStress>();
        float stressMax = 0f;
        var stressPinned = 0;
        var physicsFrames = 0;
        var physicsSeconds = 0f;
        try
        {
            controller.Activate(actor, delay, RagdollTriggerMode.FixedDelay, 0f);
            for (var frame = 0; ; frame++)
            {
                float dt;
                if (frame <= startFrame) dt = frames[frame].Dt;
                else if (recordedDt)
                {
                    // The recording's own frames after its start, as long as it has them.
                    var recorded = preFrames + (frame - startFrame) - 1;
                    if (recorded >= frames.Count) break;
                    dt = frames[recorded].Dt;
                }
                else
                {
                    if (physicsSeconds >= seconds) break;
                    dt = 1f / 60f;
                }
                clock.AdvanceFrame(dt);
                skeletons.BeginFrame(frame);
                controller.ReplayRenderFrame();
                if (!controller.ReplayActive) break;
                if (frame >= startFrame)
                {
                    physicsFrames++;
                    if (frame > startFrame) physicsSeconds += dt;
                }
            }
            if (controller.PhysicsStarted)
            {
                controller.CollectJointLimitStress(stress);
                foreach (var s in stress)
                {
                    stressMax = MathF.Max(stressMax, s.Stress);
                    if (s.Stress >= 0.98f) stressPinned++;
                }
            }
        }
        finally
        {
            RagdollRecordingSession.Stop();
            controller.Dispose();
            produced = Directory.GetFiles(recordingFolder, "*" + RagdollRecordingFormat.FileExtension).SingleOrDefault();
        }
        if (produced == null) { Directory.Delete(recordingFolder, true); return "WHAT-IF ERROR: no recording was written"; }

        string note;
        try
        {
            TrajectoryExport.ExportAs(produced, folder, name);
            var numbers = PhysicsNumbers.Read(produced);
            var text = new StringBuilder();
            void Line(string key, string value) => text.Append(key).Append('=').Append(value).Append('\n');
            Line("name", name);
            Line("profile", Path.GetFileName(Path.GetDirectoryName(folder))!);
            Line("startPercent", startPercent.ToString(Inv));
            Line("startFrame", startFrame.ToString(Inv));
            Line("preFrames", preFrames.ToString(Inv));
            Line("startTime", upTo.ToString("R", Inv));
            Line("recordedPhysics", recordedPhysics ? "1" : "0");
            Line("recordedColliders", colliderActors.ToString(Inv));
            Line("recordedGrabs", grabs.ToString(Inv));
            Line("recordedOtherCalls", otherCalls.ToString(Inv));
            Line("isPlayer", header[21].AsBool ? "1" : "0");
            Line("physicsFrames", physicsFrames.ToString(Inv));
            Line("problems", log.Problems.Count.ToString(Inv));
            Line("firstProblem", log.Problems.Count > 0 ? log.Problems[0].Split('\n')[0] : "");
            Line("guidanceLog", log.Guidance ?? "");
            Line("stressMax", stressMax.ToString("R", Inv));
            Line("stressPinned", stressPinned.ToString(Inv));
            Line("exactRays", world.ExactAnswers.ToString(Inv));
            Line("fieldRays", world.FieldAnswers.ToString(Inv));
            numbers.WriteTo(text);
            File.WriteAllText(Path.Combine(folder, name + ".wi"), text.ToString());
            note = $"start frame {startFrame + 1} of {preFrames} ({upTo.ToString("F2", Inv)} s), {numbers.Ticks} fixed steps, " +
                   $"{numbers.Bodies} bodies{(log.Guidance != null ? ", " + log.Guidance : "")}" +
                   $"{(log.Problems.Count > 0 ? $", {log.Problems.Count} problem(s): {log.Problems[0].Split('\n')[0]}" : "")}";
        }
        finally
        {
            Directory.Delete(recordingFolder, true);
        }
        return note;
    }
}

/// <summary>The target's skeleton: before the what-if start, the pose the recording has for each frame; from the
/// start on, the pose of the start frame every frame (the animation is frozen), which the controller writes over.
/// Other characters have no skeleton.</summary>
internal sealed class WhatIfSkeletons : IHostSkeletonSource
{
    private readonly ReplaySource source;
    private readonly ReplaySkeletons inner;
    private readonly nint target;
    private readonly Dictionary<int, int> preFramePose;
    private readonly int initPose;
    private readonly int startFrame;
    private BoneQs[]? frozen;
    private ReplaySkeleton? skeleton;

    public WhatIfSkeletons(ReplaySource source, nint target, Dictionary<int, int> preFramePose, int initPose, int startFrame)
    {
        this.source = source;
        this.target = target;
        this.preFramePose = preFramePose;
        this.initPose = initPose;
        this.startFrame = startFrame;
        inner = new ReplaySkeletons(source);
    }

    public ReplaySkeleton? Target => skeleton;

    public void Load(RagdollRecord pose, int index) => skeleton = inner.Load(pose, index);

    public void BeginFrame(int frame)
    {
        if (frame <= startFrame)
        {
            // The attached partial skeletons are only captured where physics started in the recording; the body
            // (partial 0) is then put back to this frame's pose.
            if (frame == startFrame && initPose >= 0) skeleton = inner.Load(source.Records[initPose], initPose);
            if (preFramePose.TryGetValue(frame, out var index)) skeleton = inner.Load(source.Records[index], index);
            if (frame == startFrame && skeleton?.At(0).Pose is { } pose) frozen = (BoneQs[])pose.Clone();
            return;
        }
        if (frozen != null && skeleton?.At(0).Pose is { } current && current.Length == frozen.Length)
            Array.Copy(frozen, current, frozen.Length);
    }

    /// <summary>The controller moved the character's draw position (the follow of the local player): the skeleton
    /// root is there from the next frame on.</summary>
    public void MoveRoot(Vector3 position)
    {
        if (skeleton == null) return;
        var root = skeleton.RootValue;
        skeleton.RootValue = new RootTransform(position, root.Rotation, root.Scale);
    }

    public IHostSkeleton? TryGetSkeleton(nint actor, PosePurpose site = PosePurpose.Other)
        => actor == target && skeleton is { Available: true } ? skeleton : null;

    public Vector3? GetBoneWorldPosition(nint actor, string boneName) => null;
}

/// <summary>The ground of the recording. A downward ray the recording cast from the same place is answered as the
/// game answered it; any other from the recorded ground triangles (the highest under the ray) or the height field of
/// all recorded hits (bilinear on the sampled grid, the nearest hit outside it).</summary>
internal sealed class WhatIfWorld : IHostWorldQuery
{
    private readonly Dictionary<(int X, int Z), RagdollRecord> exact = new();
    private readonly List<Vector3> hits = new();
    private readonly List<(Vector3 Min, Vector3 Max, bool Ok, Vector3[] Triangles)> triangleSets = new();
    private readonly float waterY = float.NaN;
    private float minX, minZ, step;
    private int nx, nz;
    private float[,]? grid;
    public int ExactAnswers, FieldAnswers;

    public WhatIfWorld(ReplaySource source)
    {
        foreach (var r in source.Records)
        {
            switch (r.Type)
            {
                case RecordType.Raycast when r[2].AsVector3.Y < -0.9f:
                    var origin = r[1].AsVector3;
                    exact.TryAdd(Key(origin), r);
                    if (r[4].AsBool) hits.Add(r[5].AsVector3);
                    break;
                case RecordType.GroundTriangles:
                    if (!triangleSets.Any(t => t.Min == r[0].AsVector3 && t.Max == r[1].AsVector3))
                        triangleSets.Add((r[0].AsVector3, r[1].AsVector3, r[3].AsBool, Rec.Vectors(r[4])));
                    break;
                case RecordType.WaterSurface when r[3].AsBool:
                    waterY = r[4].AsFloat;
                    break;
            }
        }
        BuildGrid();
    }

    private static (int, int) Key(Vector3 origin) => ((int)MathF.Round(origin.X * 1000f), (int)MathF.Round(origin.Z * 1000f));

    private void BuildGrid()
    {
        if (hits.Count < 16) return;
        var xs = hits.Select(p => p.X).Distinct().OrderBy(x => x).ToArray();
        var gaps = new List<float>();
        for (var i = 1; i < xs.Length; i++) if (xs[i] - xs[i - 1] > 0.05f) gaps.Add(xs[i] - xs[i - 1]);
        step = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 0.5f;
        minX = hits.Min(p => p.X);
        minZ = hits.Min(p => p.Z);
        nx = (int)MathF.Round((hits.Max(p => p.X) - minX) / step) + 1;
        nz = (int)MathF.Round((hits.Max(p => p.Z) - minZ) / step) + 1;
        if (nx is <= 1 or >= 200 || nz is <= 1 or >= 200) return;
        grid = new float[nx, nz];
        var filled = new bool[nx, nz];
        foreach (var p in hits)
        {
            var ix = (int)MathF.Round((p.X - minX) / step);
            var iz = (int)MathF.Round((p.Z - minZ) / step);
            if (ix < 0 || iz < 0 || ix >= nx || iz >= nz) continue;
            if (MathF.Abs(minX + ix * step - p.X) > 0.02f || MathF.Abs(minZ + iz * step - p.Z) > 0.02f) continue;
            grid[ix, iz] = p.Y;
            filled[ix, iz] = true;
        }
        for (var ix = 0; ix < nx; ix++)
        for (var iz = 0; iz < nz; iz++)
            if (!filled[ix, iz]) grid[ix, iz] = Nearest(minX + ix * step, minZ + iz * step) ?? 0f;
    }

    private float? Nearest(float x, float z)
    {
        var best = float.MaxValue;
        float? y = null;
        foreach (var p in hits)
        {
            var d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
            if (d < best) { best = d; y = p.Y; }
        }
        return y;
    }

    private float? FieldHeight(float x, float z)
    {
        if (grid == null) return Nearest(x, z);
        var fx = Math.Clamp((x - minX) / step, 0f, nx - 1);
        var fz = Math.Clamp((z - minZ) / step, 0f, nz - 1);
        var ix = Math.Min((int)fx, nx - 2);
        var iz = Math.Min((int)fz, nz - 2);
        var tx = fx - ix;
        var tz = fz - iz;
        return (grid[ix, iz] * (1 - tx) + grid[ix + 1, iz] * tx) * (1 - tz) + (grid[ix, iz + 1] * (1 - tx) + grid[ix + 1, iz + 1] * tx) * tz;
    }

    public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        hit = default;
        if (direction.Y > -0.9f) return false; // the ragdoll only casts straight down (the live hair is off)
        if (exact.TryGetValue(Key(origin), out var r))
        {
            ExactAnswers++;
            if (!r[4].AsBool) return false;
            var point = r[5].AsVector3;
            if (point.Y > origin.Y || origin.Y - point.Y > maxDistance) return false;
            hit.Point = new Vector3(origin.X, point.Y, origin.Z);
            hit.Normal = r[6].AsVector3;
            hit.Distance = origin.Y - point.Y;
            hit.Material = r[8].AsULong;
            hit.V1 = r[9].AsVector3;
            hit.V2 = r[10].AsVector3;
            hit.V3 = r[11].AsVector3;
            return true;
        }
        FieldAnswers++;
        float? y = null;
        foreach (var set in triangleSets)
            for (var i = 0; i + 2 < set.Triangles.Length; i += 3)
                if (RayTriangle(origin, set.Triangles[i], set.Triangles[i + 1], set.Triangles[i + 2], out var d) && (y == null || origin.Y - d > y))
                    y = origin.Y - d;
        y ??= FieldHeight(origin.X, origin.Z);
        if (y is not float ground || ground > origin.Y || origin.Y - ground > maxDistance) return false;
        hit.Point = new Vector3(origin.X, ground, origin.Z);
        hit.Normal = Vector3.UnitY;
        hit.Distance = origin.Y - ground;
        return true;
    }

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
    {
        triangles.Clear();
        foreach (var set in triangleSets)
            if (set.Min == min && set.Max == max)
            {
                triangles.AddRange(set.Triangles);
                return set.Ok;
            }
        // Another box: the recorded triangles that reach into it.
        foreach (var set in triangleSets)
        {
            if (!set.Ok) continue;
            for (var i = 0; i + 2 < set.Triangles.Length; i += 3)
            {
                var lo = Vector3.Min(set.Triangles[i], Vector3.Min(set.Triangles[i + 1], set.Triangles[i + 2]));
                var hi = Vector3.Max(set.Triangles[i], Vector3.Max(set.Triangles[i + 1], set.Triangles[i + 2]));
                if (hi.X < min.X || hi.Y < min.Y || hi.Z < min.Z || lo.X > max.X || lo.Y > max.Y || lo.Z > max.Z) continue;
                triangles.Add(set.Triangles[i]);
                triangles.Add(set.Triangles[i + 1]);
                triangles.Add(set.Triangles[i + 2]);
            }
        }
        return triangles.Count >= 3;
    }

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        surfaceY = waterY;
        return !float.IsNaN(waterY) && waterY <= position.Y + searchAbove && waterY >= position.Y - searchBelow;
    }

    private static bool RayTriangle(Vector3 o, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var d = -Vector3.UnitY;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        var inv = 1f / det;
        var s = o - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-4f;
    }
}

/// <summary>The target alone: its recorded identity; its position is its skeleton's root.</summary>
internal sealed class WhatIfActors : IHostActors
{
    private readonly ReplaySource source;
    private readonly WhatIfSkeletons skeletons;
    private readonly RagdollRecord? identity;

    public WhatIfActors(ReplaySource source, WhatIfSkeletons skeletons)
    {
        this.source = source;
        this.skeletons = skeletons;
        identity = source.Peek(RecordType.Identity).Record;
    }

    public bool GameRunning => true;
    public nint LocalPlayer => source.Header[21].AsBool ? (nint)source.Activate[0].AsULong : 0;
    public bool IsAlive(nint actor, uint entityId) => true;

    public Vector3 GetPosition(nint actor, ActorSite site) => site switch
    {
        ActorSite.Activation => source.Activate[4].AsVector3,
        ActorSite.Identity => identity?[6].AsVector3 ?? default,
        _ => skeletons.Target?.RootValue.Position ?? source.Activate[4].AsVector3,
    };

    public float GetFacing(nint actor, ActorSite site) => identity?[5].AsFloat ?? 0f;
    public uint GetEntityId(nint actor) => source.Activate[5].AsUInt;
    public ulong GetGameObjectId(nint actor) => 0;
    public int GetObjectIndex(nint actor) => source.Activate[6].AsInt;

    public bool TryGetDrawScale(nint actor, out Vector3 scale)
    {
        scale = Vector3.One;
        var known = skeletons.Target;
        if (known == null || !known.DrawScaleValid) return false;
        scale = known.DrawScale;
        return true;
    }

    public void ReadAppearance(nint actor, Span<byte> customize, out int modelCharaId)
    {
        modelCharaId = 0;
        if (identity == null) return;
        identity[1].Data.AsSpan(0, Math.Min(customize.Length, identity[1].Data.Length)).CopyTo(customize);
        modelCharaId = identity[2].AsInt;
    }

    public void ReadModelType(nint actor, IHostSkeleton? skeleton, out int modelType, out int raceSexId)
    {
        // The last Identity record with a model type (the recorder asks once a skeleton with models was obtained).
        RagdollRecord? best = null;
        foreach (var index in source.IndicesOf(RecordType.Identity))
            if (source.Records[index][3].AsInt >= 0) best = source.Records[index];
        best ??= identity;
        modelType = best?[3].AsInt ?? -1;
        raceSexId = best?[4].AsInt ?? -1;
    }

    public float GetAnimationSpeed(nint actor) => source.Peek(RecordType.InitEnd).Record?[12].AsFloat ?? 1f;
    public void SetAnimationSpeed(nint actor, float speed) { }
    public void SetDrawPosition(nint actor, Vector3 position) => skeletons.MoveRoot(position);
    public bool SetApproachPosition(nint actor, Vector3 position) => false;
}

internal sealed class WhatIfColliders : IHostColliders
{
    public bool HasSelector => false;
    public bool HasMovementBlock => false;
    public bool HasExtraColliders => false;
    public void GetSelectedNpcs(List<ColliderCandidate> destination) => destination.Clear();
    public IReadOnlyList<nint> GetExtraColliders() => Array.Empty<nint>();
    public bool TryGetWeaponTransform(nint actor, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        return false;
    }
}

/// <summary>The recording's settings with the guidance profile replaced and what is not restored switched off.</summary>
internal sealed class WhatIfSettings : IHostSettingsSource
{
    private readonly IHostSettingsSource inner;
    private readonly string? libraryProfile;
    private readonly bool keepRecorded;

    public WhatIfSettings(IHostSettingsSource inner, string? libraryProfile, bool keepRecorded)
    {
        this.inner = inner;
        this.libraryProfile = libraryProfile;
        this.keepRecorded = keepRecorded;
    }

    public bool Refresh(HostSettings settings)
    {
        inner.Refresh(settings);
        if (!keepRecorded)
        {
            settings.GuidedCollapse = new GuidedCollapseOptions();
            WhatIf.SetTensionProfile(settings.GuidedCollapse, libraryProfile);
        }
        settings.RagdollMeshVolumes = false;
        settings.RagdollHairPhysics = false;
        settings.RagdollHairBodyMesh = false;
        settings.NpcCollisionActive = false;
        settings.WeaponBodyMeshContacts = false;
        settings.RagdollVerboseLog = false;
        return true;
    }
}

/// <summary>The frame clock runs on the frames' dt; every other reading is the frame's time (nothing here measures
/// real time).</summary>
internal sealed class WhatIfClock : IRagdollClock
{
    private long frameTicks = 1_000_000_000_000;
    public WhatIfClock(long frequency) => Frequency = frequency;
    public long Frequency { get; }

    public void AdvanceFrame(float dt)
    {
        var ticks = (long)Math.Round(dt * (double)Frequency);
        for (var probe = ticks - 3; probe <= ticks + 3; probe++)
            if (probe >= 0 && (float)(probe / (double)Frequency) == dt) { ticks = probe; break; }
        frameTicks += ticks;
    }

    public long GetTimestamp(ClockSite site) => frameTicks;
    public TimeSpan GetElapsedTime(ClockSite site, long since) => TimeSpan.Zero;
    public TimeSpan GetElapsedTime(ClockSite site, long since, long until) => System.Diagnostics.Stopwatch.GetElapsedTime(since, until);
    public long GetTickCount64(ClockSite site) => frameTicks * 1000 / Frequency;
}

/// <summary>Seeded by the recording's name: the same draws for every profile of one recording, in the order asked.</summary>
internal sealed class WhatIfRandom : IRagdollRandom
{
    private readonly Random random;

    public WhatIfRandom(string name)
    {
        var seed = 17;
        foreach (var c in name) seed = unchecked(seed * 31 + c);
        random = new Random(seed);
    }

    public double NextDouble(RandomSite site) => random.NextDouble();
    public int Next(RandomSite site, int maxExclusive) => random.Next(maxExclusive);
}

/// <summary>
/// Numbers of one run that only the simulation knows, read from the recording the run wrote: every fixed step's body
/// states (tick 0 is the hand-off pose). Joint angles are the turn of a body against its parent body since tick 0.
/// Written as "key=value" lines; a list is the values at 0.5 s, 1 s, 2 s, 4 s of physics and at the end.
/// </summary>
internal sealed class PhysicsNumbers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly int[] SampleTicks = { 30, 60, 120, 240 };

    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["legs"] = new[] { "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r", "j_asi_d_l", "j_asi_d_r" },
        ["torso"] = new[] { "j_sebo_a", "j_sebo_b", "j_sebo_c" },
        ["arms"] = new[] { "j_sako_l", "j_sako_r", "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_te_l", "j_te_r" },
        ["headneck"] = new[] { "j_kubi", "j_kao" },
        ["knees"] = new[] { "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r" },
        ["hips"] = new[] { "j_asi_a_l", "j_asi_a_r" },
    };

    public int Ticks, Bodies;
    private readonly Dictionary<string, List<float>> series = new();
    private readonly Dictionary<string, string> scalars = new();

    public void WriteTo(StringBuilder text)
    {
        foreach (var (key, value) in scalars) text.Append(key).Append('=').Append(value).Append('\n');
        foreach (var (key, values) in series)
            text.Append(key).Append('=').Append(string.Join(",", values.Select(v => v.ToString("R", Inv)))).Append('\n');
    }

    private sealed class Body
    {
        public string Name = "";
        public int Bone, ParentBone = -1, Handle, Parent = -1;
        public float Mass = 1f, Radius = 0.05f, HalfLength = 0.05f;
        public Quaternion StartRelative = Quaternion.Identity, StartOrientation = Quaternion.Identity;
        public Vector3 StartPosition;
        public bool HasStart, Main;
    }

    public static PhysicsNumbers Read(string recording)
    {
        var n = new PhysicsNumbers();
        var bodies = new List<Body>();
        var byHandle = new Dictionary<int, Body>();
        short[] parents = Array.Empty<short>();
        ulong actor = 0;
        var samples = new Dictionary<string, List<float>>();
        void Sample(string key, float value)
        {
            if (!samples.TryGetValue(key, out var list)) samples[key] = list = new List<float>();
            list.Add(value);
        }
        float lastEnergy = 0f, maxAngular = 0f, lateMaxAngular = 0f, lateMaxEnergy = 0f, maxEnergy = 0f, maxLinear = 0f;
        int spikes = 0, lateSpikes = 0, ticks = 0, restTick = -1, asleepTick = -1, lastTick = 0, firstSpike = -1, lastSpike = -1;
        var pelvisVelocity = Vector3.Zero;
        var lastState = Array.Empty<float>();
        int[] lastHandles = Array.Empty<int>();
        using var reader = RagdollRecordingReader.Open(recording);
        foreach (var r in reader.ReadAll())
        {
            switch (r.Type)
            {
                case RecordType.Activate:
                    actor = r[0].AsULong;
                    break;
                case RecordType.SkeletonTopology when r[0].AsULong == actor && r[1].AsInt == 0:
                {
                    var names = r[6].Count;
                    var p = r[7 + names];
                    parents = new short[p.Count];
                    for (var i = 0; i < parents.Length; i++) parents[i] = (short)p.IntAt(i);
                    break;
                }
                case RecordType.RigBodies:
                {
                    bodies.Clear();
                    byHandle.Clear();
                    var count = r[0].AsInt;
                    var at = 1;
                    for (var i = 0; i < count; i++)
                    {
                        var body = new Body { Name = r[at].AsString ?? "", Bone = r[at + 1].AsInt, Handle = r[at + 2].AsInt };
                        var pairs = r[at + 3].Count;
                        for (var q = 0; q + 1 < pairs; q += 2)
                        {
                            var value = r[at + 4 + q + 1];
                            switch (r[at + 4 + q].AsString)
                            {
                                case "Mass": body.Mass = value.AsFloat; break;
                                case "CapsuleRadius": body.Radius = value.AsFloat; break;
                                case "CapsuleHalfLength": body.HalfLength = value.AsFloat; break;
                                case "ParentBoneIndex": body.ParentBone = value.AsInt; break;
                            }
                        }
                        at += 4 + pairs;
                        body.Main = body.Name == "j_kosi" || Groups.Values.Any(g => Array.IndexOf(g, body.Name) >= 0);
                        bodies.Add(body);
                        byHandle[body.Handle] = body;
                    }
                    // The parent body: the nearest ancestor bone that has one.
                    var ofBone = bodies.GroupBy(b => b.Bone).ToDictionary(g => g.Key, g => bodies.IndexOf(g.First()));
                    foreach (var body in bodies)
                    {
                        var bone = body.Bone >= 0 && body.Bone < parents.Length ? parents[body.Bone] : (short)-1;
                        for (var guard = 0; bone >= 0 && guard < 200; guard++)
                        {
                            if (ofBone.TryGetValue(bone, out var parent)) { body.Parent = parent; break; }
                            bone = bone < parents.Length ? parents[bone] : (short)-1;
                        }
                    }
                    break;
                }
                case RecordType.FrameEnd:
                    if (r[2].AsBool && restTick < 0) restTick = r[5].AsInt;
                    if (!r[2].AsBool) restTick = -1;
                    break;
                case RecordType.BodyStates:
                {
                    var tick = r[0].AsInt;
                    var handles = r[4];
                    var flags = r[5].Data;
                    var state = r[6];
                    var count = handles.Count;
                    var pose = new Dictionary<int, (Vector3 P, Quaternion Q, Vector3 V, Vector3 W)>(count);
                    var awake = 0;
                    var energy = 0f;
                    for (var i = 0; i < count; i++)
                    {
                        var handle = handles.IntAt(i);
                        if (!byHandle.TryGetValue(handle, out var body)) continue;
                        var at = i * RagdollRecordingFormat.BodyStateFloats;
                        var p = new Vector3(state.FloatAt(at), state.FloatAt(at + 1), state.FloatAt(at + 2));
                        var q = new Quaternion(state.FloatAt(at + 3), state.FloatAt(at + 4), state.FloatAt(at + 5), state.FloatAt(at + 6));
                        var v = new Vector3(state.FloatAt(at + 7), state.FloatAt(at + 8), state.FloatAt(at + 9));
                        var w = new Vector3(state.FloatAt(at + 10), state.FloatAt(at + 11), state.FloatAt(at + 12));
                        pose[handle] = (p, q, v, w);
                        if (i < flags.Length && (flags[i] & 1) != 0 && (flags[i] & 2) == 0) awake++;
                        // Energy and speeds over the skeleton's own bodies (pelvis, spine, head, arms, legs): soft tissue and
                        // garments hang on servos and would drown them.
                        if (!body.Main || (i < flags.Length && (flags[i] & 2) != 0) || !float.IsFinite(v.LengthSquared() + w.LengthSquared())) continue;
                        // Rotation with the inertia of a solid cylinder across its axis (the bodies are capsules, boxes and hulls).
                        var inertia = body.Mass * (body.Radius * body.Radius / 4f + body.HalfLength * body.HalfLength / 3f);
                        energy += 0.5f * body.Mass * v.LengthSquared() + 0.5f * inertia * w.LengthSquared();
                        maxAngular = MathF.Max(maxAngular, w.Length());
                        maxLinear = MathF.Max(maxLinear, v.Length());
                        if (tick > 240) lateMaxAngular = MathF.Max(lateMaxAngular, w.Length());
                    }
                    if (tick == 0)
                    {
                        foreach (var body in bodies)
                        {
                            if (!pose.TryGetValue(body.Handle, out var own)) continue;
                            body.StartPosition = own.P;
                            body.StartOrientation = own.Q;
                            body.HasStart = true;
                            if (body.Parent >= 0 && pose.TryGetValue(bodies[body.Parent].Handle, out var parent))
                                body.StartRelative = Quaternion.Normalize(Quaternion.Conjugate(parent.Q) * own.Q);
                            if (body.Name == "j_kosi") pelvisVelocity = own.V;
                        }
                        n.Bodies = bodies.Count;
                    }
                    else
                    {
                        // An energy spike: the rig's kinetic energy jumps up between two fixed steps.
                        if (tick > 30 && energy - lastEnergy > MathF.Max(0.05f, 0.25f * lastEnergy) && firstSpike < 0) firstSpike = tick;
                        if (tick > 30 && energy - lastEnergy > MathF.Max(0.05f, 0.25f * lastEnergy))
                        {
                            spikes++;
                            lastSpike = tick;
                            if (tick > 240) lateSpikes++;
                        }
                        if (tick > 240) lateMaxEnergy = MathF.Max(lateMaxEnergy, energy);
                    }
                    maxEnergy = MathF.Max(maxEnergy, energy);
                    lastEnergy = energy;
                    if (awake == 0 && asleepTick < 0 && tick > 0) asleepTick = tick;
                    if (awake > 0) asleepTick = -1;
                    ticks = Math.Max(ticks, tick);
                    lastTick = tick;
                    if (Array.IndexOf(SampleTicks, tick) >= 0) Measure(bodies, pose, Sample);
                    lastHandles = new int[count];
                    for (var i = 0; i < count; i++) lastHandles[i] = handles.IntAt(i);
                    lastState = new float[count * RagdollRecordingFormat.BodyStateFloats];
                    for (var i = 0; i < lastState.Length; i++) lastState[i] = state.FloatAt(i);
                    break;
                }
            }
        }
        // Samples a run was too short for repeat its last state; the last entry is always the end.
        var final = new Dictionary<int, (Vector3 P, Quaternion Q, Vector3 V, Vector3 W)>();
        for (var i = 0; i < lastHandles.Length; i++)
        {
            var at = i * RagdollRecordingFormat.BodyStateFloats;
            final[lastHandles[i]] = (new Vector3(lastState[at], lastState[at + 1], lastState[at + 2]),
                new Quaternion(lastState[at + 3], lastState[at + 4], lastState[at + 5], lastState[at + 6]),
                new Vector3(lastState[at + 7], lastState[at + 8], lastState[at + 9]),
                new Vector3(lastState[at + 10], lastState[at + 11], lastState[at + 12]));
        }
        var taken = samples.Count > 0 ? samples.Values.First().Count : 0;
        for (var i = taken; i <= SampleTicks.Length; i++) Measure(bodies, final, Sample);
        n.Ticks = ticks;
        foreach (var (key, list) in samples) n.series[key] = list;
        n.scalars["ticks"] = ticks.ToString(Inv);
        n.scalars["bodies"] = n.Bodies.ToString(Inv);
        n.scalars["energySpikes"] = spikes.ToString(Inv);
        n.scalars["energySpikesLate"] = lateSpikes.ToString(Inv);
        n.scalars["firstSpikeTick"] = firstSpike.ToString(Inv);
        n.scalars["lastSpikeTick"] = lastSpike.ToString(Inv);
        n.scalars["maxEnergy"] = maxEnergy.ToString("R", Inv);
        n.scalars["maxEnergyLate"] = lateMaxEnergy.ToString("R", Inv);
        n.scalars["maxAngularSpeed"] = maxAngular.ToString("R", Inv);
        n.scalars["maxAngularSpeedLate"] = lateMaxAngular.ToString("R", Inv);
        n.scalars["maxLinearSpeed"] = maxLinear.ToString("R", Inv);
        n.scalars["restTick"] = restTick.ToString(Inv);
        n.scalars["asleepTick"] = asleepTick.ToString(Inv);
        n.scalars["pelvisVelocity"] = string.Join(",", new[] { pelvisVelocity.X, pelvisVelocity.Y, pelvisVelocity.Z }.Select(v => v.ToString("R", Inv)));
        return n;
    }

    private static void Measure(List<Body> bodies, Dictionary<int, (Vector3 P, Quaternion Q, Vector3 V, Vector3 W)> pose, Action<string, float> sample)
    {
        float Turn(Quaternion from, Quaternion to)
        {
            var d = Quaternion.Normalize(Quaternion.Conjugate(from) * to);
            return 2f * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * 180f / MathF.PI;
        }
        var deviation = new Dictionary<string, float>();
        foreach (var body in bodies)
        {
            if (!body.HasStart || body.Parent < 0 || !pose.TryGetValue(body.Handle, out var own) ||
                !pose.TryGetValue(bodies[body.Parent].Handle, out var parent)) continue;
            deviation[body.Name] = Turn(body.StartRelative, Quaternion.Normalize(Quaternion.Conjugate(parent.Q) * own.Q));
        }
        foreach (var (group, names) in Groups)
        {
            var values = names.Where(deviation.ContainsKey).Select(name => deviation[name]).ToArray();
            sample(group + "DevMax", values.Length > 0 ? values.Max() : float.NaN);
            sample(group + "DevMean", values.Length > 0 ? values.Average() : float.NaN);
        }
        // The lower legs as bodies: how far they moved and turned in the world.
        float move = float.NaN, turn = float.NaN;
        foreach (var body in bodies)
        {
            if (body.Name is not ("j_asi_c_l" or "j_asi_c_r") || !body.HasStart || !pose.TryGetValue(body.Handle, out var own)) continue;
            var m = Vector3.Distance(own.P, body.StartPosition);
            var t = Turn(body.StartOrientation, own.Q);
            move = float.IsNaN(move) ? m : MathF.Max(move, m);
            turn = float.IsNaN(turn) ? t : MathF.Max(turn, t);
        }
        sample("calfMove", move);
        sample("calfTurn", turn);
    }
}
