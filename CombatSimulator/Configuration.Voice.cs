// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Master switch for enemy voices (voiced lines, bubbles and labeled shouts). On by
    /// default: enemies talk out of the box. The native sound player is still only touched once a
    /// fight is running, never at load.</summary>
    public bool EnableEnemyVoices { get; set; } = true;
    /// <summary>Show the line's text in the game's overhead speech bubble.</summary>
    public bool EnemyVoiceBubbles { get; set; } = true;
    /// <summary>Seconds of quiet after a voiced line before an ordinary event may start another.
    /// Engage, kills, victory and deaths wait only half a second. Also sets how often engaged enemies
    /// taunt between attacks (<see cref="Voice.VoiceDirector.IdleTauntInterval"/>).</summary>
    public float EnemyVoiceLineGap { get; set; } = 3.5f;
    /// <summary>One-time switch-on for configs saved while voices were off-by-default.</summary>
    public bool EnemyVoiceDefaultsMigrated20261024 { get; set; } = false;
    /// <summary>0..1 volume handed to the game's sound player (before the game's own sliders).</summary>
    public float EnemyVoiceVolume { get; set; } = 1.0f;
    public float EnemyVoiceAllyDeathRadius { get; set; } = 15f;
    /// <summary>A killing blow on the player or a companion always gets a line: it waits for the
    /// speaker and the line gate, and a killer that cannot speak hands it to the nearest enemy that can.</summary>
    public bool EnemyVoiceGuaranteeKills { get; set; } = true;
    /// <summary>After the party is wiped, surviving enemies keep taunting the fallen player.</summary>
    public bool EnemyVoiceDefeatTaunts { get; set; } = false;
    /// <summary>Seconds between taunts over the fallen player.</summary>
    public float EnemyVoiceDefeatTauntInterval { get; set; } = 8f;
    /// <summary>Voiced-line personas that are named story characters (Alphinaud, Y'shtola...) may be
    /// given to ordinary enemies. Off: only the unnamed personas are assigned. Not in the UI.</summary>
    public bool EnemyVoiceAllowStoryCharacters { get; set; } = true;
    /// <summary>Hand-picked speaker persona per kind of enemy: key from
    /// <see cref="Voice.VoicePersonaAssigner.OverrideKey"/> ("{BNpcBase}:{BNpcName}", or "name:{name}"),
    /// value a persona id of VoicePersonas.json. No entry = automatic choice.</summary>
    public Dictionary<string, string> EnemyVoicePersonaOverrides { get; set; } = new();
    /// <summary>Entry labels recorded by the owner with the old audition panel: label key ("pc", "npc",
    /// "mon/{sepack}") -> label -> entries. Still applied on top of Resources/VoiceEntryLabels.json;
    /// no longer editable in the UI.</summary>
    public Dictionary<string, Dictionary<string, List<int>>> EnemyVoiceEntryLabels { get; set; } = new();
}
