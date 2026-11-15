// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>Cutscene follow shots (docs/CUTSCENE_SHOTS.md): the shots chosen for victory and death, by their id in
/// Resources/CutsceneShots.json. Empty = off.</summary>
public partial class Configuration
{
    public string CutsceneVictoryShot { get; set; } = "";
    public string CutsceneDeathShot { get; set; } = "";
}
