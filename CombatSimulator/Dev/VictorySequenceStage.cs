using System;

namespace CombatSimulator.Dev;

[Serializable]
public class VictorySequenceStage
{
    public float StartTime { get; set; }
    public float EndTime { get; set; } = 3.0f;       // -1 = infinite time
    public float StartDistance { get; set; } = 3.0f;
    public float EndDistance { get; set; } = 3.0f;
    public bool UseEmote { get; set; }
    public uint EmoteId { get; set; }
    public uint ActionTimelineId { get; set; }
    public int EmoteVariant { get; set; }              // 0=Standing, 1=Ground, 2=Chair, 3=UpperBody
    public ushort ResolvedIntroTimeline { get; set; }
    public ushort ResolvedLoopTimeline { get; set; }
    public bool GrabEnabled { get; set; }
    public string NpcBoneName { get; set; } = "j_te_r";
    public string PlayerBoneName { get; set; } = "j_kubi";
    public float HeightOffset { get; set; }
    public float GrabForce { get; set; } = 1000f;
    public float GrabSpeed { get; set; } = 50f;
    public float GrabSpringFreq { get; set; } = 120f;
    // Infinite walk: NPC walks toward player at constant speed forever
    public bool InfiniteWalk { get; set; }
    public float WalkSpeed { get; set; } = 0f;          // yalms per second (negative = walk away)
    public bool LockFacing { get; set; } = true;        // lock facing to initial approach direction (prevents 180° flip)
}
