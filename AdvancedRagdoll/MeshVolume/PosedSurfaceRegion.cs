// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>
/// One cached bone-local body region queried at an arbitrary pose. Contacts report an
/// outward normal from the region's winding, so a point that has already passed through
/// the surface is pushed back out instead of further in. Mods can import reflected
/// winding; the region's dominant orientation is measured once and applied to every face.
/// </summary>
public sealed class PosedSurfaceRegion
{
    public string Name { get; }
    public BodyMeshGeometry Geometry { get; }
    private readonly Vector3 localMin, localMax;
    private readonly float windingSign;

    public PosedSurfaceRegion(string name, BodyMeshGeometry geometry)
    {
        Name = name; Geometry = geometry;
        localMin = new Vector3(float.MaxValue); localMax = new Vector3(float.MinValue);
        var centroid = Vector3.Zero;
        foreach (var vertex in geometry.Vertices)
        {
            localMin = Vector3.Min(localMin, vertex); localMax = Vector3.Max(localMax, vertex);
            centroid += vertex;
        }
        centroid /= Math.Max(1, geometry.Vertices.Length);
        double outward = 0;
        var v = geometry.Vertices; var f = geometry.Indices;
        for (int i = 0; i < f.Length; i += 3)
        {
            var a = v[f[i]]; var b = v[f[i + 1]]; var c = v[f[i + 2]];
            outward += Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3 - centroid);
        }
        windingSign = outward >= 0 ? 1 : -1;
    }

    /// <summary>Nearest surface point within <paramref name="reach"/> of a world point, for this
    /// region posed at (origin, rotation). Signed distance is negative inside the surface.</summary>
    public bool TryContact(Vector3 world, Vector3 origin, Quaternion rotation, float reach,
        out Vector3 point, out Vector3 outwardNormal, out float signedDistance)
    {
        point = outwardNormal = default; signedDistance = 0;
        var local = Vector3.Transform(world - origin, Quaternion.Conjugate(rotation));
        if (Vector3.DistanceSquared(local, Vector3.Clamp(local, localMin, localMax)) > reach * reach) return false;
        if (!Geometry.Query.Nearest(local, 1f, 1e-16f, out var closest, out var normal, out var distanceSquared, faceTowardPoint: false) ||
            distanceSquared > reach * reach) return false;
        normal *= windingSign;
        signedDistance = Vector3.Dot(local - closest, normal);
        // Outside, push from the closest surface point toward the query point. Unlike a face
        // normal this direction is continuous across edges and vertices, so a contact sliding
        // over facets does not jump direction each step. Inside keeps the outward face normal.
        var away = local - closest;
        if (signedDistance > 1e-5f && away.LengthSquared() > 1e-12f)
        {
            signedDistance = away.Length();
            normal = away / signedDistance;
        }
        point = origin + Vector3.Transform(closest, rotation);
        outwardNormal = Vector3.Transform(normal, rotation);
        return true;
    }
}
