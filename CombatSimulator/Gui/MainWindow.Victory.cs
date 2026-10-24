// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    private void DrawVictoryCelebrationSection()
    {
        if (!ImGui.CollapsingHeader("Victory Celebration"))
            return;

        var enabled = config.EnableVictoryCelebration;
        if (ImGui.Checkbox("Survivors celebrate", ref enabled))
        {
            config.EnableVictoryCelebration = enabled;
            config.Save();
            if (!enabled) animationController.Victory?.Stop();
        }
        HelpMarker("When your side is defeated, each living enemy plays a randomly chosen victory emote (humanoids) or a flourish from its own model family (creatures), with staggered start times. Ends on revive, reset or stop. Takes effect from the next fight result.");

        using (ImRaii.Disabled(!config.EnableVictoryCelebration))
        {
            var companions = config.VictoryCelebrationCompanions;
            if (ImGui.Checkbox("Companions celebrate your wins", ref companions))
            {
                config.VictoryCelebrationCompanions = companions;
                config.Save();
            }
            HelpMarker("When every enemy is down, living combat companions celebrate the same way.");
            SliderFloatSaved("Start stagger (s)##victoryStagger", () => config.VictoryCelebrationStagger,
                v => config.VictoryCelebrationStagger = v, 0f, Animation.VictoryCelebrationPlan.MaxStaggerSeconds,
                "Each performer starts after a random delay up to this long, so a group does not move in lockstep.");
            if (animationController.Victory is { IsActive: true } victory)
                ImGui.TextDisabled($"Celebrating: {victory.PerformerCount}");
        }
    }
}
