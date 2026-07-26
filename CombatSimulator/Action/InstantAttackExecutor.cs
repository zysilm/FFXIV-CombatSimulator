// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Behavior-equivalent default: routes straight to <see cref="CombatEngine.ProcessNpcAction"/>,
/// which already applies damage + visuals with no range gate (range gating lives in
/// the NPC AI). Used whenever Action Mode is off.
/// </summary>
public sealed class InstantAttackExecutor : IAttackExecutor
{
    private readonly CombatEngine engine;

    public InstantAttackExecutor(CombatEngine engine) => this.engine = engine;

    public bool Execute(SimulatedNpc source, in NpcAttackRequest req)
        => engine.ProcessNpcAction(source, req.ActionId, req.TargetId, req.Potency, req.Style, req.Radius).Success;
}
