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

    // Other actors' surfaces for weapon contacts only. A failed capture is kept as null so a
    // non-humanoid or unreadable model is not retried on every weapon drop.
    private const int MaxWeaponContactMeshes = 8;
    private readonly Dictionary<nint, (uint Entity, nint Draw, FrozenMeshGeometryCache? Cache)> weaponContactMeshes = new();
    private readonly List<nint> weaponContactMeshOrder = new();

    // Weapon and hair contacts need the cache on the main controller only; body mesh volumes need it everywhere.
    private bool FrozenMeshCacheWanted => config.RagdollMeshVolumes ||
        (config.WeaponBodyMeshContacts || config.RagdollHairPhysics && config.RagdollHairBodyMesh) && RunAnimatedHair;

    // Called from the render owner even while inactive. Only the main controller preloads
    // the local player; other controllers capture their own target on first activation.
    private void UpdateFrozenMeshCache()
    {
        var wanted = FrozenMeshCacheWanted;
        if (observedMeshEnabled != wanted || observedMeshCacheRevision != config.RagdollMeshCacheRevision)
        {
            frozenMeshCache?.Dispose(); frozenMeshCache = null;
            frozenMeshActor = 0; nextMeshCacheAttempt = 0;
            ClearWeaponContactMeshes();
            observedMeshEnabled = wanted;
            observedMeshCacheRevision = config.RagdollMeshCacheRevision;
        }
        if (!wanted) return;
        // Body LOD follows the user's setting; weapon contacts always use the coarse LOD (hair queries the full surface).
        var lodEnabled = config.RagdollMeshCollisionLod || config.WeaponBodyMeshContacts;
        var tolerance = config.RagdollMeshCollisionLod
            ? (float.IsFinite(config.RagdollMeshLodToleranceMm) ? Math.Clamp(config.RagdollMeshLodToleranceMm, .5f, 50f) : 3f) * .001f
            : Configuration.WeaponBodyMeshLodToleranceMm * .001f;
        PollWeaponContactMeshes();
        var localPlayer = Core.Services.ObjectTable.LocalPlayer?.Address ?? 0;
        nint address;
        if (config.RagdollMeshVolumes)
        {
            if (isActive && !TargetObjectAlive()) return;
            address = isActive ? targetCharacterAddress : RunAnimatedHair ? localPlayer : 0;
        }
        else address = localPlayer; // Weapon-only: other actors are captured on demand by GetWeaponContactMesh.
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
            frozenMeshCache = CaptureFrozenMeshCache(address);
            cachedMeshLodTolerance = -1; // Queue LOD once for this capture, including prewarming.
        }
        if (frozenMeshCache == null) return;
        if (cachedMeshLodEnabled != lodEnabled || cachedMeshLodTolerance != tolerance)
        {
            cachedMeshLodEnabled = lodEnabled; cachedMeshLodTolerance = tolerance;
            frozenMeshCache.ConfigureLod(cachedMeshLodEnabled, tolerance);
        }
        if (frozenMeshCache.Poll(out var error) && error != null)
            log.Warning(error, "Frozen collision cache LOD failed; full cached surface retained.");
    }

    /// <summary>Cached bone-local surface of an actor for weapon contacts. Reuses this controller's
    /// own cache when it holds that actor; otherwise captures once (synchronously) per actor.</summary>
    internal FrozenMeshGeometryCache? GetWeaponContactMesh(nint address)
    {
        if (!config.WeaponBodyMeshContacts || address == 0) return null;
        var entity = ((GameObject*)address)->EntityId;
        if (frozenMeshCache != null && frozenMeshActor == address && frozenMeshEntity == entity) return frozenMeshCache;
        // Client-spawned actors can reuse an address and entity id; a new draw object means a new model.
        var draw = (nint)((GameObject*)address)->DrawObject;
        if (weaponContactMeshes.TryGetValue(address, out var known) && known.Entity == entity && known.Draw == draw) return known.Cache;
        known.Cache?.Dispose();
        weaponContactMeshOrder.Remove(address);
        while (weaponContactMeshOrder.Count >= MaxWeaponContactMeshes)
        {
            var oldest = weaponContactMeshOrder[0];
            weaponContactMeshOrder.RemoveAt(0);
            if (weaponContactMeshes.Remove(oldest, out var evicted)) evicted.Cache?.Dispose();
        }
        // Body status text belongs to this controller's own ragdoll, not to another actor's capture.
        var status = MeshGeometryStatus;
        var cache = CaptureFrozenMeshCache(address);
        MeshGeometryStatus = status;
        cache?.ConfigureLod(true, Configuration.WeaponBodyMeshLodToleranceMm * .001f);
        weaponContactMeshes[address] = (entity, draw, cache);
        weaponContactMeshOrder.Add(address);
        return cache;
    }

    private void PollWeaponContactMeshes()
    {
        foreach (var entry in weaponContactMeshes.Values)
            if (entry.Cache != null && entry.Cache.Poll(out var error) && error != null)
                log.Warning(error, "Weapon contact mesh LOD failed; full cached surface retained.");
    }

    private void ClearWeaponContactMeshes()
    {
        foreach (var entry in weaponContactMeshes.Values) entry.Cache?.Dispose();
        weaponContactMeshes.Clear();
        weaponContactMeshOrder.Clear();
    }

    private FrozenMeshGeometryCache? CaptureFrozenMeshCache(nint address)
    {
        var maybeSkeleton = boneService.TryGetSkeleton(address);
        if (maybeSkeleton == null) return null;
        var skel = maybeSkeleton.Value;
        if (skel.CharBase->Skeleton == null) return null;
        var defs = GetBoneDefs();
        var names = new Dictionary<string, int>();
        var byName = new Dictionary<string, RagdollBoneDef>();
        foreach (var def in defs)
        {
            var index = boneService.ResolveBoneIndex(skel, def.Name);
            if (index < 0) continue;
            names[def.Name] = index; byName[def.Name] = def;
        }
        if (!IsHumanoidSkeleton(names)) return null;
        var previousAddress = targetCharacterAddress;
        targetCharacterAddress = address; // Capture resolves this actor's Penumbra collection.
        FrozenMeshGeometryCache? next = null;
        try
        {
            if (config.RagdollSoftTissueModBones && address == Core.Services.ObjectTable.LocalPlayer?.Address)
                AppendModSoftTissueDefs(skel, defs, names, byName);
            var worldParts = CaptureBodyMeshVolumes(skel, byName, names);
            if (worldParts.Count == 0) return null;
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
            var result = next; next = null;
            log.Info($"Frozen collision cache loaded for 0x{address:X}: {result.Count} regions; reused until explicit refresh or actor change.");
            return result;
        }
        finally { next?.Dispose(); targetCharacterAddress = previousAddress; }
    }
}
