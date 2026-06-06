# Generic Monster Ragdoll Plan

## Problem

The ragdoll system maps a single hand-tuned **human** bone profile
(`DefaultBoneDefs` / `config.RagdollBoneConfigs`, 42 `j_*` bones) onto every
character. `GetBoneDefs()` returns the same human defs regardless of skeleton.
There is no human/monster branch anywhere.

Consequences for non-human skeletons:

- **Fixed human capsule radii** (head 0.08m, pelvis 0.105m, ...) are applied to
  bones of completely different size. On a small skeleton (bat) the capsules
  become huge overlapping blobs → explosive solver forces → severe mesh
  deformation.
- **Human joint limits / masses** don't match monster anatomy → unnatural motion
  even when bones stay connected.
- Wings / extra limbs are driven as human arm bones (`j_ude_*`) or fall to
  rigid-follow propagation, tearing the mesh.

A prior fix (`589501a`) walks the config parent chain to the nearest present
ancestor so leaf bones no longer detach. That keeps humanoid monsters acceptable
but does nothing for the size/shape mismatch on truly non-human skeletons.

## Goal

Make non-human skeletons ragdoll plausibly **without touching the human path**.

The human profile has extensive special tuning (knee anti-hyperextension, breast
soft-body, skirt cloth chains, hair physics, face/partial-skeleton propagation,
`n_hara` follow). It must remain byte-for-byte unchanged. Auto-generation is
**only** for skeletons that are not human.

## Approach: Hybrid Dispatch (option C, variant 2)

In `RagdollController.InitializePhysics`, after resolving the human bone names:

1. **Detect humanoid.** A skeleton is humanoid if its structural signature bones
   all resolve: spine (`j_kosi`, `j_sebo_a`, `j_kubi`), upper arms
   (`j_ude_a_l`, `j_ude_a_r`), thighs (`j_asi_a_l`, `j_asi_a_r`).
2. **Humanoid → existing path** (Pass 1/2/3 over the human `BoneDefs`).
   Zero behavior change for human / beastman / imperial / humanoid monsters.
3. **Non-humanoid → generic path.** Generate a synthetic `RagdollBoneDef[]` plus
   a matching `nameToIndex` from the *actual* skeleton topology, then run the
   SAME Pass 1/2/3. Only the bone-def generation differs; body/constraint build
   and the entire `StepAndApply` write-back/propagation are reused unchanged.

### Why reuse Pass 1/2/3

`StepAndApply` reconstructs each bone transform from its body via
`SegmentHalfLength` + `CapsuleToBoneOffset`, and propagates non-simulated bones
from their skeleton parent's delta. This is already skeleton-agnostic — it keys
off `ragdollBones` + real `ParentIndices`, not bone names. So the generic path
only needs to *produce* `ragdollBones` (via synthetic defs through Pass 2/3);
everything downstream already works.

### Generic bone-def generation

Input: `SkeletonAccess` (bone count, parent indices, model pose).

- World position of every bone via `ModelToWorld`.
- Build child lists from `ParentIndices`.
- **Select** a bone to simulate iff it owns a forward segment: it has a child at
  distance >= `MinSegmentLength` (0.04m). This skips coincident/twig bones
  (fingers, tips); those follow via propagation. Cap the count at
  `MaxGenericBodies` (40), preferring the longest segments.
- Synthetic name `gen_<index>`; `nameToIndex["gen_<i>"] = i`.
- **Parent** = nearest *simulated* skeleton ancestor (walk up `ParentIndices`).
  The topmost simulated bone gets `ParentName = null` (single free root).
- **Adaptive sizing** (this is what kills the bat explosion):
  - `CapsuleRadius = clamp(segLen * 0.28, 0.02, 0.18)`
  - `CapsuleHalfLength = segLen * 0.45` (Pass 2 re-clamps anyway)
  - `Mass = clamp(segLen * 20, 0.5, 12)`
- **Generic limits** (all Ball joints — no hinge-axis guessing on unknown rigs):
  - `Joint = Ball`, `SwingLimit = 0.6 rad`, `Twist = ±0.35 rad`.
- Emit defs sorted by descending parent→bone distance so `boneToFirstChild`
  picks each bone's longest child as the capsule axis (best rotational stability).

### Active-defs lifetime

`StepAndApply` currently re-calls `GetBoneDefs()` each frame (returns human defs)
to read capsule extents for floor correction. Store the chosen defs in a field
(`activeDefByName`) set during init; `StepAndApply` reads that instead. For the
generic path this exposes the synthetic defs; for humans it is identical content.

### Human-only features under generic

`kaoBodyBoneIndex` / `nHaraIndex` resolve to -1 when `j_kao` / `n_hara` are
absent (already guarded), so hair physics and partial-skeleton propagation are
naturally skipped. No soft-body / cloth defs are generated.

## Files

- `CombatSimulator/Animation/RagdollController.cs`
  - `InitializePhysics`: detection + dispatch; set `activeDefByName`.
  - New `BuildGenericSkeletonDefs(SkeletonAccess)` + `IsHumanoidSkeleton(...)`.
  - `StepAndApply`: read `activeDefByName` instead of `GetBoneDefs()`.

No changes to `Configuration`, GUI, or the human bone profile.

## Risks / Tuning

- Detection threshold: a humanoid monster missing a signature bone routes to
  generic (still works, just less tuned) — acceptable, fail-safe direction.
- Generic capsule axis among siblings is heuristic; mis-axis costs stability, not
  correctness (bone transform is reconstructed via `CapsuleToBoneOffset`).
- Limit values (`0.6` / `±0.35`) and `MinSegmentLength` / `MaxGenericBodies` are
  first-pass; expect to tune against bat / bird / dragon / quadruped in-game.
- Zero qualifying bones → no bodies → corpse stays in frozen death pose (safe, no
  crash); log a warning.

## Verification

- Build Release only.
- Manual: bat (was severely deformed) ragdolls without explosion/blow-up.
- Manual: a few non-humanoid enemies (bird, dragon, quadruped, voidsent) settle
  plausibly, no parts flying to infinity, no NaN deactivation.
- Regression: human player + humanoid enemy ragdoll identical to before
  (cloth/hair/face/knees unchanged) — confirms human path untouched.

## Completion Criteria

- Humanoid skeletons take the unchanged human path.
- Non-humanoid skeletons build an adaptive, topology-correct ragdoll.
- No human-profile, config, or GUI changes.
- Release build succeeds.

## Iteration Log

### Iteration 1 — base generic builder + ancestor walk

- **Good:** elongated, well-separated rigs (e.g. ants) improved significantly.
- **Bad:** compact / fat-bellied bodies (a large prone toad-like reptile) shook
  violently, deformed, and flew across the screen — a physics explosion.
- **Cause:** `RagdollSelfCollision` defaults ON. Generated capsules on fat bodies
  overlap heavily; body-body contact on overlapping capsules creates explosive
  separation impulses (amplified by the model's render scale).
- **Fix:** force self-collision OFF for generic ragdolls (humanoid path keeps it).
- **Still open: bat.** Needs in-game diagnosis with `RagdollVerboseLog` — unclear
  whether it explodes (should now be fixed), stays too stiff (its small bones may
  fall under `GenericMinSegmentLength` = 0.04m and only rigid-follow), or deforms a
  specific way. Candidate follow-ups once observed: lower the segment threshold for
  small rigs (safer now that body-body collision is off), simulate leaf tip bones
  (head/wing-tip) so they flop, or add a generic angular-velocity damp to calm
  residual shaking.

### Iteration 2 — toad still exploded with self-collision off

- In-game log (Toxic Toad, 32 generated bones, self-collision already off) showed
  velocity growing **exponentially from frame 1** (1.8 → 116 → 4970 m/s over ~5s),
  bodies ending hundreds of metres away. Monotonic growth with no body-body
  collision ⇒ the **auto-built constraint network itself pumps energy**.
- Cause: a dense cluster of **small near-spherical capsules** (half-length < 0.08m,
  tiny inertia) joined by stiff ball joints that the 8-iteration solver can't
  converge; residual error compounds. Angular velocity blows up fastest (tiny
  inertia). Ants stay stable because their rig is sparse and elongated.
- Fixes (generic-only): per-frame **velocity clamp** (12 m/s / 16 rad/s) as a hard
  ceiling; raise `GenericMinSegmentLength` 0.04 → 0.08 to drop the tiny-body
  cluster; bump generic solver iterations to ≥16.
- Still to verify in-game: toad now settles instead of exploding; bat re-checked.
  If residual jitter remains, next levers: inflate small-body inertia, soften
  generic joint springs, or lower the velocity clamp.

Note: scale was investigated and ruled out as the explosion cause —
`ModelToWorld`/`WorldToModel` omit scale but are exact inverses, so the
read→simulate→write round-trip stays self-consistent at any model scale.
