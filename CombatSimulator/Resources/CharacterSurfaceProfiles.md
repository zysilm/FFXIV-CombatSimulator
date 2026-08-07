# Character surface profiles

`CharacterSurfaceProfiles.json` is a lightweight approximation of the visible humanoid body.
It is deliberately independent of weapon hitboxes and does not load or skin game MDL files at
runtime.

Each profile selects by `ModelCharaId`, race, gender, tribe, and body type, in that order of
specificity. A role scale adjusts the existing, already draw-scale-aware ragdoll dimensions.
Optional per-bone `Rings` replace that estimate with measured elliptical cross-sections:

```json
"Bones": {
  "j_sebo_b": {
    "Role": "Spine",
    "Rings": [
      { "T": -0.12, "OffsetX": 0.00, "OffsetZ": 0.00, "RadiusX": 0.11, "RadiusZ": 0.08 },
      { "T":  0.00, "OffsetX": 0.00, "OffsetZ": 0.01, "RadiusX": 0.13, "RadiusZ": 0.09 },
      { "T":  0.12, "OffsetX": 0.00, "OffsetZ": 0.00, "RadiusX": 0.12, "RadiusZ": 0.08 }
    ]
  }
}
```

`T` is the position along the physics body's local Y axis, in metres. Radii and offsets are also
metres. Three to five rings are recommended. The runtime turns them into one eight-sided convex
hull per bone, so increasing the number of rings changes hull detail but never adds rigid bodies.

The four margins allow the same measured profile to serve physical contact, corpse traversal,
grab/finger conformance, and ground contact. Keep traversal slightly inside physical contact to
avoid invisible hovering. Validate edits with:

```powershell
./tools/validate-character-surface-profiles.ps1
```
