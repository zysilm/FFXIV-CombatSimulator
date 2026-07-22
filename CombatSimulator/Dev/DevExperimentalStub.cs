using CombatSimulator.Camera;
using CombatSimulator.Gui;
using CombatSimulator.Fighting;
using CombatSimulator.Simulation;

namespace CombatSimulator.Dev;

/// <summary>
/// No-op <see cref="IDevExperimental"/> used when the private experimental module is not compiled in
/// (public build without the submodule). All dev-experimental features — including the menu entry /
/// easter-egg unlock — are simply absent, and the plugin still compiles and runs normally.
/// </summary>
public sealed class DevExperimentalStub : IDevExperimental
{
    public IVictorySequence? VictorySequence => null;
    public bool ControlsNpc(nint address) => false;
    public bool SuppressEnemyInitiation => false;
    public void OnPlayerAttackLanded() { }
    public void TickWorld(float deltaTime) { }
    public void Tick(float deltaTime) { }
    public void BeforePlayerDeath() { }
    public void OnPlayerDeath(nint playerAddress) { }
    public void SetFightingModeLane(IFightingModeLaneConstraint? lane) { }
    public void SetCameraCoordinator(CameraModeCoordinator coordinator) { }
    public System.Numerics.Vector3? ControlledMonsterCenter => null;
    public void ResetWorldState() { }
    public void ResetTransientState() { }
    public void DrawToolbars(MainWindow mainWindow) { }
    public void RestoreOcclusion() { }
    public void Dispose() { }
}
