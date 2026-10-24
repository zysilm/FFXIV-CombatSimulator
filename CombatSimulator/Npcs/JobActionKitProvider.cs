// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Npcs;

/// <summary>
/// Builds a humanoid enemy's real combat kit from its resolved job. Given a ClassJob id
/// (from <see cref="NpcWeaponClassifier.DetectJobFromCharacter"/>) and the sim's enemy level,
/// enumerates that job's real damage actions off the Action sheet, ranks them by the same
/// virtual-potency model the rest of the plugin uses, and returns the strongest N as
/// <see cref="NpcSkill"/> entries the AI can cast with their genuine action ids — so a humanoid
/// enemy performs its real weaponskills instead of a hardcoded template.
///
/// Monsters / NPC-only weapons resolve to job 0 upstream and never reach here (auto-attack only).
/// </summary>
public class JobActionKitProvider
{
    // ActionCategory row ids. We keep only GCD damage (Spell/Weaponskill): the ability category
    // mixes real damage oGCDs with pure-utility oGCDs (Winged Glide, Dismantle, Mug, jumps…) that
    // target hostiles yet deal no damage, and client data can't tell them apart. GCD spells/
    // weaponskills that target a hostile always deal damage, so this is the clean cut.
    private const uint ActionCategorySpell = 2;
    private const uint ActionCategoryWeaponskill = 3;

    private readonly ActionDataProvider actionDataProvider;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<(uint Job, int Level, int Max, bool MeleeOnly, bool IncludeAoe), List<NpcSkill>> cache = new();

    public JobActionKitProvider(ActionDataProvider actionDataProvider, IDataManager dataManager, IPluginLog log)
    {
        this.actionDataProvider = actionDataProvider;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>
    /// The strongest <paramref name="maxSkills"/> damage actions for <paramref name="jobId"/> usable
    /// at <paramref name="levelCap"/>, ranked by estimated potency. Empty if the job/data is unavailable.
    /// <paramref name="meleeOnly"/> (set for melee-weapon humanoids) drops ranged/magic GCDs — a
    /// single long-range skill in the kit (SAM's Ogi Namikiri, DRG's Piercing Talon, RPR's Harpe)
    /// otherwise flips the whole enemy to ranged intent and it camps 8–9.5y out instead of engaging
    /// in melee like its weapon implies.
    /// </summary>
    public IReadOnlyList<NpcSkill> BuildKit(uint jobId, int levelCap, int maxSkills, bool meleeOnly = false,
        bool includeAoe = false)
    {
        if (jobId == 0 || maxSkills <= 0)
            return Array.Empty<NpcSkill>();

        var key = (jobId, levelCap, maxSkills, meleeOnly, includeAoe);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var kit = BuildKitUncached(jobId, levelCap, maxSkills, meleeOnly, includeAoe);
        cache[key] = kit;
        return kit;
    }

    private List<NpcSkill> BuildKitUncached(uint jobId, int levelCap, int maxSkills, bool meleeOnly, bool includeAoe)
    {
        var empty = new List<NpcSkill>();

        var jobSheet = dataManager.GetExcelSheet<ClassJob>();
        var actionSheet = dataManager.GetExcelSheet<Action>();
        if (jobSheet == null || actionSheet == null)
            return empty;

        var jobRow = jobSheet.GetRowOrDefault(jobId);
        if (jobRow == null)
            return empty;

        // A job's kit spans its own category plus its base class (DRG also owns LNC's low-level
        // weaponskills), so match either abbreviation.
        var jobAbbr = jobRow.Value.Abbreviation.ExtractText();
        var baseAbbr = jobRow.Value.ClassJobParent.ValueNullable?.Abbreviation.ExtractText() ?? jobAbbr;
        if (string.IsNullOrEmpty(jobAbbr))
            return empty;

        var candidates = new List<(ActionData Data, byte Level)>();
        var aoeCandidates = new List<(ActionData Data, byte Level)>();
        foreach (var action in actionSheet)
        {
            if (!action.IsPlayerAction || action.IsRoleAction)
                continue;
            var lvl = action.ClassJobLevel;
            if (lvl < 1 || lvl > levelCap) // lvl 0 = PvP/shared duplicates
                continue;
            if (includeAoe && IsSelfCentredDamageAoe(action))
            {
                // PBAoEs (Overpower, Total Eclipse, Holy) target the caster, so the hostile-target
                // test below drops them; they are only offered for the AoE slot.
                if (action.ClassJobCategory.ValueNullable is { } aoeCjc &&
                    (CategoryHasJob(aoeCjc, jobAbbr) || CategoryHasJob(aoeCjc, baseAbbr)) &&
                    actionDataProvider.GetActionData(action.RowId) is { Potency: > 0 } aoeData &&
                    !(meleeOnly && (aoeData.DamageType == SimDamageType.Magical || aoeData.Range > 5f)))
                    aoeCandidates.Add((aoeData, lvl));
                continue;
            }
            if (action.AttackType.RowId == 0 || !action.CanTargetHostile)
                continue; // non-damage / can't be aimed at the player
            var category = action.ActionCategory.RowId;
            if (category != ActionCategorySpell && category != ActionCategoryWeaponskill)
                continue; // GCD damage only — drops utility oGCDs that leak past the attack-type test
            if (action.ClassJobCategory.ValueNullable is not { } cjc)
                continue;
            if (!CategoryHasJob(cjc, jobAbbr) && !CategoryHasJob(cjc, baseAbbr))
                continue;

            var data = actionDataProvider.GetActionData(action.RowId);
            if (data == null || data.Intent != ActionIntent.Damage || data.Potency <= 0)
                continue;
            // Melee-weapon humanoids keep a pure melee kit (see BuildKit doc).
            if (meleeOnly && (data.DamageType == SimDamageType.Magical || data.Range > 5f))
                continue;

            candidates.Add((data, lvl));
        }

        var deduped = candidates
            .GroupBy(c => c.Data.ActionId)
            .Select(g => g.First())
            .OrderByDescending(c => c.Data.Potency)
            .ThenByDescending(c => c.Level)
            .ToList();

        // Prefer standard 2.5s-recast GCDs — the spammable core rotation — so the enemy keeps
        // pressing attacks instead of leading with a long-cooldown burst weaponskill (Gnashing Fang
        // 30s, Air Anchor 40s…). Longer-recast actions only fill remaining slots if a job has fewer
        // than N GCDs (low level / small kit), so we never return empty.
        const float gcdRecast = 2.55f;
        var picked = deduped.Where(c => c.Data.RecastTime <= gcdRecast)
            .Concat(deduped.Where(c => c.Data.RecastTime > gcdRecast))
            .Take(maxSkills)
            .ToList();

        // AoE slot: ranking by potency keeps the single-target core, which on most jobs leaves
        // no area at all. Guarantee one when the job has one — the newest GCD AoE, replacing the
        // weakest pick when the kit is full.
        if (includeAoe && !picked.Any(c => IsArea(c.Data)))
        {
            var aoe = deduped.Where(c => IsArea(c.Data))
                .Concat(aoeCandidates.GroupBy(c => c.Data.ActionId).Select(g => g.First()))
                .OrderBy(c => c.Data.RecastTime > gcdRecast)
                .ThenByDescending(c => c.Level)
                .ThenByDescending(c => c.Data.Potency)
                .Select(c => ((ActionData Data, byte Level)?)c)
                .FirstOrDefault();
            if (aoe is { } chosen)
            {
                if (picked.Count >= maxSkills)
                    picked[^1] = chosen;
                else
                    picked.Add(chosen);
            }
        }

        var kit = picked.Select((c, rank) => ToSkill(c.Data, rank)).ToList();

        log.Info(
            $"JobActionKitProvider: job={jobId}({jobAbbr}/{baseAbbr}) lvl≤{levelCap} → {kit.Count} skills: " +
            string.Join(", ", kit.Select(s => $"{s.Name}#{s.ActionId}(p{s.Potency})")));
        return kit;
    }

    private static bool IsArea(ActionData data) => data.Shape != AoeShape.Single && data.Radius > 0f;

    /// <summary>
    /// A GCD damage AoE centred on the caster. The sheet has no damage flag for self-targeted
    /// actions; NeedToFaceTarget is what separates them from the self-targeted party heals
    /// (Medica, Helios, Succor…), measured over every player AoE GCD: 0 heals among the
    /// facing ones, 0 damage AoEs among the rest.
    /// </summary>
    private static bool IsSelfCentredDamageAoe(in Action action)
        => !action.CanTargetHostile &&
           action.Range == 0 &&
           action.EffectRange > 0 &&
           action.CastType is 2 or 3 or 5 or 10 &&
           action.AttackType.RowId != 0 &&
           action.NeedToFaceTarget &&
           action.ActionCategory.RowId is ActionCategorySpell or ActionCategoryWeaponskill;

    private static NpcSkill ToSkill(ActionData data, int rank)
    {
        var style = data.DamageType == SimDamageType.Magical
            ? NpcAttackStyle.Magic
            : data.Range > 5f
                ? NpcAttackStyle.Ranged
                : NpcAttackStyle.Melee;

        return new NpcSkill
        {
            ActionId = data.ActionId,
            Name = data.Name,
            Potency = data.Potency,
            // Enemies cast instantly. A real cast time becomes telegraph lead-in, and nothing plays
            // during lead-in — no circle, no animation, no VFX — so a 3.5s spell was 3 silent
            // seconds followed by damage appearing out of nowhere. Dropping it collapses the skill
            // into one readable burst where wind-up, circle, animation, VFX and damage line up.
            CastTime = 0f,
            // Kept aside for the telegraphed-casts option, which restores it only for skills whose
            // area can be drawn (so the lead-in is no longer silent).
            NativeCastTime = data.CastTime,
            Cooldown = MathF.Max(2.5f, data.RecastTime),
            Range = data.Range > 0f ? data.Range : 3f,
            Radius = data.Radius,
            AttackStyle = style,
            Priority = rank, // ranked strongest-first; the AI randomizes among ready skills
            HpThreshold = 1.0f,
        };
    }

    // The ClassJobCategory sheet exposes one bool column per job abbreviation. Map the abbreviation
    // to the column so we can test membership for an arbitrary resolved job.
    private static bool CategoryHasJob(in ClassJobCategory cjc, string abbr) => abbr switch
    {
        "GLA" => cjc.GLA, "PGL" => cjc.PGL, "MRD" => cjc.MRD, "LNC" => cjc.LNC,
        "ARC" => cjc.ARC, "CNJ" => cjc.CNJ, "THM" => cjc.THM, "ROG" => cjc.ROG,
        "ACN" => cjc.ACN,
        "PLD" => cjc.PLD, "MNK" => cjc.MNK, "WAR" => cjc.WAR, "DRG" => cjc.DRG,
        "BRD" => cjc.BRD, "WHM" => cjc.WHM, "BLM" => cjc.BLM, "SMN" => cjc.SMN,
        "SCH" => cjc.SCH, "NIN" => cjc.NIN, "MCH" => cjc.MCH, "DRK" => cjc.DRK,
        "AST" => cjc.AST, "SAM" => cjc.SAM, "RDM" => cjc.RDM, "BLU" => cjc.BLU,
        "GNB" => cjc.GNB, "DNC" => cjc.DNC, "RPR" => cjc.RPR, "SGE" => cjc.SGE,
        "VPR" => cjc.VPR, "PCT" => cjc.PCT,
        _ => false,
    };
}
