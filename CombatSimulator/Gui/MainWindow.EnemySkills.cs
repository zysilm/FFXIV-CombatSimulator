// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

// Enemy skills (what enemies use, how it is telegraphed, how it resolves). Drawn inside the map
// enemies section right after the humanoid kit size.
public partial class MainWindow
{
    private void DrawEnemySkillsSection()
    {
        // Collapsed by default: the map enemies header is already long, and these are set-and-forget.
        if (!ImGui.TreeNode("Enemy skills##enemySkillsSection"))
            return;
        try
        {
            DrawEnemySkillsContent();
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    private static readonly string[] EnemyAreaMarkerLabels = { "Game omens", "Drawn outlines", "Classic circles" };

    private void DrawEnemySkillsContent()
    {
        ImGui.TextDisabled("Apply to enemies added after a change");

        var maxMonster = config.EnemyMonsterRealSkills ? Math.Clamp(config.EnemyMaxMonsterSkills, 1, 6) : 0;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Monster skills", ref maxMonster, 0, 6))
        {
            config.EnemyMonsterRealSkills = maxMonster > 0;
            if (maxMonster > 0) config.EnemyMaxMonsterSkills = maxMonster;
            config.Save();
        }
        HelpMarker("Most real skills kept per monster (their own species' skills: area, cast, animation).\n" +
                   "Area skills are preferred, plus one single-target attack. 0 = monsters auto-attack only.");

        var markers = !config.EnemyTelegraphRealShapes ? 2 : config.EnemyNativeOmenVfx ? 0 : 1;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Area markers##enemySkills", ref markers, EnemyAreaMarkerLabels, EnemyAreaMarkerLabels.Length))
        {
            config.EnemyTelegraphRealShapes = markers != 2;
            config.EnemyNativeOmenVfx = markers == 0;
            config.Save();
        }
        HelpMarker("Game omens: areas take their real shape (cone, line, donut, circle) and use the game's own ground marker.\n" +
                   "Drawn outlines: real shapes, drawn by the plugin.\n" +
                   "Classic circles: every attack uses a simple circle. Real cast times only apply to real shapes.");

        if (markers == 0)
        {
            EnemySkillToggle("Outline over omen", () => config.EnemyAreaOverlayWithOmen, v => config.EnemyAreaOverlayWithOmen = v,
                "Also draw the outline on top of the native marker.");
        }
    }

    private void EnemySkillToggle(string label, Func<bool> get, Action<bool> set, string tooltip)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
        HelpMarker(tooltip);
    }
}
