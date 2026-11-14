// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Recording;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private const float ExactGroundSafetyNetDrop = 0.25f; // how far the sampled grid sits below exact ground
    private const float ExactGroundVerticalReach = 8f;    // triangles this far above/below the patch height are taken
    private const int ExactGroundMaxTriangles = 20000;

    /// <summary>
    /// Add the game's collision triangles around (centerX, centerZ) as static ground. Both windings of
    /// every triangle are added, since the game's meshes do not promise which way a face points. False
    /// when the scene could not be read, had nothing there, or had too much.
    /// </summary>
    private bool TryAddExactGround(float centerX, float centerZ, float patchGroundY)
    {
        if (simulation == null || bufferPool == null) return false;

        exactGroundScratch.Clear();
        var min = new Vector3(centerX - terrainPatchRadius, patchGroundY - ExactGroundVerticalReach, centerZ - terrainPatchRadius);
        var max = new Vector3(centerX + terrainPatchRadius, patchGroundY + ExactGroundVerticalReach, centerZ + terrainPatchRadius);
        // A larger patch may hold proportionally more triangles, up to four times the base budget.
        var budget = (int)MathF.Min(ExactGroundMaxTriangles * 4f,
            ExactGroundMaxTriangles * (terrainPatchRadius / 6f) * (terrainPatchRadius / 6f));
        if (!world.TryCollectGroundTriangles(min, max, exactGroundScratch, budget) ||
            exactGroundScratch.Count < 3)
        {
            log.Info("RagdollController: exact ground unavailable for this patch; using the sampled grid");
            return false;
        }

        var count = exactGroundScratch.Count / 3;
        bufferPool.Take<Triangle>(count * 2, out var triangles);
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var a = exactGroundScratch[i * 3];
            var b = exactGroundScratch[i * 3 + 1];
            var c = exactGroundScratch[i * 3 + 2];
            if (!float.IsFinite(a.LengthSquared() + b.LengthSquared() + c.LengthSquared())) continue;
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-10f) continue; // degenerate
            triangles[written++] = new Triangle(a, b, c);
            triangles[written++] = new Triangle(a, c, b);
        }
        exactGroundScratch.Clear();
        if (written == 0)
        {
            bufferPool.Return(ref triangles);
            return false;
        }

        var mesh = new BepuPhysics.Collidables.Mesh(triangles.Slice(written), Vector3.One, bufferPool);
        simulation.Statics.Add(new StaticDescription(Vector3.Zero, Quaternion.Identity, simulation.Shapes.Add(mesh)));
        log.Info($"RagdollController: exact ground patch with {written / 2} triangles, radius {terrainPatchRadius:F1} m");
        return true;
    }

    /// <summary>
    /// Lay one terrain collision patch centered on (centerX, centerZ): raycast
    /// a grid of ground heights and add it to the simulation as a static mesh. Used
    /// for both the player's death spot and nearby enemies so a grabbed-and-released
    /// ragdoll always has ground beneath it. <paramref name="defaultGroundY"/> is the
    /// fallback height for the center raycast and any grid ray that misses.
    /// </summary>
    private void AddTerrainPatch(float centerX, float centerZ, float defaultGroundY)
    {
        if (simulation == null || bufferPool == null)
            return;

        var center = new Vector2(centerX, centerZ);
        foreach (var existing in terrainPatchCenters)
            if (Vector2.DistanceSquared(existing, center) < 1.0f)
                return;

        if (terrainPatchCenters.Count >= MaxTerrainPatches)
            return;

        int gridSize = (int)(terrainPatchRadius * 2 / terrainPatchStep) + 1;

        // Ground estimate at the patch center — used as the ray origin height and the
        // miss fallback, so a patch under an enemy on different ground still works.
        float patchGroundY = defaultGroundY;
        if (world.Raycast(RaycastSite.PatchCenter,
                new Vector3(centerX, defaultGroundY + TerrainRaycastStartYOffset, centerZ),
                new Vector3(0, -1, 0), TerrainRaycastDistance, out var centerHit))
            patchGroundY = centerHit.Point.Y;

        // Exact ground: the game's own collision triangles for this patch. The sampled grid below is then
        // only a safety net under it, lowered so it never holds a body up where the grid is coarser than
        // the real ground. Only the player's own body gets it: building the mesh costs 2-17 ms a patch,
        // every ragdoll has its own simulation, and a wave of dying enemies would pay that once each.
        var gridDrop = settings.RagdollExactGroundCollision && targetIsLocalPlayer &&
                       !(activeRagdollMeshVolumes && !activeRagdollIsGeneric) &&
                       TryAddExactGround(centerX, centerZ, patchGroundY)
            ? ExactGroundSafetyNetDrop
            : 0f;

        var heights = new float[gridSize, gridSize];
        var ox = centerX - terrainPatchRadius;
        var oz = centerZ - terrainPatchRadius;
        for (int gz = 0; gz < gridSize; gz++)
        for (int gx = 0; gx < gridSize; gx++)
        {
            var wx = ox + gx * terrainPatchStep;
            var wz = oz + gz * terrainPatchStep;
            if (world.Raycast(RaycastSite.PatchGrid,
                    new Vector3(wx, patchGroundY + TerrainRaycastStartYOffset, wz),
                    new Vector3(0, -1, 0), TerrainRaycastDistance, out var gridHit))
                heights[gx, gz] = gridHit.Point.Y - gridDrop;
            else
                heights[gx, gz] = patchGroundY - gridDrop;
        }

        var triCount = (gridSize - 1) * (gridSize - 1) * 2;
        bufferPool.Take<Triangle>(triCount, out var triangles);
        int ti = 0;
        for (int gz = 0; gz < gridSize - 1; gz++)
        for (int gx = 0; gx < gridSize - 1; gx++)
        {
            var x0 = ox + gx * terrainPatchStep;
            var x1 = x0 + terrainPatchStep;
            var z0 = oz + gz * terrainPatchStep;
            var z1 = z0 + terrainPatchStep;
            var v00 = new Vector3(x0, heights[gx, gz], z0);
            var v10 = new Vector3(x1, heights[gx + 1, gz], z0);
            var v01 = new Vector3(x0, heights[gx, gz + 1], z1);
            var v11 = new Vector3(x1, heights[gx + 1, gz + 1], z1);

            // CW winding from above → front face points UP for collision from above
            triangles[ti++] = new Triangle(v00, v10, v01);
            triangles[ti++] = new Triangle(v10, v11, v01);
        }

        if (activeRagdollMeshVolumes && !activeRagdollIsGeneric)
        {
            // Keep the exact sampled top surface, with solid ground below it.
            // Native convex/mesh queries bypass body/body contact sampling.
            TerrainCollisionSurface.Add(simulation, bufferPool, new ReadOnlySpan<Triangle>(triangles.Memory, triCount));
            bufferPool.Return(ref triangles);
        }
        else
        {
            var terrainMesh = new BepuPhysics.Collidables.Mesh(triangles, Vector3.One, bufferPool);
            var terrainIndex = simulation.Shapes.Add(terrainMesh);
            simulation.Statics.Add(new StaticDescription(
                Vector3.Zero, Quaternion.Identity, terrainIndex));
        }
        terrainPatchCenters.Add(center);
    }

    private void EnsureTerrainPatchCoverage(Vector3[] worldPositions, bool[] boneValid, int boneCount)
    {
        if (terrainPatchCenters.Count >= MaxTerrainPatches)
            return;

        Vector3 sample = default;
        var hasSample = false;
        if (nHaraIndex >= 0)
        {
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i] || ragdollBones[i].BoneIndex != nHaraIndex)
                    continue;

                sample = worldPositions[i];
                hasSample = true;
                break;
            }
        }

        if (!hasSample)
        {
            var sum = Vector3.Zero;
            var count = 0;
            for (int i = 0; i < boneCount; i++)
            {
                if (!boneValid[i])
                    continue;

                sum += worldPositions[i];
                count++;
            }

            if (count == 0)
                return;

            sample = sum / count;
        }

        var sample2 = new Vector2(sample.X, sample.Z);
        foreach (var center in terrainPatchCenters)
            if (Vector2.DistanceSquared(center, sample2) <= terrainPatchRefreshDistance * terrainPatchRefreshDistance)
                return;

        AddTerrainPatch(sample.X, sample.Z, realGroundY);
        if (settings.RagdollVerboseLog)
            log.Info($"RagdollController: added dynamic terrain patch at ({sample.X:F2}, {sample.Z:F2}); total={terrainPatchCenters.Count}");
    }
}
