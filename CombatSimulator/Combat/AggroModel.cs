// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Combat;

/// <summary>One fighter's target-selection state: what it holds, since when, until when, and its own generator.</summary>
public sealed class AggroState
{
    public uint Target;
    public float HeldSince;
    public float DwellUntil;
    public bool HitByNew;
    /// <summary>Allies: the player's focus target when this one last looked.</summary>
    public uint Focus;
    /// <summary>Enemies going for the lord's body: who held it when this one last looked.</summary>
    public uint Holder;
    public Random Rng = null!;
    public readonly Dictionary<uint, float> LastHitBy = new();
}

/// <summary>
/// Who fights whom, for both sides, by chance (docs/PLUGIN_ARCHITECTURE.md §4): each fighter draws its
/// target at random, weighted by threat and the situation, and keeps it a few seconds; it draws again
/// only when something happens (its target falls, its time is up, someone new hits it, an outside
/// change). The same weights for allies and enemies. Pure: moved unchanged from
/// CombatCompanionManager.Aggro.cs (refactor stage B1); the situation multipliers are applied by the
/// caller. Tests/TargetingSim links it.
/// </summary>
public static class AggroModel
{
    // How long a drawn target is kept, and the shortest hold before being hit by someone new may
    // trigger a new draw; "someone new" is anyone who had not hit it in this window.
    public const float AggroDwellMin = 4f;
    public const float AggroDwellMax = 8f;
    public const float AggroHitHold = 2.5f;
    public const float NewAttackerWindow = 5f;

    // w = (floor + gain * share^2) / crowding / distance * finish * mutual * sticky * situation.
    public const float ThreatFloor = 0.25f;
    public const float ThreatGain = 1.5f;
    // Crowding: others of my kind (melee or ranged) on it count fully, the other kind half.
    public const float CrowdK = 4f;
    public const float AggroDistanceScale = 10f;
    public const float FinishGain = 0.5f;
    public const float MutualGain = 1.5f;
    public const float StickyGain = 6f;
    // Situation multipliers (applied by the caller).
    public const float FocusGain = 2f;
    public const float AttackingPlayerGain = 1.5f;
    // Enemies going for the lord's body on whoever holds it.
    public const float TaskGain = 100f;
    // Guards of the body on the enemies going for it.
    public const float GuardSeizerGain = 30f;
    // Someone kneeling to raise or pray (x4 damage, not fighting back): an opening for the enemy, and
    // a friend to cover for the allies (on the enemies hitting it).
    public const float ExposedGain = 3f;
    // An enemy a guard cannot reach without leaving the body.
    public const float UnreachableGain = 0.05f;

    /// <summary>The one choosing: where it stands, how far it reaches, its style, and what it holds now (0: nothing live).</summary>
    public readonly record struct Self(uint Id, Vector3 Position, float Reach, bool Ranged, uint CurrentTarget);

    /// <summary>One it may choose: where it stands, its share of HP, and whom it is fighting.</summary>
    public readonly record struct Candidate(uint Id, Vector3 Position, float HpShare, uint TargetId);

    /// <summary>Ranged and caster styles count as ranged for crowding.</summary>
    public static bool IsRangedStyle(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic;

    /// <summary>
    /// The candidate's threat on the chooser's table relative to the top of that table (0 with no table
    /// or an empty one).
    /// </summary>
    public static float ThreatShare(Dictionary<uint, float>? table, uint candidateId)
    {
        var share = 0f;
        if (table != null && table.Count > 0)
        {
            var max = 0f;
            foreach (var v in table.Values)
                max = MathF.Max(max, v);
            share = EnemyMovementMath.ThreatShare(table.GetValueOrDefault(candidateId), max);
        }
        return share;
    }

    /// <summary>
    /// The weight shared by both sides; the situation multiplier is the caller's. <paramref name="meleeOnIt"/>
    /// and <paramref name="rangedOnIt"/> are how many fighters of each kind (both sides, the chooser
    /// included if it holds this one) are on the candidate now.
    /// </summary>
    public static float Weight(in Self self, in Candidate candidate, float threatShare, int meleeOnIt, int rangedOnIt)
    {
        var share = threatShare;
        var w = ThreatFloor + ThreatGain * share * share;

        var same = (self.Ranged ? rangedOnIt : meleeOnIt) - (self.CurrentTarget == candidate.Id ? 1 : 0);
        var other = self.Ranged ? meleeOnIt : rangedOnIt;
        var crowd = MathF.Max(0f, same) + 0.5f * other;
        w /= 1f + crowd * crowd / (CrowdK * CrowdK);

        w /= 1f + MathF.Max(0f, CombatantMath.FlatDistance(self.Position, candidate.Position) - self.Reach) / AggroDistanceScale;
        w *= 1f + FinishGain * (1f - Math.Clamp(candidate.HpShare, 0f, 1f));
        if (candidate.TargetId == self.Id)
            w *= MutualGain;
        if (candidate.Id == self.CurrentTarget)
            w *= StickyGain;
        return w;
    }

    /// <summary>
    /// Whether to draw again: the current target is gone, the hold is over, someone new hit this one
    /// after the shortest hold, or an outside change (the player's focus for allies, the body's holder
    /// for enemies going for it). The caller then clears <see cref="AggroState.HitByNew"/> and records
    /// the outside value.
    /// </summary>
    public static bool IsDue(AggroState state, float now, bool currentLive, bool externalChange)
        => !currentLive || now >= state.DwellUntil ||
           (state.HitByNew && now - state.HeldSince >= AggroHitHold) ||
           externalChange;

    /// <summary>A hit on this one by <paramref name="sourceId"/>: "someone new" when it had not hit in the window and is not the one held.</summary>
    public static void NoteHit(AggroState state, uint sourceId, float now)
    {
        if (now - state.LastHitBy.GetValueOrDefault(sourceId, float.NegativeInfinity) > NewAttackerWindow && state.Target != sourceId)
            state.HitByNew = true;
        state.LastHitBy[sourceId] = now;
    }

    /// <summary>
    /// Draw one id by weight with the fighter's own generator (one number; none when there are no
    /// candidates, which returns 0).
    /// </summary>
    public static uint Draw(AggroState state, IReadOnlyList<(uint Id, float Weight)> weights)
    {
        var total = 0.0;
        for (var i = 0; i < weights.Count; i++)
            total += weights[i].Weight;
        if (weights.Count == 0)
            return 0;
        var r = state.Rng.NextDouble() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            r -= weights[i].Weight;
            if (r <= 0)
                return weights[i].Id;
        }
        return weights[^1].Id;
    }

    /// <summary>Hold the drawn target for 4–8 s (one number from the fighter's generator).</summary>
    public static void Hold(AggroState state, uint target, float now)
    {
        state.Target = target;
        state.HeldSince = now;
        state.DwellUntil = now + AggroDwellMin + (float)state.Rng.NextDouble() * (AggroDwellMax - AggroDwellMin);
    }

    /// <summary>
    /// The reach an enemy's distance term uses: a ranged auto-attack's range, otherwise at least 3. Not
    /// the reach it moves by (no settings scaling, a melee floor of 3).
    /// </summary>
    public static float EnemyAggroReach(NpcBehavior behavior)
        => behavior.AutoAttackStyle == NpcAttackStyle.Ranged ? behavior.AutoAttackRange : MathF.Max(3f, behavior.AutoAttackRange);
}
