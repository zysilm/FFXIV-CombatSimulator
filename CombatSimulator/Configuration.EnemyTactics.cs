// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

// Enhanced enemy tactics: movement, spacing and target-selection behaviours layered on the enemy
// approach and the party engage planner. With the master switch on, enemies spread around the player,
// keep body-size aware spacing, circle and reposition while holding range, pick targets by threat,
// take turns in melee, call for help / flee at low HP and (when big) turn slowly. Off = enemies move
// exactly as before.
public partial class Configuration
{
    public bool EnhancedEnemyTactics { get; set; } = true;

    // Party mode no longer force-engages every enemy every frame: enemies start idle and join on
    // aggro, auto-engage (Dev > NPC auto-engage), a hit, or an ally's call for help.
    // Off by default: public builds have no auto-engage UI, so with this on the enemies there would
    // only ever fight after the player strikes first.
    public bool EnemyTacticsRespectEngage { get; set; } = false;

    // How many melee enemies may fight one target at once when taking turns (3..20).
    public int EnemyTacticsMeleeAttackers { get; set; } = 8;

    // An engaged enemy more than 15y from its target sprints (1.8x) until within 8y.
    public bool EnemyTacticsCharge { get; set; } = true;

    public bool TacticsCharge => EnhancedEnemyTactics && EnemyTacticsCharge;
    public bool TacticsRespectEngage => EnhancedEnemyTactics && EnemyTacticsRespectEngage;
}
