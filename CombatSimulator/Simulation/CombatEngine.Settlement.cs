// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;

namespace CombatSimulator.Simulation;

/// <summary>The engine as the hit settlement (<see cref="HitSettlement"/>) sees it: the game behind the engine's own members.</summary>
public partial class CombatEngine
{
    /// <summary>
    /// The battle's rule parts (the allies' controller holds them), asked when an enemy's blow would fell one of
    /// the player's side (<see cref="IBattleRule.OnLethalBlow"/>). Given by constructor (built before the engine,
    /// bound to its context by the allies' controller).
    /// </summary>
    public BattleRuleSet RuleParts { get; }

    private sealed class SettlementHost(CombatEngine engine) : IHitSettlementHost
    {
        public SimulationState State => engine.State;
        // The engine is given the set before the allies' controller binds its context: until then (no hit is
        // settled in that window today) a lethal blow is not put to the rules, rather than to unbound parts.
        public BattleRuleSet? RuleParts => engine.RuleParts.IsBound ? engine.RuleParts : null;
        public ActionData? GetActionData(uint actionId) => engine.actionDataProvider.GetActionData(actionId);

        public List<SimulatedEntityState> GetHostileTargets(SimulatedEntityState source) => engine.GetHostileTargets(source);
        public Vector3 GetEntityPosition(SimulatedEntityState entity) => engine.GetEntityPosition(entity);
        public List<SimulatedEntityState> ResolveActionTargets(SimulatedEntityState source, SimulatedEntityState primaryTarget, ActionData actionData, bool primaryAlreadyHit)
            => engine.ResolveActionTargets(source, primaryTarget, actionData, primaryAlreadyHit);
        public float RangedContactDelay(SimulatedEntityState source, SimulatedEntityState target) => engine.RangedContactDelay(source, target);
        public void DeferHit(float delay, Action resolve) => engine.DeferHit(delay, resolve);

        public void LandDamage(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source) => engine.LandDamage(target, damage, source);
        public void RaiseHit(SimulatedEntityState source, SimulatedEntityState target, int damage) => engine.targeting.RegisterHit(source, target, damage);
        public void RaiseDamageLanded(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source) => engine.OnDamageLanded?.Invoke(target, damage, source);
        public void RaisePlayerDamagedByNpc() => engine.OnPlayerDamagedByNpc?.Invoke();
        public void RaisePlayerHitByNpc(uint npcId) => engine.OnPlayerHitByNpc?.Invoke(npcId);
        public void TakeCaptive(SimulatedEntityState friend) => engine.TakeCaptive(friend);
        public void AddLogEntry(string message, CombatLogType type) => engine.AddLogEntry(message, type);
        public void OnEntityDeath(SimulatedEntityState entity) => engine.OnEntityDeath(entity);

        public void RecordPlayerKiller(Combatant killer)
        {
            engine.LastPlayerDefeatedBy = killer.Name;
            engine.LastPlayerKillerAddress = killer.Address;
            engine.LastPlayerKillerEntityId = killer.SimulatedEntityId;
        }

        public void EngageIdleTarget(uint entityId) => engine.EngageIdleTarget(entityId);

        public void TriggerActionEffect(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
            bool suppressCastVfx, float contactDelay, bool deferHitReaction)
            => engine.TriggerActionEffect(source, actionData, hits, suppressCastVfx, contactDelay, deferHitReaction);

        public void TriggerManualNpcHitFeedback(IReadOnlyList<AppliedActionDamage> hits) => engine.TriggerManualNpcHitFeedback(hits);

        public void PlayActionVfx(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
            bool suppressCastVfx, bool modelTimelineOwnsVfx)
            => engine.animationController.PlayActionVfx(engine.BuildActionEffectRequest(source, actionData, hits, suppressCastVfx, modelTimelineOwnsVfx));

        public void PlayHitReactionOnTarget(SimulatedEntityState target, bool isDamage, bool includePlayer)
            => engine.PlayHitReactionOnTarget(target, isDamage, includePlayer);
    }
}
