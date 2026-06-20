using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Selects the enemies a player light attack connects with: a frontal cone in the
/// player's facing direction, tested with the shared <see cref="CombatGeometry"/>.
/// This is what replaces the sim mode's "any target, infinite range" — face away and
/// you whiff.
/// </summary>
public sealed class PlayerHitboxResolver
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly Configuration config;
    private readonly List<uint> buffer = new();

    public PlayerHitboxResolver(CombatEngine combatEngine, NpcSelector npcSelector, Configuration config)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.config = config;
    }

    /// <summary>Living enemy sim ids inside the player's frontal melee cone.</summary>
    public IReadOnlyList<uint> ResolveConeTargets()
    {
        buffer.Clear();
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return buffer;

        var pos = player.Position;
        var rot = player.Rotation;
        var forward = new Vector3(MathF.Sin(rot), 0f, MathF.Cos(rot));
        var aim = pos + forward * config.PlayerHitboxRange;

        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.State.IsAlive)
                continue;
            var npos = combatEngine.GetSimulatedEntityPosition(npc.State);
            if (CombatGeometry.IsInsideConeAngle(pos, aim, npos, config.PlayerHitboxRange, config.PlayerHitboxAngleDeg))
                buffer.Add(npc.SimulatedEntityId);
        }

        return buffer;
    }

    /// <summary>
    /// Soft-target pick: the single best enemy inside a frontal selection cone (player facing).
    /// Melee picks the NEAREST; ranged (<paramref name="smallestAngle"/>) picks the one most directly
    /// in front. Returns null if the cone is empty (a whiff). The caller then hands the primary to
    /// <see cref="CombatEngine.ApplyPlayerActionMode"/>, which fans out the real action shape.
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
                metric = -Vector2.Dot(fwd2, toC); // bigger dot = smaller angle = better (smaller metric)
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
}
