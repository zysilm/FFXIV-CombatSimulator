namespace CombatSimulator.Npcs;

public sealed class MapEnemySettings
{
    public bool Enabled { get; set; }
    public int MaxCount { get; set; } = 10;
    public float SenseRange { get; set; } = 10.0f;
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
    public NpcBehaviorType BehaviorType { get; set; } = NpcBehaviorType.BasicMelee;
}
