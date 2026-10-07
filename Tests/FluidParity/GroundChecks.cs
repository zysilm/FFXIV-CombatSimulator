using System.Numerics;
using System.Reflection;
using CombatSimulator.Effects.BodyFluids;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Rendering.WorldGeometry;

static class GroundChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static SurfaceFilm Film(GroundFilmRuntime ground) => (SurfaceFilm)typeof(GroundFilmRuntime)
        .GetField("film", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ground)!;
    static double WetArea(GroundFilmRuntime ground)
    {
        var film = Film(ground); double area = 0;
        for (int i = 0; i < film.CellCount; i++)
            if (film.GetCell(i).Thickness > 2e-6) area += film.GetCell(i).Geometry.Area;
        return area;
    }
    public static void Run()
    {
        FlatSpreading(); SlopedSpreading(); PendingSupport();
    }
    static void FlatSpreading()
    {
        var ground = new GroundFilmRuntime(); ground.SetMaterial(.4); ground.BeginStep();
        double accepted = ground.AddContact(new(.003f, 0, .003f), Vector3.UnitY,
            new(-1, 0, -1), new(0, 0, 1), new(1, 0, -1), 2e-8);
        Require(accepted > 0, "Ground impact was not admitted");
        double initialArea = WetArea(ground); int initialCells = ground.CellCount;
        for (int frame = 0; frame < 600; frame++)
        {
            ground.BeginStep(); int cellsBefore = ground.CellCount;
            ground.Advance(1d / 60, new(0, -9.81f, 0));
            Require(ground.CellCount - cellsBefore <= 8, "Cached frontier exceeded per-step cell budget");
            Require(ground.ProbeCalls <= 4 && ground.CellCount <= 768, "Ground work exceeded fixed pool/query budget");
            Require(Math.Abs(ground.Volume - accepted) < 1e-18, "Spreading lost admitted liquid inventory");
        }
        double finalArea = WetArea(ground);
        Require(finalArea > initialArea * 1.1 && ground.CellCount > initialCells,
            $"Ground puddle did not spread: wet area {initialArea:E4} -> {finalArea:E4}, cells {initialCells} -> {ground.CellCount}");
        var builder = new FluidGeometryBuilder(32766); ground.AppendGeometry(builder);
        Require(builder.Count > 0 && !builder.Overflowed, "Spread puddle did not produce bounded visible geometry");
        foreach (var v in builder.Vertices)
            Require(float.IsFinite(v.Position.X) && v.Position.Y >= 0 && float.IsFinite(v.Thickness) && v.Thickness >= 0,
                "Ground rendering generated invalid or below-support liquid");
        Require(Math.Abs(ground.Clear() - accepted) < 1e-18 && ground.Volume == 0 && ground.CellCount == 0,
            "Ground clear did not retire the entire pool");
        Console.WriteLine($"PASS ground: flat puddle spreads {initialArea * 1e6:F2} -> {finalArea * 1e6:F2} mm2; bounded geometry and exact clear");
    }
    static void SlopedSpreading()
    {
        var ground = new GroundFilmRuntime(); ground.SetMaterial(.18); ground.BeginStep();
        var normal = Vector3.Normalize(new Vector3(-.2f, 1, 0));
        double accepted = ground.AddContact(new(.003f, .0006f, .003f), normal,
            new(-1, -.2f, -1), new(0, 0, 1), new(1, .2f, -1), 2e-8);
        Require(accepted > 0, "Sloped impact was not admitted");
        double initialX = CenterX();
        for (int frame = 0; frame < 600; frame++)
        {
            ground.BeginStep(); ground.Advance(1d / 60, new(0, -9.81f, 0));
            Require(Math.Abs(ground.Volume - accepted) < 1e-18, "Sloped flow lost liquid");
        }
        double finalX = CenterX();
        Require(finalX < initialX - .0002, $"World gravity did not move slope inventory downhill: {initialX} -> {finalX}");
        var builder = new FluidGeometryBuilder(32766); ground.AppendGeometry(builder);
        Require(builder.Count > 0, "Sloped liquid failed to render");
        foreach (var v in builder.Vertices)
            Require(Vector3.Dot(v.Position, normal) >= -1e-6f,
                "Sloped free surface rendered below its supporting plane");
        Console.WriteLine($"PASS ground: slope gravity shifts liquid center {initialX * 1e3:F3} -> {finalX * 1e3:F3} mm downhill; conserved and above support");
        double CenterX()
        {
            var film = Film(ground); double weighted = 0;
            for (int i = 0; i < film.CellCount; i++)
                weighted += film.GetCell(i).Geometry.Center.X * film.GetCell(i).Volume;
            return weighted / ground.Volume;
        }
    }
    static void PendingSupport()
    {
        var ground = new GroundFilmRuntime(); ground.BeginStep();
        double accepted = ground.AddContact(new(.003f, 0, .003f), Vector3.UnitY,
            new(0, 0, 0), new(0, 0, .006f), new(.006f, 0, 0), 2e-9);
        Require(accepted > 0, "Small support impact was not admitted");
        int pendingCalls = 0;
        GroundProbeResult Pending(Vector3 from, Vector3 to, out GroundSupportHit hit)
        { pendingCalls++; hit = default; return GroundProbeResult.Pending; }
        for (int frame = 0; frame < 120; frame++)
        {
            ground.BeginStep(); ground.Advance(1d / 60, new(0, -9.81f, 0), Pending);
            Require(ground.ProbeCalls <= 4 && ground.PendingProbes == ground.ProbeCalls,
                "Pending support bypassed the query budget or became fabricated support");
            Require(Math.Abs(ground.Volume - accepted) < 1e-18,
                "Unavailable support retired or drained owned ground liquid");
        }
        Require(pendingCalls > 0, "Fixture failed to exercise unavailable frontier support");
        var film = Film(ground);
        for (int i = 0; i < film.CellCount; i++)
        {
            var g = film.GetCell(i).Geometry;
            foreach (var p in new[] { g.A, g.B, g.C })
                Require(p.X >= -1e-6f && p.Z >= -1e-6f && p.X + p.Z <= .006001f,
                    "Unknown support grew invented geometry outside its verified triangle");
        }
        Console.WriteLine("PASS ground: pending frontier queries retain mass and seal unknown support");
    }
}
