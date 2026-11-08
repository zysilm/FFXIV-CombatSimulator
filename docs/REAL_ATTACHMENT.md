# Visual only (enhanced)

Cloth hold preset **5** (formerly labelled Real attachment). Body and Legs slots only.
Saved preset indices and per-equipment override keys are unchanged.

## Runtime

Source: `Animation/DismembermentController.RealAttachment.cs`, `Animation/Attachment/WholeGarmentSlide.cs`,
`Animation/Attachment/WaistRetentionMotion.cs`, settings in `Animation/Attachment/AttachmentSettings.cs`.

- The gear clone is never armed or released to physics. Every frame it copies the live source model
  pose and partial skeletons onto itself; native animation/body posing still applies.
- `WholeGarmentSlide` adds one root translation along the garment slip direction. Travel is capped at
  0.08 m (× scale) for tops and 65% of the shortest hip→knee length for lower garments. Direction
  changes and reductions of the distance setting converge continuously, without snapping.
- `WaistRetentionMotion` cancels the slide at the pelvis as the thighs separate (blends in between
  roughly 28° and 109°). Bone weights: `j_kosi` 1, thigh 0.8, knee 0.25, ankle and below 0;
  `j_sk_*` chains take 0.65 of their parent. Closed stances keep the full slide.
- The clone is retired when the source stops being the local player or its skeleton signature changes.

## Limits

No collision correction, cloth simulation, attachment joint or mesh-based waistband fit: the garment
always stays on the body and large travel can clip or stretch the crotch. Visual-only clones are never
auto-recycled; they remain until reset or zone change.

## Controls

Effects tab → **Armor Detachment** → open the control window. Enable **Physics drop: clothing**,
**Advanced clothing settle** and **Auto cloth hold**, then choose **Visual only (enhanced)** in
**Cloth hold preset**. Settings are per slot (Body / Legs) or per equipment override:

- **Material**: preset for sliding resistance and motion damping.
- **Slip distance** (0–3 m): requested travel; the anatomical caps above still apply.
- **Sliding speed limit**, **Sliding resistance**, **Motion damping**.

These four update live. **Apply settings / restart slide** restarts the slide from the worn pose.
Legacy fields (anchors, skirt opening/stiffness/thickness, body-fit) remain readable in saved JSON
but are ignored.

## Verification

`dotnet run --project Tests/RetainedGarments -c Release` covers waist retention (wide/closed stance,
no snapping across 30–300 FPS), whole-garment slide continuity and live distance reduction,
legacy template migration and three-metre slip settings. `Tests/GearDropLifecycle` covers the
separate preparation/commit detachment lifecycle.

In-game check after reload: detach standing, fall onto each side, remain still, then stand again;
check waist retention and thigh clipping. Numerical tests do not certify native skeleton
compatibility or visible mesh clearance.
