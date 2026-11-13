// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Animation;

/// <summary>
/// Reads the game's own collision scene: the triangles the game itself tests against, rather than a
/// grid of heights sampled from it. Read-only. The scene is walked under the same shared lock the
/// game's raycasts take, because colliders stream in and out on another thread.
/// </summary>
public static unsafe class WorldCollisionMesh
{
    private const int MaxNodeDepth = 64;

    [DllImport("kernel32.dll")]
    private static extern void AcquireSRWLockShared(nint* srwLock);

    [DllImport("kernel32.dll")]
    private static extern void ReleaseSRWLockShared(nint* srwLock);

    /// <summary>
    /// Append every ground triangle overlapping the box, three world-space vertices per triangle.
    /// Returns false (leaving the list as it was) when the scene cannot be read or holds more than
    /// <paramref name="maxTriangles"/> triangles there.
    /// </summary>
    public static bool TryCollectGround(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
        => TryCollect(min, max, triangles, maxTriangles, CollisionMeshGeometry.IsGround);

    /// <summary>
    /// Append every solid triangle overlapping the box (ground, walls, roofs: what a navigation mesh is
    /// built from), leaving out water and the colliders the game keeps inactive (material 0x400 without
    /// 0x10, the rule vnavmesh uses). Same contract as <see cref="TryCollectGround"/>.
    /// </summary>
    public static bool TryCollectSolid(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
        => TryCollect(min, max, triangles, maxTriangles, IsSolid);

    private static bool IsSolid(ulong material)
        => (material & 0x410) != 0x400 && !CollisionMeshGeometry.IsWater(material);

    private static bool TryCollect(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles, Func<ulong, bool> accept)
    {
        var start = triangles.Count;
        var framework = Framework.Instance();
        if (framework == null) return false;
        var module = framework->BGCollisionModule;
        if (module == null || module->ShuttingDown || module->SceneManager == null) return false;

        var ok = true;
        AcquireSRWLockShared(&module->UpdateTaskLock);
        try
        {
            foreach (var wrapper in module->SceneManager->Scenes)
            {
                if (wrapper == null || wrapper->Scene == null || wrapper->Scene->FirstCollider == null) continue;
                foreach (var collider in wrapper->Scene->Colliders)
                {
                    if (collider == null) continue;
                    // Same gates a ground raycast applies: active, and on the default layer.
                    if ((collider->VisibilityFlags & 1) == 0 || (collider->LayerMask & 1) == 0) continue;
                    AppendCollider(collider, min, max, triangles, accept);
                    if ((triangles.Count - start) / 3 > maxTriangles) { ok = false; break; }
                }
                if (!ok) break;
            }
        }
        catch
        {
            ok = false;
        }
        finally
        {
            ReleaseSRWLockShared(&module->UpdateTaskLock);
        }

        if (!ok) triangles.RemoveRange(start, triangles.Count - start);
        return ok;
    }

    /// <summary>
    /// Height of the water surface above or below a point, if the game has a water collider there.
    /// Ground raycasts never see water (its material lacks the ground bit), so this asks for it by name.
    /// </summary>
    public static bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        surfaceY = 0f;
        var framework = Framework.Instance();
        if (framework == null) return false;
        var module = framework->BGCollisionModule;
        if (module == null || module->ShuttingDown) return false;

        // Material filter as two 64-bit words, mask then value: accept colliders whose material has the water bit.
        var flags = stackalloc int[] { (int)CollisionMeshGeometry.WaterMaterial, 0, (int)CollisionMeshGeometry.WaterMaterial, 0 };
        var origin = position + new Vector3(0f, searchAbove, 0f);
        var direction = new Vector3(0f, -1f, 0f);
        var hit = new RaycastHit();
        if (!module->RaycastMaterialFilter(&hit, &origin, &direction, searchAbove + searchBelow, 1, flags))
            return false;
        if (!float.IsFinite(hit.Point.Y)) return false;
        surfaceY = hit.Point.Y;
        return true;
    }

    private static void AppendCollider(Collider* collider, Vector3 min, Vector3 max, List<Vector3> triangles, Func<ulong, bool> accept)
    {
        switch (collider->GetColliderType())
        {
            case ColliderType.Mesh:
            {
                var mesh = (ColliderMesh*)collider;
                if (!mesh->Loaded || mesh->MeshIsSimple || mesh->Mesh == null) return;
                if (!CollisionMeshGeometry.Overlaps(mesh->WorldBoundingBox.Min, mesh->WorldBoundingBox.Max, min, max)) return;
                var pcb = (MeshPCB*)mesh->Mesh;
                if (pcb->RootNode == null) return;
                AppendMeshNode(pcb->RootNode, mesh->World.Row0, mesh->World.Row1, mesh->World.Row2, mesh->World.Row3,
                    collider->ObjectMaterialValue, collider->ObjectMaterialMask, min, max, triangles, 0, accept);
                return;
            }
            case ColliderType.Box:
            {
                if (!accept(collider->ObjectMaterialValue)) return;
                var box = (ColliderBox*)collider;
                AppendIfNear(triangles, min, max, list => CollisionMeshGeometry.AppendBox(list, box->World.Row0, box->World.Row1, box->World.Row2, box->World.Row3));
                return;
            }
            case ColliderType.Cylinder:
            {
                if (!accept(collider->ObjectMaterialValue)) return;
                var cylinder = (ColliderCylinder*)collider;
                AppendIfNear(triangles, min, max, list => CollisionMeshGeometry.AppendCylinder(list, cylinder->World.Row0, cylinder->World.Row1, cylinder->World.Row2, cylinder->World.Row3));
                return;
            }
            case ColliderType.Sphere:
            {
                if (!accept(collider->ObjectMaterialValue)) return;
                var sphere = (ColliderSphere*)collider;
                AppendIfNear(triangles, min, max, list => CollisionMeshGeometry.AppendSphere(list, sphere->World.Row0, sphere->World.Row1, sphere->World.Row2, sphere->World.Row3));
                return;
            }
            // Streamed colliders hold no geometry themselves (their meshes are separate colliders in the
            // scene); planes are zone walls and triggers, not ground.
            default:
                return;
        }
    }

    private static readonly List<Vector3> scratch = new();

    // Tessellate a primitive collider, then keep only the triangles that reach into the box.
    private static void AppendIfNear(List<Vector3> triangles, Vector3 min, Vector3 max, Action<List<Vector3>> tessellate)
    {
        scratch.Clear();
        tessellate(scratch);
        for (var i = 0; i + 2 < scratch.Count; i += 3)
        {
            if (!CollisionMeshGeometry.Overlaps(scratch[i], scratch[i + 1], scratch[i + 2], min, max)) continue;
            triangles.Add(scratch[i]);
            triangles.Add(scratch[i + 1]);
            triangles.Add(scratch[i + 2]);
        }
    }

    private static void AppendMeshNode(MeshPCB.FileNode* node, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 row3,
        ulong objectValue, ulong objectMask, Vector3 min, Vector3 max, List<Vector3> triangles, int depth, Func<ulong, bool> accept)
    {
        if (node == null || depth > MaxNodeDepth) return;

        // Skip the whole subtree when its bounds, moved to world space, miss the box.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) != 0 ? node->LocalBounds.Max.X : node->LocalBounds.Min.X,
                (i & 2) != 0 ? node->LocalBounds.Max.Y : node->LocalBounds.Min.Y,
                (i & 4) != 0 ? node->LocalBounds.Max.Z : node->LocalBounds.Min.Z);
            var world = CollisionMeshGeometry.Transform(corner, row0, row1, row2, row3);
            lo = Vector3.Min(lo, world);
            hi = Vector3.Max(hi, world);
        }
        if (!CollisionMeshGeometry.Overlaps(lo, hi, min, max)) return;

        var vertexCount = node->NumVertsRaw + node->NumVertsCompressed;
        var primitives = node->PrimitivesPtr;
        for (var i = 0; i < node->NumPrims; i++)
        {
            ref var primitive = ref primitives[i];
            if (primitive.V1 >= vertexCount || primitive.V2 >= vertexCount || primitive.V3 >= vertexCount) continue;
            if (!accept(CollisionMeshGeometry.EffectiveMaterial(primitive.Material, objectValue, objectMask))) continue;
            var a = CollisionMeshGeometry.Transform(node->Vertex(primitive.V1), row0, row1, row2, row3);
            var b = CollisionMeshGeometry.Transform(node->Vertex(primitive.V2), row0, row1, row2, row3);
            var c = CollisionMeshGeometry.Transform(node->Vertex(primitive.V3), row0, row1, row2, row3);
            if (!CollisionMeshGeometry.Overlaps(a, b, c, min, max)) continue;
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        AppendMeshNode(node->Child1, row0, row1, row2, row3, objectValue, objectMask, min, max, triangles, depth + 1, accept);
        AppendMeshNode(node->Child2, row0, row1, row2, row3, objectValue, objectMask, min, max, triangles, depth + 1, accept);
    }
}
