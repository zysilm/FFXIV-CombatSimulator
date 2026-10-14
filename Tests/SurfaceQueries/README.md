# Exact high-poly surface queries

```powershell
dotnet run --project Tests/SurfaceQueries -c Release
```

Links production `SurfaceMeshBvh`, `CharacterSurfaceRuntime` and triangle geometry.
The 73,728-face fixture compares nearest and rotated sphere-support results against
exhaustive triangle evaluation, checks unchanged-pose mesh revisions, lazy full debug
geometry, sampled/global wireframe budgets and zero steady frame allocations.

Measured on the development machine: 172 checks passed; nearest queries visited an
average 107 triangles and support queries 94, retaining all 73,728 triangles. Tree
construction took 13.2 ms / 5.2 MiB; 1,000 pose + nearest + support + sampled-wireframe
updates took 26.5 ms with zero managed allocation after warmup. Timings are an offline
CPU microbenchmark, not a claim about final game FPS or BEPU contact costs.

The geometry and query runtime share a lazy BVH, refitted once per mesh revision.
`Bottom` uses an exact minimum-projection traversal, checked against every vertex for
40 arbitrary rotations, and caches repeated poses. `Bounds` rotates the cached local
AABB in constant time: it is conservative under rotation and intended for broadphase,
while contact support and nearest queries evaluate the actual triangles.
