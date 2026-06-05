using System;

namespace CombatSimulator.Npcs;

public static class NpcHpCalculator
{
    private static readonly (int Level, int Hp)[] NormalEnemyAnchors =
    {
        (1, 120),
        (5, 270),
        (10, 450),
        (15, 725),
        (20, 1_000),
        (25, 1_400),
        (30, 1_800),
        (35, 2_400),
        (40, 3_000),
        (45, 4_000),
        (50, 5_000),
        (55, 7_000),
        (60, 9_000),
        (65, 12_000),
        (70, 15_000),
        (75, 21_000),
        (80, 27_000),
        (85, 38_500),
        (90, 50_000),
        (95, 67_500),
        (100, 85_000),
    };

    public static int CalculateNormalEnemyHp(int level, float multiplier)
    {
        var clampedLevel = Math.Max(1, level);
        var baseHp = InterpolateNormalEnemyHp(clampedLevel);
        return Math.Max(1, (int)MathF.Round(baseHp * Math.Max(0.0001f, multiplier)));
    }

    private static float InterpolateNormalEnemyHp(int level)
    {
        if (level <= NormalEnemyAnchors[0].Level)
            return NormalEnemyAnchors[0].Hp;

        for (var i = 1; i < NormalEnemyAnchors.Length; i++)
        {
            var lower = NormalEnemyAnchors[i - 1];
            var upper = NormalEnemyAnchors[i];
            if (level > upper.Level)
                continue;

            var t = (float)(level - lower.Level) / (upper.Level - lower.Level);
            return lower.Hp + (upper.Hp - lower.Hp) * t;
        }

        var last = NormalEnemyAnchors[^1];
        var previous = NormalEnemyAnchors[^2];
        var hpPerLevel = (last.Hp - previous.Hp) / (float)(last.Level - previous.Level);
        return last.Hp + (level - last.Level) * hpPerLevel;
    }
}
