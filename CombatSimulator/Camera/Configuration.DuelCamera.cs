// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>Duel camera settings (docs/DUEL_CAMERA.md): the mode itself and its one framing option.</summary>
public partial class Configuration
{
    /// <summary>Camera mode "Duel": a two-shot of the player and one opponent while a simulation runs. Exclusive with
    /// Follow bone (<see cref="EnableActiveCamera"/>) and Directed (<see cref="EnableDynamicCamera"/>); the Camera
    /// tab's mode dropdown keeps at most one of the three on.</summary>
    public bool EnableDuelCamera { get; set; } = false;

    /// <summary>How close the two-shot sits.</summary>
    public Camera.Duel.DuelFraming DuelCameraFraming { get; set; } = Camera.Duel.DuelFraming.Standard;
}
