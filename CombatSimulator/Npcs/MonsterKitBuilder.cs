// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Simulation;

namespace CombatSimulator.Npcs;

/// <summary>The Action-sheet columns a monster kit is chosen from (no Lumina types, so it tests offline).</summary>
public readonly record struct MonsterActionCandidate(
    uint ActionId,
    string Name,
    byte CastType,
    float Range,
    float EffectRange,
    float XAxisModifier,
    float CastSeconds,
    float RecastSeconds,
    uint Category,
    uint AttackType,
    bool CanTargetHostile,
    bool TargetsAllies,
    bool DeadTargetOnly,
    bool HasOmen,
    ushort TimelineId);

/// <summary>
/// Chooses which of a monster family's real actions a simulated monster uses. The family's
/// Action rows (linked through their AnimationEnd timeline) carry the real cast time, area and
/// omen; most AoE rows are self-targeted (CanTargetHostile false), so the filter accepts those
/// by shape instead of by target flag. Rows that would be unfair or meaningless here are
/// dropped: room-wide areas, charges and ground circles (no placement data), crosses/custom
/// shapes, ally-targeted and status-only rows.
/// </summary>
public static class MonsterKitBuilder
{
    /// <summary>Largest circle/cone radius kept; anything wider is an arena mechanic, not a mob skill.</summary>
    public const float MaxAreaRadius = 15f;
    /// <summary>Longest rectangle kept.</summary>
    public const float MaxLineLength = 20f;
    /// <summary>Casts longer than this are special mechanics (enrage timers, add phases).</summary>
    public const float MaxCastSeconds = 6f;

    private const uint CategorySpell = 2;
    private const uint CategoryWeaponskill = 3;

    public static bool IsUsable(in MonsterActionCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.DeadTargetOnly || c.TargetsAllies)
            return false;
        if (c.Category is not (CategorySpell or CategoryWeaponskill))
            return false; // abilities (cat 4) are mostly buffs/utility on monsters
        if (c.CastSeconds > MaxCastSeconds)
            return false;
        if (CastTypeMapping.IsCharge(c.CastType) || c.CastType == 7)
            return false;

        var shape = CastTypeMapping.Map(c.CastType, c.Range, c.CanTargetHostile);
        if (shape == AoeShape.Single)
        {
            // Only real cast types 1 reach here as damage; 6/11 collapse to Single too and are
            // area mechanics we can't draw, so they are dropped.
            return c.CastType == 1 && c.CanTargetHostile && c.AttackType != 0;
        }

        if (c.EffectRange <= 0f)
            return false;
        if (shape == AoeShape.Line ? c.EffectRange > MaxLineLength : c.EffectRange > MaxAreaRadius)
            return false;
        // An AoE with no attack type and no omen is a status cloud (Queasy Cloud, Slumber Cloud).
        return c.AttackType != 0 || c.HasOmen;
    }

    public static bool IsArea(in MonsterActionCandidate c)
        => CastTypeMapping.Map(c.CastType, c.Range, c.CanTargetHostile) != AoeShape.Single;

    /// <summary>
    /// Up to <paramref name="maxSkills"/> distinct skills: areas first (telegraphed ones before
    /// silent ones), keeping one slot for a single-target attack when the family has one so the
    /// monster still presses something between big casts. Deterministic for a given input.
    /// </summary>
    public static List<MonsterActionCandidate> Select(IEnumerable<MonsterActionCandidate> candidates, int maxSkills)
    {
        if (maxSkills <= 0)
            return new List<MonsterActionCandidate>();

        // Several rows often share one animation (per-duty copies of the same skill); one per
        // timeline, then one per name, preferring the row that has an omen and a cast.
        var distinct = candidates
            .Where(c => IsUsable(c))
            .OrderByDescending(c => c.HasOmen)
            .ThenByDescending(c => c.CastSeconds > 0f)
            .ThenBy(c => c.ActionId)
            .GroupBy(c => c.TimelineId)
            .Select(g => g.First())
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var areas = distinct.Where(c => IsArea(c))
            .OrderByDescending(c => c.HasOmen)
            .ThenByDescending(c => c.CastSeconds)
            .ThenBy(c => c.ActionId)
            .ToList();
        var singles = distinct.Where(c => !IsArea(c))
            .OrderBy(c => c.ActionId)
            .ToList();

        var areaSlots = singles.Count > 0 && maxSkills > 1 ? maxSkills - 1 : maxSkills;
        return areas.Take(areaSlots)
            .Concat(singles)
            .Take(maxSkills)
            .ToList();
    }

    /// <summary>
    /// Distance at which the monster commits the skill. Kept within melee so a melee monster never
    /// flips to ranged intent; a caster-centred area only fires when the target is actually inside.
    /// </summary>
    public static float EngageRange(in MonsterActionCandidate c)
    {
        var shape = CastTypeMapping.Map(c.CastType, c.Range, c.CanTargetHostile);
        return shape switch
        {
            AoeShape.CircleSelf or AoeShape.Cone or AoeShape.Line or AoeShape.Donut =>
                Math.Clamp(c.EffectRange * 0.75f, 2.5f, 5f),
            _ => 3f,
        };
    }

    /// <summary>
    /// Reuse time. Monster recasts are authored around a server AI that rotates many enemies; here
    /// one monster faces the player alone, so a floor keeps a skill from repeating every swing.
    /// </summary>
    public static float Cooldown(in MonsterActionCandidate c)
        => Math.Clamp(MathF.Max(c.RecastSeconds, 10f) + c.CastSeconds, 8f, 40f);

    /// <summary>
    /// Damage relative to the monster's auto-attack. The virtual action model scores self-centred
    /// monster AoEs like player AoEs (half budget, but long casts push it up), so it is bounded to a
    /// band that reads as "a skill" without one-shotting the player.
    /// </summary>
    public static int Potency(int modelPotency, int autoAttackPotency, bool isArea, float castSeconds)
    {
        var auto = Math.Max(1, autoAttackPotency);
        var floor = auto * (isArea ? 1.4f : 1.2f);
        // A long, telegraphed cast is the monster's big hit: allow more when it can be dodged.
        var ceiling = auto * (castSeconds >= 2f ? 3.0f : 2.0f);
        return (int)MathF.Round(Math.Clamp(modelPotency, floor, ceiling));
    }
}
