// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Animation;

/// <summary>
/// Physics constants that used to be Ragdoll-page settings. Every value equals the setting's former
/// default; the runtime clamps that applied to the settings still apply where the values are used.
/// </summary>
public static class RagdollTuning
{
    public const float Gravity = 9.8f;
    public const float Damping = 0.97f;

    // Velocity iterations per substep x substeps per fixed step. The player's 4 x 16 is the same 64
    // solver passes as the old 8 x 8 at twice the effective solve rate (960 Hz), which oversamples the
    // 90 Hz limit wall ~11x. Corpses of other actors get a quarter of that budget: a ragdoll is only
    // expensive until it settles, so what spikes a wipe is many corpses activating at once. Party mode
    // and non-humanoid rigs still apply their own floors.
    public const int SolverIterations = 4;
    public const int SolverSubsteps = 16;
    public const int NpcSolverIterations = 2;
    public const int NpcSolverSubsteps = 8;
    // The light budget for other characters' corpses (default): half the substeps again. The solve
    // rate (240 Hz) stays above twice the 90 Hz limit walls. Fine detail restores the 8.
    public const int NpcLightSolverSubsteps = 4;

    // Spring frequency (Hz) of the joint limit walls, and the soft overdamped edge used on ball-joint
    // swing cones so a joint sliding to its range edge settles instead of pinning at the extreme.
    public const float LimitSpringFrequency = 90f;
    public const float SoftLimitFrequency = 12f;
    public const float SoftLimitDamping = 4f;

    // Positional joint springs: the body, and the firmer foot, which takes the hardest impacts.
    public const float JointSpringFrequency = 45f;
    public const float FootJointSpringFrequency = 60f;

    public const float BodyMass = 70f;
    public const float Friction = 1f;
    public const float NpcActivationDelay = 0.5f;

    public const float WeaponDropGravity = 9.8f;
    public const float WeaponDropDamping = 0.99f;
    public const float WeaponDropAngularDamping = 0.85f;
    // Heavy on purpose: the box must not be skated around by the corpse's collider or bounced by a
    // contact it lands on, and nothing about the drop depends on it being physically honest.
    public const float WeaponDropMass = 15f;
    public const float WeaponDropRadius = 0.025f;
    public const float WeaponDropHalfLength = 0.4f;
    public const float WeaponDropBounce = 1.5f;
    public const float WeaponDropFriction = 0.6f;
    public const int WeaponDropSolverIterations = 4;

    public const float HairBendCompliance = 0.02f;
    public const float HairDamping = 2.2f;
    public const float HairSwingDamping = 0.15f;
    public const float HairSegmentMass = 0.02f;
    public const float HairThickness = 0.008f;
}
