// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>Bone look-at in a character's model space (forward = +Z, up = +Y). Pure math.</summary>
internal static class HeadLookAtMath
{
    /// <summary>
    /// Model-space swing that turns a bone from where the animation points it toward a target,
    /// limited to <paramref name="maxYaw"/> / <paramref name="maxPitch"/> radians beyond the
    /// animated direction. The animated facing is the bone's rotation relative to its bind pose
    /// applied to +Z, so no per-bone local axis convention is assumed. Twist is never added.
    /// </summary>
    public static Quaternion Swing(Quaternion currentModelRotation, Quaternion bindModelRotation,
        Vector3 boneModelPosition, Vector3 targetModelPosition, float maxYaw, float maxPitch)
    {
        var facing = Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(currentModelRotation * Quaternion.Inverse(bindModelRotation)));
        var toTarget = targetModelPosition - boneModelPosition;
        if (!Finite(facing) || !Finite(toTarget) || toTarget.LengthSquared() < 1e-6f || facing.LengthSquared() < 1e-6f)
            return Quaternion.Identity;
        facing = Vector3.Normalize(facing); toTarget = Vector3.Normalize(toTarget);
        float facingYaw = MathF.Atan2(facing.X, facing.Z), targetYaw = MathF.Atan2(toTarget.X, toTarget.Z);
        float facingPitch = MathF.Asin(Math.Clamp(facing.Y, -1, 1)), targetPitch = MathF.Asin(Math.Clamp(toTarget.Y, -1, 1));
        float yaw = Math.Clamp(Wrap(targetYaw - facingYaw), -maxYaw, maxYaw);
        float pitch = Math.Clamp(targetPitch - facingPitch, -maxPitch, maxPitch);
        float newYaw = facingYaw + yaw, newPitch = Math.Clamp(facingPitch + pitch, -1.5f, 1.5f);
        var goal = new Vector3(MathF.Sin(newYaw) * MathF.Cos(newPitch), MathF.Sin(newPitch), MathF.Cos(newYaw) * MathF.Cos(newPitch));
        return FromTo(facing, goal);
    }

    /// <summary>Local (right-multiplied) delta that applies a model-space swing to a bone.</summary>
    public static Quaternion LocalDelta(Quaternion currentModelRotation, Quaternion modelSwing)
    {
        var current = Quaternion.Normalize(currentModelRotation);
        return Quaternion.Normalize(Quaternion.Inverse(current) * modelSwing * current);
    }

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot > .999999f) return Quaternion.Identity;
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f) axis = Vector3.Cross(from, MathF.Abs(from.Y) < .9f ? Vector3.UnitY : Vector3.UnitX);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    private static float Wrap(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
