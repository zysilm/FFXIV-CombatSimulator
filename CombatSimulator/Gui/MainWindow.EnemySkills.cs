// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

// Enemy skills (what enemies use, how it is telegraphed, how it resolves). Drawn inside the map
// enemies section right after the humanoid kit size. Every switch defaults off.
public partial class MainWindow
{
    private void DrawEnemySkillsSection()
    {
        ImGui.TextDisabled("Enemy skills (apply to enemies added after a change)");

        EnemySkillToggle("Monster real skills", () => config.EnemyMonsterRealSkills, v => config.EnemyMonsterRealSkills = v,
            "Monsters use their own species' real skills (area, cast, animation) instead of auto-attack only.");
        using (ImRaii.Disabled(!config.EnemyMonsterRealSkills))
        {
            var maxMonster = config.EnemyMaxMonsterSkills;
            ImGui.SetNextItemWidth(180f);
            if (ImGui.SliderInt("Monster skills", ref maxMonster, 1, 6))
            {
                config.EnemyMaxMonsterSkills = Math.Clamp(maxMonster, 1, 6);
                config.Save();
            }
            HelpMarker("Most real skills kept per monster. Area skills are preferred, plus one single-target attack.");
        }

        EnemySkillToggle("Real-shape telegraphs", () => config.EnemyTelegraphRealShapes, v => config.EnemyTelegraphRealShapes = v,
            "Enemy areas are drawn in their real shape (cone, line, donut, circle) and hit whoever stands inside when they land.");
        using (ImRaii.Disabled(!config.EnemyTelegraphRealShapes))
        {
            ImGui.Indent();
            EnemySkillToggle("Native omen markers", () => config.EnemyNativeOmenVfx, v => config.EnemyNativeOmenVfx = v,
                "Use the game's own ground marker for the area. Falls back to the drawn outline when the marker can't be shown.");
            EnemySkillToggle("Outline over omen", () => config.EnemyAreaOverlayWithOmen, v => config.EnemyAreaOverlayWithOmen = v,
                "Also draw the outline on top of the native marker (to check the two match).");
            ImGui.Unindent();
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
