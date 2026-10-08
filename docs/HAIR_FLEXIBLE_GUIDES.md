# Flexible ragdoll hair

The hair rigid-body/garment-joint implementation is replaced by a separate guide-tree solver. No hair bodies, constraints, or impulses are added to the body's BEPU simulation. The head pose and existing collision envelopes are inputs only.

## Implementation

- Capture the loaded hair partial skeleton at activation, using its posed bone positions and rotations. Resolve hair descendants without relying on sorted mod parent indices; invalid/cyclic branches are omitted. Native pose/skeleton identities are checked before readback.
- Guide roots attach to the scalp; descendants and virtual leaf tips integrate world gravity and initial head linear/angular velocity. Persistent compliant local-rest offsets preserve the hairstyle, transported through the deformed parent frames. Distal constraints are softer. Near-inextensible distance constraints maintain segment lengths.
- The scalar distance and vector rest-offset constraints use accumulated multipliers and compliance divided by timestep squared, following [XPBD](https://matthias-research.github.io/pages/publications/XPBD.pdf). Transported direction targets are recomputed during iteration; this is a practical guide model, not a full elastic-rod torsion solver or an exact constitutive hair model.
- Follow the accepted physical head after each existing fixed body tick. Render guide positions with the same fixed-step interpolation convention and align them to the rendered head. Reassert settled hair after normal partial-skeleton propagation. Independent guide activity prevents premature rest; head/contact motion wakes settled guides.
- Ground uses bounded rays into the existing BEPU static terrain, up to 16 per body tick, with local support-plane caches. Optional body contact uses head/torso/arm capsule envelopes. Only scalp rest overlap is allowed; other body envelopes separate freely moving guides. Contacts never apply forces to the body.
- Hard limits: 256 driven bones across partials, at most 512 guide points, 8 constraint iterations. Guide solve and pose readback reuse arrays, with no per-step managed allocations or MDL decoding. Teleport/discontinuous head rotation carries the tree without a velocity explosion.

## Controls

Hair Physics remains opt-in and now starts immediately for the local player during ordinary animation, independently of Enable Ragdoll. It continues during the death-animation delay, then transfers to the physical head during ragdoll. Flexibility defaults to 0.02; damping defaults to 2.2 per second. Existing mass (0.02) and contact radius (0.008 m) remain supported. Reset restores those values. The old swing/fading-servo/contact-switch properties remain only for serialized compatibility and no longer drive simulation. Body-envelope contact is always enabled with hair physics.

## Verification and limits

`dotnet run --project Tests/HairPhysics/HairPhysics.csproj -c Release` links the actual production solver. It covers attachment, finite positions, lengths, persistent curvature, inertial lag, rotating head, rest/wake, teleport, ground/body separation, invalid trees, maximum-size admission and allocations. Offline timing excludes native terrain queries and bone writes, and does not establish visual acceptance or live frame-time cost.

This replacement drives existing bones and does not modify native vertex skinning. Sparse hair bones still limit visible curvature; unsupported custom bone names are not automatically inferred from mesh weights. Body contact envelopes approximate actual mod surfaces. Ground support is sampled, so narrow ledges and fast motion need visual verification.

## Mesh-informed guides and continuous use

The loaded local-player hair slot is captured on Framework using its resolved UTF-8 resource filename, resource/data identities, enabled attributes and shape mask. A single background managed job decodes visible LOD0 samples; it never dereferences native memory. Dawntrail BYTE8 weights and indices are paired correctly. A bounded reservoir retains up to 256 samples per dominant weighted region, with 200,000 visible vertices and 64 MiB file admission caps. Active native shape replacements are currently omitted from mesh fitting rather than approximated. Race deformation outside the current identity-race hair model is not validated.

Leaf guides use the weighted point cloud transformed by reference inverse-bind and the captured pose, with principal-axis length and radial extent fitting. The current user model previously had only eight driven bones and guessed leaf lengths of 2.5–12 cm. Fitting can recover the visible long braid extent rather than stopping at that stub. Compliance now scales with segment length squared so long fitted guides can sag under gravity instead of retaining short-stub stiffness.

Body collision checks both guide endpoints and the full segments, with one-way distributed correction. This closes the case where a long guide crosses the torso while both endpoints are outside. Ground probes during ordinary animation are capped at eight per Framework callback and are skipped on a busy hair gate. The render hook issues no native game terrain queries. The existing BEPU terrain route remains for physical ragdolls.

Continuous use only owns the primary local-player controller. Native draw/object/pose/skeleton identity changes rebuild guides. The hair setting is visible independently of the body-ragdoll switch. Normal-animation timing uses wall-clock accumulation with a four-step catchup bound, so multiple render-hook invocations do not each simulate another full tick. Mesh-envelope loading is asynchronous; visual and native lifetime validation remains necessary.

Partial-pose attachment is explicitly mapped through the connected base head bone in both capture and readback. Ordinary animated hair partials need not already be in the body's model coordinate frame; treating them as though ragdoll propagation had already run can offset the hair. Disable/unload rebuilds the validated partial ModelPose from the game's untouched LocalPose.

The native log at 2026-10-08 21:55–21:56 confirms successful reload and managed binding of 77,650 visible vertices across nine weighted regions in the current mod. The long side guide fits to 0.408 m, versus the previous 0.12 m guessed-tip ceiling. These are native binding/metadata observations, not visual proof of correct collisions. Production solver tests pass 8,775 assertions, including mesh braid extent and a long segment crossing a body capsule with clear endpoints. The 512-point/12-envelope stress case measured approximately 1.0 ms per offline solver step after segment contacts were added; native queries and bone writes remain excluded.

## Remaining native mesh deformation work

Mesh-informed bone guides are **not** full mesh physics. A braid dominated by one bone still rotates as one skinned region even if its guide has the correct physical extent. Full deformation requires an actor-specific native draw/vertex-stream binding that preserves hair textures, alpha, lighting, depth, shadows and other actors sharing the resource. The inspected [Model layout](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/Model.cs), [ModelRenderer layout](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/ModelRenderer.cs) and [ModelResourceHandle layout](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/System/Resource/Handle/ModelResourceHandle.cs) expose callbacks and material/resource ownership, but do not establish a verified per-actor writable vertex-stream API. No shared native vertex buffer or resource handle has been overwritten.

The next backend must first prove per-actor stream substitution and reversible lifetime management, then embed coarse mesh-derived guides into the full vertex surface and recompute normals/tangents. Native material rendering must remain intact. This backend is not implemented or visually accepted in this checkpoint.

## Contact amplification and elastic response correction

The near-root fixture demonstrates a real solver defect: a 0.2 m guide moves its endpoint 281.606 mm in one tick when the capsule contact is close to a pinned scalp attachment. Capping contact-point displacement does not cap endpoint motion: the inverse-mass denominator amplifies movement approximately as 1/t. The actual endpoint corrections now share a scale that limits each to 4 mm per constraint iteration. The fixture's peak motion falls to 30.606 mm/tick, with peak length error 0.040 mm. The static scalp-overlap fixture has zero late tick-to-tick motion. These are production-solver numerical results, not proof of live visual smoothness.

Contact normals and moving-envelope velocities are retained in bounded arrays. Velocity readback rejects inward contact motion and outward velocity fabricated by position depenetration while preserving predicted intentional departure. Bending-rate damping is derived from the existing guide compliance and inverse masses; relative axial velocity is removed after length solving. This addresses the underdamped spring response that weak global air drag alone did not prevent. Production tests now pass 8,778 assertions, including near-root amplification, length preservation and static-contact stability; steady solver steps remain allocation-free.

This correction does not remove the fundamental card/region appearance caused by sparse native skinning bones. It must not be described as complete strand/mesh physics, and direct user visual verification remains outstanding.
