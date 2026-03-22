using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly GlamourerIpc glamourerIpc;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    // Model override state
    private int modelOverrideId = 0;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    public MainWindow(
        Configuration config,
        NpcSelector npcSelector,
        CombatEngine combatEngine,
        GlamourerIpc glamourerIpc,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.glamourerIpc = glamourerIpc;
        this.chatGui = chatGui;
        this.log = log;
    }

    public void Draw()
    {
        if (config.ShowShortcuts)
            DrawShortcutsBar();

        ImGui.SetNextWindowSize(new Vector2(420, 600), ImGuiCond.FirstUseEver);
        var showWindow = config.ShowMainWindow;
        if (!ImGui.Begin("Combat Simulator", ref showWindow))
        {
            config.ShowMainWindow = showWindow;
            ImGui.End();
            return;
        }
        config.ShowMainWindow = showWindow;

        DrawStatusSection();
        ImGui.Separator();
        DrawSimulationSection();
        ImGui.Separator();
        DrawActiveTargetsSection();
        DrawTargetBehaviorsSection();
        ImGui.Separator();
        DrawSettingsSection();

        ImGui.End();
    }

    private void DrawStatusSection()
    {
        var simActive = combatEngine.IsActive;
        var statusColor = simActive ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0.3f, 0.3f, 1);
        var statusText = simActive ? "Active" : "Inactive";

        ImGui.Text("Status:");
        ImGui.SameLine();
        ImGui.TextColored(statusColor, statusText);

        if (simActive && combatEngine.State.CombatDuration > 0)
        {
            ImGui.Text($"DPS: {combatEngine.State.Dps:N0}");
            ImGui.SameLine();
            ImGui.Text($"Duration: {combatEngine.State.CombatDuration:F1}s");
        }

        if (!simActive)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1),
                "Start combat and attack any NPC to begin.\n" +
                "Targets are auto-registered on first attack.");
        }
    }

    private void DrawActiveTargetsSection()
    {
        if (ImGui.CollapsingHeader("Active Targets", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (npcSelector.SelectedNpcs.Count == 0)
            {
                ImGui.TextDisabled("No targets yet. Attack an NPC to register it.");
                return;
            }

            for (int i = npcSelector.SelectedNpcs.Count - 1; i >= 0; i--)
            {
                var npc = npcSelector.SelectedNpcs[i];
                ImGui.PushID(i);

                // HP bar
                float hpPercent = npc.State.MaxHp > 0
                    ? (float)npc.State.CurrentHp / npc.State.MaxHp
                    : 0;

                var hpColor = hpPercent > 0.5f
                    ? new Vector4(0, 0.8f, 0, 1)
                    : hpPercent > 0.25f
                        ? new Vector4(0.8f, 0.8f, 0, 1)
                        : new Vector4(0.8f, 0, 0, 1);

                var stateText = npc.AiState.ToString();
                var label = $"[Lv.{npc.State.Level}] {npc.Name} ({stateText})";
                var overlay = $"{npc.State.CurrentHp:N0} / {npc.State.MaxHp:N0}";

                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, hpColor);
                ImGui.ProgressBar(hpPercent, new Vector2(-65, 0), overlay);
                ImGui.PopStyleColor();

                ImGui.SameLine();
                ImGui.Text(label);

                // Cast bar
                if (npc.State.IsCasting && npc.CurrentCastSkill != null)
                {
                    float castPercent = npc.State.CastTimeTotal > 0
                        ? npc.State.CastTimeElapsed / npc.State.CastTimeTotal
                        : 0;
                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.8f, 0.5f, 0, 1));
                    ImGui.ProgressBar(castPercent, new Vector2(-65, 0), npc.CurrentCastSkill.Name);
                    ImGui.PopStyleColor();
                }

                // Model override (collapsed by default)
                if (ImGui.TreeNode("Model Override"))
                {
                    ImGui.InputInt("ModelCharaId", ref modelOverrideId);
                    if (ImGui.SmallButton("Apply Model"))
                    {
                        npcSelector.ChangeModel(npc, modelOverrideId);
                        chatGui.Print($"[CombatSim] Model changed to {modelOverrideId}.");
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Restore"))
                    {
                        npcSelector.RestoreModel(npc);
                        chatGui.Print("[CombatSim] Model restored.");
                    }
                    ImGui.TreePop();
                }

                ImGui.PopID();
            }
        }
    }

    private void DrawTargetBehaviorsSection()
    {
        if (ImGui.CollapsingHeader("Target Behaviors"))
        {
            var approach = config.EnableTargetApproach;
            if (ImGui.Checkbox("Move Targets Near Player", ref approach))
            {
                config.EnableTargetApproach = approach;
                config.Save();
            }

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                "Active targets will move to stay near your character.\n" +
                "On death or reset, they remain in place.");

            if (approach)
            {
                var dist = config.TargetApproachDistance;
                if (ImGui.SliderFloat("Approach Distance", ref dist, 1.0f, 30.0f, "%.1f yalms"))
                {
                    config.TargetApproachDistance = dist;
                    config.Save();
                }
            }

            ImGui.Spacing();

            var aggro = config.EnableAggroPropagation;
            if (ImGui.Checkbox("Aggro Propagation", ref aggro))
            {
                config.EnableAggroPropagation = aggro;
                config.Save();
            }

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                "When you attack one target, nearby idle targets\njoin the fight automatically.");

            if (aggro)
            {
                var aggroRange = config.AggroPropagationRange;
                if (ImGui.SliderFloat("Aggro Range", ref aggroRange, 1.0f, 50.0f, "%.1f yalms"))
                {
                    config.AggroPropagationRange = aggroRange;
                    config.Save();
                }
            }
        }
    }

    private void DrawSimulationSection()
    {
        if (ImGui.CollapsingHeader("Simulation Controls", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var isActive = combatEngine.IsActive;

            if (!isActive)
            {
                if (ImGui.Button("Start Combat", new Vector2(150, 0)))
                {
                    combatEngine.StartSimulation();
                    chatGui.Print("[CombatSim] Combat simulation started.");
                }
            }
            else
            {
                if (ImGui.Button("Stop Combat", new Vector2(150, 0)))
                {
                    combatEngine.StopSimulation();
                    chatGui.Print("[CombatSim] Combat simulation stopped.");
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset All", new Vector2(150, 0)))
            {
                combatEngine.ResetState();
                chatGui.Print("[CombatSim] Combat state reset.");
            }

            var dmgMult = config.DamageMultiplier;
            if (ImGui.SliderFloat("Damage Multiplier", ref dmgMult, 0.1f, 5.0f, "%.1f"))
            {
                config.DamageMultiplier = dmgMult;
                combatEngine.DamageMultiplier = dmgMult;
                config.Save();
            }

            var crit = config.EnableCriticalHits;
            if (ImGui.Checkbox("Critical Hits", ref crit))
            {
                config.EnableCriticalHits = crit;
                combatEngine.EnableCriticalHits = crit;
                config.Save();
            }

            ImGui.SameLine();
            var dh = config.EnableDirectHits;
            if (ImGui.Checkbox("Direct Hits", ref dh))
            {
                config.EnableDirectHits = dh;
                combatEngine.EnableDirectHits = dh;
                config.Save();
            }

            if (isActive)
            {
                var ps = combatEngine.State.PlayerState;
                float playerHpPercent = ps.MaxHp > 0 ? (float)ps.CurrentHp / ps.MaxHp : 1;
                var playerHpColor = playerHpPercent > 0.5f
                    ? new Vector4(0, 0.8f, 0, 1)
                    : playerHpPercent > 0.25f
                        ? new Vector4(0.8f, 0.8f, 0, 1)
                        : new Vector4(0.8f, 0, 0, 1);

                ImGui.Text("Player Simulated HP:");
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, playerHpColor);
                ImGui.ProgressBar(playerHpPercent, new Vector2(-1, 0),
                    $"{ps.CurrentHp:N0} / {ps.MaxHp:N0}");
                ImGui.PopStyleColor();
            }
        }
    }

    private void DrawSettingsSection()
    {
        if (ImGui.CollapsingHeader("Settings"))
        {
            var showHp = config.ShowEnemyHpBar;
            if (ImGui.Checkbox("Show Enemy HP Bar", ref showHp))
            {
                config.ShowEnemyHpBar = showHp;
                config.Save();
            }

            var showPlayerHp = config.ShowPlayerHpBar;
            if (ImGui.Checkbox("Show Player HP Bar", ref showPlayerHp))
            {
                config.ShowPlayerHpBar = showPlayerHp;
                config.Save();
            }

            var showHudPlayerHp = config.ShowHudPlayerHpBar;
            if (ImGui.Checkbox("Show HUD Player HP Bar", ref showHudPlayerHp))
            {
                config.ShowHudPlayerHpBar = showHudPlayerHp;
                config.Save();
            }

            var showLog = config.ShowCombatLog;
            if (ImGui.Checkbox("Show Combat Log", ref showLog))
            {
                config.ShowCombatLog = showLog;
                config.Save();
            }

            var showShortcuts = config.ShowShortcuts;
            if (ImGui.Checkbox("Show Shortcuts Bar", ref showShortcuts))
            {
                config.ShowShortcuts = showShortcuts;
                config.Save();
            }

            ImGui.Separator();

            var defaultLevel = config.DefaultNpcLevel;
            if (ImGui.SliderInt("Default NPC Level", ref defaultLevel, 1, 100))
            {
                config.DefaultNpcLevel = defaultLevel;
                config.Save();
            }

            var hpMult = config.DefaultNpcHpMultiplier;
            if (ImGui.SliderFloat("Default HP Multiplier", ref hpMult, 0.1f, 10.0f, "%.1f"))
            {
                config.DefaultNpcHpMultiplier = hpMult;
                config.Save();
            }

            var behaviorType = config.DefaultNpcBehaviorType;
            if (ImGui.Combo("Default NPC Behavior", ref behaviorType, BehaviorNames, BehaviorNames.Length))
            {
                config.DefaultNpcBehaviorType = behaviorType;
                config.Save();
            }
        }

        if (ImGui.CollapsingHeader("Animation Commands"))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1),
                "Leave empty for default timeline animation.\n" +
                "Set a command (e.g., /gsit) for custom animation.");
            ImGui.Spacing();

            var meleeCmd = config.PlayerMeleeAttackCommand;
            if (ImGui.InputText("Melee Attack", ref meleeCmd, 64))
            {
                config.PlayerMeleeAttackCommand = meleeCmd;
                config.Save();
            }

            var rangedCmd = config.PlayerRangedAttackCommand;
            if (ImGui.InputText("Ranged Attack", ref rangedCmd, 64))
            {
                config.PlayerRangedAttackCommand = rangedCmd;
                config.Save();
            }

            ImGui.Separator();

            var deathCmd = config.PlayerDeathCommand;
            if (ImGui.InputText("Player Death", ref deathCmd, 64))
            {
                config.PlayerDeathCommand = deathCmd;
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "(empty = bypass)");
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                "Empty = BypassEmote-style playdead (no unlock needed).\n" +
                "Set a command (e.g., /playdead) to use that instead.\n" +
                "NPC death always uses bypass timeline.");

            var deathEmoteId = (int)config.DeathEmoteId;
            if (ImGui.InputInt("Death Emote ID", ref deathEmoteId))
            {
                config.DeathEmoteId = (uint)Math.Max(0, deathEmoteId);
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "(0 = auto-detect)");

            ImGui.Separator();

            var victoryCmd = config.PlayerVictoryCommand;
            if (ImGui.InputText("Player Victory", ref victoryCmd, 64))
            {
                config.PlayerVictoryCommand = victoryCmd;
                config.Save();
            }

            var targetVictoryCmd = config.TargetVictoryCommand;
            if (ImGui.InputText("Target Victory", ref targetVictoryCmd, 64))
            {
                config.TargetVictoryCommand = targetVictoryCmd;
                config.Save();
            }
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                "Target Victory runs on your character\n" +
                "(NPCs can't execute commands).");
        }

        if (ImGui.CollapsingHeader("Glamourer Integration"))
        {
            DrawGlamourerSection();
        }
    }

    private void DrawGlamourerSection()
    {
        var enabled = config.ApplyGlamourerOnDeath;
        if (ImGui.Checkbox("Apply Glamourer Preset on Death", ref enabled))
        {
            config.ApplyGlamourerOnDeath = enabled;
            config.Save();
        }

        var enabledReset = config.ApplyGlamourerOnReset;
        if (ImGui.Checkbox("Apply Glamourer Preset on Reset", ref enabledReset))
        {
            config.ApplyGlamourerOnReset = enabledReset;
            config.Save();
        }

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
            "Death preset reverts on reset/stop.\n" +
            "Reset preset applies when you respawn.");

        if (!enabled && !enabledReset)
            return;

        ImGui.Spacing();

        if (!glamourerIpc.IsAvailable && glamourerDesigns.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1),
                "Glamourer not detected. Click Refresh.");
        }

        if (ImGui.Button("Refresh Designs"))
        {
            var designs = glamourerIpc.GetDesignList();
            glamourerDesigns = new List<KeyValuePair<Guid, string>>(designs);
            glamourerDesigns.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));

            glamourerSelectedIndex = FindDesignIndex(config.DeathGlamourerDesignId);
            glamourerResetSelectedIndex = FindDesignIndex(config.ResetGlamourerDesignId);

            if (glamourerDesigns.Count > 0)
                chatGui.Print($"[CombatSim] Found {glamourerDesigns.Count} Glamourer designs.");
            else
                chatGui.Print("[CombatSim] No Glamourer designs found. Is Glamourer installed?");
        }

        if (glamourerDesigns.Count > 0)
        {
            var names = new string[glamourerDesigns.Count];
            for (int i = 0; i < glamourerDesigns.Count; i++)
                names[i] = glamourerDesigns[i].Value;

            if (enabled)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##DeathDesign", ref glamourerSelectedIndex, names, names.Length))
                {
                    if (glamourerSelectedIndex >= 0 && glamourerSelectedIndex < glamourerDesigns.Count)
                    {
                        config.DeathGlamourerDesignId = glamourerDesigns[glamourerSelectedIndex].Key.ToString();
                        config.Save();
                    }
                }
                if (glamourerSelectedIndex >= 0 && glamourerSelectedIndex < glamourerDesigns.Count)
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1),
                        $"Death: {glamourerDesigns[glamourerSelectedIndex].Value}");
            }

            if (enabledReset)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##ResetDesign", ref glamourerResetSelectedIndex, names, names.Length))
                {
                    if (glamourerResetSelectedIndex >= 0 && glamourerResetSelectedIndex < glamourerDesigns.Count)
                    {
                        config.ResetGlamourerDesignId = glamourerDesigns[glamourerResetSelectedIndex].Key.ToString();
                        config.Save();
                    }
                }
                if (glamourerResetSelectedIndex >= 0 && glamourerResetSelectedIndex < glamourerDesigns.Count)
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1),
                        $"Reset: {glamourerDesigns[glamourerResetSelectedIndex].Value}");
            }
        }
    }

    private int FindDesignIndex(string designId)
    {
        if (Guid.TryParse(designId, out var id))
        {
            for (int i = 0; i < glamourerDesigns.Count; i++)
                if (glamourerDesigns[i].Key == id)
                    return i;
        }
        return -1;
    }

    private void DrawShortcutsBar()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Combat Shortcuts", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var isActive = combatEngine.IsActive;
        var btnSize = new Vector2(95, 28);

        if (!isActive)
        {
            if (ImGui.Button("Start", btnSize))
            {
                combatEngine.StartSimulation();
                chatGui.Print("[CombatSim] Combat simulation started.");
            }
        }
        else
        {
            if (ImGui.Button("Stop", btnSize))
            {
                combatEngine.StopSimulation();
                chatGui.Print("[CombatSim] Combat simulation stopped.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset All", btnSize))
        {
            combatEngine.ResetState();
            chatGui.Print("[CombatSim] Combat state reset.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reboot", btnSize))
        {
            combatEngine.StopSimulation();
            combatEngine.StartSimulation();
            chatGui.Print("[CombatSim] Combat simulation rebooted.");
        }

        ImGui.End();
    }

    public void Dispose()
    {
    }
}
