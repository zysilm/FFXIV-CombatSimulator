// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Voice;

/// <summary>What the director reads off one enemy each frame.</summary>
public readonly record struct VoiceActorSnapshot(bool Alive, bool Engaged, bool Casting, int Hp, int MaxHp);

/// <summary>
/// Turns per-frame enemy state into voice events by watching transitions. Watching state instead
/// of adding raise points keeps the AI and engine untouched, and catches damage from every source
/// (player, companions, DoTs) the same way.
/// </summary>
public sealed class VoiceActorWatch
{
    /// <summary>One frame's HP loss of at least this share of max HP is a "heavy hit".</summary>
    public const float HeavyHitFraction = 0.12f;
    public const float LowHpFraction = 0.30f;

    private bool seeded;
    private VoiceActorSnapshot last;
    private bool lowHpSpoken;

    /// <summary>
    /// The first observation only records state: an enemy already mid-fight when voice is switched
    /// on (or when it is first seen) must not suddenly shout "engage".
    /// </summary>
    public void Observe(in VoiceActorSnapshot now, List<VoiceEvent> output)
    {
        if (!seeded)
        {
            seeded = true;
            last = now;
            lowHpSpoken = now.MaxHp > 0 && now.Hp < now.MaxHp * LowHpFraction;
            return;
        }

        if (last.Alive && !now.Alive)
        {
            output.Add(VoiceEvent.Death);
            last = now;
            return;
        }

        // Revived / reset to full: a fresh life gets its low-HP line again.
        if (!last.Alive && now.Alive || now.MaxHp > 0 && now.Hp >= now.MaxHp && last.Hp < last.MaxHp)
            lowHpSpoken = false;

        if (now.Alive)
        {
            if (!last.Engaged && now.Engaged)
                output.Add(VoiceEvent.Engage);
            if (!last.Casting && now.Casting)
                output.Add(VoiceEvent.Skill);

            if (now.MaxHp > 0 && last.Alive)
            {
                var lost = last.Hp - now.Hp;
                if (!lowHpSpoken && now.Hp < now.MaxHp * LowHpFraction && last.Hp >= now.MaxHp * LowHpFraction)
                {
                    lowHpSpoken = true;
                    output.Add(VoiceEvent.LowHp);
                }
                else if (lost >= now.MaxHp * HeavyHitFraction)
                {
                    output.Add(VoiceEvent.Hurt);
                }
            }
        }

        last = now;
    }
}
