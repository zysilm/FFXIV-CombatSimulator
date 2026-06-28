using System.Collections.Generic;
using CombatSimulator.Npcs;

namespace CombatSimulator.Simulation;

/// <summary>
/// Narrow seam the combat engine uses to drive the (dev-only) cinematic victory sequence without
/// naming the concrete dev controller. Supplied at runtime; null means "no cinematic sequence".
/// </summary>
public interface IVictorySequence
{
    void Stop();
    void TrackTarget(IReadOnlyList<SimulatedNpc> npcs);
    (bool Started, SimulatedNpc? CinematicNpc) TryStart(IReadOnlyList<SimulatedNpc> npcs);
}
