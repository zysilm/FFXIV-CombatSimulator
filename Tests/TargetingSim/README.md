# Mass-battle target selection A/B

Run `dotnet run -c Release` from this folder (optional first argument: seed count, default 40; 100 seeds take ~10 s).
Tuning hooks for the probabilistic models: `TS_STICKY`, `TS_THREAT`, `TS_CROWDK`, `TS_TASK`, `TS_GUARD`,
`TS_DWELLMIN`, `TS_DWELLMAX`, `TS_HITHOLD` environment variables.

20 allies + player vs 20 enemies on a 2D plane at 30 fps: walk speed, melee/ranged reach, 6 melee slots per
target, 2.5 s auto-attacks, production threat math (`EnemyMovementMath`, linked). Models:

- `Current` — port of `CombatCompanionManager` target scoring (enemy argmax every frame, switch margin 60;
  companions every 0.25 s, margin 20; pressure caps, target graph, `LordGuardScore`, `LordSeizerTarget` override).
- `CurrentNoOverride` — same, seizers get +400 on the holder instead of the hard override (the in-game bug).
- `Softmax` — the production scores drawn by softmax (T = 50), redrawn only on events.
- `Weighted` / `WeightedCalm` — one multiplicative weight for both sides (threat x crowding x distance x finish x
  mutual x sticky x task), redrawn on events; Calm is the tuned one.

Not covered: navmesh/DetourCrowd paths, body blocking, skills/AoE, healing, reachability probes, command-range
oscillation (allies guarding the lord simply ignore enemies more than 10 y + reach from the body).
