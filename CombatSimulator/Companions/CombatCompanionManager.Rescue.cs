// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Companions;

/// <summary>
/// The rules of the War Generator that concern the fallen of the player's side: a captive (held at
/// 1 HP, kneeling, until executed or freed) and, under the Spring and Autumn rule, companions who
/// run to a fallen or captive friend and raise or free them.
/// </summary>
public unsafe partial class CombatCompanionManager
{
    /// <summary>A companion that has been freed or raised: its body comes back from the ragdoll.</summary>
    public Action<nint>? OnCompanionRevived { get; set; }

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

    /// <summary>Where a fallen actor's body lies (its ragdoll's pelvis); null: where it fell.</summary>
    public Func<nint, Vector3?>? BodyPositionOf { private get; set; }

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

    /// <summary>Who needs raising or freeing, and who goes: once per frame, before the companions act.</summary>
    private void TickRescues(float deltaTime, IReadOnlyList<Npcs.SimulatedNpc> enemies)
    {
        if (!Rules.RaisesTheFallen || !combatEngine.IsActive)
        {
            CancelAllRescues();
            return;
        }

        // Drop what no longer holds: the friend is up again, or the one raising fell or was taken.
        finishedRescues.Clear();
        foreach (var (patientId, rescue) in rescues)
        {
            var patient = combatEngine.State.GetEntity(patientId);
            var rescuer = GetCompanion(rescue.RescuerId);
            if (patient == null || !NeedsRescue(patient) || rescuer == null || !CanRescue(rescuer))
            {
                if (rescuer != null && !CanRescue(rescuer))
                    rescuersFallen++;
                finishedRescues.Add(patientId);
            }
        }
        foreach (var id in finishedRescues)
            EndRescue(id);

        // Triage: the lord first, then tanks, then the others, nearest first; only so many at once,
        // and nobody is sent into the thick of the enemy for anyone but the lord.
        var fighters = 0;
        foreach (var companion in companions)
            if (CanRescue(companion))
                fighters++;
        var allowed = Math.Max(1, (int)MathF.Ceiling(fighters / (float)AlliesPerRescue));

        rescueQueue.Clear();
        var player = combatEngine.State.PlayerState;
        var lord = world.LocalPlayer;
        if (NeedsRescue(player) && lord != null)
            rescueQueue.Add((player, BodyPositionOf?.Invoke(lord.Address) ?? lord.Position, 0, 0f));
        var anchor = lord?.Position ?? Vector3.Zero;
        foreach (var companion in companions)
        {
            if (!companion.IsSpawned || companion.BattleChara == null || !NeedsRescue(companion.State))
                continue;
            var at = PatientPosition(companion);
            rescueQueue.Add((companion.State, at, companion.State.IsTank ? 1 : 2, FlatDistance(at, anchor)));
        }
        rescueQueue.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Distance.CompareTo(b.Distance));
        foreach (var (patient, at, rank, _) in rescueQueue)
        {
            if (rescues.ContainsKey(patient.EntityId))
                continue;
            if (rescues.Count >= allowed && rank != 0)
                break;
            if (rank != 0 && Outnumbered(at, enemies))
                continue;
            TryAssignRescue(patient, at);
        }

        rescueReportTimer -= deltaTime;
        if (rescueReportTimer <= 0f)
        {
            rescueReportTimer = RescueReportSeconds;
            if (rescues.Count > 0 || raisesLanded + rescuesAbandoned + rescuersFallen > 0)
            {
                var kneeling = 0;
                foreach (var r in rescues.Values) if (r.Channelling) kneeling++;
                log.Info($"Rescue: {rescues.Count} under way ({kneeling} kneeling, at most {allowed} of {fighters} allies), {rescueQueue.Count} fallen; over {RescueReportSeconds:0} s {raisesLanded} raised, {rescuesAbandoned} given up, {rescuersFallen} rescuers fell.");
            }
            raisesLanded = rescuesAbandoned = rescuersFallen = 0;
        }
    }

    /// <summary>Where a fallen companion lies: its ragdoll, else its game position.</summary>
    private Vector3 PatientPosition(CombatCompanion companion)
        => BodyPositionOf?.Invoke(companion.Address) ?? world.PositionOf(companion);

    /// <summary>More living enemies than friends within the danger radius of a point.</summary>
    private bool Outnumbered(Vector3 at, IReadOnlyList<Npcs.SimulatedNpc> enemies)
    {
        var foes = 0;
        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;
            if (enemy.IsSentry && enemy.AiState == Ai.NpcAiState.Idle)
                continue;
            if (FlatDistance(world.PositionOf(enemy), at) <= RescueDangerRadius)
                foes++;
        }
        if (foes == 0)
            return false;
        var friends = 0;
        foreach (var companion in companions)
            if (CanRescue(companion) && FlatDistance(world.PositionOf(companion), at) <= RescueDangerRadius)
                friends++;
        return foes > friends;
    }

    /// <summary>How many enemies have this one as their target.</summary>
    private int AttackersOn(uint entityId) => targeting.AttackersOn(entityId);

    private static bool NeedsRescue(SimulatedEntityState friend)
        => !friend.IsAlive || friend.IsCaptive;

    private static bool CanRescue(CombatCompanion companion)
        => companion.IsSpawned && companion.BattleChara != null && companion.State.IsAlive && !companion.State.IsCaptive;

    private Vector3? PlayerPosition()
        => world.LocalPlayer?.Position;

    private void TryAssignRescue(SimulatedEntityState patient, Vector3? where)
    {
        if (where is not { } at || patient.EntityId == 0 || !NeedsRescue(patient) || rescues.ContainsKey(patient.EntityId))
            return;

        // The nearest friend free to go: not already raising, not resting after giving up, not badly
        // hurt, not the one the enemy is swarming, and not the one praying over the lord.
        var now = combatEngine.State.SimulationTime;
        CombatCompanion? nearest = null;
        var best = float.MaxValue;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion) || companion.State.EntityId == patient.EntityId || IsRescuing(companion.SimulatedEntityId))
                continue;
            if (companion.SimulatedEntityId == prayerId)
                continue;
            if (rescuerRestUntil.TryGetValue(companion.SimulatedEntityId, out var restUntil) && now < restUntil)
                continue;
            if (companion.State.CurrentHp < companion.State.MaxHp * RescuerGiveUpHpShare)
                continue;
            if (AttackersOn(companion.SimulatedEntityId) >= RescuerMaxAttackers)
                continue;
            var d = FlatDistance(world.PositionOf(companion), at);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        if (nearest == null)
            return;

        rescues[patient.EntityId] = new Rescue { PatientId = patient.EntityId, RescuerId = nearest.SimulatedEntityId };
        ClearCompanionActionState(nearest);
        ClearCompanionTargetState(nearest);
    }

    /// <summary>
    /// Drive a companion that is raising or freeing someone: run to them, then kneel there for
    /// <see cref="RaiseSeconds"/>, taking <see cref="RaiserDamageScale"/> times the damage. True
    /// when this companion is so engaged (its fighting is skipped).
    /// </summary>
    private bool TickRescuer(CombatCompanion companion, float deltaTime)
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

        var patient = combatEngine.State.GetEntity(rescue.PatientId);
        Vector3? where = patient == null ? null
            : patient.IsPlayer ? (world.LocalPlayer is { } lord ? BodyPositionOf?.Invoke(lord.Address) ?? lord.Position : (Vector3?)null)
            : GetCompanion(rescue.PatientId) is { BattleChara: not null } friend
                ? PatientPosition(friend)
                : null;
        if (patient == null || where is not { } at)
        {
            EndRescue(rescue.PatientId);
            return false;
        }

        var self = world.PositionOf(companion);
        if (FlatDistance(self, at) > RaiseReach)
        {
            // Pushed off mid-raise: it waits and runs down slowly, it does not start over.
            if (rescue.Channelling)
                StopChannelling(companion, rescue, keepProgress: true);
            rescue.Progress = MathF.Max(0f, rescue.Progress - deltaTime * RaiseDrainRate);
            // Hurt badly on the way: gives up and fights on.
            if (companion.State.CurrentHp < companion.State.MaxHp * RescuerGiveUpHpShare)
            {
                rescuerRestUntil[companion.SimulatedEntityId] = combatEngine.State.SimulationTime + RescuerRestSeconds;
                rescuesAbandoned++;
                combatEngine.AddLogEntry($"{companion.Name} cannot reach {patient.Name}.", CombatLogType.Info);
                EndRescue(rescue.PatientId);
                return false;
            }
            EnterCompanionState(companion, CompanionAiState.MovingToTarget, deltaTime);
            MoveToward(companion, at, deltaTime);
            return true;
        }

        HaltForEmote(companion);
        RotateToward(companion, at, deltaTime);
        if (!rescue.Channelling)
        {
            rescue.Channelling = true;
            companion.State.IncomingDamageScale = RaiserDamageScale;
            rescue.Loop = world.PlayEmoteOn(companion, RaiseEmoteId)
                ? world.EmoteLoopTimeline(RaiseEmoteId)
                : (ushort)0;
            combatEngine.AddLogEntry(patient.IsCaptive
                    ? $"{companion.Name} is freeing {patient.Name}..."
                    : $"{companion.Name} is raising {patient.Name}...",
                CombatLogType.Info);
        }

        world.HoldEmoteLoop(companion, rescue.Loop);
        rescue.Progress += deltaTime;
        if (rescue.Progress < RaiseSeconds)
            return true;

        // The raise lands: the spell's own effect, from the one kneeling onto the one raised.
        combatEngine.PlaySupportVfx(companion.State, patient, RaiseActionId);

        if (patient.IsCaptive && patient.IsAlive)
            FreeFriend(patient, companion);
        else
            RaiseFriend(patient, companion);
        raisesLanded++;
        EndRescue(rescue.PatientId);
        return true;
    }

    private void StopChannelling(CombatCompanion companion, Rescue rescue, bool keepProgress = false)
    {
        rescue.Channelling = false;
        if (!keepProgress)
            rescue.Progress = 0f;
        companion.State.IncomingDamageScale = 1f;
        if (companion.BattleChara != null && companion.State.IsAlive)
            world.ResetEmote(companion);
    }

    private void EndRescue(uint patientId)
    {
        if (!rescues.Remove(patientId, out var rescue))
            return;
        if (GetCompanion(rescue.RescuerId) is { } rescuer && rescue.Channelling)
            StopChannelling(rescuer, rescue);
    }

    private void CancelAllRescues()
    {
        if (rescues.Count == 0)
            return;
        foreach (var id in new List<uint>(rescues.Keys))
            EndRescue(id);
    }

    private void FreeFriend(SimulatedEntityState patient, CombatCompanion by)
    {
        combatEngine.ReleaseCaptive(patient, FreedHpShare);
        if (!patient.IsPlayer && GetCompanion(patient.EntityId) is { } freed)
        {
            freed.CaptiveEmotePlayed = false;
            if (freed.BattleChara != null)
                world.ResetEmote(freed);
        }
        combatEngine.AddLogEntry($"{by.Name} freed {patient.Name}.", CombatLogType.Info);
    }

    private void RaiseFriend(SimulatedEntityState patient, CombatCompanion by)
    {
        if (patient.IsPlayer)
            combatEngine.RevivePlayerInPlace(RaisedHpShare, refillMp: false);
        else if (GetCompanion(patient.EntityId) is { } fallen)
            ReviveCompanion(fallen, RaisedHpShare);
        combatEngine.AddLogEntry($"{by.Name} raised {patient.Name}.", CombatLogType.Info);
    }

    /// <summary>Bring a fallen or captive companion back on its feet with this share of its HP.</summary>
    public void ReviveCompanion(CombatCompanion companion, float hpShare)
    {
        var state = companion.State;
        state.IsCaptive = false;
        state.Executed = false;
        state.IncomingDamageScale = 1f;
        state.CurrentHp = Math.Clamp((int)MathF.Ceiling(state.MaxHp * hpShare), 1, Math.Max(1, state.MaxHp));
        // A raise brings back the body, not the mana: MP starts empty.
        state.CurrentMp = 0;
        state.AnimationLock = 0;
        companion.DeathAnimationPlayed = false;
        companion.CaptiveEmotePlayed = false;
        companion.AutoAttackTimer = 0;
        ClearCompanionTargetState(companion);

        if (companion.BattleChara == null)
            return;
        OnCompanionRevived?.Invoke(companion.Address);
        try
        {
            world.ResetDeathAnimation(companion);
            RestoreCompanionEquipment(companion);
            companion.EquipmentVariantApplied = false;
            world.SetNormalMode(companion);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to revive companion '{companion.Name}'.");
        }
        EnterCompanionState(companion, CompanionAiState.CombatReady);
    }

    /// <summary>
    /// A captive companion: it holds the chosen emote where it was taken and does nothing else.
    /// True when this companion is captive (its fighting is skipped).
    /// </summary>
    private bool TickCaptive(CombatCompanion companion)
    {
        if (!companion.State.IsCaptive)
        {
            // Freed (by a friend, or by the battle being won): back on its feet.
            if (companion.CaptiveEmotePlayed)
            {
                companion.CaptiveEmotePlayed = false;
                if (companion.BattleChara != null)
                    world.ResetEmote(companion);
            }
            return false;
        }

        ClearCompanionActionState(companion);
        ClearCompanionTargetState(companion);
        HaltForEmote(companion);
        companion.AiState = CompanionAiState.Captive;
        if (!companion.CaptiveEmotePlayed && companion.BattleChara != null)
        {
            companion.CaptiveLoop = world.PlayEmoteOn(companion, config.WarCaptiveEmoteId)
                ? world.EmoteLoopTimeline(config.WarCaptiveEmoteId)
                : (ushort)0;
            companion.CaptiveEmotePlayed = true;
        }
        if (companion.BattleChara != null)
            world.HoldEmoteLoop(companion, companion.CaptiveLoop);
        return true;
    }

    /// <summary>
    /// Stand still to hold an emote. Stopping alone left the companion marked as moving, and every
    /// later stop then cleared its base timeline: the held emote was wiped and restarted each frame,
    /// so it never showed. Marking it idle lets the emote loop stay.
    /// </summary>
    private void HaltForEmote(CombatCompanion companion)
    {
        StopMove(companion);
        if (companion.BattleChara != null)
            world.ApplyCombatIdle(companion);
    }

    private void ClearRescues()
    {
        CancelAllRescues();
        rescues.Clear();
        rescuerRestUntil.Clear();
    }
}
