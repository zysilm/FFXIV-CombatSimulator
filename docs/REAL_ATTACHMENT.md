# Half off, hanging on the body

Effects tab → **Armor Detachment** → **Body / Legs** → **Half off, hanging on the body** (stored as
cloth hold preset 5; formerly "Stay on the body" / "Visual only (enhanced)"). Body and Legs only.

## Runtime

Source: `Animation/Attachment/GarmentTether.cs` and `ApplyGarmentTether` / `DriveGarmentTetherPins`
in `Animation/DismembermentController.cs`.

The garment is the same limp box chain as **Drop when the body settles** (see `GarmentFlatten`), with
three differences:

- **Pinned segments stay on the body.** Chosen by **Top hangs from** (Waist by default; Chest, Collar,
  Left or Right sleeve) and **Lower hangs from** (Both shins by default; either shin, either leg, Waist). Each frame those boxes are re-placed on the live body bone
  with the velocity that carries them there (bounded, so a teleport cannot launch them).
- **The rest is pulled down.** When the rig is created every free segment gets a velocity toward the
  feet plus a lift off the body, on whichever side of the body faces up, scaled by its distance from the
  pinned part and by **Pull** (None, Gentle, Normal, Strong). The joints open fully (2.9 rad) with no worn-shape hold, so the free part folds back over
  the pinned part: chest, shoulders and sleeves over the hips; waist and thighs past the knees.
- **No pre-slide, no handoff drag, no auto-recycle.** The pinned part must not jump at handoff, the
  free part is being pulled away from the body, and a garment hanging on the body is not a dropped piece.

Free segments start without corpse contacts and gain them once they have left the body and collapsed
flat, exactly as a dropped top does. If the rig has nothing to pin, the piece behaves as a plain drop.

## Limits

Segments still around a body segment keep their worn volume and do not collide with the corpse, so a
free part that comes to rest around the pelvis or the shins stays tubular and can overlap the body.
The pull speeds (`PullSpeed`, `LiftSpeed`) are untuned in game.

## Previous implementation

The whole-garment slide (`DismembermentController.RealAttachment.cs`, `WholeGarmentSlide`,
`WaistRetentionMotion`, config `KoStripTopSlideDistance` / `KoStripLowerSlideDistance`) is still in
the source but not selected (`StayOnBodyUsesTether`). Remove it once the tethered version is accepted.

## Verification

`dotnet run --project Tests/RetainedGarments -c Release` covers the pin set, the pinned pose round
trip, the lift direction for lying and upright bodies, pull bounds, and follow velocities, together
with the collapse (`GarmentFlatten`) and legacy slide cases. `Tests/GearDropLifecycle` covers the
preparation/commit detachment lifecycle. None of this certifies the in-game result.
