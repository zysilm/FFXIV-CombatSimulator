// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Combat;

namespace CombatSimulator.Ai;

/// <summary>
/// What drives one enemy this frame. Exactly one layer owns an enemy at a time, chosen per enemy by
/// <see cref="EnemyLayers.Of"/> in this order (the first that applies wins):
/// <list type="number">
/// <item><see cref="Controlled"/>: something outside the enemy AI holds it (Enemy Control,
/// a private director). The AI neither moves nor decides for it, does not write its state, and leaves it out of
/// everyone else's decisions (it raises no alarm, answers no call for help). Released, it carries on from the
/// layer it was in.</item>
/// <item><see cref="Dead"/>.</item>
/// <item><see cref="Seizing"/>: going for the fallen lord's body (Warring States with Retreat).</item>
/// <item><see cref="Fighting"/>: engaged (engaging, in combat).</item>
/// <item><see cref="Patrolling"/>: a sentry that has not noticed the fight and walks about its post.</item>
/// <item><see cref="HoldingPost"/>: a sentry that has not noticed the fight and keeps to its post.</item>
/// <item><see cref="Idle"/>: anything else (an enemy that is not a sentry, with nothing to fight).</item>
/// </list>
/// Patrol is each enemy's own: nothing turns it off for everyone. One enemy's layer changes another's
/// only through <see cref="EnemyLayers.RaisesAlarm"/> (a sentry wakes when a neighbour fights someone
/// alive), which a controlled enemy never does.
/// </summary>
public enum EnemyLayer
{
    Controlled,
    Dead,
    Seizing,
    Fighting,
    Patrolling,
    HoldingPost,
    Idle,
}

/// <summary>The layer choice (pure, so it is checked offline in Tests/AiMovement).</summary>
public static class EnemyLayers
{
    /// <summary>The layer that drives an enemy this frame (see <see cref="EnemyLayer"/>).</summary>
    public static EnemyLayer Of(bool controlled, bool alive, bool seizing, CombatantAiState state, bool sentry, bool patrols)
    {
        if (controlled)
            return EnemyLayer.Controlled;
        if (!alive || state == CombatantAiState.Dead)
            return EnemyLayer.Dead;
        if (seizing)
            return EnemyLayer.Seizing;
        if (state is CombatantAiState.Engaging or CombatantAiState.Combat or CombatantAiState.Chasing)
            return EnemyLayer.Fighting;
        if (sentry && state == CombatantAiState.Idle)
            return patrols ? EnemyLayer.Patrolling : EnemyLayer.HoldingPost;
        return EnemyLayer.Idle;
    }

    /// <summary>A sentry that has not noticed the fight: it watches for the party and its neighbours.</summary>
    public static bool IsUnaware(EnemyLayer layer) => layer is EnemyLayer.Patrolling or EnemyLayer.HoldingPost;

    /// <summary>
    /// Whether an enemy in <paramref name="layer"/> wakes the unaware sentries near it: only one that is
    /// fighting (or going for the body) and has someone alive to fight. A controlled enemy never does (its AI
    /// state is stale while it is held), nor one left engaged with nobody alive to fight (a fight that is over
    /// would otherwise keep every sentry around it awake for good).
    /// </summary>
    public static bool RaisesAlarm(EnemyLayer layer, bool targetAlive)
        => (layer is EnemyLayer.Fighting or EnemyLayer.Seizing) && targetAlive;

    /// <summary>Whether something may wake an enemy in this layer (an alarm, a call for help, auto-engage).</summary>
    public static bool CanBeWoken(EnemyLayer layer) => layer is not (EnemyLayer.Controlled or EnemyLayer.Dead);
}
