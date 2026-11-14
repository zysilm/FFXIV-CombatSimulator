// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Animation.SkeletonProfiles;

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

    // Parts of a garment that can stay on the body, one bit each. The configuration stores the sum.
    public const int TopWaist = 1, TopBelly = 2, TopChest = 4, TopCollar = 8,
        TopLeftUpperArm = 16, TopLeftForearm = 32, TopRightUpperArm = 64, TopRightForearm = 128;
    public const int LowerWaist = 1, LowerLeftThigh = 2, LowerLeftShin = 4, LowerRightThigh = 8, LowerRightShin = 16;
    public const int DefaultTopHold = TopWaist;
    public const int DefaultLowerHold = LowerLeftShin | LowerRightShin;

    /// <summary>How hard the free part is pulled down. The index is what the configuration stores.</summary>
    public static readonly string[] PullLabels = { "None (just let go)", "Gentle", "Normal", "Strong" };
    private static readonly float[] PullScales = { 0f, 0.5f, 1f, 1.5f };
    public const int DefaultPull = 2;

    public static float PullScale(int pull) => PullScales[Math.Clamp(pull, 0, PullScales.Length - 1)];

    /// <summary>
    /// The hold bit of a rig segment (named after the bone it starts at) of a top (slot 1) or a lower
    /// garment (slot 3); 0 for a segment that can never be held. The shoulder piece goes with the upper arm.
    /// </summary>
    public static int HoldBit(SkeletonProfile profile, int slot, string boneName)
    {
        var bones = profile.Landmarks;
        return slot switch
        {
            1 => boneName == bones.Pelvis ? TopWaist
                : boneName == bones.LowerSpine ? TopBelly
                : boneName == bones.MidSpine ? TopChest
                : boneName == bones.Chest ? TopCollar
                : boneName == bones.Clavicle.Left || boneName == bones.UpperArm.Left ? TopLeftUpperArm
                : boneName == bones.Forearm.Left ? TopLeftForearm
                : boneName == bones.Clavicle.Right || boneName == bones.UpperArm.Right ? TopRightUpperArm
                : boneName == bones.Forearm.Right ? TopRightForearm
                : 0,
            3 => boneName == bones.Pelvis ? LowerWaist
                : boneName == bones.Thigh.Left ? LowerLeftThigh
                : boneName == bones.Shin.Left ? LowerLeftShin
                : boneName == bones.Thigh.Right ? LowerRightThigh
                : boneName == bones.Shin.Right ? LowerRightShin
                : 0,
            _ => 0,
        };
    }

    /// <summary>True for the rig segments that stay on the body under the chosen set of holds.</summary>
    public static bool IsPinned(SkeletonProfile profile, int slot, string boneName, int holds)
        => (HoldBit(profile, slot, boneName) & holds) != 0;

    /// <summary>The set of holds equal to a choice made with the earlier single-choice dropdowns.</summary>
    public static int HoldsFromLegacyChoice(int slot, int choice) => slot switch
    {
        1 => choice switch
        {
            1 => TopChest,
            2 => TopCollar,
            3 => TopLeftUpperArm | TopLeftForearm,
            4 => TopRightUpperArm | TopRightForearm,
            _ => DefaultTopHold,
        },
        _ => choice switch
        {
            1 => LowerLeftShin,
            2 => LowerRightShin,
            3 => LowerLeftThigh | LowerLeftShin,
            4 => LowerRightThigh | LowerRightShin,
            5 => LowerWaist,
            _ => DefaultLowerHold,
        },
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
    public static Vector3 PullVelocity(Vector3 towardFeet, Vector3 lift, float distanceFromPin, float strength = 1f)
    {
        var reach = float.IsFinite(distanceFromPin) ? Math.Clamp(distanceFromPin / ReferenceDistance, 0.4f, 1.5f) : 1f;
        return (towardFeet * PullSpeed + lift * LiftSpeed) * (reach * MathF.Max(0f, strength));
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
