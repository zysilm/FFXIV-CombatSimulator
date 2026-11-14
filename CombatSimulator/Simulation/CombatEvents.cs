// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// What the combat engine tells the rest of the plugin (docs/PLUGIN_ARCHITECTURE.md §6.1–§6.3): the
/// notifications, apart from the queries and commands it is given. One object, owned by the engine
/// (<see cref="CombatEngine.Events"/>); the composition root (Plugin.cs) subscribes in one ordered list.
///
/// Each one is a typed delegate slot, not a C# <c>event</c>, so the composition root keeps today's two
/// ways of wiring exactly: <c>+=</c> adds a listener after those already there (they run in that order),
/// <c>=</c> makes the one handler (and drops anything set before it). The engine's older members
/// (<c>OnSimulationReset</c>, <c>OnPlayerDeath</c>, ...) read and write these same slots, so code that
/// still uses them sees the same listeners in the same order.
/// </summary>
public sealed class CombatEvents
{
    /// <summary>A simulation started (<c>CombatEngine.StartSimulation</c>).</summary>
    public Action? SimulationStarted { get; set; }

    /// <summary>The simulation was stopped or reset (<c>StopSimulation</c>, <c>ResetState</c>); the state's
    /// <c>IsActive</c> tells which (still true on a reset mid-battle).</summary>
    public Action? SimulationReset { get; set; }

    /// <summary>An outcome was decided (true = the player's side won); once per outcome.</summary>
    public Action<bool>? OutcomeDecided { get; set; }

    /// <summary>A hit landed and took HP: (target, the hit, who struck it if known).</summary>
    public Action<SimulatedEntityState, DamageResult, SimulatedEntityState?>? DamageLanded { get; set; }

    /// <summary>An enemy died (its character address).</summary>
    public Action<nint>? EnemyDied { get; set; }

    /// <summary>Just before the local player's death animation starts.</summary>
    public Action? BeforePlayerDeath { get; set; }

    /// <summary>The local player died (its address).</summary>
    public Action<nint>? PlayerDied { get; set; }

    /// <summary>An enemy hit took HP off the player (the killing blow too).</summary>
    public Action? PlayerDamaged { get; set; }

    /// <summary>An enemy's attack landed on the still-living player (the attacker's simulated id).</summary>
    public Action<uint>? PlayerHitBy { get; set; }
}
