# Shared combat math: identity with the code it replaced

Run `dotnet run -c Release` from this folder (optional first argument: random cases per function, default 200 000).
Exit code 0 when every function matches.

Refactor stage B1 moved the pure arithmetic of `NpcAiController` and `CombatCompanionManager` into
`CombatSimulator/Combat/CombatantMath.cs` and `AggroModel.cs` (linked here). `Original.cs` holds verbatim copies of
the replaced expressions from commit `f3568b9` (`git show f3568b9:<path>`, line numbers in the comments); only the
surroundings are adapted (a game-pointer read becomes a parameter, `config` a plain object with the same property
names, the action-data price lookup a delegate).

Each moved function is evaluated against its copy over a table of edge values (zeros of both signs, the thresholds
3, 3.5, 5, the clamps 0.01 / 0.1 / 3 / 100, infinities, NaN) and a seeded random sweep (including raw bit
patterns), and must give the same bits. Also checked: the MP-price lookups in `ReachNow` happen in the same number
and order; `Draw` and `Hold` leave the fighter's generator in the same state; `NoteHit` and the due checks agree
over random hit sequences; turning throws in the same cases.

A NaN result is NaN in both versions, but which NaN (payload, quiet bit) is not fixed when two different NaNs meet
or a signalling NaN passes through `MathF.Max` (IEEE 754 leaves it open, the JIT may commute operands, and the
tiered JIT can differ between its tiers). Those cases are counted in their own column and are not differences:
nothing can compare NaNs apart. Game data and settings do not produce such NaNs; the sweep's raw bit patterns do.
