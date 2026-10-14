# Shared mesh physics workers

Run `dotnet run --project Tests/MeshDispatcher/MeshDispatcher.csproj -c Release`.

The test links `MeshPhysicsDispatcher` directly against the installed BEPU version. It verifies reference counting, first-lease worker count, idempotent disposal, worker recreation after the final release, concurrent callers stepping separate simulations, waiting for an in-flight native step before disposal, and rejecting nested acquire/run/release from both the caller and BEPU worker threads.

Use `MeshPhysicsDispatcher.Acquire(2)` once per interested controller, retain its lease, call `lease.Run(simulation, dt)` synchronously, and dispose the lease after that controller's last completed step. All leases share one pool; the first active lease chooses a total thread count clamped to 1–4 and available CPUs. BEPU includes the calling thread in this count.

The helper serializes stepping and its own lease lifetime. It does not synchronize host mutations of simulations, game state, bodies, shapes or filter collections. Those operations must remain outside every active physics step. Dispose simulations and their parent buffers only after synchronous stepping has completed.
