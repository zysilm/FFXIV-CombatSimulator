// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>
/// The skirt of a detached garment as a small cloth lattice. Each skirt bone chain (waistband, mid,
/// lower, plus a virtual hem tip) is a column of particles; neighbouring chains form the rows. The
/// waistband row is carried by whatever the garment's waist does; everything below it is free.
///
/// Cloth resists being stretched and nothing else, so: chain edges keep their length, ring and shear
/// edges only stop the rows from being pulled apart, and there is no bending stiffness and no rest
/// shape to return to. Particles and edges are kept out of the body's capsules and above the ground.
/// The solve is one-way (the skirt never pushes anything) and independent of any physics engine.
/// </summary>
public sealed class SkirtCloth
{
    /// <summary>Particles per chain: three skirt bones and the hem tip below the last one.</summary>
    public const int Tiers = 4;
    public const float StepSeconds = 1f / 60f;
    private const int MaxStepsPerAdvance = 4;
    private const int Iterations = 8;
    private const float ParticleRadius = 0.01f;
    private const float RingSlack = 1.05f;      // rows may be pulled this far past their captured spacing
    private const float ShearSlack = 1.10f;
    private const float ShearStiffness = 0.3f;
    private const float MinSeparation = 0.03f;  // keeps gathered chains from collapsing onto one point
    private const float AirDrag = 3f;           // 1/s
    private const float GroundGrip = 0.4f;      // tangential speed kept per step while resting on the ground
    private const float MaxSpeed = 12f;
    private const float TeleportDistance = 0.5f; // an anchor moving farther than this in one step carries the cloth

    public readonly record struct Capsule(Vector3 A, Vector3 B, float Radius);

    /// <summary>One chain at the moment the cloth is created, top to bottom, in world space.</summary>
    public readonly record struct ChainRest(Vector3 Waist, Vector3 Mid, Vector3 Lower);

    private readonly int chainCount;
    private readonly Vector3[] position;
    private readonly Vector3[] previous;
    private readonly Vector3[] velocity;
    private readonly bool[] pinned;
    private readonly Quaternion[] restFrame;
    private readonly bool[] restFrameHasTangent;
    private readonly Vector3[] restDown;
    private readonly List<(int A, int B, float Length)> chainEdges = new();
    private readonly List<(int A, int B, float Max, float Stiffness)> maxEdges = new();
    private readonly List<(int A, int B)> separationPairs = new();
    private float accumulator;

    public int ChainCount => chainCount;

    /// <summary>
    /// Chains are given in ring order (each next to the previous, the last next to the first when
    /// <paramref name="closedRing"/>). All particles start with <paramref name="initialVelocity"/>.
    /// </summary>
    public SkirtCloth(ReadOnlySpan<ChainRest> chains, bool closedRing, Vector3 initialVelocity)
    {
        chainCount = chains.Length;
        var count = chainCount * Tiers;
        position = new Vector3[count];
        previous = new Vector3[count];
        velocity = new Vector3[count];
        pinned = new bool[count];
        restFrame = new Quaternion[count];
        restFrameHasTangent = new bool[count];
        restDown = new Vector3[count];

        for (var c = 0; c < chainCount; c++)
        {
            var rest = chains[c];
            var lowerEdge = rest.Lower - rest.Mid;
            position[Index(c, 0)] = rest.Waist;
            position[Index(c, 1)] = rest.Mid;
            position[Index(c, 2)] = rest.Lower;
            position[Index(c, 3)] = rest.Lower + lowerEdge; // the hem continues the last segment
            pinned[Index(c, 0)] = true;
            for (var t = 0; t < Tiers; t++)
            {
                previous[Index(c, t)] = position[Index(c, t)];
                velocity[Index(c, t)] = initialVelocity;
            }
            for (var t = 0; t + 1 < Tiers; t++)
                chainEdges.Add((Index(c, t), Index(c, t + 1), Vector3.Distance(position[Index(c, t)], position[Index(c, t + 1)])));
        }

        var links = closedRing && chainCount > 2 ? chainCount : chainCount - 1;
        for (var link = 0; link < links; link++)
        {
            var c = link;
            var n = (link + 1) % chainCount;
            for (var t = 1; t < Tiers; t++)
            {
                var a = Index(c, t);
                var b = Index(n, t);
                maxEdges.Add((a, b, Vector3.Distance(position[a], position[b]) * RingSlack, 1f));
                separationPairs.Add((a, b));
                // Shear: this chain to the next row of its neighbour, both ways.
                var d1 = Index(n, t - 1);
                var d2 = Index(c, t - 1);
                maxEdges.Add((a, d1, Vector3.Distance(position[a], position[d1]) * ShearSlack, ShearStiffness));
                maxEdges.Add((b, d2, Vector3.Distance(position[b], position[d2]) * ShearSlack, ShearStiffness));
            }
        }

        for (var c = 0; c < chainCount; c++)
            for (var t = 0; t < Tiers; t++)
            {
                restDown[Index(c, t)] = Down(c, t);
                restFrameHasTangent[Index(c, t)] = TryFrame(c, t, out restFrame[Index(c, t)]);
            }
    }

    /// <summary>
    /// Particle position for rendering: between the last two fixed steps, by how far the frame clock is
    /// into the next one. The cloth is therefore shown up to one step late, and never jumps at 60 Hz
    /// when the game renders faster.
    /// </summary>
    public Vector3 Position(int chain, int tier) => Shown(Index(chain, tier));

    private Vector3 Shown(int i) => Vector3.Lerp(previous[i], position[i], Math.Clamp(accumulator / StepSeconds, 0f, 1f));

    /// <summary>
    /// World rotation that takes a bone from its orientation when the cloth was created to where the
    /// cloth has carried it: the frame of its down-chain edge and the ring direction now, against the
    /// same frame at creation. Uses the swing of the edge alone where the ring direction is unusable.
    /// </summary>
    public Quaternion RotationDelta(int chain, int tier)
    {
        var i = Index(chain, tier);
        if (restFrameHasTangent[i] && TryFrame(chain, tier, out var now))
            return Quaternion.Normalize(now * Quaternion.Inverse(restFrame[i]));
        return FromTo(restDown[i], Down(chain, tier));
    }

    /// <summary>
    /// Advance by a frame. <paramref name="waist"/> holds the world position of each chain's waistband
    /// particle; <paramref name="body"/> the capsules to stay out of; the ground is a horizontal plane.
    /// </summary>
    public void Advance(float dt, ReadOnlySpan<Vector3> waist, ReadOnlySpan<Capsule> body, float groundY, Vector3 gravity)
    {
        if (waist.Length < chainCount || !float.IsFinite(dt) || dt <= 0f) return;
        accumulator = MathF.Min(accumulator + dt, StepSeconds * MaxStepsPerAdvance);
        var steps = (int)(accumulator / StepSeconds);
        if (steps <= 0) return;

        accumulator -= steps * StepSeconds;
        // Move the waistband to its target in equal parts so a fast waist does not yank the cloth in one step.
        Span<Vector3> start = stackalloc Vector3[chainCount];
        for (var c = 0; c < chainCount; c++) start[c] = position[Index(c, 0)];
        for (var s = 1; s <= steps; s++)
        {
            var teleport = Vector3.Zero;
            var teleported = false;
            for (var c = 0; c < chainCount; c++)
            {
                var target = Vector3.Lerp(start[c], waist[c], s / (float)steps);
                if (!float.IsFinite(target.LengthSquared())) continue;
                var delta = target - position[Index(c, 0)];
                if (delta.Length() > TeleportDistance) { teleport += delta / chainCount; teleported = true; }
                previous[Index(c, 0)] = position[Index(c, 0)];
                position[Index(c, 0)] = target;
            }
            if (teleported)
            {
                Shift(teleport);
                for (var c = 0; c < chainCount; c++) previous[Index(c, 0)] = position[Index(c, 0)];
            }
            Step(body, groundY, gravity);
        }
    }

    private void Shift(Vector3 delta)
    {
        for (var i = 0; i < position.Length; i++)
        {
            if (pinned[i]) continue;
            position[i] += delta;
            previous[i] += delta;
        }
    }

    private void Step(ReadOnlySpan<Capsule> body, float groundY, Vector3 gravity)
    {
        const float dt = StepSeconds;
        var drag = MathF.Exp(-AirDrag * dt);
        for (var i = 0; i < position.Length; i++)
        {
            if (pinned[i]) continue;
            previous[i] = position[i];
            velocity[i] = (velocity[i] + gravity * dt) * drag;
            position[i] += velocity[i] * dt;
        }

        var floor = groundY + ParticleRadius;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            foreach (var (a, b, max, stiffness) in maxEdges) SolveDistance(a, b, max, stiffness, onlyWhenLonger: true);
            foreach (var (a, b) in separationPairs) SolveMinimum(a, b, MinSeparation);

            foreach (var capsule in body)
            {
                for (var i = 0; i < position.Length; i++)
                    if (!pinned[i]) position[i] = PushOutOfCapsule(position[i], capsule);
                foreach (var (a, b, _) in chainEdges) PushEdgeOutOfCapsule(a, b, capsule);
            }

            if (float.IsFinite(floor))
                for (var i = 0; i < position.Length; i++)
                    if (!pinned[i] && position[i].Y < floor) position[i].Y = floor;

            // Last, so length is what survives when constraints disagree.
            foreach (var (a, b, rest) in chainEdges) SolveDistance(a, b, rest, 1f, onlyWhenLonger: false);
        }

        for (var i = 0; i < position.Length; i++)
        {
            if (pinned[i]) continue;
            if (float.IsFinite(floor) && position[i].Y < floor) position[i].Y = floor;
            if (!float.IsFinite(position[i].LengthSquared())) { position[i] = previous[i]; velocity[i] = Vector3.Zero; continue; }
            var v = (position[i] - previous[i]) / dt;
            if (float.IsFinite(floor) && position[i].Y <= floor + 1e-4f)
                v = new Vector3(v.X * GroundGrip, MathF.Max(0f, v.Y), v.Z * GroundGrip);
            var speed = v.Length();
            if (speed > MaxSpeed) v *= MaxSpeed / speed;
            velocity[i] = v;
        }
    }

    private void SolveDistance(int a, int b, float target, float stiffness, bool onlyWhenLonger)
    {
        var wa = pinned[a] ? 0f : 1f;
        var wb = pinned[b] ? 0f : 1f;
        if (wa + wb <= 0f) return;
        var delta = position[b] - position[a];
        var length = delta.Length();
        if (length < 1e-7f || (onlyWhenLonger && length <= target)) return;
        var correction = delta * ((length - target) / length * stiffness / (wa + wb));
        position[a] += correction * wa;
        position[b] -= correction * wb;
    }

    private void SolveMinimum(int a, int b, float minimum)
    {
        var wa = pinned[a] ? 0f : 1f;
        var wb = pinned[b] ? 0f : 1f;
        if (wa + wb <= 0f) return;
        var delta = position[b] - position[a];
        var length = delta.Length();
        if (length >= minimum || length < 1e-7f) return;
        var correction = delta * ((length - minimum) / length / (wa + wb));
        position[a] += correction * wa;
        position[b] -= correction * wb;
    }

    private static Vector3 PushOutOfCapsule(Vector3 point, in Capsule capsule)
    {
        var closest = ClosestOnSegment(point, capsule.A, capsule.B);
        var offset = point - closest;
        var distance = offset.Length();
        var reach = capsule.Radius + ParticleRadius;
        if (distance >= reach) return point;
        if (distance < 1e-6f) return point; // dead centre: no direction to leave by; the edge test or a later step resolves it
        return closest + offset * (reach / distance);
    }

    private void PushEdgeOutOfCapsule(int a, int b, in Capsule capsule)
    {
        ClosestBetweenSegments(position[a], position[b], capsule.A, capsule.B, out var s, out var onEdge, out var onAxis);
        var offset = onEdge - onAxis;
        var distance = offset.Length();
        var reach = capsule.Radius + ParticleRadius;
        if (distance >= reach || distance < 1e-6f) return;
        // Move the edge's closest point out to the surface, sharing the correction by how near each end is.
        var wa = pinned[a] ? 0f : 1f - s;
        var wb = pinned[b] ? 0f : s;
        var weight = wa * (1f - s) + wb * s;
        if (weight < 1e-6f) return;
        var push = offset * ((reach - distance) / distance / weight);
        position[a] += push * wa;
        position[b] += push * wb;
    }

    private static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-10f) return a;
        return a + ab * Math.Clamp(Vector3.Dot(point - a, ab) / lengthSq, 0f, 1f);
    }

    // Closest points between segments p1-q1 and p2-q2; s is the parameter along the first.
    private static void ClosestBetweenSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out float s, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = d1.LengthSquared();
        var e = d2.LengthSquared();
        var f = Vector3.Dot(d2, r);
        float t;
        if (a <= 1e-10f && e <= 1e-10f) { s = 0f; t = 0f; }
        else if (a <= 1e-10f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-10f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denominator = a * e - b * b;
                s = denominator > 1e-10f ? Math.Clamp((b * f - c * e) / denominator, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    private static int Index(int chain, int tier) => chain * Tiers + tier;

    // Direction of the chain at a tier: toward the next particle, or along the last edge for the tip.
    private Vector3 Down(int chain, int tier)
    {
        var from = tier + 1 < Tiers ? tier : tier - 1;
        return Shown(Index(chain, from + 1)) - Shown(Index(chain, from));
    }

    // Orthonormal frame from the down-chain edge and the direction across the ring at this tier.
    private bool TryFrame(int chain, int tier, out Quaternion frame)
    {
        frame = Quaternion.Identity;
        if (chainCount < 2) return false;
        var down = Down(chain, tier);
        if (down.LengthSquared() < 1e-10f) return false;
        var y = Vector3.Normalize(down);
        var left = Shown(Index((chain + chainCount - 1) % chainCount, tier));
        var right = Shown(Index((chain + 1) % chainCount, tier));
        var across = right - left;
        across -= y * Vector3.Dot(across, y);
        if (across.LengthSquared() < 1e-8f) return false;
        var x = Vector3.Normalize(across);
        var z = Vector3.Cross(x, y);
        frame = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f)));
        return true;
    }

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) return Quaternion.Identity;
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            var any = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(any), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.Acos(dot));
    }
}
