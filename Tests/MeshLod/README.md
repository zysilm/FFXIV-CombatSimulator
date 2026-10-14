# Collision-only mesh LOD

```powershell
dotnet run --project Tests/MeshLod -c Release
```

Production-linked checks cover planar retriangulation with 20 mm edges, smooth curved
surfaces, a concave waist, a thin foot, sharp concave creases, unchanged open-cut boundary
vertices, winding, retained source indices and exact copied live skin bindings. Each
fixture independently checks every original vertex against the resulting triangle BVH.
An animated validation test distinguishes changing nearest triangles from a real surface gap.

Example unlimited-collapse-work measurements on the development machine:

| Fixture | Source faces | Collision faces | Measured sampled error | Build time |
| --- | ---: | ---: | ---: | ---: |
| Planar 20 mm grid | 2,048 | 512 | <0.001 mm | 28 ms |
| Smooth curved surface | 4,608 | 1,612 | <3 mm | 268 ms |
| Concave waist | 3,200 | 1,120 | <3 mm | 131 ms |
| Thin foot | 768 | 268 | <0.001 mm | 2 ms |
| Sharp concave crease | 1,152 | 460 | <0.001 mm | 4 ms |

The default limits collapse work to 40 ms plus input preparation and final validation;
it can return a safe partial reduction. The timings above disable this work-time limit
to demonstrate achievable counts, not a promised activation cost. Final validation uses
up to 128 stratified source samples, normally 96 vertices and 32 face centroids.

The 3 mm target is finite-sample surface deviation at the captured pose, **not** endpoint
movement or a proven Hausdorff bound. Planar patches can preserve their surface union
while endpoints move much farther. Open/crease vertices remain fixed; accepted interior
collapses retain one original endpoint and its binding. Future skin deformation can
invalidate the initial approximation. After copying retained current vertices and calling
`Geometry.MarkChanged()`, use `ValidateCurrentNearest` to detect sampled current error and
fall back to the full collider when needed. Passing finite samples does not certify all
unobserved points. The full mesh remains available for all query/debug geometry.

The queue lifecycle regression also checks private input snapshots under concurrent live mutation,
serial completion order, 50 mm input support and cancellation without publishing stale colliders.
Production uses this background queue; it never waits for `WhenIdle` on the game thread.

The frozen-cache regression now checks eight repeated activations with different bone/world
rotations and COM/body-axis offsets, input and active-instance mutations, cached LOD reuse,
LOD disable and explicit replacement by a changed surface. The cache owns managed bone-local
geometry only; every native simulation keeps independent ownership of its collision buffers.
