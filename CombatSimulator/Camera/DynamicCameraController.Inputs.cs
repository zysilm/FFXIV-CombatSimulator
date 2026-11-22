using System;
using CombatSimulator.Camera.DeathShots;

namespace CombatSimulator.Camera;

/// <summary>
/// What a scene outside the plugin's own options (the private module's stories) may say about the death shot, and
/// how it reaches the automatic death shots (docs/DEATH_SHOTS.md §10). The members keep the names and types they had
/// when the death framing was the Directed camera's own fit; what they mean now:
///
///  • <see cref="DynamicCameraController.ProfileFramingOverride"/> true: the scene wants the Directed camera's
///    framing whatever the "Directs" option says: death shots (and combat framing) run, and the combat zoom and
///    pitch it learns meanwhile are not saved.
///  • <see cref="TemporaryDeathInputs"/> non-null: a scene directs the shot. It picks the profile and the shot size
///    (see <see cref="ProfileFor"/>, <see cref="SizeFor"/>), and a jump of the game camera's yaw (the scene's entry
///    yaw, which it writes itself before the camera update) is taken as the azimuth it wants for the shot. Null: the
///    user's own profile, the profile's own size, and the yaw is the player's hand.
///  • <see cref="TemporaryDeathActor"/>: ignored for framing. The shot frames the player's body only (first version:
///    no second subject); the old fit used this actor as the second person of its two-shot. Logged once per death.
///  • <see cref="TemporaryDeathInputLock"/> (new, optional): whether the scene locks the player's camera input. The
///    scene's own lock (it pins the game camera's yaw) cannot be seen from here, so until it is set a shot a scene
///    directs ignores the wheel.
/// </summary>
public sealed unsafe partial class DynamicCameraController
{
    public Func<DynamicDeathCameraInputs?>? TemporaryDeathInputs { get; set; }
    public Func<NativeCameraActor?>? TemporaryDeathActor { get; set; }
    public Func<bool>? TemporaryDeathInputLock { get; set; }
    private bool loggedActorIgnored;

    /// <summary>
    /// The old fit's angle (radians, positive = looking down; its three presets were −0.25 "Looking up", 0.45
    /// "Default", 0.8 "Looking down") as a profile: looking up → Ground profile (the same shot), looking down →
    /// Overhead, between → Auto. No angle: the user's profile.
    /// </summary>
    internal static DeathShotProfile ProfileFor(DynamicDeathCameraInputs? inputs, DeathShotProfile users)
    {
        if (inputs?.Angle is not { } angle || !float.IsFinite(angle)) return users;
        if (angle < 0.1f) return DeathShotProfile.GroundProfile;
        if (angle > 0.625f) return DeathShotProfile.Overhead;
        return DeathShotProfile.Auto;
    }

    /// <summary>
    /// The old fit's body coverage as the shot size on top of the profile's own, within 0.5–2: 1 stays 1, less is
    /// tighter, more is wider. NOT the same thing as before: the old value counted how much of the body had to stay in
    /// frame from the head down (0.25 head and chest, 0.5 head to waist, 1 head to feet, above 1 space around it); here
    /// it only scales whatever the profile frames (0.5 on Face is a tighter face, not "head to waist"; on a whole-body
    /// profile it comes closer than the body fits). The other inputs of the old fit (body band, maximize, close-up
    /// distance, transition time) have no counterpart: the profile decides them.
    /// </summary>
    internal static float SizeFor(DynamicDeathCameraInputs? inputs) =>
        inputs?.BodyVisibility is { } visible && float.IsFinite(visible) ? Math.Clamp(visible, 0.5f, 2f) : 1f;

    private DeathShotRequest DeathShotRequestFor(nint playerAddress)
    {
        var inputs = TemporaryDeathInputs?.Invoke();
        var scene = inputs != null;
        if (scene && !loggedActorIgnored && inputs!.IgnoreActor != true && TemporaryDeathActor?.Invoke() is { } actor)
        {
            loggedActorIgnored = true;
            log.Info($"DeathShot: the scene names a second actor (entity {actor.EntityId:X}); it is ignored — the automatic death shot frames the player's body only.");
        }
        var locked = scene && TemporaryDeathInputLock?.Invoke() == true;
        return new DeathShotRequest(
            playerAddress,
            ProfileFor(inputs, config.DeathShotProfile),
            SizeFor(inputs),
            // The player's ragdoll is armed when the death animation starts.
            RagdollToCome: !deathAnimationStarted,
            SceneDirected: scene,
            InputLocked: locked,
            // Unknown lock (the scene has not said): no wheel, so a locked scene is never resized by it.
            WheelLocked: scene && (TemporaryDeathInputLock == null || locked));
    }
}
