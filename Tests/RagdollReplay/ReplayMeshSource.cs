// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BepuPhysics.Collidables;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Recording;

namespace RagdollReplay;

/// <summary>
/// The models, as the recording says the game's mesh source handed them over (stage A2b). Format version 3 records
/// every product (MeshProduct records); each question takes the product its own call produced, in order. A version 2
/// recording has only what the controller recorded where it used a product: the hull fits (StructuralFits), the
/// soft-tissue fits (read back from the bone definitions they became) and the hair samples (HairMesh); its body
/// surfaces, reduced surfaces and triangle soups cannot be answered, and the driver refuses such files.
/// </summary>
internal sealed class ReplayMeshSource : IMeshSource
{
    private readonly ReplaySource source;
    private readonly bool products;
    // Recorded cache id -> the surface handed out for it, so one recorded surface stays one object.
    private readonly Dictionary<int, FrozenMeshGeometryCache> caches = new();
    private readonly Dictionary<nint, nint> models = new();

    public ReplayMeshSource(ReplaySource source)
    {
        this.source = source;
        products = source.HasMeshProducts;
    }

    /// <summary>The body surfaces the controller carried into this activation (MeshProduct "carried"), applied to
    /// <paramref name="state"/>; false for a version 2 recording, which does not have them.</summary>
    public bool ReadCarried(RagdollController.ReplayCarriedState state)
    {
        if (!products) return false;
        var r = source.Take(RecordType.MeshProduct, x => x[0].AsString == MeshProductKind.Carried);
        if (r == null) throw new ReplayUnsupportedException("no carried body surfaces after the Activate record");
        state.HasBodySurfaces = true;
        state.FrozenMeshActor = (nint)r[1].AsULong;
        state.FrozenMeshEntity = r[2].AsUInt;
        state.ObservedMeshEnabled = r[3].AsBool;
        state.ObservedMeshCacheRevision = r[4].AsInt;
        state.CachedMeshLodEnabled = r[5].AsBool;
        state.CachedMeshLodTolerance = r[6].AsFloat;
        state.NextMeshCacheAttempt = r[7].AsLong;
        var at = 8;
        state.FrozenMeshCache = DecodeCache(r, ref at, false);
        var count = r[at++].AsInt;
        for (var i = 0; i < count; i++)
        {
            var actor = (nint)r[at].AsULong;
            var entity = r[at + 1].AsUInt;
            var draw = (nint)r[at + 2].AsULong;
            at += 3;
            models[actor] = draw;
            state.WeaponContactMeshes.Add((actor, entity, draw, DecodeCache(r, ref at, false)));
        }
        return true;
    }

    public nint ModelIdentity(nint actor) => models.TryGetValue(actor, out var model) ? model : actor;

    public FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture)
    {
        if (!products) return null;
        var r = Take(MeshProductKind.BodySurface, x => x[1].AsInt == (int)use && (nint)x[2].AsULong == actor,
            $"body surface of 0x{actor:X} ({use})");
        if (r == null) return null;
        var at = 3;
        return DecodeCache(r, ref at, false);
    }

    public RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton)
    {
        source.Diverged("rendered geometry captured (a replay hands back body surfaces, it never captures them)");
        return new RenderedGeometrySnapshot();
    }

    public void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance) { }

    public bool PollLod(FrozenMeshGeometryCache cache, out Exception? error)
    {
        error = null;
        return false;
    }

    public bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod)
    {
        lod = null!;
        if (!products) return false;
        var r = Take(MeshProductKind.Lod, null, $"reduced surface of {bone} ({use})");
        if (r == null) return false;
        if (r[1].AsInt != (int)use || r[2].AsString != bone)
            source.Diverged($"reduced surface asked for {bone} ({use}), recorded for {r[2]} ({(CachedLodUse)r[1].AsInt})");
        if (!r[3].AsBool) return false;
        var sourceVertices = Ints(r[4]);
        var vertices = new Vector3[sourceVertices.Length];
        for (var i = 0; i < vertices.Length; i++) vertices[i] = local.Vertices[sourceVertices[i]];
        lod = new MeshCollisionLodResult
        {
            Geometry = new BodyMeshGeometry(vertices, Ints(r[5])),
            SourceVertexIndices = sourceVertices,
            ValidationSamples = Array.Empty<MeshLodValidationSample>(),
            SourceFaceCount = r[6].AsInt,
            RequestedTolerance = r[7].AsFloat,
            MaximumOriginalVertexDisplacement = r[8].AsFloat,
            MaximumSampledSurfaceError = r[9].AsFloat,
            AcceptedCollapses = r[10].AsInt,
            CandidateChecks = r[11].AsInt,
            Status = "Frozen cached collision topology",
        };
        return true;
    }

    public bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local)
    {
        local = null!;
        if (!products) return cache.TryGetSurfaceGeometry(bone, out local);
        var r = Take(MeshProductKind.CollisionGeometry, null, $"collision surface of {bone}");
        if (r == null) return false;
        if (r[1].AsString != bone) source.Diverged($"collision surface asked for {bone}, recorded for {r[1]}");
        if (!r[2].AsBool) return false;
        local = new BodyMeshGeometry(Rec.Vectors(r[3]), Ints(r[4]));
        return true;
    }

    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton)
    {
        var r = source.Peek(RecordType.StructuralFits).Record;
        if (r == null) return null;
        switch (r[0].AsInt)
        {
            case 2: return new TaskCompletionSource<RagdollController.StructuralFitResult>().Task; // timed out
            case 3: return Task.FromResult(new RagdollController.StructuralFitResult(DecodeFits(r), "replayed"));
            case 4: return Task.FromException<RagdollController.StructuralFitResult>(new InvalidOperationException("the recorded fit failed"));
            default: return null;
        }
    }

    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog)
    {
        var fits = new Dictionary<int, RagdollController.SoftTissueMeshFit>();
        if (products)
        {
            var p = Take(MeshProductKind.SoftTissueFits, x => (nint)x[1].AsULong == actor, $"soft-tissue fits of 0x{actor:X}");
            if (p == null) return fits;
            var n = p[2].AsInt;
            for (int i = 0, at = 3; i < n; i++, at += 6)
                fits[p[at].AsInt] = new RagdollController.SoftTissueMeshFit(p[at + 1].AsFloat, p[at + 2].AsFloat,
                    p[at + 3].AsVector3, p[at + 4].AsVector3, p[at + 5].AsInt);
            return fits;
        }
        // Version 2: read back from the bone definitions they became (each fit field is copied into one definition
        // field: radius, half length, centre offset, axis).
        var r = source.Peek(RecordType.BoneDefs).Record;
        if (r == null) { source.Diverged("soft-tissue fits asked, no BoneDefs record"); return fits; }
        var count = r[1].AsInt;
        var index = 2;
        for (var d = 0; d < count; d++)
        {
            var pairs = r[index++].Count;
            var fields = new Dictionary<string, RecValue>(StringComparer.Ordinal);
            for (var q = 0; q < pairs; q += 2) fields[r[index + q].AsString!] = r[index + q + 1];
            index += pairs;
            var bone = r[index++].AsInt;
            if (!fields.TryGetValue("HasMeshFit", out var has) || !has.AsBool || !candidates.Contains(bone)) continue;
            fits[bone] = new RagdollController.SoftTissueMeshFit(fields["CapsuleRadius"].AsFloat, fields["CapsuleHalfLength"].AsFloat,
                fields["MeshFitCenterModelOffset"].AsVector3, fields["MeshFitAxisModel"].AsVector3, 0);
        }
        return fits;
    }

    public bool TryCollectActorTriangles(nint actor, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes)
    {
        loadedModels = processedMeshes = 0;
        if (!products) { source.Diverged($"mesh collider of 0x{actor:X} asked (not in a version 2 recording)"); return false; }
        var r = Take(MeshProductKind.ActorTriangles, x => (nint)x[1].AsULong == actor, $"triangles of 0x{actor:X}");
        if (r == null) return false;
        loadedModels = r[4].AsInt;
        processedMeshes = r[5].AsInt;
        var corners = Rec.Vectors(r[6]);
        for (var i = 0; i + 2 < corners.Length; i += 3) triangles.Add(new Triangle(corners[i], corners[i + 1], corners[i + 2]));
        return r[3].AsBool;
    }

    // The hair model is followed on the framework tick, which a replay does not run: the samples come from the
    // HairMesh record of the rig build that used them (in the frame being replayed: a build the game did not make
    // must not take the record of a later one), and the rebuild flag from its MeshProduct record.
    public void PollHairMesh() { }

    public HairMeshBinding? HairMesh
    {
        get
        {
            var r = source.Take(RecordType.HairMesh, null, true);
            if (r == null) { source.Diverged("hair samples asked, no HairMesh record"); return null; }
            if (!r[0].AsBool) return null;
            var regions = r[3].AsInt;
            var samples = new Dictionary<string, HairMeshSample[]>(StringComparer.Ordinal);
            var at = 4;
            for (var g = 0; g < regions; g++)
            {
                var bone = r[at++].AsString!;
                var positions = Rec.Vectors(r[at++]);
                var counts = Ints(r[at++]);
                var total = r[at++].Count;
                var names = new string[total];
                for (var i = 0; i < total; i++) names[i] = r[at++].AsString!;
                var weights = MemoryMarshal.Cast<byte, float>(r[at++].Data).ToArray();
                var region = new HairMeshSample[positions.Length];
                var k = 0;
                for (var i = 0; i < positions.Length; i++)
                {
                    var influences = new HairMeshInfluence[counts[i]];
                    for (var j = 0; j < influences.Length; j++, k++) influences[j] = new HairMeshInfluence(names[k], weights[k]);
                    region[i] = new HairMeshSample(positions[i], influences);
                }
                samples[bone] = region;
            }
            return new HairMeshBinding(samples, r[2].AsInt);
        }
    }

    public bool TakeHairMeshRebuild()
    {
        if (!products) return false;
        var r = Take(MeshProductKind.HairMeshRebuild, null, "hair rebuild flag");
        return r != null && r[1].AsBool;
    }

    public WeaponMeshGeometry? BuildWeaponMesh(nint weaponDrawObject, bool verboseLog) => null;

    private RagdollRecord? Take(string kind, Func<RagdollRecord, bool>? match, string what)
    {
        var r = source.Take(RecordType.MeshProduct, x => x[0].AsString == kind && (match == null || match(x)));
        if (r == null) source.Diverged($"{what} asked, none recorded");
        return r;
    }

    /// <summary>A cache written by RecordingMeshSource.WriteCache, from <paramref name="at"/>; one object per recorded id.</summary>
    private FrozenMeshGeometryCache? DecodeCache(RagdollRecord r, ref int at, bool lodFlag)
    {
        var id = r[at++].AsInt;
        var count = r[at++].AsInt;
        if (id < 0) return null;
        if (caches.TryGetValue(id, out var known))
        {
            if (count != 0) source.Diverged($"body surface {id} written twice");
            return known;
        }
        var cache = new FrozenMeshGeometryCache();
        for (var i = 0; i < count; i++)
        {
            var name = r[at++].AsString!;
            var vertices = Rec.Vectors(r[at++]);
            var indices = Ints(r[at++]);
            if (lodFlag) at++;
            if (indices.Length > 0) cache.AddLocal(name, new BodyMeshGeometry(vertices, indices));
        }
        caches[id] = cache;
        return cache;
    }

    private static int[] Ints(RecValue value) => MemoryMarshal.Cast<byte, int>(value.Data).ToArray();

    private static Dictionary<string, StructuralMeshFitter.Fit> DecodeFits(RagdollRecord r)
    {
        var fits = new Dictionary<string, StructuralMeshFitter.Fit>(StringComparer.Ordinal);
        var count = r[1].AsInt;
        var at = 2;
        for (var i = 0; i < count; i++)
        {
            var bone = r[at++].AsString!;
            var pairs = r[at++].Count;
            var fit = new StructuralMeshFitter.Fit();
            for (var p = 0; p < pairs; p += 2)
            {
                var name = r[at++].AsString!;
                var value = r[at++];
                var field = typeof(StructuralMeshFitter.Fit).GetField(name, BindingFlags.Public | BindingFlags.Instance)
                            ?? throw new ReplayUnsupportedException($"fit field {name} unknown");
                field.SetValue(fit, DecodeField(field.FieldType, value));
            }
            fits[bone] = fit;
        }
        return fits;
    }

    private static object? DecodeField(Type type, RecValue value)
    {
        if (value.IsNull) return null;
        if (type == typeof(float)) return value.AsFloat;
        if (type == typeof(int)) return value.AsInt;
        if (type == typeof(bool)) return value.AsBool;
        if (type == typeof(Vector3)) return value.AsVector3;
        if (type == typeof(Quaternion)) return value.AsQuaternion;
        if (type == typeof(Vector3[])) return Rec.Vectors(value);
        throw new ReplayUnsupportedException($"fit field type {type.Name} cannot be read back");
    }
}
