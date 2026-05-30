using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Npcs;

public static unsafe class NpcWeaponClassifier
{
    private readonly record struct WeaponModelKey(ushort Id, ushort Type, ushort Variant);

    // WeaponModelId.Type values follow the weapon model category, not item row ids.
    // Known ranged/caster categories are intentionally conservative; unknown types
    // fall back to template behavior and are logged so we can extend this safely.
    private static readonly HashSet<ushort> PhysicalRangedWeaponTypes = new()
    {
        5,  // Bow
        10, // Firearm
        17, // Throwing weapons
    };

    private static readonly HashSet<ushort> MagicalRangedWeaponTypes = new()
    {
        6,  // Cane
        7,  // Staff / scepter
        8,  // Grimoire
        13, // Star globe
        16, // Rapier / focus
        20, // Nouliths
    };

    private static readonly HashSet<WeaponModelKey> PhysicalRangedWeaponModels = new()
    {
        // Coeurlclaw Hunter bow.
        new(601, 1, 4),
    };

    public static NpcAttackStyle DetectFromCharacter(Character* character, IPluginLog? log = null, string? name = null)
    {
        if (character == null)
            return NpcAttackStyle.Auto;

        var weapon = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
        var style = DetectFromWeapon(weapon.Id, weapon.Type, weapon.Variant);

        log?.Info(
            $"NPC weapon classify '{name ?? "unknown"}': " +
            $"mainHand id={weapon.Id}, type={weapon.Type}, variant={weapon.Variant} -> {style}");

        return style;
    }

    public static NpcAttackStyle DetectFromPackedWeapon(ulong packedWeapon)
    {
        if (packedWeapon == 0)
            return NpcAttackStyle.Auto;

        var weapon = *(WeaponModelId*)&packedWeapon;
        return DetectFromWeapon(weapon.Id, weapon.Type, weapon.Variant);
    }

    private static NpcAttackStyle DetectFromWeapon(ushort id, ushort type, ushort variant)
    {
        if (PhysicalRangedWeaponModels.Contains(new WeaponModelKey(id, type, variant)))
            return NpcAttackStyle.Ranged;

        return DetectFromWeaponType(type);
    }

    private static NpcAttackStyle DetectFromWeaponType(ushort type)
    {
        if (PhysicalRangedWeaponTypes.Contains(type))
            return NpcAttackStyle.Ranged;

        if (MagicalRangedWeaponTypes.Contains(type))
            return NpcAttackStyle.Magic;

        return NpcAttackStyle.Auto;
    }
}
