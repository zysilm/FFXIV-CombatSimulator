# Fluid goal evidence audit — 2026-10-07

The previous turn made progress: `386248d` was committed and pushed to `experiment/body-fluid-rendering-prototype`. The current working tree is the authority; this audit does not treat early prototype reports as current acceptance.

| Requirement | Current evidence | Remaining verification |
| --- | --- | --- |
| Actual lip-edge binding | Production lip/outlet selectors; 123 directed assertions; earlier user confirmed gravity-selected outlet | Updated reference-edge placement on actual Rue poses after latest repair |
| Transparent refractive material | Earlier user accepted material visibility/refraction; latest persistence/contact changes do not edit shaders | Latest multi-layer appearance across camera and lighting conditions |
| Viscous skin flow and retained rivulets | Production contact tests, curved material samples, eight retained-owner tests, user accepted earlier effect | Latest persistent traces on native deformed skin, including budget-heavy poses |
| Filament extension and breakage | Production viscoelastic model, numerical recorder, retained cap-to-thread transition test | Current live breakage and junction appearance; numerical stability alone does not establish realism |
| Ground deposition and spreading | Production contact transfer tests; new flat expansion, downhill slope transport and unavailable-support checks | Uneven real terrain and visible spreading rate/material quality |
| Reset and cleanup | Configuration reset methods, controller disable/clear route, production retained and ground Clear assertions | End-to-end GUI reset, reload and map-change assessment |
| Performance isolation | Fixed particle/query/vertex pools; disabled controller pose gate; bounded ground insertion checks | Current CPU/GPU timings under indefinite multi-source supply; fixed limits alone do not prove acceptable frame time |
| Staged game verification | Earlier user reports cover projection, occlusion, material, source selection and overall appearance | Latest repair batch remains visually unverified; no automated game commands are authorized |

The new `--ground` tests pass: 600 frames double wet area from 54 to 108 mm² without additional input; 120 frames of unavailable frontier support preserve liquid and forbid fabricated support. This turn changes only tests and documentation, preserving the deployed DLL's simulation and material behavior.

Further evidence: an inclined supporting plane advances for 600 frames with world gravity; its liquid center moves downhill from X=2.589 to 0.279 mm, without losing mass or rendering below support. The live Dalamud log at 2026-10-07 22:39 confirms ongoing multi-site supply and Rue custom-driver binding for both Part 1 sites. No current full runtime trace or CPU/GPU timing sample was present in the inspected log. A user-run trace request is pending; it does not authorize automated game commands and does not establish visual acceptance.

The goal remains active. Full completion is unproven until the remaining native/visual/performance checks have direct evidence. Offline triangles and bounded pools must not be presented as proof of live Rue accuracy or acceptable rendering performance.
