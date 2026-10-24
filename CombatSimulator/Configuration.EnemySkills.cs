// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

// Enemy skills: what enemies use, how it is telegraphed and how it resolves. Every switch here
// defaults off; with all of them off enemies behave exactly as before. Kit options apply to
// enemies spawned/registered after the change.
public partial class Configuration
{
    // Monsters (no job) use their own model family's real Action rows instead of auto-attack only.
    public bool EnemyMonsterRealSkills { get; set; } = false;
    public int EnemyMaxMonsterSkills { get; set; } = 3;
}
