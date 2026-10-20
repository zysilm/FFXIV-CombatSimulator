// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.MeshVolume;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private bool activeRagdollMeshVolumes;
    internal int MeshGeometryGeneration => externalBodyGeneration;
    public string MeshGeometryStatus { get; private set; } = "No rendered mesh captured";

    public bool IsMeshBody(string boneName)
    {
        foreach (var rb in ragdollBones)
            if (rb.Name == boneName) return rb.MeshGeometry != null;
        return false;
    }

    public bool TryGetBoneGeometryBounds(string boneName, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (simulation == null || !physicsStarted || !isActive) return false;
        if (IsMeshBody(boneName) && characterSurfaceRuntime != null &&
            characterSurfaceRuntime.TryGetBounds(boneName, out min, out max)) return true;
        foreach (var rb in ragdollBones)
        {
            if (rb.Name != boneName || rb.MeshGeometry == null) continue;
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            rb.MeshGeometry.Bounds(body.Pose.Position, body.Pose.Orientation, out min, out max);
            return true;
        }
        return false;
    }

    /// <summary>Actual triangle surface under a world-space point, without capsule expansion.</summary>
    public bool TryGetBoneVerticalSupport(string boneName, Vector3 point, out Vector3 surface)
    {
        surface = default;
        if (characterSurfaceRuntime == null || !isActive || !physicsStarted) return false;
        const float radius = 0.0001f;
        if (!characterSurfaceRuntime.TryGetVerticalSupport(point, radius, boneName, out var y, out _)) return false;
        surface = new Vector3(point.X, y - radius, point.Z);
        return true;
    }

    private Dictionary<int, BodyMeshGeometry> BuildBodyMeshVolumes(SkeletonAccess skel,
        IReadOnlyDictionary<string, RagdollBoneDef> definitions,
        IReadOnlyDictionary<string, int> activeIndices,
        IReadOnlyDictionary<string, Vector3> bonePositions,
        IReadOnlyDictionary<string, Quaternion> boneRotations)
    {
        var result = new Dictionary<int, BodyMeshGeometry>();
        if (frozenMeshCache == null) return result;
        foreach (var entry in activeIndices)
            if (bonePositions.TryGetValue(entry.Key, out var origin) && boneRotations.TryGetValue(entry.Key, out var rotation) &&
                frozenMeshCache.TryCreateWorld(entry.Key, origin, rotation, out var geometry))
                result[entry.Value] = geometry;
        MeshGeometryStatus = $"Frozen cached mesh: {result.Count} body regions; manual refresh, no live skin/refit";
        return result;
    }

    private Dictionary<int, BodyMeshGeometry> CaptureBodyMeshVolumes(SkeletonAccess skel,
        IReadOnlyDictionary<string, RagdollBoneDef> definitions,
        IReadOnlyDictionary<string, int> activeIndices)
    {
        var empty = new Dictionary<int, BodyMeshGeometry>();
        try
        {
            var source = BuildRenderedGeometrySnapshot(skel);
            foreach (var diagnostic in source.Diagnostics) log.Info($"Ragdoll rendered mesh: {diagnostic}");
            if (source.TriangleIndices.Length == 0)
            {
                MeshGeometryStatus = "Rendered geometry unavailable; explicit legacy fallback (see log)";
                return empty;
            }
            if (source.TriangleIndices.Length / 3 > 100000)
                throw new InvalidOperationException("Rendered body exceeds the 100000 source-face budget; exact geometry was not decimated.");
            var byIndex = new Dictionary<int, RagdollBoneDef>();
            foreach (var entry in activeIndices)
                if (definitions.TryGetValue(entry.Key, out var definition)) byIndex[entry.Value] = definition;
            var omittedRoots = new HashSet<int>();
            var severed = useLocalDismemberBones ? localDismemberBones : config.DismemberPocBones;
            if (severed != null)
                foreach (var name in severed)
                {
                    var root = boneService.ResolveBoneIndex(skel, name);
                    if (root < 0) continue;
                    var keptDescendant = false;
                    foreach (var index in activeIndices.Values)
                        if (IsDescendantOrSelf(skel, index, root)) { keptDescendant = true; break; }
                    if (!keptDescendant) omittedRoots.Add(root);
                }
            int Owner(int bone)
            {
                int steps = 0;
                while (bone >= 0 && bone < skel.ParentCount && steps++ < skel.BoneCount)
                {
                    // A severed limb must not be reassigned to the chest/pelvis just because
                    // its bodies were removed before the visual scale masks were applied.
                    if (omittedRoots.Contains(bone)) return -1;
                    if (byIndex.TryGetValue(bone, out var def) &&
                        (!def.SoftBody || config.RagdollSoftTissueCollision) &&
                        !def.Name.StartsWith("j_sk_", StringComparison.Ordinal) &&
                        !def.Name.StartsWith("j_buki", StringComparison.Ordinal)) return bone;
                    bone = skel.HavokSkeleton->ParentIndices[bone];
                }
                return -1;
            }
            var vertices = new MeshWeightedVertex[source.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var weights = new Dictionary<int, float>();
                foreach (var influence in source.Vertices[i].Influences)
                {
                    var owner = Owner(influence.BoneIndex);
                    if (owner < 0) continue;
                    weights[owner] = weights.GetValueOrDefault(owner) + influence.Weight;
                }
                var mapped = new MeshBodyWeight[weights.Count];
                int j = 0; float sum = 0f;
                foreach (var value in weights.Values) sum += value;
                foreach (var pair in weights)
                    mapped[j++] = new MeshBodyWeight(pair.Key, sum > 0f ? pair.Value / sum : 0f);
                vertices[i] = new MeshWeightedVertex(source.Vertices[i].WorldPosition, mapped);
            }
            var parts = BodyMeshPartitioner.Partition(vertices, source.TriangleIndices);
            MeshGeometryStatus = $"Captured frozen mesh: {parts.Count} body regions, {source.TriangleIndices.Length / 3} source faces";
            log.Info($"Ragdoll mesh volume: {MeshGeometryStatus}");
            return parts;
        }
        catch (Exception ex)
        {
            MeshGeometryStatus = "Rendered geometry failed; explicit legacy fallback (see log)";
            log.Warning(ex, "Ragdoll mesh volume capture failed; preserving legacy geometry.");
            return empty;
        }
    }

    private bool TryCreateBodyMesh(string boneName, BodyMeshGeometry worldGeometry, Vector3 boneOrigin,
        Quaternion bodyRotation, float mass, out TypedIndex shapeIndex, out BodyInertia inertia,
        out Vector3 center, out BodyMeshGeometry localGeometry, out MeshCollisionLodResult? collisionLod)
    {
        shapeIndex = default; inertia = default; center = boneOrigin; localGeometry = null!;
        collisionLod = null;
        if (simulation == null || bufferPool == null) return false;
        Mesh mesh = default;
        bool created = false;
        Buffer<Triangle> triangles = default;
        try
        {
            var inverseRotation = Quaternion.Inverse(bodyRotation);
            var points = new Vector3[worldGeometry.Vertices.Length];
            for (int i = 0; i < points.Length; i++)
                points[i] = Vector3.Transform(worldGeometry.Vertices[i] - boneOrigin, inverseRotation);
            // Body partitions are open at joints. Use surface mass rather than pretending they
            // are watertight solids. Two-sided triangles handle imported winding and reflected mods.
            var indices = worldGeometry.Indices;
            bufferPool.Take<Triangle>(indices.Length / 3 * 2, out triangles);
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = points[indices[i]]; var b = points[indices[i + 1]]; var c = points[indices[i + 2]];
                triangles[i / 3 * 2] = new Triangle(a, b, c);
                triangles[i / 3 * 2 + 1] = new Triangle(a, c, b);
            }
            mesh = new Mesh(triangles, Vector3.One, bufferPool);
            created = true;
            // This overload recenters BEPU's triangles. Apply its returned shift exactly once
            // to our query vertices and to BodyToBone, leaving every anatomical anchor unchanged.
            inertia = mesh.ComputeOpenInertia(mass, out var localCenter);
            var tensor = inertia.InverseInertiaTensor;
            if (!IsFinite(localCenter) || !float.IsFinite(tensor.XX + tensor.YY + tensor.ZZ) ||
                tensor.XX <= 0f || tensor.YY <= 0f || tensor.ZZ <= 0f)
                throw new InvalidOperationException("Degenerate body surface inertia.");
            for (int i = 0; i < points.Length; i++) points[i] -= localCenter;
            localGeometry = new BodyMeshGeometry(points, indices, worldGeometry.SourceBindings);
            if (config.RagdollMeshCollisionLod && frozenMeshCache != null &&
                frozenMeshCache.TryCreateLod(boneName, localGeometry, out var cachedLod))
            {
                var reduced = CreateBodyTriangleMesh(cachedLod.Geometry);
                mesh.Dispose(bufferPool); mesh = reduced; collisionLod = cachedLod;
            }
            center = boneOrigin + Vector3.Transform(localCenter, bodyRotation);
            shapeIndex = simulation.Shapes.Add(mesh);
            return true;
        }
        catch (Exception ex)
        {
            if (created) mesh.Dispose(bufferPool);
            else if (triangles.Allocated) bufferPool.Return(ref triangles);
            log.Warning(ex, "Ragdoll body mesh rejected; preserving legacy shape for this region.");
            return false;
        }
    }

    private Mesh CreateBodyTriangleMesh(BodyMeshGeometry geometry) => TwoSidedMesh.Create(geometry, bufferPool!);
}
