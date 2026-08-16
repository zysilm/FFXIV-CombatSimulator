using System;

namespace CombatSimulator.Npcs;

/// <summary>
/// One enemy-bone/player-bone grab pair. <see cref="Configuration.EnemyControlGrabPairs"/> holds a list
/// of these — arbitrarily many can be live at once, e.g. one hand on the neck and the other on the
/// pelvis for a carry/lift pose.
/// </summary>
[Serializable]
public class EnemyControlGrabPairConfig
{
    public string NpcBone { get; set; } = "j_te_r";
    public string PlayerBone { get; set; } = "j_kubi";
}
