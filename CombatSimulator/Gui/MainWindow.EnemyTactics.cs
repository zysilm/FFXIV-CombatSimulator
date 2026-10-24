// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    // Party tab > "Enhanced enemy tactics". Every switch here defaults to the pre-existing behaviour
    // while the master is off.
    private void DrawEnemyTacticsSection()
    {
        if (!ImGui.CollapsingHeader("Enhanced enemy tactics"))
            return;

        var master = config.EnhancedEnemyTactics;
        if (ImGui.Checkbox("Enable enhanced enemy tactics", ref master))
        {
            config.EnhancedEnemyTactics = master;
            config.Save();
        }
        HelpMarker("Master switch for the behaviours below. Off = enemies move exactly as before.");

        using (ImRaii.Disabled(!config.EnhancedEnemyTactics))
        {
            TacticsCheckbox("Idle until engaged", () => config.EnemyTacticsRespectEngage, v => config.EnemyTacticsRespectEngage = v,
                "Party mode stops force-engaging every enemy. Enemies wait until hit, auto-engaged, or called by an ally.");
            TacticsCheckbox("Spread enemies around the player", () => config.EnemyTacticsPlayerSlots, v => config.EnemyTacticsPlayerSlots = v,
                "Enemies attacking you take separate slots around you (leaning to your flanks and back) instead of stacking in front.");
        }
    }

    private void TacticsCheckbox(string label, Func<bool> get, Action<bool> set, string help)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
        HelpMarker(help);
    }
}
