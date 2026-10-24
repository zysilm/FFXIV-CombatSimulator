// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Simulation;

public enum TelegraphKind
{
    Circle,
    Cone,
    Rect,
    Donut,
}

/// <summary>
/// A snapshotted enemy attack area: where it is, which way it points and how big it is, frozen
/// when the attack is committed. The overlay draws exactly this, the omen VFX is placed from it,
/// and the hit test at resolve uses it — so what the player sees is what hits, and stepping out
/// of the drawn area during the windup is a real dodge.
/// <para><see cref="Rotation"/> uses the game's yaw convention: forward = (sin r, cos r) on XZ.</para>
/// </summary>
public readonly record struct TelegraphShape(
    TelegraphKind Kind,
    Vector3 Origin,
    float Rotation,
    float Radius,
    float Width,
    float InnerRadius,
    float HalfAngleRad)
{
    /// <summary>True when the shape came from a real area (not the single-target fallback circle).</summary>
    public bool IsArea { get; init; }

    public Vector2 Forward2D => new(MathF.Sin(Rotation), MathF.Cos(Rotation));

    public bool Contains(Vector3 point)
    {
        var rel = new Vector2(point.X - Origin.X, point.Z - Origin.Z);
        var dist = rel.Length();
        switch (Kind)
        {
            case TelegraphKind.Circle:
                return dist <= Radius;
            case TelegraphKind.Donut:
                return dist <= Radius && dist >= InnerRadius;
            case TelegraphKind.Cone:
                if (dist > Radius)
                    return false;
                if (dist <= 0.001f)
                    return true; // standing on the caster's centre
                return Vector2.Dot(Forward2D, rel / dist) >= MathF.Cos(HalfAngleRad);
            case TelegraphKind.Rect:
                var f = Forward2D;
                var along = Vector2.Dot(rel, f);
                if (along < 0f || along > Radius)
                    return false;
                var perp = MathF.Abs(f.X * rel.Y - f.Y * rel.X);
                return perp <= HalfWidth;
            default:
                return false;
        }
    }

    public float HalfWidth => MathF.Max(Width > 0f ? Width : 4f, 0.5f) * 0.5f;

    /// <summary>Game yaw facing from <paramref name="from"/> to <paramref name="to"/> (0 when they coincide).</summary>
    public static float YawToward(Vector3 from, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        return dx * dx + dz * dz < 1e-6f ? 0f : MathF.Atan2(dx, dz);
    }

    /// <summary>
    /// Snapshot an action's area. Target-centred circles sit on the target; everything else is
    /// anchored on the caster and aimed at the target. Cast types 3/4/5 measure from the caster's
    /// hitbox edge (as the game does), charges reach past the target, and a single-target action
    /// (or an area with no size) becomes the legacy <paramref name="fallbackRadius"/> circle on
    /// the target.
    /// </summary>
    public static TelegraphShape Build(
        AoeShape shape,
        byte castType,
        float radius,
        float width,
        float coneAngleDeg,
        float innerRadius,
        Vector3 caster,
        float casterHitbox,
        Vector3 target,
        float fallbackRadius)
    {
        var yaw = YawToward(caster, target);
        var pad = CastTypeMapping.AddsCasterHitbox(castType) ? MathF.Max(0f, casterHitbox) : 0f;
        var isCharge = CastTypeMapping.IsCharge(castType);
        if (shape == AoeShape.Single || (radius <= 0f && !(shape == AoeShape.Line && isCharge)))
            return new TelegraphShape(TelegraphKind.Circle, target, yaw, fallbackRadius, 0f, 0f, 0f);

        return shape switch
        {
            AoeShape.Circle or AoeShape.GroundCircle =>
                new TelegraphShape(TelegraphKind.Circle, target, yaw, radius, 0f, 0f, 0f) { IsArea = true },
            AoeShape.CircleSelf =>
                new TelegraphShape(TelegraphKind.Circle, caster, yaw, radius + pad, 0f, 0f, 0f) { IsArea = true },
            AoeShape.Donut =>
                new TelegraphShape(TelegraphKind.Donut, caster, yaw, radius, 0f, MathF.Max(0f, innerRadius), 0f) { IsArea = true },
            AoeShape.Cone =>
                new TelegraphShape(TelegraphKind.Cone, caster, yaw, radius + pad, 0f, 0f,
                    float.DegreesToRadians(Math.Clamp(coneAngleDeg, 1f, 360f)) * 0.5f) { IsArea = true },
            AoeShape.Line =>
                new TelegraphShape(TelegraphKind.Rect, caster, yaw,
                    isCharge && radius <= 0f
                        ? MathF.Sqrt(MathF.Pow(target.X - caster.X, 2) + MathF.Pow(target.Z - caster.Z, 2)) + 1f
                        : radius + pad,
                    width, 0f, 0f) { IsArea = true },
            _ => new TelegraphShape(TelegraphKind.Circle, target, yaw, fallbackRadius, 0f, 0f, 0f),
        };
    }
}
