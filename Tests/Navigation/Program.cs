using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Navigation;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Crowd;

// Offline checks of the navigation mesh (DotRecast) on a scene built to reproduce the reported
// failures: open ground, a house with a door and a walkable flat roof, a ramp, and a raised island
// that cannot be reached. Run: dotnet run -c Release --project Tests/Navigation

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

// Study mode: rebuild the mesh from an input saved in game (navmesh-last-input.bin in the plugin's
// configuration folder) and report how it is connected.
if (args.Length > 0 && args[0].EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
{
    var input = NavMeshBuild.LoadTriangles(args[0], out var center);
    var studyClock = Stopwatch.StartNew();
    var studied = NavMeshBuild.Build(input, new NavMeshBuild.Settings(), threads: Environment.ProcessorCount);
    Console.WriteLine($"{input.Count / 3:N0} triangles around {center}; built in {studyClock.ElapsedMilliseconds} ms");
    if (studied == null) { Console.WriteLine("nothing walkable"); return 1; }
    Console.WriteLine("links: " + NavMeshBuild.DescribeLinks(studied));
    var studyQuery = new DtNavMeshQuery(studied);
    studyQuery.FindNearestPoly(NavMeshBuild.ToRc(center), new RcVec3f(1.5f, 2.5f, 1.5f), new DtQueryDefaultFilter(), out var seedRef, out var seedPt, out _);
    Console.WriteLine($"seed under the build centre: {seedRef} at {NavMeshBuild.ToVector(seedPt)}");
    var cutOff = NavMeshBuild.MarkUnreachable(studied, seedRef);
    Console.WriteLine($"cut off from the seed: {cutOff}");
    return 0;
}

var tris = new List<Vector3>();
void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { tris.AddRange(new[] { a, b, c, a, c, d }); }
void Box(Vector3 min, Vector3 max)
{
    var (x0, y0, z0, x1, y1, z1) = (min.X, min.Y, min.Z, max.X, max.Y, max.Z);
    Quad(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0)); // top
    Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1)); // bottom
    Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
    Quad(new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1));
    Quad(new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0));
    Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
}

// Ground 80 × 80 at y = 0.
Quad(new(-40, 0, -40), new(-40, 0, 40), new(40, 0, 40), new(40, 0, -40));
// A house 10 × 10, walls 4 m, a 2 m door in the south wall, a flat roof on top.
Box(new(-5, 0, 4.7f), new(5, 4, 5));        // north wall
Box(new(-5, 0, -5), new(-4.7f, 4, 5));      // west wall
Box(new(4.7f, 0, -5), new(5, 4, 5));        // east wall
Box(new(-5, 0, -5), new(-1, 4, -4.7f));     // south wall, left of the door
Box(new(1, 0, -5), new(5, 4, -4.7f));       // south wall, right of the door
Box(new(-1, 2.6f, -5), new(1, 4, -4.7f));   // lintel over the door
Box(new(-5.2f, 4, -5.2f), new(5.2f, 4.3f, 5.2f)); // roof slab
// A ramp to a 3 m terrace east of the house.
Quad(new(10, 0, -4), new(10, 0, 4), new(20, 3, 4), new(20, 3, -4));
Box(new(20, 0, -4), new(30, 3, 4));
// An island raised 10 m, unreachable (sheer sides).
Box(new(-30, 0, 20), new(-20, 10, 30));

var sw = Stopwatch.StartNew();
var tileCells = args.Length > 0 ? int.Parse(args[0]) : 64;
var mesh = NavMeshBuild.Build(tris, new NavMeshBuild.Settings { TileCells = tileCells }, threads: Environment.ProcessorCount);
Console.WriteLine($"built {tris.Count / 3} triangles in {sw.ElapsedMilliseconds} ms");
Check(mesh != null, "no mesh");
var query = new DtNavMeshQuery(mesh!);
var filter = new DtQueryDefaultFilter();

// Snap a point to the walkable floor with a small vertical search (±1.5 m around its own height).
(long Ref, Vector3 Pos) Snap(Vector3 p, float vertical = 1.5f)
{
    query.FindNearestPoly(NavMeshBuild.ToRc(p), new RcVec3f(2f, vertical, 2f), filter, out var r, out var pt, out _);
    return (r, NavMeshBuild.ToVector(pt));
}

var cases = new (string, Action)[]
{
    ("neighbouring tiles are linked: a long path over open ground reaches its end", () => {
        var a = Snap(new Vector3(-35, 0, -35)); var b = Snap(new Vector3(35, 0, -30));
        Span<long> path = stackalloc long[512];
        query.FindPath(a.Ref, b.Ref, NavMeshBuild.ToRc(a.Pos), NavMeshBuild.ToRc(b.Pos), filter, path, out var n, 512);
        Console.WriteLine($"      {n} polys, ends at the goal: {n > 0 && path[n - 1] == b.Ref}");
        Check(n > 0 && path[n - 1] == b.Ref, "the path stopped at a tile edge");
    }),
    ("inside the house the floor is found, never the roof", () => {
        var inside = Snap(new Vector3(0, 0.3f, 0));
        Console.WriteLine($"      inside at y 0.3 -> poly {inside.Ref}, y {inside.Pos.Y:F2}");
        Check(inside.Ref != 0 && MathF.Abs(inside.Pos.Y) < 0.4f, "inside the house did not snap to the floor");
        var roof = Snap(new Vector3(0, 4.5f, 0));
        Console.WriteLine($"      on the roof at y 4.5 -> poly {roof.Ref}, y {roof.Pos.Y:F2}");
        Check(roof.Ref != 0 && roof.Pos.Y > 4f, "the roof surface is its own layer");
    }),
    ("a path into the house goes through the door, and stays on the floor", () => {
        var from = Snap(new Vector3(0, 0, -15)); var to = Snap(new Vector3(0, 0, 2));
        Span<long> path = stackalloc long[256];
        query.FindPath(from.Ref, to.Ref, NavMeshBuild.ToRc(from.Pos), NavMeshBuild.ToRc(to.Pos), filter, path, out var n, 256);
        Span<DtStraightPath> straight = stackalloc DtStraightPath[64];
        query.FindStraightPath(NavMeshBuild.ToRc(from.Pos), NavMeshBuild.ToRc(to.Pos), path, n, straight, out var sn, 64, 0);
        var maxY = 0f; var throughDoor = false;
        for (var i = 0; i < sn; i++)
        {
            var p = NavMeshBuild.ToVector(straight[i].pos);
            maxY = MathF.Max(maxY, p.Y);
            if (MathF.Abs(p.Z + 4.85f) < 1.5f && MathF.Abs(p.X) < 1f) throughDoor = true;
            Console.WriteLine($"      corner {i}: {p.X:F1}, {p.Y:F1}, {p.Z:F1}");
        }
        Check(n > 0 && sn >= 2 && maxY < 0.5f, "the path left the floor");
        Check(sn == 2 || throughDoor, "the path did not go through the door");
    }),
    ("the ramp and terrace are walkable; the raised island is a separate layer", () => {
        var terrace = Snap(new Vector3(25, 3.2f, 0));
        var island = Snap(new Vector3(-25, 10.2f, 25));
        Check(terrace.Ref != 0 && MathF.Abs(terrace.Pos.Y - 3f) < 0.4f, "terrace");
        var start = Snap(new Vector3(0, 0, -15));
        Span<long> path = stackalloc long[256];
        query.FindPath(start.Ref, terrace.Ref, NavMeshBuild.ToRc(start.Pos), NavMeshBuild.ToRc(terrace.Pos), filter, path, out var n1, 256);
        var terraceReached = n1 > 0 && path[n1 - 1] == terrace.Ref;
        query.FindPath(start.Ref, island.Ref, NavMeshBuild.ToRc(start.Pos), NavMeshBuild.ToRc(island.Pos), filter, path, out var n2, 256);
        var last = n2 > 0 ? path[n2 - 1] : 0;
        var ramp = Snap(new Vector3(15, 1.5f, 0));
        query.FindPath(start.Ref, ramp.Ref, NavMeshBuild.ToRc(start.Pos), NavMeshBuild.ToRc(ramp.Pos), filter, path, out var nr, 256);
        Console.WriteLine($"      to the ramp: {nr} polys, reached {nr > 0 && path[nr - 1] == ramp.Ref}, last centre {NavMeshBuild.ToVector(mesh!.GetPolyCenter(path[Math.Max(0, nr - 1)]))}");
        for (var x = 8f; x <= 22f; x += 1f) { var q = Snap(new Vector3(x, 0.3f * (x - 10f) + 0.3f, 0), 3f); Console.WriteLine($"        x {x}: poly {q.Ref % 100000} y {q.Pos.Y:F2}"); }
        Console.WriteLine($"      terrace poly {terrace.Ref} at {terrace.Pos}, ramp poly {ramp.Ref} at {ramp.Pos}; terrace reached {terraceReached}; island reachable: {island.Ref != 0 && last == island.Ref}");
        Check(terraceReached, "the terrace is not reachable over the ramp");
        Check(island.Ref == 0 || last != island.Ref, "the sheer island was reachable");
    }),
    ("a crowd of 50 closes on a target, stops, and does not orbit; and it is cheap", () => {
        // Run once to warm .NET up (it re-compiles hot code during the first seconds, and longer when
        // the machine is busy), then measure a second, identical run: the steady cost.
        RunCrowd(report: false);
        RunCrowd(report: true);
    }),
    // Last: it changes the mesh's flags.
    ("only what can be walked to from where the player stands is kept: roof and island are cut off", () => {
        CutOff();
    }),
};

void RunCrowd(bool report)
{
        var crowd = new DtCrowd(new DtCrowdConfig(0.6f), mesh!);
        var agentParams = new DtCrowdAgentParams
        {
            radius = 0.45f, height = 2f, maxAcceleration = 20f, maxSpeed = 6f,
            collisionQueryRange = 4f, pathOptimizationRange = 12f, separationWeight = 1.5f,
            updateFlags = DtCrowdAgentUpdateFlags.DT_CROWD_ANTICIPATE_TURNS | DtCrowdAgentUpdateFlags.DT_CROWD_OBSTACLE_AVOIDANCE |
                          DtCrowdAgentUpdateFlags.DT_CROWD_SEPARATION | DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_VIS |
                          DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_TOPO,
            obstacleAvoidanceType = 2,
        };
        var random = new Random(4);
        var agents = new List<(DtCrowdAgent Agent, Vector3 Goal)>();
        var target = new Vector3(-15, 0, -15);
        for (var i = 0; i < 50; i++)
        {
            var start = Snap(new Vector3(random.Next(-35, 35), 0, random.Next(-35, -12)));
            var agent = crowd.AddAgent(NavMeshBuild.ToRc(start.Pos), agentParams);
            // Each its own spot around the target, fixed in world directions, on rings 1.2 m apart with
            // spots 1.2 m apart along them (the agents are 0.9 m wide).
            var (ring, slot, perRing) = (0, i, 0);
            while (true) { perRing = (int)(MathF.Tau * (2f + ring * 1.2f) / 1.2f); if (slot < perRing) break; slot -= perRing; ring++; }
            var angle = slot * MathF.Tau / perRing;
            var ringRadius = 2f + ring * 1.2f;
            var goal = Snap(target + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * ringRadius);
            crowd.RequestMoveTarget(agent, goal.Ref, NavMeshBuild.ToRc(goal.Pos));
            agents.Add((agent, goal.Pos));
        }

        var dt = 1f / 60f; var updateTicks = 0L; var frames = 0; var worst = 0.0; var firstMs = 0.0; var openingWorst = 0.0; var spikes = 0; var spikesWithGc = 0; var gcAt = GC.CollectionCount(0); var gcStart = gcAt; var allocStart = GC.GetAllocatedBytesForCurrentThread();
        var lastPositions = agents.Select(a => NavMeshBuild.ToVector(a.Agent.npos)).ToArray();
        float travelledLastSecond = 0f; var travelled = new float[agents.Count]; var arrived = new bool[agents.Count];
        for (var f = 0; f < 60 * 25; f++)
        {
            var t0 = Stopwatch.GetTimestamp();
            crowd.Update(dt, null);
            var ticks = Stopwatch.GetTimestamp() - t0;
            updateTicks += ticks; frames++;
            if (f == 0) firstMs = ticks * 1000.0 / Stopwatch.Frequency;
            else if (f < 60) openingWorst = Math.Max(openingWorst, ticks * 1000.0 / Stopwatch.Frequency);
            else { worst = Math.Max(worst, ticks * 1000.0 / Stopwatch.Frequency); if (ticks * 1000.0 / Stopwatch.Frequency > 3.0) { spikes++; if (GC.CollectionCount(0) != gcAt) spikesWithGc++; } }
            gcAt = GC.CollectionCount(0);
            // Arrived: stop asking to move.
            for (var i = 0; i < agents.Count; i++)
            {
                var (agent, goal) = agents[i];
                if (agent.targetState == DtMoveRequestState.DT_CROWDAGENT_TARGET_VALID &&
                    Vector2.Distance(new(agent.npos.X, agent.npos.Z), new(goal.X, goal.Z)) < 0.5f)
                {
                    crowd.ResetMoveTarget(agent);
                    arrived[i] = true;
                }
            }
            if (f >= 60 * 24)
                for (var i = 0; i < agents.Count; i++)
                {
                    var p = NavMeshBuild.ToVector(agents[i].Agent.npos);
                    travelledLastSecond += Vector3.Distance(p, lastPositions[i]);
                    travelled[i] += Vector3.Distance(p, lastPositions[i]);
                    lastPositions[i] = p;
                }
            else
                for (var i = 0; i < agents.Count; i++) lastPositions[i] = NavMeshBuild.ToVector(agents[i].Agent.npos);
        }
        if (!report) return;
        Console.WriteLine($"      updates over 3 ms after the first second: {spikes} of {frames - 60}, {spikesWithGc} of them during a GC; {GC.CollectionCount(0) - gcStart} gen-0 GCs; {(GC.GetAllocatedBytesForCurrentThread() - allocStart) / frames / 1024.0:F1} KB allocated per update");
        var meanMs = updateTicks * 1000.0 / Stopwatch.Frequency / frames;
        var near = agents.Count(a => Vector2.Distance(new(a.Agent.npos.X, a.Agent.npos.Z), new(target.X, target.Z)) < 8f);
        var maxY = agents.Max(a => MathF.Abs(a.Agent.npos.Y));
        Console.WriteLine($"      crowd update: mean {meanMs:F3} ms, worst {worst:F2} ms after the first second (first update {firstMs:F1} ms, JIT; rest of the first second {openingWorst:F2} ms) for 50 agents; {near}/50 within 8 m of the target after 25 s; " +
                          $"all of them moved {travelledLastSecond:F2} m in the last second; highest y {maxY:F2}");
        var settled = Enumerable.Range(0, agents.Count).Where(i => arrived[i]).Select(i => travelled[i]).DefaultIfEmpty(0f).Max();
        Console.WriteLine($"      {arrived.Count(a => a)}/50 reached their own spot (the rest stand behind those already there); in the last second the busiest agent moved {travelled.Max():F2} m, an arrived one {settled:F2} m");
        Check(near == 50, "the crowd did not close on the target");
        Check(settled < 0.1f, "an agent moves again after arriving");
        Check(travelled.Max() < 0.6f, "an agent keeps walking (orbiting) once the crowd has gathered");
        Check(maxY < 0.5f, "an agent left the ground floor");
        Check(meanMs < 2.0 && spikes <= (frames - 60) / 50, "the crowd update is too slow");
}

void CutOff()
{
        var player = Snap(new Vector3(0, 0, -15));
        var roof = Snap(new Vector3(0, 4.5f, 0)); var island = Snap(new Vector3(-25, 10.2f, 25));
        var cut = NavMeshBuild.MarkUnreachable(mesh!, player.Ref);
        var floor = Snap(new Vector3(0, 0.3f, 0)); var terrace = Snap(new Vector3(25, 3.2f, 0));
        var roofNow = Snap(new Vector3(0, 4.5f, 0), 0.6f); var islandNow = Snap(new Vector3(-25, 10.2f, 25), 0.6f);
        Console.WriteLine($"      {cut} polygons cut off; house floor kept {floor.Ref != 0}, terrace kept {terrace.Ref != 0}; roof found now {roofNow.Ref != 0}, island found now {islandNow.Ref != 0}");
        Check(roof.Ref != 0 && island.Ref != 0, "the roof or island had no mesh to cut");
        Check(floor.Ref != 0 && terrace.Ref != 0, "a reachable place was cut off");
        Check(roofNow.Ref == 0 && islandNow.Ref == 0, "an unreachable surface is still offered");
}

var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;
