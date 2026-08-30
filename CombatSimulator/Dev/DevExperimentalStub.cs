// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Gui;
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
    public bool NpcAutoAttackOnly => false;
    public bool VirtualEnemyStripBodyLegs => false;
    public bool VirtualEnemyStripAccessories => false;
    public bool SuppressEnemyInitiation => false;
    public void OnPlayerAttackLanded() { }
    public void TickWorld(float deltaTime) { }
    public void Tick(float deltaTime) { }
    public void BeforePlayerDeath() { }
    public void OnPlayerDeath(nint playerAddress) { }
    public void OnNpcDeath(nint npcAddress) { }
    public void ResetWorldState() { }
    public void ResetTransientState() { }
    public void DrawToolbars(MainWindow mainWindow) { }
    public void RestoreOcclusion() { }
    public void Dispose() { }
}
