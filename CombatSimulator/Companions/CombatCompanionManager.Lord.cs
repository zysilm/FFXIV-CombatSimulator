// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Simulation;

namespace CombatSimulator.Companions;

/// <summary>
/// The lord's side of the allies' controller: what the battle tells it about the lord's body, the
/// retainers at the body, and their surrender. The Warring States rules themselves (the lord's fall, the
/// one praying over the body, the guard's leash and target choice) are rule parts
/// (<see cref="WarringStatesLordRule"/>, <see cref="LordGuardRule"/>, <see cref="CorpseSeizureRule"/>;
/// refactor stage B5).
/// </summary>
public unsafe partial class CombatCompanionManager
{
    public const float PrayerDamageScale = LordGuardRule.PrayerDamageScale;
    public const float LordContestRadius = LordGuardRule.LordContestRadius;

    /// <summary>Where the lord's body actually lies (the ragdoll, once it falls); null: where the lord died.</summary>
    public Func<Vector3?>? LordBodyPosition { private get; set; }

    /// <summary>The lord's body has been carried off by the enemy (set by the battle).</summary>
    public bool LordCorpseCaptured { get; set; }

    /// <summary>The enemies sent to carry off the lord's body (set by the battle each frame).</summary>
    public HashSet<uint> LordSeizers { get; } = new();

    /// <summary>Whether a retainer is left who can stand before the lord's body (alive, not captive).</summary>
    public bool HasRetainer
    {
        get
        {
            foreach (var companion in companions)
                if (CanRescue(companion))
                    return true;
            return false;
        }
    }

    /// <summary>The companion praying over the lord's body, if any.</summary>
    public uint PrayerId => ruleParts.Get<LordGuardRule>().PrayerId;

    /// <summary>
    /// Whether a retainer who can still fight for it (alive, not captive) stands within
    /// <paramref name=radius/> of the lord's body: while one does, nobody can carry it off.
    /// </summary>
    public bool LordBodyContested(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
        => RetainersAtBody(body, radius, out nearestGuard, out nearestDistance) > 0;

    /// <summary>How many retainers who can still fight for it stand within <paramref name="radius"/> of the lord's body.</summary>
    public int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
    {
        var count = 0;
        nearestGuard = null;
        nearestDistance = float.MaxValue;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion))
                continue;
            var d = FlatDistance(world.PositionOf(companion), body);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearestGuard = companion.Name;
            }
            if (d <= radius)
                count++;
        }
        return count;
    }

    /// <summary>The lord's body has been carried off: whoever is left is taken.</summary>
    public void SurrenderRetainers()
    {
        ruleParts.Get<LordGuardRule>().ReleasePrayer(ruleParts.Context);
        foreach (var companion in companions)
            if (CanRescue(companion))
                combatEngine.TakeCaptive(companion.State);
    }

    /// <summary>What one of the player's side is doing, for the HP bars (the battle rules' word).</summary>
    public string StatusOf(SimulatedEntityState state) => ruleParts.StatusWord(state);
}
