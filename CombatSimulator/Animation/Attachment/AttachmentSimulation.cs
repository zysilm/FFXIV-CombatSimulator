// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>
/// Persistent world-space XPBD cage. No game pointers or rendering dependencies. Opening rings,
/// stretch/shear edges and bending spans retain the garment; it never becomes a free drop.
/// Collision is sampled on the cage, not on every render vertex.
/// </summary>
public sealed class AttachmentSimulation
{
    public const int RingSize = 6;
    private const float Step = 1f / 120f;
    public sealed class Particle
    {
        public Vector3 Position, Velocity, Target, PreviousTarget, Local;
        public Vector3 BeforeStep, ContactNormal, SurfaceVelocity, ContactDisplacement;
        public float ContactCorrection, MaxContactDepth, ContactFriction, Range, Tension;
        public Vector3 FloorPoint, FloorNormal;
        public bool HasFloor;
        internal float AnchorLambda;
    }
    public sealed class Ring
    {
        public string Name = "";
        public int Start;
        public Vector3 InitialCenter;
        public Quaternion InitialRotation, TargetRotation, RenderRotation;
        public float AnchorRange; // negative means no permanent connection here
        public Vector3 AnchorOffset;
        public Vector3 BindCenterOffset;
        public Quaternion ShapeFrameOffset = Quaternion.Identity;
    }
    private sealed class Edge
    {
        public int A, B;
        public float Rest, Compliance, Lambda;
        public bool Bend;
        public bool Surface;
    }
    public readonly record struct Capsule(Vector3 A, Vector3 B, float Radius, Vector3 Velocity);
    public readonly record struct ContactPoint(Vector3 Position, Vector3 Velocity, float Radius);
    public readonly List<Particle> Particles = new();
    public readonly List<Ring> Rings = new();
    public readonly List<Capsule> Capsules = new();
    public readonly List<ContactPoint> OtherGarments = new();
    private readonly List<Edge> edges = new();
    private readonly HashSet<(int, int)> neighbors = new();
    private readonly List<Capsule> previousCapsules = new();
    private readonly List<Capsule> stepCapsules = new();
    private AttachmentSettings settings;
    private float accumulator, restTime;
    private readonly float scale;
    public bool Sleeping { get; private set; }
    public int ContactCount { get; private set; }
    public float MaxTension { get; private set; }
    public int RecoveryCount { get; private set; }

    public AttachmentSimulation(AttachmentSettings settings, float scale = 1f)
    {
        this.settings = settings.Validated();
        this.scale = float.IsFinite(scale) ? Math.Clamp(scale, 0.1f, 10f) : 1f;
    }

    public int AddRing(string name, Vector3 center, Quaternion rotation, float radius, float anchorRange)
    {
        if (Rings.Count >= 48) throw new InvalidOperationException("Attachment cage exceeds 48 rings.");
        var ring = new Ring { Name = name, Start = Particles.Count, InitialCenter = center,
            InitialRotation = rotation, TargetRotation = rotation, RenderRotation = rotation, AnchorRange = anchorRange,
            AnchorOffset = settings.AnchorOffsets.TryGetValue(name, out var offset) ? offset.ToVector() * scale : Vector3.Zero };
        var ringIndex = Rings.Count;
        Rings.Add(ring);
        for (var i = 0; i < RingSize; i++)
        {
            var angle = i * MathF.Tau / RingSize;
            var local = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * radius;
            var p = center + Vector3.Transform(local, rotation);
            var target = p + Vector3.Transform(ring.AnchorOffset, rotation);
            Particles.Add(new Particle { Position = p, Target = target, PreviousTarget = target, Local = local,
                Range = anchorRange < 0 ? -1 : settings.SlipDistance * scale * anchorRange *
                    (1f + settings.Asymmetry * MathF.Cos(angle)) });
        }
        for (var i = 0; i < RingSize; i++)
        {
            AddEdge(ring.Start + i, ring.Start + (i + 1) % RingSize, false);
            AddEdge(ring.Start + i, ring.Start + (i + 2) % RingSize, true);
        }
        return ringIndex;
    }

    public void Connect(int parent, int child)
    {
        var a = Rings[parent]; var b = Rings[child];
        // Nearest angular alignment avoids twisting sleeves merely because their local axes differ.
        var offset = 0; var winding = 1; var best = float.PositiveInfinity;
        for (var sign = -1; sign <= 1; sign += 2)
        for (var j = 0; j < RingSize; j++)
        {
            var sum = 0f;
            for (var i = 0; i < RingSize; i++)
                sum += Vector3.DistanceSquared(Particles[a.Start + i].Position,
                    Particles[b.Start + (sign * i + j + RingSize) % RingSize].Position);
            if (sum < best) { best = sum; offset = j; winding = sign; }
        }
        for (var i = 0; i < RingSize; i++)
        {
            // These are internal cage supports, not triangles of the garment surface. In
            // particular a shoulder/waist branch can legitimately pass through the torso.
            AddEdge(a.Start + i, b.Start + (winding * i + offset + RingSize) % RingSize, false, false);
            AddEdge(a.Start + i, b.Start + (winding * i + offset + 1 + RingSize) % RingSize, true, false);
        }
    }

    private void AddEdge(int a, int b, bool bend, bool surface = true)
    {
        var key = (Math.Min(a, b), Math.Max(a, b));
        if (!neighbors.Add(key)) return;
        var material = settings.Mechanics;
        edges.Add(new Edge { A = a, B = b, Rest = Vector3.Distance(Particles[a].Position, Particles[b].Position),
            Bend = bend, Surface = surface && !bend, Compliance = bend ? material.Bend : material.Stretch });
    }

    /// <summary>Fit the proxy to the current body before time starts. Rebase its rest shape and
    /// render pivot, so depenetration neither launches nor relocates the visible garment.</summary>
    public void FitToBody()
    {
        stepCapsules.Clear(); stepCapsules.AddRange(Capsules);
        foreach (var ring in Rings)
        {
            ring.ShapeFrameOffset = Quaternion.Identity;
            // The polygon's chords, rather than just its vertices, must clear a capsule.
            for (var i = 0; i < RingSize; i++)
                Particles[ring.Start + i].Position = ring.InitialCenter +
                    Vector3.Transform(Particles[ring.Start + i].Local, ring.InitialRotation) / MathF.Cos(MathF.PI / RingSize);
        }
        foreach (var p in Particles) p.BeforeStep = p.Position;
        for (var iteration = 0; iteration < 32; iteration++) Collide(settings.Thickness * scale);
        // Intersecting capsules can trap alternating projections in their overlap. Search
        // the nearby exterior only for those unresolved bind points; this is not a runtime force.
        foreach (var p in Particles)
        {
            if (BodyClear(p.Position)) continue;
            var start = p.Position;
            var found = false;
            for (var distance = 0.002f * scale; distance <= 2f * scale && !found; distance += 0.002f * scale)
                for (var x = -1; x <= 1 && !found; x++)
                    for (var y = -1; y <= 1 && !found; y++)
                        for (var z = -1; z <= 1 && !found; z++)
                        {
                            if (x == 0 && y == 0 && z == 0) continue;
                            var candidate = start + Vector3.Normalize(new Vector3(x, y, z)) * distance;
                            if (!BodyClear(candidate)) continue;
                            p.Position = candidate; found = true;
                        }
        }
        foreach (var ring in Rings)
        {
            var inverse = Quaternion.Inverse(ring.InitialRotation);
            ring.BindCenterOffset = Vector3.Transform(Center(Rings.IndexOf(ring)) - ring.InitialCenter, inverse);
            for (var i = 0; i < RingSize; i++)
            {
                var p = Particles[ring.Start + i];
                p.Local = Vector3.Transform(p.Position - ring.InitialCenter, inverse);
                p.PreviousTarget = p.Target = p.Position + Vector3.Transform(ring.AnchorOffset, ring.InitialRotation);
                p.Velocity = p.ContactNormal = p.ContactDisplacement = Vector3.Zero;
                p.ContactCorrection = p.MaxContactDepth = 0;
            }
            ring.ShapeFrameOffset = Quaternion.Normalize(Quaternion.Inverse(ResolveRotation(Rings.IndexOf(ring))) * ring.InitialRotation);
        }
        foreach (var edge in edges)
        {
            edge.Rest = Vector3.Distance(Particles[edge.A].Position, Particles[edge.B].Position);
            if (!edge.Surface) continue;
            // A chord inside the fitted capsule union is an internal brace, not a surface.
            foreach (var capsule in Capsules)
            {
                ClosestSegments(Particles[edge.A].Position, Particles[edge.B].Position,
                    capsule.A, capsule.B, out var s, out var t);
                if (Vector3.Distance(Vector3.Lerp(Particles[edge.A].Position, Particles[edge.B].Position, s),
                        Vector3.Lerp(capsule.A, capsule.B, t)) < capsule.Radius + settings.Thickness * scale - 0.00001f)
                { edge.Surface = false; break; }
            }
        }
        previousCapsules.Clear(); previousCapsules.AddRange(Capsules);
        Wake();
    }

    private bool BodyClear(Vector3 position)
    {
        foreach (var capsule in Capsules)
        {
            var axis = capsule.B - capsule.A;
            var t = Math.Clamp(Vector3.Dot(position - capsule.A, axis) / MathF.Max(axis.LengthSquared(), 1e-7f), 0, 1);
            if (Vector3.Distance(position, capsule.A + axis * t) < capsule.Radius + settings.Thickness * scale - 0.000001f * scale)
                return false;
        }
        return true;
    }

    public void SetTarget(int ringIndex, Vector3 center, Quaternion rotation)
    {
        var ring = Rings[ringIndex];
        ring.TargetRotation = rotation;
        for (var i = 0; i < RingSize; i++)
        {
            var p = Particles[ring.Start + i];
            p.Target = center + Vector3.Transform(p.Local + ring.AnchorOffset, rotation);
        }
    }

    public void SetFloor(int ringIndex, bool valid, Vector3 point, Vector3 normal)
    {
        var start = Rings[ringIndex].Start;
        for (var i = 0; i < RingSize; i++)
        {
            var p = Particles[start + i];
            if (p.HasFloor != valid || valid && (MathF.Abs(Vector3.Dot(point - p.FloorPoint, p.FloorNormal)) > 0.001f ||
                Vector3.DistanceSquared(p.FloorNormal, SafeNormal(normal, Vector3.UnitY)) > 0.0001f))
                Wake();
            p.HasFloor = valid;
            p.FloorPoint = point;
            p.FloorNormal = SafeNormal(normal, Vector3.UnitY);
        }
    }

    public void Wake() { Sleeping = false; restTime = 0; }

    public void ResetToTargets()
    {
        foreach (var p in Particles)
        {
            p.Position = p.PreviousTarget = p.Target;
            p.Velocity = Vector3.Zero;
        }
        accumulator = 0;
        previousCapsules.Clear();
        previousCapsules.AddRange(Capsules);
        foreach (var ring in Rings) ring.RenderRotation = ring.TargetRotation;
        Wake();
    }

    public void Advance(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0 || Particles.Count == 0) return;
        dt = MathF.Min(dt, 0.1f);
        var motion = 0f;
        foreach (var p in Particles)
            motion = MathF.Max(motion, Vector3.DistanceSquared(p.Target, p.PreviousTarget));
        if (motion > 9f * scale * scale) { ResetToTargets(); return; } // teleport, not a violent impulse
        if (motion > 0.00000025f * scale * scale) Wake();
        if (previousCapsules.Count != Capsules.Count)
        {
            previousCapsules.Clear(); previousCapsules.AddRange(Capsules); Wake();
        }
        for (var i = 0; i < Capsules.Count; i++)
            if (Vector3.DistanceSquared(Capsules[i].A, previousCapsules[i].A) > 0.00000025f * scale * scale ||
                Vector3.DistanceSquared(Capsules[i].B, previousCapsules[i].B) > 0.00000025f * scale * scale)
                Wake();
        if (settings.LayerCollision)
            foreach (var p in OtherGarments) if (p.Velocity.LengthSquared() > 0.0004f) { Wake(); break; }
        if (Sleeping) return;
        accumulator = MathF.Min(accumulator + dt, 0.1f);
        var steps = (int)((accumulator + 0.000001f) / Step);
        if (steps == 0) return; // retain old targets until a physics step consumes the movement
        var elapsed = steps * Step;
        accumulator = MathF.Max(0, accumulator - elapsed);
        for (var step = 0; step < steps; step++)
            SolveStep((step + 1f) / steps, elapsed);
        foreach (var p in Particles) p.PreviousTarget = p.Target;
        previousCapsules.Clear(); previousCapsules.AddRange(Capsules);
    }

    private void SolveStep(float fraction, float targetDt)
    {
        stepCapsules.Clear();
        for (var i = 0; i < Capsules.Count; i++)
        {
            var old = previousCapsules[i]; var current = Capsules[i];
            stepCapsules.Add(new Capsule(Vector3.Lerp(old.A, current.A, fraction),
                Vector3.Lerp(old.B, current.B, fraction), current.Radius, current.Velocity));
        }
        var material = settings.Mechanics;
        var w = 1f / material.Mass;
        var damping = MathF.Exp(-settings.Damping * Step);
        var thickness = settings.Thickness * scale;
        foreach (var p in Particles)
        {
            p.BeforeStep = p.Position;
            p.ContactNormal = p.SurfaceVelocity = p.ContactDisplacement = Vector3.Zero;
            p.ContactCorrection = p.MaxContactDepth = p.AnchorLambda = p.Tension = 0;
            p.Velocity = (p.Velocity - Vector3.UnitY * (9.81f * Step)) * damping;
            var carrier = (p.Target - p.PreviousTarget) / targetDt;
            // Cap relative sliding, not world velocity: rapid carrying must remain possible.
            var relative = p.Velocity - carrier;
            var limit = settings.SpeedLimit * scale;
            if (relative.LengthSquared() > limit * limit)
                p.Velocity = carrier + SafeNormal(relative, Vector3.Zero) * limit;
            p.Position += p.Velocity * Step;
        }
        foreach (var e in edges) e.Lambda = 0;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            foreach (var edge in edges)
            {
                var a = Particles[edge.A]; var b = Particles[edge.B];
                var delta = b.Position - a.Position;
                var length = delta.Length();
                if (length < 0.00001f) continue;
                var alpha = edge.Compliance / (Step * Step);
                var lambda = (-(length - edge.Rest) - alpha * edge.Lambda) / (2 * w + alpha);
                edge.Lambda += lambda;
                var correction = delta * (w * lambda / length);
                a.Position -= correction; b.Position += correction;
            }
            foreach (var p in Particles)
            {
                if (p.Range < 0) continue;
                var target = Vector3.Lerp(p.PreviousTarget, p.Target, fraction);
                var delta = p.Position - target;
                var distance = delta.Length();
                if (distance < 0.00001f) continue;
                // Slack tether: it only pulls when taut, never springs the garment back into its outfit.
                var c = distance - p.Range;
                if (c <= 0) { p.AnchorLambda = 0; continue; }
                var alpha = (0.000002f + (1 - settings.Firmness) * 0.0002f) / (Step * Step);
                var lambda = (-c - alpha * p.AnchorLambda) / (w + alpha);
                p.AnchorLambda += lambda;
                p.Position += delta * (lambda * w / distance);
                p.Tension = Math.Clamp(c / MathF.Max(0.02f * scale, p.Range), 0, 1);
            }
            Collide(thickness);
            if (iteration == 3 || iteration == 7)
            {
                CollideEdges(thickness);
                SeparateCloth(thickness);
                Collide(thickness); // self contact must not leave a node below ground or inside the body
            }
        }
        ContactCount = 0; MaxTension = 0;
        var maxSpeed = 0f;
        foreach (var p in Particles)
        {
            if (!Finite(p.Position) || Vector3.DistanceSquared(p.Position, p.Target) > 100f * scale * scale)
            {
                RecoveryCount++;
                ResetToTargets();
                return;
            }
            // Geometric overlap removal must not become an outward launch impulse.
            p.Velocity = (p.Position - p.BeforeStep - p.ContactDisplacement) / Step;
            if (p.ContactNormal.LengthSquared() > 1e-16f)
            {
                ContactCount++;
                var normal = SafeNormal(p.ContactNormal, Vector3.UnitY);
                var relative = p.Velocity - p.SurfaceVelocity;
                var vn = Vector3.Dot(relative, normal);
                var tangent = relative - normal * vn;
                var speed = tangent.Length();
                var impulse = p.ContactCorrection / Step + MathF.Max(0, -vn);
                // Static threshold > kinetic coefficient: settling can stick, but pulling frees it.
                var mu = p.ContactFriction;
                var remaining = speed <= mu * 1.3f * impulse ? 0 : MathF.Max(0, speed - mu * impulse);
                p.Velocity = p.SurfaceVelocity + normal * MathF.Max(0, vn) +
                    (speed > 0.000001f ? tangent * (remaining / speed) : Vector3.Zero);
            }
            var carrier = (p.Target - p.PreviousTarget) / targetDt;
            var slide = p.Velocity - carrier;
            var limit = settings.SpeedLimit * scale;
            if (slide.LengthSquared() > limit * limit)
                p.Velocity = carrier + SafeNormal(slide, Vector3.Zero) * limit;
            maxSpeed = MathF.Max(maxSpeed, p.Velocity.LengthSquared());
            MaxTension = MathF.Max(MaxTension, p.Tension);
        }
        restTime = maxSpeed < 0.0004f * scale * scale ? restTime + Step : 0;
        Sleeping = restTime > 1.5f;
        for (var i = 0; i < Rings.Count; i++)
        {
            var ring = Rings[i];
            var desired = ResolveRotation(i);
            ring.RenderRotation = Quaternion.Normalize(Quaternion.Slerp(ring.RenderRotation, desired, 1f - MathF.Exp(-35f * Step)));
        }
    }

    private void Collide(float thickness)
    {
        foreach (var p in Particles)
        {
            foreach (var capsule in stepCapsules)
            {
                var axis = capsule.B - capsule.A;
                var t = Math.Clamp(Vector3.Dot(p.Position - capsule.A, axis) /
                    MathF.Max(axis.LengthSquared(), 0.0000001f), 0, 1);
                var center = capsule.A + t * axis;
                var delta = p.Position - center;
                var depth = capsule.Radius + thickness - delta.Length();
                if (depth <= 0) continue;
                var normal = SafeNormal(delta, SafeNormal(p.BeforeStep - center, Vector3.UnitX));
                Contact(p, normal, depth, capsule.Velocity, settings.BodyFriction);
            }
            if (p.HasFloor)
            {
                var depth = thickness - Vector3.Dot(p.Position - p.FloorPoint, p.FloorNormal);
                if (depth > 0) Contact(p, p.FloorNormal, depth, Vector3.Zero, settings.GroundFriction);
            }
        }
    }

    private void CollideEdges(float thickness)
    {
        // A closed opening must not slip through the body between its sampled particles.
        // Resolve the closest points on each structural segment and capsule axis, distributing
        // the correction by barycentric weights. This also protects sleeves when elbows bend.
        foreach (var edge in edges)
        {
            if (!edge.Surface || edge.Bend) continue;
            var a = Particles[edge.A]; var b = Particles[edge.B];
            foreach (var capsule in stepCapsules)
            {
                ClosestSegments(a.Position, b.Position, capsule.A, capsule.B, out var s, out var t);
                var center = Vector3.Lerp(capsule.A, capsule.B, t);
                var delta = Vector3.Lerp(a.Position, b.Position, s) - center;
                var depth = capsule.Radius + thickness - delta.Length();
                if (depth <= 0) continue;
                var normal = SafeNormal(delta, SafeNormal(Vector3.Lerp(a.BeforeStep, b.BeforeStep, s) - center, Vector3.UnitX));
                var wa = 1 - s; var wb = s;
                var denominator = MathF.Max(wa * wa + wb * wb, 0.0001f);
                Contact(a, normal, depth * wa / denominator, capsule.Velocity, settings.BodyFriction);
                Contact(b, normal, depth * wb / denominator, capsule.Velocity, settings.BodyFriction);
            }
        }
    }

    // Closest parameters on two finite segments; handles parallel and degenerate segments.
    private static void ClosestSegments(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float s, out float t)
    {
        var u = b - a; var v = d - c; var r = a - c;
        var uu = u.LengthSquared(); var vv = v.LengthSquared(); var vr = Vector3.Dot(v, r);
        if (uu < 1e-10f) { s = 0; t = vv < 1e-10f ? 0 : Math.Clamp(vr / vv, 0, 1); return; }
        var ur = Vector3.Dot(u, r);
        if (vv < 1e-10f) { t = 0; s = Math.Clamp(-ur / uu, 0, 1); return; }
        var uv = Vector3.Dot(u, v); var denominator = uu * vv - uv * uv;
        s = denominator > 1e-10f ? Math.Clamp((uv * vr - ur * vv) / denominator, 0, 1) : 0;
        t = (uv * s + vr) / vv;
        if (t < 0) { t = 0; s = Math.Clamp(-ur / uu, 0, 1); }
        else if (t > 1) { t = 1; s = Math.Clamp((uv - ur) / uu, 0, 1); }
    }

    private static void Contact(Particle p, Vector3 normal, float depth, Vector3 velocity, float friction)
    {
        p.Position += normal * depth;
        p.ContactDisplacement += normal * depth;
        p.ContactNormal += normal * depth;
        if (depth >= p.MaxContactDepth)
        {
            p.MaxContactDepth = depth;
            p.SurfaceVelocity = velocity;
            p.ContactFriction = friction;
        }
        p.ContactCorrection += depth;
    }

    private void SeparateCloth(float radius)
    {
        if (settings.SelfCollision)
            for (var i = 0; i < Particles.Count; i++)
                for (var j = i + 1; j < Particles.Count; j++)
                {
                    if (i / RingSize == j / RingSize || neighbors.Contains((i, j))) continue;
                    var a = Particles[i]; var b = Particles[j];
                    var delta = b.Position - a.Position;
                    var length = delta.Length();
                    if (length >= radius * 2) continue;
                    var correction = SafeNormal(delta, Vector3.UnitX) * ((radius * 2 - length) * 0.5f);
                    a.Position -= correction; b.Position += correction;
                }
        if (settings.LayerCollision)
            foreach (var p in Particles)
                foreach (var other in OtherGarments)
                {
                    var delta = p.Position - other.Position;
                    var depth = radius + other.Radius - delta.Length();
                    if (depth > 0)
                        Contact(p, SafeNormal(delta, Vector3.UnitY), depth, other.Velocity, settings.BodyFriction);
                }
    }

    public Vector3 Center(int ringIndex)
    {
        var result = Vector3.Zero;
        for (var i = 0; i < RingSize; i++) result += Particles[Rings[ringIndex].Start + i].Position;
        return result / RingSize;
    }

    public Quaternion Rotation(int ringIndex) => Rings[ringIndex].RenderRotation;

    public Vector3 DrivenCenter(int ringIndex) => Center(ringIndex) -
        Vector3.Transform(Rings[ringIndex].BindCenterOffset, Rotation(ringIndex));

    private Quaternion ResolveRotation(int ringIndex)
    {
        var ring = Rings[ringIndex]; var center = Center(ringIndex);
        // Fit both in-plane axes using every node. A single colliding vertex must not
        // determine the rotation of the entire rendered bone and its descendants.
        var xSum = Vector3.Zero; var zHint = Vector3.Zero;
        for (var i = 0; i < RingSize; i++)
        {
            var angle = i * MathF.Tau / RingSize;
            var delta = Particles[ring.Start + i].Position - center;
            xSum += delta * MathF.Cos(angle);
            zHint += delta * MathF.Sin(angle);
        }
        var x = SafeNormal(xSum, Vector3.Transform(Vector3.UnitX, ring.RenderRotation));
        var cross = Vector3.Cross(zHint, x);
        // A nearly flattened opening has no reliable normal. Preserve its last frame instead of
        // flipping the rendered garment 180 degrees as the signed area crosses zero.
        if (cross.LengthSquared() < 0.000001f * scale * scale) return ring.RenderRotation;
        var y = SafeNormal(cross, Vector3.Transform(Vector3.UnitY, ring.RenderRotation));
        var z = SafeNormal(Vector3.Cross(x, y), Vector3.Transform(Vector3.UnitZ, ring.TargetRotation));
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)) * ring.ShapeFrameOffset);
    }

    public IEnumerable<(Vector3 A, Vector3 B)> DebugEdges()
    {
        foreach (var e in edges) if (!e.Bend) yield return (Particles[e.A].Position, Particles[e.B].Position);
    }
    public static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-12f && Finite(value) ? Vector3.Normalize(value) : fallback;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
