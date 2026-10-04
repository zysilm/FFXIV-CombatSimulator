// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

internal enum GroundProbeResult { Pending, Miss, Hit }
internal readonly record struct GroundSupportHit(Vector3 Point, Vector3 Normal, Vector3 A, Vector3 B, Vector3 C);
internal delegate GroundProbeResult GroundSupportProbe(Vector3 from, Vector3 to, out GroundSupportHit hit);

/// <summary>
/// Bounded static terrain film. Every cell is clipped inside a caller-verified
/// collision triangle; only actual common 3D edges carry conservative flux.
/// Unknown support and steps are sealed. No native pointer, hook or worker is owned.
/// Caller retains all contact volume not admitted by AddContact.
/// </summary>
internal sealed partial class GroundFilmRuntime
{
    private const int Capacity = 384, SupportCapacity = 64;
    private const float GridSize = 0.003f, WeldTolerance = 0.00005f;
    private const double MinimumArea = 1e-9;
    private readonly SurfaceFilm film = new(Capacity, Capacity * 3);
    private readonly GroundSupportHit[] supports = new GroundSupportHit[SupportCapacity];
    private readonly Tile[] tiles = new Tile[Capacity];
    private readonly Cell[] cells = new Cell[Capacity];
    private readonly FailedProbe[] failed = new FailedProbe[Capacity];
    private int supportCount, tileCount, failedCount, frontierCursor;
    private int newTiles, newCells;
    private long step;
    private double timeDebt;
    private uint generation = 1;
    public double Volume => film.TotalVolume;
    public double DeferredSeconds { get; private set; }
    public int ProbeCalls { get; private set; }
    public int PendingProbes { get; private set; }
    public bool BudgetHit { get; private set; }
    public int CellCount => film.CellCount;

    private struct Tile { public int X, Z, Support; }
    private struct Cell { public int X, Z, Support, A, B, C, ConnectedEdges; }
    private struct FailedProbe { public int X, Z; public long RetryAfter; }

    public void SetMaterial(double viscosity) => film.SetMaterial(viscosity);

    /// <summary>Shared insertion budget includes both impacts and cached frontier growth.</summary>
    public void BeginStep()
    {
        newTiles = newCells = ProbeCalls = PendingProbes = 0;
        BudgetHit = false;
    }

    public double AddContact(Vector3 point, Vector3 normal, Vector3 a, Vector3 b, Vector3 c, double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        var hit = new GroundSupportHit(point, normal, a, b, c);
        if (!ValidSupport(hit)) return 0;
        var support = FindSupport(hit);
        if (support < 0)
        {
            if (supportCount == supports.Length) { BudgetHit = true; return 0; }
            support = supportCount++; supports[support] = NormalizeSupport(hit);
        }
        var x = Grid(point.X); var z = Grid(point.Z);
        if (!RegisterTile(x, z, support, false)) return 0;
        var contactCell = -1;
        for (var i = 0; i < film.CellCount; i++)
        {
            if (cells[i].X != x || cells[i].Z != z || cells[i].Support != support) continue;
            if (Contains(film.GetCell(i).Geometry, point)) { contactCell = i; break; }
        }
        if (contactCell < 0) return 0;
        // An impact initially occupies an actual contact footprint, instead of trying
        // to squeeze its whole volume into one 3mm half-tile. Only clipped cells on
        // this same verified terrain triangle participate; no support is fabricated.
        RegisterTile(x - 1, z, support, true);
        RegisterTile(x + 1, z, support, true);
        RegisterTile(x, z - 1, support, true);
        RegisterTile(x, z + 1, support, true);
        var accepted = 0.0;
        const double impactThickness = 0.0005;
        for (var i = 0; i < film.CellCount && accepted < requested; i++)
        {
            if (cells[i].Support != support || Math.Abs(cells[i].X - x) + Math.Abs(cells[i].Z - z) > 1) continue;
            var sample = film.GetCell(i);
            var deficit = Math.Max(0, sample.Geometry.Area * impactThickness - sample.Volume);
            accepted += film.AddVolume(i, Math.Min(requested - accepted, deficit));
        }
        // Existing local capacity can accept the remaining load. Rejected inventory
        // stays with the drop and is retried as the bounded frontier grows.
        accepted += film.AddVolume(contactCell, requested - accepted);
        return accepted;
    }

    /// <summary>At most four delegate calls per step. Pending means budget unavailable, never unsupported terrain.</summary>
    public FilmStepResult Advance(double seconds, Vector3 gravity, GroundSupportProbe? probe = null)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !WorldGeometryBuilder.Finite(gravity)) return default;
        step++;
        GrowFrontier(probe);
        timeDebt += seconds;
        var result = film.Advance(Math.Min(timeDebt, 4.0 / 60), gravity);
        timeDebt = Math.Max(0, timeDebt - result.SimulatedSeconds);
        DeferredSeconds = timeDebt;
        return result with { DeferredSeconds = timeDebt };
    }

    private void GrowFrontier(GroundSupportProbe? probe)
    {
        var initialCells = film.CellCount;
        if (initialCells == 0 || Volume <= 0) return;
        var attempts = 0;
        for (var n = 0; n < initialCells && attempts < 12 && ProbeCalls < 4; n++)
        {
            var index = (frontierCursor + n) % initialCells;
            var sample = film.GetCell(index);
            if (sample.Volume <= 0 || sample.Thickness <= 2e-6) continue;
            var cell = cells[index];
            for (var side = 0; side < 3 && attempts < 12 && ProbeCalls < 4; side++)
            {
                if ((cells[index].ConnectedEdges & (1 << side)) != 0) continue;
                var g = sample.Geometry;
                var p = side == 0 ? g.A : side == 1 ? g.B : g.C;
                var q = side == 0 ? g.B : side == 1 ? g.C : g.A;
                var middle = (p + q) * 0.5f;
                var outward = middle - g.Center; outward.Y = 0;
                if (outward.LengthSquared() < 1e-12f) continue;
                var requestPoint = middle + Vector3.Normalize(outward) * 0.0003f;
                var x = Grid(requestPoint.X); var z = Grid(requestPoint.Z);
                attempts++;
                // Even inside the same XZ tile, the source collision triangle may
                // end. Probe its actual unconnected edge rather than skip that seam.
                RegisterTile(x, z, cell.Support, true);
                if ((cells[index].ConnectedEdges & (1 << side)) != 0) continue;
                if (probe == null || RecentlyFailed(x, z)) continue;
                var sx = requestPoint.X; var sz = requestPoint.Z;
                var hint = supports[cell.Support];
                var sy = hint.A.Y - (hint.Normal.X * (sx - hint.A.X) + hint.Normal.Z * (sz - hint.A.Z)) / hint.Normal.Y;
                ProbeCalls++;
                var outcome = probe(new Vector3(sx, sy + 0.05f, sz), new Vector3(sx, sy - 0.05f, sz), out var hit);
                if (outcome == GroundProbeResult.Pending) { PendingProbes++; BudgetHit = true; continue; }
                if (outcome != GroundProbeResult.Hit || !ValidSupport(hit)) { RememberFailure(x, z); continue; }
                var support = FindSupport(hit);
                if (support < 0)
                {
                    if (supportCount == supports.Length) { BudgetHit = true; continue; }
                    support = supportCount++; supports[support] = NormalizeSupport(hit);
                }
                // A lower stair landing has no common 3D edge and is not connected.
                if (!RegisterTile(x, z, support, true)) RememberFailure(x, z);
            }
        }
        frontierCursor = (frontierCursor + Math.Max(1, attempts)) % initialCells;
        if (film.CellCount == Capacity || attempts == 12 || ProbeCalls == 4) BudgetHit = true;
    }

    private bool RegisterTile(int x, int z, int support, bool requireConnection)
    {
        if (HasTile(x, z, support)) return true;
        Span<FilmCellGeometry> pieces = stackalloc FilmCellGeometry[6];
        var count = ClipTile(supports[support], x, z, pieces);
        if (count == 0) return false;
        if (newTiles >= 2 || newCells + count > 8) { BudgetHit = true; return false; }
        if (film.CellCount + count > Capacity || tileCount == tiles.Length) { BudgetHit = true; return false; }
        if (requireConnection)
        {
            var connected = false;
            for (var p = 0; p < count && !connected; p++)
                for (var i = 0; i < film.CellCount && !connected; i++)
                {
                    if (Math.Abs(cells[i].X - x) > 1 || Math.Abs(cells[i].Z - z) > 1) continue;
                    connected = SharedEdge(pieces[p], film.GetCell(i).Geometry, out _, out _) > WeldTolerance;
                }
            if (!connected) return false;
        }
        var first = film.CellCount;
        for (var p = 0; p < count; p++)
        {
            // Capacity and geometry were prevalidated, so insertion cannot partially
            // fail and shift persistent inventory IDs during a repeated contact.
            if (!film.TryAddCell(generation, first + p, pieces[p], out var index))
                throw new InvalidOperationException("Prevalidated ground cell insertion failed");
            cells[index] = new Cell {
                X = x, Z = z, Support = support,
                A = VertexIndex(pieces[p].A, pieces[p].Normal), B = VertexIndex(pieces[p].B, pieces[p].Normal),
                C = VertexIndex(pieces[p].C, pieces[p].Normal),
            };
            for (var other = 0; other < index; other++)
            {
                if (Math.Abs(cells[other].X - x) > 1 || Math.Abs(cells[other].Z - z) > 1) continue;
                var width = SharedEdge(pieces[p], film.GetCell(other).Geometry, out var thisEdge, out var otherEdge);
                if (width <= WeldTolerance) continue;
                if (!film.TryConnect(index, other, width)) BudgetHit = true;
                else
                {
                    cells[index].ConnectedEdges |= 1 << thisEdge;
                    cells[other].ConnectedEdges |= 1 << otherEdge;
                }
            }
        }
        tiles[tileCount++] = new Tile { X = x, Z = z, Support = support };
        newTiles++; newCells += count;
        return true;
    }

    private bool HasTile(int x, int z, int support)
    {
        for (var i = 0; i < tileCount; i++) if (tiles[i].X == x && tiles[i].Z == z && tiles[i].Support == support) return true;
        return false;
    }
    private bool RecentlyFailed(int x, int z)
    {
        for (var i = 0; i < failedCount; i++) if (failed[i].X == x && failed[i].Z == z && failed[i].RetryAfter > step) return true;
        return false;
    }
    private void RememberFailure(int x, int z)
    {
        for (var i = 0; i < failedCount; i++)
            if (failed[i].X == x && failed[i].Z == z) { failed[i].RetryAfter = step + 60; return; }
        if (failedCount < failed.Length) failed[failedCount++] = new FailedProbe { X = x, Z = z, RetryAfter = step + 60 };
    }
    private int FindSupport(GroundSupportHit hit)
    {
        for (var i = 0; i < supportCount; i++)
        {
            var s = supports[i];
            if ((Near(hit.A, s.A) && ((Near(hit.B, s.B) && Near(hit.C, s.C)) || (Near(hit.B, s.C) && Near(hit.C, s.B))))
                || (Near(hit.A, s.B) && ((Near(hit.B, s.A) && Near(hit.C, s.C)) || (Near(hit.B, s.C) && Near(hit.C, s.A))))
                || (Near(hit.A, s.C) && ((Near(hit.B, s.A) && Near(hit.C, s.B)) || (Near(hit.B, s.B) && Near(hit.C, s.A))))) return i;
        }
        return -1;
    }
    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= WeldTolerance * WeldTolerance;
    private static int Grid(float value) => (int)MathF.Floor(value / GridSize);
    private static GroundSupportHit NormalizeSupport(GroundSupportHit s)
    {
        var n = Vector3.Normalize(Vector3.Cross(s.B - s.A, s.C - s.A));
        if (n.Y < 0) n = -n;
        return s with { Normal = n };
    }
    private static bool ValidSupport(GroundSupportHit s)
    {
        if (!WorldGeometryBuilder.Finite(s.Point) || !WorldGeometryBuilder.Finite(s.A)
            || !WorldGeometryBuilder.Finite(s.B) || !WorldGeometryBuilder.Finite(s.C)
            || MathF.Abs(s.Point.X) > 100000 || MathF.Abs(s.Point.Z) > 100000) return false;
        var cross = Vector3.Cross(s.B - s.A, s.C - s.A);
        if (!WorldGeometryBuilder.Finite(cross) || cross.LengthSquared() < 4e-18f) return false;
        // Initial support is a single-valued XZ terrain graph, not walls or overhangs.
        if (MathF.Abs(Vector3.Normalize(cross).Y) < 0.2f) return false;
        return Contains(new FilmCellGeometry(s.A, s.B, s.C, Vector3.Normalize(cross)), s.Point);
    }

    public string Inspect() => $"Ground film: cells={film.CellCount}/{Capacity}, edges={film.EdgeCount}, tiles={tileCount}, supports={supportCount}/{SupportCapacity}, volume={Volume * 1e6:F5}ml, probes={ProbeCalls}/4, newTiles={newTiles}/2,newCells={newCells}/8, pending={PendingProbes}, budgetHit={BudgetHit}, deferred={DeferredSeconds:F5}s, grid=3mm; verified static triangle clips, shared 3D edges; unknown/steps sealed; collision/render correspondence requires in-game proof";
    public string Status => Inspect();
    public double Clear()
    {
        var retired = film.Clear();
        supportCount = tileCount = failedCount = frontierCursor = vertexCount = 0;
        step = 0; generation++; if (generation == 0) generation = 1;
        timeDebt = 0;
        DeferredSeconds = 0; ProbeCalls = PendingProbes = 0; BudgetHit = false;
        newTiles = newCells = 0;
        return retired;
    }
}
