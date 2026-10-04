# Offline production fluid parity

This executable links the real source files from the selected checkout: `SalivaRuntime*.cs`, `SurfaceFilmRuntime`, `GroundFilmRuntime`, `CurvedCapGeometryCache`, all three simulation models, `FluidGeometryBuilder`, the vertex/material types, and the real fluid configuration. It does not copy or replace the liquid equations. It runs without Dalamud, the client, render hooks or game commands.

Only native input boundaries are replaced: `CharacterFluidSurface` is a deterministic indexed triangle grid with posed vertices, adjacency, material coordinates, walks and contact sweeps; `BGCollisionModule` provides a static ground plane. The fixture contains one connected grid or six disconnected grids to exercise receiving-patch exhaustion. This adapter is deliberately simpler than the game's mesh walker/CCD and has configurable unavailable-query frames. It does not prove the accuracy of native queries.

## Run

.NET 10 SDK is required. Supply a separate checkout of accepted commit `7c16c34`:

```powershell
git worktree add --detach C:/PROJECT/CombatSimulator-fluid-baseline 7c16c34
./Tests/FluidParity/run.ps1 -BaselineRoot C:/PROJECT/CombatSimulator-fluid-baseline
```

The script builds only the standalone test project into its ignored `artifacts` directory. It never builds or reloads the live plugin. Baseline and current use identical input fixtures, frame time and matching legacy globals / independent Mouth settings. A nonzero exit means differing rows, mass conservation failure or source isolation failure. Optional `-CheckpointRoot` can point to an archived faulty multi-outlet revision; it must show a difference and is not required for ordinary regression runs. No stash reference is required to rerun the permanent test.

## Numeric checks

Ten scenarios run 600 frames each: upright runoff, downward pendant/ground contact, rolling pose, mouth endpoint changes, unavailable query frames, skin generation replacement, stop supplying while existing liquid continues, full cap pool/recycling, six disconnected wet-patch exhaustion, and nondefault material/transport parameters with a mid-run viscosity change.

Each frame records source supply, retirement, total inventory, conservation error, deferred time, independent film/cap/thread/drop/ground inventories, reservoir, live counts and geometry count/sums. State hashes include wet film cell volumes/thickness, cap material coordinates/runoff/blocking, filament nodes/velocities/stress/broken inventory, pending contact flags/time debt and ground cell inventories/geometry. Geometry hashes include every byte of every submitted production vertex: position, normal, UV, thickness, coverage and ellipsoid metadata. Comparison is exact, not merely within an aggregate tolerance. Each runtime also independently rejects nonfinite inventory or conservation error greater than `1e-13 m^3`.

The current edition additionally exercises nine sources, removal of one source, changed settings on one remaining source, cumulative admitted supply, mass conservation, render grouping and distinct material absorption colors. Pools and native query budgets remain shared and bounded; this is not proof that nine sources under pressure equal nine unlimited independent runtimes.

## Results for this repair

Accepted `7c16c34` versus repaired current: **6,000 / 6,000 complete CSV rows identical**, including geometry/state hashes. The faulty pre-repair checkpoint also matched the original eight basic scenarios. The directed wet-patch case is necessary: **300 differing frames**, starting at frame 300 when the fifth disconnected receiving patch is requested. Baseline/repaired stop admitting new supply when their four wet patches are occupied; the faulty version keeps admitting supply into additional patches. Thus the tests demonstrably catch the allocation-policy regression rather than merely passing both implementations.

The downward case exercised a live filament, free droplets and 24 ground cells; the capacity case filled all 32 cap slots and retired old inventory. First-frame and changing nondefault viscosity, stringiness, surface speed and thickness are covered.

Limitations: these fixtures cannot validate actual posed bone outlet selection, Penumbra/Glamourer mesh retrieval, shader refraction, depth occlusion, transparency ordering between render layers, live performance or the visual appearance of fluid. Exact equality applies to the scenarios and deterministic input boundaries above; it is not a claim that all possible gameplay inputs are mathematically proven equivalent.
