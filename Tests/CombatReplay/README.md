# Combat decision replay (golden files)

Run `dotnet run -c Release` from this folder: compares the output with the golden files in `golden/` and exits 1
at the first different line. `dotnet run -c Release -- --write` rewrites them (only on purpose, after a behaviour
change the user approved). "Identical" means byte for byte on the same machine and runtime
(docs/PLUGIN_ARCHITECTURE.md §8.1); `golden/` is marked `-text` so git does not change its line endings.

Built at refactor stage B1, so it covers what the extracted pure code (`CombatSimulator/Combat/CombatantMath.cs`,
`AggroModel.cs`, linked) decides. The two controllers (`NpcAiController`, `CombatCompanionManager`) still read and
write game objects and cannot run offline before stage B4a; the round-level golden of §8.1 (the old loops in a fake
world) comes then.

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
