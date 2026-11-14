// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation.Hosting;

// The FFXIV side of the dropped-weapon simulation's host (stage A5). Each member is the game access the plugin's
// WeaponDropController made itself before the simulation moved into the library, moved here unchanged: same reads,
// same writes, same order within a call.

/// <summary>Builds the host of the dropped-weapon simulation from the plugin's services. Its adapters are its own
/// (not those of a ragdoll), so nothing it reads is ever written into a ragdoll recording.</summary>
internal static class FfxivWeaponDropHost
{
    public static WeaponDropHost Create(BoneTransformService boneService, Configuration config, IPluginLog log) => new()
    {
        Skeletons = new FfxivSkeletonSource(boneService),
        World = FfxivWorldQuery.Instance,
        Actors = new FfxivRagdollActor(null),
        Weapons = new FfxivWeaponDrawSource(boneService),
        Settings = new ConfigRagdollSettings(config),
        Log = new DalamudRagdollLog(log),
        Profile = FfxivHumanoidProfile.Profile,
        Clock = StopwatchRagdollClock.Instance,
    };
}

/// <summary>A character's weapons: the weapon draw objects (opaque ids = their address) and their own skeletons.</summary>
internal sealed unsafe class FfxivWeaponDrawSource : IWeaponDrawSource
{
    private readonly BoneTransformService boneService;

    public FfxivWeaponDrawSource(BoneTransformService boneService) => this.boneService = boneService;

    public nint GetWeapon(nint actor, DrawnWeaponSlot slot)
    {
        var character = (Character*)actor;
        return (nint)character->DrawData.Weapon(slot == DrawnWeaponSlot.MainHand
            ? DrawDataContainer.WeaponSlot.MainHand
            : DrawDataContainer.WeaponSlot.OffHand).DrawObject;
    }

    public bool TryGetRoot(nint weapon, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var skeleton = ((CharacterBase*)weapon)->Skeleton;
        if (skeleton == null) return false;
        var transform = skeleton->Transform;
        position = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        rotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        return true;
    }

    public ISkeletonPose? TryGetSkeleton(nint weapon)
    {
        var access = boneService.TryGetSkeletonFromCharBase((CharacterBase*)weapon);
        return access == null ? null : new FfxivSkeletonPose(weapon, access.Value);
    }

    public Vector3 GetDrawScale(nint weapon)
    {
        var scale = ((DrawObject*)weapon)->Scale;
        return new Vector3(scale.X, scale.Y, scale.Z);
    }

    public void SetTransform(nint weapon, Vector3 position, Quaternion rotation)
    {
        var weaponDraw = (DrawObject*)weapon;
        var cb = (CharacterBase*)weaponDraw;
        var sk = cb->Skeleton;
        if (sk != null)
        {
            sk->Transform.Position.X = position.X;
            sk->Transform.Position.Y = position.Y;
            sk->Transform.Position.Z = position.Z;
            sk->Transform.Rotation.X = rotation.X;
            sk->Transform.Rotation.Y = rotation.Y;
            sk->Transform.Rotation.Z = rotation.Z;
            sk->Transform.Rotation.W = rotation.W;
        }

        weaponDraw->Position = position;
        weaponDraw->Rotation = rotation;
    }
}
