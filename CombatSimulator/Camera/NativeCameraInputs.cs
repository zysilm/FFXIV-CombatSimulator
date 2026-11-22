using System.Numerics;

namespace CombatSimulator.Camera;

/// <summary>Temporary inputs to the existing Active Camera calculation; null values use user settings.</summary>
public sealed record ActiveCameraInputs
{
    public Vector3? Focus { get; init; }
    public float? Height { get; init; }
    public float? Side { get; init; }
    public float? MinDistance { get; init; }
    /// <summary>Entry/owned native zoom input, applied by the caller's pre-camera-update adapter.</summary>
    public float? Distance { get; init; }
    public bool? LockVertical { get; init; }
    public float? VerticalAngle { get; init; }
}

/// <summary>Temporary death-shot inputs, in the terms of the death framing they were written for. The automatic death
/// shots read <see cref="Angle"/> (as a profile), <see cref="BodyVisibility"/> (as the shot size) and
/// <see cref="IgnoreActor"/>; the rest has no counterpart (DynamicCameraController.Inputs.cs, docs/DEATH_SHOTS.md §10).</summary>
public sealed record DynamicDeathCameraInputs
{
    public float? BodyVisibility { get; init; }
    public float? BodyBand { get; init; }
    public float? Angle { get; init; }
    public bool? MaximizeBody { get; init; }
    public float? CloseUpDistance { get; init; }
    public bool? IgnoreActor { get; init; }
    public float? Transition { get; init; }
}

/// <summary>Identity checked before a temporary actor is used as the death shot's subject.</summary>
public readonly record struct NativeCameraActor(nint Address, uint EntityId);
