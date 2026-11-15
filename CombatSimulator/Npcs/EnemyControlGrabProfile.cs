using System;
using System.Collections.Generic;

namespace CombatSimulator.Npcs;

/// <summary>
/// A saved snapshot of the whole EnemyControl grab setup: the pair list, the grab animation and the
/// shared fitting tuning. Interaction mirrors MainWindow's Ragdoll bone-profile list
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
}
