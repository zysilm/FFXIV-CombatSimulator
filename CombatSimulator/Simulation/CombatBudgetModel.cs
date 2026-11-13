// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>How strong an enemy is. Replaces the enemy level, which changed three things at once.</summary>
public enum EnemyTier
{
    Weak,
    Normal,
    Strong,
    Elite,
    Boss,
}

/// <summary>One skill of an enemy's or companion's kit, as far as its sustained output is concerned.</summary>
public readonly record struct KitSkill(int Potency, float Cooldown, int MpCost, bool HpGated, float CastTime = 0f);

/// <summary>
/// The combat numbers. A fight is decided in potency per second, never in character stats: gear, level
/// and job do not enter it, so a naked level 15 character and a best-in-slot level 100 one have the same
/// fight. The only thing read from the character is its maximum HP, and only to size the numbers shown;
/// it cancels out of every outcome.
///
/// One set of numbers for everyone: the player, allies and enemies.
///   - A point of potency does the same damage whoever spends it (<see cref="DamagePerPotency"/>):
///     reference output deals the player's own HP in <see cref="SelfKillSeconds"/>. A kit with bigger
///     skills hits harder, from either side.
///   - A skill costs everyone the same share of their own MP (<see cref="EnemySkillMpCost"/>).
///   - What sets characters apart is HP. An enemy's tier is how many seconds the player needs to kill it
///     at reference output; a Normal one has about the player's HP, so it is an even duel.
///   - Level is the game's: a level gap scales an enemy's HP and its damage by what the gap is worth.
/// </summary>
public static class CombatBudgetModel
{
    public static readonly string[] TierLabels = { "Weak", "Normal", "Strong", "Elite", "Boss" };

    /// <summary>The strength choices offered: detect it from the enemy, or force one tier.</summary>
    public static readonly string[] StrengthChoices = { "Auto", "Weak", "Normal", "Strong", "Elite", "Boss" };

    /// <summary>The tier a strength choice forces, or null when it is left to detection.</summary>
    public static EnemyTier? TierForChoice(int choice) => choice <= 0 ? null : ClampTier(choice - 1);

    /// <summary>
    /// Tier of a game enemy from the rank in its data row: 0 is ordinary, 1 a tougher or notorious
    /// one, 2 a boss; the remaining ranks are the special marks, taken as elite.
    /// </summary>
    public static EnemyTier TierForRank(int rank) => rank switch
    {
        <= 0 => EnemyTier.Normal,
        1 => EnemyTier.Strong,
        2 => EnemyTier.Boss,
        _ => EnemyTier.Elite,
    };

    // What a character of a level is worth, as the HP a typically equipped one has: the game's own
    // growth, which is slow to 50 and then steep. Only ratios of it are used.
    private static readonly (int Level, float Hp)[] LevelCurve =
    {
        (1, 150f), (10, 400f), (20, 800f), (30, 1_500f), (40, 2_600f), (50, 4_500f),
        (60, 15_000f), (70, 30_000f), (80, 55_000f), (90, 80_000f), (100, 160_000f),
    };

    /// <summary>The worth of a level on the curve above, interpolated on a logarithmic scale.</summary>
    public static float LevelWorth(int level)
    {
        level = Math.Clamp(level, LevelCurve[0].Level, LevelCurve[^1].Level);
        for (var i = 1; i < LevelCurve.Length; i++)
        {
            if (level > LevelCurve[i].Level) continue;
            var (fromLevel, fromHp) = LevelCurve[i - 1];
            var (toLevel, toHp) = LevelCurve[i];
            var t = (level - fromLevel) / (float)(toLevel - fromLevel);
            return fromHp * MathF.Pow(toHp / fromHp, t);
        }
        return LevelCurve[^1].Hp;
    }

    /// <summary>
    /// How much stronger an enemy is for being above the player's level (weaker for being below): what
    /// its level is worth against the player's, as in the game itself, where a level 100 enemy has
    /// hundreds of times the HP of a level 15 one and hits accordingly. It multiplies both the
    /// enemy's HP and its damage. 1 at equal levels, whatever the level, so gear and the player's own
    /// level still do not matter; only the gap does, and a large gap is not a fight.
    /// </summary>
    public static float LevelFactor(int enemyLevel, int playerLevel)
        => LevelWorth(enemyLevel) / LevelWorth(playerLevel);

    // Seconds the player needs to kill one at reference output: the tier is its HP, nothing else.
    private static readonly float[] Tiers =
    {
        4f,    // fodder
        10f,   // about the player's own HP: an even duel
        20f,
        35f,
        90f,
    };

    /// <summary>Seconds of reference output that equal the attacker's own maximum HP. Display scale only.</summary>
    public const float SelfKillSeconds = 11f;

    /// <summary>
    /// Potency per second a player sustains. Action Mode swings a 120-potency basic attack every 0.4 s
    /// and funds skills from the MP those swings return; classic play has a 110-potency auto-attack every
    /// 2.56 s and funds skills from regeneration and the opening MP pool alone.
    /// </summary>
    public const float ActionModeOutput = 330f;
    public const float ClassicOutput = 110f;

    /// <summary>The enemy attack pace Action Mode's normal difficulty runs at.</summary>
    public const float ActionModePace = 1.6f;

    /// <summary>Ranged and caster enemies attack from where they cannot be answered, so they are fragile.</summary>
    public const float RangedHpShare = 0.5f;

    public const float CritChance = 0.15f;
    public const float CritBonus = 1.5f;
    public const float DirectHitChance = 0.15f;
    public const float DirectHitBonus = 1.25f;

    /// <summary>Output assumed for a kit with nothing in it: an auto-attack alone.</summary>
    public const float UnmeasuredKitOutput = 110f / 3f;

    // MP economy of enemies and allies (NpcAiController and CombatCompanionManager spend it).
    public const int EnemyMpRegenPerSecond = 134;
    public const int EnemyAutoAttackMpRestore = 600;
    private const float PlayerMpPool = 10000f;
    private const float ActionLockSeconds = 0.6f; // every action holds the animation lock this long
    private const float KitStepSeconds = 0.05f;

    public static EnemyTier ClampTier(int tier) => (EnemyTier)Math.Clamp(tier, 0, Tiers.Length - 1);

    public static float SecondsToKill(EnemyTier tier) => Tiers[(int)ClampTier((int)tier)];

    public static float ReferenceOutput(bool actionMode) => actionMode ? ActionModeOutput : ClassicOutput;

    /// <summary>Damage one point of potency does for the player.</summary>
    public static float PlayerDamagePerPotency(float playerMaxHp, bool actionMode)
        => MathF.Max(1f, playerMaxHp) / (SelfKillSeconds * ReferenceOutput(actionMode));

    /// <summary>
    /// Damage one point of potency does for anyone: the player's own rate (sized by the player's HP),
    /// times what the attacker's level is worth against the player's (1 at equal levels). The same skill
    /// hits as hard from an enemy or an ally as from the player.
    /// </summary>
    public static float DamagePerPotency(float playerMaxHp, bool actionMode, float levelFactor = 1f)
        => PlayerDamagePerPotency(playerMaxHp, actionMode) * MathF.Max(0.0001f, levelFactor);

    /// <summary>
    /// Maximum HP of an enemy. Independent of the play mode: the player's damage per potency carries the
    /// mode, so the same enemy takes the same time to kill either way.
    /// </summary>
    public static int EnemyMaxHp(float playerMaxHp, EnemyTier tier, bool ranged, float levelFactor = 1f)
    {
        var hp = MathF.Max(1f, playerMaxHp) * SecondsToKill(tier) / SelfKillSeconds
                 * (ranged ? RangedHpShare : 1f) * MathF.Max(0.0001f, levelFactor);
        return (int)Math.Clamp(MathF.Round(hp), 1f, int.MaxValue);
    }

    /// <summary>
    /// What an enemy or an ally pays for a skill priced against the player's 10 000 MP pool: the same
    /// share of its own pool as the player pays of theirs. A spell that empties most of the player's MP
    /// empties most of theirs.
    /// </summary>
    public static int EnemySkillMpCost(int playerPrice, int enemyMaxMp)
    {
        if (playerPrice <= 0) return 0;
        var fraction = Math.Clamp(playerPrice / PlayerMpPool, 0f, 1f);
        return Math.Max(1, (int)MathF.Round(MathF.Max(1f, enemyMaxMp) * fraction));
    }

    /// <summary>
    /// Potency per second a kit delivers over a fight of the given length. Found by playing the kit out
    /// with the rules the AI follows: the animation lock after every action, cooldowns, cast times, and
    /// for enemies the MP pool it starts with, regenerates and earns back from auto-attacks. Skills gated
    /// behind an HP threshold (phase skills, enrage) are left out on purpose, so they are real escalation
    /// on top of the tier's cost.
    /// </summary>
    /// <param name="pace">Attack pace: 1 in classic play, the Action Mode pace otherwise.</param>
    /// <param name="usesMp">False for kits limited by cooldowns alone (companions).</param>
    /// <param name="castsBlock">True in classic play, where a cast holds the enemy until it completes.</param>
    public static float KitOutput(int autoPotency, float autoDelay, ReadOnlySpan<KitSkill> skills, float pace,
        bool usesMp, int maxMp, float seconds, bool castsBlock)
    {
        pace = MathF.Max(0.1f, pace);
        seconds = Math.Clamp(seconds, 2f, 600f);
        var hasAuto = autoPotency > 0 && autoDelay > 0f && autoDelay < 1e6f;
        Span<float> cooldown = skills.Length <= 64 ? stackalloc float[skills.Length] : new float[skills.Length];
        cooldown.Clear();

        float mp = maxMp, autoTimer = hasAuto ? autoDelay / pace * 0.5f : float.MaxValue, busy = 0f, potency = 0f;
        var next = 0;
        for (var time = 0f; time < seconds; time += KitStepSeconds)
        {
            if (usesMp) mp = MathF.Min(maxMp, mp + EnemyMpRegenPerSecond * KitStepSeconds);
            for (var i = 0; i < cooldown.Length; i++) cooldown[i] -= KitStepSeconds;
            busy -= KitStepSeconds;
            if (busy > 0f) continue;

            // A ready, affordable skill goes first; the AI picks among them at random, this takes turns.
            var fired = false;
            for (var offset = 0; offset < skills.Length && !fired; offset++)
            {
                var i = (next + offset) % skills.Length;
                var skill = skills[i];
                if (skill.HpGated || skill.Potency <= 0 || cooldown[i] > 0f || (usesMp && mp < skill.MpCost)) continue;
                if (usesMp) mp -= skill.MpCost;
                var cast = castsBlock ? MathF.Max(0f, skill.CastTime) : 0f;
                cooldown[i] = cast + MathF.Max(0.05f, skill.Cooldown) / pace;
                busy = cast + ActionLockSeconds;
                potency += skill.Potency;
                next = i + 1;
                fired = true;
            }
            if (fired || !hasAuto) continue;

            // The auto-attack timer only runs while nothing else is happening.
            autoTimer -= KitStepSeconds;
            if (autoTimer > 0f) continue;
            autoTimer = autoDelay / pace;
            busy = ActionLockSeconds;
            potency += autoPotency;
            if (usesMp) mp = MathF.Min(maxMp, mp + EnemyAutoAttackMpRestore);
        }

        var output = potency / seconds;
        return output > 1f ? output : UnmeasuredKitOutput * pace;
    }

    /// <summary>
    /// Critical and direct hits. The base is divided by their expected gain, so switching them on or off
    /// changes how much hits vary and not how much damage is dealt on average.
    /// </summary>
    public static float RollModifier(double critRoll, double directHitRoll, bool enableCrit, bool enableDirectHit,
        out bool isCritical, out bool isDirectHit)
    {
        isCritical = enableCrit && critRoll < CritChance;
        isDirectHit = enableDirectHit && directHitRoll < DirectHitChance;
        var modifier = 1f;
        if (enableCrit)
            modifier *= (isCritical ? CritBonus : 1f) / (1f + CritChance * (CritBonus - 1f));
        if (enableDirectHit)
            modifier *= (isDirectHit ? DirectHitBonus : 1f) / (1f + DirectHitChance * (DirectHitBonus - 1f));
        return modifier;
    }
}
