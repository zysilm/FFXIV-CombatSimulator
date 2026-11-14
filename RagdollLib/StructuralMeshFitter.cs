// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>
/// Fits one ragdoll body's collision volume to the character surface skinned to its bone. Pure
/// geometry: reference-pose samples in, dimensions out. It owns collision geometry only, never
/// skeleton topology or joint anchors.
///
/// Every fit yields an envelope (a box for the torso, hands and feet; a capsule for limbs). The
/// torso and thighs also get a convex hull, which is what they collide with: a box is as thick as
/// the thickest point of its segment along its whole length and a capsule as thick as the limb's
/// root, so a body lying on either is held off the ground where it is thinner.
/// </summary>
internal static class StructuralMeshFitter
{
    /// <summary>A surface point in the reference pose (model space, unscaled).</summary>
    /// <param name="Influence">Skin weight of the fitted bone at this point, 0..1.</param>
    /// <param name="Area">Surface area the point stands for, so dense mesh regions do not outvote sparse ones.</param>
    public readonly record struct Sample(Vector3 Position, float Influence, float Area);

    /// <param name="HullSlices">Slices along the segment for the convex hull; 0 keeps the plain envelope.</param>
    public readonly record struct Target(string Bone, string Child, bool UseBox, int MinimumSamples, int HullSlices = 0);

    public sealed class Fit
    {
        public float RadiusX;
        public float RadiusZ;
        public float HalfLength;
        public Vector3 CenterBoneLocal;
        public Vector3 AxisBoneLocal;
        // Extra rotation around AxisBoneLocal, on top of the bone's own roll: measured from the
        // cross-section's principal axis, not the bone's assembly-time roll. Zero for capsules.
        public float RollRadians;
        public bool UseBox;
        public int SampleCount;
        /// <summary>
        /// Corners of the convex hull in the section frame (Y along the segment, origin at the bone,
        /// unscaled), or null when the envelope itself is the collision shape.
        /// </summary>
        public Vector3[]? HullPoints;
    }

    // Only the torso and the thighs are fitted: they are what a lying body rests on, and where a
    // fixed volume around the bone is furthest from the surface (the spine runs along the back of
    // the torso, not through its middle). Arms, shins, hands and feet keep their built-in volumes.
    public static readonly Target[] Targets =
    {
        new("j_kosi",    "j_sebo_a", true,  30, HullSlices: 3),
        new("j_sebo_a",  "j_sebo_b", true,  30, HullSlices: 3),
        new("j_sebo_b",  "j_sebo_c", true,  30, HullSlices: 3),
        new("j_sebo_c",  "j_kubi",   true,  30, HullSlices: 3),
        new("j_asi_a_l", "j_asi_b_l", false, 24, HullSlices: 4),
        new("j_asi_a_r", "j_asi_b_r", false, 24, HullSlices: 4),
    };

    /// <summary>Left/right pairs that must agree; a one-sided or lopsided result is a broken mesh, not anatomy.</summary>
    public static readonly (string Left, string Right)[] Pairs = { ("j_asi_a_l", "j_asi_a_r") };

    // Directions around the segment the hull is measured in. A multiple of four, so the front, back
    // and both sides each get a flat facet of their own: the most protruding part of the surface
    // (the curve of a buttock, a shoulder blade) lies in the middle of a face and never on a corner,
    // and the body rests on it without rocking.
    private const int HullDirections = 12;
    // Share of the surface that must lie inside a facet. On a round section the facet then sits
    // about 1% inside the true extreme, and stray vertices cannot move it.
    private const float HullSupportPercentile = 0.98f;
    // Every facet is at least this wide, as a share of the body's height (about 1.5 cm). A facet
    // narrower than that is a ridge or a point in disguise, and is moved in until it is wide enough.
    private const float MinimumFacetWidthPerHeight = 0.009f;
    // Flesh gives under the body's weight; a rigid hull cannot, so it is drawn in by this share of
    // the body's height (about 1 cm) and the skin settles into the ground instead of on top of it.
    private const float SoftTissueInsetPerHeight = 0.006f;
    private const float SliceOverlap = 0.25f;
    private const int MinimumSliceSamples = 12;
    private const int MaximumSamplesPerFit = 4000;

    private readonly record struct Weighted(float Value, float Weight);

    private readonly struct CrossSection
    {
        public readonly float CenterX, CenterZ, RadiusX, RadiusZ, LongitudinalLow, LongitudinalHigh;
        // Weighted 2D covariance of the inlier samples around the centre, in the measured frame.
        public readonly float Cxx, Czz, Cxz;
        public readonly int InlierCount;
        // Everything the hull is built from. Wider than the inliers the envelope is measured on:
        // those drop the 3% of the surface furthest from the bone, which on a pelvis is the curve
        // of the buttocks, the very part a lying body rests on. The hull rejects strays itself,
        // facet by facet.
        public readonly List<(Vector3 Local, float Weight)> Surface;

        public CrossSection(float centerX, float centerZ, float radiusX, float radiusZ, float low, float high,
            float cxx, float czz, float cxz, int inlierCount, List<(Vector3 Local, float Weight)> surface)
        {
            CenterX = centerX; CenterZ = centerZ; RadiusX = radiusX; RadiusZ = radiusZ;
            LongitudinalLow = low; LongitudinalHigh = high; Cxx = cxx; Czz = czz; Cxz = cxz;
            InlierCount = inlierCount; Surface = surface;
        }
    }

    /// <summary>Stature used for the plausibility limits, from the reference pose's head and feet.</summary>
    public static float ReferenceHeight(Vector3? head, Vector3? leftFoot, Vector3? rightFoot)
    {
        if (head is { } h && leftFoot is { } l && rightFoot is { } r)
        {
            var boneHeight = Vector3.Distance(h, (l + r) * 0.5f);
            if (float.IsFinite(boneHeight) && boneHeight is >= 0.80f and <= 3.0f)
                return boneHeight + 0.12f;
        }

        // Only a corruption guard when the canonical endpoints are absent. It does not define a
        // collision volume; every accepted dimension still comes from mesh samples.
        return 1.60f;
    }

    /// <summary>
    /// The frame a body is measured and later simulated in: Y along the segment, Z the bone's own Z
    /// projected off it. The ragdoll builds its body rotations with the same function, so what is
    /// measured here in the reference pose holds in any pose.
    /// </summary>
    public static Quaternion SectionRotation(Vector3 segmentDirection, Quaternion boneRotation)
    {
        var y = NormalizeOrFallback(segmentDirection, Vector3.UnitY);
        var z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitZ, boneRotation), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitX, boneRotation), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(MathF.Abs(Vector3.Dot(y, Vector3.UnitY)) > 0.9f ? Vector3.UnitZ : Vector3.UnitY, y);

        z = NormalizeOrFallback(z, Vector3.UnitZ);
        var x = NormalizeOrFallback(Vector3.Cross(y, z), Vector3.UnitX);
        z = NormalizeOrFallback(Vector3.Cross(x, y), z);

        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <param name="trunkAxis">
    /// The direction the torso runs in, in the reference pose (pelvis to neck). Torso segments are
    /// measured along it rather than along their own bones. A hull's faces are parallel to the axis
    /// it is measured along, and a body lying down turns until the face under it is flat on the
    /// ground: with one axis for the whole torso its segments lie down together and the spine keeps
    /// its shape, where with one axis per bone each segment would lie flat on its own and bend the
    /// waist by the angle between them.
    /// </param>
    public static bool TryFit(Target target, float referenceHeight, Matrix4x4 boneModel, Vector3 childOrigin,
        IReadOnlyList<Sample> samples, Vector3? trunkAxis, out Fit fit, out string? rejectionReason)
    {
        fit = new Fit();
        rejectionReason = null;
        if (samples.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {samples.Count} weighted samples";
            return false;
        }

        var maximumInfluence = 0f;
        foreach (var sample in samples)
            maximumInfluence = MathF.Max(maximumInfluence, sample.Influence);
        // A dense mesh says nothing a few thousand of its points do not: take every n-th.
        var stride = Math.Max(1, samples.Count / MaximumSamplesPerFit);
        var minimumInfluence = MathF.Max(0.12f, maximumInfluence * 0.30f);

        var boneOrigin = boneModel.Translation;
        var segment = childOrigin - boneOrigin;
        var segmentLength = segment.Length();
        if (!target.UseBox && segmentLength < 0.04f)
        {
            rejectionReason = $"reference segment too short ({segmentLength:F4}m)";
            return false;
        }

        var axis = target.UseBox && trunkAxis is { } trunk && trunk.LengthSquared() > 1e-6f
            ? Vector3.Normalize(trunk)
            : segmentLength >= 0.01f
                ? segment / segmentLength
                : NormalizeOrFallback(Vector3.TransformNormal(Vector3.UnitY, boneModel), Vector3.UnitY);

        if (!Matrix4x4.Decompose(boneModel, out _, out var boneRotation, out _))
            boneRotation = Quaternion.Identity;
        var sectionRotation = SectionRotation(axis, boneRotation);

        // Longitudinal filtering does not depend on the roll, so it happens once up front; the
        // cross-section itself may be measured more than once as the roll candidate changes.
        var deltas = new List<(Vector3 Delta, float Weight)>(samples.Count);
        var longitudinalMin = float.MaxValue;
        var longitudinalMax = float.MinValue;
        for (var index = 0; index < samples.Count; index += stride)
        {
            var sample = samples[index];
            if (sample.Influence < minimumInfluence || !(sample.Area > 0f))
                continue;
            var delta = sample.Position - boneOrigin;
            // A limb keeps to its own stretch of bone, clear of the joints at either end. A torso
            // segment takes all the surface skinned to it: its bones are short and sit at one end
            // of what they carry (the chest hangs below its bone, the pelvis below its own).
            if (!target.UseBox)
            {
                var t = Vector3.Dot(delta, axis) / segmentLength;
                if (t < 0.08f || t > 0.92f)
                    continue;
                longitudinalMin = MathF.Min(longitudinalMin, t);
                longitudinalMax = MathF.Max(longitudinalMax, t);
            }
            deltas.Add((delta, sample.Influence * sample.Area));
        }

        if (deltas.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {deltas.Count} meaningful central samples";
            return false;
        }
        if (!target.UseBox && longitudinalMax - longitudinalMin < 0.35f)
        {
            rejectionReason = $"insufficient segment coverage ({longitudinalMax - longitudinalMin:F2})";
            return false;
        }

        if (!TryMeasureCrossSection(deltas, sectionRotation, target, out var section, out rejectionReason))
            return false;

        // Torso boxes: the bone's own roll is an assembly artifact, not the body's true
        // cross-section orientation. Re-derive the roll from the sample cloud's principal axis
        // (2D PCA in the cross-section plane).
        var appliedRoll = 0f;
        if (target.UseBox)
        {
            var denom = section.Cxx - section.Czz;
            if (MathF.Abs(section.Cxz) > 1e-8f || MathF.Abs(denom) > 1e-8f)
            {
                var theta = 0.5f * MathF.Atan2(2f * section.Cxz, denom);
                theta = Math.Clamp(theta, -MathF.PI / 4f, MathF.PI / 4f);

                // Try both signs rather than trust the handedness by hand: whichever drives the
                // cross-covariance closer to zero is the one that aligns the box to the cloud.
                var bestCxz = MathF.Abs(section.Cxz);
                var baseRotation = sectionRotation;
                foreach (var candidateTheta in new[] { theta, -theta })
                {
                    var candidateRotation = Quaternion.Normalize(
                        baseRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, candidateTheta));
                    if (!TryMeasureCrossSection(deltas, candidateRotation, target, out var candidate, out _))
                        continue;
                    if (MathF.Abs(candidate.Cxz) < bestCxz)
                    {
                        bestCxz = MathF.Abs(candidate.Cxz);
                        appliedRoll = candidateTheta;
                        sectionRotation = candidateRotation;
                        section = candidate;
                    }
                }
            }
        }

        if (!ValidateDimensions(target, referenceHeight, segmentLength, section.RadiusX, section.RadiusZ, out rejectionReason))
            return false;

        var longitudinalSpan = section.LongitudinalHigh - section.LongitudinalLow;
        if (!float.IsFinite(longitudinalSpan) || longitudinalSpan < referenceHeight * 0.015f)
        {
            rejectionReason = $"invalid longitudinal mesh span ({longitudinalSpan:F4}m)";
            return false;
        }

        var centerSection = new Vector3(
            section.CenterX, (section.LongitudinalLow + section.LongitudinalHigh) * 0.5f, section.CenterZ);
        var radiusForLength = MathF.Max(section.RadiusX, section.RadiusZ);
        var halfLength = target.UseBox
            ? longitudinalSpan * 0.5f
            : MathF.Max(referenceHeight * 0.008f, longitudinalSpan * 0.5f - radiusForLength);
        if (!float.IsFinite(halfLength) || halfLength > referenceHeight * 0.22f)
        {
            rejectionReason = $"invalid mesh half length ({halfLength:F4}m)";
            return false;
        }

        var inverseBoneRotation = Quaternion.Inverse(boneRotation);
        fit = new Fit
        {
            RadiusX = section.RadiusX,
            RadiusZ = section.RadiusZ,
            HalfLength = halfLength,
            CenterBoneLocal = Vector3.Transform(Vector3.Transform(centerSection, sectionRotation), inverseBoneRotation),
            AxisBoneLocal = NormalizeOrFallback(Vector3.Transform(axis, inverseBoneRotation), Vector3.UnitY),
            RollRadians = appliedRoll,
            UseBox = target.UseBox,
            SampleCount = section.InlierCount,
        };

        if (target.HullSlices > 0)
        {
            fit.HullPoints = BuildHull(section.Surface, section.LongitudinalLow, section.LongitudinalHigh,
                target.HullSlices, referenceHeight * SoftTissueInsetPerHeight,
                referenceHeight * MinimumFacetWidthPerHeight, target.MinimumSamples);
        }
        return true;
    }

    /// <summary>Drops both sides of a pair unless both were fitted and agree in thickness.</summary>
    public static string? ValidatePair(Dictionary<string, Fit> fits, string left, string right)
    {
        if (!fits.TryGetValue(left, out var leftFit) || !fits.TryGetValue(right, out var rightFit))
        {
            fits.Remove(left);
            fits.Remove(right);
            return null;
        }

        var leftRadius = (leftFit.RadiusX + leftFit.RadiusZ) * 0.5f;
        var rightRadius = (rightFit.RadiusX + rightFit.RadiusZ) * 0.5f;
        var ratio = MathF.Max(leftRadius, rightRadius) / MathF.Max(0.001f, MathF.Min(leftRadius, rightRadius));
        if (ratio <= 1.22f)
            return null;

        fits.Remove(left);
        fits.Remove(right);
        return $"rejected asymmetric pair '{left}'/'{right}' (ratio={ratio:F2})";
    }

    /// <summary>
    /// The convex hull of a segment as a stack of prisms. Each slice of the segment gets its own
    /// outline, measured facet by facet, and spans its whole length at that outline: the thickest
    /// part of a body is therefore a flat area, and the thinner slices beside it taper away from it.
    /// </summary>
    private static Vector3[]? BuildHull(List<(Vector3 Local, float Weight)> inliers, float low, float high,
        int slices, float inset, float minimumFacetWidth, int minimumSliceSamples)
    {
        var length = high - low;
        if (slices < 1 || !(length > 1e-4f) || inliers.Count < minimumSliceSamples)
            return null;

        var directions = new Vector2[HullDirections];
        for (var d = 0; d < HullDirections; d++)
        {
            var angle = d * (2f * MathF.PI / HullDirections);
            directions[d] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        var points = new List<Vector3>(slices * HullDirections * 2);
        var sliceLength = length / slices;
        var sliceSamples = new List<(Vector3 Local, float Weight)>(inliers.Count);
        var projections = new List<Weighted>(inliers.Count);
        var supports = new float[HullDirections];
        for (var slice = 0; slice < slices; slice++)
        {
            var from = low + slice * sliceLength;
            var to = from + sliceLength;
            sliceSamples.Clear();
            foreach (var sample in inliers)
                if (sample.Local.Y >= from - sliceLength * SliceOverlap && sample.Local.Y <= to + sliceLength * SliceOverlap)
                    sliceSamples.Add(sample);
            // A slice the mesh barely covers takes the outline of the whole segment.
            var source = sliceSamples.Count >= MinimumSliceSamples ? sliceSamples : inliers;

            for (var d = 0; d < HullDirections; d++)
            {
                projections.Clear();
                foreach (var (local, weight) in source)
                    projections.Add(new Weighted(local.X * directions[d].X + local.Z * directions[d].Y, weight));
                supports[d] = WeightedPercentile(projections, HullSupportPercentile) - inset;
            }

            var outline = FacetedOutline(directions, supports, minimumFacetWidth);
            if (outline == null)
                return null;
            foreach (var corner in outline)
            {
                points.Add(new Vector3(corner.X, from, corner.Y));
                points.Add(new Vector3(corner.X, to, corner.Y));
            }
        }
        return points.Count >= 8 ? points.ToArray() : null;
    }

    /// <summary>
    /// The outline for the given supports, with every direction owning a facet at least
    /// <paramref name="minimumFacetWidth"/> wide. Where the surface comes to a ridge the facet
    /// facing it would be a single corner; it is moved in, a little at a time, until it is a face.
    /// </summary>
    private static List<Vector2>? FacetedOutline(Vector2[] directions, float[] supports, float minimumFacetWidth)
    {
        const float Step = 0.001f;
        for (var pass = 0; pass < 200; pass++)
        {
            var outline = ClipOutline(directions, supports);
            if (outline == null)
                return null;

            var narrow = false;
            for (var d = 0; d < directions.Length; d++)
            {
                // The facet of a direction is the stretch of outline lying on its half-plane's edge.
                float low = float.MaxValue, high = float.MinValue;
                var along = new Vector2(-directions[d].Y, directions[d].X);
                foreach (var corner in outline)
                {
                    if (MathF.Abs(Vector2.Dot(corner, directions[d]) - supports[d]) > 1e-4f)
                        continue;
                    var position = Vector2.Dot(corner, along);
                    low = MathF.Min(low, position);
                    high = MathF.Max(high, position);
                }
                if (high - low >= minimumFacetWidth)
                    continue;
                supports[d] -= Step;
                narrow = true;
            }
            if (!narrow)
                return outline;
        }
        return null;
    }

    /// <summary>The polygon enclosed by one half-plane per direction, or null when they enclose nothing.</summary>
    private static List<Vector2>? ClipOutline(Vector2[] directions, float[] supports)
    {
        const float Far = 10f;
        var polygon = new List<Vector2> { new(-Far, -Far), new(Far, -Far), new(Far, Far), new(-Far, Far) };
        var next = new List<Vector2>(directions.Length + 4);
        for (var d = 0; d < directions.Length && polygon.Count >= 3; d++)
        {
            next.Clear();
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                var da = Vector2.Dot(a, directions[d]) - supports[d];
                var db = Vector2.Dot(b, directions[d]) - supports[d];
                if (da <= 0f) next.Add(a);
                if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
                    next.Add(a + (b - a) * (da / (da - db)));
            }
            (polygon, next) = (next, polygon);
        }

        if (polygon.Count < 3)
            return null;
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return MathF.Abs(area) * 0.5f > 1e-5f ? polygon : null;
    }

    /// <summary>
    /// Measures a cross-section (centre, radii, longitudinal extent and the 2D covariance of the
    /// inlier samples) in the frame <paramref name="sectionRotation"/> defines.
    /// </summary>
    private static bool TryMeasureCrossSection(List<(Vector3 Delta, float Weight)> deltas, Quaternion sectionRotation,
        Target target, out CrossSection measurement, out string? rejectionReason)
    {
        measurement = default;
        rejectionReason = null;
        var inverseSectionRotation = Quaternion.Inverse(sectionRotation);

        // First discard isolated seam or corrupt vertices with a broad radial envelope around the
        // bone, then find the cross-section's centre. The collider may sit off the skeleton axis;
        // joint anchors stay at the bone pivots.
        var locals = new List<(Vector3 Local, float Weight)>(deltas.Count);
        var rawRadial = new List<Weighted>(deltas.Count);
        foreach (var (delta, weight) in deltas)
        {
            var local = Vector3.Transform(delta, inverseSectionRotation);
            locals.Add((local, weight));
            rawRadial.Add(new Weighted(MathF.Sqrt(local.X * local.X + local.Z * local.Z), weight));
        }
        var envelope = WeightedPercentile(rawRadial, 0.97f);
        if (!float.IsFinite(envelope) || envelope <= 0.005f)
        {
            rejectionReason = "invalid radial envelope";
            return false;
        }

        var inliers = new List<(Vector3 Local, float Weight)>(locals.Count);
        var surface = new List<(Vector3 Local, float Weight)>(locals.Count);
        var signedX = new List<Weighted>(locals.Count);
        var signedZ = new List<Weighted>(locals.Count);
        var longitudinal = new List<Weighted>(locals.Count);
        foreach (var (local, weight) in locals)
        {
            var distance = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
            if (distance <= envelope * 1.5f)
                surface.Add((local, weight));
            if (distance > envelope)
                continue;
            inliers.Add((local, weight));
            signedX.Add(new Weighted(local.X, weight));
            signedZ.Add(new Weighted(local.Z, weight));
            longitudinal.Add(new Weighted(local.Y, weight));
        }
        if (inliers.Count < target.MinimumSamples)
        {
            rejectionReason = $"only {inliers.Count} inlier samples";
            return false;
        }

        // The centre is the middle of the surface's extent on each axis, not its median: a median
        // follows wherever the surface has more area, and a volume centred there overshoots the
        // skin on the opposite side.
        var lowX = WeightedPercentile(signedX, 0.05f);
        var highX = WeightedPercentile(signedX, 0.95f);
        var lowZ = WeightedPercentile(signedZ, 0.05f);
        var highZ = WeightedPercentile(signedZ, 0.95f);
        var centerX = (lowX + highX) * 0.5f;
        var centerZ = (lowZ + highZ) * 0.5f;

        var radial = new List<Weighted>(inliers.Count);
        double sumWeight = 0, sumWxx = 0, sumWzz = 0, sumWxz = 0;
        foreach (var (local, weight) in inliers)
        {
            var dx = local.X - centerX;
            var dz = local.Z - centerZ;
            radial.Add(new Weighted(MathF.Sqrt(dx * dx + dz * dz), weight));
            sumWeight += weight;
            sumWxx += (double)weight * dx * dx;
            sumWzz += (double)weight * dz * dz;
            sumWxz += (double)weight * dx * dz;
        }

        float radiusX, radiusZ;
        if (target.UseBox)
        {
            radiusX = (highX - lowX) * 0.5f * 0.98f;
            radiusZ = (highZ - lowZ) * 0.5f * 0.98f;
        }
        else
        {
            radiusX = radiusZ = WeightedPercentile(radial, 0.90f) * 0.98f;
        }

        measurement = new CrossSection(centerX, centerZ, radiusX, radiusZ,
            WeightedPercentile(longitudinal, 0.06f), WeightedPercentile(longitudinal, 0.94f),
            sumWeight > 1e-12 ? (float)(sumWxx / sumWeight) : 0f,
            sumWeight > 1e-12 ? (float)(sumWzz / sumWeight) : 0f,
            sumWeight > 1e-12 ? (float)(sumWxz / sumWeight) : 0f,
            inliers.Count, surface);
        return true;
    }

    private static bool ValidateDimensions(Target target, float referenceHeight, float segmentLength,
        float radiusX, float radiusZ, out string? reason)
    {
        reason = null;
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusZ) || radiusX <= 0f || radiusZ <= 0f)
        {
            reason = "non-finite thickness";
            return false;
        }

        var smaller = MathF.Min(radiusX, radiusZ);
        var larger = MathF.Max(radiusX, radiusZ);
        if (target.Bone.StartsWith("j_asi_d_", StringComparison.Ordinal))
        {
            // A foot is box-like but must not use the torso envelope. X/Z are the two transverse
            // dimensions around the ankle-to-toe axis; accept either ordering because reference
            // bone rolls differ between races and skeleton variants.
            if (smaller < referenceHeight * 0.006f || smaller > referenceHeight * 0.030f ||
                larger < referenceHeight * 0.012f || larger > referenceHeight * 0.060f || larger / smaller > 4f)
            {
                reason = $"foot thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4})";
                return false;
            }
            return true;
        }

        if (target.Bone.StartsWith("j_te_", StringComparison.Ordinal))
        {
            if (smaller < referenceHeight * 0.0035f || smaller > referenceHeight * 0.020f ||
                larger < referenceHeight * 0.009f || larger > referenceHeight * 0.045f || larger / smaller > 5f)
            {
                reason = $"hand thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4})";
                return false;
            }
            return true;
        }

        if (target.UseBox)
        {
            if (radiusX < referenceHeight * 0.026f || radiusZ < referenceHeight * 0.026f ||
                radiusX > referenceHeight * 0.115f || radiusZ > referenceHeight * 0.115f)
            {
                reason = $"torso thickness outside stature envelope ({radiusX:F4}, {radiusZ:F4})";
                return false;
            }
            var aspect = radiusX / radiusZ;
            if (aspect < 0.50f || aspect > 2.0f)
            {
                reason = $"torso aspect ratio {aspect:F2} is implausible";
                return false;
            }
            return true;
        }

        var radius = (radiusX + radiusZ) * 0.5f;

        // j_asi_b is a short (~6 cm) proximal stub, wider than it is long: an envelope scaled by
        // the segment length would reject every measurement, so it is a pure stature ratio.
        if (target.Bone.StartsWith("j_asi_b_", StringComparison.Ordinal))
        {
            if (radius < referenceHeight * 0.018f || radius > referenceHeight * 0.038f)
            {
                reason = $"upper-knee radius {radius:F4} outside stature envelope";
                return false;
            }
            return true;
        }

        var thigh = target.Bone.StartsWith("j_asi_a_", StringComparison.Ordinal);
        var minimum = MathF.Max(referenceHeight * (thigh ? 0.020f : 0.011f), segmentLength * (thigh ? 0.10f : 0.06f));
        var maximum = MathF.Min(referenceHeight * (thigh ? 0.070f : 0.055f), segmentLength * (thigh ? 0.40f : 0.27f));
        if (maximum <= minimum || radius < minimum || radius > maximum)
        {
            reason = $"limb radius {radius:F4} outside safety envelope {minimum:F4}-{maximum:F4}";
            return false;
        }
        return true;
    }

    private static float WeightedPercentile(List<Weighted> values, float percentile)
    {
        if (values.Count == 0)
            return 0f;

        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        double totalWeight = 0;
        foreach (var value in values)
            totalWeight += MathF.Max(0f, value.Weight);
        if (totalWeight <= 1e-12)
            return values[values.Count / 2].Value;

        var threshold = Math.Clamp(percentile, 0f, 1f) * totalWeight;
        double accumulated = 0;
        foreach (var value in values)
        {
            accumulated += MathF.Max(0f, value.Weight);
            if (accumulated >= threshold)
                return value.Value;
        }
        return values[^1].Value;
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-6f)
            return Vector3.Normalize(value);
        return fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        var normal = NormalizeOrFallback(planeNormal, Vector3.UnitY);
        return value - Vector3.Dot(value, normal) * normal;
    }
}
