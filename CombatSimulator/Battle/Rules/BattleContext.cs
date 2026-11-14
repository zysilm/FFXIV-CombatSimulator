// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// What the rule parts see and use (docs/PLUGIN_ARCHITECTURE.md §5.1): the switches as named queries, the
/// engine's mechanics, the game objects, the allies' controller and this frame's enemies. At stage B5 the
/// allies' controller builds it; the engine's mechanics are its seam (<see cref="ICombatLoopEngine"/>).
/// </summary>
public sealed class BattleContext
{
    public BattleContext(BattleRules rules, ICombatLoopEngine engine, ICombatWorld world, IRuleAllies allies)
    {
        Rules = rules;
        Engine = engine;
        World = world;
        Allies = allies;
    }

    /// <summary>The war-rule switches, as named queries read live.</summary>
    public BattleRules Rules { get; }

    /// <summary>The engine: its state and clock, the log, and its mechanics (captive, release, revive, effects).</summary>
    public ICombatLoopEngine Engine { get; }

    /// <summary>The game objects: positions, emotes.</summary>
    public ICombatWorld World { get; }

    /// <summary>The allies and how to drive them.</summary>
    public IRuleAllies Allies { get; }

    /// <summary>The parts the battle runs (for what one part asks of the others).</summary>
    public BattleRuleSet RuleSet { get; internal set; } = null!;

    /// <summary>The enemies of the frame under way (as the allies' controller was given them).</summary>
    public IReadOnlyList<SimulatedNpc> Enemies { get; set; } = Array.Empty<SimulatedNpc>();
}

/// <summary>
/// The allies and their controller as the rule parts use them (docs/PLUGIN_ARCHITECTURE.md §5.1,
/// <c>IAllyDriver</c>): who they are, and how to move one, turn it, stop it for an emote, revive it.
/// Implemented by <c>CombatCompanionManager</c>.
/// </summary>
public interface IRuleAllies
{
    /// <summary>Every ally, in the controller's order.</summary>
    IReadOnlyList<CombatCompanion> All { get; }

    CombatCompanion? Get(uint allyId);

    /// <summary>Spawned, with an actor, alive and not captive: it can still act (fight, raise, pray).</summary>
    bool CanAct(CombatCompanion ally);

    /// <summary>Where the lord's body lies (its ragdoll's pelvis), or null: where the lord is.</summary>
    Vector3? LordBodyPosition();

    /// <summary>Where a fallen actor's body lies (its ragdoll's pelvis), or null: where it fell.</summary>
    Vector3? BodyPositionOf(nint address);

    /// <summary>The lord's body has been carried off.</summary>
    bool LordCorpseCaptured { get; }

    /// <summary>The enemies going for the lord's body (written by the corpse seizure each frame).</summary>
    IReadOnlySet<uint> Seizers { get; }

    /// <summary>The navigation mesh's crowd (null: none).</summary>
    ICrowdAgents? Crowd { get; }

    /// <summary>How many enemies have this one as their target.</summary>
    int AttackersOn(uint entityId);

    /// <summary>Whether this ally can stand within reach of the enemy and stay inside its leash.</summary>
    bool CanReach(CombatCompanion ally, SimulatedNpc enemy);

    /// <summary>Drops what it was casting or locked in.</summary>
    void ClearAction(CombatCompanion ally);

    /// <summary>Drops its target.</summary>
    void ClearTarget(CombatCompanion ally);

    void EnterState(CombatCompanion ally, CompanionAiState state, float deltaTime);
    void MoveToward(CombatCompanion ally, Vector3 goal, float deltaTime);
    void RotateToward(CombatCompanion ally, Vector3 point, float deltaTime);

    /// <summary>Stands still to hold an emote.</summary>
    void HaltForEmote(CombatCompanion ally);

    /// <summary>Back on its feet with this share of its HP (MP empty).</summary>
    void Revive(CombatCompanion ally, float hpShare);

    /// <summary>A line in the plugin's log (summaries for diagnosis).</summary>
    void Info(string text);
}
