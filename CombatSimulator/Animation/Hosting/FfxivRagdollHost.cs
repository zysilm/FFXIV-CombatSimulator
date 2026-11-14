// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Recording;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using HkaPose = FFXIVClientStructs.Havok.Animation.Rig.hkaPose;

namespace CombatSimulator.Animation.Hosting;

// The FFXIV side of the ragdoll's host interfaces (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §3.2). Each member
// is the game access the controller used to make itself, moved here unchanged: same reads, same writes,
// same order within a call.

/// <summary>Builds the host of a controller from the plugin's services.</summary>
internal static class FfxivRagdollHost
{
    public static RagdollHost Create(BoneTransformService boneService, Configuration config, IPluginLog log,
        Npcs.NpcSelector? npcSelector, Safety.MovementBlockHook? movementBlockHook,
        Func<IReadOnlyList<nint>>? extraCollisionProvider) => new()
    {
        Skeletons = new FfxivSkeletonSource(boneService),
        World = FfxivWorldQuery.Instance,
        Actors = new FfxivRagdollActor(movementBlockHook),
        Colliders = new FfxivColliderActors(npcSelector, movementBlockHook, extraCollisionProvider),
        Settings = new ConfigRagdollSettings(config),
        Log = new DalamudRagdollLog(log),
    };
}

/// <summary>A character's skeleton read straight from the game's memory, valid for the current frame.</summary>
internal sealed unsafe class FfxivSkeletonPose : ISkeletonPose
{
    public SkeletonAccess Access;

    public nint Actor { get; set; }

    public FfxivSkeletonPose(nint actor, SkeletonAccess access)
    {
        Actor = actor;
        Access = access;
    }

    /// <summary>The raw game skeleton behind a pose, for the code that still reads models itself (stage A2b moves it).</summary>
    public static SkeletonAccess Of(ISkeletonPose skeleton) => skeleton is FfxivSkeletonPose game
        ? game.Access
        : throw new NotSupportedException("This needs the game's models (mesh input), which only the game provides until stage A2b.");

    private FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* GameRoot => Access.CharBase->Skeleton;

    private HkaPose* PartPose(int part) => part == 0 ? Access.Pose : GameRoot->PartialSkeletons[part].GetHavokPose(0);

    public nint DrawIdentity => (nint)Access.CharBase;

    public int BoneCount => Access.BoneCount;
    public int ParentCount => Access.ParentCount;
    public Span<BoneQs> ModelPose => new(Access.Pose->ModelPose.Data, Access.Pose->ModelPose.Length);
    public short Parent(int bone) => Access.HavokSkeleton->ParentIndices[bone];
    public int BoneNameCount => Access.HavokSkeleton->Bones.Length;
    public string? BoneName(int bone) => Access.HavokSkeleton->Bones[bone].Name.String;

    public ReadOnlySpan<BoneQs> ReferencePose => Access.HavokSkeleton->ReferencePose.Data == null
        ? ReadOnlySpan<BoneQs>.Empty
        : new(Access.HavokSkeleton->ReferencePose.Data, Access.HavokSkeleton->ReferencePose.Length);

    public bool HasRoot => Access.CharBase->Skeleton != null;

    public RootTransform Root
    {
        get
        {
            var skeleton = Access.CharBase->Skeleton;
            return new RootTransform(
                new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z),
                new Quaternion(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y,
                    skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W),
                new Vector3(skeleton->Transform.Scale.X, skeleton->Transform.Scale.Y, skeleton->Transform.Scale.Z));
        }
    }

    public bool TryGetDominantAnimation(out RagdollAnimationSample sample)
    {
        sample = default;
        var found = false;
        var skeleton = Access.CharBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1 || skeleton->PartialSkeletons == null) return false;
        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null) return false;

        // The animation with the most weight is the one the pose mostly comes from.
        var bestWeight = 0f;
        for (var i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = animated->AnimationControls[i].Value;
            if (control == null) continue;
            var binding = control->hkaAnimationControl.Binding.ptr;
            if (binding == null || binding->Animation.ptr == null) continue;
            var weight = control->hkaAnimationControl.Weight;
            var duration = binding->Animation.ptr->Duration;
            if (!(weight > bestWeight) || !(duration > 0f)) continue;
            bestWeight = weight;
            sample = new RagdollAnimationSample((nint)binding->Animation.ptr, control->hkaAnimationControl.LocalTime, duration);
            found = true;
        }
        return found;
    }

    public int PartCount => GameRoot->PartialSkeletons == null ? 0 : GameRoot->PartialSkeletonCount;

    public bool PartAvailable(int part)
    {
        var pose = PartPose(part);
        return pose != null && pose->Skeleton != null;
    }

    public bool PartHasPose(int part)
    {
        var pose = PartPose(part);
        return pose != null && pose->ModelPose.Data != null;
    }

    public bool PartInSync(int part) => PartPose(part)->ModelInSync != 0;
    public nint PartIdentity(int part) => (nint)PartPose(part)->Skeleton;
    public nint PartPoseIdentity(int part) => (nint)PartPose(part);

    public Span<BoneQs> PartModelPose(int part)
    {
        var pose = PartPose(part);
        return pose->ModelPose.Data == null ? Span<BoneQs>.Empty : new(pose->ModelPose.Data, pose->ModelPose.Length);
    }

    public int PartModelPoseLength(int part) => PartPose(part)->ModelPose.Length;

    public int PartBoneNameCount(int part) => PartPose(part)->Skeleton->Bones.Length;
    public string? PartBoneName(int part, int bone) => PartPose(part)->Skeleton->Bones[bone].Name.String;
    public int PartParentCount(int part) => PartPose(part)->Skeleton->ParentIndices.Length;
    public short PartParent(int part, int bone) => PartPose(part)->Skeleton->ParentIndices[bone];

    public ReadOnlySpan<BoneQs> PartReferencePose(int part)
    {
        var skeleton = PartPose(part)->Skeleton;
        return skeleton->ReferencePose.Data == null
            ? ReadOnlySpan<BoneQs>.Empty
            : new(skeleton->ReferencePose.Data, skeleton->ReferencePose.Length);
    }

    public int PartReferenceCount(int part) => PartPose(part)->Skeleton->ReferencePose.Length;

    public void ResyncPartModelPose(int part)
    {
        var pose = PartPose(part);
        pose->ModelInSync = 0;
        pose->SyncModelSpace();
    }

    public int PartConnectedBone(int part) => GameRoot->PartialSkeletons[part].ConnectedBoneIndex;
    public int PartConnectedParentBone(int part) => GameRoot->PartialSkeletons[part].ConnectedParentBoneIndex;
}

internal sealed class FfxivSkeletonSource : ISkeletonSource
{
    private readonly BoneTransformService boneService;

    // One reusable wrapper per character and thread: the controller asks for skeletons many times a frame
    // (its own and those of every collider), from the render hook and from framework-tick calls.
    [ThreadStatic] private static Dictionary<nint, FfxivSkeletonPose>? wrappers;

    public FfxivSkeletonSource(BoneTransformService boneService) => this.boneService = boneService;

    public BoneTransformService BoneService => boneService;

    public ISkeletonPose? TryGetSkeleton(nint actor, SkeletonReadSite site = SkeletonReadSite.Other)
    {
        var access = boneService.TryGetSkeleton(actor);
        if (access == null) return null;
        var map = wrappers ??= new Dictionary<nint, FfxivSkeletonPose>();
        if (map.TryGetValue(actor, out var wrapper))
        {
            wrapper.Access = access.Value;
            return wrapper;
        }
        if (map.Count > 512) map.Clear();
        return map[actor] = new FfxivSkeletonPose(actor, access.Value);
    }

    public Vector3? GetBoneWorldPosition(nint actor, string boneName) => boneService.GetBoneWorldPos(actor, boneName);
}

internal sealed class FfxivWorldQuery : IWorldQuery
{
    public static readonly FfxivWorldQuery Instance = new();

    public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        var result = BGCollisionModule.RaycastMaterialFilter(origin, direction, out var raw, maxDistance);
        hit = new RayHit
        {
            Point = raw.Point,
            Normal = raw.Normal,
            Distance = raw.Distance,
            Material = raw.Material,
            V1 = raw.V1,
            V2 = raw.V2,
            V3 = raw.V3,
        };
        return result;
    }

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
        => WorldCollisionMesh.TryCollectGround(min, max, triangles, maxTriangles);

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
        => WorldCollisionMesh.TryFindWaterSurface(position, searchAbove, searchBelow, out surfaceY);
}

internal sealed unsafe class FfxivRagdollActor : IRagdollActor
{
    private readonly Safety.MovementBlockHook? movementBlockHook;

    public FfxivRagdollActor(Safety.MovementBlockHook? movementBlockHook) => this.movementBlockHook = movementBlockHook;

    public bool GameRunning => Core.Services.ObjectTable.LocalPlayer != null;

    public nint LocalPlayer => Core.Services.ObjectTable.LocalPlayer?.Address ?? 0;

    /// <summary>Matches against the object table rather than dereferencing the address — once the actor is
    /// freed, its memory often still reads back the stale EntityId.</summary>
    public bool IsAlive(nint actor, uint entityId)
    {
        if (actor == nint.Zero) return false;
        foreach (var o in Core.Services.ObjectTable)
        {
            if (o.Address != actor) continue;
            return entityId == 0 || o.EntityId == entityId;
        }
        return false;
    }

    public Vector3 GetPosition(nint actor, ActorReadSite site)
    {
        var go = (GameObject*)actor;
        return new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
    }

    public float GetFacing(nint actor, ActorReadSite site) => ((GameObject*)actor)->Rotation;
    public uint GetEntityId(nint actor) => ((GameObject*)actor)->EntityId;

    /// <summary>The Dalamud game object id (only asked of the local player, as before).</summary>
    public ulong GetGameObjectId(nint actor)
        => Core.Services.ObjectTable.LocalPlayer is { } player && player.Address == actor ? player.GameObjectId : 0;
    public int GetObjectIndex(nint actor) => ((GameObject*)actor)->ObjectIndex;

    public bool TryGetDrawScale(nint actor, out Vector3 scale)
    {
        scale = Vector3.One;
        var gameObject = (GameObject*)actor;
        if (gameObject->DrawObject == null) return false;
        var s = gameObject->DrawObject->Scale;
        scale = new Vector3(s.X, s.Y, s.Z);
        return true;
    }

    public void ReadAppearance(nint actor, Span<byte> customize, out int modelCharaId)
    {
        var character = (Character*)actor;
        new ReadOnlySpan<byte>(&character->DrawData.CustomizeData, 0x1A).CopyTo(customize);
        modelCharaId = character->ModelContainer.ModelCharaId;
    }

    public void ReadModelType(nint actor, ISkeletonPose? skeleton, out int modelType, out int raceSexId)
    {
        modelType = -1;
        raceSexId = -1;
        if (skeleton is not FfxivSkeletonPose game) return;
        var cb = game.Access.CharBase;
        if (cb != null && cb->Models != null && game.Access.HavokSkeleton != null)
        {
            var type = cb->GetModelType();
            modelType = (int)type;
            if (type == CharacterBase.ModelType.Human) raceSexId = ((Human*)cb)->RaceSexId;
        }
    }

    public float GetAnimationSpeed(nint actor) => ((Character*)actor)->Timeline.OverallSpeed;
    public void SetAnimationSpeed(nint actor, float speed) => ((Character*)actor)->Timeline.OverallSpeed = speed;

    public void SetDrawPosition(nint actor, Vector3 position)
    {
        var drawObject = ((GameObject*)actor)->DrawObject;
        if (drawObject == null) return;
        ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)drawObject)->Position = position;
        drawObject->NotifyTransformChanged();
    }

    public bool SetApproachPosition(nint actor, Vector3 position)
    {
        if (movementBlockHook == null) return false;
        movementBlockHook.SetApproachPosition((GameObject*)actor, position.X, position.Y, position.Z);
        return true;
    }
}

internal sealed unsafe class FfxivColliderActors : IColliderActorProvider
{
    private readonly Npcs.NpcSelector? npcSelector;
    private readonly Safety.MovementBlockHook? movementBlockHook;
    private readonly Func<IReadOnlyList<nint>>? extraCollisionProvider;

    public FfxivColliderActors(Npcs.NpcSelector? npcSelector, Safety.MovementBlockHook? movementBlockHook,
        Func<IReadOnlyList<nint>>? extraCollisionProvider)
    {
        this.npcSelector = npcSelector;
        this.movementBlockHook = movementBlockHook;
        this.extraCollisionProvider = extraCollisionProvider;
    }

    public bool HasSelector => npcSelector != null;
    public bool HasExtraColliders => extraCollisionProvider != null;
    public bool HasMovementBlock => movementBlockHook != null;

    public void GetSelectedNpcs(List<ColliderCandidate> destination)
    {
        destination.Clear();
        if (npcSelector == null) return;
        foreach (var npc in npcSelector.SelectedNpcs)
            destination.Add(new ColliderCandidate(npc.Address, npc.BattleChara != null, npc.Name));
    }

    public IReadOnlyList<nint> GetExtraColliders() => extraCollisionProvider?.Invoke() ?? Array.Empty<nint>();

    public bool TryGetWeaponTransform(nint actor, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var gameObj = (GameObject*)actor;
        if (gameObj->DrawObject == null) return false;
        var character = (Character*)actor;
        var weaponDraw = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject;
        if (weaponDraw == null) return false;
        var sk = ((CharacterBase*)weaponDraw)->Skeleton;
        if (sk == null) return false;
        position = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
        rotation = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);
        return true;
    }
}

internal sealed class DalamudRagdollLog : IRagdollLog
{
    private readonly IPluginLog log;
    public DalamudRagdollLog(IPluginLog log) => this.log = log;
    public void Info(string message) => log.Info(message);
    public void Warning(string message) => log.Warning(message);
    public void Warning(Exception exception, string message) => log.Warning(exception, message);
    public void Error(string message) => log.Error(message);
    public void Error(Exception exception, string message) => log.Error(exception, message);
}
