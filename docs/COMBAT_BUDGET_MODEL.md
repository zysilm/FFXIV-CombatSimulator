# Combat numbers: the budget model

Code: `CombatSimulator/Simulation/CombatBudgetModel.cs` (all numbers), `DamageCalculator.cs` (applies them).
Test: `Tests/CombatBalance`.

## 2026-11-13: one set of numbers for everyone

At the user's request ("同一套数值 公平计算") damage and MP are now the same for the player, allies and
enemies. This replaces the per-tier damage budget described further down (kept for its history):

- **Damage**: a point of potency does the same damage whoever spends it,
  `CombatBudgetModel.DamagePerPotency` = the player's own rate (reference output deals the player's HP in
  11 s), times the attacker's level worth against the player's. No per-kit normalisation (`KitBudget` is
  gone), no companion share, no per-hit cap, no Action Mode threat factor. A kit with bigger skills hits
  harder, from either side.
- **MP**: a skill costs everyone the same share of their own pool as it costs the player of theirs
  (`EnemySkillMpCost` = price / 10 000 of the pool). Allies pay MP now too.
- **Tier = HP only**: an enemy's tier is how long the player needs to kill it (Weak 4 s, Normal 10 s,
  about the player's HP, Strong 20, Elite 35, Boss 90). It no longer changes how hard it hits.

Measured with `Tests/CombatBalance` (classic play, 180 000 HP player): a Normal enemy fighting with the
player's own weaponskill is an even duel (the player wins with 24% HP left; two of them win every time);
a Boss with that kit kills the player in 7 s. An enemy with a full job kit (big skills on cooldowns) deals
152% of the player's HP per 10 s if unanswered, an auto-attack-only one 26%. In Action Mode the player's
potency is worth a third (they swing every 0.4 s), and so is everyone's: 58% and 12%.

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
- **Level** (slider, default "My level"): only the gap to the player's level matters, and it counts as
  levels do in the game. Each level has a worth (`CombatBudgetModel.LevelWorth`, the HP a typically
  equipped character of that level has: 150 at level 1, 4 500 at 50, 160 000 at 100); the enemy's HP and
  its damage are both multiplied by its level's worth over the player's. Five levels above is roughly
  1.4x, ten levels 2-3x, and a level 100 enemy against a level 15 character has about 280 times the HP
  and kills in one hit, with no cap. Equal levels give 1 at any level, so a naked level 15 character
  and a geared level 100 one still have the same fight against an enemy of their own level. The level
  also limits which skills the enemy's kit has.
- There is no HP multiplier any more; strength and level cover it.

## What replaced what

- `CombatStatProvider`, `NpcHpCalculator` and every attribute on `SimulatedEntityState` are gone.
- Options `DefaultNpcLevel`, `FastCombatLevel`, `DefaultNpcHpMultiplier` → `EnemyStrength`, `EnemyLevel`,
  (default 0: Auto, my level); they apply to every enemy, a recipe's included.

## Verification

`dotnet run --project Tests/CombatBalance -c Release` plays whole fights through the production
`DamageCalculator`, `KitBudget` and `VirtualActionModel`: identical fight from 100 to 300 000 max HP, every
tier within its contract in both modes, six kinds of kit within 96–124% of budget, packs, boss, companions.
