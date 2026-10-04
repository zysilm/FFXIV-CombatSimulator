// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Rendering.WorldGeometry;
namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class GroundFilmRuntime
{
    private readonly Vector3[] vertices = new Vector3[Capacity * 3], supportNormals = new Vector3[Capacity * 3];
    private readonly Vector3[] normals = new Vector3[Capacity * 3], lifted = new Vector3[Capacity * 3];
    private readonly double[] areas = new double[Capacity * 3], volumes = new double[Capacity * 3], heights = new double[Capacity * 3];
    private int vertexCount;

    private int VertexIndex(Vector3 p, Vector3 normal)
    {
        for (var i = 0; i < vertexCount; i++)
            if (Near(vertices[i], p) && Vector3.Dot(supportNormals[i], normal) > 0.5f) return i;
        var index = vertexCount++; vertices[index] = p; supportNormals[index] = normal;
        return index;
    }

    public void AppendGeometry(FluidGeometryBuilder builder)
    {
        if (Volume <= 0 || vertexCount == 0) return;
        Array.Clear(areas, 0, vertexCount); Array.Clear(volumes, 0, vertexCount); Array.Clear(normals, 0, vertexCount);
        for (var i = 0; i < film.CellCount; i++)
        {
            var sample = film.GetCell(i); var cell = cells[i];
            AccumulateGroundVertex(cell.A, sample); AccumulateGroundVertex(cell.B, sample); AccumulateGroundVertex(cell.C, sample);
        }
        for (var i = 0; i < vertexCount; i++)
        {
            heights[i] = areas[i] > 0 ? volumes[i] / areas[i] : 0;
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : supportNormals[i];
        }
        double reconstructed = 0;
        for (var i = 0; i < film.CellCount; i++)
        {
            var cell = cells[i]; reconstructed += film.GetCell(i).Geometry.Area * (heights[cell.A] + heights[cell.B] + heights[cell.C]) / 3;
        }
        var correction = reconstructed > 0 ? Volume / reconstructed : 0;
        for (var i = 0; i < vertexCount; i++) lifted[i] = vertices[i] + normals[i] * (float)(heights[i] * correction + 0.00008);
        Array.Clear(normals, 0, vertexCount);
        for (var i = 0; i < film.CellCount; i++)
        {
            var cell = cells[i]; var n = Vector3.Cross(lifted[cell.B] - lifted[cell.A], lifted[cell.C] - lifted[cell.A]);
            if (n.Y < 0) n = -n;
            normals[cell.A] += n; normals[cell.B] += n; normals[cell.C] += n;
        }
        for (var i = 0; i < vertexCount; i++) normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : supportNormals[i];
        for (var i = 0; i < film.CellCount; i++)
        {
            var cell = cells[i]; if (heights[cell.A] + heights[cell.B] + heights[cell.C] <= 1e-9) continue;
            builder.AddTriangle(GroundVertex(cell.A, correction), GroundVertex(cell.B, correction), GroundVertex(cell.C, correction));
        }
    }
    private void AccumulateGroundVertex(int index, FilmCellSample sample)
    {
        areas[index] += sample.Geometry.Area / 3; volumes[index] += sample.Volume / 3;
        normals[index] += sample.Geometry.Normal * (float)sample.Geometry.Area;
    }
    private FluidVertex GroundVertex(int index, double correction)
    {
        var h = (float)(heights[index] * correction);
        return new FluidVertex(lifted[index], normals[index], Vector2.Zero, h, Math.Clamp(h / 0.00001f, 0, 1));
    }

    private static int ClipTile(GroundSupportHit support, int x, int z, Span<FilmCellGeometry> output)
    {
        Span<Vector3> a = stackalloc Vector3[8]; Span<Vector3> b = stackalloc Vector3[8];
        a[0] = support.A; a[1] = support.B; a[2] = support.C;
        var count = 3;
        for (var plane = 0; plane < 4; plane++)
        {
            var axis = plane < 2 ? 0 : 2;
            var boundary = (plane == 0 ? x : plane == 1 ? x + 1 : plane == 2 ? z : z + 1) * GridSize;
            var positive = plane == 0 || plane == 2;
            var result = 0;
            for (var i = 0; i < count; i++)
            {
                var p = a[i]; var q = a[(i + 1) % count];
                var pd = (axis == 0 ? p.X : p.Z) - boundary;
                var qd = (axis == 0 ? q.X : q.Z) - boundary;
                var pin = positive ? pd >= 0 : pd <= 0; var qin = positive ? qd >= 0 : qd <= 0;
                if (pin && !AppendClipVertex(b, ref result, p)) return 0;
                if (pin == qin) continue;
                var point = Vector3.Lerp(p, q, pd / (pd - qd));
                if (axis == 0) point.X = boundary; else point.Z = boundary;
                if (!AppendClipVertex(b, ref result, point)) return 0;
            }
            if (result > 1 && Vector3.DistanceSquared(b[0], b[result - 1]) < 1e-16f) result--;
            if (result < 3) return 0;
            b[..result].CopyTo(a); count = result;
        }
        var pieces = 0;
        for (var i = 1; i < count - 1; i++)
        {
            var geometry = new FilmCellGeometry(a[0], a[i], a[i + 1], support.Normal);
            if (geometry.Area >= MinimumArea) output[pieces++] = geometry;
        }
        return pieces;
    }

    private static bool AppendClipVertex(Span<Vector3> polygon, ref int count, Vector3 p)
    {
        if (count > 0 && Vector3.DistanceSquared(polygon[count - 1], p) < 1e-16f) return true;
        if (count == polygon.Length) return false;
        polygon[count++] = p; return true;
    }

    private static bool Contains(FilmCellGeometry g, Vector3 point)
    {
        var v0 = g.B - g.A; var v1 = g.C - g.A; var v2 = point - g.A;
        var n = Vector3.Cross(v0, v1);
        var length = n.Length(); if (length < 1e-10f || MathF.Abs(Vector3.Dot(v2, n / length)) > 0.0001f) return false;
        var aa = Vector3.Dot(v0, v0); var ab = Vector3.Dot(v0, v1); var bb = Vector3.Dot(v1, v1);
        var denominator = aa * bb - ab * ab;
        if (denominator <= 1e-20f) return false;
        var ap = Vector3.Dot(v0, v2); var bp = Vector3.Dot(v1, v2);
        var u = (bb * ap - ab * bp) / denominator; var v = (aa * bp - ab * ap) / denominator;
        return u >= -1e-5f && v >= -1e-5f && u + v <= 1 + 1e-5f;
    }

    private static double SharedEdge(FilmCellGeometry a, FilmCellGeometry b, out int edgeA, out int edgeB)
    {
        edgeA = edgeB = -1;
        if (Vector3.Dot(a.Normal, b.Normal) < 0.5f) return 0;
        Span<Vector3> av = stackalloc Vector3[3] { a.A, a.B, a.C };
        Span<Vector3> bv = stackalloc Vector3[3] { b.A, b.B, b.C };
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var p = av[i]; var q = av[(i + 1) % 3]; var r = bv[j]; var s = bv[(j + 1) % 3];
                var edge = q - p; var length = edge.Length(); if (length <= WeldTolerance) continue;
                var axis = edge / length;
                if (Vector3.Cross(r - p, axis).Length() > WeldTolerance || Vector3.Cross(s - p, axis).Length() > WeldTolerance) continue;
                var left = Vector3.Dot(r - p, axis); var right = Vector3.Dot(s - p, axis);
                var overlap = Math.Min(length, Math.Max(left, right)) - Math.Max(0, Math.Min(left, right));
                if (overlap <= WeldTolerance) continue;
                // Reject coplanar overlapping faces: material neighbours must lie
                // on opposite sides of their actual common edge, not share area.
                var sideA = Vector3.Dot(Vector3.Cross(axis, a.Center - p), a.Normal);
                var sideB = Vector3.Dot(Vector3.Cross(axis, b.Center - p), a.Normal);
                if (sideA * sideB < 0) { edgeA = i; edgeB = j; return overlap; }
            }
        return 0;
    }
}
