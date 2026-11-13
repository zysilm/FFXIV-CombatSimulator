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
    float Arg(int i, float fallback) => args.Length > i ? float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) : fallback;
    var studySettings = new NavMeshBuild.Settings { TileCells = (int)Arg(1, 64), AgentClimb = Arg(2, 0.6f), CellSize = Arg(3, 0.3f), CellHeight = Arg(4, 0.2f), AgentRadius = Arg(5, 0.4f), MaxSlopeDegrees = Arg(6, 55f) };
    Console.WriteLine($"settings: {studySettings}");
    var studied = NavMeshBuild.Build(input, studySettings, threads: Environment.ProcessorCount);
    Console.WriteLine($"{input.Count / 3:N0} triangles around {center}; built in {studyClock.ElapsedMilliseconds} ms");
    if (studied == null) { Console.WriteLine("nothing walkable"); return 1; }
    Console.WriteLine("links: " + NavMeshBuild.DescribeLinks(studied));
    var studyQuery = new DtNavMeshQuery(studied);
    studyQuery.FindNearestPoly(NavMeshBuild.ToRc(center), new RcVec3f(1.5f, 2.5f, 1.5f), new DtQueryDefaultFilter(), out var seedRef, out var seedPt, out _);
    Console.WriteLine($"seed under the build centre: {seedRef} at {NavMeshBuild.ToVector(seedPt)}");
    // Connected pieces, largest first, with their extents.
    var pieceOf = new Dictionary<long, int>();
    var pieces = new List<(int Size, Vector3 Min, Vector3 Max)>();
    for (var ti = 0; ti < studied.GetMaxTiles(); ti++)
    {
        var tile = studied.GetTile(ti);
        if (tile?.data?.header == null) continue;
        var baseRef = studied.GetPolyRefBase(tile);
        for (var p = 0; p < tile.data.header.polyCount; p++)
        {
            var start = baseRef | (long)p;
            if (pieceOf.ContainsKey(start)) continue;
            var id = pieces.Count; var size = 0; var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            var open = new Queue<long>(); open.Enqueue(start); pieceOf[start] = id;
            while (open.Count > 0)
            {
                var cur = open.Dequeue(); size++;
                var c = NavMeshBuild.ToVector(studied.GetPolyCenter(cur)); mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c);
                studied.GetTileAndPolyByRefUnsafe(cur, out var ct, out var cp);
                for (var l = cp.firstLink; l != DtDetour.DT_NULL_LINK; l = ct.links[l].next)
                {
                    var n = ct.links[l].refs;
                    if (n != 0 && pieceOf.TryAdd(n, id)) open.Enqueue(n);
                }
            }
            pieces.Add((size, mn, mx));
        }
    }
    Console.WriteLine($"{pieces.Count} connected pieces; the seed is in piece {(pieceOf.TryGetValue(seedRef, out var sp) ? sp : -1)}");
    foreach (var (piece, idx) in pieces.Select((x, i) => (x, i)).OrderByDescending(x => x.x.Size).Take(8))
        Console.WriteLine($"  piece {idx}: {piece.Size} polygons, centres from {piece.Min:F0} to {piece.Max:F0}");
    if (pieceOf.TryGetValue(seedRef, out var seedPiece))
        Console.WriteLine($"  seed piece {seedPiece}: {pieces[seedPiece].Size} polygons, from {pieces[seedPiece].Min:F0} to {pieces[seedPiece].Max:F0}");
    // A top-down picture (BMP, 2 px a metre): mesh pieces coloured, the seed's piece red, steep
    // collision (walls) black, the build centre a white cross.
    {
        const int size = 384; const float scale = 2f;
        var pixels = new byte[size * size * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 235;
        (int, int) Px(Vector3 w) => ((int)((w.X - center.X) * scale + size / 2), (int)((w.Z - center.Z) * scale + size / 2));
        void Put(int x, int y, byte r, byte g, byte b) { if (x < 0 || y < 0 || x >= size || y >= size) return; var o = ((size - 1 - y) * size + x) * 3; pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; }
        void FillTri(Vector3 a3, Vector3 b3, Vector3 c3, byte r, byte g, byte b)
        {
            var (ax, ay) = Px(a3); var (bx, by) = Px(b3); var (cx, cy) = Px(c3);
            int minX = Math.Max(0, Math.Min(ax, Math.Min(bx, cx))), maxX = Math.Min(size - 1, Math.Max(ax, Math.Max(bx, cx)));
            int minY = Math.Max(0, Math.Min(ay, Math.Min(by, cy))), maxY = Math.Min(size - 1, Math.Max(ay, Math.Max(by, cy)));
            float Edge(int x0, int y0, int x1, int y1, int x, int y) => (x1 - x0) * (y - y0) - (y1 - y0) * (x - x0);
            var area = Edge(ax, ay, bx, by, cx, cy); if (MathF.Abs(area) < 0.5f) return;
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
            {
                var w0 = Edge(bx, by, cx, cy, x, y) / area; var w1 = Edge(cx, cy, ax, ay, x, y) / area; var w2 = 1 - w0 - w1;
                if (w0 >= 0 && w1 >= 0 && w2 >= 0) Put(x, y, r, g, b);
            }
        }
        for (var ti = 0; ti < studied.GetMaxTiles(); ti++)
        {
            var tile = studied.GetTile(ti); var data = tile?.data;
            if (data?.header == null) continue;
            var baseRef = studied.GetPolyRefBase(tile);
            for (var p = 0; p < data.header.polyCount; p++)
            {
                var poly = data.polys[p];
                var piece = pieceOf.TryGetValue(baseRef | (long)p, out var pc) ? pc : 0;
                byte r = (byte)(80 + piece * 53 % 150), g = (byte)(90 + piece * 97 % 150), b = (byte)(100 + piece * 31 % 150);
                if (piece == seedPiece) { r = 230; g = 30; b = 30; }
                Vector3 V(int k) { var v = poly.verts[k] * 3; return new Vector3(data.verts[v], data.verts[v + 1], data.verts[v + 2]); }
                for (var k = 2; k < poly.vertCount; k++) FillTri(V(0), V(k - 1), V(k), r, g, b);
            }
        }
        for (var i = 0; i + 2 < input.Count; i += 3)
        {
            var n = Vector3.Cross(input[i + 1] - input[i], input[i + 2] - input[i]);
            if (n.LengthSquared() < 1e-8f) continue;
            n = Vector3.Normalize(n);
            if (MathF.Abs(n.Y) > 0.5f) continue; // only steep faces
            var lo = MathF.Min(input[i].Y, MathF.Min(input[i + 1].Y, input[i + 2].Y));
            if (lo > center.Y + 25f || lo < center.Y - 25f) continue;
            foreach (var (pa, pb) in new[] { (input[i], input[i + 1]), (input[i + 1], input[i + 2]), (input[i + 2], input[i]) })
            {
                var (x0, y0) = Px(pa); var (x1, y1) = Px(pb);
                var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
                for (var t = 0; t <= steps; t++) Put(x0 + (x1 - x0) * t / steps, y0 + (y1 - y0) * t / steps, 0, 0, 0);
            }
        }
        for (var d = -6; d <= 6; d++) { Put(size / 2 + d, size / 2, 255, 255, 255); Put(size / 2, size / 2 + d, 255, 255, 255); }
        var file = System.IO.Path.ChangeExtension(args[0], ".bmp");
        using var bmp = new System.IO.BinaryWriter(System.IO.File.Create(file));
        bmp.Write((byte)'B'); bmp.Write((byte)'M'); bmp.Write(54 + pixels.Length); bmp.Write(0); bmp.Write(54);
        bmp.Write(40); bmp.Write(size); bmp.Write(size); bmp.Write((short)1); bmp.Write((short)24); bmp.Write(0); bmp.Write(pixels.Length); bmp.Write(2835); bmp.Write(2835); bmp.Write(0); bmp.Write(0);
        bmp.Write(pixels);
        Console.WriteLine($"picture: {file}");
    }
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
