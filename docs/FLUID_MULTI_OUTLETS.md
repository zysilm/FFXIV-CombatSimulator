# Multiple fluid outlets

The accepted single-site version is committed as `7c16c34`. The default remains one Mouth site and the existing clear material. This extension adds independently enabled sites, removal, local calibration offsets, and independent per-site flow, physics and appearance settings. All public and internal Part labels use neutral names.

## Ownership and isolation

One runtime shares skin queries, terrain queries, geometry, and fixed cap/thread/drop pools. Each of nine site kinds has its own supply reservoir, material anchor, source cap, and accepted-source time. Removing or disabling a site retires its remaining unrendered reservoir; existing skin caps, threads, free drops, and ground liquid continue normally. Changing a selected endpoint stops using the old cap as a source without teleporting it or clearing other liquid.

Each source retains the accepted runtime's four receiving patches, with the same first-empty allocation order for source and downstream contact. A full wet bank returns Pending; source switching never clears existing wet patches to force a new binding. Mouth alone retains the original four-patch working set. Additional banks are allocated only when another source binds. No new total-volume or emission-duration limits were introduced.

## Surface API and rendering

`CharacterFluidSurface.TryGetOutletAnchor(settings, out anchor)` returns posed material anchors from the currently loaded supported model, including the existing deformation path. A zero-offset Mouth delegates to the accepted mouth selector. Other sites cache candidates per topology generation and local offset. Candidate scans share 4096 cheap checks per captured pose; binding shares 2048 posed triangle checks, alongside the existing skin-vertex budget. Runtime rotation prevents a site monopolizing first access.

Eye endpoints come from the weighted lower-lid mesh extent and select center or a side using current world vertical and hysteresis. Part locations use bone-assisted approximate profiles, with local offsets for model calibration. Missing drivers or unsupported visible mesh patches remain Pending and do not emit from guessed world coordinates. Exact landmark placement for every body mod is not established by this implementation.

Each site has independent flow, viscosity, stringiness, relaxation, surface speed, thickness, clear/Blood red appearance, reflection, roughness, cloudiness, and foam. Beads, threads, detached drops, local coats and ground pools retain their origin site. Different sites do not merge into one cap or share a material-owned skin patch. Removing a site preserves the last material parameters of its existing liquid.

The editor follows the Victory Sequence selectable-list and selected-item pattern, with Flow, Appearance and Position tabs and a per-site Reset. Existing global settings are copied once during migration; new sites receive their own defaults. Legacy global fields remain only for old config and diagnostic compatibility.

Blood red changes the existing material absorption and reduces white cloud/foam influence, without modifying the refraction shader. Geometry is tagged by site and partitioned into material batches in linear passes without per-frame partition allocations. Total fluid geometry remains at the existing shared vertex limit. Extra site layers are created lazily, and the renderer layer ceiling is extended from 8 to 16 to allow nine sites alongside existing previews. The shared scene snapshot remains owned by the existing renderer. Ground storage is created lazily per site; terrain queries retain the shared runtime budget.

## Validation

Offline numerical parity tests now link the actual production runtime, film and filament solvers, cap reconstruction, ground film and geometry builder, against deterministic pose/topology/contact inputs. They compare the accepted commit against this implementation using identical settings. See Tests/FluidParity for scenarios, commands and limits. No autonomous game commands are used. Actual placement, eye selection, red appearance, simultaneous-site performance, shader/refraction and model-specific calibration remain in-game acceptance items. Shared cap/thread/drop capacity and native query/geometry budgets are finite: multiple active sources can compete for these resources; numerical parity with the single-source version is established only for matching single-source inputs.
