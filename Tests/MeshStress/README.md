# High-poly native collision stress

Run only through this executable's default supervisor. It starts an isolated child process, enforces a Windows job private-commit cap (default 512 MiB), monitors RSS every 25 ms, and terminates after 30 seconds. Direct `--child` invocation bypasses these protections and is reserved for the supervisor.

The synthetic fixture has exactly 73,475 source faces, 37,283 shared source vertices, 32 named anatomical mesh regions, 59 contact-disabled soft capsule drivers, 90 ball-socket joints, and static triangle terrain. Native body meshes are two-sided (146,950 triangles). Parent influences deform surfaces; source/partition vertices use the real production helpers. Unchanged local surfaces skip native refit at the production `1e-10` squared-distance threshold. Dense pose intentionally overlaps many neighboring regions to reproduce folded, initially intersecting mesh dispatch; it is an adversarial synthetic fixture, not a captured mod or anatomical shape validation.

Defaults reproduce configured 4 solver iterations and 16 substeps. Set `--iterations 8` for the plugin's party-ragdoll minimum. The stage event timers separately report collision and solver time, in addition to live skin/refit time, child-pair counts, native pool high-water, and child RSS. Triangle products are a pre-dispatch workload upper bound, not the actual candidate count.

Safe default guard demonstration (exit 2 means guard caught the workload):

```powershell
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 2 --rss-mib 384
```

Actual baseline under a hard memory/time limit, permitting the dense dispatch:

```powershell
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 2 --iterations 8 --pair-budget 30000000 --frame-budget 20000000000 --seconds 20 --rss-mib 384
```

Production bounded-task comparison, retaining body/body contact:

```powershell
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 2 --iterations 8 --policy bounded --seconds 20 --rss-mib 384
```

`--policy ground-only` is a diagnostic control removing body/body contact, not a proposed production fix. `--policy bounded-ground-only` combines that control with registered production collision tasks. `--pose spread` separates the initial regions to distinguish activation overlap from collapse.

Earlier Release measurements before the solid-ground change, with the compact fixture and **8 iterations / 16 substeps** (retained as historical comparisons):

| Fixture / task | Measured behavior | Native pool high-water | Peak child RSS | Result |
|---|---|---:|---:|---|
| Dense original, 2 steps | Collision 1476 / 4572 ms; second-step 17,979,136 child tests | 182.4 MiB | 221.2 MiB | Severe dispatch slowdown reproduced |
| Spread original, 120 requested, 384 MiB hard commit cap | First collision 436 ms; allocation fails before step 1 completes | incomplete | 284.0 MiB | Child `Out of memory`, exit -532462766; host protected |
| Dense bounded, 600 steps | Awake mean / median / p95: 45.4 / 43.2 / 61.9 ms; eventually sleeps | 43.3 MiB | 99.5 MiB | Pass, final source floor -1.77 mm |
| Spread bounded, 120 steps | Physics mean / median / p95: 25.9 / 22.7 / 43.1 ms | 40.4 MiB | 96.5 MiB | Pass, final source floor -4.55 mm |
| Spread bounded, 600 steps | Awake mean / median / p95: 24.1 / 23.6 / 31.2 ms; eventually sleeps | 41.1 MiB | 99.1 MiB | Pass, final source floor -1.22 mm |
| Spread ground-only control, 120 steps | Exact original ground path, no body self contacts | 49.8 MiB | 108.9 MiB | Pass, source floor -2.73 mm |

Times above include skin evaluation, conditional native refit, collision, and solver when labeled “physics.” Cold first-use JIT adds approximately 170–300 ms on this machine; fixture capture/build adds approximately 640 ms, separately reported. These are activation costs, not steady-state frame costs. The final 600-step dense run has 177 measured awake steps after the first 10 warmup steps; its overall median includes sleeping and must not be quoted as awake performance. Once asleep, complete timestep plus refit is approximately 0.005–0.015 ms. The 120-step rows are intermediate captures; the 600-step rows use the final task, including velocity/angular expansion and regional probes.

The final task limits dense pair work to 64 selected original face pairs. BVH root bounds avoid hidden full-face bounds scans; exact small-mesh dispatch is retained when the smaller mesh has at most 64 faces and the face product is at most 65,536. That exact small path is necessary for reliable terrain support. An earlier version sampled those ground contacts and failed the spread fixture with -155 mm surface penetration; the final exact-small fallback reduces it to millimetres. The small concave waist regression also passes (bone y = 0.199798 m, expected 0.2 m).

Final long stability commands:

```powershell
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 600 --iterations 8 --policy bounded --pose dense --seconds 60 --rss-mib 256
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 600 --iterations 8 --policy bounded --pose spread --seconds 60 --rss-mib 256
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --fixture concave --seconds 10 --rss-mib 128
```

Raw sanitized JSONL is retained in [Results](Results). `bounded-dense-600.jsonl` and `bounded-spread-600.jsonl` are the long runs; `original-dense.jsonl` and `original-spread-384.jsonl` document the native baseline. The latter's `Out of memory` text was emitted on stderr; its raw supervisor exit and bounded peak RSS remain in JSONL.

All final bounded runs check finite body states and reconstructed source geometry above -5 cm terrain penetration. This is a coarse stress failure guard, not a millimetre accuracy target. Final dense/spread surface positions are within 2 mm. The final spread fixture's worst joint error during collapse is 110 mm because its synthetic long connections whip; after settling it sleeps. These are representative count-matched synthetic load tests, **not the user's live mod**, and do not establish game FPS or anatomically correct deformation. Awake dense physics remains tens of milliseconds on this machine; no 60 FPS assertion is made.

An optional `--threads 2` / `--threads 4` experiment uses a synchronous BEPU dispatcher in this executable only. Dense 240-step runs measured awake medians of 32.0 / 31.4 ms respectively, approximately 99 / 106 MiB RSS, with valid final floor support. Raw captures are `experiment-dense-threads2.jsonl` and `experiment-dense-threads4.jsonl`. Dispatch scheduling changes the trajectory and sleep timing; these are additional experiments rather than identical-pose speed ratios. The plugin retains its existing serial stepping and lifetime ownership; this experiment does not install a shared dispatcher into native render hooks or the separate experimental physics paths. Per-stage cumulative task timings sum worker CPU and must not be read as parallel wall time.

## Known failure: starting inside terrain

The game subsequently reported sinking. Its initialization log requested a 66 mm whole-rig lift, but the existing lift option was off. The following additional fixture uses the production terrain layout (12 by 12 one-metre cells, 288 triangles) and initially places the lowest source surface 66 mm below ground:

```powershell
dotnet run -c Release --project Tests/MeshStress/MeshStress.csproj -- --steps 120 --iterations 8 --policy bounded --pose dense --terrain-grid 12 --start-penetration-mm 66 --rss-mib 256 --seconds 40
```

**Expected result for the old open-terrain path: failure**, source minimum approximately -58 mm after 120 steps. A triangle-surface mesh does not guarantee solid-volume depenetration from an invalid initial overlap. Passing the above-ground drop fixtures does not cover this case. The game's reported common lift may include fallback shapes, so mesh-only initial-depth calculation distinguishes which rendered regions actually overlap. This old-path command is retained as a failure control.

## Solid-ground recovery and adjustable contact quality

Production mesh mode now builds solid ground **below the original terrain top**. Rectangular coplanar patches merge into a slab; other patches use triangle prisms. Ground uses native convex/mesh contacts outside the body/body sampler. Adding `--solid-terrain` selects this production helper. `--recover-start` additionally places the initial mesh above ground as production startup now does; the legacy lift checkbox still controls proxy-driven lift.

`--quality 0..100` controls body/body contact search only: 1–4 original A representatives per spatial cell and 8–24 visited leaves per query. The default 50 retains 2 representatives and 16 leaves. It does not reduce body geometry, skip skin refit, change the solver, or reduce ground contact coverage. Lower values can miss body/body detail. The matching GUI slider applies live, defaults to 50, and uses only a hover tooltip for explanations.

Count-matched dense 120-step runs with the same 288-face terrain layout and solid floor measured these warm medians on this machine (trajectories differ after changing contact quality):

| Quality | Collision | Complete physics | Peak RSS | Final source floor |
|---|---:|---:|---:|---:|
| 0 | 22.76 ms | 46.79 ms | 108.0 MiB | -1.20 mm |
| 100 | 48.19 ms | 66.72 ms | 102.6 MiB | -4.18 mm |

These adversarial synthetic results show a useful search-cost tradeoff, not a game FPS guarantee or a guaranteed memory reduction. A separate solid-ground run starting 66 mm underground, **without** startup correction, recovered to -1.36 mm at quality 0; the previous open surface remained around -58 mm. Raw files are `solid-quality0.jsonl`, `solid-quality100.jsonl` and `solid-recovery-quality0.jsonl`.

Final solid-ground long runs also passed under the 256 MiB hard limit: dense with startup recovery and quality 0, 600 steps, -1.04 mm final surface minimum / 109.8 MiB RSS; spread with quality 100, 600 steps, -2.69 mm / 102.3 MiB RSS. See `solid-startup-600-quality0.jsonl` and `solid-spread-600-quality100.jsonl`. These runs check stability and finite states; the crowded low-quality fixture remains awake at the end and is not claimed to have completely settled.

## Shared CPU workers and collision LOD experiment

`--threads 2 --shared-dispatcher` uses the actual production reference-counted dispatcher. `--lod --lod-mm 3` enables the production endpoint collision simplifier, targeting 15% of source faces where the sample target, topology and work limits allow. Override `--lod-percent 35` to reproduce the first experiment. Complete query geometry and source skin are retained, COM/inertia come from the full mesh, and accepted runtime deformation is gathered through exact retained source indices. The fixed-witness error check runs first; if it fails, current BVH nearest-distance checks decide whether to restore the full native collider.

Same-machine 120-step comparisons, 8 iterations / 16 substeps, quality 50, solid 288-face-layout terrain (awake medians):

| Pose / variant | Native triangles | Complete physics | Peak RSS |
|---|---:|---:|---:|
| Dense full serial | 146,950 | 50.71 ms | 99.3 MiB |
| Dense full shared 2 | 146,950 | 40.08 ms | 102.4 MiB |
| Spread full serial | 146,950 | 21.82 ms | 95.9 MiB |
| Spread full shared 2 | 146,950 | 22.28 ms | 97.6 MiB |
| Spread LOD 35% serial | 51,450 | 17.01 ms | 88.9 MiB |
| Spread LOD 35% shared 2 | 51,450 | 18.79 ms | 89.2 MiB |
| Dense LOD 15% shared 2 | 23,330 | 41.56 ms | 87.8 MiB |
| Spread LOD 15% shared 2 | 23,314 | 19.47 ms | 86.1 MiB |

Changing contacts changes trajectories, contact counts and sleep time, so these are scene comparisons rather than identical-state speed ratios. The early dense LOD35% run was **slower** than full geometry. Smaller triangle counts do not guarantee lower contact-search cost: larger faces can increase overlap/accepted contacts, and live error monitoring has a cost. The production LOD checkbox therefore defaults **off**, while parallel stepping defaults on for mesh-mode rigs and has a live comparison toggle. The entire mesh feature remains off by default.

Final LOD15% + shared2 runs each completed600steps under the 256MiB limit: dense peak88.0MiB RSS, final minimum-1.93mm; spread89.2MiB/-1.12mm; no LOD fallback in these affine-skin fixtures. Dense awake median39.05ms; spread12.17ms. Sleep is excluded from the dense awake metric. A separate helper fixture deliberately warps a surface50mm and requires the live validation to reject it; affine pressure fixtures alone do not exercise fallback.

The 3mm setting is an initial-pose finite-sample approximation target, not a global Hausdorff guarantee and not a promise that every pose or ground contact is within3mm. Source-vertex/centroid samples are monitored after skin refit; detected excess restores that region's full mesh. Detailed curved/concave/crease checks and their measured errors are in `Tests/MeshLod/README.md`; dispatcher lifecycle/reentry tests are in `Tests/MeshDispatcher/README.md`.

## Background activation regression

**Current production uses frozen session geometry.** Add `--frozen-mesh` to skip live skin/refit
and measure the final full cached surface in each body's rigid frame. The original live path
remains available for failure controls. `frozen-background3-dense-600.jsonl` exercises late
background LOD installation; all 32 regions installed, no native refits/fallbacks, final cached
surface minimum -2.78 mm and peak RSS 106.1 MiB. `frozen-prepared3-spread-600.jsonl` starts with
prepared LOD, no native refits/fallbacks, final minimum -4.18 mm and peak RSS 89.4 MiB.
Both completed 600 steps under 256 MiB. Dense awake median remained 43.07 ms; spread was 10.07 ms.
This shows that eliminating refits does not remove the cost of mesh collision detection.
The spread fixture retains moving long synthetic joints; finite/floor checks do not certify
joint precision or complete settling. Counts match the game, but geometry is synthetic.

Add `--background-lod --lod-mm 20` to build serial managed snapshots in the production background queue, starting with full native colliders and applying at most one completion per 16.7 ms after validating the current pose. This exercises actual native replacement during simulation; a run fails if no result was installed. `--lod` without this flag retains the synchronous path for historical comparisons.

Two 600-step runs with 73,475 source faces, 32 mesh/59 fallback bodies, solid 288-face-layout terrain and shared 2 workers passed under a 256 MiB hard cap. Dense at 20 mm installed all 32 results, ending at 23,330 native triangles, source minimum -4.99 mm and peak RSS 103.5 MiB. Spread at 3 mm installed all 32, ending at 23,314 triangles, minimum -1.79 mm and peak RSS 104.9 MiB. Neither affine-skin fixture triggered fallback; the nonaffine helper test covers invalidation. See `lod-background20-dense-600.jsonl` and `lod-background3-spread-600.jsonl`. Full initial capture/native construction still took about 0.76 seconds in these fixtures; warmup also remains. These are lifecycle/stability results, not a claim that background generation removes all activation or steady-state costs.
