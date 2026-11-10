# Water buoyancy and exact ground collision

Both live on the Ragdoll tab under **Ground Detection**.

## Float in water (on by default, as Water)

`Animation/WaterBuoyancy.cs` (pure) and `UpdateWaterProbe` / `ApplyWaterForces` in
`Animation/RagdollController.cs`.

- **Detection.** The game's collision scene has a collider for every water surface you can fish or
  swim in; its material has bit `0x8000` and lacks the ground bit `0x4000`, which is why ground raycasts
  never see it. `WorldCollisionMesh.TryFindWaterSurface` raycasts for that bit above and below the
  corpse, at activation, twice a second, and after the corpse has moved 1.5 m.
- **Forces, per body, per fixed step.** Lift `g x submerged share / relative density` upward, then
  implicit drag (linear 2.5 /s plus 1.5 /m x speed, angular 3 /s, all scaled by the submerged share)
  toward the water's own vertical motion. Nothing poses the body.
- **Densities.** Anatomical proportions (chest 0.78, pelvis 0.96, head 1.02, thighs 1.03, arms 1.04,
  hands 1.08, shins and feet 1.09), divided by the density of the liquid. The dropdown offers two:
  **Water** (1.0): 0.92 for a whole body, about 8% of it above the surface, as a real body floats;
  **Dense liquid** (2.0): 0.46, about 54% above the surface. Either way chest up, legs trailing.
- **Surface.** Three deep-water wave trains (5 m / 2.7 m / 1.3 m long, 2.5 / 1.4 / 0.7 cm high). The
  game's own water animation cannot be read, so the swell is kept small enough not to disagree with it.
- A body in water never goes to rest, and the heavy-fall gravity boost is off while any part is wet.

Limits: only the player's and NPCs' ragdoll bodies float; dropped weapons, gear and garments do not.
Water with no collider (decorative puddles, some streams) is not detected. The swell is not the
visible one.

## Exact ground collision (on by default)

`Animation/WorldCollisionMesh.cs` (reads game memory), `Animation/CollisionMeshGeometry.cs` (pure) and
`TryAddExactGround` in `Animation/RagdollController.cs`.

The default ground is a 13 x 13 grid of heights raycast every metre, which smooths over steps, edges
and props. With the option on, each terrain patch also takes the game's own collision triangles within
6 m horizontally and 8 m vertically: mesh colliders triangle by triangle, box, cylinder and sphere
colliders tessellated, with the same gates a ground raycast applies (collider active, layer 1,
effective material has `0x4000`). Both windings of every triangle are added. The sampled grid stays as a
safety net 0.25 m lower. More than 20 000 triangles, or an unreadable scene, falls back to the grid.

Patch size follows the body (`CollisionMeshGeometry.PatchFor`): the ground always extends at least
twice the body's reach past its centre, and never less than 2 m. A human gets a 6 m radius and a new
patch once its centre is 4 m from every existing one; a monster reaching 5 m gets 20 m and 10 m; the
radius is capped at 30 m. The triangle budget grows with the patch area, up to 80 000, and the sampled
grid coarsens so it never exceeds 25 samples a side.

The scene is walked under the same shared lock the game's raycasts take. Not used together with
Rendered Mesh Body Collision. Weapon and gear drops still use their own sampled ground.

Note this is the game's *collision* mesh, which is what characters stand on; it can still differ from
the visible ground where the two were authored differently.

## Verification

`dotnet run --project Tests/WorldPhysics -c Release`: floating share and bobbing, sinking, a 5 m fall
into water (deepest 1.0 m, afloat again in 1.9 s), rising from 3 m (4.0 s, at most 0.9 m/s), whole-body
density, surface bounds and speed, drag passivity; material rules, collider tessellation and patch
culling. None of this reads the game; detection and the in-game look are unverified.
