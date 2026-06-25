# Zero-Animation Physics Death Collapse — Research Survey

**Status:** Research / direction-finding (no implementation yet)
**Goal:** Replace the current animation-dependent death ragdoll with a procedural,
biomechanically-grounded **collapse simulation that runs from t = 0.0s of death** —
so death variety comes from *controller parameters* instead of a library of death
animation mods.

---

## 0. Problem statement & current baseline

### What we do today (the thing we want to move past)
The current ragdoll is a **blended ragdoll**: the death animation plays, we truncate
it at some frame, freeze `Timeline.OverallSpeed = 0`, and hand the frozen pose to a
**passive** BEPU2 ragdoll that just goes limp under gravity. Consequences:

- Collapse *style* (kneel-then-faceplant, topple backward, crumple sideways…) is
  entirely dictated by **which death animation** was playing. To get variety we need
  **lots of death-animation mods** — expensive, and not procedural.
- The transition is "animation → limp", i.e. the body has **zero muscle tone** the
  instant physics takes over. Real dying bodies do not go instantly limp in all joints.

### What we want instead
Drive the dying character with a **physics/biomechanics model from the death instant**,
where the body's "loss of strength" (脱力) is simulated: e.g. knees buckle → kneel →
pitch forward, or topple stiffly backward, or crumple vertically. Variety = parameters.

### ⚠️ Hard caveat for this project (read before designing)
> **Our existing soft-body simulation is very poor and must NOT be used as a quality
> baseline or proof-of-concept.** The current soft-body path (`SoftBody = true` bones:
> breasts/jiggle) uses BEPU2 `AngularServo` + soft `BallSocket` springs, and the result
> is bad. The *mechanism* `AngularServo`/`ServoSettings` is still the right primitive for
> a PD-to-pose controller, **but the existing soft-body tuning proves nothing** — any
> active-death-collapse controller must be tuned and validated independently from
> scratch. Do not point at the current soft-body bones and say "see, servos work."

---

## 1. Industry landmark: Euphoria / Dynamic Motion Synthesis

NaturalMotion's **Euphoria** (GTA IV/V, RDR 1/2, Star Wars: The Force Unleashed) is the
gold standard for exactly this. It is built on **Dynamic Motion Synthesis (DMS)**, which
simulates the character as **three layers: body + muscles + motor nervous system**.

Key properties:
- Not "play animation then switch to ragdoll." Reactions are **synthesized in real time**,
  **different every time**, even replaying the same scene.
- Characters enter an **"intelligent ragdoll"** state rather than going limp: they
  protect the head while rolling, reach out to break a fall, grab injured body parts —
  realistic self-preservation.

**Relevance to us:** Euphoria *proves the direction is sound* and gives us the
**architectural pattern (muscle layer + neural/control layer + physics layer)**. It is
closed, heavyweight proprietary middleware with its own solver — **not** something we can
embed in a Dalamud plugin. Open-source "Euphoria-like" active ragdolls (notably in the
Roblox community) are lightweight imitations worth skimming for ideas.

## 2. Game-practical taxonomy (animation ↔ physics blend)

The industry splits the death/impact transition into three tiers:

| Tier | How it works | Used by | Relation to us |
|---|---|---|---|
| **Blended ragdoll** | Play death anim → bind ragdoll to last frame → limp physics | Halo 2/3, CoD4, L4D, TF2, Uncharted | **This is us today.** The reason we need anim mods. |
| **Active ragdoll** | Play target pose, but **constrain output to what physics allows**; PD controllers pull joints toward target | UT3, Killing Floor 2, UE5 Control Rig | **The direction we should take.** |
| **Learned controller** | RL net trained for balance / get-up / fall | DeepMimic family | Too heavy for a plugin. Long-term only. |

### The key reusable insight (from active-ragdoll balancing practice)
Every "standing active ragdoll" is two things:
1. **PD/PID controllers per joint** — each joint has a *target rotation* + *stiffness* +
   *damping*; the controller emits corrective torque toward the target pose. (Unity
   ArticulationBody typical seeds: stiffness ≈ 30, damping ≈ 100 — orders of magnitude
   only, not gospel.)
2. **External "marionette string" forces** — upward force on chest/torso/head to keep the
   body upright (Human: Fall Flat pulls the chest; T.A.B.S. pulls torso+head like puppet
   strings; Octodad pulls the head like a balloon). A downward raycast from the head
   measures height-above-ground to scale the force; leg forces reposition feet under the
   center of mass.

> ### 🔑 Central realization: **dying-collapse = the time-reverse of standing-balance.**
> Standing = "PD-pull joints toward a target pose + external force to stay upright."
> **Death = take that same control machinery and FADE the stiffness/gain from full → 0,
> drop the external upright force, and let gravity take over.** The *shape of the fade
> curve* and the *order in which joints release* fully determine the collapse style.
> **No death-animation mods required** — variety becomes a parameter table.

## 3. Academic research (physics-based falling / collapse / recovery)

A mature line from 2001 → 2024:

- **Virtual Stuntman / Composable Controllers** — Faloutsos et al., SIGGRAPH 2001.
  Earliest dynamic fall → roll → rise controller, pure physics.
- **Falling and landing motion control for character animation** — Ha & Liu, ACM TOG 2012.
  Splits a fall into an **airborne phase** (optimize moment of inertia to set landing
  angle) + a **landing phase in three stages: impact → rolling → getting-up.** No mocap,
  real-time. **This staged structure maps directly onto our "loss-of-balance → free fall
  → ground settle."** (Direct ACM link is paywalled; look for the authors' open PDF.)
- **Real-time falling animation with active and protective responses** — The Visual
  Computer, 2009. A biomechanics controller detects balance and triggers **active
  protective responses** (reach to break the fall, protect the head, protective backward
  stepping). This models the **"weakened but not yet dead"** reaction, distinct from
  "fully powered-off."
- **Real-Time Character Rise Motions** — arXiv 2304.05056 (2023). The inverse (getting
  up); useful for "struggling on low HP" variants.
- **SAMCON / DeepMimic / PDP (Diffusion Policy, SIGGRAPH Asia 2024)** — ML routes.
  Powerful (balance, acrobatics, recovery from large pushes) but require training.
  Long-term reference only.
- **Wobbledoll** (80.lv engineering writeup) — a complete DeepMimic-inspired active
  ragdoll system breakdown; good practitioner's view.

## 4. Biomechanics of collapse (the physiological grounding)

The intuition "full power-off = stiff timber fall" vs "some tone left = kneel then fall"
is biomechanically real:

- **Syncope / atonia (fainting → muscle-tone loss).** On loss of consciousness the body
  undergoes **global loss of muscle tone (atonia)**, rapid onset. This is the
  **"fully powered-off"** case: antigravity muscles all fail at once → the body
  **crumples roughly vertically (vertical collapse / crumple)**, landing spot decided by
  the center of mass and residual momentum, *not* a chosen direction.
- **Directed collapse with residual tone.** When failure is gradual (blood loss,
  exhaustion) the **antigravity muscles fail in sequence**: typically
  **ankle/knee extensors buckle first (knees give out) → kneeling → hip/spine tone fails
  → upper body pitches forward or to the side.** This is the "kneel first, then fall."

**Real-world fall-direction statistics** (from video-captured falls — usable directly as
randomization weights):
- **Forward ≈ 44%, Backward ≈ 41%, Lateral ≈ 33%** (categories overlap because…)
- **~40% of falls cannot be described by a single direction** — falls commonly **rotate
  from forward to sideways during descent.** This "rotation during the fall" is what makes
  a collapse look natural; with a ragdoll + a single initial directional impulse it
  **emerges for free.**
- Impact locations: hip/pelvis 47%, hand/wrist 27% (the wrist = protective reach-out,
  i.e. the protective-response case above).

## 5. Mapping to our BEPU2 setting (feasibility)

### 5.1 BEPU2 is sufficient; we already have *some* of the primitives
BEPU2 ships `AngularServo` / `OneBodyAngularServo` / `TwistServo` etc. — these **are** the
PD-to-pose mechanism. `ServoSettings.MaximumForce` ≈ muscle strength; `SpringSettings`
frequency/damping ≈ PD gains. So **"stiffness fade" = lerp each joint's servo
maxForce/frequency from full → 0 each frame.** No new framework needed.

Existing hooks in `RagdollController.cs` to reuse the *mechanism* (NOT the tuning):
- The soft-body path already constructs `AngularServo` (`InitializePhysics` Pass 3).
  ⚠️ Per the caveat in §0, treat that code as a *mechanism reference only* — its quality
  is bad and must not anchor expectations.
- `AddAnatomicalHingeRestBias` (currently early-returns / disabled) is literally a
  `TwistServo` pulling a hinge toward a rest angle — a dormant PD-to-pose hook.
- Pass 1 of `InitializePhysics` already reads the **real per-bone death-instant pose**,
  which is the natural initial PD target.

### 5.2 Recommended architecture: a "dying-muscle controller" layer over the passive ragdoll
- On death, instead of truncating animation → limp, attach a per-joint `AngularServo`
  with **target = the death-instant bone pose**, initial gain high (the body still "holds
  itself"). Then **decay gain per a collapse archetype**:
  - **Full power-off (headshot / instant death):** all joint gains → 0 within ~0.1s →
    near-vertical crumple, direction from residual CoM/momentum.
  - **Weakened kneel-then-pitch:** zero the **leg-extensor (knee/ankle) servo gains first**
    so knees buckle, hold hip/spine at partial gain to form a kneel, then after ~0.3–0.6s
    release the upper body to pitch forward. Sequenced decay = physiological order.
  - **Directed topple:** apply a small directional impulse at the CoM (direction from the
    44/41/33 distribution, or from the killing-blow direction); gravity does the rest, and
    the forward→sideways rotation during descent emerges naturally.
- **Protective response (optional):** only for "weakened-but-alive" — on detected
  imbalance, give the arms a servo target toward the ground to brace. Skip for death
  (corpses don't protect the head).

### 5.3 Staging (borrowing Ha&Liu + protective-response work)
`Phase 0` killing impact → `Phase 1` imbalance / buckle (controller active, gains
decaying) → `Phase 2` free collapse (ragdoll dominates) → `Phase 3` ground contact &
settle.

### 5.4 Risks / limits
- BEPU2 is a PGS solver; we already hit the "too-stiff hinge → frozen joint" trap
  (documented in `RagdollController.cs`). Keep servo gains modest and **prefer servos
  (soft/inequality) over equality motors.** We have scar tissue here already.
- Multiple archetypes + randomization need a **parameter table** (per archetype: gain
  decay curves + joint release order + initial impulse distribution). This is exactly the
  replacement for "death-animation mods" — **variety from parameters, not art assets.**
- True muscle synergies / neural control (Euphoria-grade) are out of reach. But
  **staged stiffness-fade + directional impulse + an archetype library** should cover all
  the cases we care about (kneel-pitch / backward / sideways / stiff-timber) and look
  visually close to Euphoria's *simple* deaths.
- **Validate independently of the existing soft-body sim** (see §0).

## 6. Suggested next steps
- (a) Turn §5.2 into a concrete tech design: data structures (archetype library, decay
  curves) + exact integration points in `InitializePhysics` / `StepAndApply`.
- (b) Build a minimal prototype: verify a BEPU2 `AngularServo` gain-fade can actually
  produce a "kneel → pitch forward" on our human rig (tuned fresh, ignoring soft-body).
- (c) Deep-dive a paper (e.g. find the open PDF of Ha & Liu 2012 for the staged control
  detail; the ACM copy is paywalled).

---

## 7. Spike results (empirical — branch `death-collapse-spike`)

We built a throwaway spike (per §6b): on death, attach a per-joint `AngularServo`
targeting the captured death-instant pose ("muscle tone"), then fade per-tier strength to
zero. Three archetypes, driven from a "Collapse Spike" panel in the Hold dev window, with a
"Die + Auto Collapse" button that arms the spike so it fires the instant physics
initializes (capturing the *standing* pose, before gravity crumples it).

### What passed
- **Q1 — can servos hold the pose against gravity? ✅** `StiffHold` keeps the body rigid in
  the captured pose. BEPU2 `AngularServo` PD-to-pose works on the human rig.
- **Q2 — smooth release? ✅** `UniformCollapse` holds briefly then fades all joints to zero
  and hands off to the passive ragdoll with no pop/snap. **The relaxation-fade model is
  sound.**

### What failed
- **Q3 — directed kneel-then-pitch (`KneelPitch`): ✗ not usable with this mechanism.**
  Observed: at high strength the knee was a rigid strut (no buckle); softening the hinge
  servo (lower strength / `hingeFactor`→0) let the knee bend, **but it bent *sideways*, not
  forward**, and the body toppled **backward into a supine pose** — nowhere near a kneel.

### Why (the conclusion that matters)
**"Hold-then-fade" is a *relaxation* model: it can only release a body from a pose it is
already in. It has no way to *drive* the body toward a NEW pose it was never in.** A kneel
is a new posture (knees deeply flexed, hips lowered, **feet planted**, weight shifted
**forward**) absent from the standing capture — so nothing pulls the body toward it, and a
freed knee just buckles whichever way the constraint network / gravity happen to push.

### Decision
- **Relaxation-type archetypes are proven and shippable as-is**: `StiffHold` (≈ full
  power-off / stiff timber fall) and `UniformCollapse` (≈ generic limp crumple). These two
  already cover the *atonia / instant-death* cases from §4.
- **Directional variety is a cheap add-on, independent of kneeling.** Layer a single CoM
  impulse (we already have `ApplyImpulse`) onto `UniformCollapse` to bias the fall
  forward / backward / sideways per the 44 / 41 / 33 distribution (§4). This buys most of
  the visible variety *without* the kneel machinery.
- **Directed posture-change archetypes (kneel-pitch, stagger, slump-to-knees) need a
  different subsystem** and should NOT keep being forced through servo-fade. They require
  *driving toward a target pose*:
  1. a **kneel target pose** for the legs to servo toward (a few hardcoded flexed
     hip/knee/ankle angles, or one cheap kneel keyframe) — drive, not relax;
  2. **planted feet** (temporary foot anchor / high friction) so flexing knees lower the
     body instead of sliding it out;
  3. a **forward CoM bias** (small pelvis torque or timed forward impulse) for the pitch.
  This is essentially a minimal active-ragdoll-to-goal controller — a bigger lift than the
  relaxation spike, to be scoped separately if kneeling is judged worth it.

**Net:** the spike de-risked the core mechanism (servo PD works), validated the relaxation
family, and proved kneeling is out of scope for *this* mechanism. Recommended next step is
architecture for the relaxation family + directional-impulse variety; treat directed
kneeling as a separate, later investigation.

---

## Sources
- Euphoria (software) — Wikipedia: https://en.wikipedia.org/wiki/Euphoria_(software)
- Euphoria — GTA Wiki: https://gta.fandom.com/wiki/Euphoria
- Ragdoll physics — Wikipedia: https://en.wikipedia.org/wiki/Ragdoll_physics
- Ragdoll Physics in Games: How to Blend Animation — MoCap Online: https://mocaponline.com/blogs/mocap-news/ragdoll-physics-animation-guide
- Balancing of Active Ragdolls in Games — Jan Schneider (Medium): https://medium.com/@jacasch/balancing-of-active-ragdolls-in-games-367f146b25fb
- Animation of Active Ragdolls in Games — Jan Schneider (Medium): https://medium.com/@jacasch/animation-of-active-ragdolls-in-games-32ca9d98afc9
- Wobbledoll: Developing an Advanced Full-Body Ragdoll System — 80.lv: https://80.lv/articles/wobbledoll-developing-an-advanced-full-body-ragdoll-system
- Towards A Fully Controllable Physics Driven Character — Digital Opus: https://digitalopus.ca/site/2019/12/04/towards-a-fully-controllable-physics-driven-character/
- Falling and landing motion control for character animation — ACM TOG 2012: https://dl.acm.org/doi/10.1145/2366145.2366174
- Real time falling animation with active and protective responses — The Visual Computer 2009: https://link.springer.com/article/10.1007/s00371-009-0321-9
- Composable Controllers for Physics-Based Character Animation (Virtual Stuntman) — SIGGRAPH 2001: https://web.cs.ucla.edu/~dt/papers/siggraph01/siggraph01.pdf
- Real-Time Character Rise Motions — arXiv 2304.05056: https://arxiv.org/pdf/2304.05056
- DeepMimic — arXiv 1804.02717: https://arxiv.org/pdf/1804.02717
- PDP: Physics-Based Character Animation via Diffusion Policy — SIGGRAPH Asia 2024: https://dl.acm.org/doi/full/10.1145/3680528.3687683
- Syncope (medicine) — Wikipedia: https://en.wikipedia.org/wiki/Syncope_(medicine)
- The 1-Second Paralyzer: Brief Muscle Tone Loss — Ubie: https://ubiehealth.com/doctors-note/losing-muscle-control-1s-paralyzer-tone-loss-47-1s41e3
- Effect of Fall Biomechanics on Risk for Hip Fracture (video-captured falls) — PMC: https://pmc.ncbi.nlm.nih.gov/articles/PMC7689902/
- The Circumstances, Orientations, and Impact Locations of Falls in Older Women — PMC: https://pmc.ncbi.nlm.nih.gov/articles/PMC5858880/
