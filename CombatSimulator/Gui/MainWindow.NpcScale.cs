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
        HelpMarker("Set the final visual scale of active target NPCs. This is an absolute scale, so 0.99 also normalizes enemies whose authored variant scale is 2x or 3x. A value of 1.00 means exactly 1.00; disable the effect to restore authored sizes. Deselected or despawned actors are restored automatically.");

        using (ImRaii.Disabled(!config.EnableNpcScale))
        {
            var scale = Math.Clamp(config.NpcScale, 0.01f, 3.0f);
            ImGui.SetNextItemWidth(180f);
            if (ImGui.SliderFloat("Visual scale##npcScale", ref scale, 0.01f, 3.0f, "%.2f"))
            {
                config.NpcScale = Math.Clamp(MathF.Round(scale * 100f) / 100f, 0.01f, 3.0f);
                config.Save();
            }
            HelpMarker("Absolute final DrawObject scale. 0.99 gives every active target NPC a 0.99 visual scale instead of multiplying its authored variant size.");
        }

        if (ImGui.Button("Reset Defaults##npcScale"))
        {
            config.EnableNpcScale = true;
            config.NpcScale = 0.99f;
            config.Save();
        }
        HelpMarker("Restore the default: enabled with a final visual scale of 0.99.");
    }
}
