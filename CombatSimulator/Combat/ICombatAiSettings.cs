// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Combat;

/// <summary>
/// Every setting the enemy and ally AI read (docs/PLUGIN_ARCHITECTURE.md §7). Read-only; implemented by
/// <c>Configuration</c> with its own properties, so the stored JSON does not change. Offline
/// tests give a small fake instead of a Configuration (which needs Dalamud).
/// </summary>
public interface ICombatAiSettings : ICombatRosterSettings
{
    int CombatCompanionMaxCount { get; }
    bool SensePartyMembers { get; }
    bool EnableMapPlayerEnemySensing { get; }
    bool PartyCompanionDeathRagdoll { get; }
    float PartyCommandRange { get; }
    float PartyCommandRangeRandomness { get; }
    bool DevCompanionAppearanceVariant { get; }
    bool EnableNpcAutoEngage { get; }
    float NpcAutoEngageDelay { get; }
    bool EnableNpcTargetPlayer { get; }
    float ActionEnemyAttackSpeed { get; }
    float NpcSkillRateLimit { get; }
    bool EnemyTelegraphRealShapes { get; }
    bool EnableNpcScale { get; }
    float NpcScale { get; }
    float NpcAttackRangeScale { get; }
    bool EnhancedEnemyTactics { get; }
    bool TacticsRespectEngage { get; }
    bool TacticsCharge { get; }
    int EnemyTacticsMeleeAttackers { get; }
    bool NpcCollisionActive { get; }
    float DefaultNpcHeightOffset { get; }
    /// <summary>The emote a captive holds (the companions' captive loop).</summary>
    uint WarCaptiveEmoteId { get; }
}

/// <summary>
/// What the roster (<c>CombatRoster</c>) reads: whether the allies take part at all. Target selection itself
/// reads no setting (its weights are code constants, <see cref="AggroModel"/>).
/// </summary>
public interface ICombatRosterSettings
{
    bool EnableCombatCompanions { get; }
}
