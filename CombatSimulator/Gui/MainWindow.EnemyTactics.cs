// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    // Combat tab > "Enhanced enemy tactics". Every switch here defaults to the pre-existing behaviour
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
        HelpMarker("Enemies circle and reposition while holding range, pick targets by threat, take turns in melee,\n" +
                   "call for help or flee at low HP, and big monsters turn slowly.\n" +
                   "Off = enemies walk straight into reach and fight.");

        using (ImRaii.Disabled(!config.EnhancedEnemyTactics))
        {
            TacticsCheckbox("Idle until engaged", () => config.EnemyTacticsRespectEngage, v => config.EnemyTacticsRespectEngage = v,
                "Party mode stops force-engaging every enemy. Enemies wait until hit, auto-engaged, or called by an ally.");
            TacticsCheckbox("Charge from far away", () => config.EnemyTacticsCharge, v => config.EnemyTacticsCharge = v,
                "An engaged enemy more than 15y from its target runs at 1.8x speed until it is within 8y.");
            var attackers = Math.Clamp(config.EnemyTacticsMeleeAttackers, 3, 20);
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Melee attackers at once", ref attackers, 3, 20))
            {
                config.EnemyTacticsMeleeAttackers = Math.Clamp(attackers, 3, 20);
                config.Save();
            }
            HelpMarker("How many melee enemies may be on one target at the same time; the rest wait on an outer ring.\nRanged and casting enemies are not counted.");
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
