// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>
/// Buoyancy for ragdoll bodies in water. Nothing here poses a body: each rigid body only receives the
/// forces water would put on it, and the float, the roll and the bobbing come out of the simulation.
///
/// Per body and per step: Archimedes' lift on the submerged share of its volume (which depends on the
/// body part's density relative to water), and drag against the water around it. The water surface is
/// a small travelling-wave field, so a floating body rides the swell because the lift under each part
/// rises and falls with the water there.
/// </summary>
public static class WaterBuoyancy
{
    /// <summary>Linear drag rate of a fully submerged body, 1/s.</summary>
    public const float LinearDrag = 2.5f;
    /// <summary>Extra drag that grows with speed, 1/m: what stops a fall into water.</summary>
    public const float QuadraticDrag = 1.5f;
    /// <summary>Angular drag rate of a fully submerged body, 1/s.</summary>
    public const float AngularDrag = 3f;

    // Deep-water gravity waves: three trains of different length and heading. Heights are small because
    // the game's own water animation cannot be read; a larger swell would visibly disagree with it.
    private static readonly (Vector2 Direction, float Wavelength, float Amplitude)[] Waves =
    {
        (Vector2.Normalize(new Vector2(1f, 0.3f)), 5.0f, 0.025f),
        (Vector2.Normalize(new Vector2(-0.4f, 1f)), 2.7f, 0.014f),
        (Vector2.Normalize(new Vector2(0.7f, -0.8f)), 1.3f, 0.007f),
    };

    /// <summary>Largest distance the surface can stand above or below its still level.</summary>
    public static float MaxWaveHeight
    {
        get
        {
            var sum = 0f;
            foreach (var wave in Waves) sum += wave.Amplitude;
            return sum;
        }
    }

    /// <summary>Height of the water surface at a point and time, and how fast it is rising there.</summary>
    public static (float Height, float VerticalSpeed) Surface(float stillLevel, float x, float z, float time, float gravity = 9.8f)
    {
        var height = stillLevel;
        var speed = 0f;
        foreach (var (direction, wavelength, amplitude) in Waves)
        {
            var k = 2f * MathF.PI / wavelength;
            var omega = MathF.Sqrt(MathF.Max(0f, gravity) * k); // deep-water dispersion
            var phase = k * (direction.X * x + direction.Y * z) - omega * time;
            height += amplitude * MathF.Sin(phase);
            speed += -amplitude * omega * MathF.Cos(phase);
        }
        return (height, speed);
    }

    /// <summary>Share (0..1) of a body's height that is under the surface.</summary>
    public static float Submerged(float bottom, float top, float surface)
    {
        if (!float.IsFinite(bottom) || !float.IsFinite(top) || !float.IsFinite(surface)) return 0f;
        if (top - bottom < 1e-4f) return surface > bottom ? 1f : 0f;
        return Math.Clamp((surface - bottom) / (top - bottom), 0f, 1f);
    }

    /// <summary>The liquids a body can be in. The index is what the configuration stores.</summary>
    public static readonly string[] LiquidLabels = { "Water", "Dense liquid" };

    /// <summary>
    /// Density of the liquid relative to water. In water a body (0.92 overall) floats with only a sliver
    /// showing, as a real one does. The dense liquid is twice as heavy, like a strong brine taken further,
    /// and carries a body with about half of it above the surface.
    /// </summary>
    public static float LiquidDensity(int liquid) => liquid == 1 ? 2f : 1f;

    /// <summary>
    /// Density of a body part relative to the liquid it is in. The proportions are anatomical (the chest
    /// holds the lungs and is lightest, shins and feet are heaviest), so a body floats chest up with the
    /// legs trailing; how high it rides depends on the liquid.
    /// </summary>
    public static float RelativeDensity(string boneName, float liquidDensity = 1f)
        => Anatomical(boneName) / (liquidDensity > 0f ? liquidDensity : 1f);

    private static float Anatomical(string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return 1f;
        if (boneName.StartsWith("j_sebo", StringComparison.Ordinal) || boneName.StartsWith("j_mune", StringComparison.Ordinal)) return 0.78f;
        if (boneName == "j_kosi") return 0.96f;
        if (boneName.StartsWith("j_kubi", StringComparison.Ordinal) || boneName.StartsWith("j_kao", StringComparison.Ordinal)) return 1.02f;
        if (boneName.StartsWith("j_asi_a", StringComparison.Ordinal)) return 1.03f;
        if (boneName.StartsWith("j_asi", StringComparison.Ordinal)) return 1.09f;
        if (boneName.StartsWith("j_sako", StringComparison.Ordinal) || boneName.StartsWith("j_ude", StringComparison.Ordinal)) return 1.04f;
        if (boneName.StartsWith("j_te", StringComparison.Ordinal)) return 1.08f;
        if (boneName.StartsWith("j_sk_", StringComparison.Ordinal)) return 1.0f;
        return 1.0f;
    }

    /// <summary>
    /// One step of water forces on a body's velocity: lift on the submerged share, then drag toward the
    /// water's own vertical motion. The drag is applied implicitly, so it can only slow the body relative
    /// to the water and stays stable at any speed.
    /// </summary>
    public static void Apply(ref Vector3 linear, ref Vector3 angular, float submerged, float relativeDensity,
        float waterVerticalSpeed, float gravity, float dt)
    {
        if (!(submerged > 0f) || !(dt > 0f)) return;
        submerged = MathF.Min(submerged, 1f);
        var density = Math.Clamp(relativeDensity, 0.3f, 3f);

        linear.Y += gravity * submerged / density * dt;

        var water = new Vector3(0f, waterVerticalSpeed, 0f);
        var relative = linear - water;
        var speed = relative.Length();
        if (float.IsFinite(speed))
            relative /= 1f + (LinearDrag + QuadraticDrag * speed) * submerged * dt;
        linear = water + relative;

        angular /= 1f + AngularDrag * submerged * dt;
    }
}
