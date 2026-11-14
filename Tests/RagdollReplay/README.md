# Ragdoll replay

Stage A2a of the refactor (`docs/REFACTOR_PLAN.md`, `docs/RAGDOLL_LIBRARY_ARCHITECTURE.md` §8). Replays a ragdoll
recording made in the game (`/combatsim ragdollrec start`, see `Tests/RagdollRecording`) through the plugin's real
`RagdollController`, offline, and checks that it produces the same output bit for bit.

## How the controller runs offline

The controller asks everything of the game through the host interfaces in
`CombatSimulator/Animation/Hosting/` (skeletons, world queries, characters, colliders, settings, clock, random,
scheduler, log). In the game those are thin adapters over the old calls; here `ReplayHost.cs` implements them
from the recording: every query takes the record its own call produced in the game, checks that it was asked the
same thing, and answers what the game answered. The controller is built with its internal host constructor, so
nothing is hooked and no game memory is touched; the Dalamud and FFXIVClientStructs assemblies are referenced
only so the plugin's types load.

The driver (`Program.cs`) restores the state the controller carried over from earlier activations (Activate
record), calls `Activate` with the recorded arguments, then walks the recording: each `RenderFrame` record runs
one render frame (the clock hands out the recorded dt), each call from outside (depth 0 `ApiCall`) is made again,
and an `End` record stops it. While it runs the controller records again, exactly as in the game; at the end the
two recordings are compared with `RagdollRecordingAnalysis.Compare`: every output record (body states after every
fixed step, the pose written back every frame, the body table, returned values, events) and every input record
(except the header, the end, notes and the frozen mesh cache, see below).

A recording replays only with the build it was recorded with. The gold recordings come from a PUBLIC_BUILD, so
the replay references a public build of the plugin.

## Run

```
dotnet build ../../CombatSimulator -c Release -p:DefineConstants=PUBLIC_BUILD -o %TEMP%\AdvancedRagdollReplay\plugin
dotnet build -c Release
"%APPDATA%\XIVLauncher\runtime\dotnet.exe" bin\Release\net10.0-windows\RagdollReplay.dll <file.rgdrec | folder> [--keep]
```

Run it on the .NET runtime the game uses (XIVLauncher's, above): results are bit-exact only on the same machine,
runtime and BepuPhysics version. `--keep` keeps the replayed recordings (they are deleted otherwise). Exit code 0
when every file replayed identically. Per file it prints `IDENTICAL (...)`, or the first output that differs
(tick, body, field), the first input that differs, the first question the replay could not answer as the game
did, recorded answers nobody asked for, and errors the controller logged.

## What stage A2a cannot replay

Model reading (mesh input) moves out of the controller in stage A2b. Until then the replay stands in for its
products where the recording has them: the hull fits (StructuralFits record) and the soft-tissue fits (read back
from the bone definitions they became). It refuses recordings that need more: mesh volumes (mesh-frame early out,
reduced meshes), mesh colliders of other characters, hair meshes. The frozen mesh cache (MeshCache record) is not
fed back; it only matters for mesh volumes, weapon-contact proxies and hair, none of which those recordings use,
and it is left out of the input comparison. Recordings made with the private module (DEV_EXPERIMENTAL) need a build
with it.
