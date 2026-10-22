// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Virtual enemies turn a bone (default head) toward their current target.
    /// On by default.</summary>
    public bool VirtualEnemyLookAtTarget { get; set; } = true;
    public string VirtualEnemyLookAtBone { get; set; } = "j_kao";
}
