// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>Post-fight celebration (see <see cref="Animation.VictoryCelebrationDirector"/>).
/// Everything defaults off so an existing install behaves exactly as before.</summary>
public partial class Configuration
{
    public bool EnableVictoryCelebration { get; set; }
    public bool VictoryCelebrationCompanions { get; set; }
    // Performers start together (the saved tuning was 0 s).
    [Newtonsoft.Json.JsonIgnore] public float VictoryCelebrationStagger => 0f;
}
