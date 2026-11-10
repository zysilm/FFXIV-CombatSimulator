using System;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>A single translation shared by the whole garment; no deformation or contact projection.</summary>
public sealed class WholeGarmentSlide
{
    private float travel, speed;
    public Vector3 Offset { get; private set; }

    /// <summary>
    /// How strongly the garment is driven along its slide direction (0..1). Gravity drives it fully when
    /// the direction points down. A loosened garment still works its way along a body lying flat, where
    /// gravity has no component along it, so the drive never drops below <see cref="RestingDrive"/>.
    /// </summary>
    public const float RestingDrive = 0.4f;
    public static float Drive(Vector3 direction)
        => float.IsFinite(direction.Y) ? Math.Clamp(MathF.Max(RestingDrive, -direction.Y), 0f, 1f) : RestingDrive;

    public void Advance(float dt, Vector3 direction, float gravityAlong, float limit,
        float speedLimit, float friction, float damping)
    {
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(direction.LengthSquared())) return;
        dt = MathF.Min(dt, .1f);
        limit = MathF.Max(0, limit);
        direction = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.Zero;
        var response = 4f / (1 + MathF.Max(0, damping) * .25f);
        var targetSpeed = Math.Clamp(gravityAlong, 0, 1) * MathF.Max(0, speedLimit) / (1 + 3 * MathF.Max(0, friction));
        var decay = MathF.Exp(-response * dt);
        travel = Math.Clamp(travel + targetSpeed * dt + (speed-targetSpeed) * (1-decay) / response, 0, limit);
        speed = travel >= limit ? 0 : targetSpeed + (speed-targetSpeed) * decay;
        var desired = direction * travel;
        // Smooth direction changes during collapse too, without lagging behind the body's root.
        var delta = (desired-Offset) * (1-MathF.Exp(-response * dt));
        var maxStep = MathF.Max(.01f, speedLimit) * dt;
        if (delta.LengthSquared() > maxStep * maxStep) delta = Vector3.Normalize(delta) * maxStep;
        Offset += delta;
    }
}
