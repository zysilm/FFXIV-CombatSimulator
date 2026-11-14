// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator.Animation.Hair;

/// <summary>Where a fixed hair step falls inside the render frame that triggered it.</summary>
internal static class HairInputTiming
{
    /// <summary>
    /// Blend from the previous frame's sampled pose (0) to this frame's (1) for a fixed step,
    /// given the accumulator left after consuming that step. The game pose is sampled once per
    /// frame; feeding every catch-up step the frame's final pose makes the head's apparent
    /// velocity pulse with the frame/step beat, which shows as hopping hair.
    /// </summary>
    public static float StepBlend(float remainingAccumulator, float frameDelta)
        => frameDelta > 1e-6f ? Math.Clamp(1 - remainingAccumulator / frameDelta, 0, 1) : 1;
}
