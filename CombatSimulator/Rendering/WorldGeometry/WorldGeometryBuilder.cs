// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>Bounded triangle builder. No heap work or GPU access in primitive methods.</summary>
public sealed class WorldGeometryBuilder
{
    private const int Sides = 8;
    private static readonly Vector2[] Circle = MakeCircle();
    private readonly WorldVertex[] vertices;
    public int Count { get; private set; }
    public bool Overflowed { get; private set; }
    public ReadOnlySpan<WorldVertex> Vertices => vertices.AsSpan(0, Count);
    public WorldGeometryBuilder(int capacity = 32766)
    {
        if (capacity < 3 || capacity > 32766) throw new ArgumentOutOfRangeException(nameof(capacity));
        vertices = new WorldVertex[capacity - capacity % 3];
    }
    public void Reset() { Count = 0; Overflowed = false; }

    private static Vector2[] MakeCircle()
    {
        var points = new Vector2[Sides];
        for (var i = 0; i < Sides; i++)
            points[i] = new Vector2(MathF.Cos(i * MathF.Tau / Sides), MathF.Sin(i * MathF.Tau / Sides));
        return points;
    }

    private void Triangle(Vector3 a, Vector3 na, Vector3 b, Vector3 nb, Vector3 c, Vector3 nc, Vector4 color)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(na) || !Finite(nb) || !Finite(nc) || !Finite(color)) return;
        if (Count + 3 > vertices.Length) { Overflowed = true; return; }
        vertices[Count++] = new WorldVertex(a, na, color);
        vertices[Count++] = new WorldVertex(b, nb, color);
        vertices[Count++] = new WorldVertex(c, nc, color);
    }

    public void AddTriangle(WorldVertex a, WorldVertex b, WorldVertex c)
    {
        if (!Finite(a.Position) || !Finite(b.Position) || !Finite(c.Position) || !Finite(a.Normal) ||
            !Finite(b.Normal) || !Finite(c.Normal) || !Finite(a.Color) || !Finite(b.Color) || !Finite(c.Color)) return;
        if (Count + 3 > vertices.Length) { Overflowed = true; return; }
        vertices[Count++] = a; vertices[Count++] = b; vertices[Count++] = c;
    }

    public void AddEllipsoid(Vector3 center, float radius, Vector4 color, float verticalScale = 1f)
    {
        if (!float.IsFinite(radius) || radius <= 0 || !Finite(center) || !float.IsFinite(verticalScale) || verticalScale <= 0) return;
        var top = center + Vector3.UnitY * radius * verticalScale;
        var bottom = center - Vector3.UnitY * radius * verticalScale;
        for (var i = 0; i < Sides; i++)
        {
            var j = (i + 1) % Sides;
            var ni = new Vector3(Circle[i].X, 0, Circle[i].Y);
            var nj = new Vector3(Circle[j].X, 0, Circle[j].Y);
            Triangle(top, Vector3.UnitY, center + ni * radius, ni, center + nj * radius, nj, color);
            Triangle(bottom, -Vector3.UnitY, center + nj * radius, nj, center + ni * radius, ni, color);
        }
    }

    public void AddTube(Vector3 a, Vector3 b, float radius, Vector4 color)
    {
        var axis = b - a;
        if (!Finite(a) || !Finite(b) || axis.LengthSquared() < 1e-10f || !float.IsFinite(radius) || radius <= 0) return;
        axis = Vector3.Normalize(axis);
        var tangent = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var bitangent = Vector3.Cross(axis, tangent);
        for (var i = 0; i < Sides; i++)
        {
            var j = (i + 1) % Sides;
            var ni = tangent * Circle[i].X + bitangent * Circle[i].Y;
            var nj = tangent * Circle[j].X + bitangent * Circle[j].Y;
            Triangle(a + ni * radius, ni, b + ni * radius, ni, b + nj * radius, nj, color);
            Triangle(a + ni * radius, ni, b + nj * radius, nj, a + nj * radius, nj, color);
        }
    }

    /// <summary>A small raised disk on a supplied surface plane; does not sample or deform the surface.</summary>
    public void AddSurfaceBead(Vector3 center, Vector3 normal, float radius, Vector4 color)
    {
        if (!Finite(center) || !Finite(normal) || normal.LengthSquared() < 0.5f || !float.IsFinite(radius) || radius <= 0) return;
        var tangent = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var bitangent = Vector3.Cross(normal, tangent);
        var raised = center + normal * 0.0005f;
        for (var i = 0; i < Sides; i++)
        {
            var j = (i + 1) % Sides;
            var ai = tangent * Circle[i].X + bitangent * Circle[i].Y;
            var aj = tangent * Circle[j].X + bitangent * Circle[j].Y;
            Triangle(raised + normal * radius * 0.3f, normal,
                raised + ai * radius, Vector3.Normalize(normal + ai * 0.3f),
                raised + aj * radius, Vector3.Normalize(normal + aj * 0.3f), color);
        }
    }

    public void AddSurfaceTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector4 color) =>
        Triangle(a + normal * 0.0008f, normal, b + normal * 0.0008f, normal, c + normal * 0.0008f, normal, color);

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Vector4 v) => Finite(new Vector3(v.X, v.Y, v.Z)) && float.IsFinite(v.W);
}
