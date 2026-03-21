using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

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
}

public class ActionDataProvider
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, ActionData> cache = new();

    public ActionDataProvider(IDataManager dataManager)
    {
        this.dataManager = dataManager;
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
}
