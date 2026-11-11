# Combat numbers: the budget model

Code: `CombatSimulator/Simulation/CombatBudgetModel.cs` (all numbers), `DamageCalculator.cs` (applies them),
`Npcs/KitBudget.cs` (measures an enemy's kit). Test: `Tests/CombatBalance`.

## Why

The old model derived damage from the character's attributes, level and gear, with a level table that was
never actually read (a 7-point linear fallback) and one attack coefficient for all levels. A naked level 15
Blue Mage did 2 damage per basic attack to a level 1 enemy and died in a few hits. Tuning it meant tuning
three coupled things (level, attributes, HP formula) per character.

## The model

A fight is decided in **potency per second**. Gear, job and attributes do not enter it, and level only as
the gap between enemy and player. The player's
maximum HP is read only to size the numbers on screen and cancels out of every outcome.

Each enemy has a **tier**, which is a contract:

| Tier   | Seconds to kill | HP an unavoided fight costs (classic) |
|--------|-----------------|----------------------------------------|
| Weak   | 4               | 5%                                     |
| Normal | 10              | 20%                                    |
| Strong | 20              | 50%                                    |
| Elite  | 35              | 85%                                    |
| Boss   | 90              | 250%                                   |

- **Player damage** = potency × maxHP / (11 s × reference output). Reference output is 330 potency/s in
  Action Mode and 110 in classic play, so a skill's potency still matters and the time to kill is the same
  in both modes.
- **Enemy HP** = player maxHP × seconds to kill / 11, halved for ranged and caster enemies, times the HP
  multiplier option.
- **Enemy damage** = potency × tier cost / (seconds to kill × the kit's own output) × target maxHP. The
  kit's output is found by playing the kit out with the AI's rules (animation lock, cooldowns, casts, MP),
  so a monster with one auto-attack and a job enemy with ten skills cost the same HP for the same tier,
  and a big skill still hits harder than an auto-attack. Skills gated behind an HP threshold are left out
  of the measurement, so phase skills are real escalation. One hit never exceeds 35% of HP (50% for a boss).
- **Action Mode** doubles enemy damage, because hits can be guarded there.
- **Companions** deal half the player's reference output whatever their kit.
- **Critical and direct hits** are normalised: they change the spread, not the average.

## Strength and level

- **Strength** (dropdown, default Auto): Auto reads the rank in the enemy's own data row
  (`BNpcBase.Rank`: 0 Normal, 1 Strong, 2 Boss, others Elite); townspeople, players and bare models are
  Normal. The other choices force one tier for every enemy.
- **Level** (slider, default "My level"): only the difference to the player's level matters. Every 20
  levels above the player doubles the enemy's HP and its damage (at most x4), below it weakens the same
  way. The single-hit cap rises with it, up to 90% of HP. The level also limits which skills its kit has.
- There is no HP multiplier any more; strength and level cover it.

## What replaced what

- `CombatStatProvider`, `NpcHpCalculator` and every attribute on `SimulatedEntityState` are gone.
- Options `DefaultNpcLevel`, `FastCombatLevel`, `DefaultNpcHpMultiplier` → `EnemyStrength`, `EnemyLevel`,
  `RecipeEnemyStrength`, `RecipeEnemyLevel` (all default 0: Auto, my level).

## Verification

`dotnet run --project Tests/CombatBalance -c Release` plays whole fights through the production
`DamageCalculator`, `KitBudget` and `VirtualActionModel`: identical fight from 100 to 300 000 max HP, every
tier within its contract in both modes, six kinds of kit within 96–124% of budget, packs, boss, companions.
