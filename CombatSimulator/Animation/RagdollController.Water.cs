// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // Look for a water surface over the corpse: at once after activation or a long move, otherwise twice a second.
    private void UpdateWaterProbe(float dt)
    {
        if (!settings.RagdollWaterBuoyancy || simulation == null || ragdollBones.Count == 0)
        {
            waterLevel = float.NaN;
            inWater = false;
            return;
        }

        var position = simulation.Bodies.GetBodyReference(ragdollBones[0].BodyHandle).Pose.Position;
        waterProbeCountdown -= dt;
        if (waterProbeCountdown > 0f && Vector3.DistanceSquared(position, waterProbePosition) < WaterProbeMoveDistance * WaterProbeMoveDistance)
            return;

        waterProbeCountdown = WaterProbeInterval;
        waterProbePosition = position;
        waterLevel = world.TryFindWaterSurface(position, 30f, 60f, out var surface) ? surface : float.NaN;
        if (float.IsNaN(waterLevel)) inWater = false;
    }

    // Lift and drag on every body that is in the water (see WaterBuoyancy). Bodies are only given forces;
    // how the corpse floats, rolls and rides the swell is left to the solver.
    private void ApplyWaterForces(float dt)
    {
        if (simulation == null || float.IsNaN(waterLevel) || ragdollBones.Count == 0)
        {
            inWater = false;
            return;
        }

        waterClock += dt;
        var liquidDensity = WaterBuoyancy.LiquidDensity(settings.RagdollWaterLiquid);
        var any = false;
        var reach = waterLevel + WaterBuoyancy.MaxWaveHeight;
        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            if (body.Kinematic) continue;
            var position = body.Pose.Position;
            var bottom = BodyBottom(rb, body);
            if (bottom >= reach) continue;

            var (surface, surfaceSpeed) = WaterBuoyancy.Surface(waterLevel, position.X, position.Z, waterClock, RagdollTuning.Gravity);
            var submerged = WaterBuoyancy.Submerged(bottom, 2f * position.Y - bottom, surface);
            if (submerged <= 0f) continue;

            any = true;
            var linear = body.Velocity.Linear;
            var angular = body.Velocity.Angular;
            WaterBuoyancy.Apply(ref linear, ref angular, submerged, WaterBuoyancy.RelativeDensity(HumanoidSkeleton, rb.Name, liquidDensity),
                surfaceSpeed, RagdollTuning.Gravity, dt);
            body.Velocity.Linear = linear;
            body.Velocity.Angular = angular;
            body.Awake = true; // moving water never lets a floating body rest
        }
        inWater = any;
    }
}
