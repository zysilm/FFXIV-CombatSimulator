// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
