// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Warring States with Retreat, the lord fallen: the retainers fall back to the lord's body. The one nearest
/// goes to stand before it and prays (it cannot fight, and takes four times the damage); pushed off, it goes
/// back; one that cannot get nearer kneels where it is; if it falls the next takes its place. The others guard
/// the body: held within 10 m of it (back until 70% of the way in), standing in a ring 3 m out while nobody
/// comes, drawing their targets with the seizers x30, those near the body up to x2 more, those out of reach
/// x0.05, and letting go at once of a target gone out of reach. Moved from
/// <c>CombatCompanionManager.Lord.cs</c> and <c>.Aggro.cs</c> (refactor stage B5).
/// </summary>
public sealed unsafe class LordGuardRule : BattleRule, IAllyLeash
{
    public const float PrayerDamageScale = 4f;
    private const uint PrayEmoteId = 58;
    private const float PrayerStandOff = 1.3f;
    private const float PrayerReach = 0.6f;
    // Guarding the body: the allies keep within this of it, and stand in a ring this far out while
    // nobody comes; an enemy inside the contest radius (and not yet dead) is fought before any other.
    private const float LordGuardRange = 10f;
    private const float GuardRingRadius = 3f;
    public const float LordContestRadius = 3.5f;
    private const float NearBodyTargetReach = 12f;
    // Pushed further than this from where it knelt, the one praying gets up and goes back; one that
    // cannot get nearer to its post for this long kneels where it is.
    private const float PrayerPushedOff = 1.0f;
    private const float PrayerStallSeconds = 1.5f;
    // A guard that strays past its leash walks back until it is this share of the way in, so it does
    // not flicker in and out at the edge.
    private const float GuardReturnShare = 0.7f;

    private uint prayerId;
    private bool prayerAtPost;
    private Vector3 prayerSpot;
    private float prayerBest = float.MaxValue;
    private float prayerStall;
    private ushort prayerLoop;

    // The ally drawing its target now: whether the body is guarded, and where it lies.
    private bool drawHasBody;
    private Vector3 drawBody;

    public override string Id => BattleRuleFactory.Guard;

    /// <summary>The companion praying over the lord's body, if any (0: none).</summary>
    public uint PrayerId => prayerId;

    /// <summary>Where the lord's body lies, and where the one praying stands: before it.</summary>
    public bool TryGetLordBody(BattleContext ctx, out Vector3 body, out Vector3 post)
    {
        body = post = default;
        var player = ctx.World.LocalPlayer;
        if (player == null)
            return false;
        body = ctx.Allies.LordBodyPosition() ?? player.Position;
        var facing = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
        post = body + facing * PrayerStandOff;
        if (ctx.Allies.Crowd?.TrySnapToFloor(post, out var floor) == true)
            post = floor;
        return true;
    }

    public override void OnLordDown(BattleContext ctx)
    {
        if (!ctx.Rules.RetainersRetreatWithTheBody)
            return;
        ctx.Engine.AddLogEntry("The lord has fallen! The retainers fall back to guard the body.", CombatLogType.Death);
    }

    /// <summary>Who prays over the lord's body: the retainer nearest the post, when nobody does.</summary>
    public override void TickBeforeAllies(BattleContext ctx, float deltaTime)
    {
        if (!ctx.Rules.LordIsDown || !ctx.Rules.RetainersRetreatWithTheBody)
        {
            ReleasePrayer(ctx);
            return;
        }

        if (prayerId != 0 && (ctx.Allies.Get(prayerId) is not { } current || !ctx.Allies.CanAct(current)))
            ReleasePrayer(ctx);
        if (prayerId != 0 || !TryGetLordBody(ctx, out _, out var post))
            return;

        CombatCompanion? nearest = null;
        var best = float.MaxValue;
        foreach (var companion in ctx.Allies.All)
        {
            if (!ctx.Allies.CanAct(companion))
                continue;
            var d = CombatantMath.FlatDistance(ctx.World.PositionOf(companion), post);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        if (nearest == null)
            return;

        prayerId = nearest.SimulatedEntityId;
        prayerAtPost = false;
        prayerBest = float.MaxValue;
        prayerStall = 0f;
        ctx.Allies.ClearAction(nearest);
        ctx.Allies.ClearTarget(nearest);
        ctx.Engine.AddLogEntry($"{nearest.Name} goes to the lord's body.", CombatLogType.Info);
    }

    /// <summary>
    /// Drive the companion praying over the lord: to its post before the body, then praying there,
    /// taking <see cref="PrayerDamageScale"/> times the damage. True when this is that companion.
    /// </summary>
    public override bool TakesOverAlly(BattleContext ctx, CombatCompanion companion, float deltaTime)
    {
        if (prayerId == 0 || companion.SimulatedEntityId != prayerId || !TryGetLordBody(ctx, out var body, out var post))
            return false;

        var self = ctx.World.PositionOf(companion);

        // Pushed off where it knelt: up, and back to the post.
        if (prayerAtPost && CombatantMath.FlatDistance(self, prayerSpot) > PrayerPushedOff)
        {
            prayerAtPost = false;
            prayerBest = float.MaxValue;
            prayerStall = 0f;
            companion.State.IncomingDamageScale = 1f;
            ctx.World.ResetEmote(companion);
        }

        if (!prayerAtPost)
        {
            var off = CombatantMath.FlatDistance(self, post);
            if (off < prayerBest - 0.1f)
            {
                prayerBest = off;
                prayerStall = 0f;
            }
            else
                prayerStall += deltaTime;
            if (off > PrayerReach && prayerStall < PrayerStallSeconds)
            {
                ctx.Allies.EnterState(companion, CompanionAiState.MovingToTarget, deltaTime);
                ctx.Allies.MoveToward(companion, post, deltaTime);
                return true;
            }
        }

        ctx.Allies.HaltForEmote(companion);
        ctx.Allies.RotateToward(companion, body, deltaTime);
        if (!prayerAtPost)
        {
            // Only kneeling does it lay itself open.
            prayerAtPost = true;
            prayerSpot = self;
            companion.State.IncomingDamageScale = PrayerDamageScale;
            prayerLoop = ctx.World.PlayEmoteOn(companion, PrayEmoteId)
                ? ctx.World.EmoteLoopTimeline(PrayEmoteId)
                : (ushort)0;
            ctx.Engine.AddLogEntry($"{companion.Name} kneels to pray over the lord.", CombatLogType.Info);
        }
        ctx.World.HoldEmoteLoop(companion, prayerLoop);
        return true;
    }

    /// <summary>The one praying, once kneeling.</summary>
    public override bool Exposes(BattleContext ctx, uint friendId) => friendId == prayerId && prayerAtPost;

    public override bool Tends(BattleContext ctx, uint allyId) => prayerId != 0 && allyId == prayerId;

    public override string? StatusWord(BattleContext ctx, SimulatedEntityState who)
    {
        if (who.EntityId == prayerId && prayerId != 0)
            return "Praying";
        if (ctx.Rules.LordBodyIsGuarded)
            return "Guarding";
        return null;
    }

    // ---- the guards' target choice ----

    public override void BeginAllyDraw(BattleContext ctx, CombatCompanion ally)
        => drawHasBody = TryGetLordBody(ctx, out drawBody, out _) && ctx.Rules.LordBodyIsGuarded;

    /// <summary>The seizers x30; nearer the body up to x2 more; out of the guard's reach x0.05.</summary>
    public override float AllyTargetWeight(BattleContext ctx, in AllyTargetQuery query, float weight)
    {
        if (!drawHasBody)
            return weight;
        var w = weight;
        if (ctx.Allies.Seizers.Contains(query.Enemy.SimulatedEntityId))
            w *= AggroModel.GuardSeizerGain;
        w *= 1f + Math.Clamp(1f - CombatantMath.FlatDistance(query.EnemyPosition, drawBody) / NearBodyTargetReach, 0f, 1f);
        if (!ctx.Allies.CanReach(query.Ally, query.Enemy))
            w *= AggroModel.UnreachableGain;
        return w;
    }

    /// <summary>A target that has gone out of the guard's reach is let go at once.</summary>
    public override bool ForcesRedraw(BattleContext ctx, CombatCompanion ally, SimulatedNpc current)
        => ctx.Rules.LordBodyIsGuarded && !ctx.Allies.CanReach(ally, current);

    // ---- the leash ----

    public override IAllyLeash? LeashFor(BattleContext ctx, CombatCompanion ally)
        => ctx.Rules.LordBodyIsGuarded ? this : null;

    /// <summary>The lord's body (its ragdoll), else where the lord is.</summary>
    public Vector3 Anchor(BattleContext ctx, Vector3 playerPosition) => ctx.Allies.LordBodyPosition() ?? playerPosition;

    public float Radius => LordGuardRange;

    /// <summary>Past the leash it must come back, and stays coming back until well inside.</summary>
    public bool Inside(CombatCompanion companion, float distance, float limit)
    {
        if (companion.ReturningToLord)
        {
            if (distance <= limit * GuardReturnShare)
                companion.ReturningToLord = false;
        }
        else if (distance > limit)
            companion.ReturningToLord = true;
        return !companion.ReturningToLord;
    }

    /// <summary>
    /// Coming back from past its leash, well inside it on its own side; otherwise its place in the ring of
    /// guards (the one praying and those raising are not in the ring).
    /// </summary>
    public Vector3 IdleGoal(BattleContext ctx, CombatCompanion companion, Vector3 body, Vector3 current, float limit)
    {
        if (companion.ReturningToLord)
        {
            var away = new Vector3(current.X - body.X, 0f, current.Z - body.Z);
            away = away.LengthSquared() > 1e-4f ? Vector3.Normalize(away) : Vector3.UnitZ;
            return body + away * (limit * GuardReturnShare * 0.85f);
        }
        int index = 0, count = 0;
        foreach (var other in ctx.Allies.All)
        {
            if (!ctx.Allies.CanAct(other) || ctx.RuleSet.Tends(other.SimulatedEntityId))
                continue;
            if (other == companion)
                index = count;
            count++;
        }
        var angle = MathF.Tau * index / Math.Max(1, count);
        return body + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * GuardRingRadius;
    }

    // ---- ending ----

    /// <summary>The one praying gets up (its damage back to normal) and nobody prays.</summary>
    public void ReleasePrayer(BattleContext ctx)
    {
        if (prayerId == 0)
            return;
        if (ctx.Allies.Get(prayerId) is { } prayer)
        {
            prayer.State.IncomingDamageScale = 1f;
            if (prayerAtPost && prayer.BattleChara != null && prayer.State.IsAlive && !prayer.State.IsCaptive)
                ctx.World.ResetEmote(prayer);
        }
        prayerId = 0;
        prayerAtPost = false;
        prayerBest = float.MaxValue;
        prayerStall = 0f;
    }

    /// <summary>Retreat off, or another rule: nobody prays.</summary>
    public override void OnDeactivated(BattleContext ctx) => ReleasePrayer(ctx);

    public override void OnReset(BattleContext ctx) => ReleasePrayer(ctx);

    public override void OnAlliesDismissed(BattleContext ctx)
    {
        prayerId = 0;
        prayerAtPost = false;
    }
}
