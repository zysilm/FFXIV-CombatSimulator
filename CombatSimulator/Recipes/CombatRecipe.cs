using System.Collections.Generic;
using CombatSimulator.Npcs;

namespace CombatSimulator.Recipes;

public sealed class CombatRecipe
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<CombatRecipeCompanionGroup> Companions { get; set; } = new();
    public List<CombatRecipeEnemyGroup> Enemies { get; set; } = new();
    public List<CombatRecipeMapEnemyGroup> MapEnemies { get; set; } = new();
}

public sealed class CombatRecipeCompanionGroup
{
    public CompanionRecipeType Type { get; set; }
    public int Count { get; set; }
}

public sealed class CombatRecipeEnemyGroup
{
    public NpcCatalogType Type { get; set; } = NpcCatalogType.Human;
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Occurrence { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
    public float HpMultiplier { get; set; } = 1.0f;
    public NpcBehaviorType Behavior { get; set; } = NpcBehaviorType.BasicMelee;
}

public sealed class CombatRecipeMapEnemyGroup
{
    public bool Enabled { get; set; } = true;
    public int MaxCount { get; set; } = 10;
    public float SenseRange { get; set; } = 10.0f;
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
    public NpcBehaviorType Behavior { get; set; } = NpcBehaviorType.BasicMelee;
}

public enum CompanionRecipeType
{
    VisiblePlayers,
    Self,
    SelfRandomized,
}
