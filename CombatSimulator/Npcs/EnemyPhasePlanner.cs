// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSimulator.Npcs;

/// <summary>
/// HP phases for a generated kit: the enemy's longest cast is held back until it is hurt, and a
/// stronger copy of its biggest area skill becomes a last-stand "enrage" when it is nearly dead —
/// so a fight escalates instead of looking the same from full HP to the kill. Uses the existing
/// <see cref="NpcSkill.HpThreshold"/> gate the AI already honours.
/// </summary>
public static class EnemyPhasePlanner
{
    public const float PhaseThreshold = 0.5f;
    public const float EnrageThreshold = 0.3f;
    public const float EnragePotencyScale = 1.5f;
    public const float EnrageMinCooldown = 30f;
    /// <summary>Casts at least this long count as a "big" skill worth gating.</summary>
    public const float MinPhaseCastSeconds = 2f;

    /// <summary>Applies phases in place to a per-enemy kit (skills must already be cloned).</summary>
    public static void Apply(List<NpcSkill> kit)
    {
        if (kit.Count == 0)
            return;

        // Phase skill: only when something else remains usable at full HP.
        if (kit.Count >= 2)
        {
            var phase = kit
                .Where(s => s.NativeCastTime >= MinPhaseCastSeconds && s.HpThreshold >= 1f)
                .OrderByDescending(s => s.Radius > 0f)
                .ThenByDescending(s => s.NativeCastTime)
                .ThenBy(s => s.ActionId)
                .FirstOrDefault();
            if (phase != null)
                phase.HpThreshold = PhaseThreshold;
        }

        // Enrage: a harder-hitting copy of the biggest area skill (or the strongest skill).
        var basis = kit
            .OrderByDescending(s => s.Radius > 0f)
            .ThenByDescending(s => s.Radius)
            .ThenByDescending(s => s.Potency)
            .ThenBy(s => s.ActionId)
            .First();
        var enrage = basis.CloneDefinition();
        enrage.Name = $"{basis.Name} (Enraged)";
        enrage.Potency = (int)MathF.Round(basis.Potency * EnragePotencyScale);
        enrage.Cooldown = MathF.Max(basis.Cooldown, EnrageMinCooldown);
        enrage.HpThreshold = EnrageThreshold;
        enrage.Priority = kit.Count;
        kit.Add(enrage);
    }
}
