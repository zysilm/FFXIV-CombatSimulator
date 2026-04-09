using System.Numerics;

namespace CombatSimulator.Npcs;

public class NpcSpawnRequest
{
    public uint BNpcNameId { get; set; }
    public uint BNpcBaseId { get; set; }
    public uint ENpcBaseId { get; set; }     // Non-zero for humanoid NPC (ENpcBase)
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
    public Vector3? Position { get; set; }
    public float? Rotation { get; set; }
    public NpcBehaviorType BehaviorType { get; set; } = NpcBehaviorType.BasicMelee;
}
