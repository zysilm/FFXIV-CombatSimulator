// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private readonly record struct SeamVertexKey(long X, long Y, long Z, ulong Skin);
    private readonly record struct SeamEdge(int Face, int Edge, int First, int Second);
    public int VerifiedSeamPairs { get; private set; }
    public string WalkStatus { get; private set; } = "No walk";

    // Pure managed worker metadata. This connects only duplicate boundary edges with the
    // same material positions and skin influences; it never joins gaps or nearby cloth layers.
    private static int BuildVerifiedSeamAdjacency(Vertex[] points, Face[] triangles, CancellationToken cancellation)
    {
        var canonical = new int[points.Length];
        var verticesByKey = new Dictionary<SeamVertexKey, int>();
        for (int i = 0; i < points.Length; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var p = points[i].Position;
            var key = new SeamVertexKey((long)Math.Round(p.X * 100000d), (long)Math.Round(p.Y * 100000d),
                (long)Math.Round(p.Z * 100000d), SkinSignature(points[i]));
            if (!verticesByKey.TryGetValue(key, out canonical[i]))
            { canonical[i] = i; verticesByKey.Add(key, i); }
        }
        var edges = new Dictionary<(int, int), List<SeamEdge>>();
        for (int i = 0; i < triangles.Length; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            for (int e = 0; e < 3; e++)
            {
                EdgeVertices(triangles[i], e, out int a, out int b);
                int ca = canonical[a], cb = canonical[b];
                if (ca == cb) continue;
                var key = ca < cb ? (ca, cb) : (cb, ca);
                if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = new List<SeamEdge>(2));
                // Three entries suffice to reject a nonmanifold/coincident branch.
                if (list.Count < 3) list.Add(new(i, e, a, b));
            }
        }
        int pairs = 0;
        int visited = 0;
        foreach (var list in edges.Values)
        {
            if ((visited++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
            if (list.Count != 2) continue;
            var first = list[0]; var second = list[1];
            if (first.Face == second.Face || Neighbour(triangles[first.Face], first.Edge) >= 0 ||
                Neighbour(triangles[second.Face], second.Edge) >= 0) continue;
            if (canonical[first.First] != canonical[second.Second] || canonical[first.Second] != canonical[second.First]) continue;
            if (!SameSkin(points[first.First], points[second.Second]) || !SameSkin(points[first.Second], points[second.First]) ||
                Vector3.DistanceSquared(points[first.First].Position, points[second.Second].Position) > 4e-10f ||
                Vector3.DistanceSquared(points[first.Second].Position, points[second.First].Position) > 4e-10f) continue;
            var f = triangles[first.Face]; var s = triangles[second.Face];
            var fn = Vector3.Cross(points[f.B].Position - points[f.A].Position, points[f.C].Position - points[f.A].Position);
            var sn = Vector3.Cross(points[s.B].Position - points[s.A].Position, points[s.C].Position - points[s.A].Position);
            if (fn.LengthSquared() < 1e-14f || sn.LengthSquared() < 1e-14f || Vector3.Dot(Vector3.Normalize(fn), Vector3.Normalize(sn)) < 0.5f) continue;
            SetNeighbour(triangles, first.Face, first.Edge, second.Face);
            SetNeighbour(triangles, second.Face, second.Edge, first.Face); pairs++;
        }
        return pairs;
    }

    private static void EdgeVertices(Face face, int edge, out int first, out int second)
    { first = edge == 0 ? face.B : edge == 1 ? face.C : face.A; second = edge == 0 ? face.C : edge == 1 ? face.A : face.B; }

    private static int SortedSkin(Vertex v, Span<int> bones, Span<float> weights)
    {
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            float w = i switch { 0 => v.Weights.X, 1 => v.Weights.Y, 2 => v.Weights.Z, 3 => v.Weights.W,
                4 => v.ExtraWeights.X, 5 => v.ExtraWeights.Y, 6 => v.ExtraWeights.Z, _ => v.ExtraWeights.W };
            if (w <= 0) continue;
            int bone = i switch { 0 => v.B0, 1 => v.B1, 2 => v.B2, 3 => v.B3, 4 => v.B4, 5 => v.B5, 6 => v.B6, _ => v.B7 };
            int at = 0;
            while (at < count && bones[at] < bone) at++;
            if (at < count && bones[at] == bone) { weights[at] += w; continue; }
            for (int j = count; j > at; j--) { bones[j] = bones[j - 1]; weights[j] = weights[j - 1]; }
            bones[at] = bone; weights[at] = w; count++;
        }
        return count;
    }
    private static ulong SkinSignature(Vertex v)
    {
        Span<int> bones = stackalloc int[8]; Span<float> weights = stackalloc float[8];
        int count = SortedSkin(v, bones, weights);
        ulong hash = 14695981039346656037;
        for (int i = 0; i < count; i++)
        { hash = unchecked((hash ^ (uint)bones[i]) * 1099511628211); hash = unchecked((hash ^ (uint)BitConverter.SingleToInt32Bits(weights[i])) * 1099511628211); }
        return hash;
    }
    private static bool SameSkin(Vertex a, Vertex b)
    {
        Span<int> ab = stackalloc int[8]; Span<int> bb = stackalloc int[8];
        Span<float> aw = stackalloc float[8]; Span<float> bw = stackalloc float[8];
        int count = SortedSkin(a, ab, aw);
        return count == SortedSkin(b, bb, bw) && ab[..count].SequenceEqual(bb[..count]) && aw[..count].SequenceEqual(bw[..count]);
    }

    private bool TryResolvedNeighbour(int triangle, int edge, out int next)
    {
        next = Neighbour(faces[triangle], edge);
        if (next < 0) return false;
        EdgeVertices(faces[triangle], edge, out int a, out int b);
        int otherEdge = -1;
        for (int e = 0; e < 3; e++) if (Neighbour(faces[next], e) == triangle) { otherEdge = e; break; }
        if (otherEdge < 0) { next = -1; return false; }
        EdgeVertices(faces[next], otherEdge, out int c, out int d);
        if (a == d && b == c) return true;
        if (!Triangle(triangle, out var ta, out var tb, out var tc) || !Triangle(next, out var na, out var nb, out var nc))
            return ContactPending("Seam pose/skin budget unavailable");
        float tolerance = Math.Max(0.00002f, 3 * Math.Max(PositionUlp(currentVertices[a]), PositionUlp(currentVertices[b])));
        if (Vector3.DistanceSquared(currentVertices[a], currentVertices[d]) > tolerance * tolerance ||
            Vector3.DistanceSquared(currentVertices[b], currentVertices[c]) > tolerance * tolerance ||
            Vector3.Dot(SafeNormal(Vector3.Cross(tb - ta, tc - ta)), SafeNormal(Vector3.Cross(nb - na, nc - na))) < 0.5f)
        { next = -1; return false; }
        return true;
    }

    private static float PositionUlp(Vector3 p)
    {
        float magnitude = Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));
        return MathF.BitIncrement(magnitude) - magnitude;
    }

    /// <summary>Stable material coordinates in managed deformed rest space, in metres. No skin/native query.</summary>
    public bool TryGetMaterialCoordinate(FluidSurfaceAnchor anchor, out Vector2 coordinate)
    {
        coordinate = default;
        if (anchor.Generation != Generation || anchor.Triangle < 0 || anchor.Triangle >= faces.Length ||
            !Finite(anchor.Barycentric) || anchor.Barycentric.X < -0.002f || anchor.Barycentric.Y < -0.002f ||
            anchor.Barycentric.Z < -0.002f || Math.Abs(anchor.Barycentric.X + anchor.Barycentric.Y + anchor.Barycentric.Z - 1) > 0.005f) return false;
        var f = faces[anchor.Triangle]; var a = vertices[f.A].Position; var b = vertices[f.B].Position; var c = vertices[f.C].Position;
        var normal = Vector3.Abs(Vector3.Cross(b - a, c - a));
        if (!Finite(normal) || normal.LengthSquared() < 1e-14f) return false;
        var p = a * anchor.Barycentric.X + b * anchor.Barycentric.Y + c * anchor.Barycentric.Z;
        coordinate = normal.X >= normal.Y && normal.X >= normal.Z ? new(p.Y, p.Z) : normal.Y >= normal.Z ? new(p.X, p.Z) : new(p.X, p.Y);
        return float.IsFinite(coordinate.X) && float.IsFinite(coordinate.Y);
    }
}
