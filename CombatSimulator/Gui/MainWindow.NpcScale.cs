// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    private void DrawNpcScaleSection()
    {
        if (!ImGui.CollapsingHeader("NPC Scale"))
            return;

        var enabled = config.EnableNpcScale;
        if (ImGui.Checkbox("Enable NPC scale", ref enabled))
        {
            config.EnableNpcScale = enabled;
            config.Save();
        }
        HelpMarker("Scale active target NPCs relative to each one's original authored size. Disabled, reset, deselected, or despawned actors are restored automatically.");

        using (ImRaii.Disabled(!config.EnableNpcScale))
        {
            var scale = Math.Clamp(config.NpcScale, 0.01f, 3.0f);
            ImGui.SetNextItemWidth(180f);
            if (ImGui.SliderFloat("Scale factor##npcScale", ref scale, 0.01f, 3.0f, "%.2f"))
            {
                config.NpcScale = Math.Clamp(MathF.Round(scale * 100f) / 100f, 0.01f, 3.0f);
                config.Save();
            }
            HelpMarker("Relative multiplier applied to every active target NPC. 0.99 means 99% of its own normal size; values above 1 enlarge it. Below 0.40, the model keeps shrinking visually but melee navigation/reach stays at a 0.40 safety floor so enemies can still approach and attack reliably.");
        }

        if (ImGui.Button("Reset Defaults##npcScale"))
        {
            config.EnableNpcScale = true;
            config.NpcScale = 0.99f;
            config.Save();
        }
        HelpMarker("Restore the default: enabled at 0.99x.");
    }
}
