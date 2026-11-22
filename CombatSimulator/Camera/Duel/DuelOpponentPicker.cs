// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Camera.Duel;

/// <summary>One enemy the duel camera could lock its framing onto.</summary>
public readonly record struct DuelCandidate(
    uint Id,
    Vector3 Position,
    bool Alive,
    /// <summary>It is fighting the player (its target is the player).</summary>
    bool AttackingPlayer,
    /// <summary>An attack of its on the player is winding up right now.</summary>
    bool Striking,
    /// <summary>The player has it targeted.</summary>
    bool Targeted);

/// <summary>
/// Who the duel camera frames the player against. Game-free.
///
/// Every living enemy in range gets a score in metres: its distance from the player, less a bonus when it is
/// attacking the player, a larger one while an attack of its on the player is winding up, and one when the player
/// has it targeted. The lowest score wins, with hysteresis: the current opponent is kept until another has been
/// better by a clear margin for a while. An opponent that goes down is kept for a moment (the camera stays on the
/// fall), then the next one is picked; nobody left means nobody (the camera frames the player alone).
/// </summary>
public sealed class DuelOpponentPicker
{
    public const float AttackingBonus = 3f, StrikingBonus = 6f, TargetedBonus = 3f;
    /// <summary>Another enemy must score this much better than the current one ...</summary>
    public const float SwitchMargin = 2.5f;
    /// <summary>... for this long.</summary>
    public const float SwitchHold = 0.8f;
    /// <summary>The camera stays on an opponent that went down for this long.</summary>
    public const float DownHold = 1.4f;
    /// <summary>Enemies farther than this are not picked; the current one is dropped beyond <see cref="DropRange"/>.</summary>
    public const float PickRange = 40f, DropRange = 50f;

    private float betterFor, downFor;
    private uint challenger;

    /// <summary>The current opponent's id (0: nobody).</summary>
    public uint Current { get; private set; }

    public void Reset()
    {
        Current = 0;
        challenger = 0;
        betterFor = downFor = 0f;
    }

    public static float Score(in DuelCandidate c, Vector3 player)
    {
        var d = c.Position - player;
        d.Y = 0f;
        return d.Length() - (c.Striking ? StrikingBonus : c.AttackingPlayer ? AttackingBonus : 0f) - (c.Targeted ? TargetedBonus : 0f);
    }

    /// <summary>One frame. Returns the opponent's id (0: nobody); <paramref name="reason"/> says why it changed
    /// (null: it did not).</summary>
    public uint Pick(IReadOnlyList<DuelCandidate> candidates, Vector3 player, float dt, out string? reason)
    {
        reason = null;
        dt = Math.Clamp(float.IsFinite(dt) ? dt : 0f, 0f, 0.1f);

        var bestId = 0u;
        var best = float.PositiveInfinity;
        var haveCurrent = false;
        DuelCandidate current = default;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.Id == 0) continue;
            if (c.Id == Current)
            {
                haveCurrent = true;
                current = c;
            }
            if (!c.Alive) continue;
            var range = c.Position - player;
            range.Y = 0f;
            if (range.Length() > PickRange) continue;
            var score = Score(in c, player);
            if (score < best)
            {
                best = score;
                bestId = c.Id;
            }
        }

        if (Current == 0)
        {
            if (bestId != 0) return Take(bestId, "first", out reason);
            return 0;
        }

        // The current opponent is gone (despawned, deselected).
        if (!haveCurrent)
            return Take(bestId, bestId != 0 ? "the last one is gone" : "nobody left", out reason);

        // It went down: stay on the fall for a moment, then move on.
        if (!current.Alive)
        {
            downFor += dt;
            if (downFor < DownHold) return Current;
            return Take(bestId, bestId != 0 ? "the last one is down" : "nobody left", out reason);
        }
        downFor = 0f;

        var away = current.Position - player;
        away.Y = 0f;
        if (away.Length() > DropRange)
            return Take(bestId == Current ? 0u : bestId, "the last one is out of range", out reason);

        // Another one has to be clearly better, and stay so.
        if (bestId != 0 && bestId != Current && best < Score(in current, player) - SwitchMargin)
        {
            if (challenger != bestId)
            {
                challenger = bestId;
                betterFor = 0f;
            }
            betterFor += dt;
            if (betterFor >= SwitchHold)
            {
                DuelCandidate other = default;
                for (var i = 0; i < candidates.Count; i++)
                    if (candidates[i].Id == bestId) other = candidates[i];
                return Take(bestId, other.Striking ? "it is striking at the player" : other.AttackingPlayer && !current.AttackingPlayer ? "it is attacking the player" : other.Targeted ? "the player targets it" : "it is nearer", out reason);
            }
        }
        else
        {
            challenger = 0;
            betterFor = 0f;
        }
        return Current;
    }

    private uint Take(uint id, string why, out string? reason)
    {
        reason = id != Current ? why : null;
        Current = id;
        challenger = 0;
        betterFor = downFor = 0f;
        return id;
    }
}
