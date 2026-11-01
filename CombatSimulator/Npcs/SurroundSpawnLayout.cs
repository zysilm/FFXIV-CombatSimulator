using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Npcs;

/// <summary>
/// Where virtual enemies stand when they are not spawned "in front": a loose, layered ring around
/// the player instead of one circle.
///
/// The old layout put every enemy at exactly the spawn distance on a random bearing, so a group read
/// as a single flat ring with chance overlaps. Here the nearest enemy still stands at the spawn
/// distance, but the others are staggered outward through a depth band, so the group has near, mid
/// and far members from any viewing direction. Bearings follow the golden angle, which spreads any
/// number of enemies evenly around the player without two landing side by side; the depth of each
/// slot follows a bit-reversal sequence, so the first few enemies already alternate between the inner
/// and outer edge of the band. Both get a small seeded jitter so the formation looks organic rather
/// than drawn with a compass, and every slot is checked against the earlier ones and nudged outward
/// until nobody stands inside another's space.
///
/// Positions depend only on the slot index and the seed, so a group that arrives over several frames
/// is laid out consistently and a regenerated group with a fresh seed scatters differently.
/// </summary>
public static class SurroundSpawnLayout
{
    private const float GoldenAngle = 2.39996323f;          // rad
    private const float MinSeparation = 2.0f;               // metres between any two enemies
    private const float MinDepthSpan = 3.0f;                // metres of depth even for a small group
    private const float AreaPerEnemy = 5.0f;                // m², keeps larger groups from packing tighter
    private const float BearingJitter = 0.21f;              // rad (~12 deg)
    private const float DepthJitterFraction = 0.12f;        // of the depth span
    private const float NudgeStep = 0.5f;                   // metres per outward nudge when too close
    private const int MaxNudges = 16;

    /// <summary>Horizontal offset from the player for the given slot (X/Z in the result, Y = 0).</summary>
    public static Vector3 Offset(int slot, float distance, int seed)
    {
        slot = Math.Max(0, slot);
        var placed = new List<Vector2>(slot + 1);
        var result = Vector2.Zero;
        for (var k = 0; k <= slot; k++)
        {
            result = Place(k, distance, seed, placed);
            placed.Add(result);
        }

        return new Vector3(result.X, 0f, result.Y);
    }

    private static Vector2 Place(int k, float distance, int seed, List<Vector2> placed)
    {
        var inner = MathF.Max(0.5f, distance);
        var span = DepthSpan(inner, k + 1);

        var bearing = Hash01(seed, 0, 0) * MathF.Tau
                      + k * GoldenAngle
                      + (Hash01(seed, k, 1) * 2f - 1f) * BearingJitter;

        var depth = BitReversal(k) * span
                    + (Hash01(seed, k, 2) * 2f - 1f) * DepthJitterFraction * span;
        depth = Math.Clamp(depth, 0f, span);

        var dir = new Vector2(MathF.Sin(bearing), MathF.Cos(bearing));
        var radius = inner + depth;
        var candidate = dir * radius;
        for (var n = 0; n < MaxNudges && TooClose(candidate, placed); n++)
        {
            radius += NudgeStep;
            candidate = dir * radius;
        }

        return candidate;
    }

    /// <summary>
    /// Depth of the band for a group of <paramref name="count"/>: at least <see cref="MinDepthSpan"/>,
    /// wider once the annulus would otherwise be too crowded for the separation we want.
    /// </summary>
    private static float DepthSpan(float inner, int count)
    {
        var needed = MathF.Sqrt(inner * inner + count * AreaPerEnemy / MathF.PI) - inner;
        return MathF.Max(MinDepthSpan, needed);
    }

    private static bool TooClose(Vector2 candidate, List<Vector2> placed)
    {
        foreach (var p in placed)
        {
            if (Vector2.DistanceSquared(candidate, p) < MinSeparation * MinSeparation)
                return true;
        }

        return false;
    }

    /// <summary>0, 0.5, 0.25, 0.75, 0.125, ... — each new value lands in the widest remaining gap.</summary>
    private static float BitReversal(int k)
    {
        var value = 0f;
        var scale = 0.5f;
        for (var bits = (uint)k; bits != 0; bits >>= 1, scale *= 0.5f)
        {
            if ((bits & 1) != 0)
                value += scale;
        }

        return value;
    }

    private static float Hash01(int seed, int k, int salt)
    {
        var h = (uint)seed * 0x9E3779B1u ^ (uint)k * 0x85EBCA77u ^ (uint)salt * 0xC2B2AE3Du;
        h ^= h >> 15; h *= 0x2C1B3C6Du;
        h ^= h >> 12; h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) * (1f / 16777216f);
    }
}
