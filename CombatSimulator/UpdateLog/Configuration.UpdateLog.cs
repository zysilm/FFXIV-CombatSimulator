// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

public partial class Configuration
{
    public string LastSeenUpdateLogVersion { get; set; } = "";
    public bool ShowUpdateLogOnUpdate { get; set; } = true;
}
