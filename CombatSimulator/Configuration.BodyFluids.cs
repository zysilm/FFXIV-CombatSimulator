// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator;

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
    }

    internal Rendering.WorldGeometry.FluidMaterial CreateBodyFluidMaterial() =>
        Rendering.WorldGeometry.FluidMaterial.Default with
        {
            ReflectionStrength = FluidClamp(BodyFluidReflectionStrength, 0f, 1f, BodyFluidDefaults.Reflection),
            Roughness = FluidClamp(BodyFluidRoughness, 0.02f, 1f, BodyFluidDefaults.Roughness),
            Cloudiness = FluidClamp(BodyFluidCloudiness, 0f, 1f, BodyFluidDefaults.Cloudiness),
            FoamAmount = FluidClamp(BodyFluidFoamAmount, 0f, 1f, BodyFluidDefaults.Foam),
        };

    private static float FluidClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
