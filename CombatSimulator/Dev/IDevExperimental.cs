// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Gui;
using CombatSimulator.Simulation;

namespace CombatSimulator.Dev;

/// <summary>
/// Public seam for the experimental dev features. The plugin depends only on this interface, so the
/// concrete implementation can live in a separate (private) module that need not be present to
/// compile or run the plugin — a no-op <see cref="DevExperimentalStub"/> stands in when it is absent.
/// </summary>
public interface IDevExperimental : IDisposable
{
    /// <summary>Cinematic victory sequence seam for the engine (null when unavailable).</summary>
    IVictorySequence? VictorySequence { get; }

    /// <summary>True while a dev controller is driving this NPC (suppresses its AI).</summary>
    bool ControlsNpc(nint address);

    /// <summary>True while the dev enemy-pack gate is waiting for a real player hit.</summary>
    bool SuppressEnemyInitiation { get; }

    /// <summary>Release the dev enemy-pack gate after confirmed player damage lands.</summary>
    void OnPlayerAttackLanded();

    /// <summary>World-level dev update that also runs while combat simulation is inactive.</summary>
    void TickWorld(float deltaTime);
    void Tick(float deltaTime);
    void BeforePlayerDeath();
    void OnPlayerDeath(nint playerAddress);

    /// <summary>An NPC has just died. Raised before the ragdoll gates, so it still fires when
    /// death ragdolls are switched off.</summary>
    void OnNpcDeath(nint npcAddress);

    /// <summary>Clear world-bound actors on territory change or logout.</summary>
    void ResetWorldState();
    void ResetTransientState();
    void DrawToolbars(MainWindow mainWindow);
    void RestoreOcclusion();
}
