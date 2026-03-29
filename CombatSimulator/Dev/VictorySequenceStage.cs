using System;

namespace CombatSimulator.Dev;

[Serializable]
public class VictorySequenceStage
{
    public string Label { get; set; } = "Stage";
    public float StartTime { get; set; }
    public float EndTime { get; set; }
    public float StartDistance { get; set; } = 3.0f;
    public float EndDistance { get; set; } = 3.0f;
    public ushort AnimationTimelineId { get; set; } // intro/one-shot timeline (0 = no change)
    public ushort LoopTimelineId { get; set; }      // loop part (0 = no loop)
    public bool GrabEnabled { get; set; }
    public string NpcBoneName { get; set; } = "j_te_r";     // NPC hand bone
    public string PlayerBoneName { get; set; } = "j_kubi";   // player neck bone
    public float HeightOffset { get; set; }
}
