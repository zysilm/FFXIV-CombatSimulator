# Ragdoll replay

Stages A2a and A2b of the refactor (`docs/REFACTOR_PLAN.md`, `docs/RAGDOLL_LIBRARY_ARCHITECTURE.md` §8). Replays a
ragdoll recording made in the game (`/combatsim ragdollrec start`, see `Tests/RagdollRecording`) through the plugin's
real `RagdollController`, offline, and checks that it produces the same output bit for bit.

## How the controller runs offline

The controller (since stage A4 the library's `Ragdoll`, which the plugin's `RagdollController` derives from) asks
everything of the game through the internal host interfaces in the library's `Hosting/` folder (https://github.com/zysilm/AdvancedRagdoll) (named `IHost*` since stage A7;
the public API other games use is a facade over them) (skeletons, world queries, characters, colliders, the models (`IMeshSource`,
A2b), settings, clock, random, scheduler, log). In the game those are thin adapters over the old calls; here
`ReplayHost.cs` and `ReplayMeshSource.cs` implement them from the recording: every query takes the record its own
call produced in the game, checks that it was asked the same thing, and answers what the game answered. The
controller is built with its internal host constructor, so nothing is hooked and no game memory is touched; the
Dalamud and FFXIVClientStructs assemblies are referenced only so the plugin's types load.

The driver (`Program.cs`) restores the state the controller carried over from earlier activations (Activate record,
and the body surfaces of the MeshProduct "carried" record after it; for the local player also whether the live hair
rig was already running, which the recording shows: every hair rig build writes a HairMesh record, so the first live
hair frame has none exactly when the game found its rig running and current), calls `Activate` with the recorded arguments,
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
  compared, mesh paths included, except the label of another character's triangle soup (its name as the collider
  provider lists it: only used in log lines and not recorded anywhere the replay could hand it back from).
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

The first command builds the plugin, which restores the AdvancedRagdoll package from nuget.org (the version in
`CombatSimulator/CombatSimulator.csproj`, pinned by its `packages.lock.json`); the replay then references the
`CombatSimulator.dll` and `AdvancedRagdoll.dll` of that output folder (`PluginDir`). A new library version can change
the outputs, so after a library version bump re-run the replay over the recordings before committing the bump. The replay assembly is named `RagdollReplay` because the library and the plugin
grant internals to that name (`InternalsVisibleTo`).

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
characters; `--synthetic-hair`: the same plus hair physics resting on the body surface (the local player's
recordings): as in the game, the live hair rig is already running when the activation starts (one render frame of the
inactive controller builds it before the recording opens), the hair model "changes" once on the third frame so the
live hair rebuilds inside the recording, and the ragdoll's hair is built when physics starts; `--synthetic-meshes`: the same plus mesh volumes and
reduced surfaces (not repeatable, see above). It says nothing about the game's real models.

### Extension points (stage A3b)

```
... RagdollReplay.dll <file | folder> --extensions
```

Replays the first six recordings with physics steps four more times with dummy extensions registered
(`ExtensionCheck.cs`): passive step and contact extensions (before activation and after the simulation exists) leave
the replay bit-identical, the step extension runs once per fixed step before its Timestep, contacts arrive only inside
a Timestep of the context's simulation; a step extension that handles the step by calling
`ctx.StepPhysicsSimulation` reproduces every output except the "stepped externally" flag; one that handles it and
does nothing stops every Timestep; a contact extension that handles the manifold skips the controller's own
configuration; in every case an extension registered after the one that handled it is never called.

### Pose trajectories for the death-shot camera (read-only)

```
... RagdollReplay.dll <file | folder> --export-trajectories [outFolder]
```

Replays nothing. `TrajectoryExport.cs` reads each recording and writes one small `.dstraj` file (default
`%TEMP%DeathShots	raj`, about 32 MB for the 97 recordings, a few seconds): per render frame the time, whether
physics had started, and the world positions of 22 bones (hips, spine, neck, head, clavicles, arms, legs) from the
frame's SkeletonPose record before physics and its PoseOut record after, plus the centre of each bone's rigid body
from the frame's last BodyStates record; and the ground the recording knows (every ground ray hit, the exact ground
triangles where recorded, the water surface). It prints one line per recording (frames, seconds, poses present,
when physics started). `Tests/DeathShots` reads these files (`docs/DEATH_SHOTS.md`). They are derived from the
user's recordings and do not go into the repository.

### "What if" re-simulation (death guidance, phase P0)

```
... RagdollReplay.dll <file | folder> --what-if <recorded|limp|knee-buckle|hold-legs|hold-pose|slow-slump|auto>
        [--start-at 0|25|50|75|100] [--out folder] [--seconds 12] [--recorded-dt] [--only text]
        [--set Field=value ...] [--label name]
```

Not a replay: nothing is compared bit for bit (`WhatIf.cs`). From a recording it takes the character (skeleton, scale,
hull fits, soft-tissue fits, settings, what the controller carried into the activation) and the death animation (the
pose of every frame before physics started), starts physics at a chosen point of that animation (`--start-at`, percent
of the recorded frames before physics; 100 = the recording's own start frame) with a chosen guidance profile, and lets
the real controller run on for `--seconds` of physics at 1/60 s a frame (`--recorded-dt`: the recording's own frame
times, to the end of the recording). One recording therefore gives standing, mid-fall and lying starts.

- Profiles: `recorded` leaves the recording's guidance settings and pending collapse alone (for the comparison with
  the recording itself); `limp` switches every guidance off (the baseline); `knee-buckle`, `hold-legs`, `hold-pose`,
  `slow-slump` and `auto` are the library's death guidance profiles (`GuidedCollapseOptions.TensionProfile`, in
  AdvancedRagdoll from 0.1.0-beta.5; the plugin's setting is "Death guidance", `docs/RAGDOLL_DEATH_GUIDANCE.md`). `--set Field=value` (repeatable) sets one of the library's guidance numbers (`PoseHoldTuning`) for
  the run; `--label name` writes the run into `<out>/<name>/` instead of `<out>/<profile>/`, so a variant can stand
  beside the profiles in the tables (`Tests/DeathShots guidance --profiles limp,hold-legs,<name>`).
- Ground: a downward ray the recording cast from the same place is answered as the game answered it (at the recorded
  start that is every ray, so the ground mesh is the recorded one); any other from the recorded ground triangles or
  the height field of all recorded hits (the same reconstruction as `Tests/DeathShots/Trajectory.cs` `RecordedWorld`).
- Not restored: other characters' colliders, grabs and every other call from outside, hair, mesh volumes, weapon
  strikes. The random source is seeded by the recording's name. A recording that never started physics has no hull
  fits: its body is built from the default volumes.
- Output per run, in `<out>/<profile>/s<start>/` (default `%TEMP%\DeathGuidance\whatif`): `<recording>.dstraj` (the
  trajectory format of `--export-trajectories`, written from the recording the run itself made, which is then
  deleted) and `<recording>.wi`, "key=value" lines of what only the simulation knows (`PhysicsNumbers`: per joint
  group the turn of each body against its parent body since the hand-off pose, at 0.5 s, 1 s, 2 s, 4 s and the end; how
  far the lower legs moved and turned; kinetic-energy spikes; largest angular speed; when the simulation rested;
  joint-limit stress at the end; what the recording had that the run does not).
- The tables and contact sheets are made by `Tests/DeathShots` (`guidance`, `guidance-validate`, `guidance-sheets`,
  see `Guidance.cs` there).

How far it is from the recordings (2026-11-22, `recorded` at 100 % with `--recorded-dt` against the 85 recordings
with physics; every one of them had 14 to 40 other characters' colliders, which the run does not have): the pelvis is
within 3 cm on the first physics frame; after 1 s it is within 1 cm in 38 recordings (median error 2 cm), after 3 s in
33 (median 3 cm); at the end the median error is 9 cm (p90: 0.36 m after 1 s, 0.69 m after 3 s, 0.80 m at the end)
and the final pose class agrees in 67 of 85. It is a tool for comparing profiles under the same conditions, not a
predictor of one particular death.

## The saved "Death guidance" setting (`--config-migration`)

```
... RagdollReplay.dll --config-migration [saved CombatSimulator.json]
```

No recording (`ConfigMigrationCheck.cs`). It reads the plugin's real `Configuration` the way Dalamud does, runs
`Initialize` (nothing is saved) and checks: the retired `GuidedCollapse` object becomes `RagdollDeathGuidance` (on =
Knee buckle, off = Limp) and is purged; a saved choice survives; "Reset All to Defaults" gives Limp; each choice
reaches the library as its `RagdollTensionProfile` with `GuidedCollapseOptions.Enabled` off. With a file (only read)
it prints what that saved configuration becomes. It lives here because this is the test project that loads the built
plugin. Exit code 0 when every check passed.

## What is still not replayed

Recordings made with the private module (DEV_EXPERIMENTAL) need a build with it. The live hair's ground samples are
read on the framework tick, which a replay does not run, so the live hair before the ragdoll starts may differ (only
its effect on the hair poses of those frames, which are not compared; the ragdoll's hair is rebuilt from the recorded
pose). Calls from outside that the driver does not make yet (dropped weapons, external rigs, impulses, ...) make it
stop with "NOT REPLAYED".
