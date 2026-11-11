// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Simulation;

// Pure action description, kept apart from ActionDataProvider (which reads game data) so the offline
// tests can link it.

public enum SimDamageType
{
    Physical,
    Magical,
    Unique,
}

/// <summary>
/// What an action is FOR, derived from the game's targeting/role columns. Enemies only ever
/// cast <see cref="Damage"/> actions; <see cref="Support"/> (buffs/heals/utility, incl. role
/// actions) and <see cref="Raise"/> are excluded from the enemy kit. Splitting Support further
/// into buff vs heal is deferred (needs StatusGainSelf/cure data we don't yet consume).
/// </summary>
public enum ActionIntent
{
    Damage,
    Support,
    Raise,
}

// AoeShape lives in CastTypeMapping.cs (pure, linked by the offline tests).

public class ActionData
{
    public uint ActionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Potency { get; set; }
    public float CastTime { get; set; }
    public float RecastTime { get; set; }
    public int RecastGroup { get; set; }
    public float Range { get; set; }
    public float Radius { get; set; }
    public AoeShape Shape { get; set; } = AoeShape.Single;
    public float Width { get; set; }
    /// <summary>Raw CastType column; decides hitbox padding and charges, which the shape alone can't.</summary>
    public byte CastType { get; set; }
    /// <summary>Full cone angle (degrees), read from the omen name; 90 when unknown.</summary>
    public float ConeAngleDeg { get; set; } = CastTypeMapping.DefaultConeAngleDeg;
    /// <summary>Donut safe radius. The sheet has none, so it is a fraction of the outer radius.</summary>
    public float InnerRadius { get; set; }
    /// <summary>Ground-marker VFX for this action (vfx/omen/eff/…), set only when the file exists.</summary>
    public string OmenVfxPath { get; set; } = string.Empty;
    public bool CanTargetHostile { get; set; }
    public SimDamageType DamageType { get; set; }
    public int NativeMpCost { get; set; }
    public int MpCost { get; set; }
    public bool IsComboAction { get; set; }
    public uint ComboFrom { get; set; }
    public int ComboPotency { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public float AnimationDuration { get; set; }
    public bool IsPlayerAction { get; set; } = true;
    public ActionIntent Intent { get; set; } = ActionIntent.Damage;
    public ushort AnimationStartTimelineId { get; set; }
    public ushort AnimationEndTimelineId { get; set; }

    // VFX paths resolved from Lumina data + TMB files
    public string CastVfxPath { get; set; } = string.Empty;
    public string StartVfxPath { get; set; } = string.Empty;
    public List<string> CasterVfxPaths { get; set; } = new();
    public List<string> TargetVfxPaths { get; set; } = new();
}
