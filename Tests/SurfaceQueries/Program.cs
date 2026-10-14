using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.SurfaceProfiles;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
const int width = 192, depth = 192; // 73,728 source faces, matching the reported mod scale.
var vertices = new Vector3[(width + 1) * (depth + 1)];
var indices = new int[width * depth * 6];
for (int z = 0; z <= depth; z++) for (int x = 0; x <= width; x++)
    vertices[z * (width + 1) + x] = new((x - width / 2) * .01f, .1f * MathF.Sin(x * .07f) * MathF.Cos(z * .08f), (z - depth / 2) * .01f);
for (int z = 0, i = 0; z < depth; z++) for (int x = 0; x < width; x++)
{
    int a = z * (width + 1) + x, b = a + 1, c = a + width + 1, d = c + 1;
    indices[i++] = a; indices[i++] = b; indices[i++] = c; indices[i++] = b; indices[i++] = d; indices[i++] = c;
}
var geometry = new BodyMeshGeometry(vertices, indices);
long beforeBuild = GC.GetAllocatedBytesForCurrentThread(); var buildTimer = Stopwatch.StartNew();
var bvh = new SurfaceMeshBvh(vertices, indices); buildTimer.Stop();
long buildBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBuild;
var random = new Random(1234);
Vector3 position = new(1, 2, -1); var rotation = Quaternion.CreateFromYawPitchRoll(.2f, .6f, -.3f);
var world = vertices.Select(p => Vector3.Transform(p, rotation) + position).ToArray();
float BruteNearest(Vector3 p, Vector3[] points)
{
    float best = float.MaxValue;
    for (int i = 0; i < indices.Length; i += 3)
    {
        var a = points[indices[i]]; var b = points[indices[i + 1]]; var c = points[indices[i + 2]];
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-16f) continue;
        best = MathF.Min(best, Vector3.DistanceSquared(p, SurfaceMeshBvh.ClosestPoint(p, a, b, c)));
    }
    return best;
}
bool BruteSupport(Vector3 p, Vector3[] points, float radius, out float y)
{
    y = float.MinValue;
    for (int i = 0; i < indices.Length; i += 3)
        if (BodyMeshGeometry.TrySphereSupport(p, radius, points[indices[i]], points[indices[i + 1]], points[indices[i + 2]], out var candidate)) y = MathF.Max(y, candidate);
    return y != float.MinValue;
}
int testedNearest = 0, testedSupport = 0;
for (int q = 0; q < 40; q++)
{
    var local = new Vector3(random.NextSingle() * 3 - 1.5f, random.NextSingle() * .8f, random.NextSingle() * 3 - 1.5f);
    Check(bvh.Nearest(local, 1, 1e-16f, out _, out _, out var actual), "BVH nearest failed");
    testedNearest += bvh.LastTriangleTests;
    Check(MathF.Abs(actual - BruteNearest(local, vertices)) < 2e-6f, "Nearest differs from exhaustive mesh");
    var center = Vector3.Transform(local, rotation) + position;
    bool expected = BruteSupport(center, world, .035f, out var expectedY);
    bool found = bvh.SphereSupport(center, .035f, position, rotation, 1, out var actualY);
    testedSupport += bvh.LastTriangleTests;
    Check(found == expected && (!found || MathF.Abs(actualY - expectedY) < 3e-5f), "Rotated sphere support differs from exhaustive mesh");
    var tilt = Quaternion.CreateFromYawPitchRoll(random.NextSingle() * 6, random.NextSingle() * 6, random.NextSingle() * 6);
    float expectedBottom = vertices.Min(p => position.Y + Vector3.Transform(p, tilt).Y);
    Check(MathF.Abs(geometry.Bottom(position, tilt) - expectedBottom) < 2e-6f, "Exact BVH minimum projection differs from all mesh vertices");
}
Check(testedNearest < 40 * width * depth * 2 / 20, "Nearest BVH did not cull high-poly faces");
Check(testedSupport < 40 * width * depth * 2 / 20, "Sphere BVH did not cull high-poly faces");
var profile = new CharacterSurfaceProfile();
var runtime = new CharacterSurfaceRuntime(profile, default, new[] { new CharacterSurfaceBoneSeed("waist", "torso", 1, 1, 1, Mesh: geometry) });
runtime.UpdateBonePose("waist", position, rotation);
var point = Vector3.Transform(new Vector3(.2f, .3f, -.1f), rotation) + position;
Check(runtime.TryGetNearestSurface("waist", point, CharacterSurfaceUsage.Physics, out var hit) &&
    MathF.Abs(hit.DistanceSquared - BruteNearest(point, world)) < 3e-6f, "Runtime inverse-pose query differs");
// Skin refit at an unchanged rigid pose must invalidate bounds, queries and lazy debug arrays.
for (int i = 0; i < vertices.Length; i++) vertices[i].Y += .25f;
geometry.MarkChanged(); runtime.UpdateBonePose("waist", position, rotation); bvh.Refit();
world = vertices.Select(p => Vector3.Transform(p, rotation) + position).ToArray();
Check(MathF.Abs(geometry.Bottom(position, rotation) - world.Min(p => p.Y)) < 2e-6f, "Mesh bottom cached stale pre-refit vertices");
Check(runtime.TryGetNearestSurface("waist", point, CharacterSurfaceUsage.Physics, out hit) &&
    MathF.Abs(hit.DistanceSquared - BruteNearest(point, world)) < 3e-6f, "Runtime retained stale BVH after mesh refit");
Check(runtime.TryGetBounds("waist", out var boundsMin, out var boundsMax) && world.All(p =>
    p.X >= boundsMin.X && p.Y >= boundsMin.Y && p.Z >= boundsMin.Z && p.X <= boundsMax.X && p.Y <= boundsMax.Y && p.Z <= boundsMax.Z),
    "Cached refitted rotated bounds exclude actual mesh vertices");
Check(!runtime.TryGetBounds("missing", out _, out _), "Bounds query accepted unknown bone");
var debug = runtime.GetDebugBones(CharacterSurfaceUsage.Physics, 128, 2048).Single();
Check(debug.Indices.Length == 128 * 3 && debug.Vertices.Length == 128 * 3, "Debug high-poly sampling exceeded per-body budget");
Check(Vector3.Distance(debug.Vertices.Span[0], world[indices[0]]) < 1e-6f, "Sampled wireframe vertex pose differs");
var all = runtime.GetDebugBones(CharacterSurfaceUsage.Physics).Single();
Check(all.Vertices.Length == vertices.Length && all.Indices.Length == indices.Length, "Full public debug geometry lost");
var many = new CharacterSurfaceRuntime(profile, default, Enumerable.Range(0, 40).Select(i => new CharacterSurfaceBoneSeed($"b{i}", "torso", 1, 1, 1, Mesh: geometry)).ToArray());
foreach (var bone in many.Bones) bone.UpdatePose(position, rotation);
Check(many.GetDebugBones(CharacterSurfaceUsage.Physics, 128, 2048).Sum(b => b.Indices.Length / 3) == 2048, "Global debug face budget exceeded");
// Warm both JIT and lazy debug allocations before verifying the frame loop.
for (int i = 0; i < 50; i++)
{ runtime.UpdateBonePose("waist", position + new Vector3(i * .0001f, 0, 0), rotation); runtime.TryGetNearestSurface("waist", point, CharacterSurfaceUsage.Physics, out _); runtime.TryGetVerticalSupport(point, .035f, "waist", out _, out _); runtime.Bones[0].GetDebugBone(CharacterSurfaceUsage.Physics, 128); geometry.Bottom(position, rotation); geometry.Bounds(position, rotation, out _, out _); }
var timer = Stopwatch.StartNew(); long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1000; i++)
{ runtime.UpdateBonePose("waist", position + new Vector3(i * .0001f, 0, 0), rotation); runtime.TryGetNearestSurface("waist", point, CharacterSurfaceUsage.Physics, out _); runtime.TryGetVerticalSupport(point, .035f, "waist", out _, out _); runtime.Bones[0].GetDebugBone(CharacterSurfaceUsage.Physics, 128); }
long allocated = GC.GetAllocatedBytesForCurrentThread() - before; timer.Stop();
Check(allocated == 0, $"Steady query/pose/debug frame allocated {allocated} bytes");
Console.WriteLine($"PASS {checks} checks; {indices.Length / 3:N0} full-fidelity faces; nearest avg {testedNearest / 40} face tests, support avg {testedSupport / 40}.");
Console.WriteLine($"BVH build {buildTimer.Elapsed.TotalMilliseconds:F2} ms / {buildBytes / 1048576d:F2} MiB; 1000 pose+nearest+support+128-face-debug frames {timer.Elapsed.TotalMilliseconds:F2} ms, {allocated} bytes.");
