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
    private const float MovementReassertInterval = 0.25f;

    public static void ApplyMoving(Character* character, ActorVisualState state, float deltaTime)
    {
        if (character == null)
            return;

        ApplyCombatFlags(character);

        state.MovementReassertTimer = MathF.Max(0, state.MovementReassertTimer - deltaTime);
        var entering = state.Kind != ActorVisualStateKind.Moving;
        var shouldReassert = entering || state.MovementReassertTimer <= 0;

        character->Timeline.BaseOverride = NormalRunTimelineId;
        if (shouldReassert && character->Timeline.TimelineSequencer.Parent != null)
        {
            character->Timeline.PlayActionTimeline(NormalRunTimelineId);
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
            (state.Kind == ActorVisualStateKind.Moving || character->Timeline.BaseOverride == NormalRunTimelineId))
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
