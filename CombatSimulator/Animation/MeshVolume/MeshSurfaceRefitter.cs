// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>Updates an existing two-sided mesh without changing mass, centre or ownership.</summary>
public static class MeshSurfaceRefitter
{
    public static bool Refit(ref Mesh mesh, ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0 ||
            mesh.Triangles.Length != indices.Length / 3 * 2 ||
            mesh.Tree.LeafCount != mesh.Triangles.Length) return false;
        // Validate before modifying the first triangle: failed reconstruction leaves the
        // previously accepted collider and its acceleration structure intact.
        foreach (var index in indices)
            if ((uint)index >= vertices.Length) return false;
        foreach (var vertex in vertices)
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z)) return false;
        if (mesh.Tree.LeafCount == 2)
        {
            // BEPU 2.5.0-beta.22 BinnedBuild's <=2-subtree fast path writes
            // root children but leaves Leaf metadata uninitialized. Rebuild the
            // two backlinks from authoritative child indices before dereferencing
            // them; otherwise fresh/reused pool contents can cause native writes
            // outside the tree or update the same root child twice.
            ref var root = ref mesh.Tree.Nodes[0];
            int leafA = -root.A.Index - 1, leafB = -root.B.Index - 1;
            if ((uint)leafA >= 2 || (uint)leafB >= 2 || leafA == leafB) return false;
            mesh.Tree.Leaves[leafA] = new Leaf(0, 0);
            mesh.Tree.Leaves[leafB] = new Leaf(0, 1);
        }
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            var front = i / 3 * 2;
            mesh.Triangles[front] = new Triangle(a, b, c);
            mesh.Triangles[front + 1] = new Triangle(a, c, b);
            var minimum = Vector3.Min(a, Vector3.Min(b, c));
            var maximum = Vector3.Max(a, Vector3.Max(b, c));
            ref var frontLeaf = ref mesh.Tree.GetNodeChildForLeaf(front);
            frontLeaf.Min = minimum; frontLeaf.Max = maximum;
            ref var backLeaf = ref mesh.Tree.GetNodeChildForLeaf(front + 1);
            backLeaf.Min = minimum; backLeaf.Max = maximum;
        }
        mesh.Tree.Refit();
        return true;
    }
}
