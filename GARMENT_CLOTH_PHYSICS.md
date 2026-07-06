# Garment Drop Physics Overhaul — Analysis & Design Notes

> Scope: the "armor detach" garment drop feature (slide-to-floor + collapse), currently implemented
> in `CombatSimulator/Animation/DismembermentController.cs`.
> Status: analysis / design exploration. No implementation yet.

---

## 1. Problem Statement

The default profile ("slide to floor" + collapse settle) produces two visible defects:

1. **Slide misalignment.** While a garment slides down the body, it drifts / tilts away from the
   body it is supposed to hug. Pants are the worst offender — they visibly sit askew relative to
   the legs during the slide.
2. **Unrealistic collapse.** The collapse ("deflate") phase is a placeholder. Two failure modes:
   - The garment visually **squashes straight through the corpse mesh** down to the floor when it
     comes to rest on top of the body.
   - With unlucky resting orientation, the garment **ends up standing upright** after the
     squash, instead of lying flat.
   Even in the good case, a rigid box that shrinks along one axis does not read as fabric
   settling.

Question raised: BEPU2 appears capable of real cloth simulation (its demo suite drapes a cloth
lattice over a ball). How hard would it be to use that here?

---

## 2. Current Implementation

The drop is a three-phase pipeline. All references are `DismembermentController.cs` unless noted.

### Phase 1 — Visual bind ("slide")

- A clone is spawned that renders **only the kept gear model**; all other models/skin are hidden
  each frame (`HideNonKeptModels` / `HideSkinMaterials`, ~line 2005).
- The clone's skeleton pose is a **one-shot snapshot from strip time**: `ApplyHandoffPose`
  (line 1133) replays `c.Handoff.Bones` (captured once) into the clone's `ModelPose` every frame.
  It does **not** mirror the source body's live pose.
- The clone's **root transform** is driven by `TryUpdateGarmentVisualBind` (line 2288):
  - Anchor position = the **average world position** of a fixed bone set on the source body
    (`BodyGearVisualBindBones` for slot 1 body gear, `LegsGearHandoffBones` for slot 3 legs),
    via `TryAverageSourceBoneWorldPositions` (line 2456).
  - Rotation = a coarse "garment frame" built from source bone positions:
    `TryBuildSourceGarmentFrame` (line 2514) — for legs, "up" = knee-centre → hip-centre
    averaged over both legs; front sign resolved against the skeleton root yaw
    (`TryCreateGarmentFrameRotation`, line 2632). Bone local axes are *not* used — per this
    project's earlier finding, they sit ~90° off the model convention; only the skeleton root
    yaw is a reliable front anchor.
  - Slip (slide-down distance) accumulates along **world down** (`ComputeAutoBindSlip`,
    line 2362): slide-to-floor preset integrates `ClothHoldSlideSpeed * ease * dt` unbounded.
  - Release when the garment's lowest point reaches the floor (`GarmentBindReachedFloor`,
    line 2395), the body settles, or a hard frame cap trips (`ShouldReleaseGarmentBind`).

### Phase 2 — Rigid body

- On release the garment becomes **one rigid body**: a compound of boxes from
  `BuildGearShapeSpec` / `BuildGearShapeParts(slot)` (line 4151), authored per equipment slot.
- Preferred path injects the body into the **player ragdoll's own simulation** so it collides
  with the corpse: `TryCreateRagdollGearBody` (line 1282) →
  `RagdollController.TryCreateExternalDynamicBody`. Fallback: a local sim in
  DismembermentController plus kinematic bone proxies for the corpse (`NpcBoneStatic`,
  line 281 — kinematic capsules tracking actor bone segments).
- The clone's skeleton is **frozen** (pose snapshot re-asserted every frame, line 2123) and the
  whole model rigidly follows the body pose; render offset math at lines 2160–2192.
- Handoff drag for the first 30 frames eases the free body away from the body anchor
  (`ApplyGarmentHandoffDrag`, line 2674).

### Phase 3 — Collapse ("deflate")

- Once the piece rests near the ground (`UpdateGearDeflateProgress`, line 2840), a 60-frame
  smoothstep drives a **purely visual non-uniform scale** (`ResolveGearVisualSquashFactor`,
  line 2877; per-slot final factors in `ResolveGearFinalSquashFactor`, line 2997 — e.g. pants
  crush thickness to ~0.4).
- Symmetric slots (hat, accessories) re-point the crush axis to the gravity-aligned local axis
  (`LockGearSquashAxisIfNeeded` line 2899, `SymmetricSquashSlots = {0,5,6,7,8,9}`).
  **Asymmetric garments (body 1, legs 3, hands/feet) keep their authored local crush axis.**
- The physics shape is swapped for a squashed version mid-deflate
  (`TryApplyGearCollapsedPhysicsShape`, line 2959).
- Skirt cloth-bone chains (`j_sk_*_a/b/c`) are separately driven to hang straight down and
  clamp above ground: `DriveSkirtHang` (line 3440) — a proven example of **per-frame,
  per-bone ModelPose driving** on the garment clone.

---

## 3. Root Cause Analysis

### Issue 1 — slide misalignment (pants tilt)

Three compounding errors, all in Phase 1:

1. **Stale internal pose.** The clone replays a strip-time snapshot while the source corpse's
   ragdoll keeps moving. Bent/​moving legs diverge from the frozen pants pose.
2. **Coarse root fit.** A bone-average anchor plus a two-axis garment frame cannot represent a
   pose where the two legs point different directions, the knees are bent, or the pelvis is
   rolled. The residual shows up as a constant tilt/offset of the whole garment.
3. **World-down slip.** The slide direction should follow the limb axis (pants slide *along the
   legs*), but slip is applied along world −Y. On a lying/leaning corpse this drags the garment
   diagonally off the body.

### Issue 2 — collapse artifacts

1. **Deflate is blind to support.** The trigger (`IsGearNearGroundContact` + rest frames) and the
   ground clamp both reference **terrain height only** (`GearGroundY`). A garment resting on the
   corpse squashes toward the terrain, i.e. visually through the body. (`settledOnSupport`
   exists but still deflates toward the authored axis with the terrain clamp.)
2. **No gravity re-pointing for asymmetric slots.** Pants/body pieces always crush along their
   authored local axis. If the rigid box comes to rest on its side or end, the crush axis is
   horizontal — the piece flattens sideways and remains standing.
3. **Fundamental fidelity ceiling.** One rigid box + scale animation cannot fold, drape, or
   crumple. Legs of dropped pants never separate; a shirt never folds over an arm.

---

## 4. BEPU2 Capability Survey (verified locally)

Package in use: **BepuPhysics 2.5.0-beta.22** (`CombatSimulator.csproj`;
`%USERPROFILE%\.nuget\packages\bepuphysics\2.5.0-beta.22\lib\net8.0\BepuPhysics.dll`).

### 4.1 Constraint types present in the local DLL

Confirmed by scanning the assembly (all found):

- **Articulation (ragdoll-style):** `BallSocket`, `BallSocketMotor`, `BallSocketServo`,
  `SwingLimit`, `TwistLimit`, `TwistServo`, `TwistMotor`, `AngularHinge`,
  `AngularSwivelHinge`, `Hinge`, `SwivelHinge`, `AngularServo`, `AngularMotor`, `Weld`,
  `PointOnLineServo`, `LinearAxisServo/Motor/Limit`, one-body variants
  (`OneBodyLinearServo/Motor`, `OneBodyAngularServo/Motor`).
- **Cloth/soft-body building blocks:** `CenterDistanceConstraint`, `CenterDistanceLimit`,
  `DistanceServo`, `DistanceLimit`, `AreaConstraint`, `VolumeConstraint`.

### 4.2 The "cloth over a ball" demo pattern (upstream `bepuphysics2/Demos/Demos/ClothDemo.cs`)

The official cloth demo builds cloth as a **lattice of small dynamic sphere bodies**:

- Grid of spheres (small radius, low mass), neighbours linked with
  `CenterDistanceConstraint` (structural links; diagonal shear links optional).
- Springiness controlled through each constraint's `SpringSettings` (frequency/damping ratio);
  low frequencies give stretchy cloth, high frequencies need more solver substeps.
- Self-collision and body collision come from the sphere shapes themselves; collision filtering
  excludes directly-linked neighbours.
- The demo drapes lattices over static/dynamic shapes (the "ball cloak") at thousands of bodies
  in real time. Performance is **not** the blocker for us.

### 4.3 The ragdoll demo pattern (already adopted in this repo)

`RagdollController.cs:890` documents the joint idiom copied from BEPU's RagdollDemo:

- Ball joint = `BallSocket` + `SwingLimit` + `TwistLimit` + `AngularMotor` (3-DOF with cone).
- Hinge joint = `Hinge` + `SwingLimit` + `AngularMotor` (knees/elbows use SwingLimit, not
  TwistLimit, to bound bending).

### 4.4 In-repo infrastructure already available

Everything an articulated garment needs already exists and is battle-tested:

| Capability | Where |
|---|---|
| Inject external dynamic bodies into the corpse ragdoll sim | `RagdollController.TryCreateExternalDynamicBody` (line 371), `ExternalShapePart` (229), `ExternalBodyHandle` (243) |
| Read back pose / set shape / velocity ops on those bodies | `TryGetExternalBodyPose` (492), `TrySetExternalBodyShape` (414), `TryApplyExternalVelocityDelta` (537), `TryDampenExternalBodyVelocity` (555), `RemoveExternalBody` (579) |
| Multi-body articulated piece rigs (dismembered limbs already do this) | `LimbRig` / `LimbBody` (`DismembermentController.cs:260`), `BuildLimbRig` (4326), `BuildHeadRig` (4504) |
| Per-frame per-bone ModelPose writing on the garment clone | `ApplyHandoffPose` (1133), `DriveSkirtHang` (3440), frozen-pose re-assert (2123) |
| Corpse collision proxies for the local sim | kinematic `NpcBoneStatic` capsules (281) |
| Terrain patches, ground raycast, ground re-sampling | `CreateTerrainPatch`, `MaybeResampleGearGround` (2937) |

### 4.5 The hard constraint: rendering is bone-driven

We cannot deform mesh vertices — the game renders garments as skinned meshes driven by the
character skeleton's havok `ModelPose`. Whatever physics we run, the result must be expressed as
**bone transforms**. Garment bone budgets:

- **Legs (slot 3):** waist (`j_kosi`) + per-leg thigh/knee/calf/ankle chains ≈ 9–13 usable bones.
- **Body (slot 1):** spine ×3 (`j_sebo_a/b/c`), clavicles, upper/lower arms, wrists, plus
  `j_sk_*` skirt chains on long coats ≈ 7–16 usable bones.
- Skinning weights are authored for body deformation; extreme bone poses produce
  candy-wrapper/self-intersection artifacts.

This caps the useful resolution of *any* cloth simulation at roughly "one rigid segment per
garment bone" — folds finer than the bone grid are unrepresentable.

---

## 5. Options Considered

### Option A — Patch the current scheme (cheap, treats symptoms)

1. Add asymmetric slots to the gravity-axis re-pointing (fixes "standing garment").
2. Change the deflate target height from terrain to **support height** — top of whatever is
   underneath (corpse collision bodies or terrain) — fixes the squash-through-body.
3. Slide fix: see the live-mirror change in Option B (independent, cheap).

*Verdict:* removes the two worst artifacts but the collapse still reads as a shrinking box.

### Option B — Articulated "empty garment" rig (recommended)

Replace the single rigid body of Phase 2 with a **small articulated rig**, exactly the
`LimbRig` idiom applied to garments:

- **Pants:** 5 bodies — pelvis + 2 thighs + 2 calves (capsules/boxes sized from the existing
  per-slot shape parts). **Body gear:** 6–7 bodies — chest + abdomen + 2 upper arms +
  2 forearms (+ skirt handled by existing `DriveSkirtHang` or extended with `_a` tier bodies).
- Joints: `BallSocket` + wide `SwingLimit`, **no motors** (or very weak `AngularServo` toward a
  crumpled pose), heavy velocity damping, low per-body mass (fabric ≈ 0.2–0.5 kg per segment).
  The rig should behave like floppy empty clothes, not a ragdoll with muscles.
- Bodies live in the corpse's ragdoll sim (`TryCreateExternalDynamicBody`) so garment-vs-corpse
  collision is native; ground contact via existing terrain patches.
- Per frame, write each body's pose back to its garment bone in the clone's `ModelPose`
  (the `LimbBody.BodyToBoneRotation` mapping already solves the body↔bone frame problem), and
  derive the clone root from the pelvis/chest body.
- Deflate shrinks to a subtle "air escaping" scale (or is removed); the crumpling now comes from
  actual articulation. The squash-through-body and standing-garment failure modes disappear
  structurally — segments rest on whatever supports them.

*Cost:* medium. ~5–7 bodies + ~4–6 joints per garment is negligible next to the existing
ragdoll. The work is the bone↔body mapping table per slot, joint tuning, and integrating with
the existing settle/release state machine.

### Option C — True lattice cloth (ClothDemo style)

Physics side is proven feasible in our BEPU version (§4.2). But:

- Lattice node positions must be **fit back onto 7–16 bones** (per-bone weighted rigid fit /
  shape matching) — a lossy projection that discards exactly the fold detail the lattice
  computes. High math + tuning cost for detail the renderer cannot show.
- Skinning artifacts under extreme bone poses get worse, not better, as the fit gets more
  aggressive.
- Needs collision filtering, substep tuning, and anchoring logic per garment type.

*Verdict:* poor cost/benefit as a general solution. Revisit only for slots whose meshes have
dense dedicated cloth chains (capes/long skirts with full `j_sk` grids), where a **coarse**
lattice (one node per cloth bone) could drive bones 1:1 without a fitting step.

### Slide fix (applies to A and B) — live mirror + limb-axis slip

Replace the Phase 1 stale-snapshot + root-fit approach:

1. **Live mirror:** each frame, copy the source body's *current* ModelPose bones into the clone
   (same mechanism as `ApplyHandoffPose`, but sampled live instead of from the snapshot). The
   garment then overlays the corpse exactly — zero tilt by construction.
2. **Slip along the garment axis:** apply the slide offset along the pelvis-down / leg direction
   (from live bone positions), not world −Y. Floor test logic (`GarmentBindReachedFloor`)
   stays as-is.
3. Release velocity seeding already uses the raw anchor (line 2462) and carries over unchanged.

*Cost:* small. This is worth doing regardless of which collapse option is chosen.

---

## 6. Recommendation

Phased, each phase independently shippable:

1. **Phase 1 (small):** live-mirror bind + limb-axis slip → fixes the slide misalignment.
2. **Phase 2 (main course):** articulated empty-garment rig (Option B) for slots 1 and 3,
   keeping the current single-body path as fallback (config toggle / auto-fallback on rig build
   failure, mirroring the existing convex-hull→capsule fallback philosophy). Retire or soften
   deflate for these slots. Hats/accessories keep the current path (a rigid piece + squash is
   fine for a helmet).
3. **Phase 3 (optional, later):** coarse per-cloth-bone lattice for cape/long-skirt chains if
   `DriveSkirtHang`'s kinematic hang proves insufficient.

Skip full lattice cloth (Option C) as a general mechanism — the bone-driven renderer caps its
payoff below its cost.

---

## 7. Risks / Open Points

- **Skinning artifacts:** garment bones posed far outside animation range (e.g. calves folded
  against thighs) may tear or candy-wrap the mesh. Mitigate with conservative `SwingLimit`s —
  crumple *softly*, don't aim for a perfectly flat pile.
- **Empty-garment interior:** with the body hidden, extreme folds can expose the garment's
  inside/backfaces. Joint limits again bound this.
- **State machine integration:** settle detection, ground visual offset, and the collapsed
  physics shape swap all assume one body; they need per-rig aggregate equivalents (the limb
  rig already solves settle detection for multi-body pieces — reuse it).
- **Handoff continuity:** transitioning from live-mirror bind to the rig must seed each segment
  body with its bone's current pose + velocity (the per-bone handoff data already captures
  linear/angular velocity per bone — `ResolveBodySeedVelocity`, line 1189).
- **Sim lifetime:** external bodies die with the player ragdoll sim; the fallback local-sim path
  needs the same rig support or a graceful single-body downgrade.
