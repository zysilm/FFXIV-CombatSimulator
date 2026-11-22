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
///  • <see cref="TemporaryDeathInputs"/>: picks the profile and the shot size (see <see cref="ProfileFor"/>,
///    <see cref="SizeFor"/>). Null: the user's own profile, the profile's own size.
///  • <see cref="TemporaryDeathActor"/>: the body the shot frames instead of the player's, when it is a character
///    in the object table (and <see cref="DynamicDeathCameraInputs.IgnoreActor"/> is not set). The director starts
///    the shot again when the body changes, and goes back to the player's when this one cannot be read.
/// </summary>
public sealed unsafe partial class DynamicCameraController
{
    public Func<DynamicDeathCameraInputs?>? TemporaryDeathInputs { get; set; }
    public Func<NativeCameraActor?>? TemporaryDeathActor { get; set; }

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
    /// The old fit's body coverage (0.25 head and chest … 1 head to feet … 2 space around the body) as the shot
    /// size on top of the profile's own: 1 stays 1, less is tighter, more is wider, within what the solver takes
    /// (0.5–2). The other inputs of the old fit (body band, maximize, close-up distance, transition time) have no
    /// counterpart: the profile decides them.
    /// </summary>
    internal static float SizeFor(DynamicDeathCameraInputs? inputs) =>
        inputs?.BodyVisibility is { } visible && float.IsFinite(visible) ? Math.Clamp(visible, 0.5f, 2f) : 1f;

    private DeathShotRequest DeathShotRequestFor(nint playerAddress)
    {
        var inputs = TemporaryDeathInputs?.Invoke();
        var subject = playerAddress;
        var label = "you";
        if (inputs?.IgnoreActor != true && TemporaryDeathActor?.Invoke() is { } actor &&
            TryResolveTemporaryDeathActor(actor, out var address, out var name) && address != playerAddress)
        {
            subject = address;
            label = name;
        }
        return new DeathShotRequest(
            playerAddress,
            subject,
            label,
            ProfileFor(inputs, config.DeathShotProfile),
            SizeFor(inputs),
            // The player's ragdoll is armed when the death animation starts; another body's is armed or it is not.
            RagdollToCome: !deathAnimationStarted);
    }

    private static bool TryResolveTemporaryDeathActor(NativeCameraActor actor, out nint address, out string name)
    {
        address = nint.Zero;
        name = "";
        if (actor.Address == nint.Zero || actor.EntityId == 0) return false;
        foreach (var obj in Core.Services.ObjectTable)
        {
            if (obj.Address != actor.Address || obj.EntityId != actor.EntityId) continue;
            address = obj.Address;
            name = $"{obj.Name.TextValue} (0x{address:X})";
            return true;
        }
        return false;
    }
}
