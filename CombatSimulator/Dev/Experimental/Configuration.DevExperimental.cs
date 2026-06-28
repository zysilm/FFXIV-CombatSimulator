using System.Collections.Generic;
using CombatSimulator.Dev;

namespace CombatSimulator;

// Configuration fields that reference experimental dev types (victory cinema stages/presets).
// Kept in the experimental module so the public Configuration has no dependency on those types.
public partial class Configuration
{
    public List<VictorySequenceStage> VictorySequenceStages { get; set; } = new();
    public List<VictorySequenceStage> VictorySequenceOtherStages { get; set; } = new();
    public List<VictoryCinematicPreset> VictoryCinematicPresets { get; set; } = new();
}
