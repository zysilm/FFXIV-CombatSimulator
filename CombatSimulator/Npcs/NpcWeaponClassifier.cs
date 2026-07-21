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

    // Keyed by weapon MODEL SET id (the model's primary/set number, e.g. 2201 = katana), NOT the
    // full (Id,Type,Variant) triple. A set belongs to exactly one weapon category/job, so any NPC
    // weapon that reuses a player weapon set resolves — even if its exact variant has no Item row.
    // (Truly bespoke NPC-only sets still don't resolve; that's the data-layer floor.)
    private static readonly Dictionary<ushort, NpcAttackStyle> itemCategoryCache = new();
    // weapon set id -> the ClassJob that equips it (Item.ClassJobUse). 0 = no/ambiguous job.
    private static readonly Dictionary<ushort, uint> itemJobCache = new();
    // ClassJob row id -> its base-class row id (ClassJob.ClassJobParent), for resolving weapon sets
    // shared between a base class and its job (GLA swords vs PLD swords use the same model sets).
    private static readonly Dictionary<uint, uint> jobParents = new();

    /// <summary>Pugilist-line job used for bare-handed humanoids — no weapon means fists.</summary>
    public const uint MonkJobId = 20;
    private static IDataManager? dataManager;
    private static IPluginLog? pluginLog;
    private static bool cacheBuilt;

    public static void Initialize(IDataManager dataManager, IPluginLog log)
    {
        NpcWeaponClassifier.dataManager = dataManager;
        pluginLog = log;
        itemCategoryCache.Clear();
        itemJobCache.Clear();
        jobParents.Clear();
        jobClaims.Clear();
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

        // A bare-handed humanoid fights with its fists — give it the pugilist/monk kit.
        // Humanoid = player-model character (ModelCharaId 0); monsters keep no-job/auto-only.
        if (weapon.Id == 0)
            return character->ModelContainer.ModelCharaId == 0 ? MonkJobId : 0u;

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
        return itemJobCache.TryGetValue(id, out var job) ? job : 0u;
    }

    private static NpcAttackStyle DetectFromWeapon(ushort id, ushort type, ushort variant)
    {
        if (TryDetectFromItemCategory(id, out var itemStyle))
            return itemStyle;

        if (PhysicalRangedWeaponModels.Contains(new WeaponModelKey(id, type, variant)))
            return NpcAttackStyle.Ranged;

        return NpcAttackStyle.Auto;
    }

    private static bool TryDetectFromItemCategory(ushort setId, out NpcAttackStyle style)
    {
        EnsureItemCategoryCache();
        if (!itemCategoryCache.TryGetValue(setId, out style))
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

            // Base-class links first, so weapon-set conflicts between a class and its job
            // (GLA/PLD, THM/BLM, CNJ/WHM, ARC/BRD, …) resolve to the job instead of poisoning
            // the set as ambiguous — shared sets between class and job are the NORM, and the
            // old "any conflict → 0" rule wiped out entire weapon families (swords most of all).
            var jobSheet = dataManager.GetExcelSheet<ClassJob>();
            if (jobSheet != null)
            {
                foreach (var job in jobSheet)
                {
                    var parent = job.ClassJobParent.RowId;
                    if (parent != 0 && parent != job.RowId)
                        jobParents[job.RowId] = parent;
                }
            }

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

            // Resolve every set's claimants in one order-independent pass.
            var poisoned = 0;
            foreach (var (setId, claims) in jobClaims)
            {
                var resolved = ResolveClaims(claims);
                itemJobCache[setId] = resolved;
                if (resolved == 0) poisoned++;
            }
            jobClaims.Clear();

            pluginLog?.Info(
                $"NPC weapon caches built: {itemCategoryCache.Count} style mappings, " +
                $"{itemJobCache.Count} job mappings ({poisoned} ambiguous).");
        }
        catch (Exception ex)
        {
            pluginLog?.Warning(ex, "Failed to build NPC weapon item-category cache.");
        }
    }

    private static void AddModel(ulong packedModel, NpcAttackStyle style)
    {
        var setId = UnpackModel(packedModel).Id;
        if (setId == 0)
            return;

        if (itemCategoryCache.TryGetValue(setId, out var existing))
        {
            if (existing == style)
                return;

            itemCategoryCache[setId] = NpcAttackStyle.Auto;
            return;
        }

        itemCategoryCache[setId] = style;
    }

    // Build-time only: every job that claims each weapon set. Resolved in one pass afterwards so
    // the outcome never depends on Item-sheet order.
    private static readonly Dictionary<ushort, HashSet<uint>> jobClaims = new();

    private static void AddModelJob(ulong packedModel, uint job)
    {
        var setId = UnpackModel(packedModel).Id;
        if (setId == 0)
            return;

        if (!jobClaims.TryGetValue(setId, out var claims))
            jobClaims[setId] = claims = new HashSet<uint>();
        claims.Add(job);
    }

    // Weapon sets shared across a class line are the NORM, not an anomaly:
    //  - class + its job (GLA/PLD swords, THM/BLM staves, …) → resolve to the JOB (fullest kit);
    //  - sibling jobs of one base (ACN/SMN/SCH grimoires)   → resolve to the common BASE class
    //    (its kit is the shared subset — stable and sensible for a book-wielding NPC);
    //  - genuinely unrelated lines on one set → ambiguous (0), auto-attack only.
    private static uint ResolveClaims(HashSet<uint> jobs)
    {
        if (jobs.Count == 1)
        {
            foreach (var only in jobs) return only;
        }

        // Exactly one member all of whose co-claimants are its ancestors → that specialized job.
        uint specialized = 0;
        var specializedCount = 0;
        foreach (var j in jobs)
        {
            var allAncestors = true;
            foreach (var other in jobs)
            {
                if (other == j) continue;
                if (!IsAncestor(other, j)) { allAncestors = false; break; }
            }
            if (allAncestors) { specialized = j; specializedCount++; }
        }
        if (specializedCount == 1)
            return specialized;

        // Siblings (and possibly their base) — accept the common root class if there is one.
        uint commonRoot = 0;
        foreach (var j in jobs)
        {
            var root = RootOf(j);
            if (commonRoot == 0) commonRoot = root;
            else if (commonRoot != root) return 0u;
        }
        return commonRoot;
    }

    private static bool IsAncestor(uint ancestor, uint job)
    {
        while (jobParents.TryGetValue(job, out var parent))
        {
            if (parent == ancestor) return true;
            job = parent;
        }
        return false;
    }

    private static uint RootOf(uint job)
    {
        while (jobParents.TryGetValue(job, out var parent))
            job = parent;
        return job;
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
