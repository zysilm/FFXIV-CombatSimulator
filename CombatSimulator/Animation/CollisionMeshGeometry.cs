// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>
/// Pure geometry and material rules for turning the game's collision scene into triangles. Kept apart
/// from <see cref="WorldCollisionMesh"/>, which reads game memory, so it can be tested offline.
/// </summary>
public static class CollisionMeshGeometry
{
    /// <summary>Material bit of ordinary solid ground: what the plugin's ground raycasts ask for.</summary>
    public const ulong GroundMaterial = 0x4000;
    /// <summary>Material bit of a water surface (the game's fishable water).</summary>
    public const ulong WaterMaterial = 0x8000;

    /// <summary>
    /// The material a triangle effectively has: its own, with the bits its collider masks replaced by
    /// the collider's value. A collider with no mask leaves the triangle's material alone.
    /// </summary>
    public static ulong EffectiveMaterial(ulong primitive, ulong objectValue, ulong objectMask)
        => objectMask == 0 ? primitive : (primitive & ~objectMask) | objectValue;

    /// <summary>True when a ground raycast (mask and value both the ground bit) would accept the material.</summary>
    public static bool IsGround(ulong material) => (material & GroundMaterial) == GroundMaterial;

    public static bool IsWater(ulong material) => (material & WaterMaterial) == WaterMaterial;

    /// <summary>Local point to world through a 4x3 matrix given as its rows (three axes, then translation).</summary>
    public static Vector3 Transform(Vector3 local, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 row3)
        => row0 * local.X + row1 * local.Y + row2 * local.Z + row3;

    /// <summary>True when a triangle's bounds overlap the box.</summary>
    public static bool Overlaps(Vector3 a, Vector3 b, Vector3 c, Vector3 min, Vector3 max)
    {
        var lo = Vector3.Min(a, Vector3.Min(b, c));
        var hi = Vector3.Max(a, Vector3.Max(b, c));
        return lo.X <= max.X && hi.X >= min.X && lo.Y <= max.Y && hi.Y >= min.Y && lo.Z <= max.Z && hi.Z >= min.Z;
    }

    /// <summary>True when two boxes overlap.</summary>
    public static bool Overlaps(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
        => minA.X <= maxB.X && maxA.X >= minB.X && minA.Y <= maxB.Y && maxA.Y >= minB.Y && minA.Z <= maxB.Z && maxA.Z >= minB.Z;

    /// <summary>
    /// A box collider (local corners at ±1) as 12 triangles in world space, three vertices each,
    /// wound so their normals point out of the box.
    /// </summary>
    public static void AppendBox(List<Vector3> triangles, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 row3)
    {
        Span<Vector3> corner = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
            corner[i] = Transform(new Vector3((i & 1) != 0 ? 1f : -1f, (i & 2) != 0 ? 1f : -1f, (i & 4) != 0 ? 1f : -1f), row0, row1, row2, row3);

        // Each face as two triangles; corner index bits are x, y, z.
        ReadOnlySpan<byte> faces = stackalloc byte[]
        {
            0, 4, 6, 0, 6, 2, // -x
            1, 3, 7, 1, 7, 5, // +x
            0, 1, 5, 0, 5, 4, // -y
            2, 6, 7, 2, 7, 3, // +y
            0, 2, 3, 0, 3, 1, // -z
            4, 5, 7, 4, 7, 6, // +z
        };
        foreach (var index in faces) triangles.Add(corner[index]);
    }

    /// <summary>
    /// A cylinder collider (local axis Y, radius and half-height 1) as triangles in world space: side
    /// quads and both caps, wound outward.
    /// </summary>
    public static void AppendCylinder(List<Vector3> triangles, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 row3, int segments = 16)
    {
        segments = Math.Clamp(segments, 3, 64);
        var top = Transform(Vector3.UnitY, row0, row1, row2, row3);
        var bottom = Transform(-Vector3.UnitY, row0, row1, row2, row3);
        for (var i = 0; i < segments; i++)
        {
            var a0 = 2f * MathF.PI * i / segments;
            var a1 = 2f * MathF.PI * (i + 1) / segments;
            var p0 = new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0));
            var p1 = new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1));
            var b0 = Transform(p0 - Vector3.UnitY, row0, row1, row2, row3);
            var b1 = Transform(p1 - Vector3.UnitY, row0, row1, row2, row3);
            var t0 = Transform(p0 + Vector3.UnitY, row0, row1, row2, row3);
            var t1 = Transform(p1 + Vector3.UnitY, row0, row1, row2, row3);
            triangles.Add(b0); triangles.Add(t0); triangles.Add(t1);
            triangles.Add(b0); triangles.Add(t1); triangles.Add(b1);
            triangles.Add(top); triangles.Add(t1); triangles.Add(t0);
            triangles.Add(bottom); triangles.Add(b0); triangles.Add(b1);
        }
    }

    /// <summary>A sphere collider (local radius 1) as a coarse latitude/longitude mesh in world space.</summary>
    public static void AppendSphere(List<Vector3> triangles, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 row3, int rings = 6, int segments = 10)
    {
        Vector3 Point(int ring, int segment)
        {
            var polar = MathF.PI * ring / rings;
            var azimuth = 2f * MathF.PI * segment / segments;
            return Transform(new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth)), row0, row1, row2, row3);
        }
        for (var ring = 0; ring < rings; ring++)
            for (var segment = 0; segment < segments; segment++)
            {
                var a = Point(ring, segment);
                var b = Point(ring, segment + 1);
                var c = Point(ring + 1, segment + 1);
                var d = Point(ring + 1, segment);
                if (ring > 0) { triangles.Add(a); triangles.Add(b); triangles.Add(c); }
                if (ring < rings - 1) { triangles.Add(a); triangles.Add(c); triangles.Add(d); }
            }
    }
}
