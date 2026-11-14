// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Combat;
using CombatSimulator.Npcs;

namespace CombatSimulator.Companions;

/// <summary>An ally. What it shares with an enemy is on <see cref="Combatant"/>.</summary>
public unsafe class CombatCompanion : Combatant
{
    public override CombatSide Side => CombatSide.Ally;

    public uint SourceEntityId { get; set; }
    public CompanionAiState AiState { get; set; }
    public uint CurrentTargetId { get; set; }
    // Simulation-time stamp at which this companion is next allowed to re-run the
    // (heavy) target-selection scoring. Reselection happens early if the current
    // target dies or is lost; see CombatCompanionManager.Tick.
    public float NextTargetSelectAt { get; set; }
    public float RecentDamage { get; set; }
    public float RecentDps { get; set; }
    public bool DeathAnimationPlayed { get; set; }
    public bool CaptiveEmotePlayed { get; set; }
    /// <summary>Guarding the lord's body: strayed past the leash and walking back.</summary>
    public bool ReturningToLord { get; set; }
    /// <summary>The loop the companion holds while captive (0: none to hold).</summary>
    public ushort CaptiveLoop { get; set; }
    public ulong[]? OriginalEquipment { get; set; }
    public bool EquipmentVariantApplied { get; set; }
    /// <summary>
    /// Spawned from the NPC catalog (any creature) rather than cloned from a player: it has no
    /// player equipment to capture, strip or glamour.
    /// </summary>
    public bool IsCatalogNpc { get; set; }
    /// <summary>A clone given a random hairstyle and face.</summary>
    public bool RandomizedLook { get; set; }
    /// <summary>The request it was spawned from, when it came from the NPC catalog.</summary>
    public NpcSpawnRequest? CatalogRequest { get; set; }
}
