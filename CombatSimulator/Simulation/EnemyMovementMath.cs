// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Pure math for enemy tactics (turning, threat, flee and circling goals).
/// No game state, so Tests/AiMovement can exercise it offline.
/// </summary>
public static class EnemyMovementMath
{
    /// <summary>Hitbox radius every authored range already assumes (a humanoid's ~0.5y).</summary>
    public const float BaselineHitboxRadius = 0.5f;

    /// <summary>Slowest turn rate (rad/s) any actor is scaled down to.</summary>
    public const float MinTurnRate = 1.2f;

    /// <summary>
    /// Turn rate for a body of <paramref name="hitboxRadius"/>: humanoids (and smaller) keep
    /// <paramref name="baseRate"/>, bigger bodies turn proportionally slower (radius^0.75), so a
    /// large monster cannot spin instantly and its flanks and back mean something.
    /// </summary>
    public static float TurnRate(float baseRate, float hitboxRadius)
    {
        var size = MathF.Max(1f, hitboxRadius / BaselineHitboxRadius);
        return MathF.Max(MathF.Min(baseRate, MinTurnRate), baseRate / MathF.Pow(size, 0.75f));
    }

    /// <summary>Threat a hit generates. Tanks generate a multiple of their damage, as in FFXIV's
    /// tank stance, so an enemy's attention follows the tank rather than raw DPS.</summary>
    public const float TankThreatMultiplier = 3.0f;
    /// <summary>Exponential threat decay time constant (s): an attacker who stops hitting is
    /// gradually forgotten instead of holding aggro forever.</summary>
    public const float ThreatDecayTau = 15f;

    public static float HitThreat(int damage, bool isTank)
        => MathF.Max(0, damage) * (isTank ? TankThreatMultiplier : 1f);

    public static float DecayThreat(float threat, float deltaTime)
        => threat * MathF.Exp(-MathF.Max(0f, deltaTime) / ThreatDecayTau);

    /// <summary>A candidate's threat relative to the top of the table, in [0, 1] (0 when nobody has any).</summary>
    public static float ThreatShare(float threat, float maxThreat)
        => maxThreat > 0.0001f ? Math.Clamp(threat / maxThreat, 0f, 1f) : 0f;

    /// <summary>
    /// Where a fleeing enemy runs: just past its rescuer on the far side from the threat, so it ends
    /// behind the ally rather than on top of it. (x, z); the caller keeps Y.
    /// </summary>
    public static (float X, float Z) FleeGoal(float threatX, float threatZ, float allyX, float allyZ, float behindDistance)
    {
        var dx = allyX - threatX;
        var dz = allyZ - threatZ;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.001f)
            return (allyX, allyZ);
        return (allyX + dx / len * behindDistance, allyZ + dz / len * behindDistance);
    }

    /// <summary>
    /// A sidestep goal for an enemy holding range: rotate its bearing around the target by
    /// <paramref name="arcDeg"/> (sign = direction) and keep its distance clamped into
    /// [<paramref name="minRadius"/>, <paramref name="maxRadius"/>], so circling never walks it out of
    /// (or into) its attack band. Returned as (x, z); the caller keeps Y.
    /// </summary>
    public static (float X, float Z) CircleGoal(
        float targetX, float targetZ, float actorX, float actorZ, float arcDeg, float minRadius, float maxRadius)
    {
        var dx = actorX - targetX;
        var dz = actorZ - targetZ;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        var bearing = dist < 0.001f ? 0f : MathF.Atan2(dx, dz);
        var lo = MathF.Max(0.1f, MathF.Min(minRadius, maxRadius));
        var radius = Math.Clamp(dist, lo, MathF.Max(lo, maxRadius));
        var angle = bearing + arcDeg * MathF.PI / 180f;
        return (targetX + MathF.Sin(angle) * radius, targetZ + MathF.Cos(angle) * radius);
    }
}
