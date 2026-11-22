// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using AdvancedRagdoll.Hosting;

namespace CombatSimulator;

/// <summary>
/// Ragdoll tab > Ragdoll Trigger > "Death guidance": what the body does when physics takes it over from the death
/// animation (docs/RAGDOLL_DEATH_GUIDANCE.md). One setting for the player, allies and enemies. Saved as a number:
/// the values are the saved ones and never change; the dropdown has its own order
/// (<see cref="RagdollDeathGuidances.DropdownOrder"/>).
/// </summary>
public enum RagdollDeathGuidance
{
    /// <summary>Nothing is held: the plain ragdoll.</summary>
    Limp = 0,
    /// <summary>The knees give way first; only on a body that still stands when physics starts.</summary>
    KneeBuckle = 1,
    /// <summary>Hip, knee and ankle joints keep their angles; lower legs and feet on the ground stay put.</summary>
    HoldLegs = 2,
    /// <summary>Legs and spine keep their angles.</summary>
    HoldPose = 3,
    /// <summary>The whole body is held and let go in order: arms and head, torso, legs.</summary>
    SlowSlump = 4,
    /// <summary>Chosen by the library from how the body is posed and moving when physics starts.</summary>
    Auto = 5,
}

public static class RagdollDeathGuidances
{
    /// <summary>The dropdown, top to bottom.</summary>
    public static readonly RagdollDeathGuidance[] DropdownOrder =
    {
        RagdollDeathGuidance.Auto, RagdollDeathGuidance.Limp, RagdollDeathGuidance.KneeBuckle,
        RagdollDeathGuidance.HoldLegs, RagdollDeathGuidance.HoldPose, RagdollDeathGuidance.SlowSlump,
    };

    /// <summary>The labels, in <see cref="DropdownOrder"/>.</summary>
    public static readonly string[] DropdownLabels =
        { "Auto", "Limp", "Knee buckle", "Hold the legs", "Hold the pose", "Slow slump" };

    /// <summary>What each choice does and how it did offline, in <see cref="DropdownOrder"/>.</summary>
    public static readonly string[] DropdownHelp =
    {
        "Auto: picks one from how the body is when physics starts. Standing: Knee buckle. Still falling: Slow slump.\n" +
        "  Kneeling, crouched or lying: Hold the legs. Creatures without a humanoid skeleton: Hold the pose.",
        "Limp: nothing is held, the plain ragdoll.",
        "Knee buckle: the knees give way and the body folds forward over them. Only when the body still stands\n" +
        "  when physics starts (a delay near zero); otherwise the same as Limp. Jittery in offline tests.",
        "Hold the legs: hips, knees and ankles keep their angles and lower legs resting on the ground stay where\n" +
        "  they are. On lying poses the legs keep their shape and the body drifts less than Limp. On kneeling or\n" +
        "  crouched poses the shins stay put, but the body folds forward and jitters more than Limp.",
        "Hold the pose: like Hold the legs, and the spine keeps its shape too (arms, head and neck stay loose).",
        "Slow slump: the whole body is held for a moment, then lets go in order: arms and head, torso, legs.\n" +
        "  Keeps the direction of the fall; slightly jittery in offline tests.",
    };

    public static RagdollDeathGuidance Clamp(RagdollDeathGuidance value)
        => value is >= RagdollDeathGuidance.Limp and <= RagdollDeathGuidance.Auto ? value : RagdollDeathGuidance.Limp;

    /// <summary>
    /// The setting from the retired "Knee buckle on death" switch (GuidedCollapse.Enabled): on = Knee buckle,
    /// off = Limp.
    /// </summary>
    public static RagdollDeathGuidance FromRetiredKneeBuckleSwitch(bool enabled)
        => enabled ? RagdollDeathGuidance.KneeBuckle : RagdollDeathGuidance.Limp;

    /// <summary>The library's profile of a choice (GuidedCollapseOptions.TensionProfile, read when physics starts).</summary>
    internal static RagdollTensionProfile ToLibrary(RagdollDeathGuidance value) => value switch
    {
        RagdollDeathGuidance.KneeBuckle => RagdollTensionProfile.KneeBuckle,
        RagdollDeathGuidance.HoldLegs => RagdollTensionProfile.HoldLegs,
        RagdollDeathGuidance.HoldPose => RagdollTensionProfile.HoldPose,
        RagdollDeathGuidance.SlowSlump => RagdollTensionProfile.SlowSlump,
        RagdollDeathGuidance.Auto => RagdollTensionProfile.Auto,
        _ => RagdollTensionProfile.None,
    };
}
