Run `dotnet run --project Tests/FluidOutletBinding/FluidOutletBinding.csproj -c Release`.

Links the actual production outlet and lip selection implementations, config types and surface anchor types.
The partial fixture supplies only native pose/model data and existing sibling-method boundaries.
Custom driver names were observed directly in the user's loaded Rue model byte snapshot; this fixture
does not contain or distribute that model. It intentionally omits stock driver names for the custom profile.

Checks both Part 1 sides select outward patches rather than internal/back or opposite-side patches,
including geometry beyond the former 100 mm cutoff, posed transforms, nonuniform actor scale,
stable material binding and unchanged zero-offset Mouth delegation.
The real lower-lip candidate scan and reference selector are also exercised against supporting-edge,
lower-bulge and inward-facing patches. Initial facial landmark changes and global pose transforms
must preserve the selected material triangle and barycentric coordinates.

Does not verify native PBD reconstruction, actual Rue anatomy placement, game shading or optical visibility.
