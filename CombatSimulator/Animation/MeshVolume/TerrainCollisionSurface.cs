// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Closed ground below the original sampled top surface. Convex/mesh
/// dispatch queries the complete body BVH without body/body contact sampling.</summary>
public static class TerrainCollisionSurface
{
    public static void Add(BepuPhysics.Simulation simulation, BufferPool pool, ReadOnlySpan<Triangle> triangles)
    {
        if (triangles.Length == 0) return;
        const float depth = .75f;
        var first = triangles[0];
        var normal = Vector3.Normalize(Vector3.Cross(first.B - first.A, first.C - first.A));
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        float projectedArea = 0f;
        bool planar = MathF.Abs(normal.Y) > .01f;
        foreach (var triangle in triangles)
        {
            projectedArea += MathF.Abs(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).Y) * .5f;
            for (int corner = 0; corner < 3; corner++)
            {
                var point = corner == 0 ? triangle.A : corner == 1 ? triangle.B : triangle.C;
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                planar &= MathF.Abs(Vector3.Dot(point - first.A, normal)) < 1e-5f;
            }
        }
        planar &= MathF.Abs(projectedArea - (max.X - min.X) * (max.Z - min.Z)) < MathF.Max(1e-5f, projectedArea * 1e-5f);
        // Production patches are rectangular grids. Merge coplanar cells into
        // one slab to avoid internal seams and extra solver constraints.
        if (planar)
        {
            if (MathF.Abs(normal.X) < 1e-6f && MathF.Abs(normal.Z) < 1e-6f)
            {
                var box = new Box(max.X - min.X, depth, max.Z - min.Z);
                var center = (min + max) * .5f - Vector3.UnitY * (depth * .5f);
                simulation.Statics.Add(new StaticDescription(center, simulation.Shapes.Add(box)));
                return;
            }
            Span<Vector3> corners = stackalloc Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? min.X : max.X, z = (i & 2) == 0 ? min.Z : max.Z;
                float y = first.A.Y - (normal.X * (x - first.A.X) + normal.Z * (z - first.A.Z)) / normal.Y;
                corners[i] = new Vector3(x, y, z); corners[i + 4] = corners[i] - Vector3.UnitY * depth;
            }
            AddHull(simulation, pool, corners);
            return;
        }
        Span<Vector3> vertices = stackalloc Vector3[6];
        foreach (var triangle in triangles)
        {
            vertices[0] = triangle.A; vertices[1] = triangle.B; vertices[2] = triangle.C;
            for (int i = 0; i < 3; i++) vertices[i + 3] = vertices[i] - Vector3.UnitY * depth;
            AddHull(simulation, pool, vertices);
        }
    }

    private static void AddHull(BepuPhysics.Simulation simulation, BufferPool pool, Span<Vector3> vertices)
    {
        var hull = new ConvexHull(vertices, pool, out var center);
        simulation.Statics.Add(new StaticDescription(center, simulation.Shapes.Add(hull)));
    }
}
