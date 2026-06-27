# Ragdoll Joint Model — Global Review, Literature, and Implementation Plan

**Status:** Research complete; plan approved. Tiers **D + B done** (committed), **C done**
(committed; C1 ROM table + C2 asymmetric axial twist live; C3 hyperextension block is
present but conservatively *widened* — needs an in-game twist-sign confirmation via the new
`[Ragdoll ROM]` log line to tighten to the true anatomical floor). Ball-joint asymmetric
**swing ellipse is deferred to Tier A** (fiddly in maximal-coordinate; ROM numbers already
in the table). Tier **A** not started (gated by feasibility spike A0).
**Goal:** Replace our pose-derived, maximal-coordinate joint modeling with an
anatomically-grounded **solver-agnostic joint model** (correct frames, asymmetric ROM,
anthropometric mass), and ultimately a reduced-coordinate articulated solver. The trigger
was a concrete bug (asymmetric knee hinge axis) that turned out to be a *base ragdoll*
problem amplified by the KneePowerLoss collapse — i.e. it happens even without kneeling.

---

## Part 1 — Global review of our current implementation

### 1.1 What we build today (`CombatSimulator/Animation/RagdollController.cs`)
- **Colliders:** one capsule per bone segment. (Standard; fine.)
- **Joints, two kinds (in `InitializePhysics`):**
  - **Ball** (hips, spine, shoulders, neck, ankle): `BallSocket` + symmetric `SwingLimit`
    cone + `TwistLimit` + weak `AngularMotor` (passive damping 0.01).
  - **Hinge** (knee `j_asi_b`, elbow `j_ude_b`): `BallSocket` + `SwingLimit` cone +
    `AngularHinge` (added recently for the sagittal-plane fix) + `AddAnatomicalHingeFoldStop`
    + `TwistLimit` + `AngularMotor`.
- **Joint frames are derived from the death-instant WORLD pose** at activation:
  - hinge axis ← `ComputeProfileHingeAxis` → falls back to `Cross(thighDir, shinDir)`
    (current pose);
  - twist bases ← `CreateTwistBasis(currentSegDir, …)`.
- **Limits are mostly symmetric:** a swing *cone* (`MaximumSwingAngle`) plus a twist ±range.
- **Masses (BoneDefs):** thigh 10, shin 3 + calf 1, foot 1, upper-arm 2, forearm 1.2,
  hand 0.5, pelvis 8, lower-spine 10, mid-spine 5, chest 6, neck 2, head 3.5.
- **Solver:** BEPU2 is **maximal-coordinate** — every bone is a free 6-DOF body held by
  *soft* constraints.

### 1.2 The systemic problems (each mapped to the literature)

**P1 — Joint axes/limits are pose-derived, not anatomy-derived (root cause).**
Authoritative methods define joint frames in a **fixed bind/rest frame**: twist axis = bone
rest direction, swing measured relative to that fixed frame (orangeduck; Baerlocher &
Boulic). The clinical standard is the **Grood–Suntay Joint Coordinate System**, adopted by
the **ISB** — axes anchored to bony landmarks, not the live pose. We instead take
`thigh×shin` of the *current* pose, which is **numerically degenerate for a near-straight
limb**. Observed: at death the knee is bent only ~2–3°, ~18–20° after entry conditioning,
so the cross product's direction is dominated by tiny lateral noise. Left knee axis came out
left-right `(0.94,0.30,-0.14)` ✓ but the right came out forward-pointing `(0.49,-0.09,0.86)`
✗ (≈60° wrong). Same machinery feeds the new planar `AngularHinge`, so it is mis-calibrated
too. **This is a base-ragdoll defect; KneePowerLoss merely drove the knee hard about the bad
axis, making it visible.**

**P2 — No swing-twist limits; we bolt a symmetric cone + a twist range together.**
Best practice is a **single swing-twist decomposition** per joint: 1-D (often asymmetric)
twist about the bone axis, 2-D swing limited by an **ellipse / reach-cone** (orangeduck;
Baerlocher & Boulic; Unity CharacterJoint / FinalIK use this). A symmetric cone **cannot**
express real ROM (hip flexes ~120° forward but extends only ~20° back; knee flexes ~140° one
way and 0° the other).

**P3 — Maximal-coordinate ragdoll: we fight the solver.**
BEPU2 holds bones together with soft constraints → joints **stretch/separate**, can't be made
stiff without the "frozen hinge" blow-up, and need damping hacks. Active-humanoid research
(DeepMimic, SAMCON, Isaac Gym) and the engines built for it (PhysX **articulations**, MuJoCo)
use **reduced-coordinate / Featherstone articulated bodies**: the humanoid lives in
generalized joint coordinates, so joints **cannot separate** and limits are exact per-DOF —
far more stable under stiff active control. BEPU2 has no articulation support; this is the
deepest gap.

**P4 — Knee modeled as a pure revolute with the wrong axis.** The revolute approximation is
acceptable for games (the real knee has screw-home / a moving axis) **only if** the axis is
anatomically fixed — which ours is not (see P1).

**P5 — Mass distribution off.** Against Winter/Dempster fractions (~70 kg): thigh should be
~7 kg (we have 10), and the trunk should be **pelvis-heavy**, but our lower-spine (10) >
pelvis (8) is inverted. Wrong inertia → wrong toppling dynamics.

### 1.3 What "correct" looks like (literature consensus)
1. **Every joint defined in a fixed anatomical frame** from the bind pose (twist = bone axis;
   knee/elbow hinge axis = skeleton medial-lateral, stable regardless of bend). — Grood-Suntay
   / ISB / orangeduck.
2. **Swing-twist limits** with **asymmetric** per-DOF ranges from clinical ROM; swing as an
   ellipse/reach-cone. — Baerlocher & Boulic / orangeduck.
3. **Reduced-coordinate articulation** for stable active control. — Featherstone / PhysX /
   MuJoCo.
4. **Anthropometric segment masses** scaled to body mass. — Winter / Dempster.

---

## Part 2 — Implementation plan

### 2.0 Guiding principle: separate the *model* from the *solver*
Tiers B, C, D are **solver-agnostic data + math**. Tier A swaps the *solver*. Build a clean
**`AnatomicalJointModel`** (per joint: rest frame, twist/hinge axis, asymmetric swing+twist
ROM, segment mass) that the constraint *builder* reads. Today the builder targets BEPU2
maximal-coordinate; for Tier A we write a *second* builder consuming the **same** model.
**The data survives; only the builder is rewritten** — this is what prevents Tier A from
throwing away B/C/D.

### 2.1 Order: D → B → C → A (with D+B as Milestone 1)
- **D** is independent and cheap but changes the dynamics all collapse forces were tuned
  against → must precede further force-tuning.
- **B** is foundational: limits (C) are expressed in B's frame; B also fixes the live bug.
- **C** builds on B's frames.
- **A** is biggest/riskiest; gated behind a feasibility spike; becomes a builder swap thanks
  to 2.0.

### Tier D — Anthropometric masses *(½ day, low risk)*
- Add `config.RagdollBodyMass` (default 70 kg).
- Replace hardcoded `Mass` in `BoneDefs` with `fraction × bodyMass` (Winter Table 3.1:
  thigh .100, shank .0465, foot .0145, upper-arm .028, forearm .016, hand .006,
  head+neck .081, trunk .497 split across pelvis/abdomen/thorax so **pelvis ≥ spine**).
- Log total mass ≈ bodyMass.
- **Validates:** weightier, correct topple; mass sums right.

### Tier B — Anatomy-fixed joint frames *(2–3 days, medium risk; fixes the live bug)*
- Capture a stable reference frame per joint from the **bind/rest skeleton (or character root
  frame + bone topology)**, NOT the death-instant pose.
- Knee/elbow hinge axis = skeleton **medial-lateral axis** projected ⟂ to the bone (stable
  regardless of bend), replacing `Cross(thigh,shin)`. Twist axis = bone rest direction.
  Ball-joint swing reference = bind frame.
- Route `ComputeProfileHingeAxis` / `CreateTwistBasis` and the `AngularHinge` through this.
- **Validates:** L/R knee axes become near-mirror (≈ ±character-right), stable across deaths
  and arbitrary death poses; the `(0.49,-0.09,0.86)` asymmetry disappears.
- **After B:** short **retune pass** on collapse forces (now vs correct masses + frames).

### Tier C — Asymmetric swing-twist ROM *(3–5 days, medium-high risk)*
- Extend the model with per-DOF ROM from clinical/ISB tables (knee 0°→~140° one-way; hip
  flex ~120°/ext ~20°; shoulder/elbow/neck/spine/ankle).
- Express in B's frame; swing as an ellipse via swing-twist, twist asymmetric min/max. In
  maximal-coordinate this is directional hinge-range + tight off-axis limits (fiddly; most
  reworked under Tier A, but the **ROM numbers survive**).
- **Validates:** no hyperextension / impossible poses; ROM matches anatomy.

### Tier A — Reduced-coordinate articulation *(weeks, high risk; gated by a spike)*
BEPU2 has no articulations, so **Phase A0 = ~1-week feasibility spike** to choose:
- **A1** integrate a reduced-coordinate articulation solver for the humanoid only, keep BEPU2
  for collision/environment;
- **A2** swap to an engine with articulations (PhysX .NET / MuJoCo);
- **A3** build a minimal Featherstone ABA for the humanoid chain (self-contained, large);
- **A4** *mitigation* — stay maximal-coordinate, get well-conditioned stiff constraints via
  more substeps + better limit conditioning (cheap fallback).
Then write the articulated builder consuming the existing `AnatomicalJointModel`.
- **Validates:** stiff active control, no separation/freeze.

### 2.2 Cross-cutting
- **Retune checkpoints** after D and again after B.
- Each tier behind a config toggle for A/B and rollback.

---

## Sources
- Joint Limits / swing-twist — theorangeduck (D. Holden): https://theorangeduck.com/page/joint-limits
- Baerlocher & Boulic, Parametrization and Range of Motion of the Ball-and-Socket Joint: https://infoscience.epfl.ch/record/100909?ln=en — PDF: https://link.springer.com/content/pdf/10.1007/978-0-306-47002-8_16.pdf
- ISB recommendation on Joint Coordinate Systems (Wu et al. 2005, Grood-Suntay basis): https://media.isbweb.org/images/documents/standards/Wu%20et%20al%20J%20Biomech%2038%20(2005)%20981%E2%80%93992.pdf — Part I (PubMed): https://pubmed.ncbi.nlm.nih.gov/11934426/
- PhysX Articulations (reduced-coordinate): https://nvidia-omniverse.github.io/PhysX/physx/5.6.0/docs/Articulations.html
- Featherstone's algorithm: https://en.wikipedia.org/wiki/Featherstone's_algorithm
- Unity — Joint and Ragdoll stability: https://docs.unity3d.com/Manual/RagdollStability.html
- Winter anthropometry / body segment parameters: https://www1.udel.edu/biology/rosewc/kaap686/notes/anthropometry.html
- Dempster body segment parameters (1967): https://onlinelibrary.wiley.com/doi/10.1002/aja.1001200104
