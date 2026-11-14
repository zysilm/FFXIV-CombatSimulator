# Mass-battle target selection A/B

Run `dotnet run -c Release` from this folder (optional first argument: seed count, default 40; 100 seeds take ~15 s).
Tuning hooks for the exploratory probabilistic models (Softmax, Weighted; not Production): `TS_STICKY`, `TS_THREAT`,
`TS_CROWDK`, `TS_TASK`, `TS_GUARD`, `TS_DWELLMIN`, `TS_DWELLMAX`, `TS_HITHOLD` environment variables.

20 allies + player vs 20 enemies on a 2D plane at 30 fps: walk speed, melee/ranged reach, 6 melee slots per
target, 2.5 s auto-attacks, production threat math (`EnemyMovementMath`, linked). Models:

- `Current` — port of the old fixed `CombatCompanionManager` target scoring (enemy argmax every frame, switch margin 60;
  companions every 0.25 s, margin 20; pressure caps, target graph, `LordGuardScore`, `LordSeizerTarget` override).
- `CurrentNoOverride` — same, seizers get +400 on the holder instead of the hard override (the in-game bug).
- `Softmax` — the old scores drawn by softmax (T = 50), redrawn only on events.
- `Weighted` — the first, untuned multiplicative weight (sticky x2, crowd K 3, task x12, guards x3, hold 3–6 s).
- `Production` — the plugin's own choice: `AggroModel` (in `CombatSimulator/Combat/`, linked: weights, draw, hold,
  due check, "hit by someone new"), applied as `CombatCompanionManager.Aggro.cs` does: allies look again only with no
  live target or every 0.25 s (+ id stagger), enemies every frame; crowding counts living enemies and companions with a
  target, not the player; the situation multipliers are the production ones (focus x2, attacking the living player
  x1.5, the holder x100 for seizers, the seizers x30 and the near-body bonus for guards, someone kneeling x3).
  This replaced `WeightedCalm`, the tuned copy of the weights the plugin was built from (refactor stage B1).

Production vs. the numbers the plugin was tuned on (`WeightedCalm`, docs/PROJECT_LOG.md §2), same machine:

| | field win | field time | field sw/min | guarding win | guarding sw/min | seizers on the prayer |
| --- | --- | --- | --- | --- | --- | --- |
| WeightedCalm, 100 seeds (the log) | 98% | 84 s | 5.6 | 45% | 3.7 | 0.72 |
| Production, 100 seeds | 99% | 84 s | 5.2 | 56% | 3.5 | 0.88 |
| WeightedCalm, 400 seeds | 98% | 84 s | 5.6 | 42% | 3.6 | 0.72 |
| Production, 400 seeds | 98% | 84 s | 5.2 | 47% | 3.4 | 0.88 |

The guarding win rate moves about ±5 points between seed sets of 100 (one standard error); over 400 seeds the gap is
5 points. It comes from four things the copy did not do and the plugin does (each checked by switching it off in a
scratch copy; with all four switched off Production gives 43% / 3.7 sw/min / 0.72 over 400 seeds): someone kneeling
x3 (enemies on the prayer, allies on whoever hits it: seizers on the prayer 0.72 -> 0.88), the hold restarting on
every draw (the copy restarted it only on a change of target), allies looking only every 0.25 s, and the player not
counted in crowding.

Still simplified in Production (not covered by the model): an ally's distance term uses its fixed reach (the plugin
uses `CrowdReachNow`, which grows with ready skills); guards ignore enemies more than 10 y + reach from the body
(the plugin keeps them as candidates at x0.05 and walks the guard back on its leash); the holder is the prayer while it
lives, else the companion nearest the body at any distance (the plugin: within 5.5 m); the player's focus is the
nearest enemy (to the body when guarding); no rescues.

Not covered: navmesh/DetourCrowd paths, body blocking, skills/AoE, healing, reachability probes, command-range
oscillation.
