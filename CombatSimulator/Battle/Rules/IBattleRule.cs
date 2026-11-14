// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// One part of a battle's rules (docs/PLUGIN_ARCHITECTURE.md §5.1). A battle runs the parts
/// <see cref="BattleRuleFactory.PartsFor"/> composes from the war settings, held by a
/// <see cref="BattleRuleSet"/>; a new mode is a new part, a line in the factory and a name in the
/// dropdown. Mechanics (captives, emotes held, revives, forced outcomes) stay with the engine and the
/// allies' controller; a part decides when they are used.
/// </summary>
/// <remarks>
/// Refactor stage B5 wires the hooks the allies' controller calls (where it called its Lord and Rescue
/// code). The engine-side hooks (<see cref="TickAfterEngine"/>, <see cref="OnLethalBlow"/>,
/// <see cref="SuppressesKillerPossession"/>, <see cref="DescribeOutcome"/>) are declared but not called
/// yet: those decisions are still made where they were (<c>CombatEngine</c>, <c>Plugin.War.cs</c>,
/// <c>Plugin.cs</c>) until stage B6.
/// </remarks>
public interface IBattleRule
{
    /// <summary>The part's name in <see cref="BattleRuleFactory.PartsFor"/>.</summary>
    string Id { get; }

    /// <summary>The battle is reset with the allies kept (every part, composed or not).</summary>
    void OnReset(BattleContext ctx);

    /// <summary>
    /// The part has left the composition (a war setting changed): drop what it holds. Called once, at its
    /// place in the <see cref="TickBeforeAllies"/> pass, where its tick would have run.
    /// </summary>
    void OnDeactivated(BattleContext ctx);

    /// <summary>Every ally is gone (despawned): forget them without touching them (every part).</summary>
    void OnAlliesDismissed(BattleContext ctx);

    /// <summary>Once a frame, before the allies act (composed parts only, in order).</summary>
    void TickBeforeAllies(BattleContext ctx, float deltaTime);

    /// <summary>The lord (the player) has fallen; once until the lord is up again (composed parts only, in order).</summary>
    void OnLordDown(BattleContext ctx);

    /// <summary>This frame the part drives this living ally instead of its fighting. True: it did.</summary>
    bool TakesOverAlly(BattleContext ctx, CombatCompanion ally, float deltaTime);

    /// <summary>This friend of the player's side kneels at a task, open to the enemy (drawn at x3; its cover too).</summary>
    bool Exposes(BattleContext ctx, uint friendId);

    /// <summary>This ally is busy with the part's task (raising, praying): not sent to another, not in the guard ring.</summary>
    bool Tends(BattleContext ctx, uint allyId);

    /// <summary>The one an enemy must cut down for its task (0: none). A change makes it draw again.</summary>
    uint HolderFor(BattleContext ctx, SimulatedNpc enemy);

    /// <summary>
    /// An enemy's weight on one of the player's side, after the shared weight: the part's multipliers applied
    /// to <paramref name="weight"/> in order (returned unchanged when none applies).
    /// </summary>
    float EnemyTargetWeight(BattleContext ctx, in EnemyTargetQuery query, float weight);

    /// <summary>An ally's target draw begins (before any candidate is weighed).</summary>
    void BeginAllyDraw(BattleContext ctx, CombatCompanion ally);

    /// <summary>An ally's weight on an enemy: the part's multipliers applied to <paramref name="weight"/> in order.</summary>
    float AllyTargetWeight(BattleContext ctx, in AllyTargetQuery query, float weight);

    /// <summary>The ally drops the target it holds and draws again now.</summary>
    bool ForcesRedraw(BattleContext ctx, CombatCompanion ally, SimulatedNpc current);

    /// <summary>What holds this ally to one place, or null: it fights wherever the fight goes.</summary>
    IAllyLeash? LeashFor(BattleContext ctx, CombatCompanion ally);

    /// <summary>The word before a name on the HP bars, or null: another part's, else "Fighting".</summary>
    string? StatusWord(BattleContext ctx, SimulatedEntityState who);

    // ---- declared for stage B6 (the engine side); not called yet ----

    /// <summary>Once a frame, after the engine and before the enemy AI (today: <c>Plugin.TickWar</c>).</summary>
    void TickAfterEngine(BattleContext ctx, float deltaTime);

    /// <summary>A blow that would kill one of the player's side (today: <c>CombatEngine</c>, hostages).</summary>
    LethalBlow OnLethalBlow(BattleContext ctx, SimulatedEntityState victim, SimulatedNpc by, bool primary);

    /// <summary>When the player dies, Enemy Control does not take over the killer (today: <c>Plugin.cs</c>).</summary>
    bool SuppressesKillerPossession(BattleContext ctx);

    /// <summary>The banner's title and reason, or null (today: <c>Plugin.War.cs</c>).</summary>
    OutcomeText? DescribeOutcome(BattleContext ctx, bool playerWon);
}

/// <summary>What a lethal blow does.</summary>
public enum LethalBlow
{
    Default,
    /// <summary>Left at 1 HP and taken captive.</summary>
    Capture,
}

/// <summary>The result banner's words.</summary>
public readonly record struct OutcomeText(string Title, string Reason);

/// <summary>An enemy weighing one of the player's side.</summary>
public readonly record struct EnemyTargetQuery(SimulatedNpc Enemy, uint Holder, uint CandidateId);

/// <summary>An ally weighing an enemy.</summary>
public readonly record struct AllyTargetQuery(CombatCompanion Ally, SimulatedNpc Enemy, Vector3 EnemyPosition);

/// <summary>
/// What holds an ally to one place (docs/PLUGIN_ARCHITECTURE.md §5.2, <c>LeashFor</c>): the anchor, the
/// radius, the way back and where it stands while nobody comes.
/// </summary>
public interface IAllyLeash
{
    /// <summary>The point the ally keeps within <see cref="Radius"/> of.</summary>
    Vector3 Anchor(BattleContext ctx, Vector3 playerPosition);

    /// <summary>The radius before the ally's own small reduction (the party range's randomness).</summary>
    float Radius { get; }

    /// <summary>
    /// Whether the ally is inside its leash, <paramref name="distance"/> from the anchor with the limit
    /// <paramref name="limit"/>: past it, it comes back, and keeps coming back until well inside.
    /// </summary>
    bool Inside(CombatCompanion ally, float distance, float limit);

    /// <summary>Where the ally goes while it has nothing to fight.</summary>
    Vector3 IdleGoal(BattleContext ctx, CombatCompanion ally, Vector3 anchor, Vector3 current, float limit);
}

/// <summary>Every hook does nothing, answers no, null, 0 or <see cref="LethalBlow.Default"/>, and leaves a weight as it is.</summary>
public abstract class BattleRule : IBattleRule
{
    public abstract string Id { get; }

    public virtual void OnReset(BattleContext ctx) { }
    public virtual void OnDeactivated(BattleContext ctx) { }
    public virtual void OnAlliesDismissed(BattleContext ctx) { }
    public virtual void TickBeforeAllies(BattleContext ctx, float deltaTime) { }
    public virtual void OnLordDown(BattleContext ctx) { }
    public virtual bool TakesOverAlly(BattleContext ctx, CombatCompanion ally, float deltaTime) => false;
    public virtual bool Exposes(BattleContext ctx, uint friendId) => false;
    public virtual bool Tends(BattleContext ctx, uint allyId) => false;
    public virtual uint HolderFor(BattleContext ctx, SimulatedNpc enemy) => 0;
    public virtual float EnemyTargetWeight(BattleContext ctx, in EnemyTargetQuery query, float weight) => weight;
    public virtual void BeginAllyDraw(BattleContext ctx, CombatCompanion ally) { }
    public virtual float AllyTargetWeight(BattleContext ctx, in AllyTargetQuery query, float weight) => weight;
    public virtual bool ForcesRedraw(BattleContext ctx, CombatCompanion ally, SimulatedNpc current) => false;
    public virtual IAllyLeash? LeashFor(BattleContext ctx, CombatCompanion ally) => null;
    public virtual string? StatusWord(BattleContext ctx, SimulatedEntityState who) => null;
    public virtual void TickAfterEngine(BattleContext ctx, float deltaTime) { }
    public virtual LethalBlow OnLethalBlow(BattleContext ctx, SimulatedEntityState victim, SimulatedNpc by, bool primary) => LethalBlow.Default;
    public virtual bool SuppressesKillerPossession(BattleContext ctx) => false;
    public virtual OutcomeText? DescribeOutcome(BattleContext ctx, bool playerWon) => null;
}
