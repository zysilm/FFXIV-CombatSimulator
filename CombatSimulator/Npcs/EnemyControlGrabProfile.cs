using System;
using System.Collections.Generic;

namespace CombatSimulator.Npcs;

/// <summary>
/// A saved snapshot of the whole EnemyControl grab-as-attack setup: the pair list, the shared fitting
/// tuning, and the arm-pose sliders. Interaction mirrors MainWindow's Ragdoll bone-profile list
/// (load/overwrite/delete/save) — see MainWindow.DrawEnemyControlGrabProfilesSection.
/// </summary>
[Serializable]
public sealed class EnemyControlGrabProfile
{
    public string Name { get; set; } = string.Empty;

    public List<EnemyControlGrabPairConfig> Pairs { get; set; } = new();
    // 0 = the built-in grab-and-hold pose; otherwise an Emote sheet row id — see
    // EnemyControlController.ResolveGrabAnimationTimeline.
    public uint GrabEmoteId { get; set; } = 0;
    public bool GrabAnyBone { get; set; }
    public bool GrabAnyNpcBone { get; set; }
    public bool GrabRigid { get; set; }
    public bool GrabSoftContact { get; set; } = true;
    public float GrabGripReach { get; set; } = 1.75f;
    public float GrabServoForce { get; set; } = 1000f;
    public float GrabServoSpeed { get; set; } = 50f;
    public float GrabServoFrequency { get; set; } = 120f;
    public bool GrabConformFingers { get; set; } = true;
    public float GrabFingerStrength { get; set; } = 0.6f;
    public float GrabFingerMaxAngle { get; set; } = 45f;
    public float GrabFingerClearance { get; set; } = 0f;

    public bool ArmPoseEnabled { get; set; }
    public float LeftArmUpperPitch { get; set; }
    public float LeftArmUpperYaw { get; set; }
    public float LeftArmUpperRoll { get; set; }
    public float LeftArmLowerPitch { get; set; }
    public float LeftArmLowerYaw { get; set; }
    public float LeftArmLowerRoll { get; set; }
    public float RightArmUpperPitch { get; set; }
    public float RightArmUpperYaw { get; set; }
    public float RightArmUpperRoll { get; set; }
    public float RightArmLowerPitch { get; set; }
    public float RightArmLowerYaw { get; set; }
    public float RightArmLowerRoll { get; set; }
}
