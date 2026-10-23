// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Virtual enemies turn a bone (default head) toward their current target.
    /// Off by default; living enemies keep watching the player after defeat.</summary>
    public bool VirtualEnemyLookAtTarget { get; set; } = false;
    public string VirtualEnemyLookAtBone { get; set; } = "j_kao";
}
