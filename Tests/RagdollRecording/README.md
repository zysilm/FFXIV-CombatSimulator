# Ragdoll recording: reader and checker

Stage A0 of the refactor (`docs/REFACTOR_PLAN.md`). The plugin can record a ragdoll run in the game
(`/combatsim ragdollrec start|stop|status`); each ragdoll activation writes one `.rgdrec` file under the
plugin configuration directory, sub-folder `ragdoll-rec/`. This project reads those files offline. It links
the production format code from `AdvancedRagdoll/Recording/` and the plugin's exact BepuPhysics package.

```
dotnet run -c Release                          # self-checks (exit code 0 = all passed)
dotnet run -c Release -- <file.rgdrec>         # summary of one recording
dotnet run -c Release -- <a.rgdrec> <b.rgdrec> # first output record that differs, or "identical"
dotnet run -c Release -- dump <file.rgdrec> [first] [count]   # print records as text
```

Self-checks, all on synthetic data (no game needed):

- every value type, including NaN payloads, negative zero, denormals and infinities, survives writer -> reader
  bit for bit, and re-encoding the decoded records reproduces the original bytes exactly;
- a file cut in the middle of a record is readable up to its last complete record and is reported as truncated;
- a record whose body throws half-way is not written: a note naming it stands in its place, the records around it
  are whole, and the writer's lock is released;
- the per-file and per-session byte limits are enforced record by record: the file stops at a record boundary
  within the limit, still ends with its End record, and nothing follows the refused record;
- watching configuration properties for changes allocates nothing while nothing changes, and reports each change
  once, for the property that changed (the plugin does this once per render frame for every recorded ragdoll);
- the files are written in format version 3; the state a controller carries over between activations is found
  after the two lists of the Activate record (since version 2), and a version 1 file is reported as lacking it;
- a small real BepuPhysics simulation (two dynamic bodies and one kinematic body over a static box) is stepped
  and recorded twice: the two recordings must be identical;
- changing one bit of one body's state, or of one written-back bone, is reported with the tick, the body
  (handle and bone name) or bone, and the field;
- a changed input (a raycast answer) is reported as the first differing input.

The summary prints the header (format version, plugin build including the DEV_EXPERIMENTAL flag, BepuPhysics
version, runtime, SIMD width, time, which actor), the number of records of each type, render frames, physics
frames, fixed steps, bodies, world queries, calls from outside and the duration.

Two recordings are comparable only when they were made on the same machine, runtime and BepuPhysics version
(the header says which). The comparison looks at output records only: body states after every fixed step, the
pose written back every frame, the body table, values returned to callers, and events. Inputs are compared
separately so that a different input is not mistaken for a different result.

What this does not show: that the recorder captured everything in a real game session. That needs recordings
made in the game.
