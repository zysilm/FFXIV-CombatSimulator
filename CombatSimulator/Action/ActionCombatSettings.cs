// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Dalamud.Game.ClientState.GamePad;

namespace CombatSimulator.ActionCombat;

// The settings action combat reads, one small read-only view per function (docs/PLUGIN_ARCHITECTURE.md §7).
// Implemented by Configuration with its own properties, so the stored JSON does not change.

/// <summary>What the player's skill and guard buttons map to (<see cref="ActionComboSink"/>).</summary>
public interface IActionComboSettings
{
    bool ActionMode { get; }
    uint ActionGuardId { get; }
    uint ActionSkill1Id { get; }
    uint ActionSkill2Id { get; }
}

/// <summary>The player's own input in action combat: the keys, the light attack and its swing (<see cref="ActionModeController"/>).</summary>
public interface IActionInputSettings
{
    bool ActionMode { get; }
    GamepadButtons ActionBasicAttackGamepadButton { get; }
    int ActionBasicAttackKey { get; }
    GamepadButtons ActionGuardGamepadButton { get; }
    int ActionGuardKey { get; }
    int LightAttackPotency { get; }
    float LightComboWindow { get; }
    float LightSwingInterval { get; }
    float PlayerHitboxAngleDeg { get; }
    float PlayerHitboxRange { get; }
    float RangedBasicRange { get; }
    float RangedSelectAngleDeg { get; }
}

/// <summary>Who the player's swing reaches (<see cref="PlayerHitboxResolver"/>).</summary>
public interface IPlayerHitboxSettings
{
    float MeleeTargetHitboxReach { get; }
    float PlayerHitboxAngleDeg { get; }
    float PlayerHitboxRange { get; }
    float RangedBasicRange { get; }
    float RangedSelectAngleDeg { get; }
}

/// <summary>The player's guard and its chain (<see cref="PlayerGuardController"/>).</summary>
public interface IPlayerGuardSettings
{
    float ChainGuardWindow { get; }
    float GuardActiveWindow { get; }
    float GuardCooldown { get; }
    int GuardMaxChain { get; }
    float GuardRecovery { get; }
}

/// <summary>
/// The enemies' attacks from commit to landing: which executor runs them (<see cref="CombatModeRouter"/>), the
/// instant one (<see cref="InstantAttackExecutor"/>), the telegraphed one's windup
/// (<see cref="TelegraphedAttackExecutor"/>) and the telegraphs (<see cref="TelegraphSystem"/>).
/// </summary>
public interface IEnemyAttackSettings
{
    bool ActionMode { get; }
    float ActionWindupSeconds { get; }
    bool ActionEnemyWindupSwing { get; }
    bool EnemyMonsterRealSkills { get; }
    bool EnemyTelegraphRealShapes { get; }
    float GuardLateTolerance { get; }
}

/// <summary>Hitstop, camera shake and sparks on a hit (<see cref="HitFeedbackController"/>).</summary>
public interface IHitFeedbackSettings
{
    bool EnableHitFeedback { get; }
    bool EnableHitSparkVfx { get; }
    float HitCameraShake { get; }
    float HitCameraShakeDuration { get; }
    float HitFeedbackDelay { get; }
    float HitstopMs { get; }
}

/// <summary>
/// How the enemies' attack areas are shown: the game's own omen (<see cref="EnemyOmenVfx"/>) and the drawn areas
/// and telegraphs (<c>Gui.EnemyAreaOverlay</c>, <c>Gui.TelegraphOverlay</c>).
/// </summary>
public interface IEnemyAreaDisplaySettings
{
    bool EnemyNativeOmenVfx { get; }
    bool EnemyAreaOverlayWithOmen { get; }
    bool ShowTelegraphs { get; }
    float TelegraphAlpha { get; }
    float TelegraphThickness { get; }
}
