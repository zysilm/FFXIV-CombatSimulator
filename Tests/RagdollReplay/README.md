# Ragdoll replay

Stages A2a and A2b of the refactor (`docs/REFACTOR_PLAN.md`, `docs/RAGDOLL_LIBRARY_ARCHITECTURE.md` §8). Replays a
ragdoll recording made in the game (`/combatsim ragdollrec start`, see `Tests/RagdollRecording`) through the plugin's
real `RagdollController`, offline, and checks that it produces the same output bit for bit.

## How the controller runs offline

The controller asks everything of the game through the host interfaces in
`CombatSimulator/Animation/Hosting/` (skeletons, world queries, characters, colliders, the models (`IMeshSource`,
A2b), settings, clock, random, scheduler, log). In the game those are thin adapters over the old calls; here
`ReplayHost.cs` and `ReplayMeshSource.cs` implement them from the recording: every query takes the record its own
call produced in the game, checks that it was asked the same thing, and answers what the game answered. The
controller is built with its internal host constructor, so nothing is hooked and no game memory is touched; the
Dalamud and FFXIVClientStructs assemblies are referenced only so the plugin's types load.

The driver (`Program.cs`) restores the state the controller carried over from earlier activations (Activate record,
and the body surfaces of the MeshProduct "carried" record after it), calls `Activate` with the recorded arguments,
then walks the recording: each `RenderFrame` record runs one render frame (the clock hands out the recorded dt), each
call from outside (depth 0 `ApiCall`) is made again, and an `End` record stops it. While it runs the controller
records again, exactly as in the game; at the end the two recordings are compared with
`RagdollRecordingAnalysis.Compare`: every output record (body states after every fixed step, the pose written back
every frame, the body table, returned values, events) and every input record (except the header, the end, notes and
the MeshCache records, whose "reduced surface ready" flags depend on background timing).

A recording replays only with the build it was recorded with. The gold recordings come from a PUBLIC_BUILD, so
the replay references a public build of the plugin.

## Format versions

- **Version 3** (from A2b): what the mesh source handed over is recorded as finished products (MeshProduct records:
  body surfaces, the reduced surfaces taken from them, weapon-contact surfaces, soft-tissue fits, other characters'
  triangle soups, the hair rebuild flag, and the body surfaces carried into the activation), every wall-clock reading
  but the frame's dt is a Clock record, and the Activate record ends with `followWasActive`. Everything is replayed and
  compared, mesh paths included.
- **Version 2** (the 55 gold recordings): read as before. The replay answers the hull fits (StructuralFits), the
  soft-tissue fits (from the bone definitions they became) and nothing else of the models; it refuses files that used
  mesh volumes, reduced surfaces, mesh colliders or hair samples. The comparison leaves out what version 3 added (the
  replayed recording is version 3) and compares the Activate record on the values version 2 wrote.

## Known limit: mesh volumes are not repeatable

With mesh volumes on, the ragdoll steps its simulation on two worker threads (`MeshPhysicsDispatcher`), and BepuPhysics
does not promise the same result twice in that mode unless `Simulation.Deterministic` is set (it is not; setting it
changes the simulation and costs time, so it is a separate decision). The synthetic check below shows it: two replays
of the same recording part in the last bits after one or two fixed steps, with identical inputs. A recording made with
mesh volumes therefore replays its inputs exactly but not its outputs.

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

### Synthetic self-check of the mesh paths

```
... RagdollReplay.dll <file | folder> --synthetic-colliders | --synthetic-hair | --synthetic-meshes
```

Until a version 3 recording with mesh paths exists, this checks the recorder and the replay on them without the game:
a first run replays the recording with made-up models (`SyntheticMeshes.cs`: a box per bone as the body surface, the
box as its reduced surface, a box as every other character's mesh collider) and the mesh settings forced on; a second
run replays the recording the first one wrote and must reproduce it bit for bit, inputs included. The line
"synthetic mesh products" says which products were exercised. `--synthetic-colliders`: mesh colliders of other
characters; `--synthetic-hair`: the same plus hair physics resting on the body surface (live hair before the ragdoll
starts, and the ragdoll's hair; the local player's recordings); `--synthetic-meshes`: the same plus mesh volumes and
reduced surfaces (not repeatable, see above). It says nothing about the game's real models.

## What is still not replayed

Recordings made with the private module (DEV_EXPERIMENTAL) need a build with it. The live hair's ground samples are
read on the framework tick, which a replay does not run, so the live hair before the ragdoll starts may differ (only
its effect on the hair poses of those frames, which are not compared; the ragdoll's hair is rebuilt from the recorded
pose). Calls from outside that the driver does not make yet (dropped weapons, external rigs, impulses, ...) make it
stop with "NOT REPLAYED".
