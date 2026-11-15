// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Companions;

namespace CombatSimulator.Combat;

/// <summary>
/// Who is on the player's side of the field (docs/PLUGIN_ARCHITECTURE.md §6.1): the allies' list, owned
/// here and kept by the allies' controller (<see cref="CombatCompanionManager"/>), and the questions the
/// engine asks about it. Built before the engine and given to both by constructor, so the engine no
/// longer needs the allies' controller for them (the queries it was handed after construction).
/// </summary>
public sealed class CombatRoster
{
    private readonly ICombatRosterSettings settings;
    private readonly List<CombatCompanion> allies = new();

    public CombatRoster(ICombatRosterSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>The allies' list itself, for the controller that adds, removes and orders them.</summary>
    internal List<CombatCompanion> AllyList => allies;

    /// <summary>Every ally, in the controller's order.</summary>
    public IReadOnlyList<CombatCompanion> Allies => allies;

    /// <summary>The allies take part and one of them is spawned and alive.</summary>
    public bool HasLivingAllies => settings.EnableCombatCompanions && allies.Any(c => c.IsSpawned && c.State.IsAlive);

    public CombatCompanion? Ally(uint entityId) => allies.FirstOrDefault(c => c.SimulatedEntityId == entityId);

    /// <summary>The character address of an ally (null: no such ally).</summary>
    public nint? AddressOf(uint entityId) => Ally(entityId)?.Address;
}
