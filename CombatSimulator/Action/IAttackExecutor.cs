using CombatSimulator.Npcs;

namespace CombatSimulator.ActionCombat;

/// <summary>A committed NPC/companion attack, decoupled from how it lands.</summary>
public readonly record struct NpcAttackRequest(
    uint ActionId,
    ulong TargetId,
    int Potency,
    NpcAttackStyle Style,
    float Radius,
    float CastTime);

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
