using System;

namespace CombatSimulator.Dev;

[Serializable]
public class VictorySequenceStage
{
    public float StartTime { get; set; }
    public float EndTime { get; set; } = 3.0f;
    public float StartDistance { get; set; } = 3.0f;
    public float EndDistance { get; set; } = 3.0f;
    public uint EmoteId { get; set; }               // Emote sheet RowId (0 = no animation)
    public ushort AnimationTimelineId { get; set; }  // auto-filled from emote intro timeline
    public ushort LoopTimelineId { get; set; }       // auto-filled from emote loop timeline
    public bool GrabEnabled { get; set; }
    public string NpcBoneName { get; set; } = "j_te_r";
    public string PlayerBoneName { get; set; } = "j_kubi";
    public float HeightOffset { get; set; }
}
