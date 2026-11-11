# Ragdoll: fitted hulls for the torso and thighs

Code: `Animation/StructuralMeshFitter.cs` (the fit, pure), `StructuralMeshSampler.cs` (reads a model's
surface, pure), `StructuralHullShape.cs` (physics shape), `RagdollController.StructuralMeshFit.cs`
(gathers the drawn models, runs the fit on a worker, caches it). Test: `Tests/RagdollHullFit`.

## What was wrong

A body lying on its back floated at the hips and waist. Two causes:

- The automatic mesh fit had never run. It sampled only `obj/body` models, which are seam connectors;
  the body's surface is in the equipment slots. Every ragdoll logged `accepted 0/18` and used the
  built-in volumes.
- The built-in torso volumes are capsules around the spine (pelvis radius 10.5 cm). The spine runs
  along the back of the torso, so the capsule stood several centimetres proud of the skin behind it,
  and the thigh capsule (radius 4.5 cm) was thinner than the thigh.

## What it does now

The pelvis, the three spine segments and both thighs collide with a convex hull fitted to the
character's own surface: the models in slots 1-4 (body, hands, legs, feet) as drawn, with mods, shape
keys, hidden parts and the race deformer applied, in the reference pose. Arms, shins, hands, feet and
head keep their built-in volumes.

How a hull is made, per bone:

1. Surface points skinned to the bone (weight at least 30% of the bone's strongest), each weighted by
   the area it stands for, so dense mesh regions do not outvote sparse ones.
2. The segment is cut into 3 slices (4 for a thigh) along its length. In each, twelve facets around the
   segment are placed where 98% of the slice's surface lies inside them.
3. Each slice is a prism over its whole length, so the thickest part of the body is a flat area and the
   thinner slices taper away from it.
4. No corner on a high point: every facet must be at least 0.9% of the body's height wide (about
   1.5 cm). Where the surface comes to a ridge or a point, the facet facing it is moved in until it is
   a face. A body therefore rests on a face, never on a corner or an edge.
5. Soft tissue: all facets are drawn in by 0.6% of the body's height (about 1 cm), because flesh gives
   under weight and a rigid hull cannot.

The box (torso) or capsule (thigh) measured from the same samples stays as the body's envelope, for
everything that asks about its rough size; a fit whose envelope is not a plausible size for the body's
height is refused and the built-in volume kept. The hull replaces the envelope for contact, centre of
mass and inertia.

## Cost

- Fitting reads every vertex of the drawn models: a few milliseconds for the game's own models, up to
  about 100 ms for a dense modded body. It starts when the ragdoll is armed (at death) and runs on a
  worker thread; one appearance is fitted once and cached. If physics starts before it is done, the
  ragdoll waits at most 150 ms, then uses the built-in volumes for that one time.
- Simulation: six hulls instead of six capsules per character, about 1.5-2x the cost of those six bodies
  while they are awake (tens of microseconds per step); nothing once the ragdoll sleeps.

## Verification

`dotnet run --project Tests/RagdollHullFit -c Release` (needs the game installed; uses the built
plugin's model parser). It fits real bodies offline, lays them on flat ground with the ragdoll's own
solver settings, and measures how far the skin of each segment ends up above the ground, against where
a rigid cast of the body would have it (the hollow of the lower back stays a hollow).

20 naked bodies (14 race/sex skeletons with the game's own body, 6 with a modded body), built-in ->
fitted, centimetres:

| Lying on its back | pelvis | waist | back | chest | thigh |
|---|---|---|---|---|---|
| floating above where its shape puts it | 4.6 -> 0.4 | 3.3 -> 0.4 | 1.5 -> 0.2 | 0.3 -> 0.0 | 0.7 -> 0.4 |
| sunk into the ground | 0.1 -> 0.2 | 0.0 -> 0.0 | 0.2 -> 0.0 | 0.2 -> 0.2 | 0.6 -> 0.2 |

Face down and on the side improve as well (built-in volumes sank the back 4-5 cm into the ground), and
12 clothed bodies are checked to be no worse than the built-in volumes. Also checked: every hull rests
on faces in all twelve directions; the fit is centred on the surface and not on the bone, scales with
the body, ignores stray vertices and mesh density, and refuses too little or implausible data.

Not verifiable offline: Roegadyn and Hrothgar men and Lalafell. The skeleton files and models read
offline do not agree in size for them, so the fit is refused there (built-in volumes, as before). The
log line `Ragdoll mesh geometry: fitted N/6 segment(s)` says what happened in game.

## Follow-up after the first in-game test (2026-11-11)

The first build made arms fly about and bent the waist. Both came from what the offline test had left
out: the ragdoll's own bodies collide with each other.

- **Waist.** A hull's faces are parallel to the axis it is measured along, and a lying body turns until
  the face under it is flat. Measured along each bone, every torso segment lay flat on its own and the
  waist bent by the angle between them (about 6 degrees with the built-in volumes, 15-24 with the first
  hulls). Torso segments are now all measured along one axis, pelvis to neck, so they lie down together
  (back to about 1-6 degrees for most bodies; some modded bodies 10-18).
- **Arms.** A hull reaches the skin where the built-in volume sat deep inside the body, so a limb lying
  against the body starts out overlapping it; the contact pushed it out, the joints pulled it back, and
  it was flung about. `StartOverlapAllowance` is the general answer, for every pair of the ragdoll's own
  bodies: a contact only resists going deeper than the pair was when the ragdoll started, the allowance
  shrinks as the pair separates and never grows. Offline, with no gravity, bodies were pushed up to 5 cm
  by overlap alone before; now 0.0 cm for all 38 bodies.

The offline test now runs with self-collision and the allowance, and reports the waist bend, the
fastest arm speed and the weightless drift for every body.

## When physics starts (2026-11-11)

`Animation/RagdollTrigger.cs` (pure; test `Tests/RagdollTrigger`) decides the moment physics takes the
body over from the death animation. Ragdoll tab, "Ragdoll Trigger", "Ragdoll starts":

| Choice | Starts |
|---|---|
| After a fixed delay | the set number of seconds after death (0-30) |
| One frame before the death animation ends | on the last frame that can still be caught before it ends |
| At 90% of the death animation | when nine tenths of it have played |
| When the body falls fastest | when the pelvis drops at 1.2 m/s or more, or, in a slower fall, once it has lost 30% of its top speed |
| When the body has landed | when the pelvis has come down a quarter of its height and stopped falling |

The last four also start when the death animation runs out (a death without a fall), fall back on the
fixed delay when neither animation nor height can be read, and never start later than 30 seconds. The
animation followed is the one with the most weight on the body's skeleton, and only if it was seen from
near its start, so the looping stance the character died in is not mistaken for the death. Enemy
ragdolls keep their short fixed delay; the player and companions use the choice.
