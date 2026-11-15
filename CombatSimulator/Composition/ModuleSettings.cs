// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.ActionCombat;
using CombatSimulator.Battle;
using CombatSimulator.Combat;
using CombatSimulator.Gui;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;

namespace CombatSimulator.Composition;

// What each feature module reads from the settings (docs/PLUGIN_ARCHITECTURE.md §7): the settings of the
// systems it builds that take a settings view, and the few it reads itself. Read-only; implemented by
// Configuration with its own properties, so the stored JSON does not change.
//
// A module does not see the whole Configuration through these. What still takes the whole Configuration is
// handed PluginModules.Config explicitly (m.Config): the systems outside the combat path that read many settings
// or write them (animation, the ragdoll and its parts, the cameras, Fighting Mode and Enemy Control, the roster's
// selector and spawner, spectators and map enemies, the main window, voices, the update-log popup), and the few
// module steps that write a setting (the directed camera switch, the voices' one-time default, an adopted ally
// turning the party features on).

/// <summary>The actors module: the allies' controller (and through it the roster) and the allies' Glamourer design.</summary>
internal interface IActorsModuleSettings : ICombatAiSettings
{
    string PartyCompanionGlamourerDesignId { get; }
}

/// <summary>The corpse module's own decisions: which bodies get a ragdoll, when, what falls off them.</summary>
internal interface ICorpseModuleSettings
{
    bool EnableRagdoll { get; }
    bool EnableNpcDeathRagdoll { get; }
    bool EnableCombatCompanions { get; }
    bool PartyCompanionDeathRagdoll { get; }
    bool EnableEnemyDismemberment { get; }
    int EnemyHumanoidDismembermentCount { get; }
    float EnemyMonsterDismembermentBonePercent { get; }
    float RagdollActivationDelay { get; }
    int RagdollTriggerMode { get; }
    bool KoStripAllies { get; }
    bool KoStripEnemies { get; }
    bool KoStripOnHitEnabled { get; }
    bool NpcCollisionActive { get; }
}

/// <summary>The combat module: the engine, action combat, the enemies' AI, the look-at, the player's target lock and UseAction hook.</summary>
internal interface ICombatModuleSettings :
    ICombatEngineSettings,
    IActionComboSettings,
    IActionInputSettings,
    IPlayerHitboxSettings,
    IPlayerGuardSettings,
    IEnemyAttackSettings,
    IHitFeedbackSettings,
    ICombatAiSettings,
    IVirtualEnemyLookAtSettings,
    IPlayerInputSettings
{
    /// <summary>The damage calculator's action-combat switch (the same setting every base view has).</summary>
    new bool ActionMode { get; }
}

/// <summary>The battle module: the battle's end (the celebration, the result banner, the body ready to be held).</summary>
internal interface IBattleModuleSettings : IBattleRuleSettings
{
    bool EnableVictoryCelebration { get; }
    float VictoryCelebrationStagger { get; }
    /// <summary>Enemy Control's "On death: Control killer".</summary>
    bool EnemyControlControlKiller { get; }
}

/// <summary>The presentation module: the overlays it builds and which windows, toolbars and overlays it draws.</summary>
internal interface IPresentationModuleSettings :
    IHpBarSettings,
    IDamageNumberSettings,
    ICombatLinkSettings,
    IEnemyAreaDisplaySettings,
    IActionOverlaySettings,
    IFightingDebugSettings,
    IDynamicCameraDebugSettings,
    IRagdollDebugSettings
{
    bool ShowShortcuts { get; }
    bool ShowFastCombatToolbar { get; }
    bool ShowDynamicCamToolbar { get; }
    bool ShowActiveCamToolbar { get; }
    bool ShowEnemyControlGui { get; }
    bool ShowMainWindow { get; }
    bool ShowProfessionalWindow { get; }
    bool ShowCombatLog { get; }
    bool EnableCombatCompanions { get; }
}
