# Rue outlet and permeable liquid corrections

These changes follow checkpoint `69b3a67`. Unlike the earlier parameter-routing repair, this request deliberately changes faulty contact/render behavior.

## Evidence and scope

The current logs show that the collection-resolved Rue body model is already loaded (slot 1 has 24,895 faces / 33,391 supported vertices). The model uses custom `iv_c_mune_l/r` weighted drivers rather than the stock-only names previously required by Part 1. The missing emitter therefore was a landmark binding problem, not an absent mod read.

Part 1 now resolves the actual loaded custom driver, with stock compatibility, and projects onto its outward front surface. Candidate normal and driver-relative direction tests exclude internal/back faces and the opposite side. Targets remain material anchors on the deformed model, not arbitrary world emission points. Bone-assisted landmarks are still approximations; actual semantic placement on every mod needs visual confirmation.

Mouth previously projected the lower-lip bone center onto whichever posed mesh was captured first. It now uses the loaded deformed reference geometry and the midpoint of the upper/lower landmarks, projected onto supported lower-lip triangles. This selects the supporting edge near the opening and removes first-expression dependence. The existing gravity/corner selector remains. This is not a universal semantic mouth-seam detector.

A rivulet's lateral world-space interpolation formed a chord between two skin points. On convex skin it could lie inside the model despite an 80 micrometer lift. Each of the seven columns now stores its own material anchor, evaluated against the current posed skin. Mapping is cached when a row is recorded; it is not resampled by full-mesh searches each render frame. Four rows per simulation step remain the mapping limit, with 24 bounded lateral samples; existing native skin/query budgets still apply.

Free drops used to skip integration when terrain/skin queries were exhausted. Skin queries are now optional accepted-inventory wetting transfers: failed/pending/full receivers do not reflect, stop or freeze the unaccepted drop. Gravity always advances it. Delayed terrain queries retain the historical sweep; terrain itself still receives fluid. Query batches resume after the last checked drop to prevent starvation. Partial skin acceptance no longer discards a ground hit from the same trajectory: the remaining inventory can deposit on ground in that same sweep.

## Verification

- `Tests/FluidOutletBinding` links the production outlet and lip selectors: 123 assertions cover custom/stock drivers, both sides, external/internal/back geometry, depth beyond the old cutoff, posed/nonuniform transforms, reference lip-edge selection and first-expression independence.
- `Tests/FluidParity/run-contacts.ps1` links the production transport/rivulet code. Directed tests cover exhausted budgets, unavailable native skin, delayed ground crossing, saturated-body permeability, partial skin acceptance followed by ground, fair terrain query batches and a convex triangulated substrate.
- The original numerical parity comparison remains exact and intentionally reports changed free-drop/geometry behavior. It is not weakened to claim an equivalent refactor. See the test README for per-scenario results.

No shader/material changes or user configuration writes are made. Test fixtures do not prove native deformation-chain accuracy, actual mod landmarks, final occlusion/refraction or live performance. No game commands are issued during this work.