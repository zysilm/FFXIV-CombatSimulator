using System.Collections.Generic;

namespace CombatSimulator;

/// <summary>
/// Persisted settings for the standalone spectator crowd. Live actors and their random
/// assignments remain world-session state owned by <see cref="Spectators.SpectatorController"/>.
/// </summary>
public partial class Configuration
{
    public uint SpectatorHumanENpcId { get; set; }
    public int SpectatorSpawnCount { get; set; } = 12;
    public float SpectatorDistance { get; set; } = 8f;
    public List<uint> SpectatorEmoteIds { get; set; } = new();
    public List<string> SpectatorExcludedNames { get; set; } = new();
    public bool SpectatorChatterEnabled { get; set; } = false;
    public bool SpectatorChatterTtsBridge { get; set; } = true;
    public float SpectatorChatterChancePerSecond { get; set; } = 5f;
    public float SpectatorChatterBubbleDuration { get; set; } = 4.5f;
    public int SpectatorChatterMaxConcurrent { get; set; } = 20;
    public List<string> SpectatorChatterLines { get; set; } =
        Spectators.SpectatorController.CreateDefaultChatterLines();

    // Legacy v1 storage. MainWindow migrates these IDs to name-wide exclusions once the
    // NPC catalog is available, then clears the list.
    public List<uint> SpectatorExcludedENpcIds { get; set; } = new();
}
