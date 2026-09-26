using System;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Effects;

/// <summary>Local-player visual override. Never edits equipment or networked weapon state.</summary>
public sealed unsafe class PlayerWeaponVisibilityController : IDisposable
{
    private readonly Configuration config;
    private readonly IObjectTable objects;
    private readonly BoneTransformService bones;
    private nint owner;
    private HiddenWeapon main, off;

    private struct HiddenWeapon
    {
        public nint Address;
        public bool WasVisible;
    }

    public PlayerWeaponVisibilityController(Configuration config, IObjectTable objects, BoneTransformService bones)
    {
        this.config = config;
        this.objects = objects;
        this.bones = bones;
        bones.OnRenderFrame += Apply;
    }

    private void Apply()
    {
        var address = objects.LocalPlayer?.Address ?? nint.Zero;
        if (address != owner)
        {
            // The previous player may already be destroyed; never dereference cached pointers.
            main = off = default;
            owner = address;
        }
        if (address == nint.Zero) return;
        var character = (Character*)address;
        Update(character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject,
            config.HidePlayerRightWeapon, ref main);
        Update(character->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).DrawObject,
            config.HidePlayerLeftWeapon, ref off);
    }

    private static void Update(DrawObject* draw, bool hide, ref HiddenWeapon captured)
    {
        if ((nint)draw != captured.Address) captured = default;
        if (draw == null) return;
        if (hide)
        {
            if (captured.Address == nint.Zero)
                captured = new HiddenWeapon { Address = (nint)draw, WasVisible = draw->IsVisible };
            draw->IsVisible = false;
        }
        else if (captured.Address != nint.Zero)
        {
            draw->IsVisible = captured.WasVisible;
            captured = default;
        }
    }

    public void Dispose()
    {
        bones.OnRenderFrame -= Apply;
        if (owner == nint.Zero || objects.LocalPlayer?.Address != owner) return;
        var character = (Character*)owner;
        Update(character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject, false, ref main);
        Update(character->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).DrawObject, false, ref off);
    }
}
