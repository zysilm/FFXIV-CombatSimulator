// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Simulation;

namespace CombatSimulator.Companions;

/// <summary>
/// The mechanics the War Generator's rules use on the player's side: a captive (held at 1 HP, kneeling,
/// until executed or freed), standing still for an emote, and bringing a companion back. Who raises or
/// frees whom under the Spring and Autumn rule is a rule part (<see cref="SpringAutumnRescueRule"/>;
/// refactor stage B5).
/// </summary>
public unsafe partial class CombatCompanionManager
{
    /// <summary>A companion that has been freed or raised: its body comes back from the ragdoll.</summary>
    public Action<nint>? OnCompanionRevived { get => combatEngine.Events.AllyRevived; set => combatEngine.Events.AllyRevived = value; }

    public const float RaiseSeconds = SpringAutumnRescueRule.RaiseSeconds;
    public const float RaiserDamageScale = SpringAutumnRescueRule.RaiserDamageScale;

    /// <summary>Where a fallen actor's body lies (its ragdoll's pelvis); null: where it fell.</summary>
    public Func<nint, Vector3?>? BodyPositionOf { private get; set; }

    /// <summary>Whether this companion is on its way to, or at, a fallen friend.</summary>
    public bool IsRescuing(uint companionId) => ruleParts.Get<SpringAutumnRescueRule>().IsRescuing(companionId);

    /// <summary>The progress (0..1) of a raise under way on this friend, or null.</summary>
    public float? RescueProgress(uint patientId) => ruleParts.Get<SpringAutumnRescueRule>().RescueProgress(patientId);

    /// <summary>The progress (0..1) of the raise this companion is kneeling at, or null (for its cast bar).</summary>
    public float? RaiseProgressBy(uint rescuerId) => ruleParts.Get<SpringAutumnRescueRule>().RaiseProgressBy(rescuerId);

    private static bool CanRescue(CombatCompanion companion)
        => companion.IsSpawned && companion.BattleChara != null && companion.State.IsAlive && !companion.State.IsCaptive;

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
        EnterCompanionState(companion, CombatantAiState.CombatReady);
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

        ClearActionState(companion);
        ClearCompanionTargetState(companion);
        HaltForEmote(companion);
        companion.AiState = CombatantAiState.Captive;
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

    // Kneeling at a body (KneelSpot): from this close, and with nothing in between on the mesh, the ally
    // walks the last bit straight (out of the crowd, which would keep it off the body and others); it is
    // there within this.
    private const float KneelWalkStraight = 2.0f;
    private const float KneelArrived = 0.05f;

    // The spot each ally kneeling at a body has taken, so no two kneel on top of each other.
    private readonly Dictionary<uint, Vector3> kneelSpots = new();
    private readonly List<Vector3> takenKneelSpots = new();
    private readonly List<Vector3> fallenBodies = new();

    /// <summary>
    /// The spot beside <paramref name="body"/> where this ally is to kneel (<see cref="KneelSpot"/>), taken
    /// for it until <see cref="LeaveKneelSpot"/>. False with no mesh, or no walkable floor close enough to
    /// the body: the ally kneels where it is, as before.
    /// </summary>
    private bool TryKneelSpot(CombatCompanion companion, Vector3 body, out Vector3 spot)
    {
        spot = body;
        var crowd = Crowd;
        if (crowd is not { Ready: true } || companion.BattleChara == null)
            return false;
        takenKneelSpots.Clear();
        foreach (var (id, taken) in kneelSpots)
            if (id != companion.SimulatedEntityId && GetCompanion(id) is { } other && CanRescue(other) && ruleParts.Tends(id))
                takenKneelSpots.Add(taken);
        // The fallen lying about (one who knelt here before and fell): not knelt on.
        fallenBodies.Clear();
        if (!combatEngine.State.PlayerState.IsAlive && world.LocalPlayer is { } player)
            fallenBodies.Add(BodyPositionOf?.Invoke(player.Address) ?? player.Position);
        foreach (var fallen in companions)
            if (fallen.IsSpawned && fallen.BattleChara != null && !fallen.State.IsAlive)
                fallenBodies.Add(BodyPositionOf?.Invoke(fallen.Address) ?? world.PositionOf(fallen));
        if (!KneelSpot.TryChoose(body, world.PositionOf(companion), takenKneelSpots, fallenBodies, crowd.TryFloorWithin, out spot))
            return false;
        kneelSpots[companion.SimulatedEntityId] = spot;
        return true;
    }

    private void LeaveKneelSpot(CombatCompanion companion) => kneelSpots.Remove(companion.SimulatedEntityId);

    /// <summary>
    /// Go to a kneel spot and hold it: the crowd walks it near; the last bit (within
    /// <see cref="KneelWalkStraight"/>, straight on the mesh) it walks by itself, and there it stands still.
    /// While walking that last bit and while there it is out of the crowd as an obstacle: the others walk
    /// round it and nobody pushes it off. True once it is there (this frame it stands, idle, for the emote).
    /// </summary>
    private bool KneelAt(CombatCompanion companion, Vector3 spot, float deltaTime)
    {
        if (companion.BattleChara == null)
            return false;
        var crowd = Crowd;
        var self = world.PositionOf(companion);
        var off = CombatantMath.FlatDistance(self, spot);
        if (off > KneelArrived)
        {
            if (off > KneelWalkStraight || crowd == null || !crowd.WalkableStraight(self, spot))
            {
                MoveToward(companion, spot, deltaTime);
                return false;
            }
            var next = Vector3.Lerp(self, spot, MathF.Min(1f, MoveSpeedOf(companion) * deltaTime / off));
            crowd.SetObstacle(companion.Address, next, HitboxOf(companion));
            world.AddApproachNpc(companion.Address);
            world.SetApproachPosition(companion, next.X, next.Y, next.Z);
            EnterCompanionState(companion, CombatantAiState.MovingToTarget, deltaTime);
            TurnToward(companion, spot, deltaTime);
            return false;
        }

        crowd?.SetObstacle(companion.Address, spot, HitboxOf(companion));
        world.AddApproachNpc(companion.Address);
        world.SetApproachPosition(companion, spot.X, spot.Y, spot.Z);
        StopMoveAnim(companion);
        world.ApplyCombatIdle(companion);
        return true;
    }
}
