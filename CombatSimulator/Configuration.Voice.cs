// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Master switch for enemy battle voices. Off by default: with it off no native
    /// sound function is ever called and the director does no per-frame work.</summary>
    public bool EnableEnemyVoices { get; set; } = false;
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
    public float EnemyVoiceTauntInterval { get; set; } = 25f;
    public float EnemyVoiceAllyDeathRadius { get; set; } = 15f;
    /// <summary>Owner-recorded entry labels: label key ("pc", "npc", "mon/{sepack}") -> label -> entries.
    /// Overrides Resources/VoiceEntryLabels.json per key+label.</summary>
    public Dictionary<string, Dictionary<string, List<int>>> EnemyVoiceEntryLabels { get; set; } = new();
}
