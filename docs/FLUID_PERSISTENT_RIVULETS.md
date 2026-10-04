# Persistent attached fluid traces

## Problem

Previously a skin ribbon borrowed the live cap's volume and path. Converting that cap to a filament, shedding it, coalescing it, or reusing its slot erased the ribbon; the separate thin wet coat did not preserve its elongated shape. Broad film and transient geometry could also consume the shared vertex budget before ribbons were submitted.

## Ownership and rendering

After a path has two verified material rows, 10% of its current cap inventory transfers once into an independent retained owner. Subsequent updates to that segment copy only anchors, never duplicate inventory. Completing the 16-row segment starts a new connected segment instead of rolling away its tail. Stored samples follow the current deformed skin.

Retained ribbons have no lifetime timer and survive cap shedding, filament conversion, source removal and supply stop. They render before broad films, falling drops and ground pools. Their free surface includes the local receiving-film cell thickness so an existing wet coat does not simply replace the narrow residual shape. This does not change refraction shaders or material settings.

There are 32 retained owners. Full capacity recycles the oldest owner and explicitly retires its volume. Explicit effect clearing and surface topology replacement also retire stored material. Unavailable posed geometry suppresses that frame's draw without deleting ownership; this remains a native-budget limitation, not a guarantee that every trace is visible in every frame. No stale world-space mesh is drawn as a substitute.

## Continuous supply

Moving an emitter to its fifth disconnected wet patch previously exhausted its four receiver banks and silently suspended supply. Source binding now reuses its own least-recently-used bank, preferring banks without live references. Old cap traces retain independent ownership, remaining cap liquid becomes free gravity drops, and the old film inventory retires explicitly. A thread keeps its material attachment and simulated mass, but loses the stale cap feeding reference. Other sources and ordinary body receiver transfer rules remain separate.

Unlimited admitted supply does not imply unlimited particles or geometry. Existing fixed pools and query budgets remain, and the new retained pool is also fixed. No additional GUI capacity options or total emission quota are added.

## Verification

Directed production-code offline tests cover retained ownership, cap/thread transitions, explicit clearing, topology changes, pool reuse, source-bank exhaustion, reserved-thread references, and continuous supply across six disconnected patches for 2,400 frames. The existing contact checks and 6,000-frame conservation/source-isolation recorder are rerun. These are intentional persistence and recycling changes, not exact behavioral parity with the earlier checkpoint. Native rendering, mod appearance and live performance still require visual assessment; no game commands are executed by these tests.
