using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Simulation;

public readonly record struct LevelModifiers(int Main, int Sub, int Div);

public class CombatStatProvider
{
    private readonly IDataManager? dataManager;
    private readonly IPluginLog? log;
    private readonly Dictionary<int, LevelModifiers> levelCache = new();
    private readonly Dictionary<uint, int> jobModifierCache = new();

    private static readonly (int level, LevelModifiers mods)[] FallbackLevelMods =
    {
        (1, new LevelModifiers(20, 56, 56)),
        (50, new LevelModifiers(202, 341, 341)),
        (60, new LevelModifiers(218, 354, 600)),
        (70, new LevelModifiers(292, 364, 900)),
        (80, new LevelModifiers(340, 380, 1900)),
        (90, new LevelModifiers(390, 400, 1900)),
        (100, new LevelModifiers(440, 420, 2780)),
    };

    public CombatStatProvider(IDataManager? dataManager = null, IPluginLog? log = null)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public LevelModifiers GetLevelModifiers(int level)
    {
        level = Math.Clamp(level, 1, 200);
        if (levelCache.TryGetValue(level, out var cached))
            return cached;

        if (TryReadLuminaLevelModifiers(level, out var mods))
        {
            levelCache[level] = mods;
            return mods;
        }

        mods = InterpolateFallback(level);
        levelCache[level] = mods;
        return mods;
    }

    public int GetPrimaryJobModifier(uint classJobId, SimDamageType damageType)
    {
        if (classJobId == 0)
            return 100;
        if (jobModifierCache.TryGetValue(classJobId, out var cached))
            return cached;

        var modifier = TryReadClassJobModifier(classJobId, damageType, out var value)
            ? value
            : 100;
        jobModifierCache[classJobId] = modifier;
        return modifier;
    }

    private bool TryReadLuminaLevelModifiers(int level, out LevelModifiers mods)
    {
        mods = default;
        if (dataManager == null)
            return false;

        try
        {
            var sheet = dataManager.GetExcelSheet<ParamGrow>();
            var row = sheet.GetRowOrDefault((uint)level);
            if (row == null)
                return false;

            var boxed = row.Value;

            // Lumina sheet shape can drift. Prefer explicit fields if present,
            // then fall back to array-like BaseParam collections.
            if (TryReadInt(boxed, out var main, "Main", "BaseMain", "MainStat", "BaseParamMain") &&
                TryReadInt(boxed, out var sub, "Sub", "BaseSub", "SubStat", "BaseParamSub") &&
                TryReadInt(boxed, out var div, "Div", "Divisor", "LevelDiv", "BaseParamDiv"))
            {
                mods = new LevelModifiers(main, sub, div);
                return IsUsable(mods);
            }

            if (TryReadIndexedInt(boxed, "BaseParam", 1, out main) &&
                TryReadIndexedInt(boxed, "BaseParam", 2, out sub) &&
                TryReadIndexedInt(boxed, "BaseParam", 3, out div))
            {
                mods = new LevelModifiers(main, sub, div);
                return IsUsable(mods);
            }
        }
        catch (Exception ex)
        {
            log?.Verbose($"CombatStatProvider: ParamGrow read failed, using fallback level mods ({ex.Message})");
        }

        return false;
    }

    private bool TryReadClassJobModifier(uint classJobId, SimDamageType damageType, out int modifier)
    {
        modifier = 100;
        if (dataManager == null)
            return false;

        try
        {
            var sheet = dataManager.GetExcelSheet<ClassJob>();
            var row = sheet.GetRowOrDefault(classJobId);
            if (row == null)
                return false;

            var boxed = row.Value;
            var names = damageType == SimDamageType.Magical
                ? new[] { "ModifierIntelligence", "ModifierMind", "Intelligence", "Mind", "Int", "Mnd" }
                : new[] { "ModifierStrength", "ModifierDexterity", "Strength", "Dexterity", "Str", "Dex" };

            foreach (var name in names)
            {
                if (!TryReadInt(boxed, out var value, name) || value <= 0)
                    continue;
                modifier = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            log?.Verbose($"CombatStatProvider: ClassJob read failed, using 100 modifier ({ex.Message})");
        }

        return false;
    }

    private static bool TryReadInt(object row, out int value, params string[] names)
    {
        value = 0;
        var type = row.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null)
                continue;

            var raw = prop.GetValue(row);
            if (TryConvertInt(raw, out value))
                return true;
        }

        return false;
    }

    private static bool TryReadIndexedInt(object row, string propertyName, int index, out int value)
    {
        value = 0;
        var prop = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        var raw = prop?.GetValue(row);
        if (raw == null)
            return false;

        var indexer = raw.GetType().GetProperty("Item", new[] { typeof(int) });
        if (indexer != null && TryConvertInt(indexer.GetValue(raw, new object[] { index }), out value))
            return true;

        if (raw is System.Collections.IList list && index >= 0 && index < list.Count)
            return TryConvertInt(list[index], out value);

        return false;
    }

    private static bool TryConvertInt(object? raw, out int value)
    {
        value = 0;
        if (raw == null)
            return false;

        try
        {
            value = Convert.ToInt32(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsable(LevelModifiers mods) => mods.Main > 0 && mods.Sub > 0 && mods.Div > 0;

    private static LevelModifiers InterpolateFallback(int level)
    {
        if (level <= FallbackLevelMods[0].level)
            return FallbackLevelMods[0].mods;

        for (var i = 0; i < FallbackLevelMods.Length - 1; i++)
        {
            var (fromLevel, from) = FallbackLevelMods[i];
            var (toLevel, to) = FallbackLevelMods[i + 1];
            if (level > toLevel)
                continue;

            var t = (float)(level - fromLevel) / (toLevel - fromLevel);
            return new LevelModifiers(
                (int)(from.Main + (to.Main - from.Main) * t),
                (int)(from.Sub + (to.Sub - from.Sub) * t),
                (int)(from.Div + (to.Div - from.Div) * t));
        }

        var lv90 = FallbackLevelMods[^2].mods;
        var lv100 = FallbackLevelMods[^1].mods;
        var extrapolated = (float)(level - 90) / 10;
        return new LevelModifiers(
            (int)(lv90.Main + (lv100.Main - lv90.Main) * extrapolated),
            (int)(lv90.Sub + (lv100.Sub - lv90.Sub) * extrapolated),
            (int)(lv90.Div + (lv100.Div - lv90.Div) * extrapolated));
    }
}
