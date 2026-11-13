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

    private bool RaisingAllowed => config.WarFallenRule == (int)WarFallenRule.SpringAndAutumn;

    /// <summary>Who needs raising or freeing, and who goes: once per frame, before the companions act.</summary>
    private void TickRescues(float deltaTime)
    {
        if (!RaisingAllowed || !combatEngine.IsActive)
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
                finishedRescues.Add(patientId);
        }
        foreach (var id in finishedRescues)
            EndRescue(id);

        // The player first, then companions; each to the nearest free companion.
        TryAssignRescue(combatEngine.State.PlayerState, PlayerPosition());
        foreach (var companion in companions)
        {
            if (!companion.IsSpawned || companion.BattleChara == null)
                continue;
            TryAssignRescue(companion.State, (Vector3)((GameObject*)companion.BattleChara)->Position);
        }
    }

    private static bool NeedsRescue(SimulatedEntityState friend)
        => !friend.IsAlive || friend.IsCaptive;

    private static bool CanRescue(CombatCompanion companion)
        => companion.IsSpawned && companion.BattleChara != null && companion.State.IsAlive && !companion.State.IsCaptive;

    private Vector3? PlayerPosition()
        => objectTable.LocalPlayer?.Position;

    private void TryAssignRescue(SimulatedEntityState patient, Vector3? where)
    {
        if (where is not { } at || patient.EntityId == 0 || !NeedsRescue(patient) || rescues.ContainsKey(patient.EntityId))
            return;

        CombatCompanion? nearest = null;
        var best = float.MaxValue;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion) || companion.State.EntityId == patient.EntityId || IsRescuing(companion.SimulatedEntityId))
                continue;
            var d = FlatDistance((Vector3)((GameObject*)companion.BattleChara)->Position, at);
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
    private bool TickRescuer(CombatCompanion companion, float deltaTime, TerrainHeightCache? terrainCache)
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
        var where = patient == null ? null
            : patient.IsPlayer ? PlayerPosition()
            : GetCompanion(rescue.PatientId) is { BattleChara: not null } friend
                ? (Vector3)((GameObject*)friend.BattleChara)->Position
                : null;
        if (patient == null || where is not { } at)
        {
            EndRescue(rescue.PatientId);
            return false;
        }

        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;
        if (FlatDistance(self, at) > RaiseReach)
        {
            if (rescue.Channelling)
                StopChannelling(companion, rescue);
            EnterCompanionState(companion, CompanionAiState.MovingToTarget, deltaTime);
            MoveToward(companion, at, deltaTime, terrainCache);
            return true;
        }

        HaltForEmote(companion);
        RotateToward(companion, at, deltaTime);
        if (!rescue.Channelling)
        {
            rescue.Channelling = true;
            companion.State.IncomingDamageScale = RaiserDamageScale;
            rescue.Loop = animationController.PlayEmoteOn((Character*)companion.BattleChara, RaiseEmoteId)
                ? animationController.EmoteLoopTimeline(RaiseEmoteId)
                : (ushort)0;
            combatEngine.AddLogEntry(patient.IsCaptive
                    ? $"{companion.Name} is freeing {patient.Name}..."
                    : $"{companion.Name} is raising {patient.Name}...",
                CombatLogType.Info);
        }

        AnimationController.HoldEmoteLoop((Character*)companion.BattleChara, rescue.Loop);
        rescue.Progress += deltaTime;
        if (rescue.Progress < RaiseSeconds)
            return true;

        // The raise lands: the spell's own effect, from the one kneeling onto the one raised.
        combatEngine.PlaySupportVfx(companion.State, patient, RaiseActionId);

        if (patient.IsCaptive && patient.IsAlive)
            FreeFriend(patient, companion);
        else
            RaiseFriend(patient, companion);
        EndRescue(rescue.PatientId);
        return true;
    }

    private void StopChannelling(CombatCompanion companion, Rescue rescue)
    {
        rescue.Channelling = false;
        rescue.Progress = 0f;
        companion.State.IncomingDamageScale = 1f;
        if (companion.BattleChara != null && companion.State.IsAlive)
            animationController.EmotePlayer.ResetEmote((Character*)companion.BattleChara);
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
                animationController.EmotePlayer.ResetEmote((Character*)freed.BattleChara);
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
            animationController.ResetDeathAnimation(companion.BattleChara);
            RestoreCompanionEquipment(companion);
            companion.EquipmentVariantApplied = false;
            var character = (Character*)companion.BattleChara;
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
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
                    animationController.EmotePlayer.ResetEmote((Character*)companion.BattleChara);
            }
            return false;
        }

        ClearCompanionActionState(companion);
        ClearCompanionTargetState(companion);
        HaltForEmote(companion);
        companion.AiState = CompanionAiState.Captive;
        if (!companion.CaptiveEmotePlayed && companion.BattleChara != null)
        {
            companion.CaptiveLoop = animationController.PlayEmoteOn((Character*)companion.BattleChara, config.WarCaptiveEmoteId)
                ? animationController.EmoteLoopTimeline(config.WarCaptiveEmoteId)
                : (ushort)0;
            companion.CaptiveEmotePlayed = true;
        }
        if (companion.BattleChara != null)
            AnimationController.HoldEmoteLoop((Character*)companion.BattleChara, companion.CaptiveLoop);
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
            ActorVisualStateController.ApplyCombatIdle((Character*)companion.BattleChara, companion.VisualState);
    }

    private void ClearRescues()
    {
        CancelAllRescues();
        rescues.Clear();
    }
}
