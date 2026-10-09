// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.Hair;

/// <summary>Bounded, one-way XPBD guide tree. Owns no native objects or body forces.
/// Parents precede children; negative parents are scalp attachments. Rest offsets
/// are transported down the deformed tree, retaining curvature without locking it.</summary>
internal sealed class FlexibleHairSolver
{
    public readonly int[] Parents;
    public readonly Vector3[] Rest;
    public readonly Vector3[] Positions;
    public readonly Vector3[] Previous;
    public readonly float[] ContactRadii;
    private readonly Vector3[] velocities, before, bendLambda, predictedVelocity, contactNormals, contactVelocities;
    private readonly byte[] contactCounts;
    private readonly Vector3[] bodyVelocities = new Vector3[64];
    private readonly float[] lengths, distanceLambda, inverseMass;
    private readonly int[] depths;
    private readonly Quaternion[] frames;
    private readonly float[] contactAllowances;
    private readonly HairContactCapsule[] previousContacts = new HairContactCapsule[64];
    private int previousContactCount;
    private Vector3 headPosition, previousHeadPosition;
    private Quaternion headRotation, previousHeadRotation;
    public bool Awake { get; private set; } = true;
    private float quietSeconds;
    public int Count => Positions.Length;

    public FlexibleHairSolver(int[] parents, Vector3[] rest, float mass,
        Vector3 head, Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity)
    {
        if (parents.Length != rest.Length || parents.Length > 512 || !Finite(head) || !Finite(rotation))
            throw new ArgumentException("Invalid hair guide tree");
        Parents = (int[])parents.Clone(); Rest = (Vector3[])rest.Clone();
        int n = rest.Length;
        Positions = new Vector3[n]; Previous = new Vector3[n]; velocities = new Vector3[n];
        ContactRadii = new float[n];
        before = new Vector3[n]; bendLambda = new Vector3[n]; lengths = new float[n];
        predictedVelocity = new Vector3[n]; contactNormals = new Vector3[n * 4]; contactVelocities = new Vector3[n * 4];
        contactCounts = new byte[n];
        distanceLambda = new float[n]; inverseMass = new float[n]; depths = new int[n]; frames = new Quaternion[n];
        contactAllowances = new float[n * 64];
        headPosition = previousHeadPosition = head;
        headRotation = previousHeadRotation = Quaternion.Normalize(rotation);
        if (!Finite(linearVelocity)) linearVelocity = Vector3.Zero;
        if (!Finite(angularVelocity)) angularVelocity = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            int p = parents[i];
            if (p < -1 || p >= i || !Finite(rest[i])) throw new ArgumentException("Hair parents must precede children");
            Positions[i] = Previous[i] = head + Vector3.Transform(rest[i], headRotation);
            velocities[i] = linearVelocity + Vector3.Cross(angularVelocity, Positions[i] - head);
            inverseMass[i] = p < 0 ? 0 : 1 / (float.IsFinite(mass) ? Math.Clamp(mass, .002f, .1f) : .02f);
            lengths[i] = p < 0 ? 0 : Vector3.Distance(rest[i], rest[p]);
            depths[i] = p < 0 ? 0 : depths[p] + 1;
        }
    }

    public void Step(float dt, Vector3 head, Quaternion rotation, float bendCompliance,
        float damping, ReadOnlySpan<HairContactPlane> ground, ReadOnlySpan<HairContactCapsule> body,
        float radius)
    {
        if (!(dt > 0 && dt <= .05f) || !Finite(head) || !Finite(rotation)) return;
        rotation = Quaternion.Normalize(rotation);
        bool headMoved = Vector3.DistanceSquared(head, headPosition) > 1e-8f ||
            MathF.Abs(Quaternion.Dot(rotation, headRotation)) < .999999f;
        Positions.CopyTo(Previous, 0);
        previousHeadPosition = headPosition; previousHeadRotation = headRotation;
        // A discontinuous teleport/redraw carries the tree instead of injecting
        // an unbounded impulse. Ordinary motion preserves world-space inertia.
        if (Vector3.DistanceSquared(head, headPosition) > 1f || MathF.Abs(Quaternion.Dot(rotation, headRotation)) < .7071f)
        {
            var delta = rotation * Quaternion.Inverse(headRotation);
            for (int i = 0; i < Count; i++)
            {
                Positions[i] = head + Vector3.Transform(Positions[i] - headPosition, delta);
                Previous[i] = Positions[i]; velocities[i] = Vector3.Zero;
            }
            previousHeadPosition = head; previousHeadRotation = rotation;
        }
        headPosition = head; headRotation = rotation;
        bool contactMoved = body.Length != previousContactCount;
        for (int j = 0; j < Math.Min(64, body.Length); j++)
        {
            bodyVelocities[j] = j < previousContactCount ?
                Limit(((body[j].A + body[j].B) - (previousContacts[j].A + previousContacts[j].B)) / (2 * dt), 8f) : Vector3.Zero;
            contactMoved |= Vector3.DistanceSquared(body[j].A, previousContacts[j].A) > 1e-8f ||
                Vector3.DistanceSquared(body[j].B, previousContacts[j].B) > 1e-8f;
            previousContacts[j] = body[j];
        }
        previousContactCount = body.Length;
        if (!Awake && !headMoved && !contactMoved) return;
        Awake = true;
        Array.Clear(contactCounts);
        float drag = MathF.Exp(-(float.IsFinite(damping) ? Math.Clamp(damping, .05f, 15f) : 2.2f) * dt);
        for (int i = 0; i < Count; i++)
        {
            before[i] = Positions[i];
            if (Parents[i] < 0) Positions[i] = head + Vector3.Transform(Rest[i], rotation);
            else
            {
                predictedVelocity[i] = velocities[i] * drag + new Vector3(0, -9.81f * dt, 0);
                Positions[i] += predictedVelocity[i] * dt;
            }
        }
        Array.Clear(distanceLambda); Array.Clear(bendLambda);
        float compliance = float.IsFinite(bendCompliance) ? Math.Clamp(bendCompliance, .0001f, 1f) : .02f;
        for (int i = 0; i < Count; i++)
            for (int j = 0; j < Math.Min(64, body.Length); j++)
                contactAllowances[i * 64 + j] = body[j].AllowRestOverlap ?
                    body[j].InitialAllowance(Rest[i], head, rotation, MathF.Max(radius, ContactRadii[i])) : 0;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            UpdateFrames(rotation);
            for (int i = 0; i < Count; i++)
            {
                int p = Parents[i]; if (p < 0) continue;
                var restEdge = Rest[i] - Rest[p];
                var desired = Vector3.Transform(restEdge, frames[p]);
                // A compliant vector constraint retains local rest curvature and
                // twist frame. Distal edges soften progressively, never fade out.
                float lengthFactor = MathF.Max(.25f, lengths[i] * lengths[i] / (.08f * .08f));
                float alpha = compliance * (1 + depths[i] * depths[i]) * lengthFactor / (dt * dt);
                var c = Positions[i] - Positions[p] - desired;
                var dl = (-c - alpha * bendLambda[i]) / (inverseMass[i] + inverseMass[p] + alpha);
                bendLambda[i] += dl;
                Positions[i] += inverseMass[i] * dl; Positions[p] -= inverseMass[p] * dl;
            }
            // Solve lengths after bending so shape guidance cannot stretch hair.
            for (int i = Count - 1; i >= 0; i--)
            {
                int p = Parents[i]; if (p < 0) continue;
                var edge = Positions[i] - Positions[p]; float length = edge.Length();
                if (length < 1e-7f) continue;
                float alpha = 1e-8f / (dt * dt);
                float dl = (-(length - lengths[i]) - alpha * distanceLambda[i]) /
                    (inverseMass[i] + inverseMass[p] + alpha);
                distanceLambda[i] += dl;
                var correction = edge * (dl / length);
                Positions[i] += inverseMass[i] * correction; Positions[p] -= inverseMass[p] * correction;
            }
            for (int i = 0; i < Count; i++)
            {
                if (Parents[i] < 0) continue; // scalp attachment must stay exact
                float contactRadiusForPoint = MathF.Max(radius, ContactRadii[i]);
                if (i < ground.Length && ground[i].Valid)
                {
                    var plane = ground[i];
                    var horizontal = Positions[i] - plane.Point; horizontal.Y = 0;
                    if (horizontal.LengthSquared() < .04f)
                    {
                        float penetration = contactRadiusForPoint - Vector3.Dot(Positions[i] - plane.Point, plane.Normal);
                        if (penetration > 0)
                        {
                            Positions[i] += plane.Normal * MathF.Min(penetration, .004f);
                            RecordContact(i, plane.Normal, Vector3.Zero);
                        }
                    }
                }
                for (int j = 0; j < Math.Min(64, body.Length); j++)
                {
                    var capsule = body[j];
                    // Rest overlap allowance avoids popping roots through a mod
                    // scalp at activation; only newly increased overlap is removed.
                    float allowance = contactAllowances[i * 64 + j];
                    var nearest = capsule.Nearest(Positions[i]);
                    var delta = Positions[i] - nearest; float length = delta.Length();
                    float contactRadius = capsule.Radius + contactRadiusForPoint - allowance;
                    if (length < contactRadius)
                    {
                        var normal = length > 1e-6f ? delta / length : Vector3.UnitY;
                        Positions[i] += normal * MathF.Min(contactRadius - length, .004f);
                        RecordContact(i, normal, bodyVelocities[j]);
                    }
                }
            }
            // Endpoints alone miss a long tuft crossing the torso. Resolve the
            // swept guide segment as well, distributing correction only to free
            // guide endpoints; body envelopes are immutable inputs.
            for (int i = 0; i < Count; i++)
            {
                int p = Parents[i]; if (p < 0) continue;
                for (int j = 0; j < Math.Min(64, body.Length); j++)
                {
                    var capsule = body[j];
                    float t = capsule.ClosestGuideParameter(Positions[p], Positions[i]);
                    var point = Vector3.Lerp(Positions[p], Positions[i], t);
                    var delta = point - capsule.Nearest(point); float separation = delta.Length();
                    float shell = capsule.Radius + MathF.Max(radius, ContactRadii[i]);
                    // The same rest-profile allowance must apply to edges and
                    // endpoints. Otherwise the edge solver pushes a correctly
                    // attached fringe away while endpoint contacts allow it.
                    if (capsule.AllowRestOverlap)
                    {
                        var localRest = Vector3.Lerp(Rest[p], Rest[i], t);
                        shell -= capsule.InitialAllowance(localRest, head, rotation, MathF.Max(radius, ContactRadii[i]));
                    }
                    if (separation >= shell) continue;
                    float wp = inverseMass[p], wi = inverseMass[i];
                    float denominator = wp * (1 - t) * (1 - t) + wi * t * t;
                    if (denominator < 1e-8f) continue;
                    var normal = separation > 1e-6f ? delta / separation :
                        Vector3.Cross(Positions[i] - Positions[p], capsule.B - capsule.A);
                    if (normal.LengthSquared() < 1e-8f) normal = Vector3.UnitZ;
                    normal = Vector3.Normalize(normal);
                    // Near an immovable scalp endpoint a deeply intersecting
                    // envelope cannot be satisfied; cap each local correction.
                    var correction = normal * MathF.Min(shell - separation, .02f) / denominator;
                    var parentCorrection = correction * wp * (1 - t);
                    var childCorrection = correction * wi * t;
                    // The point-space cap above is insufficient near a fixed
                    // root: endpoint movement grows as 1/t. Bound the actual
                    // endpoint corrections together, preserving their ratio.
                    float largest = MathF.Max(parentCorrection.Length(), childCorrection.Length());
                    float scale = largest > .004f ? .004f / largest : 1;
                    Positions[p] += parentCorrection * scale; Positions[i] += childCorrection * scale;
                    if (wp > 0) RecordContact(p, normal, bodyVelocities[j]);
                    if (wi > 0) RecordContact(i, normal, bodyVelocities[j]);
                }
            }
        }
        float maxSpeed = 0;
        for (int i = 0; i < Count; i++)
        {
            if (!Finite(Positions[i])) Positions[i] = head + Vector3.Transform(Rest[i], rotation);
            velocities[i] = (Positions[i] - before[i]) / dt;
            if (Parents[i] >= 0)
            {
                int p = Parents[i];
                var edge = Positions[i] - Positions[p];
                if (edge.LengthSquared() > 1e-10f)
                {
                    var axis = Vector3.Normalize(edge);
                    var relative = velocities[i] - velocities[p];
                    var tangent = relative - axis * Vector3.Dot(relative, axis);
                    float lengthFactor = MathF.Max(.25f, lengths[i] * lengths[i] / (.08f * .08f));
                    float effectiveCompliance = compliance * (1 + depths[i] * depths[i]) * lengthFactor;
                    // Critical bending damping follows the same compliance/mass
                    // as the guide constraint. Air drag alone leaves a springy
                    // underdamped strand, especially for short, stiff guides.
                    float rate = Math.Clamp(2 * MathF.Sqrt((inverseMass[i] + inverseMass[p]) / effectiveCompliance), 4, 120);
                    velocities[i] = velocities[p] + tangent * MathF.Exp(-rate * dt);
                }
                for (int contact = 0; contact < contactCounts[i]; contact++)
                {
                    int index = i * 4 + contact;
                    var normal = contactNormals[index]; var surfaceVelocity = contactVelocities[index];
                    float normalSpeed = Vector3.Dot(velocities[i] - surfaceVelocity, normal);
                    float allowedOutward = MathF.Max(0, Vector3.Dot(predictedVelocity[i] - surfaceVelocity, normal));
                    // Position depenetration is not a bounce impulse. Preserve
                    // intentional departure, reject inward motion and any new
                    // outward velocity fabricated by contact correction.
                    float accepted = Math.Clamp(normalSpeed, 0, allowedOutward);
                    velocities[i] += normal * (accepted - normalSpeed);
                }
            }
            float speed = velocities[i].Length();
            if (speed > 8) velocities[i] *= 8 / speed;
            if (Parents[i] >= 0) maxSpeed = MathF.Max(maxSpeed, speed);
        }
        quietSeconds = !headMoved && !contactMoved && maxSpeed < .008f ? quietSeconds + dt : 0;
        Awake = quietSeconds < .75f;
    }

    private void RecordContact(int point, Vector3 normal, Vector3 surfaceVelocity)
    {
        for (int j = 0; j < contactCounts[point]; j++)
            if (Vector3.Dot(contactNormals[point * 4 + j], normal) > .95f) return;
        if (contactCounts[point] == 4) return;
        int index = point * 4 + contactCounts[point]++;
        contactNormals[index] = normal; contactVelocities[index] = surfaceVelocity;
    }
    private static Vector3 Limit(Vector3 value, float maximum)
    {
        if (!Finite(value)) return Vector3.Zero;
        float length = value.Length(); return length > maximum ? value * (maximum / length) : value;
    }

    private void UpdateFrames(Quaternion rootRotation)
    {
        for (int i = 0; i < Count; i++)
        {
            int p = Parents[i];
            frames[i] = p < 0 ? rootRotation :
                FromTo(Vector3.Transform(Rest[i] - Rest[p], frames[p]), Positions[i] - Positions[p]) * frames[p];
        }
    }
    public Vector3 InterpolatedPosition(int i, float alpha) => Vector3.Lerp(Previous[i], Positions[i], alpha);
    public Vector3 InterpolatedHead(float alpha) => Vector3.Lerp(previousHeadPosition, headPosition, alpha);
    public Quaternion InterpolatedHeadRotation(float alpha) => Quaternion.Slerp(previousHeadRotation, headRotation, alpha);
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared() > 1e-8f;
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Quaternion.Identity;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot > .99999f) return Quaternion.Identity;
        if (dot < -.99999f)
        {
            var axis = Vector3.Cross(from, MathF.Abs(from.X) < .8f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }
}

internal readonly record struct HairContactPlane(bool Valid, Vector3 Point, Vector3 Normal);
internal readonly record struct HairContactCapsule(Vector3 A, Vector3 B, float Radius, bool AllowRestOverlap = false)
{
    public float ClosestGuideParameter(Vector3 from, Vector3 to)
    {
        var u = to - from; var v = B - A; var w = from - A;
        float a = Vector3.Dot(u, u), b = Vector3.Dot(u, v), c = Vector3.Dot(v, v);
        float d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
        if (a < 1e-10f) return 0;
        if (c < 1e-10f) return Math.Clamp(-d / a, 0, 1);
        float denominator = a * c - b * b;
        float t = denominator > 1e-10f ? Math.Clamp((b * e - c * d) / denominator, 0, 1) : 0;
        float s = (b * t + e) / c;
        if (s < 0) t = Math.Clamp(-d / a, 0, 1);
        else if (s > 1) t = Math.Clamp((b - d) / a, 0, 1);
        return t;
    }
    public Vector3 Nearest(Vector3 point)
    {
        var edge = B - A; float length = edge.LengthSquared();
        return A + edge * (length > 1e-10f ? Math.Clamp(Vector3.Dot(point - A, edge) / length, 0, 1) : 0);
    }
    public float InitialAllowance(Vector3 localRest, Vector3 head, Quaternion rotation, float radius)
    {
        var rest = head + Vector3.Transform(localRest, rotation);
        return MathF.Max(0, Radius + radius - Vector3.Distance(rest, Nearest(rest)));
    }
}
