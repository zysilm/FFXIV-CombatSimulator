using System.Collections.Generic;

namespace CombatSimulator.Simulation;

public class SimulationState
{
    public Dictionary<uint, SimulatedEntityState> Entities { get; } = new();

    public SimulatedEntityState PlayerState { get; set; } = new();

    public bool IsActive { get; set; }
    public float SimulationTime { get; set; }

    // Combat stats
    public long TotalDamageDealt { get; set; }
    public long TotalDamageTaken { get; set; }
    public long TotalHealingDone { get; set; }
    public float CombatStartTime { get; set; }
    public float CombatDuration => IsActive ? SimulationTime - CombatStartTime : 0;
    public float Dps => CombatDuration > 0 ? TotalDamageDealt / CombatDuration : 0;

    public void Reset()
    {
        foreach (var entity in Entities.Values)
            entity.Reset();

        PlayerState.Reset();
        SimulationTime = 0;
        TotalDamageDealt = 0;
        TotalDamageTaken = 0;
        TotalHealingDone = 0;
        CombatStartTime = 0;
    }

    public SimulatedEntityState? GetEntity(uint entityId)
    {
        if (PlayerState.EntityId == entityId)
            return PlayerState;
        return Entities.GetValueOrDefault(entityId);
    }

    public SimulatedEntityState? GetEntity(ulong entityId)
    {
        return GetEntity((uint)entityId);
    }
}
