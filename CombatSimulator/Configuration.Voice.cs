// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Master switch for enemy voices (voiced lines, bubbles and labeled shouts). On by
    /// default: enemies talk out of the box. The native sound player is still only touched once a
    /// fight is running, never at load.</summary>
    public bool EnableEnemyVoices { get; set; } = true;
    /// <summary>Curated voiced battle lines (Resources/VoiceLines.json) for human enemies.</summary>
    public bool EnemyVoiceLines { get; set; } = true;
    /// <summary>Show the line's text in the game's overhead speech bubble.</summary>
    public bool EnemyVoiceBubbles { get; set; } = true;
    /// <summary>Seconds of quiet after a voiced line before an ordinary event may start another.
    /// Engage, kills, victory and deaths wait only half a second.</summary>
    public float EnemyVoiceLineGap { get; set; } = 3.5f;
    /// <summary>One-time switch-on for configs saved while voices were off-by-default.</summary>
    public bool EnemyVoiceDefaultsMigrated20261024 { get; set; } = false;
    /// <summary>0..1 volume handed to the game's sound player (before the game's own sliders).</summary>
    public float EnemyVoiceVolume { get; set; } = 1.0f;
    /// <summary>Play at the speaker's position (3D) instead of flat in the listener's head.</summary>
    public bool EnemyVoicePositional { get; set; } = true;
    /// <summary>SoundVolumeCategory passed to PlaySound (0 Player, 1 Party, 2 Other, 6 bypass).
    /// Decides which of the game's volume sliders scales the voices.</summary>
    public int EnemyVoiceVolumeCategory { get; set; } = 2;
    public int EnemyVoiceClosestSpeakers { get; set; } = 4;
    public int EnemyVoiceMaxConcurrent { get; set; } = 3;
    public float EnemyVoicesPerSecond { get; set; } = 1.5f;
    /// <summary>Humanoid field enemies normally use the generic NPC battle voices; this uses the
    /// race/gender-matched player shouts instead (gender-correct, but sounds like an adventurer).</summary>
    public bool EnemyVoiceHumanoidUseAppearance { get; set; } = false;
    public bool EnemyVoiceIdleTaunts { get; set; } = true;
    /// <summary>Average seconds between idle taunts of one engaged enemy.</summary>
    public float EnemyVoiceTauntInterval { get; set; } = 14f;
    // One-time retune of the mid-fight cadence (taunt interval 25s → 14s) for configs that still hold
    // the old default; a value the user changed is left alone.
    public bool EnemyVoiceCadenceMigrated20261025 { get; set; } = false;
    public float EnemyVoiceAllyDeathRadius { get; set; } = 15f;
    /// <summary>A killing blow on the player or a companion always gets a line: it waits for the
    /// speaker and the line gate, and a killer that cannot speak hands it to the nearest enemy that can.</summary>
    public bool EnemyVoiceGuaranteeKills { get; set; } = true;
    /// <summary>After the party is wiped, surviving enemies keep taunting the fallen player.</summary>
    public bool EnemyVoiceDefeatTaunts { get; set; } = false;
    /// <summary>Seconds between taunts over the fallen player.</summary>
    public float EnemyVoiceDefeatTauntInterval { get; set; } = 8f;
    public bool EnemyVoiceDefeatTauntKill { get; set; } = true;
    public bool EnemyVoiceDefeatTauntTaunt { get; set; } = true;
    public bool EnemyVoiceDefeatTauntVictory { get; set; } = true;
    /// <summary>Owner-recorded entry labels: label key ("pc", "npc", "mon/{sepack}") -> label -> entries.
    /// Overrides Resources/VoiceEntryLabels.json per key+label.</summary>
    public Dictionary<string, Dictionary<string, List<int>>> EnemyVoiceEntryLabels { get; set; } = new();
}
