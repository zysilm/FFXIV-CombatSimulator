// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Recording;

namespace CombatSimulator.Animation.Hosting;

// The recorder (stage A0) as decorators around the host interfaces it can observe from outside
// (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §8): each passes the call through and, while the controller is
// recording, writes the same record the controller used to write beside the call (A2a), or the product the
// host answered (A2b: mesh products and the wall-clock stamps of format version 3). The skeleton captures,
// the frames, the steps and the calls stay where they are in the controller: they are observation points
// of the simulation itself, not host answers.

/// <summary>Records world queries: Raycast, GroundTriangles, WaterSurface.</summary>
internal sealed class RecordingWorldQuery : IWorldQuery
{
    private readonly IWorldQuery inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingWorldQuery(IWorldQuery inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        var result = inner.Raycast(site, origin, direction, maxDistance, out hit);
        var w = writer();
        // The live hair's samples are not part of a ragdoll activation.
        if (w == null || site == RaycastSite.AnimatedHair) return result;
        using (w.Begin(RecordType.Raycast))
        {
            w.I32((int)site);
            w.V3(origin);
            w.V3(direction);
            w.F32(maxDistance);
            w.Bool(result);
            w.V3(hit.Point);
            w.V3(hit.Normal);
            w.F32(hit.Distance);
            w.U64(hit.Material);
            w.V3(hit.V1);
            w.V3(hit.V2);
            w.V3(hit.V3);
        }
        return result;
    }

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
    {
        var result = inner.TryCollectGroundTriangles(min, max, triangles, maxTriangles);
        var w = writer();
        if (w == null) return result;
        using (w.Begin(RecordType.GroundTriangles))
        {
            w.V3(min);
            w.V3(max);
            w.I32(maxTriangles);
            w.Bool(result);
            w.V3s(CollectionsMarshal.AsSpan(triangles));
        }
        return result;
    }

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        var result = inner.TryFindWaterSurface(position, searchAbove, searchBelow, out surfaceY);
        var w = writer();
        if (w == null) return result;
        using (w.Begin(RecordType.WaterSurface))
        {
            w.V3(position);
            w.F32(searchAbove);
            w.F32(searchBelow);
            w.Bool(result);
            w.F32(surfaceY);
        }
        return result;
    }
}

/// <summary>Records every number drawn from the unseeded random source (Random records, by site name).</summary>
internal sealed class RecordingRandom : IRagdollRandom
{
    private readonly IRagdollRandom inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingRandom(IRagdollRandom inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public double NextDouble(RandomSite site)
    {
        var value = inner.NextDouble(site);
        Write(site, value);
        return value;
    }

    public int Next(RandomSite site, int maxExclusive)
    {
        var value = inner.Next(site, maxExclusive);
        Write(site, value);
        return value;
    }

    private void Write(RandomSite site, double value)
    {
        var w = writer();
        if (w == null) return;
        using (w.Begin(RecordType.Random))
        {
            w.Str(site.ToString());
            w.F64(value);
        }
    }
}

/// <summary>Records the wall-clock readings a replay needs (Clock records): every time stamp and tick count
/// except the frame's dt (that is in the RenderFrame record). The walking-support query stamp was the only one in
/// format version 2; version 3 adds the others, and the elapsed time the mesh-frame early out reads (site name +
/// <see cref="ElapsedSuffix"/>, value in TimeSpan ticks). An interval between two stamps is not a reading.</summary>
internal sealed class RecordingClock : IRagdollClock
{
    public const string ElapsedSuffix = ".elapsed";

    private readonly IRagdollClock inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingClock(IRagdollClock inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public long Frequency => inner.Frequency;

    public long GetTimestamp(ClockSite site)
    {
        var ticks = inner.GetTimestamp(site);
        if (site != ClockSite.FrameDelta) Write(site.ToString(), ticks);
        return ticks;
    }

    public TimeSpan GetElapsedTime(ClockSite site, long since)
    {
        var elapsed = inner.GetElapsedTime(site, since);
        Write(site + ElapsedSuffix, elapsed.Ticks);
        return elapsed;
    }

    public TimeSpan GetElapsedTime(ClockSite site, long since, long until) => inner.GetElapsedTime(site, since, until);

    public long GetTickCount64(ClockSite site)
    {
        var value = inner.GetTickCount64(site);
        Write(site.ToString(), value);
        return value;
    }

    private void Write(string site, long value)
    {
        var w = writer();
        if (w == null) return;
        using (w.Begin(RecordType.Clock))
        {
            w.Str(site);
            w.I64(value);
        }
    }
}

/// <summary>Kinds of <see cref="RecordType.MeshProduct"/> records (their first value).</summary>
internal static class MeshProductKind
{
    /// <summary>use:i32 (<see cref="BodySurfaceUse"/>), actor:u64, then a cache (<see cref="RecordingMeshSource.WriteCache"/>, no LOD flag).</summary>
    public const string BodySurface = "bodySurface";
    /// <summary>use:i32 (<see cref="CachedLodUse"/>), bone:str, ok:bool; when ok: sourceVertexIndices:I32Array,
    /// indices:I32Array, sourceFaceCount:i32, requestedTolerance:f32, maxVertexDisplacement:f32, maxSampledError:f32,
    /// acceptedCollapses:i32, candidateChecks:i32. The vertices are the given surface's, picked by sourceVertexIndices.</summary>
    public const string Lod = "lod";
    /// <summary>bone:str, ok:bool, vertices:Vector3Array, indices:I32Array (bone-local).</summary>
    public const string CollisionGeometry = "collisionGeometry";
    /// <summary>actor:u64, count:i32, then per fit: bone:i32 (skeleton index), radius:f32, halfLength:f32,
    /// centerModelOffset:Vector3, axisModel:Vector3, samples:i32.</summary>
    public const string SoftTissueFits = "softTissueFits";
    /// <summary>actor:u64, label:str, ok:bool, loadedModels:i32, processedMeshes:i32, triangles:Vector3Array (3 per triangle).
    /// The label (the character's name, as the collider provider lists it) only goes into log lines; it is kept to
    /// show whose mesh it is, and a replay does not compare it.</summary>
    public const string ActorTriangles = "actorTriangles";
    /// <summary>rebuild:bool.</summary>
    public const string HairMeshRebuild = "hairMeshRebuild";
    /// <summary>Written by the controller after the Activate record: the body surfaces it carries over from earlier
    /// activations. frozenActor:u64, frozenEntity:u32, observedEnabled:bool, observedRevision:i32, lodEnabled:bool,
    /// lodTolerance:f32, nextAttempt:i64, frozen cache (no LOD flag), weaponContactCount:i32, then per entry in
    /// eviction order: actor:u64, entity:u32, model:u64, cache (no LOD flag).</summary>
    public const string Carried = "carried";
}

/// <summary>
/// Records what the mesh source hands the controller, as finished products (<see cref="RecordType.MeshProduct"/>,
/// format version 3): body surfaces, the reduced and collision surfaces taken from them, soft-tissue fits, other
/// characters' triangle soups, and the hair rebuild flag. Calls the mesh source receives while it serves another
/// one (the capture a body surface runs) are not recorded: a replay hands back the outer product and never asks them.
/// </summary>
internal sealed class RecordingMeshSource : IMeshSource
{
    private readonly IMeshSource inner;
    private readonly Func<RagdollRecordingWriter?> writer;
    private readonly Func<Dictionary<object, int>> cacheIds;
    private int depth;

    public RecordingMeshSource(IMeshSource inner, Func<RagdollRecordingWriter?> writer, Func<Dictionary<object, int>> cacheIds)
    {
        this.inner = inner;
        this.writer = writer;
        this.cacheIds = cacheIds;
    }

    /// <summary>The writer, for an outermost call only.</summary>
    private RagdollRecordingWriter? Writer => depth == 0 ? writer() : null;

    public nint ModelIdentity(nint actor) => inner.ModelIdentity(actor);

    public FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture)
    {
        FrozenMeshGeometryCache? cache;
        depth++;
        try { cache = inner.ProvideBodySurface(actor, use, capture); }
        finally { depth--; }
        var w = Writer;
        if (w == null) return cache;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.BodySurface);
            w.I32((int)use);
            w.U64((ulong)actor);
            WriteCache(w, cache, cacheIds(), false);
        }
        return cache;
    }

    public RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton)
        => inner.CaptureRenderedGeometry(actor, skeleton);

    public void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance) => inner.ConfigureLod(cache, enabled, tolerance);

    public bool PollLod(FrozenMeshGeometryCache cache, out Exception? error) => inner.PollLod(cache, out error);

    public bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod)
    {
        var ok = inner.TryCreateLod(cache, use, bone, local, out lod);
        var w = Writer;
        if (w == null) return ok;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.Lod);
            w.I32((int)use);
            w.Str(bone);
            w.Bool(ok);
            if (ok)
            {
                w.Ints(lod.SourceVertexIndices);
                w.Ints(lod.Geometry.Indices);
                w.I32(lod.SourceFaceCount);
                w.F32(lod.RequestedTolerance);
                w.F32(lod.MaximumOriginalVertexDisplacement);
                w.F32(lod.MaximumSampledSurfaceError);
                w.I32(lod.AcceptedCollapses);
                w.I32(lod.CandidateChecks);
            }
        }
        return ok;
    }

    public bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local)
    {
        var ok = inner.TryGetCollisionGeometry(cache, bone, out local);
        var w = Writer;
        if (w == null) return ok;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.CollisionGeometry);
            w.Str(bone);
            w.Bool(ok);
            w.V3s(ok ? local.Vertices : default);
            w.Ints(ok ? local.Indices : default);
        }
        return ok;
    }

    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton)
        => inner.StartStructuralFit(actor, skeleton);

    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog)
    {
        var fits = inner.SoftTissueFits(actor, skeleton, candidates, verboseLog);
        var w = Writer;
        if (w == null) return fits;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.SoftTissueFits);
            w.U64((ulong)actor);
            w.I32(fits.Count);
            foreach (var entry in fits)
            {
                w.I32(entry.Key);
                w.F32(entry.Value.Radius);
                w.F32(entry.Value.HalfLength);
                w.V3(entry.Value.CenterModelOffset);
                w.V3(entry.Value.AxisModel);
                w.I32(entry.Value.SampleCount);
            }
        }
        return fits;
    }

    public bool TryCollectActorTriangles(nint actor, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes)
    {
        var ok = inner.TryCollectActorTriangles(actor, skeleton, label, verboseLog, triangles, out loadedModels, out processedMeshes);
        var w = Writer;
        if (w == null) return ok;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.ActorTriangles);
            w.U64((ulong)actor);
            w.Str(label);
            w.Bool(ok);
            w.I32(loadedModels);
            w.I32(processedMeshes);
            w.V3s(MemoryMarshal.Cast<Triangle, Vector3>(CollectionsMarshal.AsSpan(triangles)));
        }
        return ok;
    }

    public void PollHairMesh() => inner.PollHairMesh();

    public HairMeshBinding? HairMesh => inner.HairMesh;

    public bool TakeHairMeshRebuild()
    {
        var rebuild = inner.TakeHairMeshRebuild();
        var w = Writer;
        if (w == null) return rebuild;
        using (w.Begin(RecordType.MeshProduct))
        {
            w.Str(MeshProductKind.HairMeshRebuild);
            w.Bool(rebuild);
        }
        return rebuild;
    }

    public WeaponMeshGeometry? BuildWeaponMesh(nint weaponDrawObject, bool verboseLog) => inner.BuildWeaponMesh(weaponDrawObject, verboseLog);

    /// <summary>
    /// A cached body surface: id:i32 (-1 for none; per recording, the same object keeps its id), count:i32 (0 when the
    /// id was written before), then per region: name:str, vertices:Vector3Array, indices:I32Array (the full bone-local
    /// surface), and with <paramref name="lodFlag"/> hasLod:bool (whether a reduced surface was ready).
    /// </summary>
    internal static void WriteCache(RagdollRecordingWriter w, FrozenMeshGeometryCache? cache, Dictionary<object, int> ids, bool lodFlag)
    {
        if (cache == null)
        {
            w.I32(-1);
            w.I32(0);
            return;
        }
        var known = ids.TryGetValue(cache, out var id);
        if (!known) ids[cache] = id = ids.Count + 1;
        w.I32(id);
        if (known)
        {
            // Already written in this file under the same id.
            w.I32(0);
            return;
        }
        w.I32(cache.Count);
        foreach (var name in cache.Names)
        {
            w.Str(name);
            if (!cache.TryGetSurfaceGeometry(name, out var surface))
            {
                w.V3s(default);
                w.Ints(default);
                if (lodFlag) w.Bool(false);
                continue;
            }
            w.V3s(surface.Vertices);
            w.Ints(surface.Indices);
            if (lodFlag) w.Bool(cache.TryGetCollisionGeometry(name, out var collision) && !ReferenceEquals(collision, surface));
        }
    }
}
