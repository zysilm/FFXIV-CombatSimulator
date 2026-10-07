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

## Contact and curved-support correction (after 69b3a67)

The later contact correction intentionally changes the frozen-drop and curved-rivulet behavior; it is not an equivalent refactor. Run its directed checks against production code:

```powershell
./Tests/FluidParity/run-contacts.ps1
./Tests/FluidParity/run-contacts.ps1 -HistoricalRoot C:/PROJECT/CombatSimulator-fluid-baseline -HistoricalBaseline
```

The test uses reflection to seed the real drop/cap pools and invoke production `StepDrops`, `RecordRivulet` and `AppendRivulet`. It replaces no liquid equation. It verifies:

- Exact ballistic displacement and gravity velocity with no terrain/skin budget, no skin budget alone, or unavailable skin input. Skin queries are optional transfers, never a reason to freeze free liquid.
- A drop crosses ground while terrain queries are unavailable; after budget returns, its historical ground sweep deposits the original inventory. No volume disappears while crossing.
- A skin hit with all four receiving patches already wet preserves the incoming drop velocity and volume and allows passage. The cooldown assertion proves the test exercised the refused body transfer, rather than missing skin altogether.
- A long sweep first wets a horizontal skin patch and then crosses terrain; any partially accepted skin transfer must still deposit its remainder on known ground in the same sweep.
- Two terrain queries per step fairly visit a six-drop pool in three batches, observed at the actual native raycast input boundary. No late slot waits for the cursor to crawl one slot at a time.
- A 3 mm radius convex triangulated cylinder exercises rivulet width across multiple material faces. Every production `Base` sample must lie on a supporting mesh face (within 0.2 micrometers), and the rendered free surface must remain at least 75 micrometers above support. The ordinary fixture remains the same planar grid when no radius is supplied. This detects the former world-space chord interpolation cutting through convex skin.

Current production: **8 / 8 PASS**. Both accepted `7c16c34` and deployed `69b3a67` production: **7 / 8 FAIL**, with only the already-correct saturated-body permeability case passing. `69b3a67` was extracted with `git archive` into ignored test artifacts and built against the same test fixtures; no live plugin/game test was involved.

The original 6,000 frames were also rerun without relaxing their assertions or altering their planar fixture inputs. Nine of ten scenarios retain identical state hashes and all inventories. In `capacity`, 202 state hashes / 196 inventory rows change: free drops now reach ground instead of remaining frozen when contact budgets are exhausted. At most `4.18654088277087e-8 m^3` transfers from free drops into ground inventory relative to the old run; admitted supply, retirement, skin film, attached caps and filament inventories remain identical. Total inventory differs by at most `4.235165e-22 m^3` from floating-point summation order. All conservation assertions and nine-source isolation checks pass.

Geometry hashes differ in 4,347 frames because each lateral column is now evaluated from its own material anchor, including different floating-point arithmetic even on the planar fixture. `downward` and `parameters` geometry remain identical. The dedicated convex-support assertions establish the intended geometric correction; a bytewise geometry difference alone cannot establish visual accuracy. The original `run.ps1` remains an exact-parity gate and will intentionally report these behavior/geometry differences; use `run-contacts.ps1` for this correction. These offline tests do not verify native mod binding, actual Rue topology, or final rendered appearance.

## Persistent attached traces and indefinite source adaptation

Run the directed production checks with:

```powershell
dotnet build Tests/FluidParity/FluidParity.csproj -p:SourceRoot=C:/PROJECT/FFXIV-CombatSimulator -p:Baseline=false -o Tests/FluidParity/artifacts/retained-current
dotnet Tests/FluidParity/artifacts/retained-current/FluidParity.dll --retained
dotnet Tests/FluidParity/artifacts/retained-current/FluidParity.dll --contacts
```

Eight retained-trace groups pass against the actual production runtime. They prove independent ribbon mass survives cap detachment, pendant conversion, actual source-cap pool reuse and temporarily unavailable posed geometry, repeated preservation does not duplicate it, stop/removal does not erase it, explicit Clear and topology-generation replacement retire it conservatively, 40 insertions recycle a fixed 32-owner pool while all 32 attached traces retain draw priority, and a fifth disconnected source patch does not silently stop supply or clear another source. A draw-only 0.3 mm uniform wet-coat case verifies the actual retained vertices remain outside the actual wet-film free surface, while trace coverage and retained mass remain unchanged. A directed all-four-banks-reserved test keeps the old thread's Attached flag, material Anchor, model volume and node position unchanged while removing only its stale Bead feeding reference. A 2,400-frame continuous-source case cycles six disconnected wet patches and checks admitted supply and conservation every frame. All eight previous contact checks also pass.

Source-bank reuse is intentionally restricted to source binding. Ordinary body receivers retain their optional-transfer behavior. When its four source banks are wet, the source selects its own least-recently-used bank, preferring banks without active cap/thread references. Existing cap trails transfer into independent retained owners, remaining cap inventory becomes gravity drops, and the reused thin film explicitly retires its remaining inventory. If every bank feeds an old thread, that thread retains its own material endpoint and liquid model but no longer borrows from the recycled cap/film bank. This is a deliberate bounded-pool policy to support ongoing supply, not an assertion of exact old behavior parity.

The normal 6,000-frame recorder now includes a separate `retained` inventory column. Its `reservoir` calculation excludes retained ownership, and its state hash includes retained owner volume, source identity and every stored material sample. Geometry priority and inventory transfer deliberately change state and geometry hashes relative to the original accepted baseline. The earlier contact correction results above are historical results before this persistent-owner extension; they must not be read as current equivalence claims.

## Ground spreading and unknown support

Run `dotnet Tests/FluidParity/artifacts/retained-current/FluidParity.dll --ground` after rebuilding the harness. These tests execute the actual `GroundFilmRuntime` and `SurfaceFilm` with supplied collision triangles; they do not exercise native raycasts.

- A flat verified triangle receives liquid once, then advances 600 frames without further supply. Wet area above 2 micrometers grows from 54 to 108 square millimeters, while admitted inventory stays conserved. Each step inserts at most eight cells and uses at most four support probes, the pool remains below 768 cells, rendered geometry is finite and above support, and Clear retires all remaining mass.
- A plane with slope 0.2 receives liquid and advances 600 frames under world gravity. The volume-weighted center moves downhill from X=2.589 to 0.279 millimeters, inventory is conserved, and rendered vertices remain above the supporting inclined plane. This checks gravity transport separately from flat pressure-driven spreading.
- A small verified triangle receives liquid and its frontier probe returns Pending for 120 frames. The test confirms the delegate was exercised, query limits hold, owned liquid does not drain away, and every generated cell remains inside the original verified triangle.

These checks establish expansion and sealed unknown support for these numerical inputs. They do not establish visually convincing puddles, uneven-map support accuracy, production native timings or pixel-level material quality.
