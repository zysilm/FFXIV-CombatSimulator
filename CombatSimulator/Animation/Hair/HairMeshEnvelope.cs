// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.Hair;

internal readonly record struct HairMeshEnvelope(Vector3 Tip, float Radius)
{
    /// <summary>Fit the visible weighted cloud, not a guessed bone stub. PCA finds
    /// an elongated tuft/braid; gravity resolves nearly symmetric clouds.</summary>
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
        var axis = mean - origin;
        if (axis.LengthSquared() < 1e-8f) axis = downhill;
        axis = Vector3.Normalize(axis);
        for (int i = 0; i < 16; i++)
        {
            var next = new Vector3(Vector3.Dot(rowX, axis), Vector3.Dot(rowY, axis), Vector3.Dot(rowZ, axis));
            if (next.LengthSquared() < 1e-12f) break;
            axis = Vector3.Normalize(next);
        }
        if (Vector3.Dot(axis, mean - origin) < 0) axis = -axis;
        float min = 0, max = 0; float radialSum = 0;
        foreach (var p in points)
        {
            var d = p - origin; float projection = Vector3.Dot(d, axis);
            min = MathF.Min(min, projection); max = MathF.Max(max, projection);
            radialSum += (d - axis * projection).LengthSquared();
        }
        if (-min > max * 1.1f || (MathF.Abs(max + min) < .015f && Vector3.Dot(axis, downhill) < 0))
        { axis = -axis; max = -min; }
        return new(origin + axis * Math.Clamp(max, .025f, 1.5f), Math.Clamp(MathF.Sqrt(radialSum / points.Length), .004f, .06f));
    }
}
