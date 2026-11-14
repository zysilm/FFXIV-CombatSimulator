// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Battle.Rules;
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
    public Action<nint>? OnCompanionRevived { get; set; }

    public const float RaiseSeconds = SpringAutumnRescueRule.RaiseSeconds;
    public const float RaiserDamageScale = SpringAutumnRescueRule.RaiserDamageScale;

    /// <summary>Where a fallen actor's body lies (its ragdoll's pelvis); null: where it fell.</summary>
    public Func<nint, Vector3?>? BodyPositionOf { private get; set; }

    /// <summary>Whether this companion is on its way to, or at, a fallen friend.</summary>
    public bool IsRescuing(uint companionId) => ruleParts.Get<SpringAutumnRescueRule>().IsRescuing(companionId);

    /// <summary>The progress (0..1) of a raise under way on this friend, or null.</summary>
    public float? RescueProgress(uint patientId) => ruleParts.Get<SpringAutumnRescueRule>().RescueProgress(patientId);

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
}
