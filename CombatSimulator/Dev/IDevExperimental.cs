using System;
using CombatSimulator.Camera;
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

    /// <summary>True while the dev enemy-pack gate is waiting for a real player hit.</summary>
    bool SuppressEnemyInitiation { get; }

    /// <summary>Release the dev enemy-pack gate after confirmed player damage lands.</summary>
    void OnPlayerAttackLanded();

    /// <summary>World-level dev update that also runs while combat simulation is inactive.</summary>
    void TickWorld(float deltaTime);
    void Tick(float deltaTime);
    void BeforePlayerDeath();
    void OnPlayerDeath(nint playerAddress);
    void SetFightingModeLane(IFightingModeLaneConstraint? lane);

    /// <summary>Camera arbitration seam: dev camera modes (monster follow) submit
    /// requests to the coordinator instead of toggling the active camera directly.</summary>
    void SetCameraCoordinator(CameraModeCoordinator coordinator);

    /// <summary>Body-center of the dev-controlled creature while it is active AND its
    /// camera-follow preference is on; null otherwise. Fighting Mode's KO camera frames
    /// the midpoint of this and the player's corpse.</summary>
    System.Numerics.Vector3? ControlledMonsterCenter { get; }
    /// <summary>Clear world-bound actors on territory change or logout.</summary>
    void ResetWorldState();
    void ResetTransientState();
    void DrawToolbars(MainWindow mainWindow);
    void RestoreOcclusion();
}
