# Fluid behavior parity repair (checkpoint 69b3a67)

Baseline: `7c16c34`, checked out separately at `C:/PROJECT/CombatSimulator-fluid-baseline`. Current UI/refactor was preserved in a stash checkpoint before repair; the feature branch was not reset or switched.

## Confirmed regression

The initial multi-outlet refactor changed the single-source receiving-film pool from four patches to a larger shared pool and added clearing/rebinding of a wet source patch when no receiver was available. This was a behavior change, not parameter routing. In the deterministic six-disconnected-patch case, the saved refactor diverges at frame 300: it supplies 0.06 ml over the run, compared with the accepted version's 0.03 ml. The accepted version waits when all four wet patches are occupied.

## Repair

- Restore four receiving patches per source and the original first-matching/first-empty selection for both source and downstream contact.
- Remove forced wet-film retirement/rebinding. Lack of a receiver defers supply/transfer using the original rules.
- Update endpoint/source-role transitions only after a valid receiving patch exists, matching the original ordering.
- Restore source-cap allocation after emission stops and the original timing of cached world-sample updates.
- Route source parameters through the existing numerical algorithms. Refresh film material after resolving current site settings so custom parameters are available in the same step.
- Keep the selectable-site UI, independent flow/physics/appearance controls, owner-tagged material batches and neutral Part names.

The default Mouth case allocates only the original four film patches. Additional source banks allocate on first successful surface binding. Original shared cap/thread/drop capacities, native query budgets and total geometry budget remain bounded. Multiple sources can compete for those shared resources; this work does not claim that nine sources behave like nine unlimited isolated simulations.

## Numerical evidence

The offline harness compiles the actual production runtime, film and filament solvers, ground film, cap reconstruction and geometry builder from either source tree. Only external surface/pose/topology/contact inputs, native terrain raycasts and logging are replaced with deterministic fixtures. It is not a separately reimplemented liquid solver.

Ten scenarios, 600 frames each: upright skin flow, downward pendant/ground impact, rolling pose, source endpoint changes, exhausted surface budget, skin-generation replacement, stopped emission, cap capacity/recycling, exhausted wet receiving patches, and custom/dynamically changed viscosity/stringiness/surface speed/thickness.

The repaired implementation matches the baseline CSV row-for-row across all 6,000 frames. Compared values include inventories, emitted/retired mass, conservation/deferred time, cap positions and material coordinates, filament positions/velocities/stresses/break inventories/contact flags/time debt, ground cell inventory/geometry, and the complete generated vertex-byte hash (positions, normals, thickness and coverage included). The saved pre-repair implementation differs on 300 wet-patch frames. These results establish parity for these fixed inputs; they do not establish universal equivalence.

See [the reproducible harness](../Tests/FluidParity/README.md). Shader execution, transparency sorting, actual deformed/modded actor surfaces, landmark selection and game frame timing are outside this offline test. The game was not driven by test commands during this repair. User settings were not overwritten.

Later corrections intentionally change faulty free-drop contact and curved-rivulet geometry; see [Rue/contact corrections](FLUID_RUE_CONTACT_FIX.md). The equality results above describe checkpoint 69b3a67, not all later changes.
