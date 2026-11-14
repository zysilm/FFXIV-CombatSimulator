// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation;

/// <summary>Turns a fitted hull (see <see cref="StructuralMeshFitter"/>) into a physics shape.</summary>
internal static class StructuralHullShape
{
    /// <summary>
    /// Builds the shape. The points are given relative to the bone, in the body's own frame; the
    /// physics engine centres a hull on its centre of mass, which is where the body must then sit.
    /// </summary>
    /// <param name="center">The shape's centre, relative to the bone, in the body's frame.</param>
    /// <param name="corners">The hull's corners relative to that centre.</param>
    /// <param name="edges">Its edges as consecutive pairs of points, relative to that centre.</param>
    public static bool TryCreate(Vector3[] points, float scale, BufferPool pool, out ConvexHull hull, out Vector3 center,
        out Vector3[] corners, out Vector3[] edges)
    {
        hull = default;
        center = default;
        corners = Array.Empty<Vector3>();
        edges = Array.Empty<Vector3>();
        if (points.Length < 4 || !(scale > 0f) || !float.IsFinite(scale))
            return false;

        var scaled = new Vector3[points.Length];
        for (var i = 0; i < scaled.Length; i++)
        {
            scaled[i] = points[i] * scale;
            if (!float.IsFinite(scaled[i].X) || !float.IsFinite(scaled[i].Y) || !float.IsFinite(scaled[i].Z))
                return false;
        }

        var shape = new ConvexHull(scaled, pool, out center);

        Vector3 Point(HullVertexIndex index)
        {
            ref var bundle = ref shape.Points[index.BundleIndex];
            return new Vector3(bundle.X[index.InnerIndex], bundle.Y[index.InnerIndex], bundle.Z[index.InnerIndex]);
        }

        var cornerList = new List<Vector3>();
        var edgeList = new List<Vector3>();
        var seenCorners = new HashSet<int>();
        var seenEdges = new HashSet<(int, int)>();
        for (var face = 0; face < shape.FaceToVertexIndicesStart.Length; face++)
        {
            var start = shape.FaceToVertexIndicesStart[face];
            var end = face + 1 < shape.FaceToVertexIndicesStart.Length
                ? shape.FaceToVertexIndicesStart[face + 1]
                : shape.FaceVertexIndices.Length;
            for (var i = start; i < end; i++)
            {
                var a = shape.FaceVertexIndices[i];
                var b = shape.FaceVertexIndices[i + 1 < end ? i + 1 : start];
                int ia = a.BundleIndex * Vector<float>.Count + a.InnerIndex, ib = b.BundleIndex * Vector<float>.Count + b.InnerIndex;
                if (seenCorners.Add(ia))
                    cornerList.Add(Point(a));
                if (ia == ib || !seenEdges.Add((Math.Min(ia, ib), Math.Max(ia, ib))))
                    continue;
                edgeList.Add(Point(a));
                edgeList.Add(Point(b));
            }
        }

        if (cornerList.Count < 4 || !float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
        {
            shape.Dispose(pool);
            return false;
        }
        hull = shape;
        corners = cornerList.ToArray();
        edges = edgeList.ToArray();
        return true;
    }
}
