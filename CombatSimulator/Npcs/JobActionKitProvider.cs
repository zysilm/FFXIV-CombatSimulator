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
    private readonly Dictionary<(uint Job, int Level, int Max), List<NpcSkill>> cache = new();

    public JobActionKitProvider(ActionDataProvider actionDataProvider, IDataManager dataManager, IPluginLog log)
    {
        this.actionDataProvider = actionDataProvider;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>
    /// The strongest <paramref name="maxSkills"/> damage actions for <paramref name="jobId"/> usable
    /// at <paramref name="levelCap"/>, ranked by estimated potency. Empty if the job/data is unavailable.
    /// </summary>
    public IReadOnlyList<NpcSkill> BuildKit(uint jobId, int levelCap, int maxSkills)
    {
        if (jobId == 0 || maxSkills <= 0)
            return Array.Empty<NpcSkill>();

        var key = (jobId, levelCap, maxSkills);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var kit = BuildKitUncached(jobId, levelCap, maxSkills);
        cache[key] = kit;
        return kit;
    }

    private List<NpcSkill> BuildKitUncached(uint jobId, int levelCap, int maxSkills)
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
        foreach (var action in actionSheet)
        {
            if (!action.IsPlayerAction || action.IsRoleAction)
                continue;
            var lvl = action.ClassJobLevel;
            if (lvl < 1 || lvl > levelCap) // lvl 0 = PvP/shared duplicates
                continue;
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

            candidates.Add((data, lvl));
        }

        var kit = candidates
            .GroupBy(c => c.Data.ActionId)
            .Select(g => g.First())
            .OrderByDescending(c => c.Data.Potency)
            .ThenByDescending(c => c.Level)
            .Take(maxSkills)
            .Select((c, rank) => ToSkill(c.Data, rank))
            .ToList();

        log.Info(
            $"JobActionKitProvider: job={jobId}({jobAbbr}/{baseAbbr}) lvl≤{levelCap} → {kit.Count} skills: " +
            string.Join(", ", kit.Select(s => $"{s.Name}#{s.ActionId}(p{s.Potency})")));
        return kit;
    }

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
            CastTime = data.CastTime,
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
