// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
using System;
using System.Threading;
using BepuPhysics;
using BepuUtilities;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>
/// Shared, reference-counted BEPU workers. All leases serialize synchronous steps;
/// host-owned body, shape and filter mutations must remain outside those steps.
/// </summary>
public static class MeshPhysicsDispatcher
{
    private static readonly object gate = new();
    private static ThreadDispatcher? dispatcher;
    private static int[] workerThreadIds = Array.Empty<int>();
    private static int leaseCount;
    private static bool running;
    private static long generation;

    public static int ClampWorkerCount(int requestedWorkers)
        => Math.Clamp(requestedWorkers, 1, Math.Max(1, Math.Min(4, Environment.ProcessorCount)));

    public static int ActiveLeaseCount => Volatile.Read(ref leaseCount);
    public static int ActiveWorkerCount => Volatile.Read(ref dispatcher)?.ThreadCount ?? 0;
    public static long Generation => Interlocked.Read(ref generation);

    /// <summary>The first lease fixes the shared worker count until the last lease is released.</summary>
    public static unsafe Lease Acquire(int requestedWorkers = 2)
    {
        RejectWorkerReentry();
        lock (gate)
        {
            if (running) throw new InvalidOperationException("Physics leases cannot be acquired from a running physics callback.");
            if (dispatcher == null)
            {
                dispatcher = new ThreadDispatcher(ClampWorkerCount(requestedWorkers));
                // Identify the actual dedicated threads before exposing the lease.
                // Rejecting their reentry before taking gate prevents a worker from
                // waiting on the host thread that is synchronously waiting on it.
                var ids = new int[dispatcher.ThreadCount];
                dispatcher.DispatchWorkers(index => ids[index] = Environment.CurrentManagedThreadId);
                ids[0] = 0; // worker zero is whichever host calls a future step
                Volatile.Write(ref workerThreadIds, ids);
                generation++;
            }
            leaseCount++;
            return new Lease();
        }
    }

    private static void RejectWorkerReentry()
    {
        int caller = Environment.CurrentManagedThreadId;
        foreach (var id in Volatile.Read(ref workerThreadIds))
            if (id == caller)
                throw new InvalidOperationException("Physics worker callbacks cannot acquire, run or release shared physics leases.");
    }

    public sealed class Lease : IDisposable
    {
        private bool disposed;
        internal Lease() { }
        public int WorkerCount
        {
            get { RejectWorkerReentry(); lock (gate) { ThrowIfDisposed(); return dispatcher!.ThreadCount; } }
        }

        /// <summary>Waits for another controller's step, then executes and joins this entire step.</summary>
        public void Run(BepuPhysics.Simulation simulation, float dt)
        {
            ArgumentNullException.ThrowIfNull(simulation);
            if (!(dt > 0f) || !float.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt));
            RejectWorkerReentry();
            lock (gate)
            {
                ThrowIfDisposed();
                // Monitor locks are reentrant; the explicit state prevents nested
                // dispatch on the same calling thread as well as dedicated workers.
                if (running) throw new InvalidOperationException("A shared physics step cannot reenter another physics step.");
                running = true;
                try { simulation.Timestep(dt, dispatcher!); }
                finally { running = false; }
            }
        }

        public void Dispose()
        {
            RejectWorkerReentry();
            lock (gate)
            {
                if (disposed) return;
                if (running) throw new InvalidOperationException("A physics lease cannot be released from a running physics callback.");
                disposed = true;
                leaseCount--;
                if (leaseCount != 0) return;
                // Run holds the same gate until every worker has joined its step.
                // The last release now owns idle threads and their private pools.
                var retiring = dispatcher!;
                dispatcher = null;
                retiring.Dispose();
                Volatile.Write(ref workerThreadIds, Array.Empty<int>());
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(Lease));
        }
    }
}
