// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Recipes;

/// <summary>
/// A battle profile: who fights on which side, and where the opponents stand. The War Generator
/// runs these, and saves the battle on the field as one. Built-in profiles are embedded JSON;
/// the user's own are JSON files in the plugin's configuration folder.
/// </summary>
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
    /// <summary>Allies cloned from players: the ones in sight, or the player's own character.</summary>
    public List<CombatRecipeCompanionGroup> Companions { get; set; } = new();
    /// <summary>Allies from the NPC catalog: any creature.</summary>
    public List<CombatRecipeEnemyGroup> Allies { get; set; } = new();
    public List<CombatRecipeEnemyGroup> Enemies { get; set; } = new();
    public List<CombatRecipeMapEnemyGroup> MapEnemies { get; set; } = new();

    /// <summary>Saved by the user (a file that can be overwritten and deleted), not built in.</summary>
    [JsonIgnore]
    public bool IsUser { get; set; }
}

public sealed class CombatRecipeCompanionGroup
{
    public CompanionRecipeType Type { get; set; }
    public int Count { get; set; }
}

/// <summary>A number of one creature from the NPC catalog, on either side.</summary>
public sealed class CombatRecipeEnemyGroup
{
    public NpcCatalogType Type { get; set; } = NpcCatalogType.Human;
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Occurrence { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary>The exact catalog row (with <see cref="Id"/> and <see cref="NameId"/>), when known: a saved battle has it.</summary>
    public NpcCatalogSource? Source { get; set; }
    public uint NameId { get; set; }
    /// <summary>Where the group stands. Unset: as the War Generator is set. Opponents only.</summary>
    public SpawnPlacement? Placement { get; set; }
    /// <summary>Walks about its post until it notices the party. Opponents only.</summary>
    public bool Patrol { get; set; }
    /// <summary>Unset: as the War Generator is set.</summary>
    public EnemyTier? Strength { get; set; }
    /// <summary>Unset: as the War Generator is set. 0: the player's level.</summary>
    public int? Level { get; set; }
}

public sealed class CombatRecipeMapEnemyGroup
{
    public bool Enabled { get; set; } = true;
    public bool IncludeBattleNpcs { get; set; } = true;
    public bool IncludePlayers { get; set; } = false;
    public int MaxCount { get; set; } = 10;
    public float SenseRange { get; set; } = 10.0f;
}

public enum CompanionRecipeType
{
    VisiblePlayers,
    Self,
    SelfRandomized,
}
