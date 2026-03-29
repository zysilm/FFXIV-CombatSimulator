using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CombatSimulator.Dev;

[Serializable]
public class VictoryCinematicPreset
{
    public string Name { get; set; } = "Preset";
    public List<VictorySequenceStage> Stages { get; set; } = new();

    /// <summary>Deep clone the stages from a source list.</summary>
    public static List<VictorySequenceStage> CloneStages(List<VictorySequenceStage> source)
    {
        // Use JSON round-trip for deep clone (all fields are serializable)
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<List<VictorySequenceStage>>(json) ?? new();
    }
}
