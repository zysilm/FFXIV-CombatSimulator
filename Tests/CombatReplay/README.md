# Combat decision replay (golden files)

Run `dotnet run -c Release` from this folder: compares the output with the golden files in `golden/` and exits 1
at the first different line. `dotnet run -c Release -- --write` rewrites them (only on purpose, after a behaviour
change the user approved); `-- --write-missing` writes only the files that do not exist yet (a new scenario, recorded
from the code before the change it is to check). "Identical" means byte for byte on the same machine and runtime
(docs/PLUGIN_ARCHITECTURE.md §8.1); `golden/` is marked `-text` so git does not change its line endings.

Two levels. The function level and the three `<scenario>.txt` files were built at refactor stage B1 and cover
what the extracted pure code (`CombatSimulator/Combat/CombatantMath.cs`, `AggroModel.cs`, linked) decides, in a
loop of this harness's own. The round level (`golden/loop-*.txt`, stage B4a) runs the two REAL combat loops,
`NpcAiController` and `CombatCompanionManager` (production sources linked), over their seams
(`CombatSimulator/Combat/CombatLoopSeams.cs`) in a flat fake world (`LoopReplay.cs`); see the end of this file.

- `golden/functions.txt` — the function level of §8.1: every `CombatantMath` / `AggroModel` function on a fixed
  input table (gaps, stand points, standing gap, reach hysteresis, the settings' reach scale, auto/skill/current
  reach for both policies, MP prices, pace, speed, turn rate and turning, threat share, weights, the due check,
  "hit by someone new", draw and hold with seeded generators). Floats as raw bits; any NaN is written `NaN`
  (its payload is not fixed, see Tests/CombatMathIdentity).
- `golden/<scenario>.txt` — three scenarios, each with seeds 1, 2, 3 and three time steps (1/60 s, 1/30 s, a fixed
  jittered sequence), 30 s at most:
  - `field-8v8-melee` — 8 allies (2 tanks) and the player against 8 melee enemies (two big ones), Stay down.
  - `mixed-ranged-magic` — melee, ranged and casters on both sides, enemies short of MP, range scale 1.25, skill
    rate limit 1/s.
  - `guard-body` — Warring States with retreat: the lord's body, one ally praying over it (x4 damage, an
    opening), six guards (10 m leash, x30 on the seizers, near-body bonus, x0.05 out of reach), four of eight
    enemies going for the body (x100 on its holder).

  Lines: `D` a target draw (who, old -> new target, hold-until, every candidate's weight), `R` an in-reach change
  (gap, reach, standing gap), `A` an attack (auto or skill id, damage, HP left, MP), `X` a death, `S` a snapshot of
  every fighter every 2 s (HP, MP, position, facing, current reach, standing gap, in reach, target, auto timer,
  lock, cooldowns), `end`.

Seeds as §8.1: enemy skill draws `Random(s)`, ally timers `Random(s+1)`, the aggro seed source `Random(s+2)`, the
production `DamageCalculator` with `Random(s+3)`.

What comes from production code: every distance, reach, standing, hysteresis, speed, turning, pace and MP-price
decision (`CombatantMath` with `ReachPolicy` / `MovePolicy` per side and the settings' `ReachScale`), every target
choice (`AggroModel`: weights, draw, hold, due check, hit by someone new; the situation multipliers as
`CombatCompanionManager.Aggro.cs` applies them), damage (`DamageCalculator`), MP regeneration
(`SimulatedEntityState`), threat (`EnemyMovementMath`).

What is the harness's own (a stand-in, not the plugin's loop): the frame order (threat decay, allies look then
enemies, allies act, a straight-line "crowd" step, time advances, enemies act), the action step (it follows the two
controllers' rules: random ready skill with a shared token bucket for enemies, highest priority for allies, lock
0.6 s), skills dealt as a bare potency, no casts or telegraphs, no tactics (flee, strafe, charge, melee slots), no
navigation or body blocking, no rescue, prayer or seizure progress (rules come with B5), the player stands still.

## The round level (`golden/loop-*.txt`, stage B4a)

The same three scenarios (`loop-field-8v8-melee`, `loop-mixed-ranged-magic`, `loop-guard-body`), seeds 1–3, time steps
1/60 s, 1/30 s and the jittered sequence, 30 s at most, now played by the plugin's own loops. Frame order as
`Plugin.OnFrameworkUpdate`: `CombatCompanionManager.Tick`, telegraphs land, the crowd steps, the engine's clock,
the war (seizers), `NpcAiController.Tick`. Seeds: `NpcAiController.RandomSource` Random(s),
`CombatCompanionManager.RandomSource` Random(s+1), `AggroSeedSource` Random(s+2), the `DamageCalculator` Random(s+3).
Allies join through the real `CombatCompanionManager.AdoptNpc` (HP = the player's 60000 × level factor 0.5).
Since stage B4b both loops attack through the shared step `Combat/CombatStep.cs` (each side's `ActPolicy` and its
own sink); the loop golden files were recorded by B4a from the old loops and were not re-recorded.

Lines: `C` a fighter's target, AI state, in-reach or life changed (with the distance to its target); `A` an attack
committed (enemy: auto or skill and target, MP before; ally: damage, HP left, MP before); `H` an enemy telegraph
landing (damage, HP left) or lapsing; `L` a combat-log entry or an engine call (victory check, captive, revive);
`V` a death animation or an emote; `S` every living fighter every 2 s (AI state, HP, MP, position, facing, in reach,
target, auto timer, lock, cooldowns, look); `F` one line a frame: a hash of every fighter's position, facing, target
id, HP, MP, lock, cast, auto timer, engage delay, in reach, look, cooldowns, AI state, targets, crowd agent (position,
velocity, speed, radius, goal) and of how many effectful seam calls of each kind (visual states, approach writes,
facing, target id, emotes, animations, crowd) the frame made; `end`.

What is faked (everything else is the production code):

- The game objects: `FlatWorld` holds a body per address (position, facing, hitbox radius, entity id, target id). The
  combatants' `BattleChara` pointers are distinct dummy values that are never dereferenced (every read and write goes
  through `ICombatWorld`). The player stands still, attacks nobody and has nothing selected.
- The crowd (`ICrowdAgents`): every agent is always on the mesh and walks straight at its goal at its speed (step
  ≤ 0.1 s), no avoidance, no floor; position and velocity are read the frame after the goal is set, as with the real
  crowd. No corpses to stand on (foot solver: 0).
- Visual states, battle stance, animations, emotes: counted; the visual state keeps only its `Kind`; an emote always
  plays, its loop is 1000 + the emote id.
- The engine (`ICombatLoopEngine`, `FakeEngine`): the real `SimulationState` and `BattleRules`; the enemies' target is
  `CombatCompanionManager.SelectEnemyTarget` (as `Plugin.cs` wires `ResolveNpcTarget`); positions as
  `CombatEngine.GetEntityPosition` reads them; action data only for the kits' MP prices; the clock advances by dt.
  No death queue, no `EngageIdleTarget` when an ally hits, no victory or executions. Taking and releasing a captive
  and reviving the player change the state as `CombatEngine` does (HP, captive flag; no emotes or movement block);
  every such call, and the raise's effect, is logged.
- Attacks (`IAttackSink`, `FakeAttacks`): an enemy's attack is a telegraph that lands 0.8 s (+ its cast time) later,
  single target, `DamageCalculator.CalculateNpcAutoAttack` with its potency, threat through the real
  `RegisterHit`; the enemy counts as busy (`isTelegraphBusy`) until then; it lapses if either side died. An ally's
  action lands at once (`CalculateNpcAutoAttack`, no splash, no arrow delay). No snapshotted cast areas (only the
  Fighting Mode cast path asks for one, and these runs are in action combat).
- The war: while the lord's body is guarded, the scripted seizers go for it, held while a retainer is within 3.5 m;
  no seizure progress, capture or result banner (`Battle/CorpseSeizure` is not run).
- `IPluginLog` drops everything (the loops' summary lines are timed by the wall clock and decide nothing), except, in
  the scenarios added at stage B5, the battle rules' `Rescue:` line (timed by the simulation clock), recorded as
  `G <frame> <text>`.
- Compile only, never run: the managers' spawning and appearance code, and with it `Core/Services.cs`,
  `Core/ClientActorSlotAllocator.cs`, `Npcs/NpcWeaponClassifier.cs`, `Resources/Sheets/HairMakeTypeSheet.cs` and the
  game's assemblies (Dalamud, FFXIVClientStructs, Lumina, from the dev hooks folder). `LoopStubs.cs` only declares two
  namespaces the controllers import; the plain types they use from the engine (`SimulatedActionResult`,
  `CombatLogType`) are linked from their own files in `Simulation/`.

Added at stage B5 (rule parts, `CombatSimulator/Battle/Rules`), recorded with `--write-missing` from the code before
the rules became parts:

- `loop-rescue` — Spring and Autumn, the player fallen: triage, raising the lord and allies, rescuers falling, the
  `Rescue:` summary.
- `loop-lord-captured` — Warring States without retreat, the lord fallen: every retainer taken captive at once (the
  captive's held emote), the enemies on the captives.
- `loop-rule-switch` — the rule switched between frames, as the window does (`ScheduledRules`, an `L ... rules` line
  at each change): Spring and Autumn, at 6 s Warring States with retreat, at 13 s retreat off, at 17 s Spring and
  Autumn, at 23 s Warring States with retreat; the parts leaving and joining the composition mid-battle.

The loop files record decisions (targets, states, attacks, a hash of every fighter's state), not the target weights:
a weight changed by a small factor may not change any draw in these runs.

Not reached by these scenarios: the engine cast and snapshotted-area path (Fighting Mode), sentries and patrols,
the charge, freeing a captive (Spring and Autumn with hostages), executions, the corpse seizure's progress and capture
(Tests/CorpseSeizure).
