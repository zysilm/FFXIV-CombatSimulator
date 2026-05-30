using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

public static unsafe class NpcWeaponClassifier
{
    private readonly record struct WeaponModelKey(ushort Id, ushort Type, ushort Variant);

    private static readonly HashSet<uint> PhysicalRangedItemCategories = new()
    {
        4,   // Archer's Arm
        88,  // Machinist's Arm
        107, // Dancer's Arm
    };

    private static readonly HashSet<uint> MagicalRangedItemCategories = new()
    {
        6,   // One-handed thaumaturge's arm
        7,   // Two-handed thaumaturge's arm
        8,   // One-handed conjurer's arm
        9,   // Two-handed conjurer's arm
        10,  // Arcanist's grimoire
        89,  // Astrologian's arm
        97,  // Red mage's arm
        98,  // Scholar's arm
        105, // Blue mage's arm
        109, // Sage's arm
    };

    private static readonly HashSet<WeaponModelKey> PhysicalRangedWeaponModels = new()
    {
        // Coeurlclaw Hunter bow.
        new(601, 1, 4),

        // Amalj'aa Scavenger ranged weapon.
        new(601, 3, 8),
    };

    private static readonly Dictionary<WeaponModelKey, NpcAttackStyle> itemCategoryCache = new();
    private static IDataManager? dataManager;
    private static IPluginLog? pluginLog;
    private static bool cacheBuilt;

    public static void Initialize(IDataManager dataManager, IPluginLog log)
    {
        NpcWeaponClassifier.dataManager = dataManager;
        pluginLog = log;
        itemCategoryCache.Clear();
        cacheBuilt = false;
    }

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
        var key = new WeaponModelKey(id, type, variant);

        if (TryDetectFromItemCategory(key, out var itemStyle))
            return itemStyle;

        if (PhysicalRangedWeaponModels.Contains(new WeaponModelKey(id, type, variant)))
            return NpcAttackStyle.Ranged;

        return NpcAttackStyle.Auto;
    }

    private static bool TryDetectFromItemCategory(WeaponModelKey key, out NpcAttackStyle style)
    {
        EnsureItemCategoryCache();
        if (!itemCategoryCache.TryGetValue(key, out style))
            return false;

        return style != NpcAttackStyle.Auto;
    }

    private static void EnsureItemCategoryCache()
    {
        if (cacheBuilt)
            return;

        cacheBuilt = true;

        if (dataManager == null)
            return;

        try
        {
            var sheet = dataManager.GetExcelSheet<Item>();
            if (sheet == null)
                return;

            foreach (var item in sheet)
            {
                var style = ClassifyItemCategory(item.ItemUICategory.RowId);
                if (style == NpcAttackStyle.Auto)
                    continue;

                AddModel(item.ModelMain, style);
                AddModel(item.ModelSub, style);
            }

            pluginLog?.Info($"NPC weapon item-category cache built: {itemCategoryCache.Count} model mappings.");
        }
        catch (Exception ex)
        {
            pluginLog?.Warning(ex, "Failed to build NPC weapon item-category cache.");
        }
    }

    private static void AddModel(ulong packedModel, NpcAttackStyle style)
    {
        var key = UnpackModel(packedModel);
        if (key.Id == 0)
            return;

        if (itemCategoryCache.TryGetValue(key, out var existing))
        {
            if (existing == style)
                return;

            itemCategoryCache[key] = NpcAttackStyle.Auto;
            return;
        }

        itemCategoryCache[key] = style;
    }

    private static WeaponModelKey UnpackModel(ulong packedModel)
    {
        return new WeaponModelKey(
            (ushort)packedModel,
            (ushort)(packedModel >> 16),
            (ushort)(packedModel >> 32));
    }

    private static NpcAttackStyle ClassifyItemCategory(uint itemUiCategoryId)
    {
        if (PhysicalRangedItemCategories.Contains(itemUiCategoryId))
            return NpcAttackStyle.Ranged;

        if (MagicalRangedItemCategories.Contains(itemUiCategoryId))
            return NpcAttackStyle.Magic;

        return NpcAttackStyle.Auto;
    }
}
