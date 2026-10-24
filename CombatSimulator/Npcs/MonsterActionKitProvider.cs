// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Action = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Npcs;

/// <summary>
/// Builds a monster's kit from the real Action rows of its own model family — the same rows the
/// animation layer already plays timelines from — so a monster's special attack keeps its real
/// cast time, area and omen instead of becoming a 2.5y auto-attack with a random animation.
/// Selection rules live in <see cref="MonsterKitBuilder"/>.
/// </summary>
public sealed class MonsterActionKitProvider
{
    private readonly NpcModelActionTimelineProvider families;
    private readonly ActionDataProvider actionData;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<(uint Model, int Max, int Auto), List<NpcSkill>> cache = new();

    public MonsterActionKitProvider(
        NpcModelActionTimelineProvider families,
        ActionDataProvider actionData,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.families = families;
        this.actionData = actionData;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Kit definitions (shared, cached) — callers clone each skill per enemy.</summary>
    public IReadOnlyList<NpcSkill> BuildKit(uint modelCharaId, int maxSkills, int autoAttackPotency)
    {
        if (modelCharaId == 0 || maxSkills <= 0)
            return Array.Empty<NpcSkill>();

        var key = (modelCharaId, maxSkills, autoAttackPotency);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var kit = BuildUncached(modelCharaId, maxSkills, autoAttackPotency);
        cache[key] = kit;
        return kit;
    }

    private List<NpcSkill> BuildUncached(uint modelCharaId, int maxSkills, int autoAttackPotency)
    {
        var result = new List<NpcSkill>();
        try
        {
            var sheet = dataManager.GetExcelSheet<Action>();
            if (sheet == null)
                return result;

            var candidates = new List<MonsterActionCandidate>();
            foreach (var id in families.GetLinkedActionIds(modelCharaId))
            {
                if (sheet.GetRowOrDefault(id) is not { } a)
                    continue;
                candidates.Add(new MonsterActionCandidate(
                    a.RowId,
                    a.Name.ExtractText(),
                    a.CastType,
                    a.Range,
                    a.EffectRange,
                    a.XAxisModifier,
                    a.Cast100ms * 0.1f,
                    a.Recast100ms * 0.1f,
                    a.ActionCategory.RowId,
                    a.AttackType.RowId,
                    a.CanTargetHostile,
                    a.CanTargetParty || a.CanTargetAlly || a.CanTargetAlliance || a.CanTargetOwnPet || a.CanTargetPartyPet,
                    a.DeadTargetBehaviour == 1,
                    a.Omen.RowId != 0,
                    a.AnimationEnd.RowId <= ushort.MaxValue ? (ushort)a.AnimationEnd.RowId : (ushort)0));
            }

            foreach (var c in MonsterKitBuilder.Select(candidates, maxSkills))
            {
                var data = actionData.GetActionData(c.ActionId);
                if (data == null)
                    continue;

                var isArea = MonsterKitBuilder.IsArea(c);
                result.Add(new NpcSkill
                {
                    ActionId = c.ActionId,
                    Name = c.Name,
                    Potency = MonsterKitBuilder.Potency(data.Potency, autoAttackPotency, isArea, c.CastSeconds),
                    // Instant by default, like the humanoid kits; the real cast is kept aside and only
                    // used when telegraphed casts are enabled (see NpcSkill.NativeCastTime).
                    CastTime = 0f,
                    NativeCastTime = c.CastSeconds,
                    Cooldown = MonsterKitBuilder.Cooldown(c),
                    Range = MonsterKitBuilder.EngageRange(c),
                    Radius = isArea ? c.EffectRange : 0f,
                    // Melee style for every monster skill: the windup then plays the skill's own
                    // model-family timeline, and no skill flips a melee monster to ranged intent.
                    AttackStyle = NpcAttackStyle.Melee,
                    IsMonsterSkill = true,
                    Priority = result.Count,
                    HpThreshold = 1.0f,
                });
            }

            log.Info(
                $"MonsterActionKitProvider: ModelChara={modelCharaId} candidates={candidates.Count} → {result.Count} skills: " +
                string.Join(", ", result.Select(s => $"{s.Name}#{s.ActionId}(p{s.Potency},r{s.Radius},cast{s.NativeCastTime:0.0})")));
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"MonsterActionKitProvider: failed for ModelChara {modelCharaId}; auto-attack only.");
            result.Clear();
        }
        return result;
    }
}
