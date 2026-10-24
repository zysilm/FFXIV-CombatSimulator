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

    // Telegraphs take the action's real shape (cone/rect/donut/circle, caster- or target-anchored),
    // snapshotted at commit; the hit at resolve uses the same snapshot and reaches everyone inside.
    public bool EnemyTelegraphRealShapes { get; set; } = false;
    // Spawn the game's own omen ground marker under each real-shape area (needs real shapes).
    public bool EnemyNativeOmenVfx { get; set; } = false;
    // Skills with a drawable area get their real cast time back (cast bar + area during the cast,
    // snapshot at cast start, interruptible). Needs real shapes; skills without an area stay instant.
    public bool EnemyTelegraphedCasts { get; set; } = false;
    public float EnemyMaxCastSeconds { get; set; } = 5f;
    // Keep drawing the overlay outline even while a native omen marks the same area.
    public bool EnemyAreaOverlayWithOmen { get; set; } = false;
}
