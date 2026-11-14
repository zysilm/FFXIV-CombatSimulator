// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

internal static class SurfaceSmoothing
{
    /// <summary>
    /// Taubin (λ|μ) smoothing over mesh edges: removes millimetre facets and wrinkles without the
    /// shrinkage of plain Laplacian smoothing. Returns a new geometry; the source is untouched.
    /// For contact queries that should feel a body's shape, not its render detail.
    /// </summary>
    public static BodyMeshGeometry Taubin(BodyMeshGeometry source, int iterations)
    {
        var vertices = (Vector3[])source.Vertices.Clone();
        if (iterations <= 0) return new BodyMeshGeometry(vertices, (int[])source.Indices.Clone());
        // Unique undirected edges as a compact adjacency list (CSR).
        var edges = new List<long>(source.Indices.Length);
        void Edge(int a, int b) { if (a != b) edges.Add(a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a); }
        for (int i = 0; i < source.Indices.Length; i += 3)
        {
            int a = source.Indices[i], b = source.Indices[i + 1], c = source.Indices[i + 2];
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        edges.Sort();
        var degree = new int[vertices.Length + 1];
        long previous = -1;
        foreach (var e in edges)
        {
            if (e == previous) continue; previous = e;
            degree[(int)(e >> 32)]++; degree[(int)(uint)e]++;
        }
        var start = new int[vertices.Length + 1];
        for (int i = 0; i < vertices.Length; i++) start[i + 1] = start[i] + degree[i];
        var fill = (int[])start.Clone(); var neighbours = new int[start[vertices.Length]];
        previous = -1;
        foreach (var e in edges)
        {
            if (e == previous) continue; previous = e;
            int a = (int)(e >> 32), b = (int)(uint)e;
            neighbours[fill[a]++] = b; neighbours[fill[b]++] = a;
        }
        var next = new Vector3[vertices.Length];
        for (int pass = 0; pass < iterations * 2; pass++)
        {
            float factor = pass % 2 == 0 ? .5f : -.53f;
            for (int i = 0; i < vertices.Length; i++)
            {
                int count = start[i + 1] - start[i];
                if (count == 0) { next[i] = vertices[i]; continue; }
                var average = Vector3.Zero;
                for (int k = start[i]; k < start[i + 1]; k++) average += vertices[neighbours[k]];
                next[i] = vertices[i] + factor * (average / count - vertices[i]);
            }
            (vertices, next) = (next, vertices);
        }
        return new BodyMeshGeometry(vertices, (int[])source.Indices.Clone());
    }
}
