// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Effects.BodyFluids.Simulation;

/// <summary>Current posed triangle geometry, in world metres. Topology is supplied by the caller.</summary>
public readonly record struct FilmCellGeometry(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)
{
    public Vector3 Center => (A + B + C) / 3f;
    public double Area => Vector3.Cross(B - A, C - A).Length() * 0.5;
}

public readonly record struct FilmCellSample(uint Generation, int Triangle, FilmCellGeometry Geometry,
    double Volume, double Thickness, bool Pinned, double OutletVolume)
{
    public bool ExceedsThinFilmRange => Thickness > 0.002;
}

public readonly record struct FilmStepResult(double SimulatedSeconds, double DeferredSeconds,
    double InternalTransferredVolume, double OutletTransferredVolume, int Substeps);

/// <summary>
/// Bounded, conservative finite-volume film on an explicitly known local triangle graph.
/// Volumes are m³; deformation changes thickness, never inventory. Unknown boundaries are sealed.
/// This is a slow-flow lubrication approximation, not a model for thick beads or inertial splashes.
/// All material values below are calibration presets, not measured human skin properties.
/// </summary>
public sealed class SurfaceFilm
{
    private const double MinimumArea = 1e-12;
    private const double MinimumDistance = 1e-5;
    private const double Density = 1000;
    private double viscosity = 0.18; // Pa.s, deliberately viscous reduced-model preset.
    private const double SurfaceTension = 0.055; // N/m.
    private const double ContactHysteresis = 0.025; // cos(theta_R) - cos(theta_A).
    private const double ResidualThickness = 2e-6;
    private const double MaximumAdmissionThickness = 0.002;
    private const double MaximumInventory = 20e-6;
    private const int MaximumSubsteps = 12;
    private readonly Cell[] cells;
    private readonly Edge[] edges;
    private readonly double[] heights, laplacian, pressure, outgoing, incoming, delta, rowWeights;
    private int cellCount, edgeCount;
    public int CellCount => cellCount;
    public int EdgeCount => edgeCount;
    public double AcceptedVolume { get; private set; }
    public double TakenVolume { get; private set; }
    public double InternalTransferredVolume { get; private set; }
    public double OutletTransferredVolume { get; private set; }
    public double TotalVolume { get; private set; }

    /// <summary>Artistic dynamic viscosity in Pa.s. Increasing it slows h³ film flux without changing inventory.</summary>
    public void SetMaterial(double viscosityPaSeconds)
        => viscosity = double.IsFinite(viscosityPaSeconds) ? Math.Clamp(viscosityPaSeconds, 0.03, 1.2) : 0.18;

    private struct Cell
    {
        public uint Generation;
        public int Triangle;
        public FilmCellGeometry Geometry;
        public double Volume, OutletVolume;
        public bool Pinned;
    }

    private struct Edge
    {
        public int A, B; // B == -1 only for explicitly registered physical outlets.
        public double Width, Distance, Rate;
        public Vector3 OutletDirection;
    }

    public SurfaceFilm(int cellCapacity = 384, int edgeCapacity = 1152)
    {
        if (cellCapacity < 1 || cellCapacity > 4096) throw new ArgumentOutOfRangeException(nameof(cellCapacity));
        if (edgeCapacity < 1 || edgeCapacity > 12288) throw new ArgumentOutOfRangeException(nameof(edgeCapacity));
        cells = new Cell[cellCapacity];
        edges = new Edge[edgeCapacity];
        heights = new double[cellCapacity]; laplacian = new double[cellCapacity];
        pressure = new double[cellCapacity]; outgoing = new double[cellCapacity];
        incoming = new double[cellCapacity]; delta = new double[cellCapacity];
        rowWeights = new double[cellCapacity];
    }

    public bool TryGetCell(uint generation, int triangle, out int index)
    {
        for (var i = 0; i < cellCount; i++)
            if (cells[i].Generation == generation && cells[i].Triangle == triangle) { index = i; return true; }
        index = -1;
        return false;
    }

    public bool TryAddCell(uint generation, int triangle, in FilmCellGeometry geometry, out int index)
    {
        if (TryGetCell(generation, triangle, out index)) return UpdateGeometry(index, geometry);
        index = -1;
        if (triangle < 0 || cellCount == cells.Length || !Valid(geometry)) return false;
        index = cellCount++;
        cells[index] = new Cell { Generation = generation, Triangle = triangle, Geometry = geometry };
        return true;
    }

    public bool UpdateGeometry(int index, in FilmCellGeometry geometry)
    {
        if (!IsCell(index) || !Valid(geometry)) return false;
        cells[index].Geometry = geometry;
        return true;
    }

    /// <summary>Only connect resolved material neighbours, including separately validated UV seam welds.</summary>
    public bool TryConnect(int a, int b, double sharedEdgeLength)
    {
        if (!IsCell(a) || !IsCell(b) || a == b || cells[a].Generation != cells[b].Generation ||
            !double.IsFinite(sharedEdgeLength) || sharedEdgeLength <= 0) return false;
        for (var i = 0; i < edgeCount; i++)
            if ((edges[i].A == a && edges[i].B == b) || (edges[i].A == b && edges[i].B == a))
            { edges[i].Width = sharedEdgeLength; return true; }
        if (edgeCount == edges.Length) return false;
        edges[edgeCount++] = new Edge { A = a, B = b, Width = sharedEdgeLength };
        return true;
    }

    /// <summary>No outlet is inferred from missing adjacency. Drained volume stays owned until taken.</summary>
    public bool TryAddOutlet(int cell, double edgeLength, Vector3 outwardTangent)
    {
        if (!IsCell(cell) || edgeCount == edges.Length || !double.IsFinite(edgeLength) || edgeLength <= 0 ||
            !Finite(outwardTangent) || outwardTangent.LengthSquared() < 1e-10f) return false;
        edges[edgeCount++] = new Edge { A = cell, B = -1, Width = edgeLength,
            OutletDirection = Vector3.Normalize(outwardTangent) };
        return true;
    }

    /// <summary>Returns accepted volume. The caller retains every rejected fraction.</summary>
    public double AddVolume(int index, double requested)
    {
        if (!IsCell(index) || !double.IsFinite(requested) || requested <= 0) return 0;
        var room = Math.Max(0, cells[index].Geometry.Area * MaximumAdmissionThickness - cells[index].Volume);
        var accepted = Math.Min(requested, Math.Min(room, Math.Max(0, MaximumInventory - TotalVolume)));
        cells[index].Volume += accepted;
        TotalVolume += accepted; AcceptedVolume += accepted;
        return accepted;
    }

    /// <summary>Transfer to a bead, bridge or another contact owner; never delete unaccepted inventory.</summary>
    public double TakeVolume(int index, double requested)
    {
        if (!IsCell(index) || !double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, cells[index].Volume);
        cells[index].Volume -= taken;
        TotalVolume -= taken; TakenVolume += taken;
        return taken;
    }

    public double TakeOutletVolume(int index, double requested)
    {
        if (!IsCell(index) || !double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, cells[index].OutletVolume);
        cells[index].OutletVolume -= taken;
        TotalVolume -= taken; TakenVolume += taken;
        return taken;
    }

    public FilmCellSample GetCell(int index)
    {
        if (!IsCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
        ref var c = ref cells[index];
        return new(c.Generation, c.Triangle, c.Geometry, c.Volume, c.Volume / c.Geometry.Area, c.Pinned, c.OutletVolume);
    }

    /// <summary>
    /// Integrates at most twelve adaptive substeps. Caller retains DeferredSeconds, or explicitly pauses
    /// simulation after a stale pose; large frame gaps are never integrated using an unchecked large dt.
    /// effectiveGravity may include validated surface acceleration in the same world space.
    /// </summary>
    public FilmStepResult Advance(double seconds, Vector3 effectiveGravity)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !Finite(effectiveGravity)) return default;
        double simulated = 0, moved = 0, drained = 0;
        var steps = 0;
        while (simulated < seconds && steps < MaximumSubsteps)
        {
            var stableStep = Prepare(effectiveGravity);
            var dt = Math.Min(seconds - simulated, stableStep);
            // A stiff or badly compressed patch freezes instead of consuming unbounded CPU work.
            if (!double.IsFinite(dt) || dt < 1e-7) break;
            Array.Clear(delta, 0, cellCount);
            for (var i = 0; i < edgeCount; i++)
            {
                ref var e = ref edges[i];
                if (e.Rate == 0) continue;
                var donor = e.Rate > 0 ? e.A : e.B;
                var receiver = e.Rate > 0 ? e.B : e.A;
                var amount = Math.Abs(e.Rate) * dt;
                // Shared donor limiter scales every outgoing edge equally; incoming liquid cannot
                // be spent in the same substep. Every transfer is added/subtracted as one pair.
                var donorLimit = outgoing[donor] > 0 ? Math.Min(1, cells[donor].Volume / (outgoing[donor] * dt)) : 0;
                amount *= donorLimit;
                if (receiver >= 0)
                {
                    var room = Math.Max(0, cells[receiver].Geometry.Area * MaximumAdmissionThickness - cells[receiver].Volume);
                    var receiverLimit = incoming[receiver] > 0 ? Math.Min(1, room / (incoming[receiver] * dt)) : 0;
                    amount *= receiverLimit;
                }
                delta[donor] -= amount;
                if (receiver >= 0) { delta[receiver] += amount; moved += amount; }
                else { cells[donor].OutletVolume += amount; drained += amount; }
            }
            for (var i = 0; i < cellCount; i++) cells[i].Volume += delta[i];
            simulated += dt; steps++;
        }
        InternalTransferredVolume += moved; OutletTransferredVolume += drained;
        return new(simulated, Math.Max(0, seconds - simulated), moved, drained, steps);
    }

    private double Prepare(Vector3 gravity)
    {
        Array.Clear(laplacian, 0, cellCount); Array.Clear(rowWeights, 0, cellCount);
        Array.Clear(outgoing, 0, cellCount); Array.Clear(incoming, 0, cellCount);
        for (var i = 0; i < cellCount; i++)
        { heights[i] = cells[i].Volume / cells[i].Geometry.Area; cells[i].Pinned = cells[i].Volume > 0; }
        for (var i = 0; i < edgeCount; i++)
        {
            ref var e = ref edges[i];
            if (e.B < 0) { e.Distance = Math.Max(MinimumDistance, Math.Sqrt(cells[e.A].Geometry.Area)); continue; }
            e.Distance = Math.Max(MinimumDistance,
                Vector3.Distance(cells[e.A].Geometry.Center, cells[e.B].Geometry.Center));
            var weight = e.Width / e.Distance;
            var difference = (heights[e.B] - heights[e.A]) * weight;
            laplacian[e.A] += difference / cells[e.A].Geometry.Area;
            laplacian[e.B] -= difference / cells[e.B].Geometry.Area;
            rowWeights[e.A] += weight / cells[e.A].Geometry.Area;
            rowWeights[e.B] += weight / cells[e.B].Geometry.Area;
        }
        for (var i = 0; i < cellCount; i++)
        {
            var normal = Vector3.Normalize(cells[i].Geometry.Normal);
            var normalLoad = Math.Max(0, -Vector3.Dot(gravity, normal));
            // Graph Laplace-Beltrami curvature of the height field plus hydrostatic normal load.
            pressure[i] = -SurfaceTension * laplacian[i] + Density * normalLoad * heights[i];
        }
        var stable = 1.0 / 120;
        for (var i = 0; i < edgeCount; i++)
        {
            ref var e = ref edges[i];
            var normal = Vector3.Normalize(cells[e.A].Geometry.Normal);
            var direction = e.B >= 0 ? cells[e.B].Geometry.Center - cells[e.A].Geometry.Center : e.OutletDirection;
            if (e.B >= 0)
            {
                var sum = normal + Vector3.Normalize(cells[e.B].Geometry.Normal);
                if (sum.LengthSquared() > 1e-8f) normal = Vector3.Normalize(sum);
            }
            direction -= normal * Vector3.Dot(direction, normal);
            if (direction.LengthSquared() < 1e-12f) { e.Rate = 0; continue; }
            direction = Vector3.Normalize(direction);
            // A free outlet has no fabricated capillary pressure jump into an unknown cell.
            var driving = Density * Vector3.Dot(gravity, direction);
            if (e.B >= 0) driving -= (pressure[e.B] - pressure[e.A]) / e.Distance;
            var donor = driving >= 0 ? e.A : e.B;
            if (donor < 0) { e.Rate = 0; continue; }
            var h = Math.Max(0, heights[donor] - ResidualThickness);
            var receiver = donor == e.A ? e.B : e.A;
            var dryFront = receiver < 0 || heights[receiver] < ResidualThickness;
            if (dryFront && h > 0)
            {
                // Furmidge-style contact retention: pressure/body-force resultant against
                // gamma * contact width * contact-angle hysteresis. Pooling can unpin a front.
                var force = Math.Abs(driving) * cells[donor].Volume;
                var retention = SurfaceTension * e.Width * ContactHysteresis;
                if (force <= retention) { e.Rate = 0; continue; }
                driving *= 1 - retention / force;
            }
            var mobility = h * h * h / (3 * viscosity);
            e.Rate = mobility * driving * e.Width;
            if (!double.IsFinite(e.Rate)) { e.Rate = 0; continue; }
            var rate = Math.Abs(e.Rate);
            if (rate > 0)
            {
                outgoing[donor] += rate;
                if (receiver >= 0) incoming[receiver] += rate;
                cells[donor].Pinned = false;
                stable = Math.Min(stable, 0.2 * cells[donor].Volume / rate);
            }
            if (e.B >= 0 && mobility > 0)
            {
                // Conservative fourth-order explicit stability bound from graph row sums.
                var row = Math.Max(rowWeights[e.A], rowWeights[e.B]);
                var stiffness = 4 * mobility * row * (SurfaceTension * row + Density * gravity.Length());
                if (stiffness > 0) stable = Math.Min(stable, 0.15 / stiffness);
            }
        }
        return stable;
    }

    /// <summary>Returns removed inventory so lifecycle retirement remains visible to the caller's ledger.</summary>
    public double Clear()
    {
        var retired = TotalVolume;
        Array.Clear(cells, 0, cellCount); Array.Clear(edges, 0, edgeCount);
        cellCount = edgeCount = 0;
        TotalVolume = AcceptedVolume = TakenVolume = InternalTransferredVolume = OutletTransferredVolume = 0;
        return retired;
    }

    private bool IsCell(int index) => index >= 0 && index < cellCount;
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Valid(in FilmCellGeometry g) => Finite(g.A) && Finite(g.B) && Finite(g.C) &&
        Finite(g.Normal) && g.Normal.LengthSquared() > 1e-10f && double.IsFinite(g.Area) && g.Area >= MinimumArea;
}
