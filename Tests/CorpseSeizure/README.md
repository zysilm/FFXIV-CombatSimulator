# Corpse seizure scenarios

Run `dotnet run -c Release` from this folder. Exit code 0 when every check passes.

Refactor stage B3 moved the Warring States corpse seizure out of `CombatSimulator/Plugin.War.cs` into
`CombatSimulator/Battle/CorpseSeizure.cs`, which reads the game only through `ICorpseSeizureWorld` and
`ICorpseGrab`, so it runs here against a fake world (linked with `BattleRules.cs`, `IBattleRuleSettings.cs`,
`WarFallenRule.cs`). The expectations were worked out from the code as it stood in `Plugin.War.cs` at `e93e51e`,
not from the moved code. Steps are 1/64 s or 1/4 s so float sums are exact and each expectation names a frame.

Scenarios: stalemate (guards >= seizers within 3.5 m: no progress, seizers held; losing the hold logs
"Nobody stands over the lord's body!" once), outnumbered (seized on frame 128 = 2 s; retainers surrendered, enemy
victory, grab on the closest), pushed out (drains at half rate, not reset), 95% cap while the ragdoll is not up then
completion 7 frames after it is, grab retry (every 0.4 s for 8 s; the warning only when the last retry falls on the
frame the 8 s run out), seizer review every 3 s (dropped beyond max(10 m, 2x the nearest non-seizer)), idle sentries
beyond 40 m not woken, seizer share ceil(living / 2) with at least 1, the rule switches read live, reset, and the
`War:` log line word for word.

2026-11-15 (a gameplay change, not a refactor): the seizing fills by (seizers − retainers within 3.5 m) / 4 s of the
whole per second, drains by a quarter per second, stops at 95 % until the ragdoll is up, and every hit on a seizer
within 3.5 m knocks it back by 10 % (`CorpseSeizure.NoteHit`). `Progress` is now 0..1 (it was seconds, full at 2).
Changed expectations, each for that reason alone: the frames to a seizure at margin 1 (128 → 256; 1/4 s steps 8 → 16),
the half-way and drain values in "pushed out" (1 s → 0.5, drained 0.25), the 95 % cap (1.9 → 0.95; 7 → 13 frames
after the ragdoll), the progress after 1 s in "rules" (1 → 0.5 after 2 s), the reset scenario's run (200 → 300
frames), and the first `War:` line ("seized 1 %" → "0 %"). New: margin 2 is twice the rate; hits at the body knock it
back, hits elsewhere do not, never below 0, the `War:` line counts them ("knocked back by N hits").
