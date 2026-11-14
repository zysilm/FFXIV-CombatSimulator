// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;

namespace CombatSimulator.Companions;

/// <summary>
/// The battle's rule parts (Battle/Rules, docs/PLUGIN_ARCHITECTURE.md §5), called where the old Lord and
/// Rescue code was: before the allies act (<see cref="BattleRuleSet.TickBeforeAllies"/>), in each ally's turn
/// (<see cref="BattleRuleSet.TakesOverAlly"/>), in both sides' target draws, the leash and the HP-bar word.
/// </summary>
public unsafe partial class CombatCompanionManager
{
    private readonly BattleRuleSet ruleParts;

    /// <summary>The allies as the rule parts drive them.</summary>
    private sealed class RuleAllies(CombatCompanionManager owner) : IRuleAllies
    {
        public IReadOnlyList<CombatCompanion> All => owner.companions;
        public CombatCompanion? Get(uint allyId) => owner.GetCompanion(allyId);
        public bool CanAct(CombatCompanion ally) => CanRescue(ally);
        public Vector3? LordBodyPosition() => owner.LordBodyPosition?.Invoke();
        public Vector3? BodyPositionOf(nint address) => owner.BodyPositionOf?.Invoke(address);
        public bool LordCorpseCaptured => owner.LordCorpseCaptured;
        public IReadOnlySet<uint> Seizers => owner.LordSeizers;
        public ICrowdAgents? Crowd => owner.Crowd;
        public int AttackersOn(uint entityId) => owner.targeting.AttackersOn(entityId);
        public bool CanReach(CombatCompanion ally, SimulatedNpc enemy) => owner.ComputeCompanionReachability(ally, enemy).Reachable;
        public void ClearAction(CombatCompanion ally) => ClearCompanionActionState(ally);
        public void ClearTarget(CombatCompanion ally) => owner.ClearCompanionTargetState(ally);
        public void EnterState(CombatCompanion ally, CompanionAiState state, float deltaTime) => owner.EnterCompanionState(ally, state, deltaTime);
        public void MoveToward(CombatCompanion ally, Vector3 goal, float deltaTime) => owner.MoveToward(ally, goal, deltaTime);
        public void RotateToward(CombatCompanion ally, Vector3 point, float deltaTime) => owner.RotateToward(ally, point, deltaTime);
        public void HaltForEmote(CombatCompanion ally) => owner.HaltForEmote(ally);
        public void Revive(CombatCompanion ally, float hpShare) => owner.ReviveCompanion(ally, hpShare);
        public void Info(string text) => owner.log.Info(text);
    }
}
