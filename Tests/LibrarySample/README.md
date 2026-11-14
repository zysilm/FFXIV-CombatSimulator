# Library sample

Stage A7 of the refactor (`docs/REFACTOR_PLAN.md`, `docs/RAGDOLL_LIBRARY_ARCHITECTURE.md` §2.4 and §3.1): the
AdvancedRagdoll library used the way another .NET game would use it. The project references the library project and
nothing else (no plugin, no game, and no `InternalsVisibleTo`), so it compiles against the public API only.

It builds a hand-made 22-bone humanoid (`ArraySkeleton`, about 1.75 m tall, leaning slightly forward), a flat ground
plane at y = 0 (`DelegateWorldQuery.FlatGround`), and a ragdoll on `SkeletonProfile.Auto()`; activates it with a
0.05 s fixed delay and steps it for ten seconds at 60 Hz, writing the pose back every step. Checks:

- it waits for the trigger first, then physics starts; `OnPhysicsStarted` once, `OnPhysicsFrame` every physics step;
- `WritePose` writes nothing before physics starts; every written pose is finite;
- it falls (pelvis from about 1 m to below 0.35 m, head below 0.45 m) and stays on the ground (no bone below -5 cm);
- it settles (no bone moves more than 1 cm in the last second) and reaches the phase `Resting`;
- `Deactivate` gives the body back (`OnDeactivated` once, phase `Inactive`);
- two runs write bit-identical poses;
- the character vanishes while physics runs (`Exists` turns false): the ragdoll stops by itself without
  `OnDeactivated`; activated again on the same `Ragdoll`, physics starts again and `OnPhysicsStarted` fires again.

## Run

```
dotnet run -c Release
```

Exit code 0 when every check passes.
