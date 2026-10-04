// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator;

public partial class Configuration
{
    public bool BodyFluidsEnabled { get; set; }
    public bool BodyFluidsOnPlayerKo { get; set; }
    public float BodyFluidFlowMlPerSecond { get; set; } = 0.08f;
    public float BodyFluidSurfaceSpeed { get; set; } = 0.12f;
    public float BodyFluidFilamentRelaxation { get; set; } = 0.45f;
    public float BodyFluidFilamentLength { get; set; } = 0.18f;
    public float BodyFluidPuddleLifetime { get; set; } = 25f;
    public float BodyFluidOpacity { get; set; } = 0.38f;
    public float BodyFluidVisualScale { get; set; } = 2f;
    public float BodyFluidMouthForwardOffset { get; set; }
    public float BodyFluidMouthHeightOffset { get; set; }

    internal void ClampBodyFluids()
    {
        BodyFluidFlowMlPerSecond = FluidClamp(BodyFluidFlowMlPerSecond, 0.01f, 0.6f, 0.08f);
        BodyFluidSurfaceSpeed = FluidClamp(BodyFluidSurfaceSpeed, 0.01f, 0.5f, 0.12f);
        BodyFluidFilamentRelaxation = FluidClamp(BodyFluidFilamentRelaxation, 0.05f, 2f, 0.45f);
        BodyFluidFilamentLength = FluidClamp(BodyFluidFilamentLength, 0.03f, 0.35f, 0.18f);
        BodyFluidPuddleLifetime = FluidClamp(BodyFluidPuddleLifetime, 2f, 90f, 25f);
        BodyFluidOpacity = FluidClamp(BodyFluidOpacity, 0.05f, 0.8f, 0.38f);
        BodyFluidVisualScale = FluidClamp(BodyFluidVisualScale, 1f, 4f, 2f);
        BodyFluidMouthForwardOffset = FluidClamp(BodyFluidMouthForwardOffset, -0.1f, 0.1f, 0f);
        BodyFluidMouthHeightOffset = FluidClamp(BodyFluidMouthHeightOffset, -0.1f, 0.1f, 0f);
    }

    private static float FluidClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
