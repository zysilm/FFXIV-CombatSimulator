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

/// <summary>
/// Geometry of an action's effect area, derived from the game's CastType column.
/// Single is the safe default for anything we don't recognise.
/// </summary>
public enum AoeShape
{
    Single,        // CastType 1 — one target only
    Circle,        // CastType 2 — circle centred on the target
    Cone,          // CastType 3 — cone from the caster toward the target
    Line,          // CastType 4 — line/rectangle from the caster (width = XAxisModifier)
    CircleSelf,    // CastType 5 — circle centred on the caster (PBAoE)
    GroundCircle,  // CastType 7 — circle placed at a ground location
    Donut,         // CastType 10 — ring centred on the caster
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
    public AoeShape Shape { get; set; } = AoeShape.Single;
    public float Width { get; set; }
    public SimDamageType DamageType { get; set; }
    public int NativeMpCost { get; set; }
    public int MpCost { get; set; }
    public bool IsComboAction { get; set; }
    public uint ComboFrom { get; set; }
    public int ComboPotency { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public float AnimationDuration { get; set; }
    public bool IsPlayerAction { get; set; } = true;
    public ushort AnimationStartTimelineId { get; set; }
    public ushort AnimationEndTimelineId { get; set; }

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
    private readonly Configuration config;
    private readonly ActionDatabaseProvider actionDb;
    private readonly Dictionary<uint, ActionData> cache = new();
    private int cachedBasicPotency;

    // Same regex VFXEditor uses to extract .avfx paths from TMB binary data
    [GeneratedRegex(@"\u0000([a-zA-Z0-9\/_]*?)\.avfx", RegexOptions.Compiled)]
    private static partial Regex AvfxPathRegex();

    public ActionDataProvider(IDataManager dataManager, IPluginLog log, Configuration config)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.config = config;
        actionDb = new ActionDatabaseProvider(log);
    }

    /// <summary>
    /// Maps the game's CastType column to our simplified effect-area shape.
    /// Unknown values collapse to Single so we never accidentally widen an
    /// action into an AoE we don't understand.
    /// </summary>
    public static AoeShape MapCastType(byte castType) => castType switch
    {
        1 => AoeShape.Single,
        2 => AoeShape.Circle,
        3 => AoeShape.Cone,
        4 => AoeShape.Line,
        5 => AoeShape.CircleSelf,
        7 => AoeShape.GroundCircle,
        10 => AoeShape.Donut,
        _ => AoeShape.Single,
    };

    /// <summary>
    /// Classify an action's damage as physical or magical from the AttackType row
    /// id. Row 5 is magic; rows 1, 2, and 3 are physical, row 8 is limit break,
    /// and unset is uint.MaxValue for weaponskills
    /// that inherit their weapon's type). Row-id based, not name based, because the
    /// AttackType names are localized, so classification must not compare their
    /// display text. Everything that isn't row 5 is treated as physical, which is
    /// correct for weaponskills and harmless for the rest.
    /// </summary>
    public static SimDamageType ClassifyDamageType(uint attackTypeRowId)
        => attackTypeRowId == 5 ? SimDamageType.Magical : SimDamageType.Physical;

    public ActionData? GetActionData(uint actionId)
    {
        if (cachedBasicPotency != config.LightAttackPotency)
        {
            cache.Clear();
            cachedBasicPotency = config.LightAttackPotency;
        }

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
            Shape = MapCastType(action.CastType),
            Width = action.XAxisModifier,
            NativeMpCost = action.PrimaryCostValue,
            MpCost = action.PrimaryCostValue,
            AnimationLock = 0.6f,
            IsPlayerAction = action.IsPlayerAction,
            AnimationStartTimelineId = (ushort)action.AnimationStart.RowId,
            AnimationEndTimelineId = (ushort)action.AnimationEnd.RowId,
        };

        // Damage type from the AttackType row id (physical vs magical)
        data.DamageType = ClassifyDamageType(action.AttackType.RowId);

        // Resolve VFX paths (same approach as VFXEditor)
        ResolveVfxPaths(action, data);

        // Combo (from Lumina; JSON may override below)
        if (action.ActionCombo.RowId != 0)
        {
            data.IsComboAction = true;
            data.ComboFrom = action.ActionCombo.RowId;
            data.ComboPotency = (int)(data.Potency * 1.5f);
        }

        // Overlay curated/authored geometry from Actions.json. Potency is deliberately not read
        // from the file anymore; the virtual action model owns damage and MP pricing globally.
        ApplyDatabaseOverride(actionId, data);

        // Combat potency is not reliably exposed by the client sheets. Use the action-game
        // model for both MP price and damage potency after geometry overrides are applied.
        VirtualActionModel.Apply(data, config.LightAttackPotency);

        cache[actionId] = data;
        return data;
    }

    private void ApplyDatabaseOverride(uint actionId, ActionData data)
    {
        if (!actionDb.TryGet(actionId, out var e))
            return;

        if (!string.IsNullOrWhiteSpace(e.Name))
            data.Name = e.Name!;
        if (e.Range.HasValue)
            data.Range = e.Range.Value;
        if (e.Shape.HasValue)
            data.Shape = e.Shape.Value;
        if (e.Radius.HasValue)
            data.Radius = e.Radius.Value;
        if (e.Width.HasValue)
            data.Width = e.Width.Value;
        if (e.DamageType.HasValue)
            data.DamageType = e.DamageType.Value;
        if (e.ComboFrom.HasValue && e.ComboFrom.Value != 0)
        {
            data.IsComboAction = true;
            data.ComboFrom = e.ComboFrom.Value;
        }
    }

    public void ClearCache()
    {
        cache.Clear();
    }

    /// <summary>
    /// Re-read Actions.json (e.g. after an export or a hand edit) and drop the
    /// per-action cache so the new values take effect immediately.
    /// </summary>
    public void ReloadDatabase()
    {
        actionDb.Reload();
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
