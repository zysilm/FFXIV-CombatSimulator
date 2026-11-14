// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuUtilities;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>
/// Limits mesh contact sampling before BEPU allocates continuation storage. The source
/// triangles and queries stay exact; densely overlapping contacts use spatial representatives.
/// </summary>
public static class CollisionWorkBudget
{
    public const int GridWidth = 2;
    public const int SamplesPerCell = 4;
    public const int MaximumRegions = 64; // small meshes retain every original face
    public const int MaximumChildrenPerRegion = 4;
    public const int MaximumChildren = MaximumRegions * MaximumChildrenPerRegion;
    private static long sampledPairs, limitedQueries, processedPairs;
    private static int peakChildren;
    private static long selectionTicks, childQueryTicks, dispatchTicks;
    public static long SampledPairs => Interlocked.Read(ref sampledPairs);
    public static long LimitedQueries => Interlocked.Read(ref limitedQueries);
    public static long ProcessedPairs => Interlocked.Read(ref processedPairs);
    public static int PeakChildren => Volatile.Read(ref peakChildren);
    public static double SelectionMilliseconds => Interlocked.Read(ref selectionTicks) * 1000d / Stopwatch.Frequency;
    public static double ChildQueryMilliseconds => Interlocked.Read(ref childQueryTicks) * 1000d / Stopwatch.Frequency;
    public static double DispatchMilliseconds => Interlocked.Read(ref dispatchTicks) * 1000d / Stopwatch.Frequency;

    public static void Register(CollisionTaskRegistry registry, Func<int>? quality = null) => registry.Register(new BoundedMeshPairTask(quality));

    private struct Candidate
    {
        public int Triangle;
        public float Score;
        public Triangle LocalB;
        public Vector3 Min, Max;
        public Vector3 Probe;
        public bool HasProbe;
    }

    /// <summary>A selected original pair of faces, never a simplified replacement collider.</summary>
    public readonly record struct FacePair(int A, int B);

    // Also callable by offline load tests without constructing a native client or solver.
    public static unsafe int Collect(ref Mesh meshA, ref Mesh meshB, in BoundsTestedPair pair,
        float dt, Span<FacePair> destination, out bool sampled, out int limited, int quality = 50)
    {
        Span<Candidate> selected = stackalloc Candidate[MaximumRegions];
        SelectRegions(ref meshA, ref meshB, pair, dt, selected, out sampled, quality);
        int count = 0; limited = 0;
        Span<int> hits = stackalloc int[MaximumChildrenPerRegion];
        for (int i = 0; i < selected.Length; i++)
        {
            if (selected[i].Triangle < 0) continue;
            int hitCount = FindChildren(ref meshB, selected[i], hits, out bool truncated, LeafBudget(quality));
            if (truncated) limited++;
            for (int j = 0; j < hitCount && count < destination.Length; j++)
                destination[count++] = new FacePair(selected[i].Triangle, hits[j]);
        }
        return count;
    }

    private static void SelectRegions(ref Mesh meshA, ref Mesh meshB, in BoundsTestedPair pair,
        float dt, Span<Candidate> selected, out bool sampled, int quality)
    {
        for (int i = 0; i < selected.Length; i++) selected[i].Triangle = -1;
        sampled = false;
        if (meshA.Tree.LeafCount == 0 || meshB.Tree.LeafCount == 0) return;
        var inverseB = Quaternion.Conjugate(pair.OrientationB);
        var rotation = Quaternion.Normalize(inverseB * pair.OrientationA);
        var offset = Vector3.Transform(-pair.OffsetB, inverseB);
        TreeBounds(ref meshB, Quaternion.Identity, out var bMin, out var bMax);
        TreeBounds(ref meshA, rotation, out var aMin, out var aMax);
        aMin += offset; aMax += offset;
        // Keep the native pair's allowed swept window. Work is bounded by
        // selection/storage limits, never by silently shrinking physical distance.
        var angularExpansion = pair.AngularVelocityA.Length() * (aMax - aMin).Length() * .5f +
                               pair.AngularVelocityB.Length() * ((bMax - bMin).Length() * .5f + pair.OffsetB.Length());
        var speedExpansion = (pair.RelativeLinearVelocityA.Length() + angularExpansion) * MathF.Max(dt, 0f);
        var expansion = MathF.Max(0f, pair.SpeculativeMargin) +
            MathF.Min(MathF.Max(0f, pair.MaximumExpansion), speedExpansion);
        var padding = new Vector3(expansion);
        var min = Vector3.Max(aMin - padding, bMin - padding);
        var max = Vector3.Min(aMax + padding, bMax + padding);
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) return;
        var extent = Vector3.Max(max - min, new Vector3(1e-5f));
        var exactSmallMesh = meshA.Triangles.Length <= MaximumRegions;
        if (exactSmallMesh)
        {
            int next = 0;
            for (int index = 0; index < meshA.Triangles.Length; index++)
            {
                meshA.GetLocalChild(index, out var triangle);
                triangle.A = Vector3.Transform(triangle.A, rotation) + offset;
                triangle.B = Vector3.Transform(triangle.B, rotation) + offset;
                triangle.C = Vector3.Transform(triangle.C, rotation) + offset;
                var tMin = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C));
                var tMax = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C));
                if (!Intersects(tMin - padding, tMax + padding, bMin, bMax)) continue;
                selected[next++] = new Candidate { Triangle = index, LocalB = triangle, Min = tMin - padding, Max = tMax + padding };
            }
            return;
        }
        sampled = true;
        unsafe
        {
            fixed (Candidate* samples = selected)
            {
                var inverseRotation = Quaternion.Conjugate(rotation);
                for (int z = 0; z < GridWidth; z++)
                for (int y = 0; y < GridWidth; y++)
                for (int x = 0; x < GridWidth; x++)
                {
                    var cellMin = min + extent * new Vector3((float)x / GridWidth, (float)y / GridWidth, (float)z / GridWidth);
                    var cellMax = min + extent * new Vector3((float)(x + 1) / GridWidth, (float)(y + 1) / GridWidth, (float)(z + 1) / GridWidth);
                    var rawMin = new Vector3(float.MaxValue); var rawMax = new Vector3(float.MinValue);
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var p = new Vector3((corner & 1) == 0 ? cellMin.X : cellMax.X,
                            (corner & 2) == 0 ? cellMin.Y : cellMax.Y, (corner & 4) == 0 ? cellMin.Z : cellMax.Z);
                        p = Vector3.Transform(p - offset, inverseRotation) / meshA.Scale;
                        rawMin = Vector3.Min(rawMin, p); rawMax = Vector3.Max(rawMax, p);
                    }
                    int start = ((z * GridWidth + y) * GridWidth + x) * SamplesPerCell;
                    var center = (cellMin + cellMax) * .5f;
                    var selector = new RegionSelector { Mesh = (Mesh*)Unsafe.AsPointer(ref meshA), Rotation = rotation,
                        Offset = offset, Padding = padding, BMin = bMin, BMax = bMax, Center = center,
                        Samples = samples + start, SampleCount = 1 + Math.Clamp(quality, 0, 100) * 3 / 100,
                        LeafBudget = LeafBudget(quality) };
                    var rawCenter = Vector3.Transform(center - offset, inverseRotation) / meshA.Scale;
                    VisitNearest(ref meshA, rawMin, rawMax, rawCenter, ref selector);
                }
            }
        }
    }

    private unsafe struct RegionSelector : IBreakableForEach<int>
    {
        public Mesh* Mesh;
        public Quaternion Rotation;
        public Vector3 Offset, Padding, BMin, BMax, Center;
        public Candidate* Samples;
        public int Visited;
        public int SampleCount, LeafBudget;
        public bool LoopBody(int index)
        {
            Visited++;
            Mesh->GetLocalChild(index, out var triangle);
            triangle.A = Vector3.Transform(triangle.A, Rotation) + Offset;
            triangle.B = Vector3.Transform(triangle.B, Rotation) + Offset;
            triangle.C = Vector3.Transform(triangle.C, Rotation) + Offset;
            var min = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C));
            var max = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C));
            if (Intersects(min - Padding, max + Padding, BMin, BMax))
            {
                int worst = 0;
                for (int i = 0; i < SampleCount; i++)
                {
                    if (Samples[i].Triangle < 0) { worst = i; break; }
                    if (Samples[i].Score > Samples[worst].Score) worst = i;
                }
                // Bounds distance is a proven lower bound on point-to-triangle
                // distance. Once two closer faces are known, skip exact ranking.
                var lowerBound = Vector3.DistanceSquared(Center, Vector3.Clamp(Center, min, max));
                if (Samples[worst].Triangle >= 0 && Samples[worst].Score <= lowerBound)
                    return Visited < LeafBudget;
                var score = PointTriangle(Center, triangle);
                if (Samples[worst].Triangle < 0 || score < Samples[worst].Score)
                    Samples[worst] = new Candidate { Triangle = index, Score = score, LocalB = triangle, Min = min - Padding, Max = max + Padding,
                        Probe = ClosestPoint(Center, triangle), HasProbe = true };
            }
            return Visited < LeafBudget;
        }
    }

    private static bool Intersects(Vector3 min, Vector3 max, Vector3 otherMin, Vector3 otherMax)
        => min.X <= otherMax.X && max.X >= otherMin.X && min.Y <= otherMax.Y && max.Y >= otherMin.Y && min.Z <= otherMax.Z && max.Z >= otherMin.Z;

    private static void TreeBounds(ref Mesh mesh, Quaternion rotation, out Vector3 min, out Vector3 max)
    {
        // Mesh.ComputeBounds scans every triangle. Pair selection only needs a
        // conservative enclosure, available in constant time from the refitted BVH.
        ref var root = ref mesh.Tree.Nodes[0];
        var rawMin = root.A.Min; var rawMax = root.A.Max;
        if (mesh.Tree.LeafCount > 1)
        { rawMin = Vector3.Min(rawMin, root.B.Min); rawMax = Vector3.Max(rawMax, root.B.Max); }
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? rawMin.X : rawMax.X,
                (corner & 2) == 0 ? rawMin.Y : rawMax.Y, (corner & 4) == 0 ? rawMin.Z : rawMax.Z);
            point = Vector3.Transform(point * mesh.Scale, rotation);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
    }

    private static void VisitNearest<T>(ref Mesh mesh, Vector3 min, Vector3 max, Vector3 center, ref T visitor)
        where T : struct, IBreakableForEach<int>
    {
        if (mesh.Tree.LeafCount == 0) return;
        Span<int> stack = stackalloc int[128];
        int count = 1, visits = 0; stack[0] = 0;
        while (count > 0 && visits++ < 1024)
        {
            int nodeIndex = stack[--count];
            if (nodeIndex < 0) { if (!visitor.LoopBody(-nodeIndex - 1)) return; continue; }
            ref var node = ref mesh.Tree.Nodes[nodeIndex];
            bool a = Intersects(min, max, node.A.Min, node.A.Max);
            bool b = (nodeIndex != 0 || mesh.Tree.LeafCount > 1) && node.B.LeafCount > 0 && Intersects(min, max, node.B.Min, node.B.Max);
            if (a && b && count < stack.Length - 1)
            {
                float da = Vector3.DistanceSquared(center, Vector3.Clamp(center, node.A.Min, node.A.Max));
                float db = Vector3.DistanceSquared(center, Vector3.Clamp(center, node.B.Min, node.B.Max));
                if (da < db) { stack[count++] = node.B.Index; stack[count++] = node.A.Index; }
                else { stack[count++] = node.A.Index; stack[count++] = node.B.Index; }
            }
            else if (a && count < stack.Length) stack[count++] = node.A.Index;
            else if (b && count < stack.Length) stack[count++] = node.B.Index;
        }
    }

    private unsafe struct NearestChildren : IBreakableForEach<int>
    {
        public Mesh* Mesh;
        public Triangle Source;
        public Vector3 SourceMin, SourceMax;
        public int* Indices;
        public float* Distances;
        public int Count, Visited, LeafBudget;
        public bool LoopBody(int leaf)
        {
            Visited++;
            Mesh->GetLocalChild(leaf, out var triangle);
            int worst = 0;
            if (Count >= MaximumChildrenPerRegion)
            {
                for (int i = 1; i < Count; i++) if (Distances[i] > Distances[worst]) worst = i;
                var min = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C));
                var max = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C));
                var gap = Vector3.Max(Vector3.Max(SourceMin - max, min - SourceMax), Vector3.Zero);
                if (Distances[worst] <= gap.LengthSquared()) return Visited < LeafBudget;
            }
            float distance = TriangleDistanceSquared(Source, triangle);
            if (Count < MaximumChildrenPerRegion) worst = Count++;
            else if (distance >= Distances[worst]) return Visited < LeafBudget;
            Indices[worst] = leaf; Distances[worst] = distance;
            return Visited < LeafBudget;
        }
    }

    private static int LeafBudget(int quality) => 8 + Math.Clamp(quality, 0, 100) * 16 / 100;

    private static unsafe int FindChildren(ref Mesh mesh, Candidate candidate, Span<int> hits, out bool limited, int leafBudget)
    {
        Span<float> distances = stackalloc float[MaximumChildrenPerRegion];
        fixed (int* hitPointer = hits)
        fixed (float* distancePointer = distances)
        {
            var enumerator = new NearestChildren { Mesh = (Mesh*)Unsafe.AsPointer(ref mesh),
                Source = candidate.LocalB, Indices = hitPointer, Distances = distancePointer, LeafBudget = leafBudget,
                SourceMin = Vector3.Min(candidate.LocalB.A, Vector3.Min(candidate.LocalB.B, candidate.LocalB.C)),
                SourceMax = Vector3.Max(candidate.LocalB.A, Vector3.Max(candidate.LocalB.B, candidate.LocalB.C)) };
            var inverseScale = Vector3.One / mesh.Scale;
            var min = candidate.Min * inverseScale;
            var max = candidate.Max * inverseScale;
            TreeBounds(ref mesh, Quaternion.Identity, out var meshMin, out var meshMax);
            // Large terrain faces have centroids far outside the contacting actor.
            // Traverse near the actor's projection onto the actual source face instead.
            var center = (candidate.HasProbe ? candidate.Probe : ClosestPoint((meshMin + meshMax) * .5f, candidate.LocalB)) * inverseScale;
            VisitNearest(ref mesh, Vector3.Min(min, max), Vector3.Max(min, max), center, ref enumerator);
            limited = enumerator.Visited >= leafBudget;
            return enumerator.Count;
        }
    }

    private static float TriangleDistanceSquared(Triangle a, Triangle b)
    {
        // Vertex-to-face distances rank local BVH candidates. The production
        // triangle tester, including edge contacts, remains the final authority.
        float best = PointTriangle(a.A, b);
        best = MathF.Min(best, PointTriangle(a.B, b)); best = MathF.Min(best, PointTriangle(a.C, b));
        best = MathF.Min(best, PointTriangle(b.A, a)); best = MathF.Min(best, PointTriangle(b.B, a));
        return MathF.Min(best, PointTriangle(b.C, a));
    }

    private static float PointTriangle(Vector3 p, Triangle triangle)
        => Vector3.DistanceSquared(p, ClosestPoint(p, triangle));

    private static Vector3 ClosestPoint(Vector3 p, Triangle triangle)
    {
        var a = triangle.A; var b = triangle.B; var c = triangle.C;
        var ab = b - a; var ac = c - a; var ap = p - a;
        var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        var denominator = va + vb + vc;
        if (MathF.Abs(denominator) < 1e-20f) return ap.LengthSquared() <= bp.LengthSquared() && ap.LengthSquared() <= cp.LengthSquared() ? a : bp.LengthSquared() <= cp.LengthSquared() ? b : c;
        return a + ab * (vb / denominator) + ac * (vc / denominator);
    }

    private sealed class BoundedMeshPairTask : CollisionTask
    {
        private readonly Func<int>? quality;
        public BoundedMeshPairTask(Func<int>? quality)
        {
            this.quality = quality;
            BatchSize = 1; ShapeTypeIndexA = Mesh.Id; ShapeTypeIndexB = Mesh.Id; SubtaskGenerator = true; PairType = CollisionTaskPairType.BoundsTestedPair;
        }
        private readonly CompoundPairCollisionTask<Mesh, Mesh, MeshPairOverlapFinder<Mesh, Mesh>,
            MeshPairContinuations<Mesh, Mesh>, CompoundMeshReduction> exactSmallTask = new();
        public override unsafe void ExecuteBatch<TCallbacks>(ref UntypedList batch, ref CollisionBatcher<TCallbacks> batcher)
        {
            var pairs = batch.Buffer.As<BoundsTestedPair>();
            Span<Candidate> selected = stackalloc Candidate[MaximumRegions];
            Span<int> hits = stackalloc int[MaximumChildrenPerRegion];
            for (int pairIndex = 0; pairIndex < batch.Count; pairIndex++)
            {
                Interlocked.Increment(ref processedPairs);
                ref var pair = ref pairs[pairIndex];
                ref var meshA = ref Unsafe.AsRef<Mesh>(pair.A);
                ref var meshB = ref Unsafe.AsRef<Mesh>(pair.B);
                // Preserve the full footprint for inexpensive terrain/body pairs.
                // Dispatch exactly this pair, including when the batcher flushes
                // more than the normal single-pair batch size.
                long product = (long)meshA.Triangles.Length * meshB.Triangles.Length;
                if (Math.Min(meshA.Triangles.Length, meshB.Triangles.Length) <= 64 && product <= 65536)
                {
                    int pairBytes = Unsafe.SizeOf<BoundsTestedPair>();
                    batch.Buffer.Slice(pairIndex * pairBytes, pairBytes, out var singleBuffer);
                    var single = new UntypedList { Buffer = singleBuffer, Count = 1, ByteCount = pairBytes, ElementSizeInBytes = pairBytes };
                    exactSmallTask.ExecuteBatch(ref single, ref batcher);
                    continue;
                }
                long stage = Stopwatch.GetTimestamp();
                int contactQuality = Math.Clamp(quality?.Invoke() ?? 50, 0, 100);
                SelectRegions(ref meshA, ref meshB, pair, batcher.Dt, selected, out var sampled, contactQuality);
                Interlocked.Add(ref selectionTicks, Stopwatch.GetTimestamp() - stage);
                if (sampled) Interlocked.Increment(ref sampledPairs);
                batcher.Pool.Take<ChildOverlapsCollection>(MaximumRegions, out var regions);
                batcher.Pool.Take<OverlapQueryForPair>(MaximumRegions, out var queries);
                regions.Clear(0, regions.Length);
                int regionCount = 0, total = 0;
                stage = Stopwatch.GetTimestamp();
                foreach (var candidate in selected)
                {
                    if (candidate.Triangle < 0) continue;
                    int count = FindChildren(ref meshB, candidate, hits, out var limited, LeafBudget(contactQuality));
                    if (limited) Interlocked.Increment(ref limitedQueries);
                    if (count == 0) continue;
                    ref var region = ref regions[regionCount];
                    region.ChildIndex = candidate.Triangle; region.Count = count;
                    batcher.Pool.Take<int>(count, out region.Overlaps);
                    for (int i = 0; i < count; i++) region.Overlaps[i] = hits[i];
                    queries[regionCount] = new OverlapQueryForPair { Container = pair.B, Min = candidate.Min, Max = candidate.Max };
                    regionCount++; total += count;
                }
                Interlocked.Add(ref childQueryTicks, Stopwatch.GetTimestamp() - stage);
                stage = Stopwatch.GetTimestamp();
                if (total == 0) batcher.ProcessEmptyResult(ref pair.Continuation);
                else
                {
                    int peak;
                    do { peak = Volatile.Read(ref peakChildren); if (peak >= total) break; }
                    while (Interlocked.CompareExchange(ref peakChildren, total, peak) != peak);
                    regions.Slice(0, regionCount, out var usedRegions);
                    queries.Slice(0, regionCount, out var usedQueries);
                    var handler = new MeshPairContinuations<Mesh, Mesh>();
                    ref var continuation = ref handler.CreateContinuation(ref batcher, total, ref usedRegions, ref usedQueries, pair, out var continuationIndex);
                    int nextChild = 0;
                    for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
                    {
                        ref var region = ref regions[regionIndex];
                        handler.GetChildAData(ref batcher, ref continuation, pair, region.ChildIndex,
                            out var poseA, out var typeA, out var shapeA);
                        for (int i = 0; i < region.Count; i++)
                        {
                            int originalB = region.Overlaps[i];
                            int childA = pair.FlipMask < 0 ? originalB : region.ChildIndex;
                            int childB = pair.FlipMask < 0 ? region.ChildIndex : originalB;
                            int continuationChild = nextChild++;
                            var subpair = new PairContinuation(pair.Continuation.PairId, childA, childB,
                                handler.CollisionContinuationType, continuationIndex, continuationChild);
                            if (!batcher.Callbacks.AllowCollisionTesting(pair.Continuation.PairId, childA, childB))
                            { batcher.ProcessUntestedSubpairConvexResult(ref subpair); continue; }
                            handler.ConfigureContinuationChild(ref batcher, ref continuation, continuationChild, pair,
                                region.ChildIndex, typeA, originalB, poseA, out var poseB, out var typeB, out var shapeB);
                            var offset = pair.OffsetB + poseB.Position - poseA.Position;
                            if (pair.FlipMask < 0)
                                batcher.AddDirectly(typeB, typeA, shapeB, shapeA, -offset, poseB.Orientation, poseA.Orientation, pair.SpeculativeMargin, subpair);
                            else batcher.AddDirectly(typeA, typeB, shapeA, shapeB, offset, poseA.Orientation, poseB.Orientation, pair.SpeculativeMargin, subpair);
                        }
                    }
                }
                Interlocked.Add(ref dispatchTicks, Stopwatch.GetTimestamp() - stage);
                for (int i = 0; i < regionCount; i++) batcher.Pool.Return(ref regions[i].Overlaps);
                batcher.Pool.Return(ref regions); batcher.Pool.Return(ref queries);
            }
        }
    }
}
