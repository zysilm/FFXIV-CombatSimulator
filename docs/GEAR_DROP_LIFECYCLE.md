# Armor detachment disappearance investigation (2026-09-26)

## Evidence from the running game

The current Dalamud log records successful clone allocation and accepted Glamourer ApplyState
requests, followed by appearance synchronization timeouts at 12:06:26 and 12:06:33. The clones were
retired before their physics profiles ran. Earlier requests synchronized successfully. The old
caller had already hidden the source clothing and reported requested spawns as completed drops.

A read-only external memory probe at 12:14:24 captured the source's modded `c0201` top/legs and
clones 444/445. Those clones changed equipment from e0014/e6104 to the requested e0103/e0101, but
still rendered c1101/c1201 resources rather than the source's appearance. Thus this captured failure
is an actual customization/model mismatch, not just a false comparison of Penumbra cache prefixes.
The exact reason the third-party asynchronous redraw stopped completing was not established.

Separate code inspection found an unconditional lifetime deduplication by source/slot/paired side.
An old retained or unexpired piece blocked subsequent drops even after the player dressed again,
while KoStrip still hid the source. This is independently sufficient to cause disappearing clothing.

## Repair

- Human gear clones seed their own DrawData customization and equipment from the source's live
  Human before the second CharacterSetup pass. This avoids bootstrapping the unglamoured race and
  relying on an asynchronous race-change redraw. Their timelines run while preparing. Glamourer
  synchronization and exact rendered appearance verification remain enabled.
- Each requested piece has a Preparing / Ready / Committed / Cancelled operation. KoStrip retains
  the original outfit until the clone has synchronized, resolved required bones and loaded the kept
  model. Paired gloves/boots commit only when both pieces are ready. Simulation starts after commit.
- Allocation failure, synchronization failure, cancellation, an eight-second preparation timeout,
  reset or logout cancel uncommitted pieces without removing the source outfit. Changed source
  appearance also cancels a stale request. Failed requests release retry guards.
- Repeated clicks share a pending slot preparation. A new request after completion replaces the old
  clone for that source/slot/side, instead of being silently suppressed by its continued existence.
- Only committed slots are logged as physics drops. Appearance failure logs now include expected
  and actual signatures, IPC acceptance and stable-draw frame counts.

## Validation and limits

`dotnet run --project Tests/GearDropLifecycle/GearDropLifecycle.csproj -c Release`

Fourteen checks execute the production KoStripController and operation state machine with mocked
allocation/IPC and locally allocated native-layout character fixtures. They cover immediate
non-physics behavior, successful commit, allocation and synchronization failures, paired pieces,
reset, manual operation with KO disabled, changed outfits, timeout, logout, repeated clicks and
on-hit retry. No native game functions are invoked by these tests.

`Tests/GearDropProbe` is a separate Windows read-only diagnostic using ReadProcessMemory. It does
not inject code, write game memory or call game functions. `--watch` observes the player and local
actor slots for five minutes; hexadecimal actor-address arguments inspect specific actors once.
It requires the local Dalamud development FFXIVClientStructs assembly and a running game.

The Release build succeeds with the five existing CS0618 warnings. The changed native appearance
bootstrap and end-to-end rendered result still require a reload of the newly built plugin and an
in-game repeat test; the lifecycle tests do not emulate Glamourer/Penumbra or certify that path.
