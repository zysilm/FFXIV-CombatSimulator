// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using AdvancedRagdoll.SkeletonProfiles;

namespace AdvancedRagdoll.Hosting;

// What the dropped-weapon simulation (AdvancedRagdoll.WeaponDrop.WeaponDropSimulation, stage A5) asks of its host.
// It reuses the ragdoll's host interfaces (skeletons, world queries, characters, settings, log, clock) and adds
// one narrow interface for the weapons themselves. Characters and weapons are opaque ids (the game object address
// and the weapon's draw object address in the plugin), as everywhere else in the host interfaces.

/// <summary>A character's weapon slot.</summary>
internal enum DrawnWeaponSlot
{
    MainHand = 0,
    OffHand = 1,
}

/// <summary>
/// The weapons a character draws: each is a separate draw object with a skeleton of its own, whose root the host
/// attaches to the hand every frame before the simulation runs.
/// </summary>
internal interface IWeaponDrawSource
{
    /// <summary>The weapon drawn in a slot (opaque id), or 0 when the slot is unarmed or not loaded.
    /// <paramref name="actor"/> is never 0.</summary>
    nint GetWeapon(nint actor, DrawnWeaponSlot slot);

    /// <summary>The world transform of the weapon's skeleton root; false when the weapon has no skeleton.</summary>
    bool TryGetRoot(nint weapon, out Vector3 position, out Quaternion rotation);

    /// <summary>The weapon's own skeleton with a readable pose, or null.</summary>
    IHostSkeleton? TryGetSkeleton(nint weapon);

    /// <summary>The scale the weapon is drawn with.</summary>
    Vector3 GetDrawScale(nint weapon);

    /// <summary>Moves the drawn weapon: its skeleton root (when it has one), which the weapon mesh is skinned to,
    /// then the draw object's own position and rotation.</summary>
    void SetTransform(nint weapon, Vector3 position, Quaternion rotation);
}

/// <summary>The host services of a dropped-weapon simulation.</summary>
internal sealed class WeaponDropHost
{
    /// <summary>The characters' body skeletons (their pose drives the kinematic body colliders).</summary>
    public required IHostSkeletonSource Skeletons { get; init; }
    /// <summary>Ground rays for the terrain patch under each drop.</summary>
    public required IHostWorldQuery World { get; init; }
    /// <summary>The local player and the characters' draw scale.</summary>
    public required IHostActors Actors { get; init; }
    public required IWeaponDrawSource Weapons { get; init; }
    /// <summary>Read when a drop spawns (which characters get the detailed mesh contacts).</summary>
    public required IHostSettingsSource Settings { get; init; }
    public required IRagdollLog Log { get; init; }
    /// <summary>Which arm bones hold the weapon (their contacts are suppressed until the weapon has left the hand).</summary>
    public required SkeletonProfile Profile { get; init; }
    public IRagdollClock Clock { get; init; } = StopwatchRagdollClock.Instance;
}
