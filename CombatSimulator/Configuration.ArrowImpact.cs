// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Arrows loosed by any bow, friend or foe, stay lodged in the body they struck. Off by default.</summary>
    public bool ArrowImpactEnabled { get; set; } = false;

    /// <summary>Preferred landing bones (raw skeleton bone names). Empty = anywhere on the body.</summary>
    public List<string> ArrowImpactPreferredSpots { get; set; } = new();

    public int ArrowImpactMaxPerTarget { get; set; } = 12;

    /// <summary>Arrows fall away after <see cref="ArrowImpactLingerSeconds"/>; off, they stay as long as the body does.</summary>
    public bool ArrowImpactFallAway { get; set; } = false;

    /// <summary>Seconds an arrow stays before it falls away, when <see cref="ArrowImpactFallAway"/> is on.</summary>
    public float ArrowImpactLingerSeconds { get; set; } = 45f;

    /// <summary>A lodged arrow opens a wound that bleeds, where a bleeding effect is available. Off by default.</summary>
    public bool ArrowImpactBleed { get; set; } = false;
}
