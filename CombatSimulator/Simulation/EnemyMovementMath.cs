// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Pure movement math shared by the enemy approach, the companion mover and the engage planner.
/// No game state, so Tests/AiMovement can exercise it offline.
/// </summary>
public static class EnemyMovementMath
{
    /// <summary>Descent rate (yalms/s) a standing actor settles at. Slow on purpose: stepping off a
    /// corpse should ease down rather than snap.</summary>
    public const float SettleFallSpeed = 1.5f;

    /// <summary>Steepest descent a moving actor follows, as drop per yalm walked (~60°). Ground falls
    /// away under a running actor this fast at most on real terrain, so it stays glued downhill.</summary>
    public const float MaxDescentPerYalm = 1.75f;

    /// <summary>
    /// Next root height when following terrain. Rising snaps (never sink into a slope); falling is
    /// capped. The cap used to be <c>max(0.02, 1.5·dt)</c>: a per-frame floor (so the rate depended on
    /// frame rate) and 1.5 y/s regardless of run speed, so anything running downhill floated. The cap
    /// now scales with the horizontal distance covered this step and keeps the slow settle only for
    /// an actor that is (nearly) standing still.
    /// </summary>
    public static float FollowTerrainY(float fromY, float desiredY, float deltaTime, float horizontalStep)
    {
        if (desiredY >= fromY)
            return desiredY;

        var maxFall = MathF.Max(
            SettleFallSpeed * MathF.Max(0f, deltaTime),
            MathF.Max(0f, horizontalStep) * MaxDescentPerYalm);
        return fromY - MathF.Min(fromY - desiredY, maxFall);
    }

    /// <summary>Hitbox radius every authored range already assumes (a humanoid's ~0.5y).</summary>
    public const float BaselineHitboxRadius = 0.5f;

    /// <summary>
    /// Extra centre-to-centre distance two bodies need beyond an authored range. Ranges in this
    /// plugin were tuned on humanoids, so only the part of each radius above the humanoid baseline is
    /// added: humanoid pairs are unchanged, while a 3y monster no longer swallows whoever fights it.
    /// </summary>
    public static float HitboxSpacingExtra(float radiusA, float radiusB)
        => MathF.Max(0f, radiusA - BaselineHitboxRadius) + MathF.Max(0f, radiusB - BaselineHitboxRadius);

    /// <summary>Smallest gap between neighbouring melee slots: a little more than two humanoid bodies.</summary>
    public const float MinMeleeSlotGap = 1.2f;
    public const int MaxMeleeRingSlots = 12;

    /// <summary>
    /// Melee slot bearings (degrees, 0 = facing the attacker side), ordered front-first and alternating
    /// sides. Slots are spread evenly with as many as fit while neighbours keep at least
    /// <paramref name="minGap"/> apart along the ring — a fixed 32° step on a 1.5y ring put them only
    /// 0.83y apart, so attackers stood inside each other.
    /// </summary>
    public static float[] MeleeRingAngles(float ringRadius, float minGap)
    {
        var r = MathF.Max(0.1f, ringRadius);
        var half = MathF.Min(1f, MathF.Max(0.01f, minGap) / (2f * r));
        var minStep = 2f * MathF.Asin(half) * 180f / MathF.PI;
        var count = Math.Clamp((int)MathF.Floor(360f / MathF.Max(1f, minStep) + 1e-4f), 1, MaxMeleeRingSlots);
        var step = 360f / count;
        var angles = new float[count];
        for (var i = 0; i < count; i++)
        {
            var k = (i + 1) / 2;
            angles[i] = (i % 2 == 1 ? -1f : 1f) * k * step;
            if (angles[i] > 180f) angles[i] -= 360f;
        }
        return angles;
    }

    /// <summary>Chord between two bearings (degrees) on a ring.</summary>
    public static float ChordLength(float ringRadius, float angleADeg, float angleBDeg)
    {
        var d = MathF.Abs(angleADeg - angleBDeg) % 360f;
        if (d > 180f) d = 360f - d;
        return 2f * ringRadius * MathF.Sin(d * MathF.PI / 360f);
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

    /// <summary>
    /// Slot preference relative to the target's facing: +1 straight in front, -1 straight behind.
    /// Scaled by the caller into a cost so attackers drift toward flanks/rear without overriding
    /// distance or crowding.
    /// </summary>
    public static float Frontness(float slotX, float slotZ, float targetX, float targetZ, float targetYaw)
    {
        var dx = slotX - targetX;
        var dz = slotZ - targetZ;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.001f)
            return 0f;
        return (dx * MathF.Sin(targetYaw) + dz * MathF.Cos(targetYaw)) / len;
    }
}
