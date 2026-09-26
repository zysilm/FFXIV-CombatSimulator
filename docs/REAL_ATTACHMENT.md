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
forward progress; inversion retains progress.
Lower-slot authored offsets remain inactive. This is skeletal compression, not mesh folding or
surface collision: large travel can still stretch the crotch or clip the body.

All Legs-slot equipment now uses the same Trousers movement, including skirts. The separate
whole-skirt translation has been removed. Skirt bones are still detected and simulated on top of
the bounded waist/leg movement. There is no garment-construction dropdown.

Legacy Skirt (4) and Rigid (5) remain readable in saved JSON; lower-slot templates all resolve to
Trousers. Legacy upper Rigid resolves to Top. Equipment override keys and indices remain stable.
Descriptions appear in help-marker tooltips, with only controls, equipment identity and status
shown as regular text. Detailed diagnostics remain behind the debug checkbox.
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

- Slip distance accepts 0–3 m for both Body and Legs defaults and equipment overrides. Upper-body
  and lower-body travel have separate anatomical limits; increasing the slider cannot
  bypass those limits.
- Sliding speed/resistance and motion damping control bounded torso/trouser travel.
- Skirt maximum opening limits outward hinge rotation (default 65 degrees).
- Skirt damping controls settling (1.5). Stiffness controls shape retention (default 24), under
  the collapsed Advanced skirt settings with contact thickness.
- Skirt contact thickness adds clearance to collision proxies (default 0.012 m).
- Material presets set sliding resistance and skirt stiffness/damping.
- Connection multipliers control torso or thigh/knee travel. Cuffs stay fixed for trousers; waist travel has a separate multiplier.

**Apply settings / restart slide** rebuilds bindings and applies edited settings. Otherwise edits
apply on the next detachment, except slide distance/speed/resistance/damping and skirt
opening/damping/stiffness/thickness which update live. Connection changes require Apply or a new detachment.
Detailed contact counts and actual maximum angle appear only with the debug offset overlay enabled.
The experimental body-shape adaptation and its extra hip proxy/root expansion have been removed;
old saved adaptation fields are ignored. Original capsule contacts remain.
Body following is automatic every frame. Old sideways/sway,
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

`dotnet run --project Tests/RetainedGarments -c Release` covers seventeen cases: legacy Skirt/Rigid migration, three-metre settings,
removed-setting migration, live angle limits, gravity torque,
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
