// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Combat;

/// <summary>
/// How a side measures its reach (docs/PLUGIN_ARCHITECTURE.md §2.3). Enemies scale every range by the
/// settings (NpcAttackRangeScale, and NpcScale for melee) and gate skills by HP; allies use authored
/// ranges as they are, with a 3 m floor on a melee auto-attack.
/// </summary>
/// <param name="ScaledBySettings">Ranges are multiplied by <see cref="ReachScale"/> (clamped to at least 0.001).</param>
/// <param name="MeleeAutoFloor">Shortest melee auto-attack reach on the unscaled path (allies: 3). The scaled path has none.</param>
/// <param name="MagicAutoReach">How close a caster's auto-attack (a melee swing) must be; scaled as melee on the scaled path.</param>
/// <param name="SkillsGatedByHp">A skill is only usable at or below its <see cref="NpcSkill.HpThreshold"/>.</param>
public sealed record ReachPolicy(bool ScaledBySettings, float MeleeAutoFloor, float MagicAutoReach, bool SkillsGatedByHp)
{
    /// <summary>NpcAiController: scaled, caster swing 3.5 × melee scale, HP gates.</summary>
    public static ReachPolicy Enemy { get; } = new(true, 0f, 3.5f, true);
    /// <summary>CombatCompanionManager: authored ranges, melee at least 3, caster swing 3.5.</summary>
    public static ReachPolicy Ally { get; } = new(false, 3f, 3.5f, false);
}

/// <summary>
/// How a side moves (docs/PLUGIN_ARCHITECTURE.md §2.3; the corpse-stepping and write-back fields come
/// with the shared step in B4).
/// </summary>
/// <param name="SpeedScale">Speed = MoveSpeed × this when MoveSpeed &gt; 0.</param>
/// <param name="FallbackSpeed">Speed when MoveSpeed is not positive.</param>
/// <param name="BaseTurnRate">Facing turn rate, rad/s.</param>
/// <param name="TurnSlowsWithSize">Big bodies turn slower (enemies, only with Enhanced enemy tactics on).</param>
public sealed record MovePolicy(float SpeedScale, float FallbackSpeed, float BaseTurnRate, bool TurnSlowsWithSize)
{
    /// <summary>NpcAiController: 1.5 × MoveSpeed (default 6 → 9), else 8; turns at 5 rad/s, slower when big.</summary>
    public static MovePolicy Enemy { get; } = new(1.5f, 8.0f, 5.0f, true);
    /// <summary>CombatCompanionManager: MoveSpeed, else 6; turns at 6 rad/s.</summary>
    public static MovePolicy Ally { get; } = new(1.0f, 6f, 6.0f, false);
}

/// <summary>
/// The range multipliers the settings give enemies: non-melee ranges × <see cref="NonMelee"/>, melee
/// ranges × <see cref="Melee"/> (the visual NPC scale times the range scale, so melee distances follow
/// the model the player sees).
/// </summary>
public readonly record struct ReachScale(float NonMelee, float Melee)
{
    public static ReachScale From(ICombatAiSettings settings)
    {
        var attack = Math.Clamp(settings.NpcAttackRangeScale, 0.1f, 100f);
        var visualScale = settings.EnableNpcScale
            ? Math.Clamp(settings.NpcScale, 0.01f, 3.0f)
            : 1.0f;
        return new ReachScale(attack, visualScale * attack);
    }
}

/// <summary>
/// Pure arithmetic both sides of a fight share: body gaps, where to stand, when a target counts as in
/// reach, how far a fighter reaches, attack pace, MP prices, speed and turning. Moved here unchanged
/// from NpcAiController and CombatCompanionManager (refactor stage B1); the two sides' real differences
/// are the <see cref="ReachPolicy"/> and <see cref="MovePolicy"/> they pass. No game state, so the
/// offline tests link it.
/// </summary>
public static class CombatantMath
{
    // Close in to a comfortable gap for melee; ranged stops just inside its reach. Moving resumes only
    // once the target is out of reach, so a small step back does not set it walking.
    public const float MeleeStandGap = 1.0f;
    public const float RangedStandMargin = 1.0f;
    public const float StopTolerance = 0.25f;
    public const float MeleeReachLimit = 5f;

    /// <summary>Horizontal distance (Y ignored).</summary>
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    /// <summary>
    /// The space between the two bodies, horizontally: centre distance less both hitbox radii. Ranges
    /// in the game are measured this way, so a skill's authored range is used as it is.
    /// </summary>
    public static float BodyGap(Vector3 selfPos, Vector3 targetPos, float selfRadius, float targetRadius)
        => MathF.Max(0f, FlatDistance(selfPos, targetPos) - selfRadius - targetRadius);

    /// <summary>The gap a fighter closes to: melee 1 m (half its reach if shorter), ranged 1 m inside its reach.</summary>
    public static float StandGap(float reach)
        => reach <= MeleeReachLimit
            ? MathF.Min(MeleeStandGap, reach * 0.5f)
            : MathF.Max(MeleeStandGap, reach - RangedStandMargin);

    /// <summary>
    /// Reach hysteresis: in reach until the gap exceeds the reach; out of reach until the gap is within
    /// the standing gap (plus a small tolerance).
    /// </summary>
    public static bool NextInReach(bool inReach, float gap, float reach, float stand)
    {
        if (inReach && gap > reach)
            return false;
        if (!inReach && gap <= stand + StopTolerance)
            return true;
        return inReach;
    }

    /// <summary>
    /// The point <paramref name="distance"/> from the target's centre on the side the fighter is on (in
    /// front of it, +Z, when the two coincide). The caller adds the body radii to the standing gap.
    /// </summary>
    public static Vector3 StandPoint(Vector3 selfPos, Vector3 targetPos, float distance)
    {
        var away = new Vector3(selfPos.X - targetPos.X, 0f, selfPos.Z - targetPos.Z);
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitZ;
        return targetPos + away * distance;
    }

    /// <summary>A non-melee range on the scaled path (never below 0.001).</summary>
    public static float ScaleNonMeleeRange(float range, ReachScale scale)
        => MathF.Max(0.001f, MathF.Max(0f, range) * scale.NonMelee);

    /// <summary>A melee range on the scaled path (never below 0.001).</summary>
    public static float ScaleMeleeRange(float range, ReachScale scale)
        => MathF.Max(0.001f, MathF.Max(0f, range) * scale.Melee);

    /// <summary>Reach of the auto-attack (a caster's is a melee swing).</summary>
    public static float AutoReach(NpcBehavior behavior, ReachPolicy policy, ReachScale scale)
        => policy.ScaledBySettings
            ? behavior.AutoAttackStyle switch
            {
                NpcAttackStyle.Ranged => ScaleNonMeleeRange(behavior.AutoAttackRange, scale),
                NpcAttackStyle.Magic => ScaleMeleeRange(policy.MagicAutoReach, scale),
                _ => ScaleMeleeRange(behavior.AutoAttackRange, scale),
            }
            : behavior.AutoAttackStyle switch
            {
                NpcAttackStyle.Ranged => behavior.AutoAttackRange,
                NpcAttackStyle.Magic => policy.MagicAutoReach,
                _ => MathF.Max(behavior.AutoAttackRange, policy.MeleeAutoFloor),
            };

    /// <summary>How close a caster's auto-attack swing must be.</summary>
    public static float MagicAutoReach(ReachPolicy policy, ReachScale scale)
        => policy.ScaledBySettings ? ScaleMeleeRange(policy.MagicAutoReach, scale) : policy.MagicAutoReach;

    /// <summary>A skill's authored range (measured between the bodies), scaled on the scaled path by its own style (Auto: the auto-attack's).</summary>
    public static float SkillReach(NpcSkill skill, NpcAttackStyle autoAttackStyle, ReachPolicy policy, ReachScale scale)
    {
        if (!policy.ScaledBySettings)
            return skill.Range;
        var style = skill.AttackStyle == NpcAttackStyle.Auto
            ? autoAttackStyle
            : skill.AttackStyle;
        return style == NpcAttackStyle.Melee
            ? ScaleMeleeRange(skill.Range, scale)
            : ScaleNonMeleeRange(skill.Range, scale);
    }

    /// <summary>Share of HP left (1 when MaxHp is not positive).</summary>
    public static float HpShare(SimulatedEntityState state)
        => state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 1f;

    /// <summary>
    /// How far this fighter can attack from right now: its auto-attack, or the longest skill it could
    /// use this moment (off cooldown, unlocked by HP when the policy gates by HP, affordable).
    /// <paramref name="mpPriceOf"/> is the skill's player price (ActionData.MpCost, 0 when unknown);
    /// it is asked once per skill that passes the cooldown and HP checks, in skill-list order.
    /// </summary>
    public static float ReachNow(NpcBehavior behavior, SimulatedEntityState state, ReachPolicy policy, ReachScale scale, Func<uint, int> mpPriceOf)
    {
        var reach = AutoReach(behavior, policy, scale);
        var hpShare = policy.SkillsGatedByHp ? HpShare(state) : 0f;
        foreach (var skill in behavior.Skills)
        {
            if (skill.CooldownRemaining > 0 || (policy.SkillsGatedByHp && hpShare > skill.HpThreshold))
                continue;
            if (state.CurrentMp < SkillMpCost(mpPriceOf(skill.ActionId), state.MaxMp))
                continue;
            reach = MathF.Max(reach, SkillReach(skill, behavior.AutoAttackStyle, policy, scale));
        }
        return reach;
    }

    /// <summary>
    /// What a fighter pays for a skill, as a fraction of its OWN pool: the same fraction the player pays
    /// of theirs (<see cref="CombatBudgetModel.EnemySkillMpCost"/>).
    /// </summary>
    public static int SkillMpCost(int playerPrice, int maxMp)
        => CombatBudgetModel.EnemySkillMpCost(playerPrice, maxMp);

    /// <summary>
    /// Action Mode initiates attacks faster (auto delay and skill cooldowns are divided by this), the
    /// same for both sides.
    /// </summary>
    public static float ActionPace(bool actionMode, float actionEnemyAttackSpeed)
        => actionMode ? MathF.Max(0.1f, actionEnemyAttackSpeed) : 1f;

    /// <summary>Walking speed for a behaviour's MoveSpeed under a side's policy.</summary>
    public static float MoveSpeed(float moveSpeed, MovePolicy policy)
        => moveSpeed > 0 ? moveSpeed * policy.SpeedScale : policy.FallbackSpeed;

    /// <summary>Facing turn rate (rad/s): slower for big bodies when the policy allows it and it is switched on.</summary>
    public static float TurnRate(MovePolicy policy, bool sizeTurningOn, float hitboxRadius)
        => policy.TurnSlowsWithSize && sizeTurningOn
            ? EnemyMovementMath.TurnRate(policy.BaseTurnRate, hitboxRadius)
            : policy.BaseTurnRate;

    /// <summary>
    /// One frame of turning toward a point at <paramref name="turnRate"/> rad/s. False (and nothing to
    /// write) when the point is on top of the fighter.
    /// </summary>
    public static bool TurnToward(Vector3 position, float currentRotation, Vector3 targetPos, float turnRate, float deltaTime, out float newRotation)
    {
        newRotation = currentRotation;
        var dir = targetPos - position;
        if (dir.LengthSquared() < 0.001f)
            return false;

        var targetRot = MathF.Atan2(dir.X, dir.Z);
        var diff = targetRot - currentRotation;
        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotStep = turnRate * deltaTime;
        newRotation = MathF.Abs(diff) < rotStep ? targetRot : currentRotation + MathF.Sign(diff) * rotStep;
        return true;
    }
}
