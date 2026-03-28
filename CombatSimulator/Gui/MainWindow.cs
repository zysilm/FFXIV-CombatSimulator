using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
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
    private readonly AnimationController animationController;
    private readonly RagdollController ragdollController;
    private readonly DeathCamController deathCamController;
    private readonly ActiveCameraController activeCameraController;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    // Model override state
    private int modelOverrideId = 0;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    // Death cam preset state
    private string newPresetName = "";
    private int selectedPresetIndex = -1;
    private bool overwritePopupOpen = false;
    private string overwriteTargetName = "";

    // Dev easter egg state
    private int devClickCount = 0;
    private bool devUnlocked = false;

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
        AnimationController animationController,
        RagdollController ragdollController,
        DeathCamController deathCamController,
        ActiveCameraController activeCameraController,
        IClientState clientState,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.glamourerIpc = glamourerIpc;
        this.animationController = animationController;
        this.ragdollController = ragdollController;
        this.deathCamController = deathCamController;
        this.activeCameraController = activeCameraController;
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.log = log;
    }

    private int selectedTab = 0;

    private static readonly string[] TabNames = new[]
    {
        "Combat",
        "Targets",
        "Animation",
        "Camera",
        "Ragdoll",
        "Settings",
    };

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 500), ImGuiCond.FirstUseEver);
        var showWindow = config.ShowMainWindow;
        if (!ImGui.Begin("Combat Simulator", ref showWindow))
        {
            config.ShowMainWindow = showWindow;
            ImGui.End();
            return;
        }
        config.ShowMainWindow = showWindow;

        // Status bar at top (always visible)
        DrawStatusSection();
        ImGui.Separator();

        // Left sidebar + right content with draggable splitter
        var contentHeight = ImGui.GetContentRegionAvail().Y;
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var sidebarWidth = config.SidebarWidth;

        // Clamp sidebar width
        sidebarWidth = Math.Clamp(sidebarWidth, 80f, totalWidth - 150f);

        // Left sidebar
        ImGui.BeginChild("##sidebar", new Vector2(sidebarWidth, contentHeight), true);
        for (int i = 0; i < TabNames.Length; i++)
        {
            if (ImGui.Selectable(TabNames[i], selectedTab == i))
                selectedTab = i;
        }
        ImGui.EndChild();

        // Draggable splitter
        ImGui.SameLine();
        ImGui.Button("##splitter", new Vector2(4, contentHeight));
        if (ImGui.IsItemActive())
        {
            config.SidebarWidth = sidebarWidth + ImGui.GetIO().MouseDelta.X;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor((ImGuiMouseCursor)5); // ResizeEW

        ImGui.SameLine();

        // Right content panel
        ImGui.BeginChild("##content", new Vector2(0, contentHeight), true);
        switch (selectedTab)
        {
            case 0: // Combat
                DrawSimulationSection();
                break;
            case 1: // Targets
                DrawActiveTargetsSection();
                DrawNpcDefaultsSection();
                DrawTargetBehaviorsSection();
                break;
            case 2: // Animation
                DrawAnimationCommandsSection();
                DrawHitVfxSection();
                DrawGlamourerHeaderSection();
                break;
            case 3: // Camera
                DrawActiveCamSection();
                DrawDeathCamSection();
                break;
            case 4: // Ragdoll
                DrawRagdollSection();
                DrawNpcCollisionSection();
                break;
            case 5: // Settings
                DrawGuiSettingsSection();
                DrawDevSection();
                break;
        }
        ImGui.EndChild();

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
                if (ImGui.SliderFloat("Approach Distance", ref dist, 0.1f, 5.0f, "%.1f yalms"))
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
                if (ImGui.SliderInt("Aggro Max Targets", ref maxTargets, 1, 30))
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

    private void DrawHitVfxSection()
    {
        if (ImGui.CollapsingHeader("Hit VFX"))
        {
            var enableVfx = config.EnableHitVfx;
            if (ImGui.Checkbox("Enable Hit VFX on Player", ref enableVfx))
            {
                config.EnableHitVfx = enableVfx;
                config.Save();
            }
            HelpMarker("Spawn a visual effect on your character when hit by NPC attacks.");

            var vfxPath = config.HitVfxPath;
            if (ImGui.InputText("VFX Path (.avfx)", ref vfxPath, 256))
            {
                config.HitVfxPath = vfxPath;
                config.Save();
            }
            HelpMarker("Game VFX path to spawn on hit. Uses FFXIV's internal .avfx format.\nDefault: vfx/common/eff/dk05th_stdn0t.avfx");

            if (!animationController.HitVfxAvailable)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "ActorVfxCreate not found — hit VFX unavailable.");
            }
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

            var showDcToolbar = config.ShowDeathCamToolbar;
            if (ImGui.Checkbox("Show Death Cam Toolbar", ref showDcToolbar))
            {
                config.ShowDeathCamToolbar = showDcToolbar;
                config.Save();
            }
            HelpMarker("Show a floating toolbar to quickly switch between death cam presets.");

            var showAcToolbar = config.ShowActiveCamToolbar;
            if (ImGui.Checkbox("Show Active Cam Toolbar", ref showAcToolbar))
            {
                config.ShowActiveCamToolbar = showAcToolbar;
                config.Save();
            }
            HelpMarker("Show a floating toolbar to quickly adjust active camera bone, zoom, and offsets.");

            // Custom player name
            var customName = config.CustomPlayerName;
            if (ImGui.InputText("Custom Player Name", ref customName, 64))
            {
                config.CustomPlayerName = customName;
                config.Save();
            }
            HelpMarker("Custom name shown on the sim HP bar. Leave empty to use your character's actual name.");

            // Player HP bar Y offset
            var hpYOffset = config.PlayerHpBarYOffset;
            if (ImGui.SliderFloat("Player HP Bar Y Offset", ref hpYOffset, -3.0f, 5.0f, "%.2f"))
            {
                config.PlayerHpBarYOffset = hpYOffset;
                config.Save();
            }
            HelpMarker("Vertical offset for the player HP bar in world space. Higher values move the bar up.");
        }
    }

    private void DrawDeathCamSection()
    {
        if (ImGui.CollapsingHeader("Death Cam (Experimental)"))
        {
            var enabled = config.EnableDeathCam;
            if (ImGui.Checkbox("Enable Death Cam", ref enabled))
            {
                config.EnableDeathCam = enabled;
                config.Save();
            }
            HelpMarker("On player death, smoothly transition camera to an anchored position following a bone.");

            if (!enabled)
                return;

            // --- Presets ---
            ImGui.Separator();
            ImGui.Text("Presets");

            var presetNames = new string[config.DeathCamPresets.Count];
            for (int i = 0; i < config.DeathCamPresets.Count; i++)
                presetNames[i] = config.DeathCamPresets[i].Name;

            var hasSelection = selectedPresetIndex >= 0 && selectedPresetIndex < config.DeathCamPresets.Count;

            if (ImGui.BeginListBox("##PresetSelect", new Vector2(250, ImGui.GetTextLineHeightWithSpacing() * 8 + ImGui.GetStyle().FramePadding.Y * 2)))
            {
                for (int i = 0; i < presetNames.Length; i++)
                {
                    bool isSelected = selectedPresetIndex == i;
                    if (ImGui.Selectable(presetNames[i], isSelected))
                        selectedPresetIndex = i;
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndListBox();
            }

            ImGui.SameLine();
            if (ImGui.Button("Load") && hasSelection)
            {
                LoadPreset(config.DeathCamPresets[selectedPresetIndex]);
            }

            ImGui.SameLine();
            if (ImGui.Button("Overwrite") && hasSelection)
            {
                overwriteTargetName = config.DeathCamPresets[selectedPresetIndex].Name;
                overwritePopupOpen = true;
                ImGui.OpenPopup("Confirm Overwrite##PresetOverwrite");
            }

            ImGui.SameLine();
            var io = ImGui.GetIO();
            bool ctrlShiftHeld = io.KeyCtrl && io.KeyShift;
            if (!ctrlShiftHeld)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Delete");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Hold Ctrl+Shift to enable delete.");
            }
            else if (ImGui.Button("Delete") && hasSelection)
            {
                config.DeathCamPresets.RemoveAt(selectedPresetIndex);
                selectedPresetIndex = Math.Min(selectedPresetIndex, config.DeathCamPresets.Count - 1);
                config.Save();
            }

            ImGui.SetNextItemWidth(250);
            ImGui.InputText("##PresetName", ref newPresetName, 64);
            ImGui.SameLine();
            if (ImGui.Button("Save Preset") && newPresetName.Length > 0)
            {
                var existingIdx = config.DeathCamPresets.FindIndex(p =>
                    p.Name.Equals(newPresetName, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                {
                    overwriteTargetName = newPresetName;
                    overwritePopupOpen = true;
                    ImGui.OpenPopup("Confirm Overwrite##PresetOverwrite");
                }
                else
                {
                    SavePreset(newPresetName);
                    newPresetName = "";
                }
            }

            // Overwrite confirmation popup
            if (ImGui.BeginPopupModal("Confirm Overwrite##PresetOverwrite", ref overwritePopupOpen,
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
            {
                ImGui.Text($"Overwrite preset \"{overwriteTargetName}\"?");
                ImGui.Spacing();

                if (ImGui.Button("Yes", new Vector2(80, 0)))
                {
                    SavePreset(overwriteTargetName);
                    newPresetName = "";
                    overwritePopupOpen = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(80, 0)))
                {
                    overwritePopupOpen = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            // --- Settings ---
            ImGui.Separator();

            // Bone selector
            var boneNames = new string[DeathCamController.CenterBones.Length];
            int currentBoneIdx = 0;
            for (int i = 0; i < DeathCamController.CenterBones.Length; i++)
            {
                boneNames[i] = DeathCamController.CenterBones[i].Label;
                if (DeathCamController.CenterBones[i].BoneName == config.DeathCamBoneName)
                    currentBoneIdx = i;
            }

            if (ImGui.Combo("Center Bone", ref currentBoneIdx, boneNames, boneNames.Length))
            {
                config.DeathCamBoneName = DeathCamController.CenterBones[currentBoneIdx].BoneName;
                config.Save();
            }
            HelpMarker("The bone the camera orbits around. Waist is recommended for stable tracking.");

            // Disable Camera Collision
            var disableCollision = config.DeathCamDisableCollision;
            if (ImGui.Checkbox("Disable Camera Collision", ref disableCollision))
            {
                config.DeathCamDisableCollision = disableCollision;
                config.Save();
            }
            HelpMarker("Camera ignores wall/object collision during death cam.");

            // Transition duration
            var duration = config.DeathCamTransitionDuration;
            if (ImGui.SliderFloat("Transition Duration", ref duration, 0.5f, 5.0f, "%.1f sec"))
            {
                config.DeathCamTransitionDuration = duration;
                config.Save();
            }
            HelpMarker("How long the camera takes to interpolate to the anchor on death.");

            // --- Actions ---
            ImGui.Separator();

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
            var previewLabel = preview ? "Preview: ON" : "Preview: OFF";
            if (preview)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.2f, 1f));
            if (ImGui.Button(previewLabel))
            {
                deathCamController.SetPreview(!preview);
            }
            if (preview)
                ImGui.PopStyleColor();
            HelpMarker("Toggle live preview. Opens camera control window for intuitive tweaking.");

            ImGui.SameLine();
            if (config.DeathCamAnchorSet && ImGui.SmallButton("Clear Anchor"))
            {
                config.DeathCamAnchorSet = false;
                config.Save();
            }

            // Anchor status
            if (config.DeathCamAnchorSet)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Anchor is set.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No anchor set. Position camera and click Set Anchor.");
            }

            // Show current state
            if (deathCamController.State != DeathCamState.Inactive)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"State: {deathCamController.State}");
            }
        }

        // Draw camera control window when preview is active
        if (deathCamController.IsPreviewActive)
            DrawCameraControlWindow();
    }

    private void SavePreset(string name)
    {
        // Update existing preset with same name, or add new
        var existing = config.DeathCamPresets.FindIndex(p => p.Name == name);
        var preset = new DeathCamPreset
        {
            Name = name,
            BoneName = config.DeathCamBoneName,
            DirH = config.DeathCamAnchorDirH,
            DirV = config.DeathCamAnchorDirV,
            Distance = config.DeathCamAnchorDistance,
            FoV = config.DeathCamFoV,
            HeightOffset = config.DeathCamHeightOffset,
            SideOffset = config.DeathCamSideOffset,
            Tilt = config.DeathCamTilt,
            DisableCollision = config.DeathCamDisableCollision,
            TransitionDuration = config.DeathCamTransitionDuration,
        };
        if (existing >= 0)
            config.DeathCamPresets[existing] = preset;
        else
            config.DeathCamPresets.Add(preset);
        config.DeathCamAnchorSet = true;
        config.Save();
        chatGui.Print($"[CombatSim] Preset '{name}' saved.");
    }

    private void LoadPreset(DeathCamPreset preset)
    {
        config.DeathCamBoneName = preset.BoneName;
        config.DeathCamAnchorDirH = preset.DirH;
        config.DeathCamAnchorDirV = preset.DirV;
        config.DeathCamAnchorDistance = preset.Distance;
        config.DeathCamFoV = preset.FoV;
        config.DeathCamHeightOffset = preset.HeightOffset;
        config.DeathCamSideOffset = preset.SideOffset;
        config.DeathCamTilt = preset.Tilt;
        config.DeathCamDisableCollision = preset.DisableCollision;
        config.DeathCamTransitionDuration = preset.TransitionDuration;
        config.DeathCamAnchorSet = true;
        config.Save();
        chatGui.Print($"[CombatSim] Preset '{preset.Name}' loaded.");
    }

    private void DrawCameraControlWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Death Cam Controls##CamCtrl", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        bool changed = false;

        // --- Orbit ---
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Orbit");
        ImGui.Separator();

        var distance = config.DeathCamAnchorDistance;
        if (ImGui.DragFloat("Distance", ref distance, 0.05f, 0.0f, 30.0f, "%.2f"))
        { config.DeathCamAnchorDistance = distance; changed = true; }

        var dirH = config.DeathCamAnchorDirH;
        if (ImGui.DragFloat("Horizontal", ref dirH, 0.01f, -MathF.PI, MathF.PI, "%.2f rad"))
        { config.DeathCamAnchorDirH = dirH; changed = true; }

        var dirV = config.DeathCamAnchorDirV;
        if (ImGui.DragFloat("Vertical", ref dirV, 0.01f, -MathF.PI / 2f, MathF.PI / 2f, "%.2f rad"))
        { config.DeathCamAnchorDirV = dirV; changed = true; }

        ImGui.Spacing();

        // --- Lens ---
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Lens");
        ImGui.Separator();

        var fov = config.DeathCamFoV;
        if (ImGui.DragFloat("Field of View", ref fov, 0.005f, 0.1f, 2.0f, "%.2f rad"))
        { config.DeathCamFoV = fov; changed = true; }

        var tilt = config.DeathCamTilt;
        if (ImGui.DragFloat("Tilt (Roll)", ref tilt, 0.005f, -MathF.PI, MathF.PI, "%.2f rad"))
        { config.DeathCamTilt = tilt; changed = true; }

        ImGui.Spacing();

        // --- Offset ---
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Offset");
        ImGui.Separator();

        var heightOffset = config.DeathCamHeightOffset;
        if (ImGui.DragFloat("Height", ref heightOffset, 0.02f, -5.0f, 10.0f, "%.2f"))
        { config.DeathCamHeightOffset = heightOffset; changed = true; }

        var sideOffset = config.DeathCamSideOffset;
        if (ImGui.DragFloat("Side", ref sideOffset, 0.02f, -5.0f, 5.0f, "%.2f"))
        { config.DeathCamSideOffset = sideOffset; changed = true; }

        ImGui.Spacing();

        // --- Quick Actions ---
        if (ImGui.Button("Reset All to Default"))
        {
            config.DeathCamAnchorDirH = 0;
            config.DeathCamAnchorDirV = 0;
            config.DeathCamAnchorDistance = 5.0f;
            config.DeathCamFoV = 0.78f;
            config.DeathCamTilt = 0f;
            config.DeathCamHeightOffset = 0f;
            config.DeathCamSideOffset = 0f;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Close Preview"))
        {
            deathCamController.SetPreview(false);
        }

        if (changed)
            config.Save();

        ImGui.End();
    }

    private void DrawActiveCamSection()
    {
        if (ImGui.CollapsingHeader("Active Camera"))
        {
            ImGui.Indent();

            var enabled = config.EnableActiveCamera;
            if (ImGui.Checkbox("Enable##activecam", ref enabled))
            {
                config.EnableActiveCamera = enabled;
                config.Save();
                activeCameraController.SetActive(enabled);
            }
            HelpMarker("Camera tracks the selected bone. You control rotation and zoom freely with mouse/keyboard.");

            if (config.EnableActiveCamera)
            {
                // Bone selector
                var bones = ActiveCameraController.CenterBones;
                var boneNames = new string[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                    boneNames[b] = bones[b].Label;

                int boneIdx = 0;
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b].BoneName == config.ActiveCameraBoneName) { boneIdx = b; break; }

                if (ImGui.Combo("Center Bone##activecam", ref boneIdx, boneNames, boneNames.Length))
                {
                    config.ActiveCameraBoneName = bones[boneIdx].BoneName;
                    config.Save();
                }

                // Height / Side offsets
                var height = config.ActiveCameraHeightOffset;
                if (ImGui.DragFloat("Height Offset##activecam", ref height, 0.01f, -5f, 10f, "%.2f"))
                {
                    config.ActiveCameraHeightOffset = height;
                    config.Save();
                }

                var side = config.ActiveCameraSideOffset;
                if (ImGui.DragFloat("Side Offset##activecam", ref side, 0.01f, -5f, 5f, "%.2f"))
                {
                    config.ActiveCameraSideOffset = side;
                    config.Save();
                }

                // Vertical angle lock
                var lockV = config.ActiveCameraLockVertical;
                if (ImGui.Checkbox("Lock Vertical Angle##activecam", ref lockV))
                {
                    config.ActiveCameraLockVertical = lockV;
                    config.Save();
                }
                HelpMarker("Lock the vertical camera angle to the value below. Mouse/keyboard vertical input is ignored.");

                if (config.ActiveCameraLockVertical)
                {
                    var vAngle = config.ActiveCameraVerticalAngle;
                    if (ImGui.DragFloat("Vertical Angle##activecam", ref vAngle, 0.01f, -MathF.PI / 2f, MathF.PI / 2f, "%.2f rad"))
                    {
                        config.ActiveCameraVerticalAngle = vAngle;
                        config.Save();
                    }
                }

                // Disable collision
                var noCollision = config.ActiveCameraDisableCollision;
                if (ImGui.Checkbox("Disable Camera Collision##activecam", ref noCollision))
                {
                    config.ActiveCameraDisableCollision = noCollision;
                    config.Save();
                }
                HelpMarker("Camera passes through walls and objects.");

                var closeZoom = config.ActiveCameraCloseZoom;
                if (ImGui.Checkbox("Allow Close Zoom##activecam", ref closeZoom))
                {
                    config.ActiveCameraCloseZoom = closeZoom;
                    config.Save();
                }
                HelpMarker("Allow the camera to zoom closer than the game default.");
            }

            ImGui.Unindent();
        }
    }

    private void DrawRagdollSection()
    {
        if (ImGui.CollapsingHeader("Rag doll (Experimental)"))
        {
            var enabled = config.EnableRagdoll;
            if (ImGui.Checkbox("Enable Ragdoll##ragdoll", ref enabled))
            {
                config.EnableRagdoll = enabled;
                config.Save();
            }
            HelpMarker("Replace death animation with ragdoll physics after a configurable delay.");

            if (config.EnableRagdoll)
            {
                ImGui.Indent();

                // Ragdoll Now toggle
                var ragdollActive = ragdollController.IsActive;
                if (ImGui.Checkbox("Ragdoll Now", ref ragdollActive))
                {
                    if (ragdollActive)
                    {
                        var player = clientState.LocalPlayer;
                        if (player != null)
                            ragdollController.Activate(player.Address);
                    }
                    else
                    {
                        ragdollController.Deactivate();
                    }
                }
                HelpMarker("Instantly toggle ragdoll on the player character.");

                ImGui.Separator();

                var delay = config.RagdollActivationDelay;
                if (ImGui.SliderFloat("Activation Delay (s)##ragdoll", ref delay, 0.0f, 10.0f, "%.1f"))
                {
                    config.RagdollActivationDelay = delay;
                    config.Save();
                }
                HelpMarker("Seconds after death before ragdoll physics take over.");

                var gravity = config.RagdollGravity;
                if (ImGui.SliderFloat("Gravity##ragdoll", ref gravity, 0.1f, 30.0f, "%.1f"))
                {
                    config.RagdollGravity = gravity;
                    config.Save();
                }

                var damping = config.RagdollDamping;
                if (ImGui.SliderFloat("Damping##ragdoll", ref damping, 0.8f, 1.0f, "%.3f"))
                {
                    config.RagdollDamping = damping;
                    config.Save();
                }
                HelpMarker("Velocity damping per frame. Lower = more energy loss.");

                var floorOffset = config.RagdollFloorOffset;
                if (ImGui.SliderFloat("Floor Offset##ragdoll", ref floorOffset, 0.0f, 0.5f, "%.2f"))
                {
                    config.RagdollFloorOffset = floorOffset;
                    config.Save();
                }
                HelpMarker("Lower the terrain ground by this amount. Prevents joints from starting at floor level, which causes bad constraint solving.");

                var selfCollision = config.RagdollSelfCollision;
                if (ImGui.Checkbox("Self Collision##ragdoll", ref selfCollision))
                {
                    config.RagdollSelfCollision = selfCollision;
                    config.Save();
                }
                HelpMarker("Body parts collide with each other (arms vs torso, legs vs legs). Prevents clipping but may cause slight stretching. Takes effect on next ragdoll activation.");

                var tightKnees = config.RagdollTightKneeLimits;
                if (ImGui.Checkbox("Tight Knee Limits##ragdoll", ref tightKnees))
                {
                    config.RagdollTightKneeLimits = tightKnees;
                    config.Save();
                }
                HelpMarker("Use tight swing limits on knees and elbows. Prevents hyperextension but reduces range of motion (guided bend). Takes effect on next ragdoll activation.");

                ImGui.Separator();
                if (ImGui.Button("Reset to Defaults##ragdoll"))
                {
                    config.RagdollActivationDelay = 1.0f;
                    config.RagdollGravity = 9.8f;
                    config.RagdollDamping = 0.97f;
                    config.RagdollFloorOffset = 0f;
                    config.RagdollSelfCollision = true;
                    config.RagdollTightKneeLimits = false;
                    config.RagdollMassScale = 1.0f;
                    config.Save();
                }

                ImGui.Unindent();
            }
        }
    }

    private void DrawNpcCollisionSection()
    {
        if (ImGui.CollapsingHeader("NPC Collision (Experimental)"))
        {
            var npcCollision = config.RagdollNpcCollision;
            if (ImGui.Checkbox("Enable NPC Collision##npccol", ref npcCollision))
            {
                config.RagdollNpcCollision = npcCollision;
                config.Save();
            }
            HelpMarker("Active combat targets have per-bone collision volumes for ragdoll interaction.");

            if (config.RagdollNpcCollision)
            {
                ImGui.Indent();

                var npcScale = config.RagdollNpcCollisionScale;
                if (ImGui.SliderFloat("NPC Collision Scale##npccol", ref npcScale, 0.0001f, 5.0f, "%.4f"))
                {
                    config.RagdollNpcCollisionScale = npcScale;
                    config.Save();
                }
                HelpMarker("Scale multiplier for NPC bone collision capsules. Increase for larger NPC models.");

                ImGui.Separator();
                if (ImGui.Button("Reset to Defaults##npccol"))
                {
                    config.RagdollNpcCollision = false;
                    config.RagdollNpcCollisionScale = 0.0001f;
                    config.Save();
                }

                ImGui.Unindent();
            }
        }
    }

    private void DrawDevSection()
    {
        if (ImGui.CollapsingHeader("Dev (Experimental)"))
        {
            if (!devUnlocked)
            {
                ImGui.BeginDisabled();
                var dummy = false;
                ImGui.Checkbox("Unlock experimental features", ref dummy);
                ImGui.EndDisabled();

                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    var mousePos = ImGui.GetMousePos();
                    if (mousePos.X >= min.X && mousePos.X <= max.X &&
                        mousePos.Y >= min.Y && mousePos.Y <= max.Y)
                    {
                        devClickCount++;
                        if (devClickCount >= 13)
                            devUnlocked = true;
                    }
                }
                return;
            }

            ImGui.Indent();

            var massScale = config.RagdollMassScale;
            if (ImGui.SliderFloat("Mass Scale##dev", ref massScale, 0.1f, 10.0f, "%.1f"))
            {
                config.RagdollMassScale = massScale;
                config.Save();
            }
            HelpMarker("Ragdoll body mass multiplier. Higher = heavier (resists collision). Lower = lighter (flies on impact).");

            ImGui.Unindent();
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

    public void DrawDeathCamToolbar()
    {
        var presets = config.DeathCamPresets;
        if (presets.Count == 0)
        {
            ImGui.SetNextWindowSize(new Vector2(200, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Death Cam Presets", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextDisabled("No presets saved.");
            }
            ImGui.End();
            return;
        }

        if (selectedPresetIndex < 0 || selectedPresetIndex >= presets.Count)
            selectedPresetIndex = 0;

        ImGui.SetNextWindowSize(new Vector2(280, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Death Cam Presets", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var arrowSize = new Vector2(28, 28);

        var atStart = selectedPresetIndex <= 0;
        if (atStart) ImGui.BeginDisabled();
        if (ImGui.Button("<", arrowSize))
        {
            selectedPresetIndex--;
            LoadPreset(presets[selectedPresetIndex]);
        }
        if (atStart) ImGui.EndDisabled();

        ImGui.SameLine();

        var presetName = presets[selectedPresetIndex].Name;
        var avail = ImGui.GetContentRegionAvail().X - arrowSize.X - ImGui.GetStyle().ItemSpacing.X;
        var textSize = ImGui.CalcTextSize(presetName).X;
        var pad = (avail - textSize) * 0.5f;
        if (pad > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
        ImGui.AlignTextToFramePadding();
        ImGui.Text(presetName);

        ImGui.SameLine();
        if (pad > 0) ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - arrowSize.X);

        var atEnd = selectedPresetIndex >= presets.Count - 1;
        if (atEnd) ImGui.BeginDisabled();
        if (ImGui.Button(">", arrowSize))
        {
            selectedPresetIndex++;
            LoadPreset(presets[selectedPresetIndex]);
        }
        if (atEnd) ImGui.EndDisabled();

        ImGui.End();
    }

    public void DrawActiveCamToolbar()
    {
        if (!ImGui.Begin("Active Cam", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        // Bone selector (compact combo)
        var bones = ActiveCameraController.CenterBones;
        int boneIdx = 0;
        for (int b = 0; b < bones.Length; b++)
            if (bones[b].BoneName == config.ActiveCameraBoneName) { boneIdx = b; break; }

        var boneLabels = new string[bones.Length];
        for (int b = 0; b < bones.Length; b++)
            boneLabels[b] = bones[b].Label;

        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("##acbone", ref boneIdx, boneLabels, boneLabels.Length))
        {
            config.ActiveCameraBoneName = bones[boneIdx].BoneName;
            config.Save();
        }

        // Height offset
        ImGui.SameLine();
        var height = config.ActiveCameraHeightOffset;
        ImGui.SetNextItemWidth(60);
        if (ImGui.DragFloat("H##actb", ref height, 0.01f, -5f, 10f, "%.2f"))
        {
            config.ActiveCameraHeightOffset = height;
            config.Save();
        }

        // Side offset
        ImGui.SameLine();
        var side = config.ActiveCameraSideOffset;
        ImGui.SetNextItemWidth(60);
        if (ImGui.DragFloat("S##actb", ref side, 0.01f, -5f, 5f, "%.2f"))
        {
            config.ActiveCameraSideOffset = side;
            config.Save();
        }

        // Close zoom toggle
        ImGui.SameLine();
        var closeZoom = config.ActiveCameraCloseZoom;
        if (ImGui.Checkbox("Zoom##actb", ref closeZoom))
        {
            config.ActiveCameraCloseZoom = closeZoom;
            config.Save();
        }

        ImGui.End();
    }

    public void Dispose()
    {
    }
}
