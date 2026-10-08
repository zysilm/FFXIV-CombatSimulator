// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.Hair;

internal readonly record struct HairMeshEnvelope(Vector3 Tip, float Radius)
{
    /// <summary>Fit a rooted tuft. Width is not strand length or contact thickness:
    /// broad fringe clouds must not become sideways guides or thick spheres.</summary>
    public static HairMeshEnvelope Fit(ReadOnlySpan<Vector3> points, Vector3 origin, Vector3 downhill)
    {
        if (points.Length < 4) return new(origin + downhill * .04f, .008f);
        Vector3 mean = default;
        foreach (var p in points) mean += p;
        mean /= points.Length;
        Vector3 rowX = default, rowY = default, rowZ = default;
        foreach (var p in points)
        {
            var d = p - mean; rowX += d * d.X; rowY += d * d.Y; rowZ += d * d.Z;
        }
        var rooted = mean - origin;
        var axis = rooted;
        if (axis.LengthSquared() < 1e-8f) axis = downhill;
        axis = Vector3.Normalize(axis);
        for (int i = 0; i < 16; i++)
        {
            var next = new Vector3(Vector3.Dot(rowX, axis), Vector3.Dot(rowY, axis), Vector3.Dot(rowZ, axis));
            if (next.LengthSquared() < 1e-12f) break;
            axis = Vector3.Normalize(next);
        }
        if (Vector3.Dot(axis, mean - origin) < 0) axis = -axis;
        // Centered PCA describes spread, not the direction away from a root.
        // A wide fringe's largest eigenvector runs across the forehead. Preserve
        // the rooted direction unless the cloud actually extends along that axis.
        if (rooted.LengthSquared() > 1e-8f &&
            MathF.Abs(Vector3.Dot(axis, Vector3.Normalize(rooted))) < .65f)
            axis = Vector3.Normalize(rooted);
        float min = 0, max = 0;
        foreach (var p in points)
        {
            var d = p - origin; float projection = Vector3.Dot(d, axis);
            min = MathF.Min(min, projection); max = MathF.Max(max, projection);
        }
        if (-min > max * 1.1f || (MathF.Abs(max + min) < .015f && Vector3.Dot(axis, downhill) < 0))
        { axis = -axis; max = -min; }
        // Find the larger transverse dimension, then measure thickness along
        // its perpendicular. RMS radius around the guide confuses card width
        // with thickness and pushes side hair centimetres away from the face.
        var side = Vector3.Cross(axis, MathF.Abs(axis.X) < .8f ? Vector3.UnitX : Vector3.UnitY);
        side = Vector3.Normalize(side);
        side = Vector3.Normalize(side + Vector3.Cross(axis, side) * .618f);
        for (int i = 0; i < 16; i++)
        {
            var next = new Vector3(Vector3.Dot(rowX, side), Vector3.Dot(rowY, side), Vector3.Dot(rowZ, side));
            next -= axis * Vector3.Dot(next, axis);
            if (next.LengthSquared() < 1e-12f) break;
            side = Vector3.Normalize(next);
        }
        var normal = Vector3.Normalize(Vector3.Cross(axis, side));
        float thickness = 0;
        foreach (var point in points)
        {
            float depth = Vector3.Dot(point - mean, normal);
            thickness += depth * depth;
        }
        return new(origin + axis * Math.Clamp(max, .025f, 1.5f),
            Math.Clamp(MathF.Sqrt(thickness / points.Length), .004f, .02f));
    }
}
