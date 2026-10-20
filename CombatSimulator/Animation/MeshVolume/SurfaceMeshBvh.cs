// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Exact local-space triangle queries. Topology is built once; skin changes refit
/// bounds without rebuilding, and rigid motion only transforms query points/node bounds.</summary>
internal sealed class SurfaceMeshBvh
{
    private struct Node { public Vector3 Min, Max; public int Left, Right, Start, Count; }
    private readonly Vector3[] vertices;
    private readonly int[] indices;
    private readonly int[] faces;
    private readonly Node[] nodes;
    internal int LastTriangleTests { get; private set; }

    internal SurfaceMeshBvh(Vector3[] vertices, int[] indices)
    {
        this.vertices = vertices; this.indices = indices;
        faces = new int[indices.Length / 3];
        var centroids = new Vector3[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        { faces[i] = i; centroids[i] = (vertices[indices[i * 3]] + vertices[indices[i * 3 + 1]] + vertices[indices[i * 3 + 2]]) / 3f; }
        var build = new List<Node>(Math.Max(1, faces.Length / 3));
        int Build(int start, int count, int depth)
        {
            int index = build.Count; build.Add(default);
            if (count <= 8 || depth == 32) { build[index] = new() { Start = start, Count = count, Left = -1, Right = -1 }; return index; }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = start; i < start + count; i++) { min = Vector3.Min(min, centroids[faces[i]]); max = Vector3.Max(max, centroids[faces[i]]); }
            var extent = max - min;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            float cut = (min[axis] + max[axis]) * 0.5f;
            int split = start;
            for (int i = start; i < start + count; i++)
                if (centroids[faces[i]][axis] < cut) { (faces[split], faces[i]) = (faces[i], faces[split]); split++; }
            if (split == start || split == start + count) split = start + count / 2;
            int left = Build(start, split - start, depth + 1), right = Build(split, start + count - split, depth + 1);
            build[index] = new() { Left = left, Right = right }; return index;
        }
        Build(0, faces.Length, 0); nodes = build.ToArray(); Refit();
    }

    internal void Refit()
    {
        for (int ni = nodes.Length - 1; ni >= 0; ni--)
        {
            ref var node = ref nodes[ni];
            if (node.Left >= 0)
            { node.Min = Vector3.Min(nodes[node.Left].Min, nodes[node.Right].Min); node.Max = Vector3.Max(nodes[node.Left].Max, nodes[node.Right].Max); continue; }
            node.Min = new(float.MaxValue); node.Max = new(float.MinValue);
            for (int i = node.Start; i < node.Start + node.Count; i++)
                for (int corner = 0; corner < 3; corner++)
                { var p = vertices[indices[faces[i] * 3 + corner]]; node.Min = Vector3.Min(node.Min, p); node.Max = Vector3.Max(node.Max, p); }
        }
    }

    internal void WorldBounds(Vector3 position, Quaternion orientation, float radialScale, out Vector3 min, out Vector3 max, bool pad = true)
        => WorldBounds(nodes[0], position, Matrix4x4.CreateFromQuaternion(orientation), radialScale, out min, out max, pad);

    private static void WorldBounds(in Node node, Vector3 position, in Matrix4x4 rotation, float scale, out Vector3 min, out Vector3 max, bool pad = true)
    {
        var radial = new Vector3(scale, 1f, scale);
        var center = Vector3.Transform((node.Min + node.Max) * 0.5f * radial, rotation) + position;
        var localExtent = (node.Max - node.Min) * 0.5f * radial;
        var extent = new Vector3(MathF.Abs(rotation.M11) * localExtent.X + MathF.Abs(rotation.M21) * localExtent.Y + MathF.Abs(rotation.M31) * localExtent.Z,
            MathF.Abs(rotation.M12) * localExtent.X + MathF.Abs(rotation.M22) * localExtent.Y + MathF.Abs(rotation.M32) * localExtent.Z,
            MathF.Abs(rotation.M13) * localExtent.X + MathF.Abs(rotation.M23) * localExtent.Y + MathF.Abs(rotation.M33) * localExtent.Z);
        // Float transform roundoff must not cull a triangle on a node boundary.
        float padding = 1e-6f + MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) * 4e-5f;
        if (pad) extent += new Vector3(padding); min = center - extent; max = center + extent;
    }

    internal float MinimumProjection(Vector3 direction)
    {
        float best = float.MaxValue; LastTriangleTests = 0;
        float Bound(int index)
        {
            ref var node = ref nodes[index];
            return direction.X * (direction.X >= 0 ? node.Min.X : node.Max.X) +
                direction.Y * (direction.Y >= 0 ? node.Min.Y : node.Max.Y) + direction.Z * (direction.Z >= 0 ? node.Min.Z : node.Max.Z);
        }
        Span<int> stack = stackalloc int[66]; int size = 0; stack[size++] = 0;
        while (size > 0)
        {
            int index = stack[--size];
            if (Bound(index) > best + 1e-6f) continue;
            ref var node = ref nodes[index];
            if (node.Left >= 0)
            {
                int near = node.Left, far = node.Right;
                if (Bound(near) > Bound(far)) (near, far) = (far, near);
                stack[size++] = far; stack[size++] = near; continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int face = faces[i] * 3; LastTriangleTests++;
                best = MathF.Min(best, Vector3.Dot(direction, vertices[indices[face]]));
                best = MathF.Min(best, Vector3.Dot(direction, vertices[indices[face + 1]]));
                best = MathF.Min(best, Vector3.Dot(direction, vertices[indices[face + 2]]));
            }
        }
        return best;
    }

    internal bool Nearest(Vector3 point, float radialScale, float minimumNormalSquared,
        out Vector3 closest, out Vector3 normal, out float distanceSquared, bool faceTowardPoint = true)
    {
        closest = default; normal = Vector3.UnitY; distanceSquared = float.MaxValue; LastTriangleTests = 0;
        var radial = new Vector3(radialScale, 1f, radialScale);
        float Bound(int index) => Vector3.DistanceSquared(point, Vector3.Clamp(point, nodes[index].Min * radial, nodes[index].Max * radial));
        Span<int> stack = stackalloc int[66]; int size = 0; stack[size++] = 0;
        while (size > 0)
        {
            int ni = stack[--size];
            if (Bound(ni) > distanceSquared) continue;
            ref var node = ref nodes[ni];
            if (node.Left >= 0)
            {
                int near = node.Left, far = node.Right;
                if (Bound(near) > Bound(far)) (near, far) = (far, near);
                stack[size++] = far; stack[size++] = near; continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int face = faces[i] * 3;
                var a = vertices[indices[face]] * radial; var b = vertices[indices[face + 1]] * radial; var c = vertices[indices[face + 2]] * radial;
                LastTriangleTests++;
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < minimumNormalSquared) continue;
                var candidate = ClosestPoint(point, a, b, c);
                float distance = Vector3.DistanceSquared(point, candidate);
                if (distance >= distanceSquared) continue;
                closest = candidate; normal = Vector3.Normalize(n); distanceSquared = distance;
            }
        }
        if (distanceSquared == float.MaxValue) return false;
        if (faceTowardPoint && Vector3.Dot(normal, point - closest) < 0f) normal = -normal;
        return true;
    }

    internal bool SphereSupport(Vector3 center, float radius, Vector3 position, Quaternion orientation,
        float radialScale, out float requiredCenterY)
    {
        requiredCenterY = float.MinValue; LastTriangleTests = 0;
        var rotation = Matrix4x4.CreateFromQuaternion(orientation); var radial = new Vector3(radialScale, 1f, radialScale);
        Span<int> stack = stackalloc int[66]; int size = 0; stack[size++] = 0;
        while (size > 0)
        {
            ref var node = ref nodes[stack[--size]];
            WorldBounds(node, position, rotation, radialScale, out var min, out var max);
            if (center.X < min.X - radius || center.X > max.X + radius || center.Z < min.Z - radius || center.Z > max.Z + radius || max.Y + radius < requiredCenterY) continue;
            if (node.Left >= 0) { stack[size++] = node.Left; stack[size++] = node.Right; continue; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int face = faces[i] * 3;
                var a = Vector3.Transform(vertices[indices[face]] * radial, rotation) + position;
                var b = Vector3.Transform(vertices[indices[face + 1]] * radial, rotation) + position;
                var c = Vector3.Transform(vertices[indices[face + 2]] * radial, rotation) + position;
                LastTriangleTests++;
                if (BodyMeshGeometry.TrySphereSupport(center, radius, a, b, c, out var candidate)) requiredCenterY = MathF.Max(requiredCenterY, candidate);
            }
        }
        return requiredCenterY != float.MinValue;
    }

    internal static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        float inverse = 1f / (va + vb + vc); return a + ab * (vb * inverse) + ac * (vc * inverse);
    }
}
