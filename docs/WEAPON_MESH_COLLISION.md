# Optional weapon mesh collision

Enable **Ragdoll > Weapon Drop > Mesh-based weapon collision**. Default off.
The switch applies when a new weapon simulation is created; existing bodies retain their
captured shape. The enabled option displays the same yellow stuttering warning as extended
terrain detection. Loading/parsing/skinning/fitting happens at release, not every frame.
Compound contacts and extra physics steps also increase ongoing CPU cost.

The fitter reads the currently loaded weapon resource path, including local mod MDLs,
skins indexed triangles to the current weapon pose and applies skeleton scale. It clips
triangles into twelve longitudinal by two transverse cells and fits a convex hull to each occupied
cell. Clipping triangles rather than binning vertices preserves long low-poly barrels.
There are at most 24 hulls; near-planar point sets get 3 mm minimum thickness only on their
thinnest axis. Sloped contact faces are preserved instead of becoming artificial flat supports.
Actual hull-volume-weighted child masses determine centre of mass and compound inertia. Rendering
subtracts that same centre offset, preserving the release pose while allowing rotation.

Missing/unsupported data, invalid skin mappings, more than 120,000 triangles or local
files larger than 64 MiB (or a cell exceeding 4096 unique clipped points) fall back to the previous simple collision. Fit counts/time and
fallback are logged. No truncated mesh is silently accepted. Child shapes are disposed
with their parent. Ordinary mesh drops use two collision steps and four solver substeps
per existing 60 Hz simulation tick; simple-only worlds retain the original settings.

This is a bounded compound approximation, not triangle-exact dynamic collision. Some
concavities remain filled. All selected LOD geometry is included, so hidden model variants
can still enlarge the fit. The body's own collision profile can also leave visible gaps.
The fitted pose stays frozen for collision; later native weapon animation can differ.

Synthetic validation covers sparse barrel coverage, empty-space removal, release-origin
preservation, mass, high-speed angled impacts, settling and recursive disposal. A narrow
support test reproduces a >20 cm barrel gap with the resource box and reduces it below
1.5 cm with the compound. A tapered-profile comparison shows the old cell boxes resting
on a false flat base while the corresponding hulls naturally tip sideways under the same
damping. Runtime logs confirm the reported gun reached mesh fitting (984 triangles, 23
parts); raw-parser fallback was not simple-collider fallback. Final hull rendering/contact
still requires in-game verification. Logs now include scale, centre and fitted full extent.
