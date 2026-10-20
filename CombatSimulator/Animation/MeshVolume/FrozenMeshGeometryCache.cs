// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Managed bone-local geometry retained across simulation lifetimes. No native ownership.</summary>
public sealed class FrozenMeshGeometryCache : IDisposable
{
    private sealed record Part(string Name, BodyMeshGeometry Geometry)
    {
        public MeshCollisionLodResult? Lod;
    }
    private readonly List<Part> parts = new();
    private readonly Dictionary<string, Part> byName = new(StringComparer.Ordinal);
    private MeshLodBuildQueue? builds;
    public int Count => parts.Count;
    public IEnumerable<string> Names => byName.Keys;
    public Task WhenIdle => builds?.WhenIdle ?? Task.CompletedTask;

    public void Add(string name, BodyMeshGeometry world, Vector3 boneOrigin, Quaternion boneRotation)
    {
        var inverse = Quaternion.Inverse(boneRotation);
        var vertices = new Vector3[world.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = Vector3.Transform(world.Vertices[i] - boneOrigin, inverse);
        var part = new Part(name, new(vertices, (int[])world.Indices.Clone()));
        byName.Add(name, part);
        parts.Add(part);
    }

    public void ConfigureLod(bool enabled, float tolerance)
    {
        builds?.Dispose();
        builds = enabled ? new() : null;
        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].Lod = null;
            builds?.Enqueue(i, parts[i].Geometry, tolerance);
        }
    }

    public bool Poll(out Exception? error)
    {
        error = null;
        if (builds == null || !builds.TryDequeue(out var result)) return false;
        error = result.Error;
        if (result.Lod is { Reduced: true } lod) parts[result.BodyId].Lod = lod;
        return true;
    }

    public bool TryCreateWorld(string name, Vector3 origin, Quaternion rotation, out BodyMeshGeometry world)
    {
        world = null!;
        if (!byName.TryGetValue(name, out var part)) return false;
        var vertices = new Vector3[part.Geometry.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = origin + Vector3.Transform(part.Geometry.Vertices[i], rotation);
        world = new(vertices, (int[])part.Geometry.Indices.Clone());
        return true;
    }

    /// <summary>Bone-local collision surface: the cached LOD once built, otherwise the full surface.</summary>
    public bool TryGetCollisionGeometry(string name, out BodyMeshGeometry local)
    {
        local = null!;
        if (!byName.TryGetValue(name, out var part)) return false;
        local = part.Lod?.Geometry ?? part.Geometry;
        return true;
    }

    /// <summary>Full-detail bone-local surface, for queries that do not pay physics contact cost.</summary>
    public bool TryGetSurfaceGeometry(string name, out BodyMeshGeometry local)
    {
        local = null!;
        if (!byName.TryGetValue(name, out var part)) return false;
        local = part.Geometry;
        return true;
    }

    /// <summary>Rebase cached topology onto this simulation's rigid COM frame. No simplification/refit.</summary>
    public bool TryCreateLod(string name, BodyMeshGeometry local, out MeshCollisionLodResult result)
    {
        result = null!;
        if (!byName.TryGetValue(name, out var part) || part.Lod is not { } lod) return false;
        var vertices = new Vector3[lod.SourceVertexIndices.Length];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = local.Vertices[lod.SourceVertexIndices[i]];
        result = new()
        {
            Geometry = new(vertices, (int[])lod.Geometry.Indices.Clone()),
            SourceVertexIndices = (int[])lod.SourceVertexIndices.Clone(),
            ValidationSamples = (MeshLodValidationSample[])lod.ValidationSamples.Clone(),
            SourceFaceCount = lod.SourceFaceCount, RequestedTolerance = lod.RequestedTolerance,
            MaximumOriginalVertexDisplacement = lod.MaximumOriginalVertexDisplacement,
            MaximumSampledSurfaceError = lod.MaximumSampledSurfaceError,
            AcceptedCollapses = lod.AcceptedCollapses, CandidateChecks = lod.CandidateChecks,
            BuildMilliseconds = lod.BuildMilliseconds, Status = "Frozen cached collision topology"
        };
        return true;
    }

    public void Dispose() { builds?.Dispose(); builds = null; parts.Clear(); byName.Clear(); }
}
