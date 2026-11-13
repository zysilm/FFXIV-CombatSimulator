// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

// Enhanced enemy tactics: where enemies choose to stand and whom they fight. The crowd on the
// navigation mesh does the moving. With the master switch on, enemies circle and reposition while
// holding range, pick targets by threat, take turns in melee, call for help / flee at low HP and (when
// big) turn slowly. Off = they walk straight to their reach and fight.
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
