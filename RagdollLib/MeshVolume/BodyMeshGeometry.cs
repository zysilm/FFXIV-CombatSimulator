// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Rigid local triangle surface. Scale is baked before this representation is built.</summary>
public sealed class BodyMeshGeometry
{
    public Vector3[] Vertices { get; }
    public int[] Indices { get; }
    public MeshSourceBinding[]? SourceBindings { get; }
    public int Revision { get; private set; }
    private SurfaceMeshBvh? query;
    private int queryRevision;
    private int bottomRevision = -1;
    private Vector3 bottomPosition;
    private Quaternion bottomOrientation;
    private float cachedBottom;
    internal SurfaceMeshBvh Query
    {
        get
        {
            if (query == null) { query = new SurfaceMeshBvh(Vertices, Indices); queryRevision = Revision; }
            else if (queryRevision != Revision) { query.Refit(); queryRevision = Revision; }
            return query;
        }
    }
    /// <summary>Invalidate pose-following query caches after an accepted in-place skin refit.</summary>
    public void MarkChanged() => Revision = unchecked(Revision + 1);
    public BodyMeshGeometry(Vector3[] vertices, int[] indices, MeshSourceBinding[]? sourceBindings = null)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0) throw new ArgumentException("Triangle indices required.");
        foreach (var p in vertices)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new ArgumentException("Nonfinite mesh vertex.");
        foreach (var i in indices)
            if ((uint)i >= vertices.Length) throw new ArgumentException("Mesh index out of range.");
        Vertices = vertices;
        Indices = indices;
        if (sourceBindings != null && sourceBindings.Length != vertices.Length)
            throw new ArgumentException("One source binding per vertex required.");
        SourceBindings = sourceBindings;
    }

    public float Bottom(Vector3 position, Quaternion orientation)
    {
        if (bottomRevision == Revision && bottomPosition == position && bottomOrientation == orientation) return cachedBottom;
        var direction = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(orientation));
        cachedBottom = position.Y + Query.MinimumProjection(direction);
        bottomRevision = Revision; bottomPosition = position; bottomOrientation = orientation;
        return cachedBottom;
    }

    public void Bounds(Vector3 position, Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        Query.WorldBounds(position, orientation, 1f, out min, out max, false);
    }

    public static Vector3 BoneOrigin(Vector3 center, Quaternion bodyRotation, Vector3 centerOffset)
        => center - Vector3.Transform(centerOffset, bodyRotation);

    /// <summary>
    /// Exact vertical sphere support on a triangle, including edges. Unlike a foot disk or
    /// a handful of rays this cannot invent a flat envelope over a concave mesh.
    /// Winding is immaterial for this query; physics retains the original surface winding.
    /// </summary>
    public static bool TrySphereSupport(Vector3 center, float radius, Vector3 a, Vector3 b, Vector3 c,
        out float requiredCenterY, float minimumUpwardNormal = 0.28f)
    {
        requiredCenterY = float.MinValue;
        if (!(radius > 0f) || !float.IsFinite(radius)) return false;
        var normal = Vector3.Cross(b - a, c - a);
        var normalLength = normal.Length();
        if (normalLength < 1e-10f) return false;
        normal /= normalLength;
        if (normal.Y < 0f) normal = -normal;
        if (normal.Y < minimumUpwardNormal) return false;

        var contactXz = new Vector2(center.X - radius * normal.X, center.Z - radius * normal.Z);
        if (ContainsProjected(contactXz, a, b, c))
        {
            var plane = Vector3.Dot(normal, a);
            requiredCenterY = (plane - normal.X * center.X - normal.Z * center.Z + radius) / normal.Y;
        }
        var best = requiredCenterY;
        EdgeSupport(center, radius, a, b, minimumUpwardNormal, ref best);
        EdgeSupport(center, radius, b, c, minimumUpwardNormal, ref best);
        EdgeSupport(center, radius, c, a, minimumUpwardNormal, ref best);
        requiredCenterY = best;
        return best != float.MinValue;
    }

    private static bool ContainsProjected(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var u = new Vector2(b.X - a.X, b.Z - a.Z);
        var v = new Vector2(c.X - a.X, c.Z - a.Z);
        var d = p - new Vector2(a.X, a.Z);
        var determinant = u.X * v.Y - u.Y * v.X;
        if (MathF.Abs(determinant) < 1e-12f) return false;
        var s = (d.X * v.Y - d.Y * v.X) / determinant;
        var t = (u.X * d.Y - u.Y * d.X) / determinant;
        return s >= -1e-5f && t >= -1e-5f && s + t <= 1f + 1e-5f;
    }

    private static void EdgeSupport(Vector3 center, float radius, Vector3 a, Vector3 b,
        float minNormal, ref float best)
    {
        var edge = new Vector2(b.X - a.X, b.Z - a.Z);
        var relative = new Vector2(a.X - center.X, a.Z - center.Z);
        var lengthSq = edge.LengthSquared();
        float t;
        if (lengthSq < 1e-14f)
            t = b.Y > a.Y ? 1f : 0f;
        else
        {
            var t0 = -Vector2.Dot(relative, edge) / lengthSq;
            var distanceSq = (relative + edge * t0).LengthSquared();
            var available = radius * radius - distanceSq;
            if (available < 0f) return;
            var reach = MathF.Sqrt(available / lengthSq);
            var lo = MathF.Max(0f, t0 - reach);
            var hi = MathF.Min(1f, t0 + reach);
            if (lo > hi) return;
            var dy = b.Y - a.Y;
            t = Math.Clamp(t0 + dy * MathF.Sqrt(available) /
                MathF.Sqrt(lengthSq * (lengthSq + dy * dy)), lo, hi);
        }
        var horizontalSq = (relative + edge * t).LengthSquared();
        if (horizontalSq > radius * radius + 1e-8f) return;
        var rise = MathF.Sqrt(MathF.Max(0f, radius * radius - horizontalSq));
        if (rise < radius * minNormal) return;
        best = MathF.Max(best, a.Y + (b.Y - a.Y) * t + rise);
    }
}

public readonly record struct MeshBodyWeight(int Body, float Weight);
public readonly record struct MeshWeightedVertex(Vector3 Position, MeshBodyWeight[] Weights);
public readonly record struct MeshSourceBinding(int A, int B, int C, Vector3 Barycentric);

/// <summary>Partitions triangles along interpolated weight boundaries, preserving the source surface.
/// No convexification, interior caps, duplicate triangles, or percentile envelopes are introduced.</summary>
public static class BodyMeshPartitioner
{
    private sealed class Part
    {
        public readonly List<Vector3> Points = new();
        public readonly List<MeshSourceBinding> Bindings = new();
        public readonly List<int> Indices = new();
        private readonly Dictionary<MeshSourceBinding, int> vertices = new();

        public void Add(Vector3 point, MeshSourceBinding binding)
        {
            // Canonical source-vertex keys weld shared vertices without moving geometry or
            // changing skinning. Nontrivial clipped barycentric points remain exact.
            if (binding.Barycentric == Vector3.UnitX) binding = new(binding.A, binding.A, binding.A, Vector3.UnitX);
            else if (binding.Barycentric == Vector3.UnitY) binding = new(binding.B, binding.B, binding.B, Vector3.UnitX);
            else if (binding.Barycentric == Vector3.UnitZ) binding = new(binding.C, binding.C, binding.C, Vector3.UnitX);
            if (!vertices.TryGetValue(binding, out var index))
            {
                index = Points.Count;
                vertices.Add(binding, index);
                Points.Add(point);
                Bindings.Add(binding);
            }
            Indices.Add(index);
        }
    }

    public static Dictionary<int, BodyMeshGeometry> Partition(MeshWeightedVertex[] vertices, int[] indices,
        int maximumOutputTriangles = 200000)
    {
        if (indices.Length % 3 != 0) throw new ArgumentException("Triangle indices required.");
        foreach (var index in indices)
            if ((uint)index >= vertices.Length) throw new ArgumentException("Source index out of range.");
        var parts = new Dictionary<int, Part>();
        int outputTriangles = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            var va = vertices[indices[i]]; var vb = vertices[indices[i + 1]]; var vc = vertices[indices[i + 2]];
            if (va.Weights.Length == 0 || vb.Weights.Length == 0 || vc.Weights.Length == 0) continue;
            var weights = new SortedDictionary<int, Vector3>();
            AddWeights(weights, va.Weights, 0); AddWeights(weights, vb.Weights, 1); AddWeights(weights, vc.Weights, 2);
            if (weights.Count == 0) continue;
            foreach (var candidate in weights)
            {
                var polygon = new List<Vector3> { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
                foreach (var other in weights)
                {
                    if (other.Key == candidate.Key) continue;
                    var difference = candidate.Value - other.Value;
                    // Equal weight across the whole face must have exactly one deterministic owner.
                    if (difference.LengthSquared() < 1e-14f)
                    {
                        if (other.Key < candidate.Key) { polygon.Clear(); break; }
                        continue;
                    }
                    polygon = Clip(polygon, difference);
                    if (polygon.Count < 3) break;
                }
                if (polygon.Count < 3) continue;
                if (!parts.TryGetValue(candidate.Key, out var part)) parts[candidate.Key] = part = new();
                Vector3 World(Vector3 bary) => va.Position * bary.X + vb.Position * bary.Y + vc.Position * bary.Z;
                var a = World(polygon[0]);
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    var b = World(polygon[k]); var c = World(polygon[k + 1]);
                    if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-16f) continue;
                    if (++outputTriangles > maximumOutputTriangles)
                        throw new InvalidOperationException("Exact mesh partition exceeded its output-face budget.");
                    part.Add(a, new(indices[i], indices[i + 1], indices[i + 2], polygon[0]));
                    part.Add(b, new(indices[i], indices[i + 1], indices[i + 2], polygon[k]));
                    part.Add(c, new(indices[i], indices[i + 1], indices[i + 2], polygon[k + 1]));
                }
            }
        }
        var result = new Dictionary<int, BodyMeshGeometry>();
        foreach (var part in parts)
        {
            if (part.Value.Indices.Count < 3) continue;
            result[part.Key] = new BodyMeshGeometry(part.Value.Points.ToArray(), part.Value.Indices.ToArray(), part.Value.Bindings.ToArray());
        }
        return result;
    }

    private static void AddWeights(SortedDictionary<int, Vector3> weights, MeshBodyWeight[] influences, int corner)
    {
        foreach (var influence in influences)
        {
            if (influence.Weight <= 0f || !float.IsFinite(influence.Weight)) continue;
            weights.TryGetValue(influence.Body, out var value);
            if (corner == 0) value.X += influence.Weight;
            else if (corner == 1) value.Y += influence.Weight;
            else value.Z += influence.Weight;
            weights[influence.Body] = value;
        }
    }

    private static List<Vector3> Clip(List<Vector3> polygon, Vector3 difference)
    {
        var output = new List<Vector3>();
        if (polygon.Count == 0) return output;
        var previous = polygon[^1]; var prevDistance = Vector3.Dot(previous, difference);
        foreach (var current in polygon)
        {
            var distance = Vector3.Dot(current, difference);
            if ((distance >= 0f) != (prevDistance >= 0f))
                output.Add(Vector3.Lerp(previous, current, prevDistance / (prevDistance - distance)));
            if (distance >= 0f) output.Add(current);
            previous = current; prevDistance = distance;
        }
        return output;
    }
}
