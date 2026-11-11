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
/// The contract, per enemy tier: how many seconds a player needs to kill it at reference output, and
/// what share of their HP that fight costs them when they do nothing to avoid damage.
///   - Player damage per potency is set so reference output deals the player's own HP in
///     <see cref="SelfKillSeconds"/>. That makes numbers look right for the character on screen.
///   - Enemy HP is that output times the tier's time to kill.
///   - Enemy damage is the tier's cost spread over the enemy's own kit, potency for potency, so every kit
///     of a tier deals the same sustained damage and a big skill still hits harder than an auto-attack.
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

    /// <summary>Levels above the player that double an enemy's HP and its damage.</summary>
    public const float LevelsPerDoubling = 20f;
    public const float MaxLevelFactor = 4f;

    /// <summary>
    /// How much stronger an enemy is for being above the player's level (weaker for being below). It
    /// multiplies both its HP and its damage, so the fight is longer and every hit worse. 1 at equal
    /// levels; the player's own level and gear still do not matter, only the difference.
    /// </summary>
    public static float LevelFactor(int enemyLevel, int playerLevel)
        => Math.Clamp(MathF.Pow(2f, (enemyLevel - playerLevel) / LevelsPerDoubling), 1f / MaxLevelFactor, MaxLevelFactor);

    // (seconds to kill at reference output, share of the target's HP an unavoided fight costs)
    private static readonly (float SecondsToKill, float Cost)[] Tiers =
    {
        (4f, 0.05f),   // fodder
        (10f, 0.20f),  // the fair fight: three at once is lethal without guarding or help
        (20f, 0.50f),
        (35f, 0.85f),  // needs guarding or a companion
        (90f, 2.50f),  // cannot be out-raced
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

    /// <summary>In Action Mode a hit can be guarded, so an unguarded one costs this much more.</summary>
    public const float ActionModeThreat = 2f;
    /// <summary>The enemy attack pace Action Mode's normal difficulty runs at; kits are budgeted at it.</summary>
    public const float ActionModePace = 1.6f;

    /// <summary>A companion's sustained output as a share of the player's reference output.</summary>
    public const float CompanionShare = 0.5f;
    /// <summary>Ranged and caster enemies attack from where they cannot be answered, so they are fragile.</summary>
    public const float RangedHpShare = 0.5f;

    public const float CritChance = 0.15f;
    public const float CritBonus = 1.5f;
    public const float DirectHitChance = 0.15f;
    public const float DirectHitBonus = 1.25f;

    /// <summary>No single enemy hit takes more than this share of the target's HP.</summary>
    public const float MaxHitShare = 0.35f;
    public const float BossMaxHitShare = 0.5f;

    /// <summary>Output assumed for a kit that has not been measured yet: an auto-attack alone.</summary>
    public const float UnmeasuredKitOutput = 110f / 3f;

    // Enemy MP economy (NpcAiController spends it; the kit budget below predicts it).
    public const int EnemyMpRegenPerSecond = 134;
    public const int EnemyAutoAttackMpRestore = 600;
    private const float EnemySkillMpCostMinFraction = 0.15f;
    private const float EnemySkillMpCostMaxFraction = 0.40f;
    private const float ActionCostFloor = 1500f;
    private const float ActionCostCeiling = 8500f;
    private const float ActionLockSeconds = 0.6f; // every action holds the animation lock this long
    private const float KitStepSeconds = 0.05f;
    private const float CompanionKitSeconds = 12f; // about one fight: the opening cooldowns count

    public static EnemyTier ClampTier(int tier) => (EnemyTier)Math.Clamp(tier, 0, Tiers.Length - 1);

    public static float SecondsToKill(EnemyTier tier) => Tiers[(int)ClampTier((int)tier)].SecondsToKill;

    /// <summary>Share of the target's HP one such enemy costs over its whole fight, unavoided, in classic play.</summary>
    public static float FightCost(EnemyTier tier) => Tiers[(int)ClampTier((int)tier)].Cost;

    public static float ReferenceOutput(bool actionMode) => actionMode ? ActionModeOutput : ClassicOutput;

    /// <summary>Damage one point of potency does for the player.</summary>
    public static float PlayerDamagePerPotency(float playerMaxHp, bool actionMode)
        => MathF.Max(1f, playerMaxHp) / (SelfKillSeconds * ReferenceOutput(actionMode));

    /// <summary>
    /// Damage one point of potency does for a companion: its kit, whatever it is, sustains
    /// <see cref="CompanionShare"/> of the player's reference output.
    /// </summary>
    public static float CompanionDamagePerPotency(float companionMaxHp, float kitOutput)
        => CompanionShare * MathF.Max(1f, companionMaxHp) / (SelfKillSeconds * MathF.Max(1f, kitOutput));

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
    /// Share of the target's maximum HP one enemy attack removes. The tier's cost is paid out over the
    /// time the enemy is meant to live, in proportion to potency, against the output its kit sustains.
    /// </summary>
    public static float EnemyHitShare(int potency, EnemyTier tier, float kitOutput, bool actionMode, float levelFactor = 1f)
    {
        if (potency <= 0) return 0f;
        levelFactor = MathF.Max(0.0001f, levelFactor);
        var perPotency = FightCost(tier) * (actionMode ? ActionModeThreat : 1f) * levelFactor
                         / (SecondsToKill(tier) * MathF.Max(1f, kitOutput));
        // A higher-level enemy may hit past the usual cap, but never in one blow.
        var cap = (tier == EnemyTier.Boss ? BossMaxHitShare : MaxHitShare) * Math.Clamp(levelFactor, 1f, 2f);
        return MathF.Min(potency * perPotency, MathF.Min(cap, 0.9f));
    }

    /// <summary>What an enemy pays for a skill priced against the player's 10 000 MP pool.</summary>
    public static int EnemySkillMpCost(int playerPrice, int enemyMaxMp)
    {
        if (playerPrice <= 0) return 0;
        var t = Math.Clamp((playerPrice - ActionCostFloor) / (ActionCostCeiling - ActionCostFloor), 0f, 1f);
        var fraction = EnemySkillMpCostMinFraction + t * (EnemySkillMpCostMaxFraction - EnemySkillMpCostMinFraction);
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

    /// <summary>How long a companion's kit is played out for when it is measured.</summary>
    public static float CompanionKitWindow => CompanionKitSeconds;

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
