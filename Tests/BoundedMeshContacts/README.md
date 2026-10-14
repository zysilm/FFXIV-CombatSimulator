# Bounded mesh contact regressions

Run `dotnet run --project Tests/BoundedMeshContacts/BoundedMeshContacts.csproj -c Release`.

The executable links the production collision task and geometry helpers. It exercises meshes above the small-pair exact-dispatch threshold: 4,608 double-sided concave body faces against a 512-face terrain mesh, with a separate raised mesh inside the waist recess. Additional cases rotate the actor, deform its feet during simulation, and use a 3 cm wide supporting foot.

These support-height fixtures fix angular inertia while leaving translation and gravity active. This isolates false support and terrain penetration from an unconstrained arch tipping onto a different real surface. Both the stock task and bounded task can tip a freely falling disconnected arch; this executable does not certify full unconstrained ragdoll stability.

The solver cases use ordinary body/terrain dispatch. Both explicit pair orders are additionally checked through the production candidate collector; they are not two separately ordered solver runs. Candidate indices must refer to original mesh faces, and a large velocity/angular expansion must retain the hard storage/work bound.

`--original` runs the solver scenarios with the stock BEPU mesh task as a control. It does not bypass the bounded collector assertions.

`--solid-terrain --deform-down` uses the production closed ground beneath the original sampled top. After settling it pushes the skinned feet down by 8 cm and requires the body to recover from y=0.2 to y=0.28. The prior open surface path settled at approximately 0.206 m and failed; solid ground recovers to 0.27993 m. These flags run in CI as an additional regression, rather than replacing the original concavity checks.

Every invocation also ray-tests flat, sloped and nonplanar ground against its source triangle heights. A missing-triangle case verifies the terrain builder does not merge an incomplete footprint into a filled rectangle. Planar rectangular patches merge into one slab; nonplanar patches use downward triangular prisms with their original top faces.
