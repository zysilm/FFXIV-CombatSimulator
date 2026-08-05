// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Npcs;

namespace CombatSimulator.Recipes;

public sealed class CombatRecipe
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>
    /// Forces ragdoll NPC collision on or off for as long as this recipe is running. Leave unset
    /// (the usual case) and the user's own setting stands.
    ///
    /// It exists for the recipes that field a lot of bodies at once: NPC collision repositions and
    /// re-bounds a collider per tracked NPC per corpse per frame, so its cost goes up with the
    /// product, and a wipe is exactly when it is highest and least noticed.
    /// </summary>
    public bool? NpcCollision { get; set; }
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
}

public sealed class CombatRecipeMapEnemyGroup
{
    public bool Enabled { get; set; } = true;
    public bool IncludeBattleNpcs { get; set; } = true;
    public bool IncludePlayers { get; set; } = false;
    public int MaxCount { get; set; } = 10;
    public float SenseRange { get; set; } = 10.0f;
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
}

public enum CompanionRecipeType
{
    VisiblePlayers,
    Self,
    SelfRandomized,
}
