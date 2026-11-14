// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Core;

namespace CombatSimulator.Animation.Hosting;

/// <summary>
/// The FFXIV side of <see cref="IMeshSource"/> (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §3.2, stage A2b): every read of
/// a model file or of the game's model memory the ragdoll used to make itself, moved here unchanged — same reads,
/// same order within a call, same background tasks. One per controller (it keeps the hair model it follows and the
/// stamps of the last rendered capture).
/// </summary>
internal sealed unsafe partial class FfxivMeshSource : IMeshSource
{
    private readonly BoneTransformService boneService;
    private readonly IRagdollActor actors;
    private readonly IRagdollLog log;
    private readonly IRagdollScheduler scheduler;

    // The controller's RagdollVerboseLog for the call being served (calls come from the render hook and from
    // framework-tick handlers; each sets it on entry).
    [ThreadStatic] private static bool verboseLog;

    public FfxivMeshSource(BoneTransformService boneService, IRagdollActor actors, IRagdollLog log, IRagdollScheduler scheduler)
    {
        this.boneService = boneService;
        this.actors = actors;
        this.log = log;
        this.scheduler = scheduler;
    }

    public nint ModelIdentity(nint actor) => (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor)->DrawObject;

    public FrozenMeshGeometryCache? ProvideBodySurface(nint actor, BodySurfaceUse use, Func<FrozenMeshGeometryCache?> capture)
        => capture();

    public RenderedGeometrySnapshot CaptureRenderedGeometry(nint actor, ISkeletonPose skeleton)
        => BuildRenderedGeometrySnapshot(actor, FfxivSkeletonPose.Of(skeleton));

    public void ConfigureLod(FrozenMeshGeometryCache cache, bool enabled, float tolerance) => cache.ConfigureLod(enabled, tolerance);

    public bool PollLod(FrozenMeshGeometryCache cache, out Exception? error) => cache.Poll(out error);

    public bool TryCreateLod(FrozenMeshGeometryCache cache, CachedLodUse use, string bone, BodyMeshGeometry local,
        out MeshCollisionLodResult lod) => cache.TryCreateLod(bone, local, out lod);

    public bool TryGetCollisionGeometry(FrozenMeshGeometryCache cache, string bone, out BodyMeshGeometry local)
        => cache.TryGetCollisionGeometry(bone, out local);

    public Task<RagdollController.StructuralFitResult>? StartStructuralFit(nint actor, ISkeletonPose skeleton)
        => StartStructuralMeshFit(actor, FfxivSkeletonPose.Of(skeleton));

    public Dictionary<int, RagdollController.SoftTissueMeshFit> SoftTissueFits(nint actor, ISkeletonPose skeleton,
        HashSet<int> candidates, bool verboseLog)
    {
        FfxivMeshSource.verboseLog = verboseLog;
        return BuildSoftTissueMeshFits(FfxivSkeletonPose.Of(skeleton), candidates);
    }

    // ------------------------------------------------------------------ hair

    // The local player's hair model (slot 10) as last seen, and its samples, loaded on a worker (moved here from
    // RagdollController.HairRig.cs in stage A2b, unchanged).
    private HairMeshBinding? hairMesh;
    private Task<HairMeshBinding?>? hairMeshLoad;
    private nint hairMeshResource;
    private uint hairMeshAttributes;
    private uint hairMeshShapeMask;
    private nint hairMeshResourceData;
    private bool hairMeshReadyRebuild;

    public HairMeshBinding? HairMesh => hairMesh;

    public bool TakeHairMeshRebuild()
    {
        var rebuild = hairMeshReadyRebuild;
        hairMeshReadyRebuild = false;
        return rebuild;
    }

    public void PollHairMesh()
    {
        var player = Services.ObjectTable.LocalPlayer; if (player == null) return;
        var access = boneService.TryGetSkeleton(player.Address); if (access == null) return;
        var cb = access.Value.CharBase;
        if (cb->SlotCount <= 10 || cb->Models == null || cb->Models[10] == null || cb->Models[10]->ModelResourceHandle == null) return;
        var model = cb->Models[10]; var resource = model->ModelResourceHandle;
        if (hairMeshLoad != null)
        {
            if (!hairMeshLoad.IsCompleted) return;
            var result = hairMeshLoad.GetAwaiter().GetResult(); hairMeshLoad = null;
            if (hairMeshResource == (nint)resource && hairMeshResourceData == (nint)resource->ModelData &&
                hairMeshAttributes == model->EnabledAttributeIndexMask && hairMeshShapeMask == model->EnabledShapeKeyIndexMask)
            {
                hairMesh = result; hairMeshReadyRebuild = true;
                log.Info($"Hair mesh binding: visibleVertices={result?.VisibleVertices ?? 0}, weightedRegions={result?.Samples.Count ?? 0}; native vertex deformation pending, guide envelopes only.");
            }
        }
        if (hairMeshResource == (nint)resource && hairMeshResourceData == (nint)resource->ModelData &&
            hairMeshAttributes == model->EnabledAttributeIndexMask && hairMeshShapeMask == model->EnabledShapeKeyIndexMask) return;
        hairMeshResource = (nint)resource; hairMeshAttributes = model->EnabledAttributeIndexMask;
        hairMeshResourceData = (nint)resource->ModelData; hairMeshShapeMask = model->EnabledShapeKeyIndexMask;
        hairMesh = null; hairMeshReadyRebuild = true;
        // Active shape replacements require a separate index snapshot; omit
        // mesh fitting rather than reading disabled replacement geometry.
        if (model->EnabledShapeKeyIndexMask != 0) return;
        var fileName = resource->FileName;
        if (fileName.Length == 0 || fileName.Length > 4096) return;
        string path = Encoding.UTF8.GetString(fileName.AsSpan());
        if (path.StartsWith('|')) { int end = path.IndexOf('|', 1); if (end >= 0) path = path[(end + 1)..]; }
        var gameData = Services.DataManager.GameData; uint attributes = hairMeshAttributes;
        hairMeshLoad = scheduler.Run<HairMeshBinding?>(() =>
        {
            try
            {
                byte[]? data;
                if (Path.IsPathRooted(path))
                {
                    var info = new FileInfo(path); if (!info.Exists || info.Length > 64 * 1024 * 1024) return null;
                    data = File.ReadAllBytes(path);
                }
                else data = gameData.GetFile(path)?.Data;
                return data == null || data.Length > 64 * 1024 * 1024 ? null : HairMeshBinding.Decode(data, attributes);
            }
            catch { return null; }
        });
    }

    // ------------------------------------------------------------------ helpers shared by the model readers

    // Game-skeleton forms of the controller's skeleton helpers.
    private static int ResolveBoneIndex(SkeletonAccess skel, string boneName)
        => RagdollController.ResolveBoneIndex(new FfxivSkeletonPose(0, skel), boneName);

    private static Vector3 GetSkeletonScale(SkeletonAccess ns)
        => ns.CharBase == null ? Vector3.One : RagdollController.GetSkeletonScale(new FfxivSkeletonPose(0, ns));

    private static Matrix4x4 QsToMatrix(BoneQs transform) => RagdollController.QsToMatrix(transform);

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
