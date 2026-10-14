using System.Numerics;
using System.Diagnostics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var positions = new[] { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1) };
MeshSkinInfluence[][] sources = positions.Select(p => new[] {
    new MeshSkinInfluence(0, .35f, p), new MeshSkinInfluence(1, .65f, p)
}).ToArray();
var bones = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
// Root scale belongs between model bone rotation and root rotation. Include
// reflection and changing joint orientations so quaternion-only replay fails.
var rootRotation = Quaternion.CreateFromYawPitchRoll(.63f, -.21f, .37f);
var rootScale = new Vector3(-1.7f, .6f, 1.25f);
var modelRotations = new[] { Quaternion.CreateFromYawPitchRoll(.2f, -.5f, .1f), Quaternion.CreateFromYawPitchRoll(-.4f, .7f, .9f) };
var origins = new[] { new Vector3(4, 2, -3), new Vector3(3.7f, 2.4f, -2.9f) };
var unweighted = new[] { new Vector3(.2f, -.35f, .47f), new Vector3(-.14f, .63f, .23f) };
var affineSources = new MeshSkinInfluence[1][];
affineSources[0] = new MeshSkinInfluence[2];
for (int i = 0; i < 2; i++)
{
    var affine = MeshSkinDeformer.BoneTransform(origins[i], rootRotation * modelRotations[i], rootRotation, rootScale);
    var renderedPoint = origins[i] + Vector3.Transform(Vector3.Transform(unweighted[i], modelRotations[i]) * rootScale, rootRotation);
    Check(Vector3.Distance(Vector3.Transform(unweighted[i], affine), renderedPoint) < 1e-6f, "Nonuniform root scale applied around world bone axes");
    Check(Matrix4x4.Invert(affine, out var inverse), "Reflected initial bone frame became singular");
    affineSources[0][i] = new MeshSkinInfluence(i, i == 0 ? .3f : .7f, Vector3.Transform(renderedPoint, inverse));
    modelRotations[i] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f) * modelRotations[i]);
    bones[i] = MeshSkinDeformer.BoneTransform(origins[i], rootRotation * modelRotations[i], rootRotation, rootScale);
}
var affineWorld = new Vector3[1];
Check(MeshSkinDeformer.Evaluate(affineSources, bones, affineWorld), "Reflected anisotropic joint skin failed");
var expectedAffine = Vector3.Zero;
for (int i = 0; i < 2; i++) expectedAffine += (origins[i] + Vector3.Transform(Vector3.Transform(unweighted[i], modelRotations[i]) * rootScale, rootRotation)) * (i == 0 ? .3f : .7f);
Check(Vector3.Distance(expectedAffine, affineWorld[0]) < 2e-6f, "Moving blended skin changed nonuniform scale order");
bones[0] = bones[1] = Matrix4x4.Identity;
var world = new Vector3[positions.Length];
Check(MeshSkinDeformer.Evaluate(sources, bones, world), "Initial skin reconstruction failed");
for (int i = 0; i < world.Length; i++) Check(Vector3.Distance(world[i], positions[i]) < 1e-6f, "Initial surface changed");
bones[1] = Matrix4x4.CreateRotationZ(.8f) * Matrix4x4.CreateTranslation(2, 1, 0);
Check(MeshSkinDeformer.Evaluate(sources, bones, world), "Bent skin reconstruction failed");
for (int i = 0; i < world.Length; i++)
    Check(Vector3.Distance(world[i], positions[i] * .35f + Vector3.Transform(positions[i], bones[1]) * .65f) < 1e-6f,
        "Joint blend collapsed to rigid dominant owner");
var bindings = new[] { new MeshSourceBinding(0, 1, 2, new(.2f, .3f, .5f)),
    new MeshSourceBinding(0, 1, 2, Vector3.UnitY), new MeshSourceBinding(0, 1, 2, Vector3.UnitZ) };
var local = new Vector3[bindings.Length];
var orientation = Quaternion.CreateFromYawPitchRoll(.4f, -.3f, .2f);
var center = new Vector3(5, -2, 3);
Check(MeshSkinDeformer.Interpolate(world, bindings, center, orientation, local), "Clipped vertex interpolation failed");
Check(Vector3.Distance(center + Vector3.Transform(local[0], orientation), world[0] * .2f + world[1] * .3f + world[2] * .5f) < 1e-6f,
    "Weight-boundary vertex lost source barycentrics");
var pool = new BufferPool();
pool.Take<Triangle>(2, out var triangles);
triangles[0] = new(positions[0], positions[1], positions[2]);
triangles[1] = new(positions[0], positions[2], positions[1]);
var mesh = new Mesh(triangles, Vector3.One, pool);
// beta.22's small BinnedBuild path leaves these backlinks uninitialized.
// Poison them deterministically instead of relying on allocator luck to expose it.
mesh.Tree.Leaves[0] = new Leaf(int.MaxValue, 0);
mesh.Tree.Leaves[1] = new Leaf(int.MaxValue - 1, 1);
Check(MeshSurfaceRefitter.Refit(ref mesh, local, new[] { 0, 1, 2 }), "In-place mesh refit failed");
Check(Vector3.Distance(mesh.Triangles[0].A, local[0]) < 1e-6f && Vector3.Distance(mesh.Triangles[1].C, local[1]) < 1e-6f,
    "Front/back triangles diverged");
var minimum = Vector3.Min(local[0], Vector3.Min(local[1], local[2]));
var maximum = Vector3.Max(local[0], Vector3.Max(local[1], local[2]));
for (int i = 0; i < mesh.Tree.LeafCount; i++)
{
    ref var leaf = ref mesh.Tree.GetNodeChildForLeaf(i);
    Check(Vector3.Distance(leaf.Min, minimum) < 1e-6f && Vector3.Distance(leaf.Max, maximum) < 1e-6f,
        "BVH bounds retained the unbent shape");
}
var accepted = mesh.Triangles[0].A;
local[0] = new Vector3(float.NaN);
Check(!MeshSurfaceRefitter.Refit(ref mesh, local, new[] { 0, 1, 2 }) && mesh.Triangles[0].A == accepted,
    "Invalid deformation partially corrupted the accepted collider");
var shapes = new Shapes(pool, 1);
var shapeIndex = shapes.Add(mesh);
ref var registered = ref shapes.GetShape<Mesh>(shapeIndex.Index);
var relocated = positions.Select(p => p + new Vector3(10, 2, 0)).ToArray();
Check(MeshSurfaceRefitter.Refit(ref registered, relocated, new[] { 0, 1, 2 }), "Registered shape refit failed");
var ray = new RayData { Origin = new Vector3(10, 5, .25f), Direction = -Vector3.UnitY };
var maximumT = 10f;
var handler = new Hit();
registered.RayTest(new RigidPose(Vector3.Zero, Quaternion.Identity), ray, ref maximumT, ref handler);
Check(handler.Found && MathF.Abs(handler.T - 3) < 1e-5f, "Refitted BVH missed relocated triangle");
ray.Origin = new Vector3(0, 5, .25f); maximumT = 10;
handler = new Hit();
registered.RayTest(new RigidPose(Vector3.Zero, Quaternion.Identity), ray, ref maximumT, ref handler);
Check(!handler.Found, "Refitted BVH still hit the old triangle location");
// Adjacent clipped regions must share exactly the same bent source edge.
var sourceTriangle = new[] {
    new MeshWeightedVertex(positions[0], new[] { new MeshBodyWeight(0, 1) }),
    new MeshWeightedVertex(positions[1], new[] { new MeshBodyWeight(1, 1) }),
    new MeshWeightedVertex(positions[2], new[] { new MeshBodyWeight(0, .5f), new MeshBodyWeight(1, .5f) }) };
var parts = BodyMeshPartitioner.Partition(sourceTriangle, new[] { 0, 1, 2 });
var bent = new Dictionary<int, Vector3[]>();
foreach (var part in parts)
{
    var points = new Vector3[part.Value.Vertices.Length];
    Check(part.Value.SourceBindings != null && MeshSkinDeformer.Interpolate(world, part.Value.SourceBindings,
        Vector3.Zero, Quaternion.Identity, points), "Clipped partition lost deformation bindings");
    bent[part.Key] = points;
}
Check(bent[0].Count(p => bent[1].Any(q => Vector3.DistanceSquared(p, q) < 1e-12f)) >= 2,
    "Neighboring body regions separated along their shared blended boundary");
// Warmed production helper budget: 12,000 visible triangles, 24,000 physical
// front/back triangles, each vertex influenced by two physical bones.
const int faceCount = 12000;
var largeSources = new MeshSkinInfluence[faceCount * 3][];
var largeBindings = new MeshSourceBinding[largeSources.Length];
var largeWorld = new Vector3[largeSources.Length];
var largeLocal = new Vector3[largeSources.Length];
var largeIndices = Enumerable.Range(0, largeSources.Length).ToArray();
pool.Take<Triangle>(faceCount * 2, out var largeTriangles);
for (int i = 0; i < faceCount; i++)
{
    var offset = new Vector3((i % 100) * .02f, 0, (i / 100) * .02f);
    for (int j = 0; j < 3; j++)
    {
        var point = offset + positions[j] * .005f;
        var index = i * 3 + j;
        largeSources[index] = new[] { new MeshSkinInfluence(0, .5f, point), new MeshSkinInfluence(1, .5f, point) };
        largeBindings[index] = new MeshSourceBinding(index, index, index, Vector3.UnitX);
        largeLocal[index] = point;
    }
    largeTriangles[i * 2] = new(largeLocal[i * 3], largeLocal[i * 3 + 1], largeLocal[i * 3 + 2]);
    largeTriangles[i * 2 + 1] = new(largeLocal[i * 3], largeLocal[i * 3 + 2], largeLocal[i * 3 + 1]);
}
var largeMesh = new Mesh(largeTriangles, Vector3.One, pool);
void RefitLarge()
{
    if (!MeshSkinDeformer.Evaluate(largeSources, bones, largeWorld) ||
        !MeshSkinDeformer.Interpolate(largeWorld, largeBindings, Vector3.Zero, Quaternion.Identity, largeLocal) ||
        !MeshSurfaceRefitter.Refit(ref largeMesh, largeLocal, largeIndices)) throw new Exception("Budget fixture refit failed");
}
for (int i = 0; i < 20; i++) RefitLarge();
var timer = Stopwatch.StartNew();
long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 60; i++) RefitLarge();
long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
timer.Stop();
Check(allocated == 0, $"Skin/refit helpers allocate per update: {allocated}");
Console.WriteLine($"12,000 visible faces / 24,000 collision faces: {timer.Elapsed.TotalMilliseconds / 60:F3} ms/refit, zero warmed managed allocations (offline CPU fixture).");
largeMesh.Dispose(pool);
shapes.RemoveAndDispose(shapeIndex, pool); shapes.Dispose(); pool.Clear();
Console.WriteLine("PASS: exact multibone skin, joint-boundary continuity, offset body frame, registered BVH ray refit and invalid-update atomicity.");

struct Hit : IShapeRayHitHandler
{
    public bool Found;
    public float T;
    public bool AllowTest(int childIndex) => true;
    public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, int childIndex)
    { Found = true; T = t; maximumT = t; }
}
