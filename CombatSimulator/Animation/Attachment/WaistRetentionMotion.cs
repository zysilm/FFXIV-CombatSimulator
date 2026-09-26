using System;
using System.Numerics;

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

    public static float BoneWeight(string name, float parentWeight) => name switch
    {
        "j_kosi" => 1,
        "j_asi_a_l" or "j_asi_a_r" => .8f,
        "j_asi_b_l" or "j_asi_b_r" => .25f,
        "j_asi_c_l" or "j_asi_c_r" or "j_asi_d_l" or "j_asi_d_r" => 0,
        _ => name.StartsWith("j_sk_", StringComparison.Ordinal) ? parentWeight * .65f : parentWeight,
    };
}
