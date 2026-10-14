// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.MeshVolume;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private FrozenMeshGeometryCache? frozenMeshCache;
    private nint frozenMeshActor;
    private uint frozenMeshEntity;
    private int observedMeshCacheRevision = -1, activeMeshCacheRevision;
    private bool observedMeshEnabled, cachedMeshLodEnabled;
    private float cachedMeshLodTolerance;
    private long nextMeshCacheAttempt;

    // Called from the render owner even while inactive. Only the main controller preloads
    // the local player; other controllers capture their own target on first activation.
    private void UpdateFrozenMeshCache()
    {
        if (observedMeshEnabled != config.RagdollMeshVolumes || observedMeshCacheRevision != config.RagdollMeshCacheRevision)
        {
            frozenMeshCache?.Dispose(); frozenMeshCache = null;
            frozenMeshActor = 0; nextMeshCacheAttempt = 0;
            observedMeshEnabled = config.RagdollMeshVolumes;
            observedMeshCacheRevision = config.RagdollMeshCacheRevision;
        }
        if (!config.RagdollMeshVolumes) return;
        if (isActive && !TargetObjectAlive()) return;
        var address = isActive ? targetCharacterAddress : RunAnimatedHair ? Core.Services.ObjectTable.LocalPlayer?.Address ?? 0 : 0;
        if (address == 0) return;
        var entity = ((GameObject*)address)->EntityId;
        if (frozenMeshActor != address || frozenMeshEntity != entity)
        {
            frozenMeshCache?.Dispose(); frozenMeshCache = null;
            frozenMeshActor = address; frozenMeshEntity = entity; nextMeshCacheAttempt = 0;
        }
        if (frozenMeshCache == null && Environment.TickCount64 >= nextMeshCacheAttempt)
        {
            nextMeshCacheAttempt = Environment.TickCount64 + 2000;
            CaptureFrozenMeshCache(address);
        }
        if (frozenMeshCache == null) return;
        var tolerance = (float.IsFinite(config.RagdollMeshLodToleranceMm) ? Math.Clamp(config.RagdollMeshLodToleranceMm, .5f, 50f) : 3f) * .001f;
        if (cachedMeshLodEnabled != config.RagdollMeshCollisionLod || cachedMeshLodTolerance != tolerance)
        {
            cachedMeshLodEnabled = config.RagdollMeshCollisionLod; cachedMeshLodTolerance = tolerance;
            frozenMeshCache.ConfigureLod(cachedMeshLodEnabled, tolerance);
        }
        if (frozenMeshCache.Poll(out var error) && error != null)
            log.Warning(error, "Frozen collision cache LOD failed; full cached surface retained.");
    }

    private void CaptureFrozenMeshCache(nint address)
    {
        var maybeSkeleton = boneService.TryGetSkeleton(address);
        if (maybeSkeleton == null) return;
        var skel = maybeSkeleton.Value;
        if (skel.CharBase->Skeleton == null) return;
        var defs = GetBoneDefs();
        var names = new Dictionary<string, int>();
        var byName = new Dictionary<string, RagdollBoneDef>();
        foreach (var def in defs)
        {
            var index = boneService.ResolveBoneIndex(skel, def.Name);
            if (index < 0) continue;
            names[def.Name] = index; byName[def.Name] = def;
        }
        if (!IsHumanoidSkeleton(names)) return;
        var previousAddress = targetCharacterAddress;
        targetCharacterAddress = address; // Capture resolves this actor's Penumbra collection.
        FrozenMeshGeometryCache? next = null;
        try
        {
            if (config.RagdollSoftTissueModBones && address == Core.Services.ObjectTable.LocalPlayer?.Address)
                AppendModSoftTissueDefs(skel, defs, names, byName);
            var worldParts = CaptureBodyMeshVolumes(skel, byName, names);
            if (worldParts.Count == 0) return;
            next = new();
            var root = skel.CharBase->Skeleton->Transform;
            var rootPosition = new Vector3(root.Position.X, root.Position.Y, root.Position.Z);
            var rootRotation = Quaternion.Normalize(new(root.Rotation.X, root.Rotation.Y, root.Rotation.Z, root.Rotation.W));
            var scale = GetCharacterScale(address, skel);
            foreach (var entry in names)
            {
                if (!worldParts.TryGetValue(entry.Value, out var geometry)) continue;
                ref var pose = ref skel.Pose->ModelPose.Data[entry.Value];
                var origin = rootPosition + Vector3.Transform(new Vector3(pose.Translation.X, pose.Translation.Y, pose.Translation.Z) * scale, rootRotation);
                var rotation = Quaternion.Normalize(rootRotation * new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W));
                next.Add(entry.Key, geometry, origin, rotation);
            }
            frozenMeshCache = next; next = null;
            cachedMeshLodTolerance = -1; // Queue LOD once for this capture, including prewarming.
            log.Info($"Frozen collision cache loaded: {frozenMeshCache.Count} regions; reused until explicit refresh or actor change.");
        }
        finally { next?.Dispose(); targetCharacterAddress = previousAddress; }
    }
}
