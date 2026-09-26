# Real attachment

Real attachment is cloth hold profile **5**. Existing saved profile indices 0–4 are unchanged.
It retains Body/Legs garments on the player through persistent world-space clothing cages, rather
than advancing a single slide amount or releasing a garment as a free rigid body.

## Using it

In **Armor Detachment**, enable **Physics drop: clothing**, **Advanced clothing settle**, and
**Auto cloth hold**, then select **Real attachment**. Selecting it disables Tube model for new
pieces. Choose Body/Legs in the slots list and use **Detach Now**, or enable **Detach on KO**.
The floating window is resizable and scrollable so all of the controls remain reachable.

The new controls edit either Body/Legs defaults or an equipment override. Equipment keys contain
the rendered model and material paths captured before stripping, so Glamourer appearances can
have their own tuning. Both currently worn and already attached models are listed. Overrides
are saved in the plugin configuration, including connection offsets and slack multipliers.

Settings are copied when a cage is created. **Rebind attached clothing** applies current settings
to existing Real attachment pieces and returns them to the worn pose. **Undo edits** restores the
snapshot captured when the editor selected that slot/model. **Use slot defaults** removes the
selected model override. Ordinary profile changes affect the next detachment.

Parameters:

- Slip distance is allowed slack, not a required travel distance. Retained connections pull only
  when taut; the solver does not spring the entire outfit back to its original pose.
- Sliding speed limit bounds relative motion against the body's moving frame. Gravity and
  contact still determine the actual motion.
- Attachment firmness controls tether compliance. Friction and damping control settling.
- Opening clearance changes the collision cage's opening size, not the render mesh's dimensions.
- Uneven slack introduces deterministic circumferential differences, with no per-frame random forces.
- Individual neckline, shoulder, cuff, and waist connections support slack multipliers and local
  position offsets. Zero slack holds a connection in place. Final connections remain retained.

Top, Coat, Dress, Trousers, Skirt and Rigid construction templates provide different cage topology.
Coat/Dress/Skirt include available `j_sk_*` chains. Fabric, Silk, HeavyWeave, Leather and Armor
materials supply mass, stretch/bending compliance and friction/damping presets. Rigid construction
adds braces and enforces rigid material compliance.

## Implementation

- `Animation/Attachment/AttachmentSimulation.cs` is a game-independent fixed-step XPBD solver.
  Rings have six particles; structural and bending spans link the rings. Retained openings have
  compliant slack tethers. The solver steps at 120 Hz, interpolates moving body inputs, retains
  velocity and uses bounded catch-up after a hitch.
- `Animation/DismembermentController.RealAttachment.cs` creates cages from the live skeleton,
  supplies body/terrain contact, and drives the clone's model-space bone pose from the cage.
  Other bones inherit their nearest driven ancestor's transform. Opening orientation is smoothed
  in fixed steps, and degenerate openings preserve their previous orientation.
- Body contacts use active player ragdoll capsules when available and skeleton-derived capsules
  otherwise. Exterior opening edges also collide; cross-ring supports and chords inside the fitted
  body union are internal braces, not collision surfaces. Bind-time fitting rebases rest lengths,
  targets and render pivots without shifting the visible worn pose.
  Ground planes are sampled per ring and refreshed after movement or a short timeout; slope normals
  come from neighboring terrain samples. Contact friction distinguishes sticking and sliding.
- Optional self contact separates non-neighbor particles. Cross-garment contact consumes a common
  previous-frame snapshot, avoiding dependence on clone update order.
- A resting cage sleeps; body/collider movement and moving adjacent clothing wake it. Large target
  jumps rebase the cage instead of injecting teleport velocities. Invalid solver state has a bounded
  reset path and a visible recovery count.
- Attached clones never enter the armed free-drop path, so free-drop automatic expiration cannot
  remove them. Existing clone removal handles reset, zone transitions and disposal. A missing player
  or incompatible source skeleton retires the attached clone rather than releasing it to the floor.

**Show attachment cage** draws structural links and retained connections. Blue particles are free,
green particles have contact, and red particles indicate connection tension. Status shows active
cages, nodes, contacts, sleeping cages and numerical recoveries. Connection positions can be edited
while watching this overlay and applied with Rebind.

## Representation limits

This implementation drives existing equipment bones. It does not install a GPU vertex deformer,
extract a garment-specific simulation mesh, infer seams from mesh geometry, or add new equipment
bones. Fold detail and silhouettes therefore depend on the authored equipment skeleton. A rendered
skin/cloth mesh can penetrate even when the sampled cage does not; capsule and ring contact is an
approximation rather than render-triangle collision. Opening clearance cannot physically widen a
rendered collar. The ring sampling of self/layer contact is not triangle-level cloth self-collision.

Ground is a local sampled support plane, not a full environmental mesh collider; narrow steps and
overhangs require in-game inspection. Garment nodes respond to body motion but do not apply equal
and opposite forces back into the player ragdoll. The cage is bounded to 48 rings per garment.
These limitations should be considered when authoring model-specific profiles.

## Verification

Run the headless numerical checks without Dalamud or a running game:

```powershell
dotnet run --project Tests/AttachmentPhysics/AttachmentPhysics.csproj -c Release
```

Checks cover retained hanging, gravity after a side fall, local ground contact and pickup, pinned
openings, 30/60/144/300 FPS agreement, teleport recovery, settings/offset persistence and undo-copy
isolation, orientation, friction, sleep/wake, moving body contact, garment layering and a larger cage.
They validate numerical behavior, not game rendering or arbitrary equipment compatibility.

The repair adds continuous bone-frame transport (including bone twist), depth-weighted contact
normals, separate geometric depenetration, and a post-solve relative velocity limit. Opening rotation
uses all six nodes. Sampling another point on the same floor plane no longer wakes sleeping cloth.
Additional regressions cover these paths, fitted pivot/orientation transport and internal braces.
The speed limit bounds stored relative velocity; collision and tether position corrections can
still move a node farther than speed multiplied by elapsed time when constraints conflict.

Fitted cage geometry remains a proxy. Internal support links retain branch connectivity but do not
represent garment seams or a collision mesh. A fitted offset is preserved between proxy center and
rendered bone, so proxy clearance alone does not guarantee visible mesh clearance.

Build the plugin into the existing release directory:

```powershell
dotnet build CombatSimulator/CombatSimulator.csproj -c Release
```

Output: `CombatSimulator/bin/Release/CombatSimulator.dll`.

In-game acceptance should cover upright detachment, left/right falls, bent legs, rolling, dragging,
lifting off the floor, slopes/steps, reset, zoning, and redrawing the character. Check both ordinary
equipment and Glamourer replacements. These visual/lifecycle scenarios need a running game; the
headless checks do not claim to have exercised them.
