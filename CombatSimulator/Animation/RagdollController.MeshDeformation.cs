// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
using System;
using BepuPhysics.Collidables;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private long lastMeshBudgetReport, lastMeshLodInstall;

    // Frozen mesh mode never skins vertices or refits a native mesh during simulation.
    // Only newly completed cached LODs can replace a shape, once, outside worker steps.
    private void ApplyCachedMeshLod()
    {
        if (simulation == null || bufferPool == null || frozenMeshCache == null || !config.RagdollMeshCollisionLod) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastMeshLodInstall != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastMeshLodInstall, now).TotalMilliseconds < 16.7) return;
        for (int index = 0; index < ragdollBones.Count; index++)
        {
            var bone = ragdollBones[index];
            if (bone.MeshGeometry is not { } full || bone.MeshLod != null ||
                !frozenMeshCache.TryCreateLod(bone.Name, full, out var lod)) continue;
            var body = simulation.Bodies.GetBodyReference(bone.BodyHandle);
            var replacement = CreateBodyTriangleMesh(lod.Geometry);
            TypedIndex replacementIndex;
            try { replacementIndex = simulation.Shapes.Add(replacement); }
            catch { replacement.Dispose(bufferPool); throw; }
            var previous = body.Collidable.Shape;
            body.SetShape(replacementIndex);
            simulation.Shapes.RemoveAndDispose(previous, bufferPool);
            bone.MeshLod = lod;
            ragdollBones[index] = bone;
            body.Awake = true; body.UpdateBounds();
            lastMeshLodInstall = now;
            return;
        }
    }

    private void ReportMeshPhysicsBudget(long start, int steps)
    {
        if (start == 0 || steps == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start, now).TotalMilliseconds;
        if (milliseconds < FixedTimestep * 1000 ||
            (lastMeshBudgetReport != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastMeshBudgetReport, now).TotalSeconds < 5)) return;
        lastMeshBudgetReport = now;
        int meshes = 0, faces = 0, collisionFaces = 0;
        foreach (var bone in ragdollBones)
            if (bone.MeshGeometry is { } geometry)
            {
                meshes++; faces += geometry.Indices.Length / 3;
                collisionFaces += bone.MeshLod?.CollisionFaceCount ?? geometry.Indices.Length / 3;
            }
        double ticksToMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
        double collisionMs = meshCollisionTicks * ticksToMs, solveMs = meshSolveTicks * ticksToMs;
        log.Warning($"Ragdoll mesh physics exceeded render budget: {milliseconds:F1} ms, {steps} fixed ticks, " +
                    $"{meshes}/{ragdollBones.Count} mesh/total bodies, {collisionFaces}/{faces} collision/full surface faces; catch-up limited. " +
                    $"Workers={meshDispatcherLease?.WorkerCount ?? 1}, solver={simulation?.Solver.VelocityIterationCount}x{simulation?.Solver.SubstepCount}, " +
                    $"frozen cache; skin/refit=0, collision={collisionMs:F1}, solve={solveMs:F1}, other={Math.Max(0, milliseconds - collisionMs - solveMs):F1} ms, " +
                    $"main native pool={(bufferPool?.GetTotalAllocatedByteCount() ?? 0) / 1048576d:F1} MiB.");
    }
}
