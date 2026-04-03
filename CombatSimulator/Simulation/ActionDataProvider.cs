using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Simulation;

public enum SimDamageType
{
    Physical,
    Magical,
    Unique,
}

public class ActionData
{
    public uint ActionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Potency { get; set; }
    public float CastTime { get; set; }
    public float RecastTime { get; set; }
    public int RecastGroup { get; set; }
    public float Range { get; set; }
    public float Radius { get; set; }
    public SimDamageType DamageType { get; set; }
    public int MpCost { get; set; }
    public bool IsComboAction { get; set; }
    public uint ComboFrom { get; set; }
    public int ComboPotency { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public bool IsPlayerAction { get; set; } = true;

    // VFX paths resolved from Lumina data + TMB files
    public string CastVfxPath { get; set; } = string.Empty;
    public string StartVfxPath { get; set; } = string.Empty;
    public List<string> CasterVfxPaths { get; set; } = new();
    public List<string> TargetVfxPaths { get; set; } = new();
}

public partial class ActionDataProvider
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, ActionData> cache = new();

    // Same regex VFXEditor uses to extract .avfx paths from TMB binary data
    [GeneratedRegex(@"\u0000([a-zA-Z0-9\/_]*?)\.avfx", RegexOptions.Compiled)]
    private static partial Regex AvfxPathRegex();

    public ActionDataProvider(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public ActionData? GetActionData(uint actionId)
    {
        if (cache.TryGetValue(actionId, out var cached))
            return cached;

        var sheet = dataManager.GetExcelSheet<Action>();
        if (sheet == null)
            return null;

        var row = sheet.GetRowOrDefault(actionId);
        if (row == null)
            return null;

        var action = row.Value;
        var data = new ActionData
        {
            ActionId = actionId,
            Name = action.Name.ExtractText(),
            CastTime = action.Cast100ms * 0.1f,
            RecastTime = action.Recast100ms * 0.1f,
            RecastGroup = action.CooldownGroup,
            Range = action.Range,
            Radius = action.EffectRange,
            MpCost = action.PrimaryCostValue,
            AnimationLock = 0.6f,
            IsPlayerAction = action.IsPlayerAction,
        };

        // Damage type from AttackType
        data.DamageType = action.AttackType.RowId switch
        {
            1 => SimDamageType.Physical, // Slashing
            2 => SimDamageType.Physical, // Piercing
            3 => SimDamageType.Physical, // Blunt
            4 => SimDamageType.Physical, // Shot
            5 => SimDamageType.Magical,  // Magic
            _ => SimDamageType.Unique,
        };

        // Potency: not directly in the sheet in a simple field;
        // we approximate from the action description or use defaults.
        // For MVP, use a reasonable default based on recast.
        data.Potency = EstimatePotency(data);

        // Resolve VFX paths (same approach as VFXEditor)
        ResolveVfxPaths(action, data);

        // Combo
        if (action.ActionCombo.RowId != 0)
        {
            data.IsComboAction = true;
            data.ComboFrom = action.ActionCombo.RowId;
            data.ComboPotency = (int)(data.Potency * 1.5f); // Combo bonus estimate
        }

        cache[actionId] = data;
        return data;
    }

    private static int EstimatePotency(ActionData data)
    {
        // Heuristic: GCD attacks have higher potency, oGCDs vary
        if (data.RecastTime >= 2.0f && data.RecastTime <= 3.0f)
        {
            // GCD action
            return data.CastTime > 0 ? 300 : 200;
        }

        if (data.RecastTime > 3.0f)
        {
            // oGCD with cooldown
            return (int)(data.RecastTime * 15); // Longer CD = higher potency
        }

        return 150; // Default
    }

    public void ClearCache()
    {
        cache.Clear();
    }

    /// <summary>
    /// Resolve VFX paths for an action using the same data chain as VFXEditor:
    /// 1. CastVfxPath: Action.VFX → ActionCastVFX.VFX → VFX.Location
    /// 2. StartVfxPath: Action.AnimationStart → ActionTimeline.VFX → VFX.Location
    /// 3. CasterVfxPaths: AnimationEnd TMB file → regex-extract embedded .avfx paths
    /// 4. TargetVfxPaths: ActionTimelineHit TMB file → regex-extract embedded .avfx paths
    /// </summary>
    private void ResolveVfxPaths(Action action, ActionData data)
    {
        try
        {
            // Cast VFX (casting circle / channeling effect)
            var castLoc = action.VFX.ValueNullable?.VFX.ValueNullable?.Location.ExtractText();
            if (!string.IsNullOrEmpty(castLoc))
                data.CastVfxPath = $"vfx/common/eff/{castLoc}.avfx";

            // Start VFX (effect when action begins)
            var startLoc = action.AnimationStart.ValueNullable?.VFX.ValueNullable?.Location.ExtractText();
            if (!string.IsNullOrEmpty(startLoc))
                data.StartVfxPath = $"vfx/common/eff/{startLoc}.avfx";

            // Caster VFX from AnimationEnd TMB (main skill effects)
            var endKey = action.AnimationEnd.ValueNullable?.Key.ExtractText();
            if (!string.IsNullOrEmpty(endKey) && !endKey.Contains("[SKL_ID]"))
                data.CasterVfxPaths = ExtractVfxFromTmb($"chara/action/{endKey}.tmb");

            // Target VFX from ActionTimelineHit TMB (hit/impact effects)
            var hitKey = action.ActionTimelineHit.ValueNullable?.Key.ExtractText();
            if (!string.IsNullOrEmpty(hitKey) && !hitKey.Contains("[SKL_ID]") && !hitKey.Contains("normal_hit"))
                data.TargetVfxPaths = ExtractVfxFromTmb($"chara/action/{hitKey}.tmb");

            log.Info($"[VFX] Action {data.ActionId} '{data.Name}': " +
                     $"cast='{data.CastVfxPath}', start='{data.StartVfxPath}', " +
                     $"casterTmb={data.CasterVfxPaths.Count} paths, targetTmb={data.TargetVfxPaths.Count} paths");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[VFX] Failed to resolve VFX paths for action {data.ActionId}");
        }
    }

    /// <summary>
    /// Read a TMB (timeline) binary file and extract all embedded .avfx paths via regex.
    /// This is the same approach VFXEditor uses (ParsedPaths + AvfxRegex).
    /// </summary>
    private List<string> ExtractVfxFromTmb(string tmbPath)
    {
        var paths = new List<string>();
        try
        {
            if (!dataManager.FileExists(tmbPath))
                return paths;

            var file = dataManager.GetFile(tmbPath);
            if (file?.Data == null)
                return paths;

            var text = Encoding.UTF8.GetString(file.Data);
            var matches = AvfxPathRegex().Matches(text);
            foreach (Match m in matches)
                paths.Add(m.Value.Trim('\0'));
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[VFX] Failed to parse TMB: {tmbPath}");
        }
        return paths;
    }
}
