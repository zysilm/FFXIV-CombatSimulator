// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

// Enhanced enemy tactics: movement, spacing and target-selection behaviours layered on the enemy
// approach and the party engage planner. The master switch defaults off, and with it off every
// behaviour below is inert, so existing setups move exactly as before. Sub-switches default on so
// enabling the master gives the full set; each can then be turned off on its own to isolate it.
public partial class Configuration
{
    public bool EnhancedEnemyTactics { get; set; } = false;

    // Party mode no longer force-engages every enemy every frame: enemies start idle and join on
    // aggro, auto-engage (Dev > NPC auto-engage), a hit, or an ally's call for help.
    public bool EnemyTacticsRespectEngage { get; set; } = true;

    // Enemies on the player get spread slots from the engage planner (preferring flanks/rear)
    // instead of each walking to its own pursuit point, where they stacked ~0.3y apart.
    public bool EnemyTacticsPlayerSlots { get; set; } = true;

    // Spacing accounts for body size above a humanoid's: big monsters are not walked into, and
    // melee ring slots keep neighbours from overlapping.
    public bool EnemyTacticsHitboxSpacing { get; set; } = true;

    // Enemies holding range now and then walk a short arc around their target.
    public bool EnemyTacticsStrafe { get; set; } = true;

    // Enemy target choice follows a decaying threat table (damage dealt, tanks x3) instead of
    // recent DPS, with wider switch hysteresis.
    public bool EnemyTacticsThreat { get; set; } = true;

    // At most three melee enemies fight one target at once; the rest wait on an outer ring and
    // step in as places free. Engage delays also get a little random jitter.
    public bool EnemyTacticsStagger { get; set; } = true;

    // An enemy entering the fight wakes idle allies within its aggro range (10y when unset); below
    // 20% HP about a third of enemies run to an ally once, then return.
    public bool EnemyTacticsFlee { get; set; } = true;

    public bool TacticsStagger => EnhancedEnemyTactics && EnemyTacticsStagger;
    public bool TacticsFlee => EnhancedEnemyTactics && EnemyTacticsFlee;

    // Ranged/caster enemies sometimes move to a new firing spot after an attack (8s cooldown);
    // any enemy standing inside another body takes one short step out (1.5s cooldown).
    public bool EnemyTacticsReposition { get; set; } = true;

    public bool TacticsReposition => EnhancedEnemyTactics && EnemyTacticsReposition;
    public bool TacticsStrafe => EnhancedEnemyTactics && EnemyTacticsStrafe;
    public bool TacticsThreat => EnhancedEnemyTactics && EnemyTacticsThreat;
    public bool TacticsRespectEngage => EnhancedEnemyTactics && EnemyTacticsRespectEngage;
    public bool TacticsPlayerSlots => EnhancedEnemyTactics && EnemyTacticsPlayerSlots;
    public bool TacticsHitboxSpacing => EnhancedEnemyTactics && EnemyTacticsHitboxSpacing;
}
