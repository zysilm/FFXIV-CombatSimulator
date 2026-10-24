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
            TacticsCheckbox("Body-size aware spacing", () => config.EnemyTacticsHitboxSpacing, v => config.EnemyTacticsHitboxSpacing = v,
                "Ranges count from the edge of big bodies, and melee slots no longer overlap. Humanoid pairs are unchanged.");
            TacticsCheckbox("Circle while holding range", () => config.EnemyTacticsStrafe, v => config.EnemyTacticsStrafe = v,
                "Every few seconds an enemy in range may walk a short arc around its target. Never mid-cast or mid-swing.");
            TacticsCheckbox("Threat-based aggro", () => config.EnemyTacticsThreat, v => config.EnemyTacticsThreat = v,
                "Enemies target whoever built the most threat on them (damage, tanks count triple) instead of the top DPS. Needs companions.");
            TacticsCheckbox("Take turns in melee", () => config.EnemyTacticsStagger, v => config.EnemyTacticsStagger = v,
                "At most three melee enemies fight one target; extras wait on an outer ring and step in when a place frees.");
            TacticsCheckbox("Call for help / flee at low HP", () => config.EnemyTacticsFlee, v => config.EnemyTacticsFlee = v,
                "An enemy joining the fight wakes idle allies nearby. Below 20% HP some enemies run to an ally once, then return.");
            TacticsCheckbox("Reposition after attacks", () => config.EnemyTacticsReposition, v => config.EnemyTacticsReposition = v,
                "Ranged/caster enemies sometimes shift firing spot after an attack; enemies standing inside another body step out.");
            TacticsCheckbox("Big monsters turn slowly", () => config.EnemyTacticsHeavyTurn, v => config.EnemyTacticsHeavyTurn = v,
                "Enemies larger than a humanoid turn more slowly, so getting to their flank or back matters.");
            TacticsCheckbox("Charge from far away", () => config.EnemyTacticsCharge, v => config.EnemyTacticsCharge = v,
                "An engaged enemy more than 15y from its target runs at 1.8x speed until it is within 8y.");
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
