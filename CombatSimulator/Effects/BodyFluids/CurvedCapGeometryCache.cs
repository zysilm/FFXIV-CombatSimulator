// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>
/// A bounded, volume-normalized free surface over a real local skin patch, not a closed sphere.
/// All mappings read existing film geometry and known adjacency. No skin, native or global nearest queries.
/// The 16-side/6-ring tessellation approximates curvature between material samples.
/// </summary>
internal sealed class CurvedCapGeometryCache
{
    private const int Sides = 16, Rings = 6, Points = 1 + Sides * Rings;
    private const float SkinLift = 0.00008f;
    private static readonly Vector2[] Directions = MakeDirections();
    private static readonly int[] Triangles = MakeTriangles();
    private static readonly int[] Edges = MakeEdges();
    private readonly int[] cells = new int[Points], nextCells = new int[Points];
    private readonly Vector3[] barycentrics = new Vector3[Points], nextBarycentrics = new Vector3[Points];
    private readonly Vector3[] substrate = new Vector3[Points], positions = new Vector3[Points], normals = new Vector3[Points];
    private readonly float[] heights = new float[Points], nextHeights = new float[Points];
    private SurfaceFilmRuntime? owner;
    private uint generation;
    private bool valid;
    private readonly SupportedPatch supported = new();
    private bool drawSupported;
    public bool Pending { get; private set; }
    public string Status { get; private set; } = "No cap mapping";
    public double VolumeCorrection { get; private set; }
    public double CurvatureMinimum { get; private set; }
    public double CurvatureMaximum { get; private set; }
    public bool UsesClippedSupport => drawSupported;
    public int RenderedTriangles => drawSupported ? supported.TriangleCount : Triangles.Length / 3;

    public void Invalidate() { valid = false; owner = null; supported.Invalidate(); }
    public void MarkUnavailable() { Pending = true; Status = "Current cap anchor/patch unavailable"; }

    public bool Prepare(SurfaceFilmRuntime film, FluidSurfaceAnchor anchor, FluidSurfaceSample sample,
        double radius, double volume, ref int budget)
    {
        Pending = false;
        drawSupported = false;
        bool compatible = valid && owner == film && generation == anchor.Generation;
        if (!compatible) valid = false;
        if (!film.TryGetCell(anchor, out int centerCell) || !film.TryGetCapGeometry(centerCell, out var centerGeometry))
            return Fail("Verified receiving skin patch unavailable");
        var center = Point(centerGeometry, anchor.Barycentric);
        var axis = centerGeometry.Normal;
        compatible = compatible && film.TryGetCapGeometry(cells[0], out var priorCenter) &&
            Vector3.DistanceSquared(Point(priorCenter, barycentrics[0]), center) <= 4e-8f &&
            Vector3.Dot(priorCenter.Normal, axis) > 0.95f;
        if (!compatible) valid = false;
        float poseTolerance = PositionTolerance(center);
        if (Vector3.DistanceSquared(center, sample.Position) > poseTolerance * poseTolerance || Vector3.Dot(axis, sample.Normal) < 0.999f)
            return Fail($"Film patch pose does not match current cap anchor delta={Vector3.Distance(center, sample.Position) * 1e3:F4}mm normalDot={Vector3.Dot(axis, sample.Normal):F5}");
        var tangent = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var bitangent = Vector3.Cross(axis, tangent);
        nextCells[0] = centerCell; nextBarycentrics[0] = anchor.Barycentric;
        nextHeights[0] = (float)(radius * (1 - Math.Cos(0.9)));
        substrate[0] = center;
        string reason = string.Empty;
        for (int ring = 1; ring <= Rings && reason.Length == 0; ring++)
        {
            float theta = 0.9f * ring / Rings;
            float distance = (float)radius * MathF.Sin(theta);
            float height = ring == Rings ? 0 : (float)radius * (MathF.Cos(theta) - MathF.Cos(0.9f));
            for (int side = 0; side < Sides; side++)
            {
                int index = 1 + (ring - 1) * Sides + side;
                var target = Directions[side] * distance;
                // Most samples stay in their previous triangle. A failed cached test falls back to a
                // radial path from the already verified preceding ring, never a full patch scan.
                bool mapped = compatible && TryCached(film, cells[index], target, center, tangent, bitangent, axis,
                    ref budget, out nextBarycentrics[index], out substrate[index]);
                if (mapped) nextCells[index] = cells[index];
                else
                {
                    int previous = ring == 1 ? 0 : index - Sides;
                    if (!MapRadial(film, nextCells[previous], nextBarycentrics[previous], target,
                        center, tangent, bitangent, axis, ref budget, out nextCells[index],
                        out nextBarycentrics[index], out substrate[index], out reason)) break;
                }
                nextHeights[index] = height;
            }
        }
        if (reason.Length == 0)
            _ = VerifyEdges(film, nextCells, nextBarycentrics, center, tangent, bitangent, axis, ref budget, out reason);
        if (reason.Length == 0)
        {
            // Commit a complete footprint only. A partial failed expansion never overwrites the valid mapping.
            Array.Copy(nextCells, cells, Points); Array.Copy(nextBarycentrics, barycentrics, Points);
            Array.Copy(nextHeights, heights, Points);
            owner = film; generation = anchor.Generation; valid = true;
            if (BuildFreeSurface(center, axis, volume)) { Status = "Verified curved cap; discrete volume normalized"; return true; }
            valid = false;
            return Fail("Cap chart inverted/degenerate or volume nonfinite");
        }
        Pending = true;
        Status = reason;
        // Only the old verified footprint may remain pinned as inventory grows. Its height is recomputed
        // from the actual inventory. A moved owner or changed/folded patch cannot reuse an unrelated footprint.
        if (!compatible || !film.TryGetCapGeometry(cells[0], out var oldCenterGeometry) ||
            Vector3.DistanceSquared(Point(oldCenterGeometry, barycentrics[0]), center) > 1e-8f)
            return PrepareSupported(film, anchor, center, axis, tangent, bitangent, radius, volume, ref budget);
        for (int i = 0; i < Points; i++)
        {
            if (!Consume(ref budget) || !film.TryGetCapGeometry(cells[i], out var geometry) || Vector3.Dot(geometry.Normal, axis) < 0.5f)
                return PrepareSupported(film, anchor, center, axis, tangent, bitangent, radius, volume, ref budget);
            substrate[i] = Point(geometry, barycentrics[i]);
        }
        if (!VerifyEdges(film, cells, barycentrics, center, tangent, bitangent, axis, ref budget, out _) ||
            !BuildFreeSurface(center, axis, volume))
            return PrepareSupported(film, anchor, center, axis, tangent, bitangent, radius, volume, ref budget);
        Status += "; drawing retained verified footprint without expansion";
        return true;
    }

    private bool PrepareSupported(SurfaceFilmRuntime film, FluidSurfaceAnchor anchor, Vector3 center, Vector3 axis,
        Vector3 u, Vector3 v, double radius, double volume, ref int budget)
    {
        drawSupported = supported.Prepare(film, anchor, center, axis, u, v, radius, volume, ref budget, out var reason);
        if (drawSupported)
        {
            Status = "Verified skin support clipped cap; reduced pinned footprint, not exact Young-Laplace";
            Pending = supported.Pending;
            if (Pending) Status += "; retained support: " + reason;
        }
        else { Pending = true; Status = reason; }
        return drawSupported;
    }

    private bool Fail(string reason) { Pending = true; Status = reason; return false; }

    private static bool TryCached(SurfaceFilmRuntime film, int cell, Vector2 target, Vector3 origin,
        Vector3 tangent, Vector3 bitangent, Vector3 axis, ref int budget, out Vector3 bary, out Vector3 point)
    {
        bary = point = default;
        if (!Consume(ref budget) || !film.TryGetCapGeometry(cell, out var g) || Vector3.Dot(g.Normal, axis) < 0.5f ||
            !ChartBary(g, target, origin, tangent, bitangent, out bary) || !Inside(bary)) return false;
        bary = ClampBary(bary); point = Point(g, bary); return true;
    }

    private static bool MapRadial(SurfaceFilmRuntime film, int cell, Vector3 startBary, Vector2 target,
        Vector3 origin, Vector3 tangent, Vector3 bitangent, Vector3 axis, ref int budget,
        out int mappedCell, out Vector3 bary, out Vector3 point, out string reason)
    {
        mappedCell = -1; bary = point = default; reason = string.Empty;
        for (int step = 0; step < 8; step++)
        {
            if (!Consume(ref budget)) { reason = "Cap chart work budget exhausted"; return false; }
            if (!film.TryGetCapGeometry(cell, out var g) || Vector3.Dot(g.Normal, axis) < 0.5f ||
                !ChartBary(g, target, origin, tangent, bitangent, out var targetBary))
            { reason = "Cap chart unavailable, folded or degenerate"; return false; }
            if (Inside(targetBary))
            {
                mappedCell = cell; bary = ClampBary(targetBary); point = Point(g, bary); return true;
            }
            float fraction = 1; int edge = -1;
            for (int e = 0; e < 3; e++)
            {
                float old = Component(startBary, e), next = Component(targetBary, e);
                if (next >= 0 || old - next <= 0) continue;
                float crossing = Math.Clamp(old / (old - next), 0, 1);
                if (crossing <= fraction) { fraction = crossing; edge = e; }
            }
            int neighbour = edge < 0 ? -1 : film.GetCapNeighbour(cell, edge);
            if (neighbour < 0 || !film.TryGetCapGeometry(neighbour, out var nextGeometry))
            { reason = "Cap footprint reached unknown patch edge/seam " + film.GetCapEdgeDiagnostic(cell, edge); return false; }
            var crossingPoint = Point(g, ClampBary(Vector3.Lerp(startBary, targetBary, fraction)));
            var projected = Project(crossingPoint, origin, tangent, bitangent);
            if (!ChartBary(nextGeometry, projected, origin, tangent, bitangent, out startBary))
            { reason = "Cap adjacent chart is degenerate"; return false; }
            startBary = ClampBary(startBary); cell = neighbour;
        }
        reason = "Cap footprint radial path exceeded eight known transitions"; return false;
    }

    private bool BuildFreeSurface(Vector3 center, Vector3 axis, double volume)
    {
        double integral = 0;
        for (int i = 0; i < Triangles.Length; i += 3)
        {
            int a = Triangles[i], b = Triangles[i + 1], c = Triangles[i + 2];
            double projectedArea = Vector3.Dot(Vector3.Cross(substrate[b] - substrate[a], substrate[c] - substrate[a]), axis) * 0.5;
            if (!(projectedArea > 1e-16) || !double.IsFinite(projectedArea)) return false;
            integral += projectedArea * (heights[a] + heights[b] + heights[c]) / 3;
        }
        if (!(integral > 0) || !(volume > 0) || !double.IsFinite(volume)) return false;
        double correction = volume / integral;
        if (!double.IsFinite(correction) || correction <= 0) return false;
        VolumeCorrection = correction;
        CurvatureMinimum = double.PositiveInfinity; CurvatureMaximum = double.NegativeInfinity;
        Array.Clear(normals);
        for (int i = 0; i < Points; i++)
        {
            var elevation = heights[i] * correction;
            if (!double.IsFinite(elevation) || elevation > 0.02) return false;
            positions[i] = substrate[i] + axis * (float)(elevation + SkinLift);
            if (i >= 1 + (Rings - 1) * Sides)
            {
                double gap = Vector3.Dot(substrate[i] - center, axis);
                CurvatureMinimum = Math.Min(CurvatureMinimum, gap); CurvatureMaximum = Math.Max(CurvatureMaximum, gap);
            }
        }
        for (int i = 0; i < Triangles.Length; i += 3)
        {
            int a = Triangles[i], b = Triangles[i + 1], c = Triangles[i + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (int i = 0; i < Points; i++)
            normals[i] = normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[i]) : axis;
        return true;
    }

    private bool VerifyEdges(SurfaceFilmRuntime film, int[] mappedCells, Vector3[] mappedBary,
        Vector3 origin, Vector3 tangent, Vector3 bitangent, Vector3 axis, ref int budget, out string reason)
    {
        reason = string.Empty;
        for (int i = 0; i < Edges.Length; i += 2)
        {
            int a = Edges[i], b = Edges[i + 1];
            if (mappedCells[a] == mappedCells[b]) continue; // The segment lies in one convex, verified triangle.
            if (!MapRadial(film, mappedCells[a], mappedBary[a], Project(substrate[b], origin, tangent, bitangent),
                origin, tangent, bitangent, axis, ref budget, out _, out _, out var point, out reason)) return false;
            float tolerance = PositionTolerance(substrate[b]);
            if (Vector3.DistanceSquared(point, substrate[b]) > tolerance * tolerance)
            { reason = "Cap chart has overlapping/ambiguous skin branches"; return false; }
        }
        return true;
    }

    private static bool Consume(ref int budget)
    {
        if (budget <= 0) return false;
        budget--; return true;
    }
    private static float PositionTolerance(Vector3 p)
    {
        // World float precision can exceed micrometres far from the origin. This is only an
        // equality tolerance, not a skin offset or permission to bind another surface branch.
        float ulp = MathF.Max(MathF.Abs(MathF.BitIncrement(p.X) - p.X),
            MathF.Max(MathF.Abs(MathF.BitIncrement(p.Y) - p.Y), MathF.Abs(MathF.BitIncrement(p.Z) - p.Z)));
        return MathF.Max(0.00001f, ulp * 4);
    }

    public void Append(FluidGeometryBuilder builder)
    {
        if (drawSupported) { supported.Append(builder); return; }
        for (int i = 0; i < Triangles.Length; i += 3)
            builder.AddTriangle(Vertex(Triangles[i]), Vertex(Triangles[i + 1]), Vertex(Triangles[i + 2]));
    }
    private FluidVertex Vertex(int i)
    {
        float h = (float)(heights[i] * VolumeCorrection);
        // SkinLift is only rasterization separation and is excluded from optical thickness and inventory.
        return new(positions[i], normals[i], Vector2.Zero, h, Math.Clamp(h / 0.00002f, 0, 1));
    }

    private static Vector3 Point(FilmCellGeometry g, Vector3 bary) => g.A * bary.X + g.B * bary.Y + g.C * bary.Z;
    private static Vector2 Project(Vector3 p, Vector3 origin, Vector3 u, Vector3 v)
        => new(Vector3.Dot(p - origin, u), Vector3.Dot(p - origin, v));
    private static bool ChartBary(FilmCellGeometry g, Vector2 p, Vector3 origin, Vector3 u, Vector3 v, out Vector3 bary)
    {
        var a = Project(g.A, origin, u, v); var ab = Project(g.B, origin, u, v) - a;
        var ac = Project(g.C, origin, u, v) - a; var ap = p - a;
        float determinant = ab.X * ac.Y - ab.Y * ac.X;
        bary = default;
        if (!(determinant > 1e-14f)) return false;
        float y = (ap.X * ac.Y - ap.Y * ac.X) / determinant;
        float z = (ab.X * ap.Y - ab.Y * ap.X) / determinant;
        bary = new(1 - y - z, y, z);
        return float.IsFinite(bary.X) && float.IsFinite(bary.Y) && float.IsFinite(bary.Z);
    }
    private static bool Inside(Vector3 b) => b.X >= -1e-5f && b.Y >= -1e-5f && b.Z >= -1e-5f;
    private static Vector3 ClampBary(Vector3 b) { b = Vector3.Max(b, Vector3.Zero); return b / (b.X + b.Y + b.Z); }
    private static float Component(Vector3 b, int e) => e == 0 ? b.X : e == 1 ? b.Y : b.Z;
    private static Vector2[] MakeDirections()
    {
        var result = new Vector2[Sides];
        for (int i = 0; i < Sides; i++) result[i] = new(MathF.Cos(MathF.Tau * i / Sides), MathF.Sin(MathF.Tau * i / Sides));
        return result;
    }
    private static int[] MakeTriangles()
    {
        var result = new int[(Sides + (Rings - 1) * Sides * 2) * 3]; int at = 0;
        for (int side = 0; side < Sides; side++)
        { result[at++] = 0; result[at++] = 1 + side; result[at++] = 1 + (side + 1) % Sides; }
        for (int ring = 1; ring < Rings; ring++)
            for (int side = 0; side < Sides; side++)
            {
                int a = 1 + (ring - 1) * Sides + side, b = 1 + ring * Sides + side;
                int c = 1 + ring * Sides + (side + 1) % Sides, d = 1 + (ring - 1) * Sides + (side + 1) % Sides;
                result[at++] = a; result[at++] = b; result[at++] = c;
                result[at++] = a; result[at++] = c; result[at++] = d;
            }
        return result;
    }
    private static int[] MakeEdges()
    {
        var unique = new HashSet<(int, int)>();
        var result = new List<int>();
        for (int i = 0; i < Triangles.Length; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = Triangles[i + e], b = Triangles[i + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (unique.Add(key)) { result.Add(key.Item1); result.Add(key.Item2); }
            }
        return result.ToArray();
    }

    // When a full circular contact line crosses a genuine unknown boundary, render only the
    // connected, verified skin support inside the nominal footprint. No seam weld is inferred.
    private sealed class SupportedPatch
    {
        private const int MaximumCells = 24, MaximumTriangles = 256, MaximumVertices = MaximumTriangles * 3;
        private int[] mappedCells = new int[MaximumVertices], nextCells = new int[MaximumVertices];
        private Vector3[] mappedBary = new Vector3[MaximumVertices], nextBary = new Vector3[MaximumVertices];
        private float[] profile = new float[MaximumVertices], nextProfile = new float[MaximumVertices];
        private Vector2[] gradients = new Vector2[MaximumVertices], nextGradients = new Vector2[MaximumVertices];
        private readonly Vector3[] skin = new Vector3[MaximumVertices], free = new Vector3[MaximumVertices], freeNormals = new Vector3[MaximumVertices];
        private readonly int[] component = new int[MaximumCells];
        private readonly bool[] selected = new bool[128], seen = new bool[128];
        private readonly Vector2[] boundaryA = new Vector2[MaximumCells * 3], boundaryB = new Vector2[MaximumCells * 3];
        private readonly Dictionary<Vector2, (float Height, Vector2 Gradient)> profileSamples = new();
        private int count, componentCount, boundaryCount, originCell;
        private Vector3 originBary;
        private uint generation;
        private SurfaceFilmRuntime? owner;
        private double cachedRadius, correction;
        public bool Pending { get; private set; }
        public int TriangleCount => count / 3;
        private readonly record struct ClipPoint(Vector2 XY, Vector3 Bary);

        public void Invalidate() { count = 0; owner = null; }

        public bool Prepare(SurfaceFilmRuntime film, FluidSurfaceAnchor anchor, Vector3 center, Vector3 axis,
            Vector3 u, Vector3 v, double radius, double volume, ref int budget, out string reason)
        {
            Pending = false; reason = string.Empty;
            bool compatible = count > 0 && owner == film && generation == anchor.Generation &&
                film.TryGetCapGeometry(originCell, out var oldOrigin) &&
                Vector3.DistanceSquared(Point(oldOrigin, originBary), center) <= 4e-8f;
            bool resize = !compatible || Math.Abs(radius - cachedRadius) > cachedRadius * 0.1;
            if (resize)
            {
                if (!Build(film, anchor, center, axis, u, v, radius, ref budget, out reason))
                {
                    Pending = true;
                    if (!compatible) return false;
                }
            }
            if (!Repose(film, center, axis, u, v, volume, ref budget))
            { reason = budget == 0 ? "Supported cap pose work budget exhausted" : "Supported cap chart folded/height bound/pose unavailable"; Pending = true; return false; }
            return true;
        }

        private bool Build(SurfaceFilmRuntime film, FluidSurfaceAnchor anchor, Vector3 center, Vector3 axis,
            Vector3 u, Vector3 v, double radius, ref int budget, out string reason)
        {
            reason = string.Empty;
            if (!film.TryGetCell(anchor, out int seed)) { reason = "Supported cap source cell missing"; return false; }
            Array.Clear(selected); Array.Clear(seen); componentCount = boundaryCount = 0;
            profileSamples.Clear();
            component[componentCount++] = seed; selected[seed] = seen[seed] = true;
            float footprint = (float)(radius * Math.Sin(0.9));
            for (int cursor = 0; cursor < componentCount && componentCount < MaximumCells; cursor++)
                for (int edge = 0; edge < 3 && componentCount < MaximumCells; edge++)
                {
                    if (!Consume(ref budget)) { reason = "Supported cap component budget exhausted"; return false; }
                    int next = film.GetCapNeighbour(component[cursor], edge);
                    if (next < 0 || next >= selected.Length || seen[next]) continue;
                    seen[next] = true;
                    if (!film.TryGetCapGeometry(next, out var g) || Vector3.Dot(g.Normal, axis) < 0.5f) continue;
                    var a = Project(g.A, center, u, v); var b = Project(g.B, center, u, v); var c = Project(g.C, center, u, v);
                    var minimum = Vector2.Min(a, Vector2.Min(b, c)); var maximum = Vector2.Max(a, Vector2.Max(b, c));
                    if (minimum.X > footprint || minimum.Y > footprint || maximum.X < -footprint || maximum.Y < -footprint) continue;
                    selected[next] = true; component[componentCount++] = next;
                }
            for (int i = 0; i < componentCount; i++)
            {
                int cell = component[i];
                if (!film.TryGetCapGeometry(cell, out var g) || Vector3.Dot(g.Normal, axis) < 0.5f)
                { reason = "Supported cap source chart unavailable/folded"; return false; }
                for (int edge = 0; edge < 3; edge++)
                {
                    int other = film.GetCapNeighbour(cell, edge);
                    if (other >= 0 && other < selected.Length && selected[other]) continue;
                    var a = edge == 0 ? g.B : edge == 1 ? g.C : g.A;
                    var b = edge == 0 ? g.C : edge == 1 ? g.A : g.B;
                    boundaryA[boundaryCount] = Project(a, center, u, v); boundaryB[boundaryCount++] = Project(b, center, u, v);
                }
            }
            int nextCount = 0;
            Span<ClipPoint> polygon = stackalloc ClipPoint[24];
            Span<ClipPoint> scratch = stackalloc ClipPoint[24];
            for (int i = 0; i < componentCount; i++)
            {
                int cell = component[i]; film.TryGetCapGeometry(cell, out var g);
                polygon[0] = new(Project(g.A, center, u, v), Vector3.UnitX);
                polygon[1] = new(Project(g.B, center, u, v), Vector3.UnitY);
                polygon[2] = new(Project(g.C, center, u, v), Vector3.UnitZ);
                int vertices = 3;
                // Clip the actual skin triangle against a regular inscribed 16-sided footprint.
                for (int plane = 0; plane < Sides && vertices > 0; plane++)
                {
                    float angle = MathF.Tau * (plane + 0.5f) / Sides;
                    var n = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    float limit = footprint * MathF.Cos(MathF.PI / Sides);
                    int written = 0;
                    for (int edge = 0; edge < vertices; edge++)
                    {
                        if (!Consume(ref budget)) { reason = "Supported cap polygon budget exhausted"; return false; }
                        var a = polygon[edge]; var b = polygon[(edge + 1) % vertices];
                        float da = Vector2.Dot(a.XY, n) - limit, db = Vector2.Dot(b.XY, n) - limit;
                        if (da <= 0) scratch[written++] = a;
                        if ((da <= 0) != (db <= 0))
                        {
                            float t = da / (da - db);
                            scratch[written++] = new(Vector2.Lerp(a.XY, b.XY, t), Vector3.Lerp(a.Bary, b.Bary, t));
                        }
                    }
                    vertices = written; scratch[..written].CopyTo(polygon);
                }
                if (vertices < 3) continue;
                Vector2 average = default; Vector3 averageBary = default;
                for (int j = 0; j < vertices; j++) { average += polygon[j].XY; averageBary += polygon[j].Bary; }
                var middle = new ClipPoint(average / vertices, averageBary / vertices);
                for (int j = 0; j < vertices; j++)
                {
                    var a = polygon[j]; var b = polygon[(j + 1) % vertices];
                    float twiceArea = (a.XY.X - middle.XY.X) * (b.XY.Y - middle.XY.Y) - (a.XY.Y - middle.XY.Y) * (b.XY.X - middle.XY.X);
                    if (twiceArea <= 1e-16f) continue;
                    if (nextCount + 3 > MaximumVertices) { reason = "Supported cap finite triangle capacity exhausted"; return false; }
                    if (!Store(cell, middle, radius, footprint, ref nextCount, ref budget) ||
                        !Store(cell, a, radius, footprint, ref nextCount, ref budget) ||
                        !Store(cell, b, radius, footprint, ref nextCount, ref budget))
                    { reason = "Supported cap height profile budget exhausted"; return false; }
                }
            }
            if (nextCount == 0) { reason = "No positive verified skin support"; return false; }
            // Complete private buffers are installed together. Failed expansion never changes the old profile.
            (mappedCells, nextCells) = (nextCells, mappedCells); (mappedBary, nextBary) = (nextBary, mappedBary);
            (profile, nextProfile) = (nextProfile, profile); (gradients, nextGradients) = (nextGradients, gradients);
            count = nextCount; owner = film; generation = anchor.Generation; originCell = seed;
            originBary = anchor.Barycentric; cachedRadius = radius;
            return true;
        }

        private bool Store(int cell, ClipPoint point, double radius, float footprint, ref int at, ref int budget)
        {
            if (profileSamples.TryGetValue(point.XY, out var cached))
            {
                if (!Consume(ref budget)) return false;
                nextCells[at] = cell; nextBary[at] = ClampBary(point.Bary);
                nextProfile[at] = cached.Height; nextGradients[at] = cached.Gradient; at++;
                return true;
            }
            float distance = float.PositiveInfinity; Vector2 distanceGradient = default;
            for (int i = 0; i < boundaryCount; i++)
            {
                if (!Consume(ref budget)) return false;
                var edge = boundaryB[i] - boundaryA[i];
                float t = edge.LengthSquared() > 1e-20f ? Math.Clamp(Vector2.Dot(point.XY - boundaryA[i], edge) / edge.LengthSquared(), 0, 1) : 0;
                var delta = point.XY - (boundaryA[i] + edge * t); float candidate = delta.Length();
                if (candidate < distance) { distance = candidate; distanceGradient = candidate > 1e-12f ? delta / candidate : Vector2.Zero; }
            }
            for (int i = 0; i < Sides; i++)
            {
                if (!Consume(ref budget)) return false;
                float angle = MathF.Tau * (i + 0.5f) / Sides;
                var n = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                float candidate = footprint * MathF.Cos(MathF.PI / Sides) - Vector2.Dot(point.XY, n);
                if (candidate < distance) { distance = candidate; distanceGradient = -n; }
            }
            float fadeScale = MathF.Max(footprint * 0.25f, 1e-7f);
            float fade = Math.Clamp(distance / fadeScale, 0, 1);
            float vertical = MathF.Sqrt(MathF.Max(0, (float)(radius * radius) - point.XY.LengthSquared()));
            float baseHeight = MathF.Max(0, vertical - (float)(radius * Math.Cos(0.9)));
            var gradient = vertical > 1e-12f ? -point.XY / vertical * fade : Vector2.Zero;
            if (fade > 0 && fade < 1) gradient += distanceGradient * (baseHeight / fadeScale);
            nextCells[at] = cell; nextBary[at] = ClampBary(point.Bary);
            nextProfile[at] = baseHeight * fade; nextGradients[at] = gradient; at++;
            profileSamples[point.XY] = (baseHeight * fade, gradient);
            return true;
        }

        private bool Repose(SurfaceFilmRuntime film, Vector3 center, Vector3 axis, Vector3 u, Vector3 v, double volume, ref int budget)
        {
            for (int i = 0; i < count; i++)
            {
                if (!Consume(ref budget) || !film.TryGetCapGeometry(mappedCells[i], out var g) || Vector3.Dot(g.Normal, axis) < 0.5f) return false;
                skin[i] = Point(g, mappedBary[i]);
                freeNormals[i] = g.Normal / Vector3.Dot(g.Normal, axis);
            }
            double integral = 0;
            for (int i = 0; i < count; i += 3)
            {
                double area = Vector3.Dot(Vector3.Cross(skin[i + 1] - skin[i], skin[i + 2] - skin[i]), axis) * 0.5;
                if (area <= 1e-16 || !double.IsFinite(area)) return false;
                integral += area * (profile[i] + profile[i + 1] + profile[i + 2]) / 3;
            }
            if (!(integral > 0)) return false;
            correction = volume / integral;
            if (!double.IsFinite(correction) || correction <= 0) return false;
            for (int i = 0; i < count; i++)
            {
                double height = profile[i] * correction;
                if (!double.IsFinite(height) || height > 0.02) return false;
                free[i] = skin[i] + axis * (float)(height + SkinLift);
                var n = freeNormals[i] - (u * gradients[i].X + v * gradients[i].Y) * (float)correction;
                freeNormals[i] = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : axis;
            }
            return true;
        }

        public void Append(FluidGeometryBuilder builder)
        {
            for (int i = 0; i < count; i += 3) builder.AddTriangle(Vertex(i), Vertex(i + 1), Vertex(i + 2));
        }
        private FluidVertex Vertex(int i)
        {
            float h = (float)(profile[i] * correction);
            return new(free[i], freeNormals[i], Vector2.Zero, h, Math.Clamp(h / 0.00002f, 0, 1));
        }
    }
}
