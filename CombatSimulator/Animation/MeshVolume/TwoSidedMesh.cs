// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Numerics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace CombatSimulator.Animation.MeshVolume;

internal static class TwoSidedMesh
{
    /// <summary>BEPU mesh with both windings of every face. Body partitions are open at joints and
    /// mods may import reflected winding, so one-sided contacts would fail from either side.</summary>
    public static Mesh Create(BodyMeshGeometry geometry, BufferPool bufferPool)
    {
        bufferPool.Take<Triangle>(geometry.Indices.Length / 3 * 2, out var triangles);
        try
        {
            for (int i = 0; i < geometry.Indices.Length; i += 3)
            {
                var a = geometry.Vertices[geometry.Indices[i]];
                var b = geometry.Vertices[geometry.Indices[i + 1]];
                var c = geometry.Vertices[geometry.Indices[i + 2]];
                triangles[i / 3 * 2] = new Triangle(a, b, c);
                triangles[i / 3 * 2 + 1] = new Triangle(a, c, b);
            }
            return new Mesh(triangles, Vector3.One, bufferPool);
        }
        catch
        {
            bufferPool.Return(ref triangles);
            throw;
        }
    }
}
