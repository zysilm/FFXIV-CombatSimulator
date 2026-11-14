// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Combat;

/// <summary>How a side picks among the skills that are ready.</summary>
public enum SkillChoice
{
    /// <summary>Any ready skill, at random (one draw on the side's random source), after a token from the shared bucket when the side uses one.</summary>
    RandomReady,
    /// <summary>The first ready skill by <see cref="NpcSkill.Priority"/>, highest first (stable for equal priorities).</summary>
    HighestPriority,
}

/// <summary>How the animation lock is written after an attack lands.</summary>
public enum AnimationLockWrite
{
    /// <summary><c>lock = MathF.Max(lock, AttackLockSeconds)</c> (keeps a longer lock the attack itself set).</summary>
    MaxWithCurrent,
    /// <summary><c>lock = AttackLockSeconds</c>.</summary>
    Overwrite,
}

/// <summary>
/// How a side acts once its target is within reach (docs/PLUGIN_ARCHITECTURE.md §2.3, §3.2). When the
/// cooldowns and the animation lock tick, and what happens on death, stay with each controller (§3.3).
/// </summary>
/// <param name="Choice">Random ready skill, or highest priority.</param>
/// <param name="UsesSkillTokens">A skill start takes a token from the side's shared bucket; none left: auto-attack instead, the skill's cooldown unspent.</param>
/// <param name="UsesCastAndTelegraph">Holds its attacks while its telegraph is still running (the cast itself is the side's sink).</param>
/// <param name="LockWrite">How the lock is written after a landed attack.</param>
/// <param name="AttackLockSeconds">The lock after a landed attack.</param>
public sealed record ActPolicy(
    SkillChoice Choice,
    bool UsesSkillTokens,
    bool UsesCastAndTelegraph,
    AnimationLockWrite LockWrite,
    float AttackLockSeconds)
{
    /// <summary>NpcAiController: a random ready skill after a token, telegraphs, lock = max(lock, 0.6).</summary>
    public static ActPolicy Enemy { get; } = new(SkillChoice.RandomReady, true, true, AnimationLockWrite.MaxWithCurrent, 0.6f);
    /// <summary>CombatCompanionManager: highest priority, no tokens, no telegraphs, lock = 0.6.</summary>
    public static ActPolicy Ally { get; } = new(SkillChoice.HighestPriority, false, false, AnimationLockWrite.Overwrite, 0.6f);
}

/// <summary>A side's three policies (docs/PLUGIN_ARCHITECTURE.md §2.3).</summary>
public sealed record CombatPolicy(ReachPolicy Reach, ActPolicy Act, MovePolicy Move)
{
    public static CombatPolicy Enemy { get; } = new(ReachPolicy.Enemy, ActPolicy.Enemy, MovePolicy.Enemy);
    public static CombatPolicy Ally { get; } = new(ReachPolicy.Ally, ActPolicy.Ally, MovePolicy.Ally);
}

/// <summary>What a committed skill came to.</summary>
public enum AttackCommit
{
    Failed,
    Landed,
    /// <summary>A cast began (the sink set up the cast and paid its MP); the attack lands when the cast ends.</summary>
    CastStarted,
}

/// <summary>What <see cref="CombatStep.Run{TSelf, TTarget}"/> did this frame.</summary>
public enum StepResult
{
    /// <summary>Animation lock, or (telegraphing sides) its telegraph is still running.</summary>
    Locked,
    SkillLanded,
    SkillFailed,
    CastStarted,
    AutoAttackLanded,
    AutoAttackFailed,
    /// <summary>No skill, and the auto-attack is not due (or a caster's swing is held until it is in melee).</summary>
    Waiting,
}

/// <summary>
/// Where a side's attacks go and how it shows its stance: the side's own half of the shared step. The
/// enemies' (NpcAiController) goes to the attack router and may start a cast; the allies'
/// (CombatCompanionManager) goes to the engine's companion action and registers damage and threat.
/// </summary>
public interface ICombatStepSink<in TSelf, in TTarget> where TSelf : Combatant
{
    /// <summary>It cannot act this frame (lock or telegraph): show it locked.</summary>
    void ShowLocked(TSelf self);
    /// <summary>It is free to act: called once before the skills are looked at.</summary>
    void ShowReady(TSelf self);
    /// <summary>Commit a skill. <paramref name="mpCost"/> is its price against this fighter's pool; the step pays it on <see cref="AttackCommit.Landed"/>, a sink that starts a cast pays it itself.</summary>
    AttackCommit CommitSkill(TSelf self, TTarget target, NpcSkill skill, int mpCost);
    /// <summary>Commit an auto-attack; true when it landed.</summary>
    bool CommitAutoAttack(TSelf self, TTarget target);
}

/// <summary>What the shared step reads besides the fighter, its target and its sink.</summary>
public readonly struct CombatStepContext
{
    /// <summary>The settings (the reach scale on a side whose reach is scaled by them).</summary>
    public ICombatAiSettings Settings { get; init; }
    /// <summary>Only <see cref="SkillChoice.RandomReady"/> draws from it: one draw when a skill is picked.</summary>
    public Random? Rng { get; init; }
    /// <summary>The side's shared skill-start budget; required when the policy uses tokens.</summary>
    public SkillTokenBucket? Tokens { get; init; }
    /// <summary>Is this fighter's telegraph still running (by its simulated entity id); asked only when the policy uses telegraphs.</summary>
    public Func<uint, bool>? IsTelegraphBusy { get; init; }
    /// <summary>A skill's price against the player's pool (<c>GetActionData(id)?.MpCost ?? 0</c>).</summary>
    public Func<uint, int> MpPriceOf { get; init; }
    /// <summary>Auto-attack delays and skill cooldowns are divided by this (<see cref="CombatantMath.ActionPace"/>).</summary>
    public float Pace { get; init; }
}

/// <summary>
/// The shared skill-start budget of one side (enemies): refilled every frame at the settings' rate,
/// at most <see cref="Burst"/> back to back. Rate ≤ 0: uncapped.
/// </summary>
public sealed class SkillTokenBucket
{
    /// <summary>How many starts may go back-to-back after a lull, so a fight still opens with a
    /// volley instead of a metronome.</summary>
    public const float Burst = 3f;

    private readonly ICombatAiSettings settings;
    private float tokens;

    public SkillTokenBucket(ICombatAiSettings settings) => this.settings = settings;

    /// <summary>Once a frame, before anyone acts.</summary>
    public void Refill(float deltaTime)
        => tokens = MathF.Min(Burst, tokens + MathF.Max(0f, settings.NpcSkillRateLimit) * deltaTime);

    /// <summary>Take a skill-start slot if one is free. Only the COMMIT points call this: a cast
    /// that already paid on the way in must not pay again when it lands.</summary>
    public bool TryTake()
    {
        if (settings.NpcSkillRateLimit <= 0f) return true; // uncapped
        if (tokens < 1f) return false;
        tokens -= 1f;
        return true;
    }
}

/// <summary>
/// The part of an attack both sides do the same way (docs/PLUGIN_ARCHITECTURE.md §3): with the target
/// within reach, use a ready skill or, failing that, the auto-attack when it is due. The side's
/// differences come from <see cref="Combatant.Policy"/> and its <see cref="ICombatStepSink{TSelf, TTarget}"/>.
/// What happens before (cooldown and lock ticks, death, casts, rules, tactics, reach and movement)
/// stays in each controller.
/// </summary>
public static class CombatStep
{
    /// <param name="gap">The body gap to the target (as the controller measured it).</param>
    public static StepResult Run<TSelf, TTarget>(
        TSelf self, TTarget target, float gap, float deltaTime,
        ICombatStepSink<TSelf, TTarget> sink, in CombatStepContext ctx)
        where TSelf : Combatant
    {
        var policy = self.Policy;
        var act = policy.Act;
        var state = self.State;

        if (state.AnimationLock > 0)
        {
            sink.ShowLocked(self);
            return StepResult.Locked;
        }

        // Already mid-attack (telegraph windup / late-guard grace / recovery) — do not commit a new
        // attack, or the next swing overlaps the current one.
        if (act.UsesCastAndTelegraph && TelegraphBusy(ctx)(self.SimulatedEntityId))
        {
            sink.ShowLocked(self);
            return StepResult.Locked;
        }

        sink.ShowReady(self);

        var scale = policy.Reach.ScaledBySettings ? ReachScale.From(ctx.Settings) : default;
        var (skill, mpCost) = act.Choice == SkillChoice.RandomReady
            ? PickRandomReady(self, gap, scale, ctx)
            : PickHighestPriority(self, gap, scale, ctx);

        if (skill != null)
        {
            var result = sink.CommitSkill(self, target, skill, mpCost);
            if (result == AttackCommit.Landed)
            {
                state.CurrentMp = Math.Max(0, state.CurrentMp - mpCost);
                skill.CooldownRemaining = skill.Cooldown / ctx.Pace;
                WriteLock(state, act);
            }
            // Committed or not, no auto-attack this frame.
            return result switch
            {
                AttackCommit.Landed => StepResult.SkillLanded,
                AttackCommit.CastStarted => StepResult.CastStarted,
                _ => StepResult.SkillFailed,
            };
        }

        self.AutoAttackTimer -= deltaTime;
        if (!(self.AutoAttackTimer <= 0)) // as the old loops: a NaN timer does not attack
            return StepResult.Waiting;

        // FFXIV: caster/magic auto-attacks are MELEE (only physical ranged — bow/gun — auto at
        // range). Their spells stay ranged via the skills above; the basic swing only lands up
        // close. Hold the swing (don't reset the timer) until the target is in melee.
        if (self.Behavior.AutoAttackStyle == NpcAttackStyle.Magic &&
            gap > CombatantMath.MagicAutoReach(policy.Reach, scale))
            return StepResult.Waiting;

        self.AutoAttackTimer = self.Behavior.AutoAttackDelay / ctx.Pace;
        if (!sink.CommitAutoAttack(self, target))
            return StepResult.AutoAttackFailed;

        // Auto-attacks recover MP so a depleted fighter earns its way back into its skills.
        state.CurrentMp = Math.Min(state.MaxMp, state.CurrentMp + CombatBudgetModel.EnemyAutoAttackMpRestore);
        WriteLock(state, act);
        return StepResult.AutoAttackLanded;
    }

    private static SkillTokenBucket Tokens(in CombatStepContext ctx)
        => ctx.Tokens ?? throw new InvalidOperationException(
            "CombatStep: the fighter's ActPolicy uses skill tokens but CombatStepContext.Tokens is null.");

    private static Func<uint, bool> TelegraphBusy(in CombatStepContext ctx)
        => ctx.IsTelegraphBusy ?? throw new InvalidOperationException(
            "CombatStep: the fighter's ActPolicy uses telegraphs but CombatStepContext.IsTelegraphBusy is null.");

    private static void WriteLock(SimulatedEntityState state, ActPolicy act)
    {
        if (act.LockWrite == AnimationLockWrite.MaxWithCurrent)
            state.AnimationLock = MathF.Max(state.AnimationLock, act.AttackLockSeconds);
        else
            state.AnimationLock = act.AttackLockSeconds;
    }

    /// <summary>Ready: off cooldown, within its reach, unlocked by HP (when the policy gates by HP), affordable.</summary>
    private static bool IsReady(Combatant self, NpcSkill skill, float gap, ReachScale scale, ReachPolicy reach,
        float hpPercent, in CombatStepContext ctx, out int mpCost)
    {
        mpCost = 0;
        if (skill.CooldownRemaining > 0)
            return false;
        if (gap > CombatantMath.SkillReach(skill, self.Behavior.AutoAttackStyle, reach, scale))
            return false;
        if (reach.SkillsGatedByHp && hpPercent > skill.HpThreshold)
            return false;
        // Only skills it can afford now; otherwise it auto-attacks to earn MP back.
        mpCost = CombatantMath.SkillMpCost(ctx.MpPriceOf(skill.ActionId), self.State.MaxMp);
        return self.State.CurrentMp >= mpCost;
    }

    /// <summary>
    /// Every ready skill in list order, then one at random — so an enemy varies its real rotation
    /// instead of always leading with the same top skill. A ready skill still needs a token: without
    /// one it falls through to the auto-attack, the skill's cooldown unspent. Token first, then the draw.
    /// </summary>
    private static (NpcSkill? Skill, int MpCost) PickRandomReady(Combatant self, float gap, ReachScale scale, in CombatStepContext ctx)
    {
        var reach = self.Policy.Reach;
        var hpPercent = reach.SkillsGatedByHp ? (float)self.State.CurrentHp / self.State.MaxHp : 0f;
        List<NpcSkill>? ready = null; // allocates only when a skill is actually ready
        foreach (var skill in self.Behavior.Skills)
        {
            if (IsReady(self, skill, gap, scale, reach, hpPercent, ctx, out _))
                (ready ??= new List<NpcSkill>()).Add(skill);
        }
        if (ready == null || (self.Policy.Act.UsesSkillTokens && !Tokens(ctx).TryTake()))
            return (null, 0);

        var rng = ctx.Rng ?? throw new InvalidOperationException(
            "CombatStep: the fighter's ActPolicy picks a random ready skill but CombatStepContext.Rng is null.");
        var pick = ready[rng.Next(ready.Count)];
        return (pick, CombatantMath.SkillMpCost(ctx.MpPriceOf(pick.ActionId), self.State.MaxMp));
    }

    /// <summary>The first ready skill, highest priority first; the rest are not looked at.</summary>
    private static (NpcSkill? Skill, int MpCost) PickHighestPriority(Combatant self, float gap, ReachScale scale, in CombatStepContext ctx)
    {
        var reach = self.Policy.Reach;
        var hpPercent = reach.SkillsGatedByHp ? (float)self.State.CurrentHp / self.State.MaxHp : 0f;
        foreach (var skill in self.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (!IsReady(self, skill, gap, scale, reach, hpPercent, ctx, out var mpCost))
                continue;
            if (self.Policy.Act.UsesSkillTokens && !Tokens(ctx).TryTake())
                return (null, 0);
            return (skill, mpCost);
        }
        return (null, 0);
    }
}
