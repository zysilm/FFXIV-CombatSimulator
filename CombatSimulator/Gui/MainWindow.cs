using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly GlamourerIpc glamourerIpc;
    private readonly DeathCamController deathCamController;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    // Model override state
    private int modelOverrideId = 0;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    private static void HelpMarker(string desc)
    {
        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20.0f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public MainWindow(
        Configuration config,
        NpcSelector npcSelector,
        CombatEngine combatEngine,
        GlamourerIpc glamourerIpc,
        DeathCamController deathCamController,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.glamourerIpc = glamourerIpc;
        this.deathCamController = deathCamController;
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
        DrawSimulationSection();
        ImGui.Separator();
        DrawActiveTargetsSection();
        DrawNpcDefaultsSection();
        DrawTargetBehaviorsSection();
        ImGui.Separator();
        DrawAnimationCommandsSection();
        DrawGlamourerHeaderSection();
        DrawGuiSettingsSection();
        DrawDeathCamSection();
        DrawExperimentalHeaderSection();

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

    private void DrawNpcDefaultsSection()
    {
        if (ImGui.CollapsingHeader("NPC Defaults"))
        {
            var defaultLevel = config.DefaultNpcLevel;
            if (ImGui.SliderInt("Default NPC Level", ref defaultLevel, 1, 100))
            {
                config.DefaultNpcLevel = defaultLevel;
                config.Save();
            }
            HelpMarker("Level assigned to newly selected NPC targets.");

            var hpMult = config.DefaultNpcHpMultiplier;
            if (ImGui.SliderFloat("Default HP Multiplier", ref hpMult, 0.1f, 10.0f, "%.1f"))
            {
                config.DefaultNpcHpMultiplier = hpMult;
                config.Save();
            }
            HelpMarker("Multiplier applied to base NPC HP. Higher = tankier enemies.");

            var behaviorType = config.DefaultNpcBehaviorType;
            if (ImGui.Combo("Default NPC Behavior", ref behaviorType, BehaviorNames, BehaviorNames.Length))
            {
                config.DefaultNpcBehaviorType = behaviorType;
                config.Save();
            }
            HelpMarker("AI behavior for newly selected NPCs. Dummy = no attacks, Melee/Ranged = auto-attack, Boss = special patterns.");
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
            HelpMarker("Teleport selected NPC targets close to the player when combat starts.");

            if (approach)
            {
                var dist = config.TargetApproachDistance;
                if (ImGui.SliderFloat("Approach Distance", ref dist, 1.0f, 30.0f, "%.1f yalms"))
                {
                    config.TargetApproachDistance = dist;
                    config.Save();
                }
                HelpMarker("How close (in yalms) targets are moved to the player.");
            }

            ImGui.Spacing();

            var aggro = config.EnableAggroPropagation;
            if (ImGui.Checkbox("Aggro Propagation", ref aggro))
            {
                config.EnableAggroPropagation = aggro;
                config.Save();
            }
            HelpMarker("Automatically add nearby BattleNpcs as combat targets when one is engaged.");

            if (aggro)
            {
                var aggroRange = config.AggroPropagationRange;
                if (ImGui.SliderFloat("Aggro Range", ref aggroRange, 1.0f, 50.0f, "%.1f yalms"))
                {
                    config.AggroPropagationRange = aggroRange;
                    config.Save();
                }
                HelpMarker("Radius (in yalms) to scan for nearby BattleNpcs to auto-add.");

                var maxTargets = config.MaxTargets;
                if (ImGui.SliderInt("Aggro Max Targets", ref maxTargets, 1, 100))
                {
                    config.MaxTargets = maxTargets;
                    config.Save();
                }
                HelpMarker("Maximum number of active combat targets (includes manually selected and auto-aggro'd).");
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
            HelpMarker("Global damage multiplier applied to all attacks.");

            var crit = config.EnableCriticalHits;
            if (ImGui.Checkbox("Critical Hits", ref crit))
            {
                config.EnableCriticalHits = crit;
                combatEngine.EnableCriticalHits = crit;
                config.Save();
            }
            HelpMarker("Enable critical hit chance on attacks.");

            ImGui.SameLine();
            var dh = config.EnableDirectHits;
            if (ImGui.Checkbox("Direct Hits", ref dh))
            {
                config.EnableDirectHits = dh;
                combatEngine.EnableDirectHits = dh;
                config.Save();
            }
            HelpMarker("Enable direct hit chance on attacks.");

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

    private void DrawAnimationCommandsSection()
    {
        if (ImGui.CollapsingHeader("Animation Commands"))
        {
            var meleeCmd = config.PlayerMeleeAttackCommand;
            if (ImGui.InputText("Melee Attack", ref meleeCmd, 64))
            {
                config.PlayerMeleeAttackCommand = meleeCmd;
                config.Save();
            }
            HelpMarker("Chat command to execute for melee attack animation. Empty = use default timeline.");

            var rangedCmd = config.PlayerRangedAttackCommand;
            if (ImGui.InputText("Ranged Attack", ref rangedCmd, 64))
            {
                config.PlayerRangedAttackCommand = rangedCmd;
                config.Save();
            }
            HelpMarker("Chat command to execute for ranged attack animation. Empty = use default timeline.");

            ImGui.Separator();

            var deathCmd = config.PlayerDeathCommand;
            if (ImGui.InputText("Player Death", ref deathCmd, 64))
            {
                config.PlayerDeathCommand = deathCmd;
                config.Save();
            }
            HelpMarker("Chat command on player death (e.g. /playdead). Empty = bypass emote timeline.");

            var deathEmoteId = (int)config.DeathEmoteId;
            if (ImGui.InputInt("Death Emote ID", ref deathEmoteId))
            {
                config.DeathEmoteId = (uint)Math.Max(0, deathEmoteId);
                config.Save();
            }
            HelpMarker("Emote ID for the death animation. 0 = auto-detect 'Play Dead' from emote sheet.");

            ImGui.Separator();

            var victoryCmd = config.PlayerVictoryCommand;
            if (ImGui.InputText("Player Victory", ref victoryCmd, 64))
            {
                config.PlayerVictoryCommand = victoryCmd;
                config.Save();
            }
            HelpMarker("Chat command when the player wins (e.g. /victory).");

            var targetVictoryCmd = config.TargetVictoryCommand;
            if (ImGui.InputText("Target Victory", ref targetVictoryCmd, 64))
            {
                config.TargetVictoryCommand = targetVictoryCmd;
                config.Save();
            }
            HelpMarker("Chat command when the target/NPC side wins.");
        }
    }

    private void DrawGlamourerHeaderSection()
    {
        if (ImGui.CollapsingHeader("Glamourer Integration"))
        {
            DrawGlamourerSection();
        }
    }

    private void DrawGuiSettingsSection()
    {
        if (ImGui.CollapsingHeader("GUI Settings"))
        {
            var showHp = config.ShowEnemyHpBar;
            if (ImGui.Checkbox("Show Enemy HP Bar", ref showHp))
            {
                config.ShowEnemyHpBar = showHp;
                config.Save();
            }
            HelpMarker("Display a floating HP bar above enemy targets during combat.");

            var showPlayerHp = config.ShowPlayerHpBar;
            if (ImGui.Checkbox("Show Player HP Bar", ref showPlayerHp))
            {
                config.ShowPlayerHpBar = showPlayerHp;
                config.Save();
            }
            HelpMarker("Display a simulated player HP bar overlay during combat.");

            var showHudPlayerHp = config.ShowHudPlayerHpBar;
            if (ImGui.Checkbox("Show HUD Player HP Bar", ref showHudPlayerHp))
            {
                config.ShowHudPlayerHpBar = showHudPlayerHp;
                config.Save();
            }
            HelpMarker("Override the native FFXIV HUD HP bar with simulated HP values.");

            var showLog = config.ShowCombatLog;
            if (ImGui.Checkbox("Show Combat Log", ref showLog))
            {
                config.ShowCombatLog = showLog;
                config.Save();
            }
            HelpMarker("Show the combat log window with damage/healing/death events.");

            var showShortcuts = config.ShowShortcuts;
            if (ImGui.Checkbox("Show Shortcuts Bar", ref showShortcuts))
            {
                config.ShowShortcuts = showShortcuts;
                config.Save();
            }
            HelpMarker("Show a floating shortcuts bar for quick access to common actions.");
        }
    }

    private void DrawDeathCamSection()
    {
        if (ImGui.CollapsingHeader("Death Cam (Experimental)"))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Experimental: may cause camera issues.");
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Use /combatsim stop or zone change to recover.");
            ImGui.Spacing();

            var enabled = config.EnableDeathCam;
            if (ImGui.Checkbox("Enable Death Cam", ref enabled))
            {
                config.EnableDeathCam = enabled;
                config.Save();
            }
            HelpMarker("On player death, smoothly transition camera to an anchored position following a bone.");

            if (!enabled)
                return;

            // Bone selector
            var boneNames = new string[DeathCamController.CenterBones.Length];
            int currentBoneIdx = 0;
            for (int i = 0; i < DeathCamController.CenterBones.Length; i++)
            {
                boneNames[i] = DeathCamController.CenterBones[i].Name;
                if (DeathCamController.CenterBones[i].Index == config.DeathCamBoneIndex)
                    currentBoneIdx = i;
            }

            if (ImGui.Combo("Center Bone", ref currentBoneIdx, boneNames, boneNames.Length))
            {
                config.DeathCamBoneIndex = DeathCamController.CenterBones[currentBoneIdx].Index;
                config.Save();
            }
            HelpMarker("The bone the camera orbits around. Waist is recommended for stable tracking.");

            // Height offset
            var heightOffset = config.DeathCamHeightOffset;
            if (ImGui.SliderFloat("Height Offset", ref heightOffset, -5.0f, 10.0f, "%.2f"))
            {
                config.DeathCamHeightOffset = heightOffset;
                config.Save();
            }
            HelpMarker("Vertical offset added to the camera look-at point. Raise or lower the camera focus.");

            // Side offset
            var sideOffset = config.DeathCamSideOffset;
            if (ImGui.SliderFloat("Side Offset", ref sideOffset, -5.0f, 5.0f, "%.2f"))
            {
                config.DeathCamSideOffset = sideOffset;
                config.Save();
            }
            HelpMarker("Horizontal offset relative to character facing. Positive = right, negative = left.");

            // Set Anchor button + Preview toggle
            if (ImGui.Button("Set Anchor"))
            {
                if (deathCamController.SetAnchor())
                    chatGui.Print("[CombatSim] Death cam anchor set.");
                else
                    chatGui.PrintError("[CombatSim] Failed to set anchor. Make sure you have a character loaded.");
            }
            HelpMarker("Capture the current camera angle and distance as the death cam target.");

            ImGui.SameLine();
            var preview = deathCamController.IsPreviewActive;
            if (ImGui.Button(preview ? "Preview: ON" : "Preview: OFF"))
            {
                deathCamController.SetPreview(!preview);
            }
            HelpMarker("Toggle live preview: applies height/side offsets to the current camera so you can see the result.");

            // Anchor status
            if (config.DeathCamAnchorSet)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Anchor is set.");
                ImGui.Text($"H: {config.DeathCamAnchorDirH:F2}  V: {config.DeathCamAnchorDirV:F2}  Dist: {config.DeathCamAnchorDistance:F1}  Height: {config.DeathCamHeightOffset:F1}  Side: {config.DeathCamSideOffset:F1}");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No anchor set. Position camera and click Set Anchor.");
            }

            // Transition duration
            var duration = config.DeathCamTransitionDuration;
            if (ImGui.SliderFloat("Transition Duration", ref duration, 0.5f, 5.0f, "%.1f sec"))
            {
                config.DeathCamTransitionDuration = duration;
                config.Save();
            }
            HelpMarker("How long (in seconds) the camera takes to interpolate from current position to the anchor.");

            // Clear anchor
            if (config.DeathCamAnchorSet)
            {
                if (ImGui.SmallButton("Clear Anchor"))
                {
                    config.DeathCamAnchorSet = false;
                    config.Save();
                }
            }

            // Show current state
            if (deathCamController.State != DeathCamState.Inactive)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"State: {deathCamController.State}");
            }
        }
    }

    private void DrawExperimentalHeaderSection()
    {
        if (ImGui.CollapsingHeader("Experimental"))
        {
            var torture = config.EnableTorture;
            if (ImGui.Checkbox("Torture", ref torture))
            {
                config.EnableTorture = torture;
                config.Save();
            }
            HelpMarker("Allow attacks on dead characters. Dead targets stay on the floor and take hits but cannot fight back.");
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
        if (ImGui.Checkbox("Apply Glamourer Preset on Start/Reset", ref enabledReset))
        {
            config.ApplyGlamourerOnReset = enabledReset;
            config.Save();
        }

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
            "Death preset reverts on reset/stop.\n" +
            "Start/Reset preset applies on start, reset, and reboot.");

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

    public void DrawShortcutsBar()
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
