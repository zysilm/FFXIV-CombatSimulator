# Real attachment simulation diagnostics — 2026-09-26

## Scope

**Status after repair (2026-09-26):** The sections below record the original diagnosis. The current
executable links the repaired production source, uses `AttachmentFrame` and calls `FitToBody` before
floor sampling, matching runtime initialization order. `LegacyFrame` deliberately preserves the old
projection formula only for comparison. Running the diagnostic does not modify the plugin DLL.

Current results: all six Top/Skirt posture baselines have zero initial body penetrations greater than
1 mm, zero initially penetrating collision edges, no numerical recoveries, and maximum stored speed
at or below the configured 0.6 m/s. These conditions and late center jitter below 1 mm/frame are now
assertions with a nonzero exit code on failure. All 30 scenarios completed; all six baselines slept.
The suspended side-skirt baseline has 0.005 mm/frame late center jitter. Coupled ground fixtures
settled with and without layer contacts. Ground contact can still initially correct about 1 cm,
because floor samples are supplied after body fitting.

The transported-frame oscillation probe measures 0.01 cm maximum target jump and 0.07 degrees maximum
render rotation step. The legacy frame with the repaired solver still jumps 12.02 cm and 71.10 degrees:
velocity limiting alone does not repair discontinuous input. The opposite-contact probe now returns
a depth-weighted X component of -0.0999. The 21 physics regressions also pass, including preserved
visible bind position/orientation after asymmetric fitting and rigid transport.

These are synthetic results, not a live-game visual certification. The cage retains internal
cross-ring braces; it does not reconstruct real garment surface topology. Collision-position
corrections are not bounded by the stored velocity limit. Actual equipment deformation, model scale,
terrain sampling and animation interactions still require in-game inspection.

Run `dotnet run --project Tests/AttachmentDiagnostics/AttachmentDiagnostics.csproj -c Release`.
The diagnostic executable links the unchanged production settings and solver source. Fixtures
approximate a human skeleton and mirror the production Top/Skirt construction, radii, connections
and fallback body capsules. They are not a capture of the user's character or equipment.

Thirty single-garment cases cover two templates, three postures (standing, suspended on the side,
and lying near the ground), and five contact ablations. Each runs ten simulated seconds at 60 FPS.
Two additional runs compare coupled clothing with/without layer contact. Reflection changes only
diagnostic instances to skip edge contact while preserving the original constraint compliance.
No production source or plugin DLL is modified by these experiments.

## Confirmed reference-frame discontinuity

The production `ResolveAttachmentFrame` projects the bone's X axis onto the plane perpendicular
to the bone-to-child direction. Near parallel axes, the projected vector approaches zero and changes
sign. Its fallback does not preserve continuity with the preceding frame.

An input direction changing from -0.012 to +0.012 radians (1.375 degrees total) produces a **180-degree
frame change**. This is an actual orientation change, not just quaternion sign ambiguity.

A six-node opening with radius 0.085 m, driven by a 1 Hz direction oscillation of amplitude 0.012 rad:

| Metric | Current frame construction | Continuous-frame control input |
|---|---:|---:|
| Maximum node target jump per frame | 12.02 cm | about 0.01 cm |
| Maximum particle speed | 9.68 m/s | 0.67 m/s |
| Maximum rendered rotation change per frame | 77.72 degrees | 3.79 degrees |

Both runs use the same solver. The continuous input is an experimental control, not a deployed fix.
The displayed rotation uses the same simulated-frame / source-frame delta as the production bone
drive. Therefore smooth physical input can generate a violent visual jump before any mesh-level
cloth limitation is relevant.

## Initial geometry conflicts

The synthetic standing Top has 78 nodes. At creation, 40 nodes and 120 structural edges penetrate
at least one body capsule by more than 1 mm. One collision projection pass, with **no elapsed
simulation time**, displaces a ring center by up to 6.26 cm. Skirt: 22 nodes, 74 edges, 6.95 cm.

Skipping contact on cross-ring bridge edges reduces this immediate displacement to 3.44 cm for
Top and 2.30 cm for Skirt, but does not remove point/ring penetration. Disabling every edge contact
reduces it to 2.39 / 1.92 cm. These ablations diagnose conflicting geometry; they are not a proposal
to ship clothing with collision disabled.

The template connects entire rings across shoulder branches and from the waist to skirt columns.
Such structural links are not necessarily garment surface edges. Treating all of them as collision
edges can push internal support links out of the torso, deforming the clothing cage itself.
Clavicle-centered rings can also start inside neighboring torso volumes.

## Confirmed contact-normal accumulation error

`Contact` adds `normal * depth` and then immediately normalizes the running total. This discards
the accumulated weight after every contact and makes subsequent contacts incomparable in magnitude.

Calling the actual private method with a +X correction of **0.1 mm**, followed by a -X correction
of **100 mm**, leaves the stored normal pointing **+X**. A depth-weighted total points -X.
Friction therefore may use the wrong direction in multi-contact cases.

Collision/constraint corrections are subsequently converted directly to velocity using
`(Position - BeforeStep) / Step`. The speed limit is applied before those corrections, so it does
not bound correction-generated velocities. This amplifies initial overlaps and discontinuous targets.

## What did not reproduce

- Baseline static fixtures eventually slept, including the coupled Top/Skirt ground case. Coupling
  alone did not reproduce sustained static jitter in these fixtures.
- A zero-deformation render coordinate round trip over varied root rotations had a maximum position
  error of 3.990e-6 m at unit scale. This does not validate game-specific scale or live skeleton data.
- The original fifteen numerical checks still pass. They lack the production frame-construction
  discontinuity and branched template/contact fixtures above, so their success was insufficient
  evidence of in-game visual correctness.

## Repair assessment

The persistent-attachment goal is salvageable, but tuning speed or damping alone is insufficient.

1. Replace per-frame reference-axis projection with a continuous transported frame and regression
   tests for near-parallel axes. Preserve a consistent relationship to the authored bone rotation.
2. Rebuild shoulder/waist/skirt connectivity: distinguish garment surface edges from internal support
   constraints, validate starting clearance, and avoid rings centered inside neighboring body volumes.
3. Correct normal accumulation and manifold friction; avoid treating depenetration as an unrestricted
   launch impulse. Validate motion and residual error rather than only finite values/recovery counts.
4. Re-run the branched/ground/moving-frame scenarios, then inspect the actual character in game.
   Simulation findings establish bugs but cannot certify that every observed visual symptom is fixed.
