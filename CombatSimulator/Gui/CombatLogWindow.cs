using System;
using System.Numerics;
using CombatSimulator.Simulation;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public class CombatLogWindow : IDisposable
{
    private readonly CombatEngine combatEngine;
    private bool autoScroll = true;
    private int lastLogCount;

    public CombatLogWindow(CombatEngine combatEngine)
    {
        this.combatEngine = combatEngine;
    }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(450, 250), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Combat Log##CombatSim"))
        {
            ImGui.End();
            return;
        }

        // Summary bar
        if (combatEngine.State.CombatDuration > 0)
        {
            var state = combatEngine.State;
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1),
                $"Duration: {state.CombatDuration:F1}s | " +
                $"Damage: {state.TotalDamageDealt:N0} | " +
                $"DPS: {state.Dps:N0} | " +
                $"Taken: {state.TotalDamageTaken:N0}");
            ImGui.Separator();
        }

        // Log entries
        if (ImGui.BeginChild("LogEntries", new Vector2(0, -25)))
        {
            foreach (var entry in combatEngine.CombatLog)
            {
                var color = entry.Type switch
                {
                    CombatLogType.DamageDealt => new Vector4(1, 1, 1, 1),
                    CombatLogType.DamageTaken => new Vector4(1, 0.6f, 0.6f, 1),
                    CombatLogType.Healing => new Vector4(0.6f, 1, 0.6f, 1),
                    CombatLogType.Miss => new Vector4(0.6f, 0.6f, 0.6f, 1),
                    CombatLogType.Death => new Vector4(1, 0.3f, 0.3f, 1),
                    CombatLogType.Info => new Vector4(0.7f, 0.7f, 1, 1),
                    _ => new Vector4(1, 1, 1, 1),
                };

                var timestamp = TimeSpan.FromSeconds(entry.Timestamp);
                ImGui.TextColored(color, $"[{timestamp:mm\\:ss}] {entry.Message}");
            }

            // Auto-scroll
            if (combatEngine.CombatLog.Count != lastLogCount)
            {
                lastLogCount = combatEngine.CombatLog.Count;
                if (autoScroll)
                    ImGui.SetScrollHereY(1.0f);
            }
        }
        ImGui.EndChild();

        // Bottom bar
        ImGui.Checkbox("Auto-scroll", ref autoScroll);
        ImGui.SameLine();
        if (ImGui.Button("Clear Log"))
        {
            combatEngine.CombatLog.Clear();
            lastLogCount = 0;
        }

        ImGui.End();
    }

    public void Dispose()
    {
    }
}
