// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>Bounded smooth surface builder, separate from simulation node counts.</summary>
public sealed class FluidGeometryBuilder
{
    private readonly FluidVertex[] vertices;
    public int Count { get; private set; }
    public bool Overflowed { get; private set; }
    public ReadOnlySpan<FluidVertex> Vertices => vertices.AsSpan(0, Count);
    public FluidGeometryBuilder(int capacity = 12288)
    {
        if (capacity < 3 || capacity > WorldGeometryRenderer.MaxVertices) throw new ArgumentOutOfRangeException(nameof(capacity));
        vertices = new FluidVertex[capacity - capacity % 3];
    }
    public void Reset() { Count = 0; Overflowed = false; }

    /// <summary>Read-only status diagnostic; no extra work in the render or pose callbacks.</summary>
    public string DescribeVisibility()
    {
        float peakThickness = 0, coverageSum = 0;
        int covered = 0;
        for (int i = 0; i < Count; i++)
        {
            peakThickness = MathF.Max(peakThickness, vertices[i].Thickness);
            coverageSum += vertices[i].Coverage;
            if (vertices[i].Coverage > 0) covered++;
        }
        return $"submittedLiquid:maxThickness={peakThickness * 1000:F3}mm,meanCoverage={(Count > 0 ? coverageSum / Count : 0):F3},coveredVertices={covered}/{Count}";
    }

    /// <summary>
    /// Diagnostic geometry exaggeration only. Leaves liquid ownership, contacts and shader/material untouched.
    /// Thin surfaces are thickened along their normals; declared ellipsoids grow uniformly about their center.
    /// This visible volume does not represent conserved simulation inventory. Adds no vertices or allocations.
    /// </summary>
    public void ExaggerateForVisibilityPreview(float thicknessMultiplier, float minimumThickness, float ellipsoidScale)
    {
        if (!float.IsFinite(thicknessMultiplier) || !float.IsFinite(minimumThickness) || !float.IsFinite(ellipsoidScale)) return;
        thicknessMultiplier = Math.Clamp(thicknessMultiplier, 1, 16);
        minimumThickness = Math.Clamp(minimumThickness, 0, .01f);
        ellipsoidScale = Math.Clamp(ellipsoidScale, 1, 4);
        for (int i = 0; i < Count; i++)
        {
            var vertex = vertices[i];
            bool closed = vertex.VolumeRadii.X > 0 && vertex.VolumeRadii.Y > 0 && vertex.VolumeRadii.Z > 0;
            if (closed)
                vertices[i] = new FluidVertex(vertex.VolumeCenter + (vertex.Position - vertex.VolumeCenter) * ellipsoidScale,
                    vertex.Normal, vertex.UV, vertex.Thickness * ellipsoidScale, vertex.Coverage,
                    vertex.VolumeCenter, vertex.VolumeRadii * ellipsoidScale);
            else if (vertex.Thickness > 0)
            {
                float thickness = Math.Clamp(vertex.Thickness * thicknessMultiplier, minimumThickness, .02f);
                vertices[i] = new FluidVertex(vertex.Position + vertex.Normal * (thickness - vertex.Thickness),
                    vertex.Normal, vertex.UV, thickness, vertex.Coverage);
            }
        }
    }

    /// <summary>
    /// A continuous variable-radius Hermite thread with parallel-transport frames
    /// and hemispherical caps. Input nodes are simulation samples, not separate beads.
    /// The whole thread is rejected if its bounded tessellation does not fit.
    /// Thickness is a local normal-chord approximation, not an exact optical ray path.
    /// </summary>
    public void AddThread(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> radii,
        int radialSides = 24, int subdivisionsPerSegment = 4)
    {
        if (positions.Length < 2 || positions.Length != radii.Length) return;
        radialSides = Math.Clamp(radialSides, 12, 32);
        subdivisionsPerSegment = Math.Clamp(subdivisionsPerSegment, 1, 12);
        const int capRings = 6;
        var intervals = (long)(positions.Length - 1) * subdivisionsPerSegment;
        var required = intervals * radialSides * 6 + 2L * radialSides * (2 * capRings - 1) * 3;
        if (required > vertices.Length - Count) { Overflowed = true; return; }
        float threadLength = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            if (!WorldGeometryBuilder.Finite(positions[i]) || !float.IsFinite(radii[i]) || radii[i] <= 0) return;
            if (i > 0)
            {
                var distanceSquared = (positions[i] - positions[i - 1]).LengthSquared();
                if (!float.IsFinite(distanceSquared) || distanceSquared < 1e-12f) return;
                threadLength += MathF.Sqrt(distanceSquared);
            }
        }
        Span<FluidVertex> previousRing = stackalloc FluidVertex[32];
        Span<FluidVertex> currentRing = stackalloc FluidVertex[32];
        EvaluateThread(positions, radii, 0, 0, out var center, out var tangent, out var radius, out var slope);
        var reference = MathF.Abs(tangent.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var radialFrame = Vector3.Normalize(Vector3.Cross(tangent, reference));
        FillThreadRing(previousRing, center, tangent, radialFrame, radius, slope, 0, radialSides);
        AddThreadCap(center, -tangent, radialFrame, radius, radialSides, capRings, 0);
        var previousTangent = tangent;
        for (var segment = 0; segment < positions.Length - 1; segment++)
            for (var subdivision = 1; subdivision <= subdivisionsPerSegment; subdivision++)
            {
                var t = (float)subdivision / subdivisionsPerSegment;
                EvaluateThread(positions, radii, segment, t, out center, out tangent, out radius, out slope);
                radialFrame = TransportThreadFrame(previousTangent, tangent, radialFrame);
                var u = (segment + t) / (positions.Length - 1);
                FillThreadRing(currentRing, center, tangent, radialFrame, radius, slope, u * threadLength, radialSides);
                for (var side = 0; side < radialSides; side++)
                {
                    var next = (side + 1) % radialSides;
                    AddTriangle(previousRing[side], currentRing[side], currentRing[next]);
                    AddTriangle(previousRing[side], currentRing[next], previousRing[next]);
                }
                currentRing[..radialSides].CopyTo(previousRing);
                previousTangent = tangent;
            }
        AddThreadCap(center, tangent, radialFrame, radius, radialSides, capRings, threadLength);
    }

    private static void EvaluateThread(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> radii,
        int segment, float t, out Vector3 center, out Vector3 tangent, out float radius, out float slope)
    {
        var a = positions[segment]; var b = positions[segment + 1];
        var chord = b - a; var length = chord.Length();
        var before = positions[Math.Max(0, segment - 1)];
        var after = positions[Math.Min(positions.Length - 1, segment + 2)];
        // Distance-scaled Hermite tangents avoid the long overshoots of uniform
        // Catmull-Rom when adjacent simulation segments have different lengths.
        var m0 = (b - before) * (length / MathF.Max(length + (a - before).Length(), 1e-6f));
        var m1 = (after - a) * (length / MathF.Max(length + (after - b).Length(), 1e-6f));
        var t2 = t * t; var t3 = t2 * t;
        center = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * m0
            + (-2 * t3 + 3 * t2) * b + (t3 - t2) * m1;
        var derivative = (6 * t2 - 6 * t) * a + (3 * t2 - 4 * t + 1) * m0
            + (-6 * t2 + 6 * t) * b + (3 * t2 - 2 * t) * m1;
        tangent = derivative.LengthSquared() > 1e-12f ? Vector3.Normalize(derivative) : chord / length;
        // Smooth monotone radius interpolation cannot become negative between nodes.
        var radiusDifference = radii[segment + 1] - radii[segment];
        radius = radii[segment] + radiusDifference * (3 * t2 - 2 * t3);
        slope = radiusDifference * (6 * t - 6 * t2) / MathF.Max(derivative.Length(), 1e-6f);
    }

    private static Vector3 TransportThreadFrame(Vector3 previousTangent, Vector3 tangent, Vector3 frame)
    {
        var cross = Vector3.Cross(previousTangent, tangent);
        var sine = cross.Length(); var cosine = Math.Clamp(Vector3.Dot(previousTangent, tangent), -1, 1);
        if (sine > 1e-6f)
        {
            var axis = cross / sine;
            frame = frame * cosine + Vector3.Cross(axis, frame) * sine
                + axis * Vector3.Dot(axis, frame) * (1 - cosine);
        }
        // At an exact reversal, rotation around frame leaves frame itself unchanged.
        // Reproject to remove accumulated drift before constructing the next ring.
        frame -= tangent * Vector3.Dot(frame, tangent);
        if (frame.LengthSquared() < 1e-12f)
            frame = Vector3.Cross(tangent, MathF.Abs(tangent.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        return Vector3.Normalize(frame);
    }

    private static void FillThreadRing(Span<FluidVertex> ring, Vector3 center, Vector3 tangent,
        Vector3 frame, float radius, float slope, float u, int sides)
    {
        var bitangent = Vector3.Cross(tangent, frame);
        for (var side = 0; side < sides; side++)
        {
            var v = (float)side / sides; var angle = v * MathF.Tau;
            var radial = frame * MathF.Cos(angle) + bitangent * MathF.Sin(angle);
            var normal = Vector3.Normalize(radial - tangent * slope);
            ring[side] = new FluidVertex(center + radial * radius, normal, new Vector2(u, v * MathF.Tau * radius), 2 * radius);
        }
    }

    private void AddThreadCap(Vector3 center, Vector3 outwardAxis, Vector3 frame, float radius,
        int sides, int rings, float u)
    {
        Span<FluidVertex> previous = stackalloc FluidVertex[32];
        Span<FluidVertex> current = stackalloc FluidVertex[32];
        var bitangent = Vector3.Cross(outwardAxis, frame);
        for (var side = 0; side < sides; side++)
            previous[side] = new FluidVertex(center + outwardAxis * radius, outwardAxis, new Vector2(u, (float)side / sides * MathF.Tau * radius), 2 * radius);
        for (var ring = 1; ring <= rings; ring++)
        {
            var theta = (float)ring / rings * MathF.PI * 0.5f;
            for (var side = 0; side < sides; side++)
            {
                var v = (float)side / sides; var angle = v * MathF.Tau;
                var radial = frame * MathF.Cos(angle) + bitangent * MathF.Sin(angle);
                var normal = outwardAxis * MathF.Cos(theta) + radial * MathF.Sin(theta);
                current[side] = new FluidVertex(center + normal * radius, normal, new Vector2(u, v * MathF.Tau * radius), 2 * radius);
            }
            for (var side = 0; side < sides; side++)
            {
                var next = (side + 1) % sides;
                AddTriangle(previous[side], current[side], current[next]);
                if (ring > 1) AddTriangle(previous[side], current[next], previous[next]);
            }
            current[..sides].CopyTo(previous);
        }
    }
    public void AddTriangle(FluidVertex a, FluidVertex b, FluidVertex c)
    {
        if (!Valid(a) || !Valid(b) || !Valid(c)) return;
        // The producer supplies outward normals. Make geometric winding agree
        // with them so the shared back-face cull shows the liquid's near surface.
        // This also protects future film producers from a reversed patch basis.
        var geometricNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        if (Vector3.Dot(geometricNormal, a.Normal + b.Normal + c.Normal) < 0)
            (b, c) = (c, b);
        if (Count + 3 > vertices.Length) { Overflowed = true; return; }
        vertices[Count++] = a; vertices[Count++] = b; vertices[Count++] = c;
    }
    internal static bool Valid(FluidVertex v) => WorldGeometryBuilder.Finite(v.Position) && WorldGeometryBuilder.Finite(v.Normal)
        && v.Normal.LengthSquared() > 1e-8f && float.IsFinite(v.UV.X) && float.IsFinite(v.UV.Y)
        && float.IsFinite(v.Thickness) && v.Thickness >= 0 && float.IsFinite(v.Coverage) && v.Coverage >= 0 && v.Coverage <= 1
        && WorldGeometryBuilder.Finite(v.VolumeCenter) && WorldGeometryBuilder.Finite(v.VolumeRadii)
        && (v.VolumeRadii == Vector3.Zero || (v.VolumeRadii.X > 0 && v.VolumeRadii.Y > 0 && v.VolumeRadii.Z > 0));
    /// <summary>Analytic smooth normals and normal-chord liquid thickness.</summary>
    public void AddEllipsoid(Vector3 center, Vector3 radii, int longitude = 48, int latitude = 24)
    {
        if (!WorldGeometryBuilder.Finite(center) || !WorldGeometryBuilder.Finite(radii) || radii.X <= 0 || radii.Y <= 0 || radii.Z <= 0) return;
        longitude = Math.Clamp(longitude, 12, 64); latitude = Math.Clamp(latitude, 6, 32);
        if (Count + longitude * (latitude - 1) * 6 > vertices.Length) { Overflowed = true; return; }
        FluidVertex Point(int x, int y)
        {
            var u = (float)x / longitude; var v = (float)y / latitude;
            var theta = v * MathF.PI; var phi = u * MathF.Tau;
            var unit = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
            var normal = Vector3.Normalize(unit / radii);
            var inverse = normal / radii;
            var chord = 2 * Vector3.Dot(unit / radii, normal) / inverse.LengthSquared();
            return new FluidVertex(center + unit * radii, normal, new Vector2(u * MathF.Tau * radii.X, v * MathF.PI * radii.Y), MathF.Max(0, chord),
                volumeCenter: center, volumeRadii: radii);
        }
        for (var y = 0; y < latitude; y++)
            for (var x = 0; x < longitude; x++)
            {
                var a = Point(x, y); var b = Point(x + 1, y); var c = Point(x, y + 1); var d = Point(x + 1, y + 1);
                if (y > 0) AddTriangle(a, b, c);
                if (y < latitude - 1) AddTriangle(b, d, c);
            }
    }
}
