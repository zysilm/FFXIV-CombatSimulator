# Visual only (enhanced)

Profile **5**, previously labelled Real attachment, follows the live source skeleton.
Original Visual only and saved profile indices are unchanged. Equipment override keys persist.

## Retained clothing

Upper clothing keeps its neckline, shoulders and sleeves on the live pose. Lower torso/hem
slide only toward the feet along the current torso axis. Gravity changes the rate, not the
direction. Travel is capped at min(Slip distance, 0.08 m), then regionally weighted.
Legacy multipliers and authored offsets cannot bypass this cap or unpin upper openings.

Trousers slide the waist along the mean live thigh direction, capped at 45% of the shorter
thigh and reduced when the thighs oppose. Thigh/knee offsets follow their own current segments,
weighted by 0.85/0.45 and capped at 65% of segment length. Cuffs stay fixed. Slip distance now
controls travel without the former 8 cm progress cap or millimetre output caps. Gravity controls
forward progress; inversion retains progress. Skirt construction retains its fixed waist.
Lower-slot authored offsets remain inactive. This is skeletal compression, not mesh folding or
surface collision: large travel can still stretch the crotch or clip the body.
## Retained skirt model

Main-skeleton `j_sk_*` chains are detected automatically, including with the Trousers template.
Each chain is a fixed-length strip with one outward hinge at its root. Reference-pose geometry
is carried by the current non-skirt parent; source animated skirt rotations are not inherited.
Gravity torque, a shape-retention spring and implicit damping control the hinge. Simulation uses
120 Hz steps with bounded backlog and angular velocity.

Live pelvis/leg capsules and a sampled horizontal floor plane constrain the angle. Contacts
search the allowed angular interval, including non-monotonic obstruction by both legs. Corrections
do not inject angular velocity. Contact hysteresis and a 0.25 mm collider-noise threshold reduce
chatter. If no sampled angle clears all contacts, the solver retains a bounded least-penetrating
sample and reports residual penetration as **Blocked panels** (over 1 mm).

Compatible partial skeletons mirror the driven main pose. Partial-only skirt descendants use
reference local transforms on their current parents as a stable kinematic fallback; they do not
receive separate hinge simulation. Missing reference data prevents procedural skirt construction.
The clone timeline remains stopped after synchronization. Source identity checks and the safe
preparation/commit detachment lifecycle are unchanged.

## Controls

Enable Physics drop: clothing, Advanced clothing settle and Auto cloth hold; choose
**Visual only (enhanced)**.

- Slip distance, sliding speed/resistance and motion damping control bounded torso/trouser travel.
- Skirt maximum opening limits outward hinge rotation (default 65 degrees).
- Skirt stiffness controls shape retention (default 24); skirt damping controls settling (1.5).
- Skirt contact thickness adds clearance to collision proxies (default 0.012 m).
- Material presets set sliding resistance and skirt stiffness/damping. Rigid fixes the hinges.
- Connection multipliers control torso or thigh/knee travel. Cuffs stay fixed for trousers; waist travel has a separate multiplier.

**Apply settings / restart slide** rebuilds bindings and applies edited settings. Otherwise edits
apply on the next detachment. Body following is automatic every frame. Old sideways/sway,
self/layer collision and opening-clearance settings remain serialized but are inactive here.

## Limits

This is restrained skeletal skirt physics, not mesh cloth simulation. Each chain turns as a rigid
strip; it cannot fold, bunch on the floor, collide with itself or model layered fabric. Contacts
sample three longitudinal lines at 0.025 m half-width, not garment triangles. A single-bone chain
uses a 0.15 m proxy tip because mesh hem geometry is unavailable. Capsule radii are approximations
unless supplied by active ragdoll geometry. Nonuniform scale and sloped/uneven floors are approximate.
Impossible poses can retain penetration; greater thickness or stiffness cannot guarantee clearance.
Partial-only chains and unsupported skeletons have reduced behavior. Native mesh rendering still
requires in-game verification.

## Verification

`dotnet run --project Tests/RetainedGarments -c Release` covers fourteen cases: gravity torque,
fixed seams/lengths, leg and floor contact, conflicting contacts, contact noise, moving-leg release,
multiple-leg obstruction, frame rates, repeated pose changes and retained trouser limits.
`Tests/AttachedGarmentMotion` includes upper-body regression checks; its older shared-offset cases
exercise retained helper APIs, not the new runtime skirt model. `Tests/GearDropLifecycle` checks
the separate safe detachment lifecycle.

Build: `dotnet build CombatSimulator/CombatSimulator.csproj -c Release --no-restore`.
Output: `CombatSimulator/bin/Release/CombatSimulator.dll`.

In-game check after reload: detach standing, fall onto each side, remain still, then stand again.
Check waist retention, stationary skirt jitter, thigh penetration and the panel/blocked counts.
Repeat after applying edited settings. Numerical tests do not certify native skeleton compatibility
or visible mesh clearance.
