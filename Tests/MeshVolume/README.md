# Mesh volume checks

Run `dotnet run --project Tests/MeshVolume/MeshVolume.csproj`.

The executable uses the plugin's exact BEPU `2.5.0-beta.22` package. It checks dynamic triangle mesh contact against triangle mesh terrain, a concave waist over a raised obstacle, asymmetric shape center recovery, nonuniform scale baked into shape vertices, closed and open inertia, and articulated mesh body contact. Flat, concave, and articulated contact cases run with both one-sided and doubled two-sided body triangles.

The concavity case has two feet at local y = -0.2 and a recessed waist at y = 0.15. A 0.25 m pedestal fits between the feet. A convex envelope would create false support on the pedestal; the triangle surface instead settles with the original bone origin at y = 0.2. The geometry is offset 0.17 m from the bone's x axis to detect accidental conflation of bone origin and center of mass.

BEPU triangle collision is one sided. The visible side is clockwise in right handed coordinates; the upward-facing terrain in these tests therefore has a downward `Cross(B-A,C-A)`. Open and closed inertia overloads taking `out center` both recenter mesh vertices in place. Body position must receive the rotated center once, while bone recovery subtracts it once.

Open inertia treats triangle soup as a surface mass distribution. It is appropriate for unsealed anatomical sections, but it is not a computed volumetric body density. Tests distinguish its surface center from closed solid inertia rather than assuming they agree.

The production `BodyMeshGeometry.cs`, `CharacterSurfaceRuntime.cs`, and surface profile types are linked directly. Checks cover bone recovery, sphere slope/edge/vertex/gap support against analytical and dense sampled references, deterministic equal-weight ownership, and 100 seeded three-owner surface partitions. Partition tests verify total area, triangle winding, dominant interpolated owner, barycentric source binding conservation, and explicit expanded face budget rejection. Runtime queries must retain the original supplied mesh at every usage setting, including millimetre mesh faces.

`LiveMeshPhysics.cs` additionally links the production skin deformer and native mesh refitter. Two jointed dynamic drivers deform one mixed-weight body surface while falling onto mesh terrain for 20 seconds. Meshes refit before and after each 120 Hz step, adjacent driver contact is excluded as in the plugin, and the test requires significant deformation, coincident shared boundaries, bounded joint error, stable floor contact, and zero managed allocations for warmed refit calls.

For a diagnostic partition allocation/timing sample, run `dotnet run -c Release --project Tests/MeshVolume/MeshVolume.csproj -- --benchmark`. This uses deliberately adversarial faces crossing three owners. It excludes native BEPU trees, runtime vertex arrays, and game rendering; the result is not a game frame-time estimate.

These are offline algorithm and package compatibility checks. They do not establish that a particular mod's runtime geometry has been read correctly.
