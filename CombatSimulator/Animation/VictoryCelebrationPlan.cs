// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Animation;

public enum CelebrationMood
{
    /// <summary>Living enemies after the player's side is wiped.</summary>
    EnemyVictory,
    /// <summary>Living companions after every enemy is down.</summary>
    PartyVictory,
    /// <summary>Low-key idle loops for actors a story leaves out of its scene.</summary>
    Bystander,
}

public readonly record struct CelebrationEmote(uint EmoteId, float Weight);

/// <summary>Pure data and timing for <see cref="VictoryCelebrationDirector"/>; no game access.</summary>
public static class VictoryCelebrationPlan
{
    // Emote rows checked against the live Emote sheet (victorypose, cheer, laugh, fist pump, flex,
    // beckon, clap, humble triumph, battle stance). Missing rows resolve to nothing and drop out.
    public static readonly IReadOnlyList<CelebrationEmote> VictoryEmotes = new CelebrationEmote[]
    {
        new(122, 3f), new(6, 2f), new(21, 2f), new(195, 2f), new(139, 1f),
        new(8, 1f), new(7, 1f), new(275, 1f), new(121, 1f),
    };

    // Lookout, think, stretch, shrug, converse, at ease, battle stance: present but not performing.
    public static readonly IReadOnlyList<CelebrationEmote> BystanderEmotes = new CelebrationEmote[]
    {
        new(22, 2f), new(39, 2f), new(37, 1f), new(33, 1f), new(159, 1f), new(165, 2f), new(121, 1f),
    };

    public static IReadOnlyList<CelebrationEmote> PoolFor(CelebrationMood mood)
        => mood == CelebrationMood.Bystander ? BystanderEmotes : VictoryEmotes;

    public const float MaxStaggerSeconds = 5f;

    /// <summary>How long an emote holds before the actor changes to another one. Monsters replay
    /// their one-shot flourish on the shorter monster cadence instead.</summary>
    public static (float Min, float Max) Interval(CelebrationMood mood, bool humanoid)
        => !humanoid ? (4.5f, 8f) : mood == CelebrationMood.Bystander ? (9f, 16f) : (6f, 11f);

    public static float BoundedStagger(float seconds)
        => float.IsFinite(seconds) ? Math.Clamp(seconds, 0f, MaxStaggerSeconds) : 0f;
}

/// <summary>
/// One actor's cue clock: the first play waits a random stagger so a group does not move in
/// lockstep, then each replay or emote change waits a random interval.
/// </summary>
public sealed class CelebrationCue
{
    private readonly float minInterval, maxInterval;
    private float elapsed, nextAt;

    public int Plays { get; private set; }

    public CelebrationCue(float startDelay, float minInterval, float maxInterval)
    {
        nextAt = float.IsFinite(startDelay) ? Math.Max(0f, startDelay) : 0f;
        // Math.Max propagates NaN, so filter non-finite values before bounding.
        this.minInterval = float.IsFinite(minInterval) ? Math.Max(0.5f, minInterval) : 0.5f;
        this.maxInterval = float.IsFinite(maxInterval) ? Math.Max(this.minInterval, maxInterval) : this.minInterval;
    }

    /// <summary>True on the tick a play is due. A hitch fires at most once per tick.</summary>
    public bool Advance(float dt, double roll)
    {
        if (float.IsFinite(dt) && dt > 0f) elapsed += Math.Min(dt, 0.25f);
        if (elapsed < nextAt) return false;
        var t = Math.Clamp(double.IsFinite(roll) ? roll : 0d, 0d, 1d);
        nextAt = elapsed + minInterval + (float)t * (maxInterval - minInterval);
        Plays++;
        return true;
    }
}
