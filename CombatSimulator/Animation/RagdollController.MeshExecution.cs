// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using CombatSimulator.Animation.MeshVolume;
using System.Diagnostics;
using BepuPhysics;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private MeshPhysicsDispatcher.Lease? meshDispatcherLease;
    private long meshStageStart, meshCollisionTicks, meshSolveTicks;

    private void InitializeMeshTiming()
    {
        if (simulation?.Timestepper is not DefaultTimestepper timestepper) return;
        timestepper.BeforeCollisionDetection += (_, _) => meshStageStart = Stopwatch.GetTimestamp();
        timestepper.CollisionsDetected += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            meshCollisionTicks += now - meshStageStart;
            meshStageStart = now;
        };
        timestepper.ConstraintsSolved += (_, _) => meshSolveTicks += Stopwatch.GetTimestamp() - meshStageStart;
    }

    /// <summary>Workers finish before skin refit, native bone writeback or game access.</summary>
    private void StepPhysicsSimulation(float timestep)
    {
        if (simulation == null) return;
        SyncWeaponContactProxies();
        if (!config.RagdollMeshParallelPhysics || !activeRagdollMeshVolumes || activeRagdollIsGeneric)
        {
            ReleaseMeshDispatcher();
            simulation.Timestep(timestep);
            return;
        }
        meshDispatcherLease ??= MeshPhysicsDispatcher.Acquire(2);
        meshDispatcherLease.Run(simulation, timestep);
    }

    private void ReleaseMeshDispatcher()
    {
        meshDispatcherLease?.Dispose();
        meshDispatcherLease = null;
    }
}
