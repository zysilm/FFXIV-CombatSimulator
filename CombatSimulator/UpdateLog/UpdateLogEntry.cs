using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CombatSimulator.UpdateLog;

public sealed class UpdateLogEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("changes")]
    public List<string> Changes { get; set; } = [];
}
