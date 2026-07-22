using System.Collections.Generic;
using Newtonsoft.Json;

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
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<uint> SpectatorEmoteIds { get; set; } =
        Spectators.SpectatorController.CreateDefaultEmoteIds();
    public List<string> SpectatorExcludedNames { get; set; } = new();
    public bool SpectatorChatterEnabled { get; set; } = false;
    public bool SpectatorChatterTtsBridge { get; set; } = true;
    public float SpectatorChatterChancePerSecond { get; set; } = 5f;
    public float SpectatorChatterBubbleDuration { get; set; } = 4.5f;
    public int SpectatorChatterMaxConcurrent { get; set; } = 20;
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> SpectatorBattleChatterLines { get; set; } =
        Spectators.SpectatorController.CreateDefaultBattleChatterLines();
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> SpectatorDecisiveChatterLines { get; set; } =
        Spectators.SpectatorController.CreateDefaultDecisiveChatterLines();
    public bool SpectatorCollectionReplaceRepairApplied { get; set; }

    // Legacy single-pool storage. SpectatorController copies this list verbatim into the
    // decisive pool on load, then clears this property. Null is intentional for new configs:
    // it distinguishes a new install from a config that actually contains the old list.
    public List<string>? SpectatorChatterLines { get; set; }

    // Legacy v1 storage. MainWindow migrates these IDs to name-wide exclusions once the
    // NPC catalog is available, then clears the list.
    public List<uint> SpectatorExcludedENpcIds { get; set; } = new();
}
