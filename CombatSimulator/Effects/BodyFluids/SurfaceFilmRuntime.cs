// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>
/// Production bridge between a verified local material patch, conservative film inventory and
/// one continuous rendered surface. Does not own hooks, capture poses or discover mouth landmarks.
/// </summary>
internal sealed class SurfaceFilmRuntime
{
    private const int Capacity = 128;
    private readonly CharacterFluidSurface surface;
    private readonly SurfaceFilm film = new(Capacity, Capacity * 3);
    private readonly Dictionary<int, int> triangleCells = new(Capacity);
    private readonly Dictionary<int, int> vertexIndices = new(Capacity * 3);
    private readonly FluidSurfaceAnchor[] anchors = new FluidSurfaceAnchor[Capacity];
    private readonly FilmCellGeometry[] pendingGeometry = new FilmCellGeometry[Capacity];
    private readonly int[] vertexA = new int[Capacity], vertexB = new int[Capacity], vertexC = new int[Capacity];
    private readonly int[] capNeighbours = new int[Capacity * 3];
    public bool GeometryReady { get; private set; }
    private uint geometryPoseRevision;
    private readonly int[] capEdgeKinds = new int[Capacity * 3];
    private readonly Vector3[] positions = new Vector3[Capacity * 3], normals = new Vector3[Capacity * 3];
    private readonly Vector3[] liftedPositions = new Vector3[Capacity * 3], freeNormals = new Vector3[Capacity * 3];
    private readonly double[] areaSums = new double[Capacity * 3], volumeSums = new double[Capacity * 3], heights = new double[Capacity * 3];
    private uint generation;
    private int sourceCell = -1, vertexCount;
    public double Volume => film.TotalVolume;
    public double DeferredSeconds { get; private set; }
    public int CellCount => film.CellCount;
    public uint Generation => generation;

    public (double SourceThickness, double PeakThickness, double WetArea) Inspect()
    {
        double peak = 0, wetArea = 0;
        for (var i = 0; i < film.CellCount; i++)
        {
            var cell = film.GetCell(i);
            peak = Math.Max(peak, cell.Thickness);
            if (cell.Volume > 1e-15) wetArea += cell.Geometry.Area;
        }
        return (sourceCell >= 0 ? film.GetCell(sourceCell).Thickness : 0, peak, wetArea);
    }

    public SurfaceFilmRuntime(CharacterFluidSurface surface)
    {
        this.surface = surface;
        Array.Fill(capNeighbours, -1);
    }

    public void SetMaterial(double viscosity) => film.SetMaterial(viscosity);

    public bool TryBind(FluidSurfaceAnchor source)
    {
        if (!surface.TryEvaluate(source, out _)) return false;
        if (generation == source.Generation && triangleCells.TryGetValue(source.Triangle, out sourceCell)) return true;
        if (film.TotalVolume > 0) return false; // Controller must explicitly retire previous generation inventory.
        Clear();
        var patch = surface.GetAdjacentTriangles(source, Capacity);
        foreach (var anchor in patch)
        {
            if (!surface.TryGetDiagnosticTriangle(anchor, out var a, out var b, out var c, out var normal) ||
                !surface.TryGetTriangleVertexIds(anchor.Generation, anchor.Triangle, out var ia, out var ib, out var ic)) continue;
            if (!film.TryAddCell(anchor.Generation, anchor.Triangle, new FilmCellGeometry(a, b, c, normal), out var cell)) continue;
            anchors[cell] = anchor;
            triangleCells[anchor.Triangle] = cell;
            vertexA[cell] = VertexIndex(ia); vertexB[cell] = VertexIndex(ib); vertexC[cell] = VertexIndex(ic);
        }
        if (!triangleCells.TryGetValue(source.Triangle, out sourceCell)) { Clear(); return false; }
        generation = source.Generation;
        for (var i = 0; i < film.CellCount; i++)
            for (var edge = 0; edge < 3; edge++)
            {
                if (!surface.TryGetAdjacentTriangle(anchors[i], edge, out var neighbour, out var width))
                { capEdgeKinds[i * 3 + edge] = surface.BudgetExhausted ? 3 : 1; continue; }
                if (triangleCells.TryGetValue(neighbour.Triangle, out var other))
                {
                    capNeighbours[i * 3 + edge] = other;
                    capEdgeKinds[i * 3 + edge] = 0;
                    if (other > i) film.TryConnect(i, other, width);
                }
                else capEdgeKinds[i * 3 + edge] = 2;
            }
        GeometryReady = true;
        geometryPoseRevision = surface.PoseRevision;
        return true;
    }

    private int VertexIndex(int id)
    {
        if (vertexIndices.TryGetValue(id, out var index)) return index;
        index = vertexCount++;
        vertexIndices.Add(id, index);
        return index;
    }

    public double AddVolume(double volume) => sourceCell < 0 ? 0 : film.AddVolume(sourceCell, volume);

    public bool TryGetCell(FluidSurfaceAnchor anchor, out int index)
    {
        index = -1;
        return anchor.Generation == generation && triangleCells.TryGetValue(anchor.Triangle, out index);
    }

    public FilmCellSample GetCell(int index) => film.GetCell(index);
    public FluidSurfaceAnchor GetAnchor(int index) => anchors[index];
    // Read only the already verified film geometry. Cap rendering performs no new skin/native queries.
    public bool TryGetCapGeometry(int cell, out FilmCellGeometry geometry)
    {
        geometry = default;
        if (!GeometryReady || geometryPoseRevision != surface.PoseRevision || generation != surface.Generation || !surface.HasSurface || cell < 0 || cell >= film.CellCount) return false;
        geometry = film.GetCell(cell).Geometry;
        return true;
    }
    public int GetCapNeighbour(int cell, int edge) => cell >= 0 && cell < film.CellCount && edge >= 0 && edge < 3
        ? capNeighbours[cell * 3 + edge] : -1;
    public string GetCapEdgeDiagnostic(int cell, int edge) => cell < 0 || cell >= film.CellCount || edge < 0 || edge > 2
        ? "invalid cell/edge" : $"tri={anchors[cell].Triangle},edge={edge},kind={(capEdgeKinds[cell * 3 + edge] == 3 ? "bind pose/budget unknown" : capEdgeKinds[cell * 3 + edge] == 2 ? "outside bounded patch" : "raw split/open/nonmanifold boundary")}";
    public double TakeVolume(int index, double requested) => film.TakeVolume(index, requested);
    public double AddVolume(FluidSurfaceAnchor anchor, double requested) =>
        TryGetCell(anchor, out var index) ? film.AddVolume(index, requested) : 0;

    public bool Advance(double seconds, Vector3 gravity)
    {
        if (!RefreshPoseGeometry()) return false;
        var result = film.Advance(seconds, gravity);
        DeferredSeconds = result.DeferredSeconds;
        return true;
    }

    public bool RefreshPoseGeometry()
    {
        if (GeometryReady && geometryPoseRevision == surface.PoseRevision && generation == surface.Generation && surface.HasSurface) return true;
        GeometryReady = false;
        if (generation != surface.Generation || sourceCell < 0) return false;
        // Validate the complete patch before updating any geometry; unavailable poses freeze inventory.
        for (var i = 0; i < film.CellCount; i++)
        {
            if (!surface.TryGetDiagnosticTriangle(anchors[i], out var a, out var b, out var c, out var normal)) return false;
            var next = new FilmCellGeometry(a, b, c, normal);
            if (!double.IsFinite(next.Area) || next.Area <= 1e-12) return false;
            pendingGeometry[i] = next;
        }
        for (var i = 0; i < film.CellCount; i++)
            if (!film.UpdateGeometry(i, pendingGeometry[i])) return false;
        GeometryReady = true;
        geometryPoseRevision = surface.PoseRevision;
        return true;
    }

    public void AppendGeometry(FluidGeometryBuilder builder)
    {
        if (!RefreshPoseGeometry()) return;
        if (!GeometryReady || generation != surface.Generation || film.TotalVolume <= 0) return;
        Array.Clear(normals, 0, vertexCount);
        Array.Clear(areaSums, 0, vertexCount); Array.Clear(volumeSums, 0, vertexCount);
        for (var i = 0; i < film.CellCount; i++)
        {
            var cell = film.GetCell(i);
            var g = cell.Geometry;
            Accumulate(vertexA[i], g.A, g.Normal, g.Area, cell.Volume);
            Accumulate(vertexB[i], g.B, g.Normal, g.Area, cell.Volume);
            Accumulate(vertexC[i], g.C, g.Normal, g.Area, cell.Volume);
        }
        for (var i = 0; i < vertexCount; i++)
        {
            heights[i] = areaSums[i] > 0 ? volumeSums[i] / areaSums[i] : 0;
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        }
        double reconstructedVolume = 0;
        for (var i = 0; i < film.CellCount; i++)
            reconstructedVolume += film.GetCell(i).Geometry.Area *
                (heights[vertexA[i]] + heights[vertexB[i]] + heights[vertexC[i]]) / 3;
        var correction = reconstructedVolume > 0 ? film.TotalVolume / reconstructedVolume : 0;
        Array.Clear(freeNormals, 0, vertexCount);
        for (var i = 0; i < vertexCount; i++)
            liftedPositions[i] = positions[i] + normals[i] * (float)(heights[i] * correction + 0.00008);
        for (var i = 0; i < film.CellCount; i++)
        {
            var a = vertexA[i]; var b = vertexB[i]; var c = vertexC[i];
            var areaNormal = Vector3.Cross(liftedPositions[b] - liftedPositions[a], liftedPositions[c] - liftedPositions[a]);
            if (Vector3.Dot(areaNormal, normals[a] + normals[b] + normals[c]) < 0) areaNormal = -areaNormal;
            freeNormals[a] += areaNormal; freeNormals[b] += areaNormal; freeNormals[c] += areaNormal;
        }
        for (var i = 0; i < vertexCount; i++)
            freeNormals[i] = freeNormals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(freeNormals[i]) : normals[i];
        for (var i = 0; i < film.CellCount; i++)
        {
            var a = vertexA[i]; var b = vertexB[i]; var c = vertexC[i];
            if (heights[a] + heights[b] + heights[c] <= 1e-9) continue;
            builder.AddTriangle(RenderVertex(a, correction), RenderVertex(b, correction), RenderVertex(c, correction));
        }
    }

    private void Accumulate(int i, Vector3 p, Vector3 normal, double area, double volume)
    {
        positions[i] = p;
        normals[i] += normal * (float)area;
        areaSums[i] += area / 3;
        volumeSums[i] += volume / 3;
    }

    private FluidVertex RenderVertex(int i, double correction)
    {
        var h = (float)(heights[i] * correction);
        // Offset only resolves z fighting; optical thickness remains the liquid thickness.
        return new FluidVertex(liftedPositions[i], freeNormals[i], Vector2.Zero,
            h, Math.Clamp(h / 0.00001f, 0, 1));
    }

    public double Clear()
    {
        var retired = film.Clear();
        triangleCells.Clear(); vertexIndices.Clear();
        generation = 0; sourceCell = -1; vertexCount = 0; DeferredSeconds = 0;
        GeometryReady = false;
        Array.Fill(capNeighbours, -1);
        return retired;
    }
}
