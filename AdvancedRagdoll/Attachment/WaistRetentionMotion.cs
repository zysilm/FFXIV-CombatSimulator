using System;
using System.Numerics;
using CombatSimulator.Animation.SkeletonProfiles;

namespace CombatSimulator.Animation.Attachment;

/// <summary>Continuously retain the waistband as the thighs separate. No collision projection.</summary>
public sealed class WaistRetentionMotion
{
    public float Strength { get; private set; }

    public void Advance(float dt, Vector3 leftThigh, Vector3 rightThigh)
    {
        if (!float.IsFinite(dt) || dt <= 0) return;
        var target = 0f;
        var length = leftThigh.Length() * rightThigh.Length();
        if (float.IsFinite(length) && length > 1e-6f)
        {
            var cosine = Math.Clamp(Vector3.Dot(leftThigh, rightThigh) / length, -1, 1);
            // Start retaining at roughly 28 degrees, fully retain by roughly 109 degrees.
            var opening = Math.Clamp((1 - cosine - .12f) / 1.2f, 0, 1);
            target = opening * opening * (3 - 2 * opening);
        }
        var delta = (target - Strength) * (1 - MathF.Exp(-6 * MathF.Min(dt, .1f)));
        Strength += Math.Clamp(delta, -2 * dt, 2 * dt);
    }

    public Vector3 Correction(Vector3 slide, float waistWeight)
        => -slide * (Strength * Math.Clamp(waistWeight, 0, 1));

    /// <summary>
    /// How strongly a rig segment (named after the bone it starts at) is held by the waistband: fully at
    /// the pelvis, less down the legs, not at all from the calves; a cloth bone carries part of its parent's.
    /// </summary>
    public static float BoneWeight(SkeletonProfile profile, string name, float parentWeight)
    {
        var bones = profile.Landmarks;
        if (name == bones.Pelvis) return 1;
        if (name == bones.Thigh.Left || name == bones.Thigh.Right) return .8f;
        if (name == bones.Shin.Left || name == bones.Shin.Right) return .25f;
        if (name == bones.Calf.Left || name == bones.Calf.Right || name == bones.Foot.Left || name == bones.Foot.Right) return 0;
        return profile.Classes.Cloth.Matches(name) ? parentWeight * .65f : parentWeight;
    }
}
