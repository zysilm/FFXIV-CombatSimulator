// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Spring and Autumn: allies run to a fallen or captive friend and raise or free them
/// (docs/WAR_GENERATOR.md). Triage: the lord first, then tanks, then the others, nearest first; at most
/// one ally in four raising at a time; nobody sent into the thick of the enemy but for the lord. The one
/// raising kneels 8 s at the friend's side taking four times the damage; pushed off, the raise runs down
/// at half its rate; hurt badly on the way, it gives up and rests 6 s. A raise brings the friend back at
/// half its HP with no MP. Moved from <c>CombatCompanionManager.Rescue.cs</c> (refactor stage B5).
/// </summary>
public sealed unsafe class SpringAutumnRescueRule : BattleRule
{
    // Raising takes this long at the fallen friend's side, and the one raising takes this many
    // times the damage while at it.
    public const float RaiseSeconds = 8f;
    public const float RaiserDamageScale = 4f;
    private const float RaiseReach = 1.6f;
    private const float RaisedHpShare = 0.5f;
    private const float FreedHpShare = 0.25f;
    // The kneel the one raising holds while at it.
    private const uint RaiseEmoteId = 19;
    // Raise: the game's own resurrection spell, for its effect when a raise lands.
    private const uint RaiseActionId = 125;
    // Triage: at most one ally in this many living ones raises at a time, so the army keeps fighting.
    private const int AlliesPerRescue = 4;
    // A fallen friend in the thick of it (more enemies than friends this close) waits; the lord does not.
    private const float RescueDangerRadius = 8f;
    // Pushed off a friend mid-raise, the raise waits and runs down at this share of its rate.
    private const float RaiseDrainRate = 0.5f;
    // Hurt this badly on the way, a rescuer gives up and fights on, and is not sent again for a while.
    private const float RescuerGiveUpHpShare = 0.3f;
    private const float RescuerRestSeconds = 6f;
    // Someone two enemies are already on is not sent to kneel.
    private const int RescuerMaxAttackers = 2;
    private const float RescueReportSeconds = 5f;

    private readonly Dictionary<uint, float> rescuerRestUntil = new();
    private readonly List<(SimulatedEntityState Patient, Vector3 At, int Rank, float Distance)> rescueQueue = new();
    private int raisesLanded, rescuesAbandoned, rescuersFallen;
    private float rescueReportTimer;

    private sealed class Rescue
    {
        public uint PatientId;
        public uint RescuerId;
        public float Progress;
        public bool Channelling;
        public ushort Loop;
    }

    // Patient id -> the companion raising or freeing them.
    private readonly Dictionary<uint, Rescue> rescues = new();
    private readonly List<uint> finishedRescues = new();

    public override string Id => BattleRuleFactory.Rescue;

    /// <summary>Whether this companion is on its way to, or at, a fallen friend.</summary>
    public bool IsRescuing(uint companionId)
    {
        foreach (var rescue in rescues.Values)
            if (rescue.RescuerId == companionId)
                return true;
        return false;
    }

    /// <summary>The progress (0..1) of a raise under way on this friend, or null.</summary>
    public float? RescueProgress(uint patientId)
        => rescues.TryGetValue(patientId, out var rescue) && rescue.Channelling ? rescue.Progress / RaiseSeconds : null;

    public override bool Tends(BattleContext ctx, uint allyId) => IsRescuing(allyId);

    /// <summary>The one raising, once kneeling.</summary>
    public override bool Exposes(BattleContext ctx, uint friendId)
    {
        foreach (var rescue in rescues.Values)
            if (rescue.Channelling && rescue.RescuerId == friendId)
                return true;
        return false;
    }

    public override string? StatusWord(BattleContext ctx, SimulatedEntityState who)
        => IsRescuing(who.EntityId) ? "Raising" : null;

    /// <summary>Who needs raising or freeing, and who goes: once per frame, before the companions act.</summary>
    public override void TickBeforeAllies(BattleContext ctx, float deltaTime)
    {
        var enemies = ctx.Enemies;
        if (!ctx.Rules.RaisesTheFallen || !ctx.Engine.IsActive)
        {
            CancelAllRescues(ctx);
            return;
        }

        // Drop what no longer holds: the friend is up again, or the one raising fell or was taken.
        finishedRescues.Clear();
        foreach (var (patientId, rescue) in rescues)
        {
            var patient = ctx.Engine.State.GetEntity(patientId);
            var rescuer = ctx.Allies.Get(rescue.RescuerId);
            if (patient == null || !NeedsRescue(patient) || rescuer == null || !ctx.Allies.CanAct(rescuer))
            {
                if (rescuer != null && !ctx.Allies.CanAct(rescuer))
                    rescuersFallen++;
                finishedRescues.Add(patientId);
            }
        }
        foreach (var id in finishedRescues)
            EndRescue(ctx, id);

        // Triage: the lord first, then tanks, then the others, nearest first; only so many at once,
        // and nobody is sent into the thick of the enemy for anyone but the lord.
        var fighters = 0;
        foreach (var companion in ctx.Allies.All)
            if (ctx.Allies.CanAct(companion))
                fighters++;
        var allowed = Math.Max(1, (int)MathF.Ceiling(fighters / (float)AlliesPerRescue));

        rescueQueue.Clear();
        var player = ctx.Engine.State.PlayerState;
        var lord = ctx.World.LocalPlayer;
        if (NeedsRescue(player) && lord != null)
            rescueQueue.Add((player, ctx.Allies.BodyPositionOf(lord.Address) ?? lord.Position, 0, 0f));
        var anchor = lord?.Position ?? Vector3.Zero;
        foreach (var companion in ctx.Allies.All)
        {
            if (!companion.IsSpawned || companion.BattleChara == null || !NeedsRescue(companion.State))
                continue;
            var at = PatientPosition(ctx, companion);
            rescueQueue.Add((companion.State, at, companion.State.IsTank ? 1 : 2, CombatantMath.FlatDistance(at, anchor)));
        }
        rescueQueue.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Distance.CompareTo(b.Distance));
        foreach (var (patient, at, rank, _) in rescueQueue)
        {
            if (rescues.ContainsKey(patient.EntityId))
                continue;
            if (rescues.Count >= allowed && rank != 0)
                break;
            if (rank != 0 && Outnumbered(ctx, at, enemies))
                continue;
            TryAssignRescue(ctx, patient, at);
        }

        rescueReportTimer -= deltaTime;
        if (rescueReportTimer <= 0f)
        {
            rescueReportTimer = RescueReportSeconds;
            if (rescues.Count > 0 || raisesLanded + rescuesAbandoned + rescuersFallen > 0)
            {
                var kneeling = 0;
                foreach (var r in rescues.Values) if (r.Channelling) kneeling++;
                ctx.Allies.Info($"Rescue: {rescues.Count} under way ({kneeling} kneeling, at most {allowed} of {fighters} allies), {rescueQueue.Count} fallen; over {RescueReportSeconds:0} s {raisesLanded} raised, {rescuesAbandoned} given up, {rescuersFallen} rescuers fell.");
            }
            raisesLanded = rescuesAbandoned = rescuersFallen = 0;
        }
    }

    /// <summary>Where a fallen companion lies: its ragdoll, else its game position.</summary>
    private static Vector3 PatientPosition(BattleContext ctx, CombatCompanion companion)
        => ctx.Allies.BodyPositionOf(companion.Address) ?? ctx.World.PositionOf(companion);

    /// <summary>More living enemies than friends within the danger radius of a point.</summary>
    private static bool Outnumbered(BattleContext ctx, Vector3 at, IReadOnlyList<Npcs.SimulatedNpc> enemies)
    {
        var foes = 0;
        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;
            if (enemy.IsSentry && enemy.AiState == Ai.NpcAiState.Idle)
                continue;
            if (CombatantMath.FlatDistance(ctx.World.PositionOf(enemy), at) <= RescueDangerRadius)
                foes++;
        }
        if (foes == 0)
            return false;
        var friends = 0;
        foreach (var companion in ctx.Allies.All)
            if (ctx.Allies.CanAct(companion) && CombatantMath.FlatDistance(ctx.World.PositionOf(companion), at) <= RescueDangerRadius)
                friends++;
        return foes > friends;
    }

    private static bool NeedsRescue(SimulatedEntityState friend)
        => !friend.IsAlive || friend.IsCaptive;

    private void TryAssignRescue(BattleContext ctx, SimulatedEntityState patient, Vector3? where)
    {
        if (where is not { } at || patient.EntityId == 0 || !NeedsRescue(patient) || rescues.ContainsKey(patient.EntityId))
            return;

        // The nearest friend free to go: not already raising or praying over the lord (busy with a
        // rule's task), not resting after giving up, not badly hurt, not the one the enemy is swarming.
        var now = ctx.Engine.State.SimulationTime;
        CombatCompanion? nearest = null;
        var best = float.MaxValue;
        foreach (var companion in ctx.Allies.All)
        {
            if (!ctx.Allies.CanAct(companion) || companion.State.EntityId == patient.EntityId || ctx.RuleSet.Tends(companion.SimulatedEntityId))
                continue;
            if (rescuerRestUntil.TryGetValue(companion.SimulatedEntityId, out var restUntil) && now < restUntil)
                continue;
            if (companion.State.CurrentHp < companion.State.MaxHp * RescuerGiveUpHpShare)
                continue;
            if (ctx.Allies.AttackersOn(companion.SimulatedEntityId) >= RescuerMaxAttackers)
                continue;
            var d = CombatantMath.FlatDistance(ctx.World.PositionOf(companion), at);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        if (nearest == null)
            return;

        rescues[patient.EntityId] = new Rescue { PatientId = patient.EntityId, RescuerId = nearest.SimulatedEntityId };
        ctx.Allies.ClearAction(nearest);
        ctx.Allies.ClearTarget(nearest);
    }

    /// <summary>
    /// Drive a companion that is raising or freeing someone: run to them, then kneel there for
    /// <see cref="RaiseSeconds"/>, taking <see cref="RaiserDamageScale"/> times the damage. True
    /// when this companion is so engaged (its fighting is skipped).
    /// </summary>
    public override bool TakesOverAlly(BattleContext ctx, CombatCompanion companion, float deltaTime)
    {
        Rescue? rescue = null;
        foreach (var r in rescues.Values)
        {
            if (r.RescuerId == companion.SimulatedEntityId)
            {
                rescue = r;
                break;
            }
        }
        if (rescue == null)
            return false;

        var patient = ctx.Engine.State.GetEntity(rescue.PatientId);
        Vector3? where = patient == null ? null
            : patient.IsPlayer ? (ctx.World.LocalPlayer is { } lord ? ctx.Allies.BodyPositionOf(lord.Address) ?? lord.Position : (Vector3?)null)
            : ctx.Allies.Get(rescue.PatientId) is { BattleChara: not null } friend
                ? PatientPosition(ctx, friend)
                : null;
        if (patient == null || where is not { } at)
        {
            EndRescue(ctx, rescue.PatientId);
            return false;
        }

        var self = ctx.World.PositionOf(companion);
        if (CombatantMath.FlatDistance(self, at) > RaiseReach)
        {
            // Pushed off mid-raise: it waits and runs down slowly, it does not start over.
            if (rescue.Channelling)
                StopChannelling(ctx, companion, rescue, keepProgress: true);
            rescue.Progress = MathF.Max(0f, rescue.Progress - deltaTime * RaiseDrainRate);
            // Hurt badly on the way: gives up and fights on.
            if (companion.State.CurrentHp < companion.State.MaxHp * RescuerGiveUpHpShare)
            {
                rescuerRestUntil[companion.SimulatedEntityId] = ctx.Engine.State.SimulationTime + RescuerRestSeconds;
                rescuesAbandoned++;
                ctx.Engine.AddLogEntry($"{companion.Name} cannot reach {patient.Name}.", CombatLogType.Info);
                EndRescue(ctx, rescue.PatientId);
                return false;
            }
            ctx.Allies.EnterState(companion, CompanionAiState.MovingToTarget, deltaTime);
            ctx.Allies.MoveToward(companion, at, deltaTime);
            return true;
        }

        ctx.Allies.HaltForEmote(companion);
        ctx.Allies.RotateToward(companion, at, deltaTime);
        if (!rescue.Channelling)
        {
            rescue.Channelling = true;
            companion.State.IncomingDamageScale = RaiserDamageScale;
            rescue.Loop = ctx.World.PlayEmoteOn(companion, RaiseEmoteId)
                ? ctx.World.EmoteLoopTimeline(RaiseEmoteId)
                : (ushort)0;
            ctx.Engine.AddLogEntry(patient.IsCaptive
                    ? $"{companion.Name} is freeing {patient.Name}..."
                    : $"{companion.Name} is raising {patient.Name}...",
                CombatLogType.Info);
        }

        ctx.World.HoldEmoteLoop(companion, rescue.Loop);
        rescue.Progress += deltaTime;
        if (rescue.Progress < RaiseSeconds)
            return true;

        // The raise lands: the spell's own effect, from the one kneeling onto the one raised.
        ctx.Engine.PlaySupportVfx(companion.State, patient, RaiseActionId);

        if (patient.IsCaptive && patient.IsAlive)
            FreeFriend(ctx, patient, companion);
        else
            RaiseFriend(ctx, patient, companion);
        raisesLanded++;
        EndRescue(ctx, rescue.PatientId);
        return true;
    }

    private static void StopChannelling(BattleContext ctx, CombatCompanion companion, Rescue rescue, bool keepProgress = false)
    {
        rescue.Channelling = false;
        if (!keepProgress)
            rescue.Progress = 0f;
        companion.State.IncomingDamageScale = 1f;
        if (companion.BattleChara != null && companion.State.IsAlive)
            ctx.World.ResetEmote(companion);
    }

    private void EndRescue(BattleContext ctx, uint patientId)
    {
        if (!rescues.Remove(patientId, out var rescue))
            return;
        if (ctx.Allies.Get(rescue.RescuerId) is { } rescuer && rescue.Channelling)
            StopChannelling(ctx, rescuer, rescue);
    }

    private void CancelAllRescues(BattleContext ctx)
    {
        if (rescues.Count == 0)
            return;
        foreach (var id in new List<uint>(rescues.Keys))
            EndRescue(ctx, id);
    }

    private static void FreeFriend(BattleContext ctx, SimulatedEntityState patient, CombatCompanion by)
    {
        ctx.Engine.ReleaseCaptive(patient, FreedHpShare);
        if (!patient.IsPlayer && ctx.Allies.Get(patient.EntityId) is { } freed)
        {
            freed.CaptiveEmotePlayed = false;
            if (freed.BattleChara != null)
                ctx.World.ResetEmote(freed);
        }
        ctx.Engine.AddLogEntry($"{by.Name} freed {patient.Name}.", CombatLogType.Info);
    }

    private static void RaiseFriend(BattleContext ctx, SimulatedEntityState patient, CombatCompanion by)
    {
        if (patient.IsPlayer)
            ctx.Engine.RevivePlayerInPlace(RaisedHpShare, refillMp: false);
        else if (ctx.Allies.Get(patient.EntityId) is { } fallen)
            ctx.Allies.Revive(fallen, RaisedHpShare);
        ctx.Engine.AddLogEntry($"{by.Name} raised {patient.Name}.", CombatLogType.Info);
    }

    /// <summary>The switch went off (another rule chosen): every rescue under way ends.</summary>
    public override void OnDeactivated(BattleContext ctx) => CancelAllRescues(ctx);

    public override void OnReset(BattleContext ctx)
    {
        CancelAllRescues(ctx);
        rescues.Clear();
        rescuerRestUntil.Clear();
    }

    public override void OnAlliesDismissed(BattleContext ctx) => rescues.Clear();
}
