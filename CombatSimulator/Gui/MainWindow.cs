using System;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly Core.HyperboreaDetector hyperboreaDetector;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly NpcAiController npcAiController;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    // Target selection state
    private int selectedLevel = 90;
    private float selectedHpMultiplier = 1.0f;
    private int selectedBehaviorType = 1; // BasicMelee
    private int modelOverrideId = 0;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    public MainWindow(
        Configuration config,
        Core.HyperboreaDetector hyperboreaDetector,
        NpcSelector npcSelector,
        CombatEngine combatEngine,
        NpcAiController npcAiController,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.config = config;
        this.hyperboreaDetector = hyperboreaDetector;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.npcAiController = npcAiController;
        this.chatGui = chatGui;
        this.log = log;
    }

    public void Draw()
    {
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
        DrawTargetSelectionSection();
        ImGui.Separator();
        DrawActiveTargetsSection();
        ImGui.Separator();
        DrawSimulationSection();
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

        hyperboreaDetector.CheckStatus();
        var hyperLoaded = hyperboreaDetector.IsHyperboreaLoaded;
        var hyperColor = hyperLoaded ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0.3f, 0.3f, 1);
        var hyperText = hyperLoaded ? "Detected" : "Not Found";

        ImGui.Text("Hyperborea:");
        ImGui.SameLine();
        ImGui.TextColored(hyperColor, hyperText);

        if (!hyperLoaded && config.RequireHyperborea)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1),
                "WARNING: Hyperborea is required for safe operation.");
        }

        if (simActive && combatEngine.State.CombatDuration > 0)
        {
            ImGui.Text($"DPS: {combatEngine.State.Dps:N0}");
            ImGui.SameLine();
            ImGui.Text($"Duration: {combatEngine.State.CombatDuration:F1}s");
        }
    }

    private void DrawTargetSelectionSection()
    {
        if (ImGui.CollapsingHeader("Select Combat Target", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1),
                "Target an NPC in-game, then click Select.");

            // Settings for the selected target
            ImGui.SliderInt("Level", ref selectedLevel, 1, 100);
            ImGui.SliderFloat("HP Multiplier", ref selectedHpMultiplier, 0.1f, 10.0f, "%.1f");
            ImGui.Combo("Behavior", ref selectedBehaviorType, BehaviorNames, BehaviorNames.Length);

            // Select button
            var canSelect = npcSelector.SelectedNpcs.Count < npcSelector.MaxTargets;
            if (!canSelect)
                ImGui.BeginDisabled();

            if (ImGui.Button("Select Current Target", new Vector2(200, 0)))
            {
                var (npc, error) = npcSelector.SelectCurrentTarget(
                    selectedLevel, selectedHpMultiplier,
                    (NpcBehaviorType)selectedBehaviorType);

                if (npc != null)
                {
                    combatEngine.RegisterNpcEntity(npc);
                    chatGui.Print($"[CombatSim] Selected '{npc.Name}' as combat target (Lv.{selectedLevel}).");
                }
                else
                {
                    chatGui.PrintError($"[CombatSim] {error}");
                }
            }

            if (!canSelect)
                ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Deselect All", new Vector2(150, 0)))
            {
                foreach (var npc in npcSelector.SelectedNpcs)
                    combatEngine.UnregisterNpcEntity(npc.SimulatedEntityId);
                npcSelector.DeselectAll();
                chatGui.Print("[CombatSim] All targets deselected.");
            }

            ImGui.Text($"Selected: {npcSelector.SelectedNpcs.Count} / {npcSelector.MaxTargets}");
        }
    }

    private void DrawActiveTargetsSection()
    {
        if (ImGui.CollapsingHeader("Active Targets", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (npcSelector.SelectedNpcs.Count == 0)
            {
                ImGui.TextDisabled("No targets selected.");
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

                // Deselect button
                ImGui.SameLine(ImGui.GetWindowWidth() - 70);
                if (ImGui.SmallButton("Deselect"))
                {
                    combatEngine.UnregisterNpcEntity(npc.SimulatedEntityId);
                    npcSelector.DeselectNpc(npc);
                }

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

    private void DrawSimulationSection()
    {
        if (ImGui.CollapsingHeader("Simulation Controls", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var isActive = combatEngine.IsActive;

            if (!isActive)
            {
                var canStart = !config.RequireHyperborea || hyperboreaDetector.IsHyperboreaLoaded;
                if (!canStart)
                    ImGui.BeginDisabled();

                if (ImGui.Button("Start Combat", new Vector2(150, 0)))
                {
                    combatEngine.StartSimulation();
                    chatGui.Print("[CombatSim] Combat simulation started.");
                }

                if (!canStart)
                    ImGui.EndDisabled();
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

            var showLog = config.ShowCombatLog;
            if (ImGui.Checkbox("Show Combat Log", ref showLog))
            {
                config.ShowCombatLog = showLog;
                config.Save();
            }

            var requireHyper = config.RequireHyperborea;
            if (ImGui.Checkbox("Require Hyperborea", ref requireHyper))
            {
                config.RequireHyperborea = requireHyper;
                config.Save();
            }

            if (!requireHyper)
            {
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1),
                    "WARNING: Disabling Hyperborea requirement\n" +
                    "reduces safety. The UseAction hook still\n" +
                    "blocks server packets, but Hyperborea's\n" +
                    "full firewall provides stronger protection.");
            }

            var defaultLevel = config.DefaultNpcLevel;
            if (ImGui.SliderInt("Default NPC Level", ref defaultLevel, 1, 100))
            {
                config.DefaultNpcLevel = defaultLevel;
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
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "(default: /playdead)");

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
    }

    public void Dispose()
    {
    }
}
