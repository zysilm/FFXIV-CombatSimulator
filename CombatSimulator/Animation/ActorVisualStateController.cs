// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Animation;

public enum ActorVisualStateKind
{
    None,
    CombatIdle,
    Moving,
    ActionLocked,
    Dead,
}

public sealed class ActorVisualState
{
    public ActorVisualStateKind Kind { get; set; }
    public float MovementReassertTimer { get; set; }
}

public static unsafe class ActorVisualStateController
{
    public const ushort NormalRunTimelineId = 22;
    public const ushort NormalWalkTimelineId = 13;
    private const float MovementReassertInterval = 0.25f;

    public static void ApplyMoving(Character* character, ActorVisualState state, float deltaTime)
        => ApplyMoving(character, state, deltaTime, NormalRunTimelineId);

    /// <summary>Moving, with the given locomotion loop (run, or walk for an unhurried patrol).</summary>
    public static void ApplyMoving(Character* character, ActorVisualState state, float deltaTime, ushort timelineId)
    {
        if (character == null)
            return;

        ApplyCombatFlags(character);

        state.MovementReassertTimer = MathF.Max(0, state.MovementReassertTimer - deltaTime);
        var entering = state.Kind != ActorVisualStateKind.Moving;
        // (A change of gait, walk to run, is taken up at once.)
        var shouldReassert = entering || state.MovementReassertTimer <= 0 ||
                             character->Timeline.BaseOverride != timelineId;

        character->Timeline.BaseOverride = timelineId;
        if (shouldReassert && character->Timeline.TimelineSequencer.Parent != null)
        {
            character->Timeline.PlayActionTimeline(timelineId);
            state.MovementReassertTimer = MovementReassertInterval;
        }

        state.Kind = ActorVisualStateKind.Moving;
    }

    public static void ApplyCombatIdle(Character* character, ActorVisualState state)
    {
        if (character == null)
            return;

        ApplyCombatFlags(character);
        ClearMovement(character, state);
        state.Kind = ActorVisualStateKind.CombatIdle;
    }

    public static void ApplyActionLocked(Character* character, ActorVisualState state)
    {
        if (character == null)
            return;

        ApplyCombatFlags(character);
        ClearMovement(character, state);
        state.Kind = ActorVisualStateKind.ActionLocked;
    }

    public static void ApplyDead(Character* character, ActorVisualState state)
    {
        ClearMovement(character, state);
        state.Kind = ActorVisualStateKind.Dead;
    }

    public static void ClearMovement(Character* character, ActorVisualState state)
    {
        if (character != null &&
            (state.Kind == ActorVisualStateKind.Moving || character->Timeline.BaseOverride is NormalRunTimelineId or NormalWalkTimelineId))
        {
            character->Timeline.BaseOverride = 0;
        }

        state.MovementReassertTimer = 0;
    }

    private static void ApplyCombatFlags(Character* character)
    {
        character->CharacterData.InCombat = true;
        character->CharacterData.IsHostile = true;
        character->Timeline.IsWeaponDrawn = true;
        character->Timeline.ModelState = 1;
    }
}
