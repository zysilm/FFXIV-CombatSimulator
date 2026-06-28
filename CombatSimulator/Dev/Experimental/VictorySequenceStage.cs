using System;

namespace CombatSimulator.Dev;

[Serializable]
public class VictorySequenceStage
{
    // Legacy absolute timing is kept for old saved presets. New editing/runtime
    // uses Duration only; StartTime/EndTime are synchronized for compatibility.
    public float StartTime { get; set; }
    public float EndTime { get; set; } = 3.0f;       // -1 = infinite time
    public float? Duration { get; set; }
    public bool UseEmote { get; set; }
    public uint EmoteId { get; set; }
    public uint ActionTimelineId { get; set; }
    public int EmoteVariant { get; set; }              // 0=Standing, 1=Ground, 2=Chair, 3=UpperBody
    public ushort ResolvedIntroTimeline { get; set; }
    public ushort ResolvedLoopTimeline { get; set; }
    public bool GrabEnabled { get; set; }
    public string NpcBoneName { get; set; } = "j_te_r";
    public string PlayerBoneName { get; set; } = "j_kubi";
    public float GrabForce { get; set; } = 1000f;
    public float GrabSpeed { get; set; } = 50f;
    public float GrabSpringFreq { get; set; } = 120f;
    // Offset of the grab target in the NPC bone's LOCAL axes (meters).
    // For long-armed enemies where j_te_r sits at the wrist, +Y typically
    // shifts the player toward the fingertips so they land on the palm
    // instead of dangling off the wrist joint.
    public float GrabOffsetX { get; set; }
    public float GrabOffsetY { get; set; }
    public float GrabOffsetZ { get; set; }

    // Shoulder rotation override — tweaks the grabbing NPC's upper arm so the
    // arm pose matches the grab position. Manual pitch/yaw/roll (degrees, local
    // bone space). Applied every render frame via BoneTransformService.
    // ApplyRotationDeltas, which propagates the delta down the arm chain.
    public bool ShoulderRotationEnabled { get; set; }
    public string ShoulderBoneName { get; set; } = "j_ude_a_r";
    public float ShoulderPitch { get; set; } // X rotation, degrees
    public float ShoulderYaw { get; set; }   // Y rotation, degrees
    public float ShoulderRoll { get; set; }  // Z rotation, degrees
    public bool LockFacing { get; set; } = true;

    public bool ApproachBeforeStage { get; set; }
    public float ApproachDistance { get; set; } = 2.0f;
}
