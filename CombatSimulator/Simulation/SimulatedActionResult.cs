// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Simulation;

public class SimulatedActionResult
{
    public bool Success { get; set; }
    public string? FailReason { get; set; }
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public bool IsCombo { get; set; }
    public uint ActionId { get; set; }
    public uint SourceId { get; set; }
    public ulong TargetId { get; set; }
    public bool TargetKilled { get; set; }
}
