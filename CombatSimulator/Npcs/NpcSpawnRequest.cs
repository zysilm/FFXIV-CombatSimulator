// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace CombatSimulator.Npcs;

/// <summary>Where a spawned opponent is put.</summary>
public enum SpawnPlacement
{
    /// <summary>In a loose ring around the player.</summary>
    Surround,
    /// <summary>Fanned out in front of the player.</summary>
    Front,
    /// <summary>Somewhere on the map, away from the player: a post it holds until it notices the party.</summary>
    Map,
}

public class NpcSpawnRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public uint BNpcNameId { get; set; }
    public uint BNpcBaseId { get; set; }
    public uint ENpcBaseId { get; set; }     // Non-zero for humanoid NPC (ENpcBase)
    public uint ModelCharaId { get; set; }   // Direct model fallback for ModelChara-only Monsters
    public Simulation.EnemyTier? Tier { get; set; } // null: taken from the enemy's own data
    public int Level { get; set; }                  // 0: the player's level
    public Vector3? Position { get; set; }
    public float? Rotation { get; set; }
    public bool IsRanged { get; set; } // Carries the per-NPC ranged-attack flag through respawn
    /// <summary>Fights at the player's side: handed to the companion system once it is drawn.</summary>
    public bool Ally { get; set; }
    public SpawnPlacement Placement { get; set; }
    /// <summary>Walks about its post while it has not noticed the party.</summary>
    public bool Patrol { get; set; }
    /// <summary>Requests with the same non-zero squad are placed together (one post on the map).</summary>
    public int Squad { get; set; }
}
