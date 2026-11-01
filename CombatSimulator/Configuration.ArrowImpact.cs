// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Arrows loosed by any bow, friend or foe, stay lodged in the body they struck. Off by default.</summary>
    public bool ArrowImpactEnabled { get; set; } = false;

    /// <summary>Preferred landing spots (keys of <see cref="Effects.ArrowImpactController.HumanSpots"/>).
    /// Empty = anywhere on the body.</summary>
    public List<string> ArrowImpactPreferredSpots { get; set; } = new();

    public int ArrowImpactMaxPerTarget { get; set; } = 12;

    /// <summary>Seconds an arrow stays before it falls away.</summary>
    public float ArrowImpactLingerSeconds { get; set; } = 45f;
}
