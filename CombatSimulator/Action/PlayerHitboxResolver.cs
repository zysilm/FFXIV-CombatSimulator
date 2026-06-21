using System;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Action Mode soft-target resolver. It chooses a primary enemy from a player-facing
/// selection cone; <see cref="CombatEngine.ApplyPlayerActionMode"/> then fans the hit
/// out through the action's real shape/range data.
/// </summary>
public sealed class PlayerHitboxResolver
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly Configuration config;

    public PlayerHitboxResolver(CombatEngine combatEngine, NpcSelector npcSelector, Configuration config)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.config = config;
    }

    /// <summary>
    /// Soft-target pick: the single best enemy inside a frontal selection cone.
    /// Melee picks the nearest; ranged picks the one closest to the player's facing.
    /// </summary>
    public SimulatedNpc? ResolvePrimary(float selectRange, float selectAngleDeg, bool smallestAngle)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var pos = player.Position;
        var rot = player.Rotation;
        var forward = new Vector3(MathF.Sin(rot), 0f, MathF.Cos(rot));
        var range = MathF.Max(0.5f, selectRange);
        var aim = pos + forward * range;
        var fwd2 = new Vector2(forward.X, forward.Z);

        SimulatedNpc? best = null;
        var bestMetric = float.MaxValue;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.State.IsAlive)
                continue;

            var npos = combatEngine.GetSimulatedEntityPosition(npc.State);
            if (!CombatGeometry.IsInsideConeAngle(pos, aim, npos, range, selectAngleDeg))
                continue;

            float metric;
            if (smallestAngle)
            {
                if (!CombatGeometry.TryGetDirection2D(pos, npos, out var toC))
                    continue;
                metric = -Vector2.Dot(fwd2, toC);
            }
            else
            {
                metric = CombatGeometry.Distance2D(pos, npos);
            }

            if (metric < bestMetric)
            {
                bestMetric = metric;
                best = npc;
            }
        }

        return best;
    }

    /// <summary>
    /// The current basic-attack soft target, using the job-based melee/ranged selection cone.
    /// Shared by the attack itself and the reticle overlay so they always agree.
    /// </summary>
    public SimulatedNpc? ResolveBasicAttackPrimary()
    {
        var ranged = IsRangedBasicAttackJob();
        var range = ranged ? config.RangedBasicRange : config.PlayerHitboxRange;
        var angle = ranged ? config.RangedSelectAngleDeg : config.PlayerHitboxAngleDeg;
        return ResolvePrimary(range, angle, ranged);
    }

    /// <summary>Bard/Archer + Astrologian fire a ranged basic attack; everyone else is melee.</summary>
    public static bool IsRangedBasicAttackJob()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        return player != null && player.ClassJob.RowId is 5 or 23 or 31 or 38;
    }
}
