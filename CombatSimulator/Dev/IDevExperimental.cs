using System;
using CombatSimulator.Fighting;
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

    void Tick(float deltaTime);
    void BeforePlayerDeath();
    void OnPlayerDeath(nint playerAddress);
    void SetFightingModeLane(IFightingModeLaneConstraint? lane);
    void ResetTransientState();
    void DrawToolbars(MainWindow mainWindow);
    void RestoreOcclusion();
}
