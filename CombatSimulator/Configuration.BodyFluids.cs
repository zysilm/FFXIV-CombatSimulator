// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator;

public enum BodyFluidOutletKind
{
    Mouth, Part1Left, Part1Right, Part2, Part3, NoseLeft, NoseRight, EyeLeft, EyeRight,
}

public sealed class BodyFluidOutletSettings
{
    public BodyFluidOutletKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int SettingsVersion { get; set; }
    public float FlowMlPerSecond { get; set; } = 0.006f;
    public bool BloodTint { get; set; }
    public float ThicknessScale { get; set; } = 2f;
    public float ViscosityPaSeconds { get; set; } = 0.18f;
    public float Stringiness { get; set; } = 6f;
    public float FilamentRelaxation { get; set; } = 0.45f;
    public float SurfaceSpeed { get; set; } = 0.12f;
    public float ReflectionStrength { get; set; } = 0.35f;
    public float Roughness { get; set; } = 0.08f;
    public float Cloudiness { get; set; } = 0.28f;
    public float FoamAmount { get; set; } = 0.22f;
    // Retained only for migration from the shared settings version.
    public float FlowMultiplier { get; set; } = 1f;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }

    internal void ResetSettings()
    {
        SettingsVersion = 1; Enabled = true; FlowMultiplier = 1f;
        FlowMlPerSecond = 0.006f; BloodTint = false; ThicknessScale = 2f;
        ViscosityPaSeconds = 0.18f; Stringiness = 6f; FilamentRelaxation = 0.45f;
        SurfaceSpeed = 0.12f; ReflectionStrength = 0.35f; Roughness = 0.08f;
        Cloudiness = 0.28f; FoamAmount = 0.22f;
        OffsetX = OffsetY = OffsetZ = 0;
    }
}

public partial class Configuration
{
    internal static class BodyFluidDefaults
    {
        internal const float Flow = 0.006f, Viscosity = 0.18f, Reflection = 0.35f, Roughness = 0.08f,
            Cloudiness = 0.28f, Foam = 0.22f,
            SurfaceSpeed = 0.12f, Relaxation = 0.45f,
            FilamentLength = 0.18f, PuddleLifetime = 25f, Opacity = 0.38f, VisualScale = 2f;
    }
    public bool BodyFluidsEnabled { get; set; }
    public bool BodyFluidsOnPlayerKo { get; set; }
    public List<BodyFluidOutletSettings> BodyFluidOutlets { get; set; } =
        new() { new() { Kind = BodyFluidOutletKind.Mouth } };
    public bool BodyFluidBloodTint { get; set; }
    public float BodyFluidFlowMlPerSecond { get; set; } = BodyFluidDefaults.Flow;
    public int BodyFluidSettingsVersion { get; set; }
    public float BodyFluidViscosityPaSeconds { get; set; } = BodyFluidDefaults.Viscosity;
    public float BodyFluidReflectionStrength { get; set; } = BodyFluidDefaults.Reflection;
    public float BodyFluidRoughness { get; set; } = BodyFluidDefaults.Roughness;
    public float BodyFluidCloudiness { get; set; } = BodyFluidDefaults.Cloudiness;
    public float BodyFluidFoamAmount { get; set; } = BodyFluidDefaults.Foam;
    public float BodyFluidThicknessScale { get; set; } = 2f;
    public float BodyFluidStringiness { get; set; } = 6f;
    public float BodyFluidSurfaceSpeed { get; set; } = BodyFluidDefaults.SurfaceSpeed;
    public float BodyFluidFilamentRelaxation { get; set; } = BodyFluidDefaults.Relaxation;
    public float BodyFluidFilamentLength { get; set; } = BodyFluidDefaults.FilamentLength;
    public float BodyFluidPuddleLifetime { get; set; } = BodyFluidDefaults.PuddleLifetime;
    public float BodyFluidOpacity { get; set; } = BodyFluidDefaults.Opacity;
    public float BodyFluidVisualScale { get; set; } = BodyFluidDefaults.VisualScale;
    public float BodyFluidMouthForwardOffset { get; set; }
    public float BodyFluidMouthHeightOffset { get; set; }

    internal void ResetBodyFluids()
    {
        BodyFluidsEnabled = BodyFluidsOnPlayerKo = false;
        BodyFluidOutlets = new() { new() { Kind = BodyFluidOutletKind.Mouth, SettingsVersion = 1 } };
        BodyFluidBloodTint = false;
        BodyFluidFlowMlPerSecond = BodyFluidDefaults.Flow;
        BodyFluidSettingsVersion = 2;
        BodyFluidViscosityPaSeconds = BodyFluidDefaults.Viscosity;
        BodyFluidReflectionStrength = BodyFluidDefaults.Reflection;
        BodyFluidRoughness = BodyFluidDefaults.Roughness;
        BodyFluidCloudiness = BodyFluidDefaults.Cloudiness;
        BodyFluidFoamAmount = BodyFluidDefaults.Foam;
        BodyFluidThicknessScale = 2f;
        BodyFluidStringiness = 6f;
        BodyFluidSurfaceSpeed = BodyFluidDefaults.SurfaceSpeed;
        BodyFluidFilamentRelaxation = BodyFluidDefaults.Relaxation;
        BodyFluidFilamentLength = BodyFluidDefaults.FilamentLength;
        BodyFluidPuddleLifetime = BodyFluidDefaults.PuddleLifetime;
        BodyFluidOpacity = BodyFluidDefaults.Opacity;
        BodyFluidVisualScale = BodyFluidDefaults.VisualScale;
        BodyFluidMouthForwardOffset = BodyFluidMouthHeightOffset = 0;
    }

    internal void ClampBodyFluids()
    {
        // Prototype flow settings drove a discrete jet. Start the new film source gently once.
        if (BodyFluidSettingsVersion < 1)
        {
            BodyFluidFlowMlPerSecond = BodyFluidDefaults.Flow;
            BodyFluidSettingsVersion = 1;
        }
        BodyFluidFlowMlPerSecond = FluidClamp(BodyFluidFlowMlPerSecond, 0.001f, 0.12f, BodyFluidDefaults.Flow);
        BodyFluidViscosityPaSeconds = FluidClamp(BodyFluidViscosityPaSeconds, 0.03f, 1.2f, BodyFluidDefaults.Viscosity);
        BodyFluidReflectionStrength = FluidClamp(BodyFluidReflectionStrength, 0f, 1f, BodyFluidDefaults.Reflection);
        BodyFluidRoughness = FluidClamp(BodyFluidRoughness, 0.02f, 1f, BodyFluidDefaults.Roughness);
        BodyFluidCloudiness = FluidClamp(BodyFluidCloudiness, 0f, 1f, BodyFluidDefaults.Cloudiness);
        BodyFluidFoamAmount = FluidClamp(BodyFluidFoamAmount, 0f, 1f, BodyFluidDefaults.Foam);
        if (BodyFluidSettingsVersion < 2)
        {
            if (BodyFluidThicknessScale == 1f) BodyFluidThicknessScale = 2f;
            BodyFluidSettingsVersion = 2;
        }
        BodyFluidThicknessScale = FluidClamp(BodyFluidThicknessScale, 1f, 4f, 2f);
        BodyFluidStringiness = FluidClamp(BodyFluidStringiness, 1f, 20f, 6f);
        BodyFluidSurfaceSpeed = FluidClamp(BodyFluidSurfaceSpeed, 0.01f, 0.5f, BodyFluidDefaults.SurfaceSpeed);
        BodyFluidFilamentRelaxation = FluidClamp(BodyFluidFilamentRelaxation, 0.05f, 2f, BodyFluidDefaults.Relaxation);
        BodyFluidFilamentLength = FluidClamp(BodyFluidFilamentLength, 0.03f, 0.35f, BodyFluidDefaults.FilamentLength);
        BodyFluidPuddleLifetime = FluidClamp(BodyFluidPuddleLifetime, 2f, 90f, BodyFluidDefaults.PuddleLifetime);
        BodyFluidOpacity = FluidClamp(BodyFluidOpacity, 0.05f, 0.8f, BodyFluidDefaults.Opacity);
        BodyFluidVisualScale = FluidClamp(BodyFluidVisualScale, 1f, 4f, BodyFluidDefaults.VisualScale);
        BodyFluidMouthForwardOffset = FluidClamp(BodyFluidMouthForwardOffset, -0.1f, 0.1f, 0f);
        BodyFluidMouthHeightOffset = FluidClamp(BodyFluidMouthHeightOffset, -0.1f, 0.1f, 0f);
        NormalizeBodyFluidOutlets();
    }

    internal Rendering.WorldGeometry.FluidMaterial CreateBodyFluidMaterial() =>
        Rendering.WorldGeometry.FluidMaterial.Default with
        {
            // Selective absorption preserves the existing refraction path.
            Absorption = BodyFluidBloodTint ? new Vector3(25f, 1800f, 2600f)
                : Rendering.WorldGeometry.FluidMaterial.Default.Absorption,
            ReflectionStrength = FluidClamp(BodyFluidReflectionStrength, 0f, 1f, BodyFluidDefaults.Reflection),
            Roughness = FluidClamp(BodyFluidRoughness, 0.02f, 1f, BodyFluidDefaults.Roughness),
            Cloudiness = FluidClamp(BodyFluidCloudiness, 0f, 1f, BodyFluidDefaults.Cloudiness)
                * (BodyFluidBloodTint ? 0.15f : 1f),
            FoamAmount = FluidClamp(BodyFluidFoamAmount, 0f, 1f, BodyFluidDefaults.Foam)
                * (BodyFluidBloodTint ? 0.25f : 1f),
        };

    internal Rendering.WorldGeometry.FluidMaterial CreateBodyFluidMaterial(BodyFluidOutletSettings site) =>
        Rendering.WorldGeometry.FluidMaterial.Default with
        {
            Absorption = site.BloodTint ? new Vector3(25f, 1800f, 2600f)
                : Rendering.WorldGeometry.FluidMaterial.Default.Absorption,
            ReflectionStrength = FluidClamp(site.ReflectionStrength, 0f, 1f, BodyFluidDefaults.Reflection),
            Roughness = FluidClamp(site.Roughness, 0.02f, 1f, BodyFluidDefaults.Roughness),
            Cloudiness = FluidClamp(site.Cloudiness, 0f, 1f, BodyFluidDefaults.Cloudiness)
                * (site.BloodTint ? 0.15f : 1f),
            FoamAmount = FluidClamp(site.FoamAmount, 0f, 1f, BodyFluidDefaults.Foam)
                * (site.BloodTint ? 0.25f : 1f),
        };

    internal void NormalizeBodyFluidOutlets()
    {
        BodyFluidOutlets ??= new() { new() { Kind = BodyFluidOutletKind.Mouth } };
        var seen = 0;
        for (var i = 0; i < BodyFluidOutlets.Count;)
        {
            var source = BodyFluidOutlets[i];
            var kind = source == null ? -1 : (int)source.Kind;
            if (kind < 0 || kind > (int)BodyFluidOutletKind.EyeRight || (seen & (1 << kind)) != 0)
            {
                BodyFluidOutlets.RemoveAt(i);
                continue;
            }
            seen |= 1 << kind;
            source!.FlowMultiplier = FluidClamp(source.FlowMultiplier, 0.1f, 4f, 1f);
            if (source.SettingsVersion < 1)
            {
                source.FlowMlPerSecond = BodyFluidFlowMlPerSecond * source.FlowMultiplier;
                source.BloodTint = BodyFluidBloodTint;
                source.ThicknessScale = BodyFluidThicknessScale;
                source.ViscosityPaSeconds = BodyFluidViscosityPaSeconds;
                source.Stringiness = BodyFluidStringiness;
                source.FilamentRelaxation = BodyFluidFilamentRelaxation;
                source.SurfaceSpeed = BodyFluidSurfaceSpeed;
                source.ReflectionStrength = BodyFluidReflectionStrength;
                source.Roughness = BodyFluidRoughness;
                source.Cloudiness = BodyFluidCloudiness;
                source.FoamAmount = BodyFluidFoamAmount;
                source.SettingsVersion = 1;
            }
            source.FlowMlPerSecond = FluidClamp(source.FlowMlPerSecond, 0.001f, 0.48f, BodyFluidDefaults.Flow);
            source.ThicknessScale = FluidClamp(source.ThicknessScale, 1f, 4f, 2f);
            source.ViscosityPaSeconds = FluidClamp(source.ViscosityPaSeconds, 0.03f, 1.2f, BodyFluidDefaults.Viscosity);
            source.Stringiness = FluidClamp(source.Stringiness, 1f, 20f, 6f);
            source.FilamentRelaxation = FluidClamp(source.FilamentRelaxation, 0.05f, 2f, BodyFluidDefaults.Relaxation);
            source.SurfaceSpeed = FluidClamp(source.SurfaceSpeed, 0.01f, 0.5f, BodyFluidDefaults.SurfaceSpeed);
            source.ReflectionStrength = FluidClamp(source.ReflectionStrength, 0f, 1f, BodyFluidDefaults.Reflection);
            source.Roughness = FluidClamp(source.Roughness, 0.02f, 1f, BodyFluidDefaults.Roughness);
            source.Cloudiness = FluidClamp(source.Cloudiness, 0f, 1f, BodyFluidDefaults.Cloudiness);
            source.FoamAmount = FluidClamp(source.FoamAmount, 0f, 1f, BodyFluidDefaults.Foam);
            source.OffsetX = FluidClamp(source.OffsetX, -0.1f, 0.1f, 0f);
            source.OffsetY = FluidClamp(source.OffsetY, -0.1f, 0.1f, 0f);
            source.OffsetZ = FluidClamp(source.OffsetZ, -0.1f, 0.1f, 0f);
            i++;
        }
    }

    private static float FluidClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
