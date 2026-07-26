// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
