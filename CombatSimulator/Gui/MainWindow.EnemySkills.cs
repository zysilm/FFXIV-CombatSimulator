// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

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

    private void DrawEnemySkillsContent()
    {
        ImGui.TextDisabled("Apply to enemies added after a change");

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

        EnemySkillToggle("Humanoid AoE skill", () => config.EnemyKitIncludeAoe, v => config.EnemyKitIncludeAoe = v,
            "Humanoid kits always include one area skill when the job has one, instead of only the strongest single-target skills.");

        EnemySkillToggle("HP phase skills", () => config.EnemyHpPhaseSkills, v => config.EnemyHpPhaseSkills = v,
            "The longest cast is held until the enemy is at half HP; a stronger 'Enraged' area skill unlocks below 30%.");

        EnemySkillToggle("Real-shape telegraphs", () => config.EnemyTelegraphRealShapes, v => config.EnemyTelegraphRealShapes = v,
            "Enemy areas are drawn in their real shape (cone, line, donut, circle) and hit whoever stands inside when they land.");
        using (ImRaii.Disabled(!config.EnemyTelegraphRealShapes))
        {
            ImGui.Indent();
            EnemySkillToggle("Native omen markers", () => config.EnemyNativeOmenVfx, v => config.EnemyNativeOmenVfx = v,
                "Use the game's own ground marker for the area. Falls back to the drawn outline when the marker can't be shown.");
            EnemySkillToggle("Outline over omen", () => config.EnemyAreaOverlayWithOmen, v => config.EnemyAreaOverlayWithOmen = v,
                "Also draw the outline on top of the native marker (to check the two match).");
            EnemySkillToggle("Telegraphed casts", () => config.EnemyTelegraphedCasts, v => config.EnemyTelegraphedCasts = v,
                "Area skills keep their real cast time: cast bar and area show during the cast, then it lands. Interject/Head Graze or a stun cancels it. Skills without an area stay instant.");
            using (ImRaii.Disabled(!config.EnemyTelegraphedCasts))
            {
                var maxCast = config.EnemyMaxCastSeconds;
                ImGui.SetNextItemWidth(180f);
                if (ImGui.SliderFloat("Longest cast", ref maxCast, 0.5f, 6f, "%.1fs"))
                {
                    config.EnemyMaxCastSeconds = Math.Clamp(maxCast, 0.5f, 6f);
                    config.Save();
                }
                HelpMarker("Real casts longer than this are shortened to it.");
            }
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
