# Ragdoll hull fit checks

Fits the torso and thigh collision hulls to real bodies offline and compares a body lying on the ground
with the built-in volumes. See `docs/RAGDOLL_FITTED_HULLS.md`.

```powershell
dotnet build CombatSimulator/CombatSimulator.csproj -c Release
dotnet run --project Tests/RagdollHullFit -c Release
```

Needs the game installed (pass its `sqpack` directory as the first argument if it is not in the default
place); it reads files through Lumina and never opens a game process. The model parser is the built
plugin's, called by reflection; the fit, the sampler and the hull shape are the production sources.
Modded bodies are included when the mod folder named in `Harness.cs` exists.

Options: `--only=<text>` runs the bodies whose label contains the text, `--trace` prints every body's
speed and height once a second while it settles.

The skeleton's reference pose is read from the `.sklb` (a Havok tagfile) by `Skeleton.cs` without Havok:
the bone names, the parent indices and the run of 48-byte transforms.
