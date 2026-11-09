// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

// Enemy skills: what enemies use, how it is telegraphed and how it resolves. Kit options apply to
// enemies spawned/registered after the change. Fixed behaviour: humanoid kits keep one area skill when
// the job has one; HP phases hold the longest cast until half HP and unlock an enraged area skill below
// 30%; area skills keep their real cast time (capped at 5 s, interruptible) when real shapes are on.
public partial class Configuration
{
    // Monsters (no job) use their own model family's real Action rows instead of auto-attack only.
    public bool EnemyMonsterRealSkills { get; set; } = true;
    public int EnemyMaxMonsterSkills { get; set; } = 3;

    // Telegraphs take the action's real shape (cone/rect/donut/circle, caster- or target-anchored),
    // snapshotted at commit; the hit at resolve uses the same snapshot and reaches everyone inside.
    public bool EnemyTelegraphRealShapes { get; set; } = true;
    // Spawn the game's own omen ground marker under each real-shape area (needs real shapes).
    public bool EnemyNativeOmenVfx { get; set; } = true;
    // Keep drawing the overlay outline even while a native omen marks the same area.
    public bool EnemyAreaOverlayWithOmen { get; set; } = false;
}
