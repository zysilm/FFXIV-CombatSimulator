// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;

namespace CombatSimulator.Simulation;

internal readonly record struct AppliedActionDamage(
    SimulatedEntityState Target,
    DamageResult DamageResult);

/// <summary>
/// How one side's hits land (docs/PLUGIN_ARCHITECTURE.md §9 B6, §10.2): what differs between an enemy's
/// attack on the player's side and an ally's attack on an enemy. Everything else is the one settlement
/// (<see cref="HitSettlement.Settle"/>).
/// </summary>
/// <param name="OnPlayersSide">
/// The hits land on the player's side: a captive is struck only after the wind-up and is then executed, a
/// lethal blow is put to the battle rules (hostages), and a hit on the player counts towards the damage
/// taken, raises the player's hit and flinch cues and records the killer.
/// </param>
/// <param name="SkillDamageType">The hit takes the skill's damage type; otherwise it is physical.</param>
/// <param name="FullFormulaForSkills">
/// A skill without a potency override goes through the full formula (critical and direct hits, the damage
/// multiplier); otherwise it is the bare-potency formula at the skill's potency.
/// </param>
/// <param name="RaisesOnHit">Each landed hit is reported as threat (<c>TargetingService.RegisterHit</c>, the enemies' threat); the allies' is registered by their controller.</param>
/// <param name="CountsDamageDealt">Each landed hit counts towards the damage dealt.</param>
/// <param name="EngagesWhoItHits">Each target it hits is woken if idle (the primary as soon as the attack is made).</param>
/// <param name="DeferredNeedsAttacker">An arrow still in the air is dropped if its shooter is gone when it would land.</param>
/// <param name="DeferredFlinchesPlayer">An arrow that lands also flinches the player.</param>
/// <param name="DeferredDeathsAfterAllHits">
/// The deaths an arrow causes are settled after every target it hits has taken its share, the primary first
/// (otherwise each target dies as it is hit).
/// </param>
/// <param name="LogsDamageTaken">The battle log line is "… → You: …" (damage taken); otherwise "… -> target: …" (damage dealt).</param>
public sealed record HitSide(
    bool OnPlayersSide,
    bool SkillDamageType,
    bool FullFormulaForSkills,
    bool RaisesOnHit,
    bool CountsDamageDealt,
    bool EngagesWhoItHits,
    bool DeferredNeedsAttacker,
    bool DeferredFlinchesPlayer,
    bool DeferredDeathsAfterAllHits,
    bool LogsDamageTaken)
{
    /// <summary>An enemy's attack on the player's side (<c>CombatEngine.ProcessNpcAction</c>).</summary>
    public static HitSide Enemy { get; } = new(
        OnPlayersSide: true, SkillDamageType: true, FullFormulaForSkills: false, RaisesOnHit: true, CountsDamageDealt: false,
        EngagesWhoItHits: false, DeferredNeedsAttacker: true, DeferredFlinchesPlayer: true, DeferredDeathsAfterAllHits: true,
        LogsDamageTaken: true);

    /// <summary>An ally's attack on an enemy (<c>CombatEngine.ProcessCompanionAction</c>).</summary>
    public static HitSide Ally { get; } = new(
        OnPlayersSide: false, SkillDamageType: false, FullFormulaForSkills: true, RaisesOnHit: false, CountsDamageDealt: true,
        EngagesWhoItHits: true, DeferredNeedsAttacker: false, DeferredFlinchesPlayer: false, DeferredDeathsAfterAllHits: false,
        LogsDamageTaken: false);
}

/// <summary>
/// What the hit settlement uses of the engine (<see cref="CombatEngine"/>): its state, the battle's rule parts,
/// the game (positions, targets in an area, the shot's flight, effects and flinches) and its events.
/// Implemented by the engine over the game, and by a recording fake in <c>Tests/CombatReplay</c>.
/// </summary>
internal interface IHitSettlementHost
{
    SimulationState State { get; }
    /// <summary>The battle's rule parts (a lethal blow is put to them), or null before they are wired.</summary>
    BattleRuleSet? RuleParts { get; }
    ActionData? GetActionData(uint actionId);
    bool EnableCriticalHits { get; }
    bool EnableDirectHits { get; }
    float DamageMultiplier { get; }

    List<SimulatedEntityState> GetHostileTargets(SimulatedEntityState source);
    Vector3 GetEntityPosition(SimulatedEntityState entity);
    List<SimulatedEntityState> ResolveActionTargets(SimulatedEntityState source, SimulatedEntityState primaryTarget, ActionData actionData, bool primaryAlreadyHit);
    /// <summary>When a bow or gun auto-attack by the source connects, or negative (see <c>CombatEngine.RangedShots</c>).</summary>
    float RangedContactDelay(SimulatedEntityState source, SimulatedEntityState target);
    void DeferHit(float delay, Action resolve);

    /// <summary>Takes a landed hit off the target's HP (<c>CombatEngine.LandDamage</c>).</summary>
    void LandDamage(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source);
    /// <summary>The hit's threat (<c>TargetingService.RegisterHit</c>; the engine's <c>OnHit</c> before Refactoring II).</summary>
    void RaiseHit(SimulatedEntityState source, SimulatedEntityState target, int damage);
    /// <summary><c>OnDamageLanded</c>.</summary>
    void RaiseDamageLanded(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source);
    /// <summary><c>OnPlayerDamagedByNpc</c>.</summary>
    void RaisePlayerDamagedByNpc();
    /// <summary><c>OnPlayerHitByNpc</c>.</summary>
    void RaisePlayerHitByNpc(uint npcId);
    void TakeCaptive(SimulatedEntityState friend);
    void AddLogEntry(string message, CombatLogType type);
    void OnEntityDeath(SimulatedEntityState entity);
    /// <summary>The player fell to this fighter (<c>LastPlayerDefeatedBy</c>, its address and id).</summary>
    void RecordPlayerKiller(Combatant killer);
    void EngageIdleTarget(uint entityId);

    void TriggerActionEffect(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
        bool suppressCastVfx, float contactDelay, bool deferHitReaction);
    void TriggerManualNpcHitFeedback(IReadOnlyList<AppliedActionDamage> hits);
    /// <summary>The effect only, without the caster's animation (<c>PlayActionVfx(BuildActionEffectRequest(...))</c>).</summary>
    void PlayActionVfx(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
        bool suppressCastVfx, bool modelTimelineOwnsVfx);
    void PlayHitReactionOnTarget(SimulatedEntityState target, bool isDamage, bool includePlayer);
}

/// <summary>
/// How an attack lands, for both sides (refactor stage B6: the engine's two paths, one for an enemy's attack
/// and one for an ally's, made one, the differences being the side's <see cref="HitSide"/>): the damage,
/// who else the action takes in, the hit landing now or when the shot arrives, the effects, the log line,
/// the deaths. Game-free: everything it needs of the game is its host's.
/// </summary>
internal sealed class HitSettlement
{
    private readonly IHitSettlementHost host;
    private readonly DamageCalculator damageCalculator;

    public HitSettlement(IHitSettlementHost host, DamageCalculator damageCalculator)
    {
        this.host = host;
        this.damageCalculator = damageCalculator;
    }

    private SimulationState State => host.State;

    // An enemy's first try at a captive starts the wind-up; it strikes once the wind-up is over.
    private const float ExecutionWindupSeconds = 1.5f;
    private const float ExecutionWindupForgetSeconds = 4f;
    private readonly Dictionary<(uint Npc, uint Captive), float> executionWindups = new();

    /// <summary>Every wind-up is forgotten (the battle starts or is reset).</summary>
    public void ForgetExecutionWindups() => executionWindups.Clear();

    /// <summary>The wind-ups on this captive are forgotten (it is free).</summary>
    public void ForgetExecutionWindupsOn(uint captiveId)
    {
        foreach (var key in new List<(uint Npc, uint Captive)>(executionWindups.Keys))
            if (key.Captive == captiveId)
                executionWindups.Remove(key);
    }

    private bool ExecutionReady(Combatant attacker, SimulatedEntityState captive)
    {
        var key = (attacker.SimulatedEntityId, captive.EntityId);
        // A wind-up the enemy walked away from (it fought someone else meanwhile) starts over.
        if (!executionWindups.TryGetValue(key, out var since) ||
            State.SimulationTime - since > ExecutionWindupSeconds + ExecutionWindupForgetSeconds)
        {
            executionWindups[key] = State.SimulationTime;
            return false;
        }
        return State.SimulationTime - since >= ExecutionWindupSeconds;
    }

    /// <summary>An enemy's attack on the player's side.</summary>
    public SimulatedActionResult ProcessNpcAction(
        SimulatedNpc npc,
        uint actionId,
        ulong targetId,
        int potency = 0,
        NpcAttackStyle attackStyle = NpcAttackStyle.Auto,
        float radius = 0,
        bool suppressCasterActionEffect = false,
        bool suppressCastVfx = false,
        uint modelVfxActionId = 0,
        bool modelTimelineOwnsVfx = false,
        TelegraphShape? area = null,
        ulong excludeTargetId = 0)
        => Settle(HitSide.Enemy, npc, null, targetId, actionId, potency, attackStyle, radius,
            suppressCasterActionEffect, suppressCastVfx, modelVfxActionId, modelTimelineOwnsVfx, area, excludeTargetId);

    /// <summary>An ally's attack on an enemy.</summary>
    public SimulatedActionResult ProcessCompanionAction(
        CombatCompanion companion,
        SimulatedNpc targetNpc,
        uint actionId,
        int potency = 0,
        NpcAttackStyle attackStyle = NpcAttackStyle.Auto,
        float radius = 0)
        => Settle(HitSide.Ally, companion, targetNpc, targetNpc.SimulatedEntityId, actionId, potency, attackStyle, radius,
            false, false, 0, false, null, 0);

    /// <summary>
    /// One attack lands. The target is <paramref name="targetFighter"/>'s when the attacker was handed a
    /// fighter (an ally), else the entity <paramref name="targetId"/> (an enemy's, which a snapshotted
    /// <paramref name="area"/> may move to whoever stands in it).
    /// </summary>
    private SimulatedActionResult Settle(
        HitSide side,
        Combatant attacker,
        Combatant? targetFighter,
        ulong targetId,
        uint actionId,
        int potency,
        NpcAttackStyle attackStyle,
        float radius,
        bool suppressCasterActionEffect,
        bool suppressCastVfx,
        uint modelVfxActionId,
        bool modelTimelineOwnsVfx,
        TelegraphShape? area,
        ulong excludeTargetId)
    {
        var result = new SimulatedActionResult
        {
            ActionId = actionId,
            SourceId = attacker.SimulatedEntityId,
            TargetId = targetId,
        };

        var target = targetFighter != null ? targetFighter.State : State.GetEntity(targetId);
        if (target == null || !attacker.State.IsAlive || !target.IsAlive)
        {
            result.FailReason = "Invalid target or source dead.";
            return result;
        }

        // An execution is not a passing blow: the enemy stands over the captive a moment first.
        if (side.OnPlayersSide && target.IsCaptive && !ExecutionReady(attacker, target))
        {
            result.FailReason = "Winding up an execution.";
            return result;
        }

        // A snapshotted telegraph area decides who is hit: everyone hostile standing in it at
        // resolve, and only them — the primary included only if it stayed inside, the guarding
        // player (excludeTargetId) spared without sparing the companions next to them.
        List<SimulatedEntityState>? areaTargets = null;
        if (area is { } areaShape)
        {
            areaTargets = new List<SimulatedEntityState>();
            foreach (var candidate in host.GetHostileTargets(attacker.State))
            {
                if (areaTargets.Count >= 32)
                    break;
                if (!candidate.IsAlive || candidate.EntityId == excludeTargetId)
                    continue;
                if (areaShape.Contains(host.GetEntityPosition(candidate)))
                    areaTargets.Add(candidate);
            }
            if (areaTargets.Count == 0)
            {
                result.FailReason = "No targets in area.";
                return result;
            }
            if (!areaTargets.Contains(target))
            {
                target = areaTargets[0];
                result.TargetId = target.EntityId;
            }
        }
        // Who the action was aimed at, as the others it takes in are told apart from it.
        var primaryId = targetFighter?.SimulatedEntityId ?? target.EntityId;

        var resolvedActionData = host.GetActionData(actionId);

        // The enemy's damage is budgeted against what its own kit sustains; measured once per kit.
        var damageType = side.SkillDamageType ? resolvedActionData?.DamageType ?? SimDamageType.Physical : SimDamageType.Physical;
        DamageResult dmgResult;
        if (potency > 0)
            dmgResult = damageCalculator.CalculateNpcAutoAttack(attacker.State, target, potency, damageType);
        else if (resolvedActionData != null)
            dmgResult = side.FullFormulaForSkills
                ? damageCalculator.Calculate(attacker.State, target, resolvedActionData, false, host.EnableCriticalHits, host.EnableDirectHits, host.DamageMultiplier)
                : damageCalculator.CalculateNpcAutoAttack(attacker.State, target, resolvedActionData.Potency, damageType);
        else
            dmgResult = damageCalculator.CalculateNpcAutoAttack(attacker.State, target);

        // A bow or gun auto-attack starts its shot motion now and lands when the arrow arrives; the
        // damage, flinch and death wait for that moment. Everything else lands as it always has.
        var contactDelay = !suppressCasterActionEffect && area == null && actionId is 7 or 8
            ? host.RangedContactDelay(attacker.State, target)
            : -1f;
        var deferred = contactDelay >= 0f;

        if (!deferred)
            Land(side, attacker, target, dmgResult, primary: true);

        // An ally's attack makes the target fight back. The dev player-hit gate suppresses this path
        // so only a confirmed local-player hit can release the pack.
        if (side.EngagesWhoItHits)
            host.EngageIdleTarget(primaryId);

        // Preserve the selected action id so ranged/caster skills can use their own timelines/VFX
        // instead of every action collapsing to ActionId 7.
        var visualAction = resolvedActionData != null ? CloneActionData(resolvedActionData) : new ActionData
        {
            ActionId = actionId == 0 ? 7 : actionId,
            Name = actionId == 7 ? "Auto-attack" : $"Action #{actionId}",
            Potency = potency > 0 ? potency : 110,
            DamageType = SimDamageType.Physical,
            Range = attacker.Behavior.AutoAttackRange,
            AnimationLock = 0.6f,
        };
        if (potency > 0)
            visualAction.Potency = potency;
        if (radius > 0)
        {
            visualAction.Radius = radius;
            if (visualAction.Shape == AoeShape.Single)
                visualAction.Shape = AoeShape.CircleSelf;
        }

        ApplyNpcAttackStyle(visualAction, attackStyle == NpcAttackStyle.Auto ? attacker.Behavior.AutoAttackStyle : attackStyle);
        var hits = new List<AppliedActionDamage> { new(target, dmgResult) };
        var totalDamage = dmgResult.Damage;
        foreach (var extraTarget in areaTargets ?? host.ResolveActionTargets(attacker.State, target, visualAction, primaryAlreadyHit: true))
        {
            if (extraTarget.EntityId == primaryId)
                continue;

            var extraDamage = side.FullFormulaForSkills && potency <= 0 && resolvedActionData != null
                ? damageCalculator.Calculate(attacker.State, extraTarget, visualAction, false, host.EnableCriticalHits, host.EnableDirectHits, host.DamageMultiplier)
                : damageCalculator.CalculateNpcAutoAttack(attacker.State, extraTarget, visualAction.Potency > 0 ? visualAction.Potency : 110, damageType);
            if (!deferred)
                Land(side, attacker, extraTarget, extraDamage, primary: false);
            hits.Add(new AppliedActionDamage(extraTarget, extraDamage));
            totalDamage += extraDamage.Damage;

            if (side.EngagesWhoItHits)
                host.EngageIdleTarget(extraTarget.EntityId);
        }

        if (suppressCasterActionEffect)
        {
            // The caster's swing already played as a windup pose, so skip a second animation — but a
            // Keep a VFX-only strike even for Action 7: an action with no authored target effect
            // uses the configured contact spark instead of resolving as an invisible hit.
            host.TriggerManualNpcHitFeedback(hits);
            var vfxAction = modelVfxActionId != 0
                ? host.GetActionData(modelVfxActionId)
                : null;
            host.PlayActionVfx(
                attacker.State,
                vfxAction != null ? CloneActionData(vfxAction) : visualAction,
                hits,
                suppressCastVfx,
                modelTimelineOwnsVfx);
        }
        else
            host.TriggerActionEffect(attacker.State, visualAction, hits, suppressCastVfx, contactDelay, deferHitReaction: deferred);

        result.Success = true;
        result.Damage = totalDamage;
        result.IsCritical = dmgResult.IsCritical;
        result.IsDirectHit = dmgResult.IsDirectHit;

        var actionName = actionId is 7 or 8
            ? "auto-attacks"
            : $"uses {(!string.IsNullOrWhiteSpace(visualAction.Name) ? visualAction.Name : "action")}";
        // TODO: an enemy's line on a companion still renders the target as "You".
        if (side.LogsDamageTaken)
            host.AddLogEntry(
                $"{attacker.Name} {actionName} → You: {dmgResult.Damage:N0} damage",
                CombatLogType.DamageTaken);
        else
            host.AddLogEntry(
                $"{attacker.Name} {actionName} -> {targetFighter?.Name ?? target.Name}: {dmgResult.Damage:N0} damage",
                CombatLogType.DamageDealt);

        if (deferred)
        {
            host.DeferHit(contactDelay, () =>
            {
                if (side.DeferredNeedsAttacker && !attacker.IsSpawned)
                    return;
                foreach (var hit in hits)
                {
                    if (!hit.Target.IsAlive)
                        continue;
                    Land(side, attacker, hit.Target, hit.DamageResult, primary: hit.Target == target);
                    host.PlayHitReactionOnTarget(hit.Target, hit.DamageResult.Damage > 0, side.DeferredFlinchesPlayer);
                    if (!side.DeferredDeathsAfterAllHits && !hit.Target.IsAlive)
                        host.OnEntityDeath(hit.Target);
                }
                if (side.DeferredDeathsAfterAllHits)
                    FinishDeaths(side, attacker, hits, target, primaryId, null);
            });
            return result;
        }

        FinishDeaths(side, attacker, hits, target, primaryId, result);
        return result;
    }

    /// <summary>
    /// Land one hit: HP, and the side's tallies and cues. On the player's side a captive dies only to a
    /// blow aimed at them (<paramref name="primary"/>), not to a splash, and a blow that would fell one of
    /// them is put to the battle rules first (hostages: taken captive at 1 HP instead).
    /// </summary>
    private void Land(HitSide side, Combatant attacker, SimulatedEntityState target, DamageResult damage, bool primary)
    {
        if (side.OnPlayersSide)
        {
            var friend = target.IsPlayer || target.IsCompanion;
            if (friend && target.IsCaptive)
            {
                if (!primary)
                    return;
                target.Executed = true;
                target.CurrentHp = 0;
                executionWindups.Remove((attacker.SimulatedEntityId, target.EntityId));
                host.AddLogEntry($"{attacker.Name} executes {target.Name}!", CombatLogType.Death);
                return;
            }
            if (friend && target.IsAlive && damage.Damage >= target.CurrentHp &&
                host.RuleParts?.OnLethalBlow(target, attacker, primary) == LethalBlow.Capture)
            {
                if (target.IsPlayer)
                    State.TotalDamageTaken += Math.Max(0, target.CurrentHp - 1);
                target.CurrentHp = 1;
                host.RaiseDamageLanded(target, damage, attacker.State);
                host.TakeCaptive(target);
                return;
            }
        }

        host.LandDamage(target, damage, attacker.State);
        if (side.RaisesOnHit)
            host.RaiseHit(attacker.State, target, damage.Damage);
        if (side.CountsDamageDealt)
            State.TotalDamageDealt += damage.Damage;
        if (side.OnPlayersSide && target.IsPlayer)
        {
            State.TotalDamageTaken += damage.Damage;
            if (damage.Damage > 0)
                host.RaisePlayerDamagedByNpc();
            // Player was hit and survived → let auto-counter consider locking this attacker.
            if (target.IsAlive)
                host.RaisePlayerHitByNpc(attacker.SimulatedEntityId);
        }
    }

    /// <summary>The deaths an attack caused, the primary target first.</summary>
    private void FinishDeaths(HitSide side, Combatant attacker, IReadOnlyList<AppliedActionDamage> hits,
        SimulatedEntityState target, uint primaryId, SimulatedActionResult? result)
    {
        if (!target.IsAlive)
        {
            if (side.OnPlayersSide && target.IsPlayer)
                host.RecordPlayerKiller(attacker);
            if (result != null)
                result.TargetKilled = true;
            host.OnEntityDeath(target);
        }
        foreach (var hit in hits)
        {
            if (hit.Target.EntityId == primaryId || hit.Target.IsAlive)
                continue;

            if (side.OnPlayersSide && hit.Target.IsPlayer)
                host.RecordPlayerKiller(attacker);
            host.OnEntityDeath(hit.Target);
        }
    }

    internal static void ApplyNpcAttackStyle(ActionData actionData, NpcAttackStyle attackStyle)
    {
        switch (attackStyle)
        {
            case NpcAttackStyle.Ranged:
                actionData.Range = Math.Max(actionData.Range, 20f);
                break;
            case NpcAttackStyle.Magic:
                actionData.Range = Math.Max(actionData.Range, 20f);
                actionData.DamageType = SimDamageType.Magical;
                break;
            case NpcAttackStyle.Melee:
                if (actionData.Range <= 0 || actionData.Range > 5f)
                    actionData.Range = 3f;
                break;
        }
    }

    internal static ActionData CloneActionData(ActionData source)
    {
        return new ActionData
        {
            ActionId = source.ActionId,
            Name = source.Name,
            Potency = source.Potency,
            CastTime = source.CastTime,
            RecastTime = source.RecastTime,
            RecastGroup = source.RecastGroup,
            Range = source.Range,
            Radius = source.Radius,
            Shape = source.Shape,
            Width = source.Width,
            CastType = source.CastType,
            ConeAngleDeg = source.ConeAngleDeg,
            InnerRadius = source.InnerRadius,
            OmenVfxPath = source.OmenVfxPath,
            CanTargetHostile = source.CanTargetHostile,
            DamageType = source.DamageType,
            NativeMpCost = source.NativeMpCost,
            MpCost = source.MpCost,
            IsComboAction = source.IsComboAction,
            ComboFrom = source.ComboFrom,
            ComboPotency = source.ComboPotency,
            AnimationLock = source.AnimationLock,
            AnimationDuration = source.AnimationDuration,
            IsPlayerAction = source.IsPlayerAction,
            AnimationStartTimelineId = source.AnimationStartTimelineId,
            AnimationEndTimelineId = source.AnimationEndTimelineId,
            CastVfxPath = source.CastVfxPath,
            StartVfxPath = source.StartVfxPath,
            CasterVfxPaths = new List<string>(source.CasterVfxPaths),
            TargetVfxPaths = new List<string>(source.TargetVfxPaths),
        };
    }
}
