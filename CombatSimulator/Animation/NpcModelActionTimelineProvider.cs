// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Npcs;
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
    private const string SkeletonTemplatePrefix = "mon_sp/[SKL_ID]/mon_sp";

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, FamilyEntry> cache = new();
    private readonly Dictionary<uint, ushort> lastSelection = new();

    private sealed class FamilyEntry
    {
        public HashSet<ushort> AllTimelines { get; init; } = new();
        /// <summary>
        /// One entry per timeline: the family's Action rows that
        /// <see cref="MonsterKitBuilder.IsAnimationUsable"/> accepts, or every timeline (ActionId 0)
        /// when no Action row is linked at all. Empty when linked rows exist but none is usable —
        /// the generic auto-attack then plays.
        /// </summary>
        public NpcModelActionSelection[] AttackPool { get; init; } = Array.Empty<NpcModelActionSelection>();
        /// <summary>
        /// Motion-only victory flourishes: the attack pool's timelines, or — when that is empty —
        /// the timelines of every linked Action row, so a family whose rows are all unfit to lend
        /// their VFX to an attack still has its own authored motions to celebrate with.
        /// </summary>
        public ushort[] FlourishTimelines { get; init; } = Array.Empty<ushort>();
        /// <summary>Every Action row whose AnimationEnd is one of this family's timelines.</summary>
        public uint[] LinkedActionIds { get; init; } = Array.Empty<uint>();
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
    /// action already supplied a timeline from the same family, it is preserved; otherwise a
    /// timeline is drawn from the family's attack pool (rows fit to lend an attack animation, see
    /// <see cref="MonsterKitBuilder.IsAnimationUsable"/>). Returns 0 when the pool is empty, so
    /// the caller plays the generic auto-attack.
    /// </summary>
    public ushort Select(uint modelCharaId, ushort preferredTimeline = 0)
        => SelectAction(modelCharaId, preferredTimeline).TimelineId;

    /// <summary>
    /// A celebratory one-shot from the model's own family. Played as motion only (no Action id,
    /// so no Action VFX reaches another actor); independent of whether the attack pool is empty.
    /// Returns 0 for human models and families without timelines.
    /// </summary>
    public ushort SelectFlourish(uint modelCharaId)
    {
        if (modelCharaId == 0)
            return 0;
        var flourishes = GetFamily(modelCharaId).FlourishTimelines;
        return flourishes.Length == 0 ? (ushort)0 : flourishes[Random.Shared.Next(flourishes.Length)];
    }

    /// <summary>
    /// Resolves the concrete TMB behind a selected timeline. Generic <c>[SKL_ID]</c> rows are
    /// expanded with the actor's DemiHuman/Monster skeleton family before the resource is opened.
    /// </summary>
    public string? ResolveTmbPath(uint modelCharaId, ushort timelineId)
    {
        if (modelCharaId == 0 || timelineId == 0)
            return null;

        try
        {
            var model = dataManager.GetExcelSheet<ModelChara>()?.GetRowOrDefault(modelCharaId);
            var timeline = dataManager.GetExcelSheet<ActionTimeline>()?.GetRowOrDefault(timelineId);
            if (model == null || timeline == null || model.Value.Type is not (2 or 3))
                return null;

            var key = timeline.Value.Key.ExtractText();
            if (string.IsNullOrWhiteSpace(key))
                return null;

            if (key.StartsWith(SkeletonTemplatePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var familyLetter = model.Value.Type == 2 ? 'd' : 'm';
                var suffix = key[SkeletonTemplatePrefix.Length..];
                key = $"mon_sp/{familyLetter}{model.Value.Model:D4}/mon_sp{suffix}";
            }
            else if (key.Contains("[SKL_ID]", StringComparison.Ordinal))
            {
                return null;
            }

            var path = $"chara/action/{key}.tmb";
            return dataManager.FileExists(path) ? path : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to resolve model timeline TMB for ModelChara {modelCharaId}, timeline {timelineId}.");
            return null;
        }
    }

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

        var family = GetFamily(modelCharaId);

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

    /// <summary>
    /// The real Action rows authored for this model family (all of them, not just the attack
    /// pool), for building a monster's skill kit. Empty for human models and for families that
    /// only resolve through skeleton templates — those have no Action rows to read areas from.
    /// </summary>
    public IReadOnlyList<uint> GetLinkedActionIds(uint modelCharaId)
        => modelCharaId == 0 ? Array.Empty<uint>() : GetFamily(modelCharaId).LinkedActionIds;

    /// <summary>Whether <paramref name="timelineId"/> belongs to the model's own family.</summary>
    public bool FamilyContainsTimeline(uint modelCharaId, ushort timelineId)
        => modelCharaId != 0 && timelineId != 0 && GetFamily(modelCharaId).AllTimelines.Contains(timelineId);

    private FamilyEntry GetFamily(uint modelCharaId)
    {
        if (!cache.TryGetValue(modelCharaId, out var family))
        {
            family = BuildFamily(modelCharaId);
            cache[modelCharaId] = family;
        }
        return family;
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
                        FlourishTimelines = templateTimelines.Order().ToArray(),
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

            // The pool's Action id also picks the VFX that plays on the target, so a row is only
            // borrowed when it is an attack (MonsterKitBuilder.IsAnimationUsable: the kit's rule plus
            // the family's own single-target auto-attack/ability swings): escapes, buffs, heals and
            // arena mechanics stay out. A family whose rows are all rejected gets an empty pool, and
            // the caller falls back to the generic auto-attack.
            NpcModelActionSelection[] pool;
            string tier;
            if (linked.Length > 0)
            {
                pool = linked
                    .Select(static action => MonsterActionRows.ToCandidate(action))
                    .Where(static candidate => MonsterKitBuilder.IsAnimationUsable(candidate))
                    .GroupBy(static candidate => candidate.TimelineId)
                    .Select(static group => new NpcModelActionSelection(group.First().ActionId, group.Key))
                    .ToArray();
                tier = pool.Length > 0 ? "usable-action" : "no-usable-action";
            }
            else
            {
                // No Action row authors any of these timelines, so there is no VFX to borrow
                // (ActionId 0) and nothing to judge; any family timeline is an attack pose.
                pool = all.Select(timeline => new NpcModelActionSelection(0, timeline)).ToArray();
                tier = "family-fallback";
            }

            log.Info(
                $"NPC model action family: ModelChara={modelCharaId}, prefix={prefix}, " +
                $"timelines={all.Count}, linkedActions={linked.Length}, attackPool={pool.Length}, tier={tier}" +
                (pool.Length == 0 ? "; using generic attack fallback." : "."));

            var flourishes = pool.Length > 0
                ? pool.Select(static entry => entry.TimelineId)
                : linked.Select(static action => (ushort)action.AnimationEnd.RowId);

            return new FamilyEntry
            {
                AllTimelines = all,
                AttackPool = pool,
                FlourishTimelines = flourishes.Distinct().Order().ToArray(),
                LinkedActionIds = linked.Select(static action => action.RowId).ToArray(),
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
