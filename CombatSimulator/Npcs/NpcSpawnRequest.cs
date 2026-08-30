// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace CombatSimulator.Npcs;

public class NpcSpawnRequest
{
    public uint BNpcNameId { get; set; }
    public uint BNpcBaseId { get; set; }
    public uint ENpcBaseId { get; set; }     // Non-zero for humanoid NPC (ENpcBase)
    public uint ModelCharaId { get; set; }   // Direct model fallback for ModelChara-only Monsters
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
    public Vector3? Position { get; set; }
    public float? Rotation { get; set; }
    public bool IsRanged { get; set; } // Carries the per-NPC ranged-attack flag through respawn
}
