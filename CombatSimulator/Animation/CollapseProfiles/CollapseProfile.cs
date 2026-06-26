using System.Collections.Generic;

namespace CombatSimulator.Animation.CollapseProfiles;

public sealed class CollapseProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<CollapsePhaseProfile> Phases { get; set; } = new();
}

public sealed class CollapsePhaseProfile
{
    public string Name { get; set; } = string.Empty;
    public float Duration { get; set; } = 1f;
    public List<CollapseControllerProfile> Controllers { get; set; } = new();
}

public sealed class CollapseControllerProfile
{
    public string Type { get; set; } = string.Empty;
    public List<string> Bones { get; set; } = new();
    public string Bone { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public float Force { get; set; }
    public float Frequency { get; set; }
    public float MaxSpeed { get; set; } = 12f;
    public float StartTime { get; set; }
    public float EndTime { get; set; } = 1f;
    public float StrengthFrom { get; set; } = 1f;
    public float StrengthTo { get; set; } = 1f;
    public float Drop { get; set; }
    public float Distance { get; set; }
    public float PitchDegrees { get; set; }
    public float PelvisPitchScale { get; set; } = 0.35f;
    public string TargetBone { get; set; } = string.Empty;
}
