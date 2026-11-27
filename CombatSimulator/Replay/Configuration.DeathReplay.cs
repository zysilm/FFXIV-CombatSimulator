// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator;

/// <summary>Death replay settings (docs/DEATH_REPLAY.md): Effects tab → "Death Replay".</summary>
public partial class Configuration
{
    /// <summary>Record the last 20 s while a simulation runs and replay the end of it once the player's body has come
    /// to rest after a death. Off: nothing is recorded (a flag check per frame).</summary>
    public bool DeathReplayEnabled { get; set; } = false;

    /// <summary>"Replay length": how many seconds before the body came to rest are replayed (3–20).</summary>
    public float DeathReplaySeconds { get; set; } = 8f;

    /// <summary>"Loop": the replay repeats (the shots keep cycling) until the player is revived, the simulation is
    /// reset or stopped, or Esc / "Stop replay". Off: one pass, then everything is back as it was.</summary>
    public bool DeathReplayLoop { get; set; } = false;

    /// <summary>The replay's camera shots, in order (at most 100), relative to the body at death. Empty: the replay
    /// keeps the camera view it started with.</summary>
    public List<Replay.Model.DeathReplayShot> DeathReplayShots { get; set; } = new();
}
