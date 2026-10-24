// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Geometry of an action's effect area, derived from the game's CastType column (see
/// <see cref="CastTypeMapping"/>). Single is the safe default for anything we don't recognise.
/// Serialized by name in Actions.json, so new members may only be appended.
/// </summary>
public enum AoeShape
{
    Single,        // one target only
    Circle,        // circle centred on the (hostile) target
    Cone,          // cone from the caster toward the target
    Line,          // rectangle from the caster toward the target (width = XAxisModifier)
    CircleSelf,    // circle centred on the caster (PBAoE)
    GroundCircle,  // circle placed at a ground location
    Donut,         // ring centred on the caster
}

/// <summary>
/// Pure CastType → shape rules, verified against the Action sheet (scratchpad experiment over
/// the 5,138 monster-family rows plus every player damage action):
/// <list type="bullet">
/// <item>1 single target.</item>
/// <item>2 circle. Centred on the target only when the action can be aimed at a hostile and has
/// a range; the 636 self-targeted monster rows and player PBAoEs (Overpower, Holy: Range 0,
/// CanTargetHostile false) are centred on the caster.</item>
/// <item>3 / 13 cone from the caster (450/360 rows carry a <c>gl_fan###</c> omen; the digits
/// are the full angle). 3 adds the caster's hitbox to the reach, 13 does not.</item>
/// <item>4 / 12 rectangle from the caster, EffectRange long, XAxisModifier wide (4 adds hitbox).</item>
/// <item>5 circle on the caster plus its hitbox — every one of the 617 rows is self-targeted.
/// (The old comment calling only this type self-centred was right about 5 and wrong about 2.)</item>
/// <item>7 ground circle, 8 charge (rectangle from caster to target, EffectRange 0), 10 donut.</item>
/// <item>6 (custom, mostly room-wide r100) and 11 (cross) stay Single: their real area needs
/// per-encounter data we do not have.</item>
/// </list>
/// </summary>
public static class CastTypeMapping
{
    /// <summary>Default full cone angle when the omen name does not say (matches the old fixed 90°).</summary>
    public const float DefaultConeAngleDeg = 90f;

    /// <summary>The sheet has no donut inner radius; this fraction of the outer radius stands in.</summary>
    public const float DonutInnerFraction = 0.4f;

    public static AoeShape Map(byte castType, float range, bool canTargetHostile) => castType switch
    {
        1 => AoeShape.Single,
        2 => canTargetHostile && range > 0f ? AoeShape.Circle : AoeShape.CircleSelf,
        3 or 13 => AoeShape.Cone,
        4 or 12 or 8 => AoeShape.Line,
        5 => AoeShape.CircleSelf,
        7 => AoeShape.GroundCircle,
        10 => AoeShape.Donut,
        _ => AoeShape.Single,
    };

    /// <summary>
    /// Context-free mapping kept for callers that only have the column (the database exporter's
    /// reference field). Type 2 assumes the common aimed form here.
    /// </summary>
    public static AoeShape Map(byte castType) => Map(castType, 1f, true);

    /// <summary>Cast types whose reach is measured from the caster's hitbox edge, not its centre.</summary>
    public static bool AddsCasterHitbox(byte castType) => castType is 3 or 4 or 5;

    /// <summary>Charges reach the target, wherever it stands; their EffectRange is 0.</summary>
    public static bool IsCharge(byte castType) => castType == 8;

    /// <summary>
    /// Full cone angle from an omen name such as <c>gl_fan090_1bf</c> / <c>er_gl_fan120_1bf</c>.
    /// Measured omen angles: 30, 60, 90, 120, 135, 150, 180, 270.
    /// </summary>
    public static float ParseConeAngleDeg(string? omenPath)
    {
        if (string.IsNullOrEmpty(omenPath))
            return DefaultConeAngleDeg;

        var idx = omenPath.IndexOf("fan", StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + 6 > omenPath.Length)
            return DefaultConeAngleDeg;

        var digits = omenPath.AsSpan(idx + 3, 3);
        if (!int.TryParse(digits, out var deg) || deg < 10 || deg > 360)
            return DefaultConeAngleDeg;
        return deg;
    }
}
