using System;
using System.Numerics;

namespace CombatSimulator.Simulation;

/// <summary>
/// Pure 2D (XZ-plane) hit-shape tests shared by every combat path: existing AoE
/// resolution (<see cref="CombatEngine"/>), Action-Mode enemy telegraphs, and the
/// player melee hitbox. Keeping a single source guarantees "what you see is what
/// hits" — the telegraph overlay draws the same cone/circle/line these functions
/// test against.
/// </summary>
public static class CombatGeometry
{
    public static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static bool TryGetDirection2D(Vector3 from, Vector3 to, out Vector2 direction)
    {
        var delta = new Vector2(to.X - from.X, to.Z - from.Z);
        var len = delta.Length();
        if (len <= 0.001f)
        {
            direction = Vector2.Zero;
            return false;
        }

        direction = delta / len;
        return true;
    }

    /// <summary>Flat distance from <paramref name="source"/> to <paramref name="candidate"/> within <paramref name="radius"/>.</summary>
    public static bool IsInsideCircle(Vector3 source, Vector3 candidate, float radius)
        => Distance2D(source, candidate) <= radius;

    /// <summary>
    /// Broad frontal cone (90° total) from <paramref name="source"/> aimed at
    /// <paramref name="primaryTarget"/>, reaching <paramref name="radius"/> yalms.
    /// </summary>
    public static bool IsInsideCone(Vector3 source, Vector3 primaryTarget, Vector3 candidate, float radius)
    {
        if (!TryGetDirection2D(source, primaryTarget, out var forward) ||
            !TryGetDirection2D(source, candidate, out var toCandidate))
        {
            return false;
        }

        var distance = Distance2D(source, candidate);
        if (distance > radius)
            return false;

        // cos(45°) — half-angle of a 90° cone.
        return Vector2.Dot(forward, toCandidate) >= 0.70710677f;
    }

    /// <summary>
    /// Frontal cone with a caller-supplied full angle (degrees). Used by the player
    /// melee hitbox where the cone width is configurable.
    /// </summary>
    public static bool IsInsideConeAngle(Vector3 source, Vector3 forwardTarget, Vector3 candidate, float radius, float fullAngleDeg)
    {
        if (!TryGetDirection2D(source, forwardTarget, out var forward) ||
            !TryGetDirection2D(source, candidate, out var toCandidate))
        {
            return false;
        }

        if (Distance2D(source, candidate) > radius)
            return false;

        var halfCos = MathF.Cos(float.DegreesToRadians(fullAngleDeg) * 0.5f);
        return Vector2.Dot(forward, toCandidate) >= halfCos;
    }

    /// <summary>
    /// Rectangle from <paramref name="source"/> toward <paramref name="primaryTarget"/>,
    /// <paramref name="length"/> long and <paramref name="width"/> wide.
    /// </summary>
    public static bool IsInsideLine(Vector3 source, Vector3 primaryTarget, Vector3 candidate, float length, float width)
    {
        if (!TryGetDirection2D(source, primaryTarget, out var forward))
            return false;

        var toCandidate = new Vector2(candidate.X - source.X, candidate.Z - source.Z);
        var along = Vector2.Dot(toCandidate, forward);
        if (along < 0 || along > length)
            return false;

        var perpendicular = MathF.Abs(forward.X * toCandidate.Y - forward.Y * toCandidate.X);
        var halfWidth = MathF.Max(width > 0 ? width : 4f, 0.5f) * 0.5f;
        return perpendicular <= halfWidth;
    }
}
