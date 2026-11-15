// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Gui;

// The settings the combat overlays read, one small read-only view per overlay or group of overlays
// (docs/PLUGIN_ARCHITECTURE.md §7). Implemented by Configuration with its own properties, so the stored JSON
// does not change. (The main window reads and writes the settings and keeps the whole Configuration.)

/// <summary>The HP bars over the fighters and on the HUD (<see cref="HpBarOverlay"/>).</summary>
public interface IHpBarSettings
{
    bool AnonymousMode { get; }
    string CustomPlayerName { get; }
    string DeathNameAlias { get; }
    float EnemyHpBarYOffset { get; }
    bool HpBarOcclusion { get; }
    float PlayerHpBarXOffset { get; }
    float PlayerHpBarYOffset { get; }
    bool ShowAllBarLabels { get; }
    bool ShowEnemyHpBar { get; }
    bool ShowHudPlayerHpBar { get; }
    bool ShowPlayerHpBar { get; }
}

/// <summary>The damage numbers (<see cref="DamageNumberOverlay"/>).</summary>
public interface IDamageNumberSettings
{
    bool ShowDamageNumbers { get; }
}

/// <summary>The arcs between the fighters and their targets, and the lock marker (<see cref="CombatLinkOverlay"/>).</summary>
public interface ICombatLinkSettings
{
    float CombatLinkAlpha { get; }
    float CombatLinkFlowSpeed { get; }
    float CombatLinkHeightOffset { get; }
    float CombatLinkThickness { get; }
    bool ShowCombatLinkArcs { get; }
    bool ShowLockMarker { get; }
}

/// <summary>Action combat's own overlays: the parry circle (<see cref="OsuParryOverlay"/>) and the reticle (<see cref="ReticleOverlay"/>).</summary>
public interface IActionOverlaySettings
{
    bool ActionMode { get; }
    float GuardActiveWindow { get; }
    float OsuAnchorHeight { get; }
    bool OsuCircleEnabled { get; }
    float OsuInnerRadius { get; }
    float OsuOuterStartScale { get; }
}

/// <summary>The dynamic camera's framing (<c>DynamicCameraDebugOverlay</c>).</summary>
public interface IDynamicCameraDebugSettings
{
    bool EnableDynamicCamera { get; }
    bool DynCamDebugOverlay { get; }
    float DynCamDeathAngle { get; }
    float DynCamDeathBodyBand { get; }
    float DynCamDeathBodyVisibility { get; }
    float DynCamDeathCloseUpDistance { get; }
    bool DynCamDeathMaximizeBody { get; }
    float DynCamDeathTranslateDuration { get; }
    float DynCamShoulderScreenFrac { get; }
    float DynCamSubjectScreenShare { get; }
}

/// <summary>The ragdoll's bodies (<c>Gui.RagdollDebugOverlay</c>).</summary>
public interface IRagdollDebugSettings
{
    bool RagdollDebugOverlay { get; }
}
