// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Npcs;

namespace CombatSimulator.ActionCombat;

/// <summary>A committed NPC/companion attack, decoupled from how it lands.</summary>
public readonly record struct NpcAttackRequest(
    uint ActionId,
    ulong TargetId,
    int Potency,
    NpcAttackStyle Style,
    float Radius,
    float CastTime,
    bool IsAutoAttack = false);

/// <summary>
/// Seam 2: how an enemy attack resolves. The enemy AI (skills, pathing, HP) is
/// unchanged — it just commits an attack through this interface. The default
/// <see cref="InstantAttackExecutor"/> reproduces the original range-gated/instant
/// behavior; <see cref="TelegraphedAttackExecutor"/> defers to a snapshot telegraph
/// + active-frame hitbox.
/// </summary>
public interface IAttackExecutor
{
    bool Execute(SimulatedNpc source, in NpcAttackRequest req);
}
