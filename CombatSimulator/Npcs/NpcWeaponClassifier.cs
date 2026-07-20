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
    // weapon model -> the ClassJob that equips it (Item.ClassJobUse). 0 = no/ambiguous job.
    // Built in the same Item-sheet pass as itemCategoryCache. Lets a humanoid enemy wielding a
    // real player weapon be resolved to a job, whose real action kit it can then cast.
    private static readonly Dictionary<WeaponModelKey, uint> itemJobCache = new();
    private static IDataManager? dataManager;
    private static IPluginLog? pluginLog;
    private static bool cacheBuilt;

    public static void Initialize(IDataManager dataManager, IPluginLog log)
    {
        NpcWeaponClassifier.dataManager = dataManager;
        pluginLog = log;
        itemCategoryCache.Clear();
        itemJobCache.Clear();
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

    /// <summary>
    /// Resolve the ClassJob that equips this character's main-hand weapon, via the weapon model →
    /// Item.ClassJobUse map. Returns 0 when the weapon is not a real equippable item (monster /
    /// NPC-only weapon) or the job is ambiguous — the caller then treats the enemy as monster/auto.
    /// </summary>
    public static uint DetectJobFromCharacter(Character* character)
    {
        if (character == null)
            return 0;

        var weapon = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
        return DetectJobFromWeapon(weapon.Id, weapon.Type, weapon.Variant);
    }

    public static uint DetectJobFromPackedWeapon(ulong packedWeapon)
    {
        if (packedWeapon == 0)
            return 0;

        var weapon = *(WeaponModelId*)&packedWeapon;
        return DetectJobFromWeapon(weapon.Id, weapon.Type, weapon.Variant);
    }

    private static uint DetectJobFromWeapon(ushort id, ushort type, ushort variant)
    {
        EnsureItemCategoryCache();
        return itemJobCache.TryGetValue(new WeaponModelKey(id, type, variant), out var job) ? job : 0u;
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
                // Job map: any item that declares an equipping job and carries a weapon model.
                var job = item.ClassJobUse.RowId;
                if (job != 0)
                {
                    AddModelJob(item.ModelMain, job);
                    AddModelJob(item.ModelSub, job);
                }

                var style = ClassifyItemCategory(item.ItemUICategory.RowId);
                if (style == NpcAttackStyle.Auto)
                    continue;

                AddModel(item.ModelMain, style);
                AddModel(item.ModelSub, style);
            }

            pluginLog?.Info(
                $"NPC weapon caches built: {itemCategoryCache.Count} style mappings, {itemJobCache.Count} job mappings.");
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

    private static void AddModelJob(ulong packedModel, uint job)
    {
        var key = UnpackModel(packedModel);
        if (key.Id == 0)
            return;

        if (itemJobCache.TryGetValue(key, out var existing))
        {
            // Same model shared by two different jobs → ambiguous, mark unknown (0) and keep it there.
            if (existing != job)
                itemJobCache[key] = 0u;
            return;
        }

        itemJobCache[key] = job;
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
