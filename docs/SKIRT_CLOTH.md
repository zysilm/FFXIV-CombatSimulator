# Skirt cloth for detached garments

Skirts, coat tails and dress skirts are skinned to 18 skirt bones on the main skeleton:
`j_sk_{f|s|b}_{a|b|c}_{l|r}`, six chains (front, side, back; left, right) of three bones. In every
race's skeleton the six `_a_` bones are children of `j_kosi` and form a waistband ring; the chains
reach roughly to the knee (adult: front 0.415 + 0.12 m, side 0.218 + 0.273 m, back 0.186 + 0.306 m).

## Runtime

`Animation/Attachment/SkirtCloth.cs` (pure, engine-independent) and `EnsureSkirtCloth` /
`DriveSkirtCloth` / `CollectSkirtLegCapsules` in `Animation/DismembermentController.cs`. Used for Body
and Legs garments in both modes (drop, half off), in either simulation.

- One particle per skirt bone plus a virtual hem tip below each chain: up to 6 chains x 4 particles.
- The waistband row is carried: each frame it is placed where the bone it hangs from (posed by the
  garment rig that frame) puts it. Everything below is free.
- Constraints: chain edges keep their length (solved last); ring and shear edges only limit how far
  neighbouring chains can be pulled apart (5% / 10% slack), with a 3 cm minimum separation. There is no
  bending stiffness and no rest shape.
- Collision, one-way: particles and chain edges are pushed out of the body's thigh and shin capsules
  (the ragdoll's when it runs, otherwise the live skeleton with nominal radii) and above the ground.
- Fixed 60 Hz steps, 8 iterations, shown interpolated (up to one step late).
- Bone positions are the particles; bone rotations are the captured rotation turned by the change of
  the frame built from the down-chain edge and the direction across the ring. No bone-local axes.
- The garment collapse (`ApplyGarmentFlatten`) skips the skirt bones. A top whose skeleton has no
  skirt bones keeps the old frozen hang (`DriveSkirtHang`), as does the single-box fallback.

## Verification

`dotnet run --project Tests/RetainedGarments -c Release`, cases prefixed `skirt:`. With the measured
adult geometry: lying on the ground the frozen bell crushed chain lengths by 70%, the cloth keeps them
within 0.1%; a thigh raised through the 0.4 m front segment is not penetrated by particles or edges;
stable at 30/60/144/300 fps with a tumbling waist and after a 40 m teleport; bone rotations follow a
turned waist. None of this certifies the in-game look.

## Limits

Eighteen bones: the smallest fold is 15-40 cm, with no wrinkles and no crumpled heap. The front
panel's first segment is one rigid 0.4 m plate. Everything below the knee is a rigid extension of the
last bone. Chains gather where they meet and the mesh between them can still clip. The skirt never
pulls on the garment rig. Gear that uses extra `j_ex_*` bones instead of the skirt bones is not covered.
