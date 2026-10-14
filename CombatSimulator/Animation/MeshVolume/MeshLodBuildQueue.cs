// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Serial background simplification of private managed snapshots. No native/game access.</summary>
public sealed class MeshLodBuildQueue : IDisposable
{
    public readonly record struct Completion(int BodyId, MeshCollisionLodResult? Lod, Exception? Error);
    private readonly ConcurrentQueue<Completion> completed = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object publicationGate = new();
    private Task tail = Task.CompletedTask;
    private bool disposed;
    // Enqueue/Dispose are owned by the simulation host; only completed is shared with its worker.
    public void Enqueue(int bodyId, BodyMeshGeometry geometry, float tolerance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshot = new BodyMeshGeometry((Vector3[])geometry.Vertices.Clone(),
            (int[])geometry.Indices.Clone(), geometry.SourceBindings == null ? null :
            (MeshSourceBinding[])geometry.SourceBindings.Clone());
        var token = cancellation.Token;
        tail = tail.ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            Completion result;
            try { result = new(bodyId, MeshCollisionLod.Build(snapshot, tolerance, targetFaceRatio: .15f), null); }
            catch (Exception ex) { result = new(bodyId, null, ex); }
            lock (publicationGate)
                if (!token.IsCancellationRequested) completed.Enqueue(result);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    public bool TryDequeue(out Completion result) => completed.TryDequeue(out result);
    /// <summary>Diagnostics/tests only: the host must never wait for background work.</summary>
    public Task WhenIdle => tail;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (publicationGate)
        {
            cancellation.Cancel();
            cancellation.Dispose();
            while (completed.TryDequeue(out _)) { }
        }
        // No join: a running managed-only build can finish without touching disposed native memory.
    }
}
