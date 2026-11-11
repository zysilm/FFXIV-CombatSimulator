// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Core;
using CombatSimulator.Simulation;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

/// <summary>Turns the strength and level options into what one enemy fights with.</summary>
internal static class EnemyStrength
{
    /// <summary>The chosen tier, or when none is forced, the one the game's own data gives this enemy.</summary>
    public static EnemyTier ResolveTier(EnemyTier? chosen, uint bNpcBaseId)
    {
        if (chosen is { } tier)
            return tier;
        if (bNpcBaseId == 0)
            return EnemyTier.Normal; // townspeople, players and bare models carry no rank
        var row = Services.DataManager.GetExcelSheet<BNpcBase>().GetRowOrDefault(bNpcBaseId);
        return row is { } bNpc ? CombatBudgetModel.TierForRank(bNpc.Rank) : EnemyTier.Normal;
    }

    /// <summary>The chosen level; 0 follows the player's own.</summary>
    public static int ResolveLevel(int chosen) => chosen > 0 ? chosen : PlayerLevel;

    public static float LevelFactor(int enemyLevel) => CombatBudgetModel.LevelFactor(enemyLevel, PlayerLevel);

    /// <summary>Enemy HP is sized from the player's own maximum HP (see CombatBudgetModel).</summary>
    public static int MaxHp(EnemyTier tier, bool fightsAtRange, int enemyLevel)
        => CombatBudgetModel.EnemyMaxHp(Services.ObjectTable.LocalPlayer?.MaxHp ?? 1000u, tier, fightsAtRange,
            LevelFactor(enemyLevel));

    private static int PlayerLevel => Services.ObjectTable.LocalPlayer?.Level ?? 1;
}
