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
}
