// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

public readonly record struct MeshLodValidationSample(int A, int B, int C, Vector3 Barycentric, int CollisionTriangle);

public sealed class MeshCollisionLodResult
{
    public required BodyMeshGeometry Geometry { get; init; }
    public required int[] SourceVertexIndices { get; init; }
    public required MeshLodValidationSample[] ValidationSamples { get; init; }
    public int SourceFaceCount { get; init; }
    public int CollisionFaceCount => Geometry.Indices.Length / 3;
    public bool Reduced => CollisionFaceCount < SourceFaceCount;
    /// <summary>Endpoint movement can be large in an exactly planar patch; this is NOT surface error.</summary>
    public float MaximumOriginalVertexDisplacement { get; init; }
    /// <summary>Measured against a finite set of source samples. Not a Hausdorff bound.</summary>
    public float MaximumSampledSurfaceError { get; init; }
    public float RequestedTolerance { get; init; }
    public int AcceptedCollapses { get; init; }
    public int CandidateChecks { get; init; }
    public double BuildMilliseconds { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// Optional collision-only LOD. Endpoint collapses preserve the source vertex identity and
/// live skin binding; the full source/query geometry is never modified. Open boundaries,
/// nonmanifold vertices and creases are locked. Link checks and positive face areas protect
/// topology/winding. Each accepted local patch is checked against all of its represented
/// source vertices, source face centroids and edge midpoints, within the initial-pose target.
/// These finite samples do not prove a Hausdorff bound or a bound after later deformation.
/// </summary>
public static class MeshCollisionLod
{
    private struct Face { public int A, B, C; public bool Active; }
    private struct Edge { public int First, Second, Count; }
    private readonly record struct Candidate(int Keep, int Remove, int KeepVersion, int RemoveVersion);
    private readonly record struct SourceSample(int A, int B, int C, Vector3 Barycentric)
    { public Vector3 Position(Vector3[] source) => source[A] * Barycentric.X + source[B] * Barycentric.Y + source[C] * Barycentric.Z; }

    public static MeshCollisionLodResult Build(BodyMeshGeometry source, float tolerance = .003f,
        float targetFaceRatio = .35f, int maximumCandidateChecks = 100000, double maximumBuildMilliseconds = 40,
        int maximumValidationSamples = 128)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!float.IsFinite(targetFaceRatio) || targetFaceRatio <= 0 || targetFaceRatio > 1) throw new ArgumentOutOfRangeException(nameof(targetFaceRatio));
        var clock = Stopwatch.StartNew();
        int faceCount = source.Indices.Length / 3, vertexCount = source.Vertices.Length;
        MeshCollisionLodResult Original(string reason) => new() { Geometry = source, SourceVertexIndices = Identity(vertexCount),
            ValidationSamples = Array.Empty<MeshLodValidationSample>(), SourceFaceCount = faceCount, RequestedTolerance = tolerance,
            BuildMilliseconds = clock.Elapsed.TotalMilliseconds, Status = reason };
        if (faceCount < 16 || targetFaceRatio == 1 || maximumCandidateChecks <= 0) return Original("Full source retained: below LOD threshold or disabled.");
        var faces = new Face[faceCount]; var normal = new Vector3[faceCount];
        var incident = new HashSet<int>[vertexCount];
        var locked = new bool[vertexCount]; var removed = new bool[vertexCount]; var versions = new int[vertexCount];
        var parent = Identity(vertexCount);
        var edges = new Dictionary<(int, int), Edge>(source.Indices.Length / 2);
        var sampleHead = new int[vertexCount]; var sampleTail = new int[vertexCount];
        var samples = new SourceSample[vertexCount + faceCount * 4]; var nextSample = new int[samples.Length];
        Array.Fill(nextSample, -1);
        for (int i = 0; i < vertexCount; i++) { sampleHead[i] = sampleTail[i] = i; samples[i] = new(i, i, i, Vector3.UnitX); }
        int sampleCursor = vertexCount;
        void AddSample(int owner, SourceSample sample)
        { samples[sampleCursor] = sample; nextSample[sampleTail[owner]] = sampleCursor; sampleTail[owner] = sampleCursor++; }
        void AddEdge(int a, int b, int face)
        {
            if (a > b) (a, b) = (b, a);
            var key = (a, b);
            edges.TryGetValue(key, out var edge);
            if (edge.Count == 0) edge.First = face; else if (edge.Count == 1) edge.Second = face;
            edge.Count++; edges[key] = edge;
        }
        for (int i = 0; i < faceCount; i++)
        {
            int a = source.Indices[i * 3], b = source.Indices[i * 3 + 1], c = source.Indices[i * 3 + 2];
            var n = Vector3.Cross(source.Vertices[b] - source.Vertices[a], source.Vertices[c] - source.Vertices[a]);
            if (a == b || a == c || b == c || n.LengthSquared() < 1e-16f) return Original("Full source retained: degenerate input faces.");
            faces[i] = new() { A = a, B = b, C = c, Active = true }; normal[i] = Vector3.Normalize(n);
            (incident[a] ??= new()).Add(i); (incident[b] ??= new()).Add(i); (incident[c] ??= new()).Add(i);
            AddEdge(a, b, i); AddEdge(b, c, i); AddEdge(c, a, i);
            AddSample(a, new(a, b, c, new(1f / 3)));
            AddSample(a, new(a, b, c, new(.5f, .5f, 0)));
            AddSample(a, new(a, b, c, new(0, .5f, .5f)));
            AddSample(a, new(a, b, c, new(.5f, 0, .5f)));
        }
        foreach (var pair in edges)
        {
            var edge = pair.Value;
            bool invalid = edge.Count != 2 || Vector3.Dot(normal[edge.First], normal[edge.Second]) < .8f;
            if (!invalid)
            {
                bool Forward(Face face, int a, int b) => (face.A == a && face.B == b) || (face.B == a && face.C == b) || (face.C == a && face.A == b);
                invalid = Forward(faces[edge.First], pair.Key.Item1, pair.Key.Item2) == Forward(faces[edge.Second], pair.Key.Item1, pair.Key.Item2);
            }
            if (invalid) locked[pair.Key.Item1] = locked[pair.Key.Item2] = true;
        }
        // Edge multiplicities alone miss a bow-tie vertex made of disconnected fans.
        // Lock those vertices too instead of treating their links as manifold disks.
        var fanVisited = new HashSet<int>(); var fanQueue = new List<int>();
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            var ring = incident[vertex];
            if (locked[vertex] || ring == null || ring.Count < 2) continue;
            if (ring.Count > 128) { locked[vertex] = true; continue; }
            fanVisited.Clear(); fanQueue.Clear();
            foreach (int fi in ring) { fanVisited.Add(fi); fanQueue.Add(fi); break; }
            for (int cursor = 0; cursor < fanQueue.Count; cursor++)
            {
                var f = faces[fanQueue[cursor]];
                foreach (int fi in ring)
                {
                    if (fanVisited.Contains(fi)) continue;
                    var other = faces[fi];
                    bool shares = (f.A != vertex && (f.A == other.A || f.A == other.B || f.A == other.C)) ||
                        (f.B != vertex && (f.B == other.A || f.B == other.B || f.B == other.C)) ||
                        (f.C != vertex && (f.C == other.A || f.C == other.B || f.C == other.C));
                    if (shares) { fanVisited.Add(fi); fanQueue.Add(fi); }
                }
            }
            if (fanVisited.Count != ring.Count) locked[vertex] = true;
        }
        var queue = new PriorityQueue<Candidate, float>(edges.Count);
        void Enqueue(int a, int b)
        {
            if (a == b || removed[a] || removed[b] || locked[a] || locked[b]) return;
            if (a > b) (a, b) = (b, a);
            queue.Enqueue(new(a, b, versions[a], versions[b]), Vector3.DistanceSquared(source.Vertices[a], source.Vertices[b]));
        }
        foreach (var edge in edges.Keys) Enqueue(edge.Item1, edge.Item2);
        // Reused scratch prevents per-candidate garbage.
        var affected = new HashSet<int>(); var patchVertices = new HashSet<int>();
        var neighborsA = new HashSet<int>(); var neighborsB = new HashSet<int>();
        var replacement = new List<(int Face, int A, int B, int C, Vector3 Normal)>();
        var validationPatch = new List<(int A, int B, int C)>();
        var validationFaces = new HashSet<int>();
        var duplicateCheck = new HashSet<(int, int, int)>();
        int activeFaces = faceCount, checks = 0, collapses = 0;
        int dequeued = 0;
        double workStart = clock.Elapsed.TotalMilliseconds;
        int target = Math.Max(4, (int)MathF.Ceiling(faceCount * targetFaceRatio));
        float toleranceSquared = tolerance * tolerance + 1e-12f;
        bool TryCollapse(int keep, int remove)
        {
            affected.Clear(); neighborsA.Clear(); neighborsB.Clear(); patchVertices.Clear(); replacement.Clear(); duplicateCheck.Clear(); validationPatch.Clear(); validationFaces.Clear();
            foreach (int fi in incident[keep]) affected.Add(fi);
            foreach (int fi in incident[remove]) affected.Add(fi);
            int shared = 0;
            foreach (int fi in incident[keep])
            {
                var f = faces[fi]; neighborsA.Add(f.A); neighborsA.Add(f.B); neighborsA.Add(f.C);
                if (f.A == remove || f.B == remove || f.C == remove) shared++;
            }
            foreach (int fi in incident[remove]) { var f = faces[fi]; neighborsB.Add(f.A); neighborsB.Add(f.B); neighborsB.Add(f.C); }
            neighborsA.Remove(keep); neighborsA.Remove(remove); neighborsB.Remove(keep); neighborsB.Remove(remove);
            int common = 0; foreach (int n in neighborsA) if (neighborsB.Contains(n)) common++;
            if (shared != 2 || common != 2) return false; // Interior manifold link condition.
            if (neighborsA.Count + neighborsB.Count - common > 24) return false; // Bound vertex fan growth.
            Vector3 planeNormal = default, planeOrigin = default;
            bool coplanar = true, firstPlane = true;
            foreach (int fi in affected)
            {
                var f = faces[fi]; patchVertices.Add(f.A); patchVertices.Add(f.B); patchVertices.Add(f.C);
                if (firstPlane) { planeNormal = normal[fi]; planeOrigin = source.Vertices[f.A]; firstPlane = false; }
                else if (Vector3.Dot(planeNormal, normal[fi]) < .9999999f) coplanar = false;
                if (MathF.Abs(Vector3.Dot(source.Vertices[f.A] - planeOrigin, planeNormal)) > 1e-7f ||
                    MathF.Abs(Vector3.Dot(source.Vertices[f.B] - planeOrigin, planeNormal)) > 1e-7f ||
                    MathF.Abs(Vector3.Dot(source.Vertices[f.C] - planeOrigin, planeNormal)) > 1e-7f) coplanar = false;
                int a = f.A == remove ? keep : f.A, b = f.B == remove ? keep : f.B, c = f.C == remove ? keep : f.C;
                if (a == b || b == c || a == c) continue;
                var n = Vector3.Cross(source.Vertices[b] - source.Vertices[a], source.Vertices[c] - source.Vertices[a]);
                if (n.LengthSquared() < 1e-16f) return false;
                n = Vector3.Normalize(n);
                if (Vector3.Dot(n, normal[fi]) < .5f) return false;
                int sa = Math.Min(a, Math.Min(b, c)), sc = Math.Max(a, Math.Max(b, c)), sb = a + b + c - sa - sc;
                if (!duplicateCheck.Add((sa, sb, sc))) return false;
                replacement.Add((fi, a, b, c, n));
            }
            if (replacement.Count == 0) return false;
            // A planar manifold star with unchanged boundary and no flipped faces keeps
            // the same surface union. Tangential endpoint travel is not surface error.
            // Existing approximation samples remain valid because this union is unchanged.
            if (!coplanar)
            {
            foreach (var f in replacement) validationPatch.Add((f.A, f.B, f.C));
            // Samples owned by a neighboring vertex can lie in its other, unchanged faces.
            // Include that surrounding ring for validation without modifying its topology.
            foreach (int vertex in patchVertices)
                foreach (int fi in incident[vertex])
                    if (!affected.Contains(fi) && validationFaces.Add(fi))
                    { var f = faces[fi]; validationPatch.Add((f.A, f.B, f.C)); }
            foreach (int vertex in patchVertices)
                for (int si = sampleHead[vertex]; si >= 0; si = nextSample[si])
                {
                    var p = samples[si].Position(source.Vertices); float distance = float.MaxValue;
                    foreach (var f in validationPatch)
                    {
                        var closest = SurfaceMeshBvh.ClosestPoint(p, source.Vertices[f.A], source.Vertices[f.B], source.Vertices[f.C]);
                        distance = MathF.Min(distance, Vector3.DistanceSquared(p, closest));
                        if (distance <= toleranceSquared) break;
                    }
                    if (distance > toleranceSquared) return false;
                }
            }
            foreach (int fi in affected)
            {
                var old = faces[fi]; incident[old.A].Remove(fi); incident[old.B].Remove(fi); incident[old.C].Remove(fi);
                faces[fi].Active = false; activeFaces--;
            }
            foreach (var f in replacement)
            {
                faces[f.Face] = new() { A = f.A, B = f.B, C = f.C, Active = true }; normal[f.Face] = f.Normal;
                incident[f.A].Add(f.Face); incident[f.B].Add(f.Face); incident[f.C].Add(f.Face); activeFaces++;
            }
            nextSample[sampleTail[keep]] = sampleHead[remove]; sampleTail[keep] = sampleTail[remove]; sampleHead[remove] = -1;
            removed[remove] = true; parent[remove] = keep; versions[keep]++; versions[remove]++;
            foreach (int fi in incident[keep])
            { var f = faces[fi]; Enqueue(f.A, f.B); Enqueue(f.B, f.C); Enqueue(f.C, f.A); }
            return true;
        }
        bool timedOut = false;
        while (activeFaces > target && checks < maximumCandidateChecks && queue.TryDequeue(out var candidate, out _))
        {
            if ((dequeued++ & 31) == 0 && maximumBuildMilliseconds > 0 && clock.Elapsed.TotalMilliseconds - workStart >= maximumBuildMilliseconds)
            { timedOut = true; break; }
            if (removed[candidate.Keep] || removed[candidate.Remove] || versions[candidate.Keep] != candidate.KeepVersion || versions[candidate.Remove] != candidate.RemoveVersion) continue;
            checks++;
            // Either endpoint can remain. Never introduce a new position or blended binding.
            if (TryCollapse(candidate.Keep, candidate.Remove) || TryCollapse(candidate.Remove, candidate.Keep)) collapses++;
        }
        if (collapses == 0) return Original(timedOut ? "Full source retained: time budget reached before a safe collapse." : $"Full source retained: no safe topology/surface-preserving collapse ({checks} candidates, {Array.FindAll(locked, x => x).Length}/{vertexCount} locked vertices).");
        var retained = new List<int>(); var outputIndices = new int[activeFaces * 3]; var remap = new int[vertexCount]; Array.Fill(remap, -1);
        int used = 0;
        int Map(int original)
        { if (remap[original] < 0) { remap[original] = retained.Count; retained.Add(original); } return remap[original]; }
        foreach (var f in faces) if (f.Active) { outputIndices[used++] = Map(f.A); outputIndices[used++] = Map(f.B); outputIndices[used++] = Map(f.C); }
        var outputVertices = new Vector3[retained.Count]; var outputBindings = source.SourceBindings == null ? null : new MeshSourceBinding[retained.Count];
        for (int i = 0; i < retained.Count; i++) { outputVertices[i] = source.Vertices[retained[i]]; if (outputBindings != null) outputBindings[i] = source.SourceBindings![retained[i]]; }
        var output = new BodyMeshGeometry(outputVertices, outputIndices, outputBindings);
        float displacement = 0;
        for (int i = 0; i < vertexCount; i++)
        { int survivor = i; while (parent[survivor] != survivor) survivor = parent[survivor]; displacement = MathF.Max(displacement, Vector3.Distance(source.Vertices[i], source.Vertices[survivor])); }
        int validationCount = Math.Min(Math.Max(0, maximumValidationSamples), samples.Length);
        int vertexSamples = Math.Min(vertexCount, (validationCount * 3 + 3) / 4);
        var validation = new MeshLodValidationSample[validationCount]; float surfaceError = 0;
        for (int i = 0; i < validationCount; i++)
        {
            int sampleIndex = i < vertexSamples ? (int)((long)i * vertexCount / vertexSamples) :
                vertexCount + (int)((long)(i - vertexSamples) * faceCount / (validationCount - vertexSamples)) * 4;
            var sample = samples[sampleIndex]; var p = sample.Position(source.Vertices);
            float best = float.MaxValue; int closestFace = 0;
            for (int f = 0; f < outputIndices.Length; f += 3)
            {
                var closest = SurfaceMeshBvh.ClosestPoint(p, outputVertices[outputIndices[f]], outputVertices[outputIndices[f + 1]], outputVertices[outputIndices[f + 2]]);
                float distance = Vector3.DistanceSquared(p, closest);
                if (distance < best) { best = distance; closestFace = f / 3; }
            }
            surfaceError = MathF.Max(surfaceError, MathF.Sqrt(best)); validation[i] = new(sample.A, sample.B, sample.C, sample.Barycentric, closestFace);
        }
        if (surfaceError > tolerance + 1e-6f) return Original("Full source retained: final sampled surface error exceeded the initial-pose target.");
        return new() { Geometry = output, SourceVertexIndices = retained.ToArray(), ValidationSamples = validation, SourceFaceCount = faceCount,
            MaximumOriginalVertexDisplacement = displacement, MaximumSampledSurfaceError = surfaceError, RequestedTolerance = tolerance,
            AcceptedCollapses = collapses, CandidateChecks = checks, BuildMilliseconds = clock.Elapsed.TotalMilliseconds,
            Status = $"Collision-only endpoint LOD; {(activeFaces <= target ? "target reached" : timedOut ? "time budget reached" : "safe work/reduction limit reached")}; initial-pose finite-sample target, not a Hausdorff or future-deformation bound." };
    }

    /// <summary>Cheap conservative check of retained initial-pose validation patches after
    /// deformation. The selected triangle can cease to be the nearest, so rejection may be
    /// conservative. Passing these finite samples cannot certify the whole animated surface.</summary>
    public static bool ValidateCurrent(BodyMeshGeometry full, MeshCollisionLodResult lod, float tolerance, out float maximumError)
    {
        maximumError = 0;
        var coarse = lod.Geometry;
        foreach (var sample in lod.ValidationSamples)
        {
            if ((uint)sample.A >= full.Vertices.Length || (uint)sample.B >= full.Vertices.Length || (uint)sample.C >= full.Vertices.Length ||
                (uint)sample.CollisionTriangle >= coarse.Indices.Length / 3) return false;
            var p = full.Vertices[sample.A] * sample.Barycentric.X + full.Vertices[sample.B] * sample.Barycentric.Y + full.Vertices[sample.C] * sample.Barycentric.Z;
            int f = sample.CollisionTriangle * 3;
            var closest = SurfaceMeshBvh.ClosestPoint(p, coarse.Vertices[coarse.Indices[f]], coarse.Vertices[coarse.Indices[f + 1]], coarse.Vertices[coarse.Indices[f + 2]]);
            float error = Vector3.Distance(p, closest);
            if (!float.IsFinite(error)) return false;
            maximumError = MathF.Max(maximumError, error);
        }
        return maximumError <= tolerance;
    }

    /// <summary>Current finite-sample error against the nearest current LOD triangle.
    /// Mark the LOD geometry changed after updating its retained vertices, before calling.
    /// This avoids rejecting a valid deformed surface merely because its nearest face changed.</summary>
    public static bool ValidateCurrentNearest(BodyMeshGeometry full, MeshCollisionLodResult lod, float tolerance, out float maximumError)
    {
        maximumError = 0;
        var query = lod.Geometry.Query;
        foreach (var sample in lod.ValidationSamples)
        {
            if ((uint)sample.A >= full.Vertices.Length || (uint)sample.B >= full.Vertices.Length || (uint)sample.C >= full.Vertices.Length) return false;
            var p = full.Vertices[sample.A] * sample.Barycentric.X + full.Vertices[sample.B] * sample.Barycentric.Y + full.Vertices[sample.C] * sample.Barycentric.Z;
            if (!query.Nearest(p, 1f, 1e-16f, out _, out _, out var squared) || !float.IsFinite(squared)) return false;
            maximumError = MathF.Max(maximumError, MathF.Sqrt(squared));
        }
        return maximumError <= tolerance;
    }

    private static int[] Identity(int count) { var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i; return result; }
}
