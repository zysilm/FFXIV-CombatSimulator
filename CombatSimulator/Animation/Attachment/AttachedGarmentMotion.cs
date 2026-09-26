using System;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>Bounded offsets in the live torso frame, never independent world-space cloth bodies.
/// The source pose supplies all bone rotations. Skirt columns share one small secondary offset.</summary>
public sealed class AttachedGarmentMotion
{
    private Vector3 slip, velocity, sway;
    public Vector3 LocalSlip => slip;
    public Vector3 LocalSway => sway;

    public void Advance(float dt, Quaternion bodyFrame, AttachmentSettings settings, bool upperBody = false)
    {
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(bodyFrame.LengthSquared()) || bodyFrame.LengthSquared() < 1e-8f) return;
        dt = MathF.Min(dt, .1f);
        var gravity = Vector3.Transform(-Vector3.UnitY, Quaternion.Inverse(Quaternion.Normalize(bodyFrame)));
        if (upperBody)
        {
            AdvanceUpperBody(dt, gravity, settings);
            return;
        }
        var limit = settings.SlipDistance;
        if (limit <= 0) { slip = velocity = sway = Vector3.Zero; return; }
        // Subdivision bounds error across frame rates. No contact projection, spring network or
        // inferred rest lengths participate in the clothing pose.
        var steps = Math.Max(1, (int)MathF.Ceiling(dt * 240));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var targetVelocity = gravity * (settings.SpeedLimit / (1 + 3 * settings.BodyFriction));
            var response = 4f / (1 + settings.Damping * .25f);
            velocity = Vector3.Lerp(velocity, targetVelocity, 1 - MathF.Exp(-response * h));
            var next = slip + velocity * h;
            slip = LimitSlide(next, limit, settings.LateralDistance);
            // Remove outward velocity at the travel limit; no bounce or stored spring energy.
            var correction = next - slip;
            if (correction.LengthSquared() > 1e-14f)
            {
                var normal = Vector3.Normalize(correction);
                velocity -= normal * MathF.Max(0, Vector3.Dot(velocity, normal));
            }
            var swayLimit = settings.Template == GarmentTemplate.Rigid ? 0 :
                MathF.Min(limit * .05f, settings.SwayDistance) * (1 - settings.Firmness);
            var targetSway = new Vector3(gravity.X, 0, gravity.Z) * swayLimit;
            sway = Vector3.Lerp(sway, targetSway, 1 - MathF.Exp(-6f / (1 + settings.Damping) * h));
            sway = Limit(sway, swayLimit);
        }
    }

    public Vector3 Offset(Quaternion bodyFrame, float scale, float multiplier, bool skirt,
        AttachmentSettings settings, bool upperBody = false)
    {
        if (upperBody)
        {
            var distance = MathF.Min(settings.SlipDistance, .08f);
            var travel = Math.Clamp(-slip.Y * Math.Clamp(multiplier, 0, 2), 0, distance);
            return Vector3.Transform(-Vector3.UnitY * (travel * scale), bodyFrame);
        }
        // Sway uses the same finite offset for the entire skirt, never an accumulated per-link term.
        var bound = settings.SlipDistance * Math.Clamp(multiplier, 0, 2);
        var local = LimitSlide(slip * multiplier + (skirt ? sway : Vector3.Zero), bound,
            settings.LateralDistance * multiplier);
        return Vector3.Transform(local * scale, bodyFrame);
    }

    public void AdvanceTrousers(float dt, Quaternion bodyFrame, AttachmentSettings settings)
    {
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(bodyFrame.LengthSquared()) || bodyFrame.LengthSquared() < 1e-8f) return;
        var gravity = Vector3.Transform(-Vector3.UnitY, Quaternion.Inverse(Quaternion.Normalize(bodyFrame)));
        AdvanceUpperBody(MathF.Min(dt, .1f), gravity, settings, settings.SlipDistance);
    }

    private void AdvanceUpperBody(float dt, Vector3 gravity, AttachmentSettings settings, float? travelLimit = null)
    {
        var limit = travelLimit ?? MathF.Min(settings.SlipDistance, .08f);
        if (limit <= 0) { slip = velocity = sway = Vector3.Zero; return; }
        // A retained top can loosen toward its hem, never migrate toward the neck. Gravity
        // controls the rate only; inversion cannot reverse existing slide or reset its progress.
        var speed = MathF.Max(0, -velocity.Y);
        var travel = Math.Clamp(-slip.Y, 0, limit);
        var targetSpeed = MathF.Max(0, -gravity.Y) * settings.SpeedLimit / (1 + 3 * settings.BodyFriction);
        var response = 4f / (1 + settings.Damping * .25f);
        var decay = MathF.Exp(-response * dt);
        travel = MathF.Min(limit, travel + targetSpeed * dt + (speed - targetSpeed) * (1 - decay) / response);
        speed = travel >= limit ? 0 : targetSpeed + (speed - targetSpeed) * decay;
        slip = -Vector3.UnitY * travel;
        velocity = -Vector3.UnitY * speed;
        sway = Vector3.Zero;
    }

    /// <summary>Retain the upper opening and sleeves; loosen progressively toward the hem.
    /// Saved anchor multipliers cannot turn the collar/shoulders into a translating rigid top.</summary>
    public static float UpperBodyWeight(string bone) => bone switch
    {
        "j_kosi" => 1f,
        "j_sebo_a" => .65f,
        "j_sebo_b" => .3f,
        "j_sebo_c" => .1f,
        _ => bone.StartsWith("j_sk_", StringComparison.Ordinal) ? 1f : 0f,
    };

    public Vector3 TrouserOffset(string bone, Vector3 segmentDirection, float segmentLength,
        float scale, float multiplier, AttachmentSettings settings)
    {
        // Slide along each current body segment and gather toward fixed ankle openings.
        // Anatomical caps retain segment ordering instead of silently imposing millimetre travel.
        var weight = bone == "j_kosi" ? 1f : bone.StartsWith("j_asi_a_", StringComparison.Ordinal) ? .85f :
            bone.StartsWith("j_asi_b_", StringComparison.Ordinal) ? .45f : 0f;
        if (weight == 0 || segmentLength < .001f || settings.Template == GarmentTemplate.Rigid) return Vector3.Zero;
        var cap = MathF.Min(settings.SlipDistance * scale * weight, segmentLength * (bone == "j_kosi" ? .45f : .65f));
        var travel = Math.Clamp(-slip.Y * scale * Math.Clamp(multiplier, 0, 2) * weight, 0, cap);
        return AttachmentSimulation.SafeNormal(segmentDirection, Vector3.Zero) * travel;
    }

    private static Vector3 Limit(Vector3 value, float limit)
        => value.LengthSquared() > limit * limit ? Vector3.Normalize(value) * limit : value;

    private static Vector3 LimitSlide(Vector3 value, float limit, float lateral)
    {
        var side = Limit(new Vector3(value.X, 0, value.Z), MathF.Min(limit, lateral));
        return Limit(new Vector3(side.X, value.Y, side.Z), limit);
    }
}
