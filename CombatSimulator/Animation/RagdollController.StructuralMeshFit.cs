// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // The torso and thighs collide with a hull fitted to the character's own surface: the body and
    // gear models it is actually drawn with, mods included, in their reference pose. The fit owns
    // collision geometry only; skeleton topology and joint anchors stay on the bone pivots.
    //
    // Reading a dense body takes tens of milliseconds, so the fit is started when the ragdoll is
    // armed and computed on a worker; the death animation's delay is ample time. One appearance is
    // fitted once and shared by every ragdoll that wears it.

    internal sealed record StructuralFitResult(Dictionary<string, StructuralMeshFitter.Fit> Fits, string Summary);

    private static readonly Dictionary<string, StructuralMeshFitter.Fit> NoStructuralFits = new(StringComparer.Ordinal);

    private Task<StructuralFitResult>? structuralFitTask;

    /// <summary>Starts fitting the target's collision hulls, if that has not been done for its appearance.</summary>
    private void PrewarmStructuralMeshFits()
    {
        structuralFitTask = null;
        try
        {
            var skel = skeletons.TryGetSkeleton(targetCharacterAddress);
            if (skel != null)
                structuralFitTask = StartStructuralMeshFit(skel);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Ragdoll mesh geometry: could not start the fit; built-in volumes will be used.");
        }
    }

    /// <summary>The fitted volumes for the target, or none: every body then keeps its built-in volume.</summary>
    private Dictionary<string, StructuralMeshFitter.Fit> ResolveStructuralMeshFits(ISkeletonPose skel)
    {
        try
        {
            var task = structuralFitTask ?? StartStructuralMeshFit(skel);
            structuralFitTask = null;
            if (task == null)
            {
                if (rec != null) recFitOutcome = 1;
                return NoStructuralFits;
            }

            // Normally long finished. With no activation delay the first death of an appearance
            // waits here once, briefly; past that the built-in volumes are better than a stall.
            if (!scheduler.TryWait(task, 150, out var result))
            {
                log.Info("Ragdoll mesh geometry: fit not ready yet; built-in volumes used for this ragdoll.");
                if (rec != null) recFitOutcome = 2;
                return NoStructuralFits;
            }
            if (rec != null) recFitOutcome = 3;
            log.Info($"Ragdoll mesh geometry: {result.Summary}");
            return result.Fits;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Ragdoll mesh geometry: fit failed; built-in volumes used.");
            if (rec != null) recFitOutcome = 4;
            return NoStructuralFits;
        }
    }

    /// <summary>Starts the hull fit: the mesh source reads the target's models now and fits them on a worker.</summary>
    private Task<StructuralFitResult>? StartStructuralMeshFit(ISkeletonPose skel)
        => meshes.StartStructuralFit(targetCharacterAddress, skel);

    private bool TryCreateStructuralHull(Vector3[] hullPoints, float scale, out ConvexHull hull, out Vector3 center,
        out Vector3[] corners, out Vector3[] edges)
    {
        hull = default;
        center = default;
        corners = Array.Empty<Vector3>();
        edges = Array.Empty<Vector3>();
        if (bufferPool == null)
            return false;
        try
        {
            return StructuralHullShape.TryCreate(hullPoints, scale, bufferPool, out hull, out center, out corners, out edges);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Ragdoll mesh geometry: hull construction failed; envelope used.");
            return false;
        }
    }

    internal static bool TryBuildReferenceModelTransforms(
        ISkeletonPose skel,
        out Matrix4x4[] referenceModel)
    {
        referenceModel = Array.Empty<Matrix4x4>();
        var reference = skel.ReferencePose;
        if (reference.IsEmpty)
            return false;

        var count = Math.Min(skel.BoneCount,
            Math.Min(skel.ParentCount, reference.Length));
        if (count <= 0)
            return false;

        referenceModel = new Matrix4x4[count];
        for (var i = 0; i < count; i++)
        {
            var local = QsToMatrix(reference[i]);
            var parent = skel.Parent(i);
            referenceModel[i] = parent >= 0 && parent < i
                ? local * referenceModel[parent]
                : local;
        }
        return true;
    }
}
