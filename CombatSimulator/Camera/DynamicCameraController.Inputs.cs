using System;

namespace CombatSimulator.Camera;

public sealed unsafe partial class DynamicCameraController
{
    public Func<DynamicDeathCameraInputs?>? TemporaryDeathInputs { get; set; }
    public Func<NativeCameraActor?>? TemporaryDeathActor { get; set; }
    private NativeCameraActor? previousTemporaryDeathActor;
    private DynamicDeathCameraInputs? DeathInputs => TemporaryDeathInputs?.Invoke();
    private static float DeathInput(float? value, float fallback) =>
        value.HasValue && float.IsFinite(value.Value) ? value.Value : fallback;
    private float EffectiveDeathBodyVisibility => DeathInput(DeathInputs?.BodyVisibility, config.DynCamDeathBodyVisibility);
    private float EffectiveDeathBodyBand => DeathInput(DeathInputs?.BodyBand, config.DynCamDeathBodyBand);
    private float EffectiveDeathAngle => DeathInput(DeathInputs?.Angle, config.DynCamDeathAngle);
    private bool EffectiveDeathMaximizeBody => DeathInputs?.MaximizeBody ?? config.DynCamDeathMaximizeBody;
    private float EffectiveDeathCloseUpDistance => DeathInput(DeathInputs?.CloseUpDistance, config.DynCamDeathCloseUpDistance);
    private bool EffectiveDeathIgnoreKiller => DeathInputs?.IgnoreActor ?? config.DynCamDeathIgnoreKiller;
    private float EffectiveDeathTranslateDuration => DeathInput(DeathInputs?.Transition, config.DynCamDeathTranslateDuration);

    private void UpdateTemporaryDeathFocus(NativeCameraActor? actor)
    {
        if (previousTemporaryDeathActor == actor) return;
        previousTemporaryDeathActor = actor;
        ResetKillerEnvelope();
        killerFit = KillerFit.FullBody;
        ladderLevel = 0;
        ladderDwell = 0f;
    }

    private bool TryResolveTemporaryDeathActor(NativeCameraActor actor, out nint address)
    {
        address = nint.Zero;
        if (actor.Address == nint.Zero || actor.EntityId == 0) return false;
        foreach (var obj in Core.Services.ObjectTable)
        {
            if (obj.Address != actor.Address || obj.EntityId != actor.EntityId) continue;
            address = obj.Address;
            return true;
        }
        return false;
    }
}
