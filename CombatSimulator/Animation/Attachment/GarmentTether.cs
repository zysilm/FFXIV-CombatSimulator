// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>
/// "Half off, hanging on the body": the garment is the same limp chain as a dropped one, except that the
/// segments named here stay on the body. A top stays on at the waist and its chest, shoulders and sleeves
/// are pulled down over the hips; a lower garment stays on at both shins and its waist and thighs are
/// pulled down past the knees. This class holds the pose-independent maths.
/// </summary>
public static class GarmentTether
{
    /// <summary>Speed (m/s) the free segments are pulled toward the feet with, at the reference distance.</summary>
    public const float PullSpeed = 1.6f;
    /// <summary>Speed (m/s) they are lifted off the body with, so they clear it instead of passing through.</summary>
    public const float LiftSpeed = 1.4f;
    /// <summary>Distance from the pinned segment at which the pull has its nominal speed.</summary>
    public const float ReferenceDistance = 0.3f;
    /// <summary>Swing range of every joint, so the free part can fold right back over the pinned part.</summary>
    public const float FoldSwingLimit = 2.9f;
    public const float MaxFollowSpeed = 8f;
    public const float MaxFollowAngularSpeed = 20f;

    /// <summary>True for the rig segments (named after the bone they start at) that stay on the body.</summary>
    public static bool IsPinned(int slot, string boneName) => slot switch
    {
        1 => boneName == "j_kosi",
        3 => boneName is "j_asi_b_l" or "j_asi_b_r",
        _ => false,
    };

    /// <summary>Pose a pinned segment's box must have for its bone to sit exactly on the live body bone.</summary>
    public static (Vector3 Position, Quaternion Rotation) PinnedBodyPose(Vector3 bonePosition, Quaternion boneRotation,
        Quaternion bodyToBoneRotation, Vector3 bodyToBoneOffsetLocal)
    {
        var rotation = Quaternion.Normalize(boneRotation * Quaternion.Inverse(bodyToBoneRotation));
        return (bonePosition - Vector3.Transform(bodyToBoneOffsetLocal, rotation), rotation);
    }

    /// <summary>
    /// Direction the free part is lifted away from the body: the body's front/back axis, on whichever
    /// side faces up. For an upright body neither side does, so the hint (the character's facing) decides.
    /// </summary>
    public static Vector3 LiftDirection(Vector3 towardFeet, Vector3 lateral, Vector3 facingHint)
    {
        var depth = GarmentFlatten.DepthAxis(towardFeet, lateral);
        if (depth.Y > 0.3f) return depth;
        if (depth.Y < -0.3f) return -depth;
        return Vector3.Dot(depth, facingHint) >= 0f ? depth : -depth;
    }

    /// <summary>
    /// Velocity a free segment starts with. Segments farther from the pinned part have farther to travel
    /// as the garment folds over it, so the pull grows with distance (within bounds).
    /// </summary>
    public static Vector3 PullVelocity(Vector3 towardFeet, Vector3 lift, float distanceFromPin)
    {
        var reach = float.IsFinite(distanceFromPin) ? Math.Clamp(distanceFromPin / ReferenceDistance, 0.4f, 1.5f) : 1f;
        return (towardFeet * PullSpeed + lift * LiftSpeed) * reach;
    }

    /// <summary>Linear velocity that carries a pinned segment to its new pose, bounded so a teleport cannot launch it.</summary>
    public static Vector3 FollowVelocity(Vector3 from, Vector3 to, float dt)
    {
        if (!(dt > 1e-5f)) return Vector3.Zero;
        var velocity = (to - from) / dt;
        var speed = velocity.Length();
        if (!float.IsFinite(speed) || speed > MaxFollowSpeed) return Vector3.Zero;
        return velocity;
    }

    /// <summary>Angular velocity that carries a pinned segment to its new orientation, bounded likewise.</summary>
    public static Vector3 FollowAngularVelocity(Quaternion from, Quaternion to, float dt)
    {
        if (!(dt > 1e-5f)) return Vector3.Zero;
        var delta = Quaternion.Normalize(to * Quaternion.Inverse(from));
        if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        var axis = new Vector3(delta.X, delta.Y, delta.Z);
        var sin = axis.Length();
        if (sin < 1e-6f) return Vector3.Zero;
        var angle = 2f * MathF.Atan2(sin, delta.W);
        var velocity = axis / sin * (angle / dt);
        return float.IsFinite(velocity.LengthSquared()) && velocity.Length() <= MaxFollowAngularSpeed ? velocity : Vector3.Zero;
    }
}
