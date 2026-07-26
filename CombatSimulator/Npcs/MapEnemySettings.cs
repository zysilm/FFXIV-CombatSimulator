// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Npcs;

public sealed class MapEnemySettings
{
    public bool Enabled { get; set; }
    public bool IncludeBattleNpcs { get; set; } = true;
    public bool IncludePlayers { get; set; } = false;
    public int MaxCount { get; set; } = 10;
    public float SenseRange { get; set; } = 10.0f;
    public int Level { get; set; } = 90;
    public float HpMultiplier { get; set; } = 1.0f;
}
