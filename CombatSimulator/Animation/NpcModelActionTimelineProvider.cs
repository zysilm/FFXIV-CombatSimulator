// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Animation;

public readonly record struct NpcModelActionSelection(uint ActionId, ushort TimelineId);

/// <summary>
/// Resolves attack animations that belong to a DemiHuman/Monster model's own <c>mon_sp</c>
/// family. The game does not expose a universal BNpc-to-skill-kit table, so this deliberately
/// chooses animation timelines only; combat damage and targeting remain owned by the simulator.
/// </summary>
public sealed class NpcModelActionTimelineProvider
{
    private const uint ActionCategorySpell = 2;
    private const uint ActionCategoryWeaponskill = 3;
    private const string SkeletonTemplatePrefix = "mon_sp/[SKL_ID]/mon_sp";

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, FamilyEntry> cache = new();
    private readonly Dictionary<uint, ushort> lastSelection = new();

    private sealed class FamilyEntry
    {
        public HashSet<ushort> AllTimelines { get; init; } = new();
        public NpcModelActionSelection[] AttackPool { get; init; } = Array.Empty<NpcModelActionSelection>();
        public string Prefix { get; init; } = string.Empty;
        public string Tier { get; init; } = "none";
    }

    public NpcModelActionTimelineProvider(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>
    /// Returns an animation compatible with <paramref name="modelCharaId"/>. If an authored
    /// action already supplied a timeline from the same family, it is preserved; otherwise an
    /// attack candidate is selected without consulting localized action names.
    /// </summary>
    public ushort Select(uint modelCharaId, ushort preferredTimeline = 0)
        => SelectAction(modelCharaId, preferredTimeline).TimelineId;

    /// <summary>
    /// Returns both the model-compatible timeline and the Action row that authored it. The Action
    /// id is metadata for resolving VFX only; combat and audio remain on the simulator's action.
    /// </summary>
    public NpcModelActionSelection SelectAction(
        uint modelCharaId,
        ushort preferredTimeline = 0,
        uint preferredActionId = 0)
    {
        if (modelCharaId == 0)
            return default;

        if (!cache.TryGetValue(modelCharaId, out var family))
        {
            family = BuildFamily(modelCharaId);
            cache[modelCharaId] = family;
        }

        if (preferredTimeline != 0 && family.AllTimelines.Contains(preferredTimeline))
            return new NpcModelActionSelection(preferredActionId, preferredTimeline);

        var pool = family.AttackPool;
        if (pool.Length == 0)
            return default;
        if (pool.Length == 1)
            return pool[0];

        lastSelection.TryGetValue(modelCharaId, out var previous);
        var start = Random.Shared.Next(pool.Length);
        for (var offset = 0; offset < pool.Length; offset++)
        {
            var selected = pool[(start + offset) % pool.Length];
            if (selected.TimelineId == previous)
                continue;

            lastSelection[modelCharaId] = selected.TimelineId;
            return selected;
        }

        return pool[start];
    }

    private FamilyEntry BuildFamily(uint modelCharaId)
    {
        try
        {
            var model = dataManager.GetExcelSheet<ModelChara>()?.GetRowOrDefault(modelCharaId);
            if (model == null || model.Value.Type is not (2 or 3))
                return new FamilyEntry();

            var familyLetter = model.Value.Type == 2 ? 'd' : 'm';
            var prefix = $"mon_sp/{familyLetter}{model.Value.Model:D4}/";
            var timelineSheet = dataManager.GetExcelSheet<ActionTimeline>();
            var actionSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (timelineSheet == null || actionSheet == null)
                return new FamilyEntry { Prefix = prefix };

            var all = timelineSheet
                .Where(row => row.RowId <= ushort.MaxValue &&
                              row.Key.ExtractText().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(row => (ushort)row.RowId)
                .ToHashSet();
            if (all.Count == 0)
            {
                // Newer/non-player families may have the TMB/PAP files in SqPack without a
                // literal ActionTimeline row such as mon_sp/d1048/mon_sp001. The game exposes
                // generic rows named mon_sp/[SKL_ID]/mon_spNNN instead and substitutes the
                // current actor's skeleton family when they play. Only admit a template when
                // that exact family's TMB exists; otherwise PlayTimeline would address a
                // nonexistent resource and leave the actor motionless.
                var templatePool = new List<NpcModelActionSelection>();
                foreach (var row in timelineSheet)
                {
                    if (row.RowId > ushort.MaxValue)
                        continue;

                    var key = row.Key.ExtractText();
                    if (!key.StartsWith(SkeletonTemplatePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var suffix = key.AsSpan(SkeletonTemplatePrefix.Length);
                    if (suffix.Length != 3 || !int.TryParse(suffix, out var specialIndex))
                        continue;

                    var familyTmbPath =
                        $"chara/action/{prefix}mon_sp{specialIndex:D3}.tmb";
                    if (!dataManager.FileExists(familyTmbPath))
                        continue;

                    templatePool.Add(new NpcModelActionSelection(0, (ushort)row.RowId));
                }

                if (templatePool.Count > 0)
                {
                    templatePool.Sort(static (left, right) =>
                        left.TimelineId.CompareTo(right.TimelineId));
                    var templateTimelines = templatePool
                        .Select(static candidate => candidate.TimelineId)
                        .ToHashSet();

                    log.Info(
                        $"NPC model action family: ModelChara={modelCharaId}, prefix={prefix}, " +
                        $"literalTimelines=0, templateTimelines={templatePool.Count}, " +
                        "tier=skeleton-template.");
                    return new FamilyEntry
                    {
                        AllTimelines = templateTimelines,
                        AttackPool = templatePool.ToArray(),
                        Prefix = prefix,
                        Tier = "skeleton-template",
                    };
                }

                log.Info(
                    $"NPC model action family: ModelChara={modelCharaId}, prefix={prefix}, " +
                    "literalTimelines=0, templateTimelines=0; using generic attack fallback.");
                return new FamilyEntry { Prefix = prefix };
            }

            var linked = actionSheet
                .Where(action => action.AnimationEnd.RowId <= ushort.MaxValue &&
                                 all.Contains((ushort)action.AnimationEnd.RowId))
                .ToArray();

            // Strongest signal: the game explicitly permits a hostile target. This excludes
            // heals/guards without relying on localized names. Self-centred hostile AoEs may not
            // carry this flag, so progressively broader structural fallbacks follow.
            var pool = linked
                .Where(action => action.CanTargetHostile)
                .GroupBy(action => (ushort)action.AnimationEnd.RowId)
                .Select(group => new NpcModelActionSelection(group.First().RowId, group.Key))
                .ToArray();
            var tier = "hostile-target";

            if (pool.Length == 0)
            {
                pool = linked
                    .Where(action => action.AttackType.RowId != 0 &&
                                     action.DeadTargetBehaviour != 1 &&
                                     action.ActionCategory.RowId is ActionCategorySpell or ActionCategoryWeaponskill &&
                                     !action.CanTargetParty &&
                                     !action.CanTargetAlliance &&
                                     !action.CanTargetAlly &&
                                     !action.CanTargetOwnPet &&
                                     !action.CanTargetPartyPet)
                    .GroupBy(action => (ushort)action.AnimationEnd.RowId)
                    .Select(group => new NpcModelActionSelection(group.First().RowId, group.Key))
                    .ToArray();
                tier = "attack-shaped";
            }

            if (pool.Length == 0)
            {
                pool = linked
                    .GroupBy(action => (ushort)action.AnimationEnd.RowId)
                    .Select(group => new NpcModelActionSelection(group.First().RowId, group.Key))
                    .ToArray();
                tier = "linked-family";
            }

            if (pool.Length == 0)
            {
                pool = all.Select(timeline => new NpcModelActionSelection(0, timeline)).ToArray();
                tier = "family-fallback";
            }

            log.Info(
                $"NPC model action family: ModelChara={modelCharaId}, prefix={prefix}, " +
                $"timelines={all.Count}, attackPool={pool.Length}, tier={tier}.");

            return new FamilyEntry
            {
                AllTimelines = all,
                AttackPool = pool,
                Prefix = prefix,
                Tier = tier,
            };
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to resolve model action family for ModelChara {modelCharaId}.");
            return new FamilyEntry();
        }
    }
}
