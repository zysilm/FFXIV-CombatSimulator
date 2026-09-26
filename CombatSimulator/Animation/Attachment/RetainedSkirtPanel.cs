using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>A waist-mounted rigid cloth strip with one outward hinge. Gravity supplies torque,
/// a torsional spring supplies shape retention and implicit damping dissipates energy. Capsule
/// contacts constrain the ANGLE, never independently project vertices or stretch bone links.</summary>
public sealed class RetainedSkirtPanel
{
    public readonly record struct Capsule(Vector3 A, Vector3 B, float Radius);
    public readonly record struct Segment(Vector3 A, Vector3 B);
    private readonly Segment[] segments;
    private readonly Vector3 axis, centerOfMass, widthAxis;
    private readonly float width, length;
    private readonly List<Capsule> colliders = new();
    private float accumulator, contactAngle;
    public float Angle { get; private set; }
    public float AngularVelocity { get; private set; }
    public float ResidualPenetration { get; private set; }
    public bool InContact { get; private set; }
    public Plane? Ground { get; set; }
    public Quaternion Rotation => Quaternion.CreateFromAxisAngle(axis, Angle);

    public RetainedSkirtPanel(Segment[] segments, Vector3 hingeAxis, float halfWidth)
    {
        if (segments.Length == 0) throw new ArgumentException("A panel needs at least one segment.", nameof(segments));
        this.segments = (Segment[])segments.Clone();
        axis = Vector3.Normalize(hingeAxis);
        widthAxis = axis;
        width = MathF.Max(0, halfWidth);
        var center = Vector3.Zero;
        foreach (var segment in segments)
        {
            center += (segment.A + segment.B) * .5f;
            length = MathF.Max(length, MathF.Max(segment.A.Length(), segment.B.Length()));
        }
        centerOfMass = center / segments.Length;
        length = MathF.Max(length, .02f);
    }

    /// <param name="gravity">Gravity in the current anchor frame; magnitudes are m/s².</param>
    /// <param name="capsules">Body capsules in that frame, relative to the fixed panel pivot.</param>
    public void Advance(float dt, Vector3 gravity, IReadOnlyList<Capsule> capsules,
        float maxAngle, float thickness, float stiffness, float damping)
    {
        if (!float.IsFinite(dt) || dt < 0 || !float.IsFinite(gravity.LengthSquared())) return;
        var startAngle = Angle;
        maxAngle = Math.Clamp(maxAngle, 0, MathF.PI * .65f);
        startAngle = Math.Min(startAngle, maxAngle);
        thickness = MathF.Max(0, thickness);
        // Ignore sub-0.25mm collider noise relative to the waist; true carrying is handled by
        // the anchor transform. This does not freeze the body or accumulate a world-space lag.
        if (colliders.Count != capsules.Count) { colliders.Clear(); for (var i = 0; i < capsules.Count; i++) colliders.Add(capsules[i]); }
        else for (var i = 0; i < capsules.Count; i++)
        {
            var old = colliders[i]; var next = capsules[i];
            if (Vector3.DistanceSquared(old.A, next.A) > 6.25e-8f ||
                Vector3.DistanceSquared(old.B, next.B) > 6.25e-8f || MathF.Abs(old.Radius - next.Radius) > .00025f)
                colliders[i] = next;
        }

        // Contact is resolved in angular space after integration. A second leg can obstruct
        // larger angles too, so never assume all angles above the first clear sample are safe.
        if (Angle > maxAngle) { Angle = maxAngle; AngularVelocity = 0; }
        accumulator = MathF.Min(accumulator + MathF.Min(dt, .1f), .1f);
        const float step = 1f / 120;
        var spring = Math.Clamp(stiffness, 4, 100);
        var drag = 2 * MathF.Sqrt(spring) * Math.Clamp(damping, 1, 4);
        while (accumulator + 1e-7f >= step)
        {
            accumulator = MathF.Max(0, accumulator - step);
            var lever = Vector3.Transform(centerOfMass, Rotation);
            var torque = Vector3.Dot(Vector3.Cross(lever, gravity), axis) / MathF.Max(length * length / 3, .0004f);
            AngularVelocity = (AngularVelocity + step * (torque - spring * Angle)) /
                (1 + drag * step + spring * step * step);
            AngularVelocity = Math.Clamp(AngularVelocity, -1.5f, 1.5f);
            var next = Angle + AngularVelocity * step;
            Angle = Math.Clamp(next, 0, maxAngle);
            if (Angle != next) AngularVelocity = 0;
            if (MathF.Abs(AngularVelocity) < .0001f && MathF.Abs(torque - spring * Angle) < .001f)
                AngularVelocity = 0;
        }
        var allowed = FindClearAngle(Angle, maxAngle, thickness);
        var depthAtPrevious = Depth(contactAngle, thickness);
        if (InContact && contactAngle <= maxAngle && MathF.Abs(allowed - contactAngle) < .02f &&
            depthAtPrevious is > -.003f and <= .00025f) allowed = contactAngle;
        InContact = MathF.Abs(allowed - Angle) > 1e-7f;
        if (InContact)
        {
            // Contact search can jump between distant feasible branches as the legs cross.
            // Its solution is a target, not permission to teleport a visible skirt panel.
            // Bound the complete frame displacement, including the spring integration above.
            var maxChange = 2.5f * Math.Clamp(dt, 0, .1f);
            Angle = Math.Clamp(allowed, MathF.Max(0, startAngle - maxChange), MathF.Min(maxAngle, startAngle + maxChange));
            AngularVelocity = 0;
            contactAngle = Angle;
        }
        // Some postures have no collision-free angle within the allowed cone. Keep the best
        // bounded pose and expose the residual; never oscillate between incompatible projections.
        ResidualPenetration = MathF.Max(0, Depth(Angle, thickness));
    }

    private float FindClearAngle(float desired, float maxAngle, float thickness)
    {
        if (Depth(desired, thickness) <= 0) return desired;
        // Prefer the current branch when competing penetrations are almost equivalent.
        var best = desired; var bestDepth = Depth(desired, thickness);
        var nearest = float.NaN;
        var steps = Math.Max(1, (int)MathF.Ceiling(maxAngle / (.025f)));
        for (var i = 0; i <= steps; i++)
        {
            var candidate = maxAngle * i / steps;
            var depth = Depth(candidate, thickness);
            if (depth < bestDepth - .0005f) { bestDepth = depth; best = candidate; }
            if (depth > 0 || !float.IsNaN(nearest) && MathF.Abs(candidate - desired) >= MathF.Abs(nearest - desired)) continue;
            nearest = candidate;
        }
        if (!float.IsNaN(nearest))
        {
            var low = desired; var high = nearest;
            for (var refine = 0; refine < 12; refine++)
            {
                var mid = (low + high) * .5f;
                if (Depth(mid, thickness) > 0) low = mid; else high = mid;
            }
            return high;
        }
        return best;
    }

    public float Depth(float angle, float thickness)
    {
        var worst = float.NegativeInfinity;
        var rotation = Quaternion.CreateFromAxisAngle(axis, angle);
        foreach (var segment in segments)
            for (var strip = -1; strip <= 1; strip++)
            {
                var side = widthAxis * (strip * width);
                var a = Vector3.Transform(segment.A + side, rotation);
                var b = Vector3.Transform(segment.B + side, rotation);
                // The sewn waist itself cannot move. Check the panel below the first 2cm seam.
                if (segment.A.Length() < .001f)
                    a = Vector3.Lerp(a, b, MathF.Min(.2f, .02f / MathF.Max((b - a).Length(), .001f)));
                if (Ground is { } floor)
                    worst = MathF.Max(worst, thickness - MathF.Min(Plane.DotCoordinate(floor, a), Plane.DotCoordinate(floor, b)));
                foreach (var capsule in colliders)
                    worst = MathF.Max(worst, capsule.Radius + thickness - SegmentDistance(a, b, capsule.A, capsule.B));
            }
        return worst;
    }

    private static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var u = b - a; var v = d - c; var r = a - c;
        var uu = u.LengthSquared(); var vv = v.LengthSquared();
        var ur = Vector3.Dot(u, r); var vr = Vector3.Dot(v, r); var uv = Vector3.Dot(u, v);
        float s, t;
        if (uu < 1e-10f) { s = 0; t = vv < 1e-10f ? 0 : Math.Clamp(vr / vv, 0, 1); }
        else if (vv < 1e-10f) { t = 0; s = Math.Clamp(-ur / uu, 0, 1); }
        else
        {
            var denominator = uu * vv - uv * uv;
            s = denominator > 1e-10f ? Math.Clamp((uv * vr - ur * vv) / denominator, 0, 1) : 0;
            t = (uv * s + vr) / vv;
            if (t < 0) { t = 0; s = Math.Clamp(-ur / uu, 0, 1); }
            else if (t > 1) { t = 1; s = Math.Clamp((uv - ur) / uu, 0, 1); }
        }
        return Vector3.Distance(a + u * s, c + v * t);
    }
}
