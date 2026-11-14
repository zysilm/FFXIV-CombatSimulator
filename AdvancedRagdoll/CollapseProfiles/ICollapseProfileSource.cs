// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace AdvancedRagdoll.CollapseProfiles;

/// <summary>The guided-collapse profiles a host offers (the plugin's are embedded data files it loads itself).</summary>
public interface ICollapseProfileSource
{
    /// <summary>The profile with this id (ignoring case), or null.</summary>
    CollapseProfile? FindById(string id);
}
