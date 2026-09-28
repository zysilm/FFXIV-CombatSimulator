using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation;

/// <summary>Bounded, closed compound fitted to clipped mesh triangles, not a single resource AABB.</summary>
public sealed class WeaponMeshGeometry
{
    public readonly record struct Part(Vector3 Center, Vector3 Half, Vector3[] Points, float Volume);
    public Part[] Parts { get; private init; } = Array.Empty<Part>();
    public Vector3 Center { get; private init; }
    public Vector3 Half { get; private init; }

    public static WeaponMeshGeometry? Fit(IReadOnlyList<Triangle> triangles)
    {
        if (triangles.Count < 4 || triangles.Count > 120000) return null;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var t in triangles)
        {
            if (!float.IsFinite(t.A.LengthSquared() + t.B.LengthSquared() + t.C.LengthSquared())) return null;
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
        }
        var span = max - min;
        if (span.LengthSquared() is < .0001f or > 100f) return null;
        var axis = span.X >= span.Y && span.X >= span.Z ? 0 : span.Y >= span.Z ? 1 : 2;
        var side = axis == 0 ? (span.Y >= span.Z ? 1 : 2) : axis == 1 ? (span.X >= span.Z ? 0 : 2) : (span.X >= span.Y ? 0 : 1);
        var parts = new List<Part>(24);
        var pool = new BufferPool();
        try
        {
            // Clip whole triangles to cells: vertex-only binning leaves gaps in long, low-poly barrels.
            Span<Vector3> a = stackalloc Vector3[16]; Span<Vector3> b = stackalloc Vector3[16];
            for (var x = 0; x < 12; x++) for (var y = 0; y < 2; y++)
            {
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue); var found = false;
                var points = new HashSet<Vector3>();
                var x0 = min[axis] + span[axis] * x / 12; var x1 = min[axis] + span[axis] * (x + 1) / 12;
                var y0 = min[side] + span[side] * y / 2; var y1 = min[side] + span[side] * (y + 1) / 2;
                foreach (var t in triangles)
                {
                    if (MathF.Max(t.A[axis], MathF.Max(t.B[axis], t.C[axis])) < x0 || MathF.Min(t.A[axis], MathF.Min(t.B[axis], t.C[axis])) > x1) continue;
                    a[0] = t.A; a[1] = t.B; a[2] = t.C;
                    var n = Clip(a, 3, b, axis, x0, true); n = Clip(b, n, a, axis, x1, false);
                    n = Clip(a, n, b, side, y0, true); n = Clip(b, n, a, side, y1, false);
                    for (var i = 0; i < n; i++) { lo = Vector3.Min(lo, a[i]); hi = Vector3.Max(hi, a[i]); points.Add(a[i]); found = true; }
                }
                if (!found) continue;
                if (points.Count > 4096) return null;
                var vertices = new List<Vector3>(points);
                var size = hi - lo;
                var thinAxis = size.X <= size.Y && size.X <= size.Z ? 0 : size.Y <= size.Z ? 1 : 2;
                if (size[thinAxis] < .003f)
                {
                    var pad = Vector3.Zero; pad[thinAxis] = (.003f - size[thinAxis]) * .5f;
                    vertices.Clear(); foreach (var p in points) { vertices.Add(p - pad); vertices.Add(p + pad); }
                }
                var hull = new ConvexHull(vertices.ToArray(), pool, out var hullCenter);
                try
                {
                    var hullVolume = 0f;
                    for (var face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
                    {
                        hull.GetVertexIndicesForFace(face, out var indices);
                        if (indices.Length < 3) continue;
                        hull.GetPoint(indices[0], out var p0);
                        for (var k = 1; k + 1 < indices.Length; k++)
                        {
                            hull.GetPoint(indices[k], out var p1); hull.GetPoint(indices[k + 1], out var p2);
                            hullVolume += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6;
                        }
                    }
                    hullVolume = MathF.Abs(hullVolume);
                    if (!float.IsFinite(hullVolume) || hullVolume < 1e-12f) return null;
                    var local = new Vector3[vertices.Count]; var partHalf = Vector3.Zero;
                    for (var k = 0; k < local.Length; k++) { local[k] = vertices[k] - hullCenter; partHalf = Vector3.Max(partHalf, Vector3.Abs(local[k])); }
                    parts.Add(new(hullCenter, partHalf, local, hullVolume));
                }
                finally { hull.Dispose(pool); }
            }
            if (parts.Count == 0) return null;
            // Volume-weighted centre; every child and the rendered origin use this same offset.
            var center = Vector3.Zero; var volume = 0f;
            foreach (var p in parts) { center += p.Center * p.Volume; volume += p.Volume; }
            center /= volume;
            var half = Vector3.Zero;
            for (var i = 0; i < parts.Count; i++) { var p = parts[i]; parts[i] = p with { Center = p.Center - center }; half = Vector3.Max(half, Vector3.Abs(parts[i].Center) + p.Half); }
            return new WeaponMeshGeometry { Parts = parts.ToArray(), Center = center, Half = half };
        }
        catch (ArgumentException) { return null; }
        finally { pool.Clear(); }
    }

    private static int Clip(Span<Vector3> input, int count, Span<Vector3> output, int axis, float bound, bool above)
    {
        if (count == 0) return 0; var n = 0; var prev = input[count - 1]; var dp = above ? prev[axis] - bound : bound - prev[axis];
        for (var i = 0; i < count; i++)
        {
            var cur = input[i]; var dc = above ? cur[axis] - bound : bound - cur[axis];
            if ((dp >= 0) != (dc >= 0)) output[n++] = Vector3.Lerp(prev, cur, dp / (dp - dc));
            if (dc >= 0) output[n++] = cur;
            prev = cur; dp = dc;
        }
        return n;
    }

    public TypedIndex CreateShape(BepuPhysics.Simulation simulation, BufferPool pool, float mass, out BodyInertia inertia)
    {
        pool.Take<CompoundChild>(Parts.Length, out var children);
        var allocated = 0;
        try
        {
        var masses = new float[Parts.Length]; var total = 0f;
        for (var i = 0; i < Parts.Length; i++) total += Parts[i].Volume;
        for (var i = 0; i < Parts.Length; i++)
        {
            var p = Parts[i];
            var hull = new ConvexHull(p.Points, pool, out var shift);
            children[i] = new CompoundChild { ShapeIndex = simulation.Shapes.Add(hull), LocalPosition = p.Center + shift, LocalOrientation = Quaternion.Identity };
            allocated++;
            masses[i] = mass * p.Volume / total;
        }
        var compound = new Compound(children);
        inertia = compound.ComputeInertia(masses, simulation.Shapes);
        return simulation.Shapes.Add(compound);
        }
        catch
        {
            for (var i = 0; i < allocated; i++) simulation.Shapes.RemoveAndDispose(children[i].ShapeIndex, pool);
            pool.Return(ref children);
            throw;
        }
    }
}
