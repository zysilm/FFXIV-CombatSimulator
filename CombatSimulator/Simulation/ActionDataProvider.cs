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
/// What an action is FOR, derived from the game's targeting/role columns. Enemies only ever
/// cast <see cref="Damage"/> actions; <see cref="Support"/> (buffs/heals/utility, incl. role
/// actions) and <see cref="Raise"/> are excluded from the enemy kit. Splitting Support further
/// into buff vs heal is deferred (needs StatusGainSelf/cure data we don't yet consume).
/// </summary>
public enum ActionIntent
{
    Damage,
    Support,
    Raise,
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
    public ActionIntent Intent { get; set; } = ActionIntent.Damage;
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

    // Impact effects end their file name with a {t}{index} marker (mgc_ston1t0h, wsw_ws_s10t4m),
    // caster effects with {c}{index} (mgc_ff015_c0v, wsp_true1c0m); the index count tracks the
    // number of hits. Anchored at the tail so an incidental "t<digit>" earlier in the name
    // (cmws_shoot1c) doesn't trip it.
    [GeneratedRegex(@"t\d[a-z0-9]{0,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TargetSideVfxRegex();

    /// <summary>
    /// Whether an .avfx extracted from an action timeline belongs on the TARGET rather than the
    /// caster. Generic common/camera effects (cast charges, weapon release, screen shake) are
    /// always caster-side and are excluded before the name marker is consulted.
    /// </summary>
    private static bool IsTargetSideVfx(string path)
    {
        if (path.StartsWith("vfx/common/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("vfx/camera/", StringComparison.OrdinalIgnoreCase))
            return false;

        var slash = path.LastIndexOf('/');
        var file = slash >= 0 ? path[(slash + 1)..] : path;
        if (file.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            file = file[..^5];

        return TargetSideVfxRegex().IsMatch(file);
    }

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

    /// <summary>
    /// Classify an action's purpose from the game's targeting/role columns. Used both to tag
    /// <see cref="ActionData.Intent"/> and to filter an enemy's real job kit down to castable
    /// damage actions. Note AttackType is <c>-1</c> (uint.MaxValue) for physical weaponskills
    /// (they inherit the weapon's type), so the test is "has an attack type" = RowId != 0.
    /// Role actions (Second Wind, Leg Sweep, Feint, …) are physical-typed but functionally
    /// utility, so <paramref name="isRoleAction"/> demotes them out of Damage.
    /// </summary>
    public static ActionIntent ClassifyIntent(bool isRoleAction, uint attackTypeRowId, bool canTargetHostile, sbyte deadTargetBehaviour)
    {
        if (deadTargetBehaviour == 1)
            return ActionIntent.Raise; // "can only target dead" ⇒ resurrection
        if (attackTypeRowId != 0 && canTargetHostile && !isRoleAction)
            return ActionIntent.Damage;
        return ActionIntent.Support; // buffs, heals, role utility, self-targeted, etc.
    }

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

        // Purpose (damage vs support vs raise) from targeting/role columns.
        data.Intent = ClassifyIntent(
            action.IsRoleAction, action.AttackType.RowId, action.CanTargetHostile, action.DeadTargetBehaviour);

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

            // The AnimationEnd timeline carries BOTH halves of a skill: the caster-side effect AND
            // the impact effect that belongs ON THE TARGET, distinguished by a {c|t}{index} marker
            // in the file name (mgc_ff015_c0v vs mgc_ff015_t0v). Treating the whole list as
            // caster-side detonated a spell on its own caster: harmless-looking for melee, which
            // stands next to its target, but several spells (Fire III/IV, Blizzard III, Stone,
            // Aero) have target-side entries ONLY, so nothing correct rendered at all.
            var endKey = action.AnimationEnd.ValueNullable?.Key.ExtractText();
            if (!string.IsNullOrEmpty(endKey) && !endKey.Contains("[SKL_ID]"))
            {
                foreach (var path in ExtractVfxFromTmb($"chara/action/{endKey}.tmb"))
                {
                    if (IsTargetSideVfx(path))
                        data.TargetVfxPaths.Add(path);
                    else
                        data.CasterVfxPaths.Add(path);
                }
            }

            // Target VFX from ActionTimelineHit TMB (hit/impact effects)
            var hitKey = action.ActionTimelineHit.ValueNullable?.Key.ExtractText();
            if (!string.IsNullOrEmpty(hitKey) && !hitKey.Contains("[SKL_ID]") && !hitKey.Contains("normal_hit"))
                data.TargetVfxPaths.AddRange(ExtractVfxFromTmb($"chara/action/{hitKey}.tmb"));

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
