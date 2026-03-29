using System;

namespace CombatSimulator.Dev;

[Serializable]
public class VictorySequenceStage
{
    public float StartTime { get; set; }
    public float EndTime { get; set; } = 3.0f;       // -1 = infinite (last stage runs forever)
    public float StartDistance { get; set; } = 3.0f;
    public float EndDistance { get; set; } = 3.0f;
    public bool UseEmote { get; set; }               // false = action timeline, true = emote
    public uint EmoteId { get; set; }                 // Emote sheet RowId (when UseEmote=true)
    public uint ActionTimelineId { get; set; }        // ActionTimeline RowId (when UseEmote=false)
    public ushort ResolvedIntroTimeline { get; set; } // auto-filled from emote
    public ushort ResolvedLoopTimeline { get; set; }  // auto-filled from emote
    public bool GrabEnabled { get; set; }
    public string NpcBoneName { get; set; } = "j_te_r";
    public string PlayerBoneName { get; set; } = "j_kubi";
    public float HeightOffset { get; set; }
    // Grab physics parameters
    public float GrabForce { get; set; } = 1000f;
    public float GrabSpeed { get; set; } = 50f;
    public float GrabSpringFreq { get; set; } = 120f;
}
