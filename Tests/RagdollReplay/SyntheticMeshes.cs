// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using BepuPhysics.Collidables;
using CombatSimulator;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.MeshVolume;

namespace RagdollReplay;

// --synthetic-meshes: a self-check of the mesh paths of format version 3 without a recording that has them. A first
// run replays a recording with mesh volumes, reduced surfaces and mesh colliders switched on and made-up models (a
// box per bone, a box per collider); its own recording then holds those products. A second run replays that
// recording normally and must reproduce it bit for bit. It proves the recorder writes, and the replay hands back,
// every mesh product the controller asks for, and that the mesh-volume simulation (two worker threads) repeats
// exactly. It says nothing about the game's real models.

/// <summary>Turns the mesh paths on, whatever the recording says.</summary>
internal sealed class SyntheticSettings : IRagdollSettingsSource
{
    private readonly IRagdollSettingsSource inner;
    /// <summary>Mesh volumes run the simulation on two worker threads (see the README: not repeatable); false
    /// checks the single-threaded mesh paths (other characters' mesh colliders) on their own.</summary>
    public static bool MeshVolumes = true;
    /// <summary>Hair physics on, resting on the body surface (the local player's recordings only have hair).</summary>
    public static bool Hair;

    public SyntheticSettings(IRagdollSettingsSource inner) => this.inner = inner;

    public bool Refresh(RagdollSettings settings)
    {
        inner.Refresh(settings);
        settings.RagdollMeshVolumes = MeshVolumes;
        settings.RagdollMeshCollisionLod = true;
        settings.RagdollNpcCollisionMode = RagdollNpcCollisionMode.Mesh;
        if (Hair)
        {
            settings.RagdollHairPhysics = true;
            settings.RagdollHairBodyMesh = true;
        }
        return true;
    }
}

/// <summary>Made-up models: a 10 cm box for every "j_" bone of a body surface, the full box as its reduced surface
/// once a fixed step asks for it, and a 0.5 x 1.7 x 0.5 m box for another character's mesh collider.</summary>
internal sealed class SyntheticMeshSource : IMeshSource
{
    private readonly ReplayMeshSource inner;
    private readonly ReplaySkeletons skeletons;
    private readonly HashSet<string> reduced = new(StringComparer.Ordinal);

    public SyntheticMeshSource(ReplayMeshSource inner, ReplaySkeletons skeletons)
    {
        this.inner = inner;
        this.skeletons = skeletons;
    }

    public nint ModelIdentity(nint actor) => inner.ModelIdentity(actor);

    public FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture)
    {
        var skeleton = skeletons.Known(actor);
        if (skeleton == null) return null;
        var cache = new FrozenMeshGeometryCache();
        foreach (var name in skeleton.At(0).Names)
            if (name != null && name.StartsWith("j_", StringComparison.Ordinal))
                cache.AddLocal(name, Box(new Vector3(.05f)));
        return cache.Count == 0 ? null : cache;
    }

    public RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton) => new();
    public void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance) { }
    public bool PollLod(FrozenMeshGeometryCache cache, out Exception? error) { error = null; return false; }

    public bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod)
    {
        lod = null!;
        if (use != CachedLodUse.Step || !reduced.Add(bone)) return false;
        var indices = new int[local.Vertices.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        lod = new MeshCollisionLodResult
        {
            Geometry = new BodyMeshGeometry((Vector3[])local.Vertices.Clone(), (int[])local.Indices.Clone()),
            SourceVertexIndices = indices,
            ValidationSamples = Array.Empty<MeshLodValidationSample>(),
            SourceFaceCount = local.Indices.Length / 3 + 1,
            Status = "synthetic",
        };
        return true;
    }

    public bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local)
        => cache.TryGetSurfaceGeometry(bone, out local);

    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton)
        => inner.StartStructuralFit(actor, skeleton);

    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog) => inner.SoftTissueFits(actor, skeleton, candidates, verboseLog);

    public bool TryCollectActorTriangles(nint actor, ISkeletonPose skeleton, string label, bool verboseLog,
        List<Triangle> triangles, out int loadedModels, out int processedMeshes)
    {
        var box = Box(new Vector3(.25f, .85f, .25f));
        for (var i = 0; i < box.Indices.Length; i += 3)
        {
            var lift = new Vector3(0, .85f, 0);
            triangles.Add(new Triangle(box.Vertices[box.Indices[i]] + lift, box.Vertices[box.Indices[i + 1]] + lift,
                box.Vertices[box.Indices[i + 2]] + lift));
        }
        loadedModels = processedMeshes = 1;
        return true;
    }

    public void PollHairMesh() { }
    public HairMeshBinding? HairMesh => inner.HairMesh;
    // The hair model "changes" once, on the third frame of the recording (the first call is the frame that starts
    // the live hair before the activation), so the live hair also rebuilds inside the recording.
    private int hairRebuildQuestions;
    public bool TakeHairMeshRebuild() => SyntheticSettings.Hair && ++hairRebuildQuestions == 4;
    public WeaponMeshGeometry? BuildWeaponMesh(nint weaponDrawObject, bool verboseLog) => null;

    private static BodyMeshGeometry Box(Vector3 half)
    {
        var v = new Vector3[8];
        for (var i = 0; i < 8; i++)
            v[i] = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
        int[] faces =
        {
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
            2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        };
        return new BodyMeshGeometry(v, faces);
    }
}
