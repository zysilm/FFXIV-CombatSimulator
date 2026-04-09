using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Gui;

public class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly NpcSpawner npcSpawner;
    private readonly CombatEngine combatEngine;
    private readonly GlamourerIpc glamourerIpc;
    private readonly AnimationController animationController;
    private readonly RagdollController ragdollController;
    private readonly DeathCamController deathCamController;
    private readonly ActiveCameraController activeCameraController;
    private readonly HookSafetyChecker hookSafetyChecker;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly Dev.VictorySequenceGui victorySequenceGui;
    private readonly IPluginLog log;

    // Conflict confirmation popup
    private bool showConflictConfirmPopup;

    // Model override state
    private int modelOverrideId = 0;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;

    // Skeleton bone cache for ragdoll advanced UI
    private string[] skeletonBoneNames = Array.Empty<string>();
    private Dictionary<string, string?> skeletonBoneParents = new();
    private bool skeletonBonesLoaded;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    // Emote list cache for target victory dropdown
    private List<(uint Id, string Name)>? emoteListCache;
    private int targetVictoryEmoteIndex = -1;

    // Death cam preset state
    private string newPresetName = "";
    private int selectedPresetIndex = -1;
    private bool overwritePopupOpen = false;
    private string overwriteTargetName = "";

    // Dev easter egg state
    private int devClickCount = 0;
    private bool devUnlocked = false;

    // Virtual Enemies section state
    private NpcCatalog? npcCatalog;
    private string spawnSearchFilter = "";
    private int spawnCategoryIndex = 0; // 0=Popular, 1=Recent, 2=All
    private int selectedCatalogIndex = -1;
    private NpcCatalogEntry? selectedCatalogEntry;
    private int virtualEnemiesClickCount = 0;
    private bool virtualEnemiesUnlocked = false;

    private static readonly string[] SpawnDirectionNames = { "Front", "Behind", "Left", "Right" };
    private static readonly string[] SpawnCategoryNames = { "Popular", "Recent", "Human", "All" };

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
        NpcSpawner npcSpawner,
        CombatEngine combatEngine,
        GlamourerIpc glamourerIpc,
        AnimationController animationController,
        RagdollController ragdollController,
        DeathCamController deathCamController,
        ActiveCameraController activeCameraController,
        HookSafetyChecker hookSafetyChecker,
        IClientState clientState,
        IDataManager dataManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.npcSpawner = npcSpawner;
        this.combatEngine = combatEngine;
        this.glamourerIpc = glamourerIpc;
        this.animationController = animationController;
        this.ragdollController = ragdollController;
        this.deathCamController = deathCamController;
        this.activeCameraController = activeCameraController;
        this.hookSafetyChecker = hookSafetyChecker;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.log = log;
        this.victorySequenceGui = new Dev.VictorySequenceGui(config, npcSelector, log);
    }

    private int selectedTab = 0;
    /// <summary>Name of the bone currently being edited in the advanced UI (for overlay highlighting).</summary>
    public string? EditingBoneName { get; private set; }
    /// <summary>Which parameter is being actively dragged (for joint limit visualization).</summary>
    public enum EditParam { None, Swing, TwistMin, TwistMax }
    public EditParam EditingParameter { get; private set; }

    private static readonly string[] TabNames = new[]
    {
        "Combat",
        "Targets",
        "Animation",
        "Camera",
        "Ragdoll",
        "Ragdoll (Adv)",
        "Virtual Enemies",
        "Settings",
        "Diagnose",
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
                DrawNpcSettleCollisionSection();
                ImGui.Separator();
                if (ImGui.Button("Reset All to Defaults##ragdollpage"))
                {
                    // Ragdoll
                    config.RagdollActivationDelay = 1.0f;
                    config.RagdollGravity = 9.8f;
                    config.RagdollDamping = 0.97f;
                    config.RagdollSolverIterations = 8;
                    config.RagdollSelfCollision = true;
                    config.RagdollFriction = 1.0f;
                    config.RagdollWeaponDrop = true;
                    config.RagdollHairPhysics = false;
                    config.RagdollHairGravityStrength = 0.5f;
                    config.RagdollHairDamping = 0.92f;
                    config.RagdollHairStiffness = 0.1f;
                    // NPC Collision
                    config.RagdollNpcCollision = true;
                    config.RagdollNpcCollisionScale = 0.0001f;
                    // NPC Collision (Settle)
                    config.RagdollNpcSettleCollision = true;
                    config.Save();
                }
                break;
            case 5: // Ragdoll (Advanced)
                DrawRagdollAdvancedSection();
                break;
            case 6: // Virtual Enemies
                DrawVirtualEnemiesTab();
                break;
            case 7: // Settings
                DrawGuiSettingsSection();
                DrawDevSection();
                break;
            case 8: // Diagnose
                DrawDiagnoseSection();
                break;
        }
        ImGui.EndChild();

        // Conflict confirmation popup (must be at window scope, not inside a child)
        DrawConflictConfirmPopup();

        ImGui.End();
    }

    private void DrawStatusSection()
    {
        var simActive = combatEngine.IsActive;
        var hasConflicts = hookSafetyChecker.HasConflicts;

        ImGui.Text("Status:");
        ImGui.SameLine();
        if (simActive && hasConflicts)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "Active — Conflict Detected");
        }
        else if (simActive)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Active");
        }
        else if (hasConflicts)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "Inactive — Conflict Detected");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Inactive");
        }

        if (hasConflicts)
        {
            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 25.0f);
                ImGui.TextUnformatted(
                    "Other plugins are hooking native functions used by Combat Simulator. " +
                    "This may cause crashes during combat.\n\n" +
                    "Go to the Diagnose tab to see details and " +
                    "consider deactivating conflicting plugins.");
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
            if (ImGui.IsItemClicked())
                selectedTab = 8; // Jump to Diagnose tab
        }

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

    private void DrawVirtualEnemiesTab()
    {
        // 6-click activation gate (resets every time mode is turned off or plugin loads)
        if (!virtualEnemiesUnlocked)
        {
            ImGui.TextWrapped("Virtual Enemies allows you to spawn client-side enemies to practice combat against.");

            ImGui.Spacing();
            ImGui.BeginDisabled();
            var dummy = false;
            ImGui.Checkbox("Enable Virtual Enemies Mode", ref dummy);
            ImGui.EndDisabled();

            // The (!) icon IS the click target
            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(new Vector4(1f, 0.7f, 0f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            // Track clicks on the (!) icon
            if (ImGui.IsItemClicked())
            {
                virtualEnemiesClickCount++;
                if (virtualEnemiesClickCount >= 6)
                    virtualEnemiesUnlocked = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20.0f);
                ImGui.TextUnformatted(
                    "EXPERIMENTAL: This feature spawns client-side objects and intercepts " +
                    "combat actions. It modifies game memory in ways that may cause crashes " +
                    "or unexpected behavior.\n\n" +
                    "Click this icon 6 times to acknowledge the risk and unlock.");
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            if (virtualEnemiesClickCount > 0 && virtualEnemiesClickCount < 6)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                    $"({virtualEnemiesClickCount}/6)");
            }
            else if (virtualEnemiesClickCount == 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                    "Click the warning icon to unlock.");
            }

            return;
        }

        // Lazy-load catalog on first open
        npcCatalog ??= new NpcCatalog(dataManager, log);

        // On/Off toggle
        var spawnMode = npcSpawner.SpawnModeActive;
        if (ImGui.Checkbox("Virtual Enemies Mode", ref spawnMode))
        {
            npcSpawner.SpawnModeActive = spawnMode;
            if (spawnMode)
            {
                // Auto-start combat sim when mode turns on
                if (!combatEngine.IsActive)
                {
                    combatEngine.StartSimulation();
                    chatGui.Print("[CombatSim] Virtual Enemies mode ON. Combat simulation started.");
                }
            }
            else
            {
                // Despawn all and stop combat when turning off
                foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
                    npcSelector.UnregisterSpawnedNpc(npc);
                npcSpawner.DespawnAll();
                if (combatEngine.IsActive)
                {
                    combatEngine.StopSimulation();
                    chatGui.Print("[CombatSim] Virtual Enemies mode OFF. All enemies despawned.");
                }

                // Re-lock: require 6 clicks again next time
                virtualEnemiesUnlocked = false;
                virtualEnemiesClickCount = 0;
            }
        }
        HelpMarker(
            "EXPERIMENTAL: When ON, virtual enemies are automatically targeted.\n" +
            "All your skills will hit the last spawned enemy.\n" +
            "Turning OFF despawns all enemies and stops combat.\n\n" +
            "This feature modifies game memory and may cause crashes.");

        ImGui.SameLine();
        if (spawnMode)
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Active");
        else
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "Inactive");

        if (!spawnMode)
        {
            ImGui.TextDisabled("Enable Virtual Enemies Mode to spawn and fight enemies.");
            return;
        }

        ImGui.Separator();

        // Category tabs: Popular / Recent / All
        for (int c = 0; c < SpawnCategoryNames.Length; c++)
        {
            if (c > 0) ImGui.SameLine();
            bool selected = spawnCategoryIndex == c;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.SmallButton(SpawnCategoryNames[c]))
            {
                spawnCategoryIndex = c;
                selectedCatalogIndex = -1;
                selectedCatalogEntry = null;
            }
            if (selected) ImGui.PopStyleColor();
        }

        // Search filter (Human / All)
        if (spawnCategoryIndex >= 2)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##npcSearch", "Search NPC name...", ref spawnSearchFilter, 256);
        }

        // Get entries for current category
        IReadOnlyList<NpcCatalogEntry> entries = spawnCategoryIndex switch
        {
            0 => npcCatalog.GetPopularEntries(),
            1 => npcCatalog.GetRecentEntries(config.RecentNpcEntries),
            2 => npcCatalog.Search(spawnSearchFilter, NpcCatalogType.Human),
            3 => npcCatalog.Search(spawnSearchFilter),
            _ => Array.Empty<NpcCatalogEntry>(),
        };

        // NPC list box
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 15;
        if (ImGui.BeginListBox("##npcList", new Vector2(-1, listHeight)))
        {
            if (entries.Count == 0)
            {
                ImGui.TextDisabled(spawnCategoryIndex == 1 ? "No recent NPCs." : "No results.");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    bool isSelected = selectedCatalogEntry != null && selectedCatalogEntry.Id == entry.Id && selectedCatalogEntry.Type == entry.Type;
                    if (ImGui.Selectable($"{entry.Name}##cat{entry.Type}{entry.Id}", isSelected))
                    {
                        selectedCatalogIndex = i;
                        selectedCatalogEntry = entry;
                    }
                }
            }
            ImGui.EndListBox();
        }

        // Spawn settings
        ImGui.Spacing();
        var dir = config.SpawnDirection;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Direction", ref dir, SpawnDirectionNames, SpawnDirectionNames.Length))
        {
            config.SpawnDirection = dir;
            config.Save();
        }
        HelpMarker("Direction relative to your character's facing.");

        ImGui.SameLine();
        var dist = config.SpawnDistance;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##spawnDist", ref dist, 1.0f, 15.0f, "%.1f yalms"))
        {
            config.SpawnDistance = dist;
            config.Save();
        }

        // Behavior
        var behaviorIdx = config.DefaultNpcBehaviorType;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("Behavior##spawn", ref behaviorIdx, BehaviorNames, BehaviorNames.Length))
        {
            config.DefaultNpcBehaviorType = behaviorIdx;
            config.Save();
        }

        // Level + HP multiplier (inline)
        var defaultLevel = config.DefaultNpcLevel;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Level", ref defaultLevel))
        {
            config.DefaultNpcLevel = Math.Clamp(defaultLevel, 1, 200);
            config.Save();
        }

        ImGui.SameLine();
        var hpMult = config.DefaultNpcHpMultiplier;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("HP Mult", ref hpMult, 0.1f, 10.0f, "%.1f"))
        {
            config.DefaultNpcHpMultiplier = hpMult;
            config.Save();
        }

        // Spawn button + counter
        ImGui.Spacing();
        bool canSpawn = selectedCatalogEntry != null && npcSpawner.TotalCount < npcSpawner.MaxNpcs;
        if (!canSpawn) ImGui.BeginDisabled();
        if (ImGui.Button("Spawn", new Vector2(80, 0)))
        {
            var entry = selectedCatalogEntry!;
            var pos = CalculateSpawnPosition(config.SpawnDirection, config.SpawnDistance);

            npcSpawner.QueueSpawn(new NpcSpawnRequest
            {
                BNpcBaseId = entry.Type == NpcCatalogType.BNpc ? entry.Id : 0,
                BNpcNameId = entry.BNpcNameId,
                ENpcBaseId = entry.Type is NpcCatalogType.ENpc or NpcCatalogType.Human ? entry.Id : 0,
                Level = config.DefaultNpcLevel,
                HpMultiplier = config.DefaultNpcHpMultiplier,
                Position = pos,
                BehaviorType = (NpcBehaviorType)config.DefaultNpcBehaviorType,
            });

            // Track in recent list (avoid duplicates, keep last 20)
            config.RecentNpcEntries.RemoveAll(r => r.BNpcBaseId == entry.Id);
            config.RecentNpcEntries.Insert(0, new RecentNpcEntry
            {
                BNpcBaseId = entry.Id,
                BNpcNameId = entry.BNpcNameId,
            });
            if (config.RecentNpcEntries.Count > 20)
                config.RecentNpcEntries.RemoveRange(20, config.RecentNpcEntries.Count - 20);
            config.Save();

            chatGui.Print($"[CombatSim] Spawning {entry.Name}...");
        }
        if (!canSpawn) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled($"{npcSpawner.SpawnedNpcs.Count}/{npcSpawner.MaxNpcs} spawned");

        if (npcSpawner.PendingCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"({npcSpawner.PendingCount} pending...)");
        }

        // Spawned NPCs list with HP bars and despawn buttons
        if (npcSpawner.SpawnedNpcs.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Spawned:");

            for (int i = npcSpawner.SpawnedNpcs.Count - 1; i >= 0; i--)
            {
                var npc = npcSpawner.SpawnedNpcs[i];
                ImGui.PushID($"spawned_{i}");

                // HP bar
                if (npc.State.MaxHp > 0)
                {
                    float hp = (float)npc.State.CurrentHp / npc.State.MaxHp;
                    var hpColor = hp > 0.5f ? new Vector4(0, 0.8f, 0, 1)
                        : hp > 0.25f ? new Vector4(0.8f, 0.8f, 0, 1)
                        : new Vector4(0.8f, 0, 0, 1);
                    if (!npc.IsAlive) hpColor = new Vector4(0.4f, 0.4f, 0.4f, 1);
                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, hpColor);
                    ImGui.ProgressBar(hp, new Vector2(ImGui.GetContentRegionAvail().X - 70, 0),
                        $"{npc.State.CurrentHp:N0}/{npc.State.MaxHp:N0}");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                }

                ImGui.Text(npc.Name);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 55);
                if (ImGui.SmallButton("Despawn"))
                {
                    npcSelector.UnregisterSpawnedNpc(npc);
                    npcSpawner.DespawnNpc(npc);
                }

                ImGui.PopID();
            }

            if (npcSpawner.SpawnedNpcs.Count > 1)
            {
                if (ImGui.SmallButton("Despawn All"))
                {
                    foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
                        npcSelector.UnregisterSpawnedNpc(npc);
                    npcSpawner.DespawnAll();
                }
            }
        }
    }

    private Vector3 CalculateSpawnPosition(int directionIndex, float distance)
    {
        var player = clientState.LocalPlayer;
        if (player == null) return Vector3.Zero;

        var playerPos = player.Position;
        var playerRot = player.Rotation;

        // FFXIV: -Sin(rot) = forward X, -Cos(rot) = forward Z
        float angle = directionIndex switch
        {
            0 => playerRot,                    // Front
            1 => playerRot + MathF.PI,          // Behind
            2 => playerRot + MathF.PI / 2f,     // Left
            3 => playerRot - MathF.PI / 2f,     // Right
            _ => playerRot,
        };

        var dir = new Vector3(-MathF.Sin(angle), 0, -MathF.Cos(angle));
        return playerPos + dir * distance;
    }

    private void DrawNpcDefaultsSection()
    {
        if (ImGui.CollapsingHeader("NPC Defaults"))
        {
            var defaultLevel = config.DefaultNpcLevel;
            if (ImGui.SliderInt("Default NPC Level", ref defaultLevel, 1, 200))
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
            var npcTarget = config.EnableNpcTargetPlayer;
            if (ImGui.Checkbox("NPCs Target Player", ref npcTarget))
            {
                config.EnableNpcTargetPlayer = npcTarget;
                config.Save();
            }
            HelpMarker("Active NPCs visually target the player during combat. Affects head tracking, emote interactions, and combat stance direction.");

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
                    if (hookSafetyChecker.HasConflicts)
                    {
                        showConflictConfirmPopup = true;
                    }
                    else
                    {
                        combatEngine.StartSimulation();
                        chatGui.Print("[CombatSim] Combat simulation started.");
                    }
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

            // Target Victory: emote played on each surviving NPC when the player dies
            if (emoteListCache == null)
            {
                emoteListCache = new List<(uint, string)> { (0, "(None)") };
                try
                {
                    var emoteSheet = Core.Services.DataManager.GetExcelSheet<Emote>();
                    if (emoteSheet != null)
                    {
                        var emotes = new List<(uint Id, string Name)>();
                        foreach (var emote in emoteSheet)
                        {
                            var name = emote.Name.ToString();
                            if (!string.IsNullOrWhiteSpace(name))
                                emotes.Add((emote.RowId, $"{name} [{emote.RowId}]"));
                        }
                        emotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        emoteListCache.AddRange(emotes);
                    }
                }
                catch { }
            }

            // Find current selection index
            if (targetVictoryEmoteIndex < 0)
            {
                targetVictoryEmoteIndex = 0;
                for (int i = 0; i < emoteListCache.Count; i++)
                {
                    if (emoteListCache[i].Id == config.TargetVictoryEmoteId)
                    { targetVictoryEmoteIndex = i; break; }
                }
            }

            var currentEmoteName = targetVictoryEmoteIndex < emoteListCache.Count
                ? emoteListCache[targetVictoryEmoteIndex].Name : "(None)";
            if (ImGui.BeginCombo("Target Victory", currentEmoteName))
            {
                for (int i = 0; i < emoteListCache.Count; i++)
                {
                    var isSelected = i == targetVictoryEmoteIndex;
                    if (ImGui.Selectable(emoteListCache[i].Name, isSelected))
                    {
                        targetVictoryEmoteIndex = i;
                        config.TargetVictoryEmoteId = emoteListCache[i].Id;
                        config.Save();
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            HelpMarker("Emote played on each surviving NPC when the player dies. NPCs perform this emote toward the player.");
        }
    }

    private void DrawHitVfxSection()
    {
        if (ImGui.CollapsingHeader("VFX"))
        {
            var skillVfx = config.EnableSkillVfx;
            if (ImGui.Checkbox("Enable Skill VFX", ref skillVfx))
            {
                config.EnableSkillVfx = skillVfx;
                config.Save();
            }
            HelpMarker("Spawn per-skill visual effects (cast circles, impact particles) during combat.");

            if (config.EnableSkillVfx && hookSafetyChecker.HasConflicts)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "! Check Diagnose");
            }

            ImGui.Spacing();

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

    private void DrawConflictConfirmPopup()
    {
        if (showConflictConfirmPopup)
        {
            ImGui.OpenPopup("##ConflictConfirm");
            showConflictConfirmPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0));
        var popupOpen = true;
        if (ImGui.BeginPopupModal("##ConflictConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "Plugin Conflict Detected");

            ImGui.Spacing();
            ImGui.TextWrapped(
                "Other plugins are hooking native functions used by Combat Simulator. " +
                "This may cause the game to crash during combat.\n\n" +
                "Check the Diagnose tab to see which plugins are conflicting " +
                "and consider deactivating them before starting.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Start with Conflicts", new Vector2(150, 0)))
            {
                combatEngine.StartSimulation();
                chatGui.Print("[CombatSim] Combat simulation started.");
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Go to Diagnose", new Vector2(120, 0)))
            {
                selectedTab = 8;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawRagdollAdvancedSection()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Per-Bone Physics Parameters");
        ImGui.TextWrapped("Toggle bones on/off for physics. Adjust rotation limits, capsule volume, and mass.");
        ImGui.Spacing();

        var debugOverlay = config.RagdollDebugOverlay;
        if (ImGui.Checkbox("Show Debug Overlay##ragdollAdv", ref debugOverlay))
        {
            config.RagdollDebugOverlay = debugOverlay;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Renders capsules and joints in 3D.");
        ImGui.Spacing();

        // Quick toggle for weapon holster/sheathe bones
        {
            var bukiBones = new[] { "j_buki_kosi_l", "j_buki_kosi_r", "j_buki2_kosi_l", "j_buki2_kosi_r", "j_buki_sebo_l", "j_buki_sebo_r" };
            bool anyOn = false;
            foreach (var b in config.RagdollBoneConfigs)
                if (Array.IndexOf(bukiBones, b.Name) >= 0 && b.Enabled) { anyOn = true; break; }

            var bukiEnabled = anyOn;
            if (ImGui.Checkbox("Sheathed Weapon Physics##ragdollAdv", ref bukiEnabled))
            {
                foreach (var b in config.RagdollBoneConfigs)
                    if (Array.IndexOf(bukiBones, b.Name) >= 0)
                        b.Enabled = bukiEnabled;
                config.Save();
                if (ragdollController.IsActive)
                {
                    var addr = ragdollController.TargetCharacterAddress;
                    ragdollController.Deactivate();
                    if (addr != nint.Zero) ragdollController.Activate(addr);
                }
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Toggle all j_buki holster/scabbard bones.");
        }
        ImGui.Spacing();

        if (ragdollController.IsActive)
        {
            if (ImGui.Button("Apply Changes (Reactivate Ragdoll)"))
            {
                var addr = ragdollController.TargetCharacterAddress;
                ragdollController.Deactivate();
                if (addr != nint.Zero)
                    ragdollController.Activate(addr);
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Press to apply.");
            ImGui.Spacing();
        }

        // Read skeleton bones from player character (once, or on refresh)
        if (!skeletonBonesLoaded)
            RefreshSkeletonBones();

        if (ImGui.Button("Refresh Bones"))
            RefreshSkeletonBones();
        ImGui.SameLine();
        ImGui.TextDisabled($"{skeletonBoneNames.Length} bones in skeleton");
        ImGui.Spacing();

        // Populate config from C# defaults + skeleton if empty
        if (config.RagdollBoneConfigs.Count == 0)
            PopulateBoneConfigsFromDefaults();

        // Sync: add any skeleton bones not yet in config (new bones get disabled defaults)
        SyncConfigWithSkeleton();

        if (ImGui.Button("Reset All to Defaults##boneconfigs"))
        {
            // Clear JSON → GetBoneDefs falls back to C# DefaultBoneDefs
            config.RagdollBoneConfigs.Clear();
            config.Save();
            // Re-populate UI from C# defaults
            PopulateBoneConfigsFromDefaults();
            if (ragdollController.IsActive)
            {
                var addr = ragdollController.TargetCharacterAddress;
                ragdollController.Deactivate();
                if (addr != nint.Zero)
                    ragdollController.Activate(addr);
            }
        }

        var enabledCount = 0;
        foreach (var b in config.RagdollBoneConfigs)
            if (b.Enabled) enabledCount++;
        ImGui.SameLine();
        ImGui.TextDisabled($"{enabledCount}/{config.RagdollBoneConfigs.Count} bones active");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var jointTypes = new[] { "Ball", "Hinge" };
        var changed = false;
        // EditingBoneName persists — only updated on value/toggle change

        for (int i = 0; i < config.RagdollBoneConfigs.Count; i++)
        {
            var bone = config.RagdollBoneConfigs[i];
            var id = $"##{bone.Name}";

            // Enable/disable checkbox
            var enabled = bone.Enabled;
            if (ImGui.Checkbox($"##en{bone.Name}", ref enabled))
            {
                bone.Enabled = enabled;
                changed = true;
                EditingBoneName = bone.Name;
                // Auto-reactivate so overlay updates immediately
                if (ragdollController.IsActive)
                {
                    config.Save();
                    var addr = ragdollController.TargetCharacterAddress;
                    ragdollController.Deactivate();
                    if (addr != nint.Zero)
                        ragdollController.Activate(addr);
                }
            }
            ImGui.SameLine();

            // Header color: bright for enabled, dim for disabled
            var headerColor = bone.Enabled
                ? new Vector4(0.9f, 0.95f, 1f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, headerColor);

            var headerLabel = bone.Enabled
                ? $"{bone.Name} ({(bone.JointType == 0 ? "Ball" : "Hinge")}){id}"
                : $"{bone.Name} (off){id}";

            var isOpen = ImGui.CollapsingHeader(headerLabel);
            ImGui.PopStyleColor();

            if (isOpen)
            {
                ImGui.Indent(10);

                if (bone.SkeletonParent != null)
                    ImGui.TextDisabled($"Skeleton parent: {bone.SkeletonParent}");

                if (bone.Enabled)
                {
                    var jt = bone.JointType;
                    if (ImGui.Combo($"Joint Type{id}", ref jt, jointTypes, jointTypes.Length))
                    { bone.JointType = jt; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var radius = bone.CapsuleRadius;
                    if (ImGui.SliderFloat($"Capsule Radius{id}", ref radius, 0.01f, 0.3f, "%.3f"))
                    { bone.CapsuleRadius = radius; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var halfLen = bone.CapsuleHalfLength;
                    if (ImGui.SliderFloat($"Capsule Half-Length{id}", ref halfLen, 0.0f, 0.3f, "%.3f"))
                    { bone.CapsuleHalfLength = halfLen; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var mass = bone.Mass;
                    if (ImGui.SliderFloat($"Mass{id}", ref mass, 0.1f, 15.0f, "%.1f"))
                    { bone.Mass = mass; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var swing = bone.SwingLimit;
                    if (ImGui.SliderFloat($"Swing Limit (rad){id}", ref swing, 0.0f, MathF.PI, "%.2f"))
                    { bone.SwingLimit = swing; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                    var twistMin = bone.TwistMinAngle;
                    if (ImGui.SliderFloat($"Twist Min (rad){id}", ref twistMin, -MathF.PI, 0f, "%.2f"))
                    { bone.TwistMinAngle = twistMin; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.TwistMin; }

                    var twistMax = bone.TwistMaxAngle;
                    if (ImGui.SliderFloat($"Twist Max (rad){id}", ref twistMax, 0f, MathF.PI, "%.2f"))
                    { bone.TwistMaxAngle = twistMax; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.TwistMax; }

                    // Soft body settings
                    var softBody = bone.SoftBody;
                    if (ImGui.Checkbox($"Soft Body##soft{bone.Name}", ref softBody))
                    { bone.SoftBody = softBody; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    ImGui.SameLine();
                    ImGui.TextDisabled("Bouncy spring physics (breast/jiggle)");

                    if (bone.SoftBody)
                    {
                        var ssFreq = bone.SoftSpringFreq;
                        if (ImGui.SliderFloat($"Spring Freq (Hz){id}", ref ssFreq, 1f, 30f, "%.1f"))
                        { bone.SoftSpringFreq = ssFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var ssDamp = bone.SoftSpringDamp;
                        if (ImGui.SliderFloat($"Spring Damping{id}", ref ssDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftSpringDamp = ssDamp; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var svFreq = bone.SoftServoFreq;
                        if (ImGui.SliderFloat($"Servo Freq (Hz){id}", ref svFreq, 1f, 20f, "%.1f"))
                        { bone.SoftServoFreq = svFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var svDamp = bone.SoftServoDamp;
                        if (ImGui.SliderFloat($"Servo Damping{id}", ref svDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftServoDamp = svDamp; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

                    // Reset this bone to its default
                    if (i < RagdollController.AllBoneDefaults.Length)
                    {
                        if (ImGui.SmallButton($"Reset{id}"))
                        {
                            var def = RagdollController.AllBoneDefaults[i];
                            bone.CapsuleRadius = def.CapsuleRadius;
                            bone.CapsuleHalfLength = def.CapsuleHalfLength;
                            bone.Mass = def.Mass;
                            bone.SwingLimit = def.SwingLimit;
                            bone.JointType = def.JointType;
                            bone.TwistMinAngle = def.TwistMinAngle;
                            bone.TwistMaxAngle = def.TwistMaxAngle;
                            bone.Enabled = def.Enabled;
                            bone.SoftBody = def.SoftBody;
                            bone.SoftSpringFreq = def.SoftSpringFreq;
                            bone.SoftSpringDamp = def.SoftSpringDamp;
                            bone.SoftServoFreq = def.SoftServoFreq;
                            bone.SoftServoDamp = def.SoftServoDamp;
                            changed = true;
                            EditingBoneName = bone.Name;
                        }
                    }
                }
                else
                {
                    ImGui.TextDisabled("Enable this bone to edit parameters.");
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }
        }

        if (changed)
            config.Save();
    }

    private unsafe void RefreshSkeletonBones()
    {
        skeletonBonesLoaded = true;
        var player = clientState.LocalPlayer;
        if (player == null) return;

        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        if (gameObj->DrawObject == null) return;
        var charBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;
        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return;

        var havokBones = pose->Skeleton->Bones;
        var parentIndices = pose->Skeleton->ParentIndices;
        var names = new List<string>();
        var parents = new Dictionary<string, string?>();

        for (int i = 0; i < havokBones.Length; i++)
        {
            var name = havokBones[i].Name.String;
            if (string.IsNullOrWhiteSpace(name)) continue;
            names.Add(name);

            string? parentName = null;
            if (i < parentIndices.Length)
            {
                var pi = parentIndices[i];
                if (pi >= 0 && pi < havokBones.Length)
                    parentName = havokBones[pi].Name.String;
            }
            parents[name] = parentName;
        }

        skeletonBoneNames = names.ToArray();
        skeletonBoneParents = parents;
    }

    /// <summary>Populate config from C# AllBoneDefaults (source of truth).</summary>
    private void PopulateBoneConfigsFromDefaults()
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var def in RagdollController.AllBoneDefaults)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(def));
        config.Save();
    }

    /// <summary>Add skeleton bones not yet in config. Never modifies existing entries.</summary>
    private void SyncConfigWithSkeleton()
    {
        if (skeletonBoneNames.Length == 0) return;

        var existing = new HashSet<string>();
        foreach (var c in config.RagdollBoneConfigs)
            existing.Add(c.Name);

        bool added = false;
        foreach (var boneName in skeletonBoneNames)
        {
            if (existing.Contains(boneName)) continue;

            skeletonBoneParents.TryGetValue(boneName, out var skelParent);

            // Check AllBoneDefaults for known defaults
            RagdollBoneConfig? known = null;
            foreach (var def in RagdollController.AllBoneDefaults)
                if (def.Name == boneName) { known = CloneBoneConfig(def); break; }

            if (known != null)
            {
                // Update skeleton parent from live data
                known.SkeletonParent = skelParent;
                config.RagdollBoneConfigs.Add(known);
            }
            else
            {
                // Unknown bone — safe disabled defaults
                config.RagdollBoneConfigs.Add(new RagdollBoneConfig
                {
                    Name = boneName,
                    SkeletonParent = skelParent,
                    Enabled = false,
                    CapsuleRadius = 0.03f,
                    CapsuleHalfLength = 0.03f,
                    Mass = 1.0f,
                    SwingLimit = 0.3f,
                    JointType = 0,
                    TwistMinAngle = -0.2f,
                    TwistMaxAngle = 0.2f,
                });
            }
            added = true;
        }

        if (added) config.Save();
    }

    private static RagdollBoneConfig CloneBoneConfig(RagdollBoneConfig src)
    {
        return new RagdollBoneConfig
        {
            Name = src.Name,
            SkeletonParent = src.SkeletonParent,
            Enabled = src.Enabled,
            CapsuleRadius = src.CapsuleRadius,
            CapsuleHalfLength = src.CapsuleHalfLength,
            Mass = src.Mass,
            SwingLimit = src.SwingLimit,
            JointType = src.JointType,
            TwistMinAngle = src.TwistMinAngle,
            TwistMaxAngle = src.TwistMaxAngle,
            Description = src.Description,
            SoftBody = src.SoftBody,
            SoftSpringFreq = src.SoftSpringFreq,
            SoftSpringDamp = src.SoftSpringDamp,
            SoftServoFreq = src.SoftServoFreq,
            SoftServoDamp = src.SoftServoDamp,
        };
    }

    private void DrawDiagnoseSection()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Plugin Conflict Diagnostics");
        ImGui.Separator();
        ImGui.Spacing();

        // Overall status
        if (hookSafetyChecker.HasConflicts)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "Conflict detected — other plugins hook functions we call");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "One or more native game functions that Combat Simulator calls are hooked by other plugins. " +
                "When Combat Simulator invokes these functions, the other plugin's hook code runs inside our call " +
                "and may crash if it encounters our simulated NPC actors.");
        }
        else
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), FontAwesomeIcon.Check.ToIconString());
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "No conflicts detected");
        }

        ImGui.Spacing();

        // Re-scan button
        if (ImGui.Button("Re-scan"))
            hookSafetyChecker.Scan();
        ImGui.SameLine();
        ImGui.TextDisabled("Re-check after enabling/disabling plugins.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Function table
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Native Functions");
        ImGui.Spacing();

        if (ImGui.BeginTable("##HookTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Function", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Hooked By", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var func in hookSafetyChecker.Functions)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(func.Name);

                ImGui.TableNextColumn();
                if (func.IsHooked)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "HOOKED");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Clean");
                }

                ImGui.TableNextColumn();
                if (func.IsHooked && !string.IsNullOrEmpty(func.HookedBy))
                {
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), func.HookedBy);
                }
                else if (func.IsHooked)
                {
                    ImGui.TextDisabled("(unknown module)");
                }
                else
                {
                    ImGui.TextDisabled("-");
                }

                ImGui.TableNextColumn();
                if (func.IsHooked)
                {
                    ImGui.TextWrapped(func.DetourInfo);
                }
                else
                {
                    ImGui.TextDisabled(func.Description);
                }
            }

            ImGui.EndTable();
        }

        if (hookSafetyChecker.Functions.Count == 0)
        {
            ImGui.TextDisabled("No native functions registered for checking.");
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

            var hpOcclusion = config.HpBarOcclusion;
            if (ImGui.Checkbox("Hide HP Bar Behind Player", ref hpOcclusion))
            {
                config.HpBarOcclusion = hpOcclusion;
                config.Save();
            }
            HelpMarker("Hide enemy HP bars when the player's body is between the camera and the HP bar.");

            if (config.ShowEnemyHpBar)
            {
                var enemyYOff = config.EnemyHpBarYOffset;
                if (ImGui.DragFloat("Enemy HP Bar Y Offset", ref enemyYOff, 0.05f, -2f, 5f, "%.2f"))
                {
                    config.EnemyHpBarYOffset = enemyYOff;
                    config.Save();
                }
                HelpMarker("Vertical offset from the enemy's head bone. Higher = bar floats higher.");
            }

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
            HelpMarker("Vertical offset from the player's head bone. Higher values move the bar up.");

            var hpXOffset = config.PlayerHpBarXOffset;
            if (ImGui.SliderFloat("Player HP Bar X Offset", ref hpXOffset, -500f, 500f, "%.0f"))
            {
                config.PlayerHpBarXOffset = hpXOffset;
                config.Save();
            }
            HelpMarker("Horizontal screen offset for the player HP bar. Positive = right, negative = left.");
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

                var minDist = config.ActiveCameraMinZoomDistance;
                if (ImGui.SliderFloat("Min Zoom Distance##activecam", ref minDist, 0.0f, 2.0f, "%.2f"))
                {
                    config.ActiveCameraMinZoomDistance = minDist;
                    config.Save();
                }
                HelpMarker("Minimum camera distance. Lower = closer zoom. Default 1.0.");

                var preventFade = config.ActiveCameraPreventFade;
                if (ImGui.Checkbox("Prevent Model Fade##activecam", ref preventFade))
                {
                    config.ActiveCameraPreventFade = preventFade;
                    config.Save();
                }
                HelpMarker("Prevent characters and NPCs from disappearing when the camera zooms very close.");
            }

            ImGui.Unindent();
        }
    }

    private void DrawRagdollSection()
    {
        if (ImGui.CollapsingHeader("Ragdoll"))
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

                var solverIter = config.RagdollSolverIterations;
                if (ImGui.SliderInt("Solver Iterations##ragdoll", ref solverIter, 4, 64))
                {
                    config.RagdollSolverIterations = solverIter;
                    config.Save();
                }
                HelpMarker("BEPU2 constraint solver iterations per timestep. Higher = more stable/accurate joints but costs performance. Default 8. Takes effect on next ragdoll activation.");

                var selfCollision = config.RagdollSelfCollision;
                if (ImGui.Checkbox("Self Collision##ragdoll", ref selfCollision))
                {
                    config.RagdollSelfCollision = selfCollision;
                    config.Save();
                }
                HelpMarker("Body parts collide with each other (arms vs torso, legs vs legs). Prevents clipping but may cause slight stretching. Takes effect on next ragdoll activation.");

                var friction = config.RagdollFriction;
                if (ImGui.SliderFloat("Friction##ragdoll", ref friction, 0.0f, 2.0f, "%.2f"))
                {
                    config.RagdollFriction = friction;
                    config.Save();
                }
                HelpMarker("Surface friction for all ragdoll contacts. 0 = ice (limbs slide freely), 1 = grippy (default). Lower values make the body slide more realistically. Takes effect on next ragdoll activation.");

                ImGui.Separator();
                ImGui.Text("Weapon Drop");

                var weaponDrop = config.RagdollWeaponDrop;
                if (ImGui.Checkbox("Enable Weapon Drop##ragdoll", ref weaponDrop))
                {
                    config.RagdollWeaponDrop = weaponDrop;
                    config.Save();
                }
                HelpMarker("Weapon detaches from the hand and falls with physics on death. Uses battle/dead animation instead of play-dead emote to keep weapons drawn. Takes effect on next ragdoll activation.");

                ImGui.Separator();
                ImGui.Text("Hair Physics");

                var hairPhysics = config.RagdollHairPhysics;
                if (ImGui.Checkbox("Enable Hair Physics##ragdoll", ref hairPhysics))
                {
                    config.RagdollHairPhysics = hairPhysics;
                    config.Save();
                }
                HelpMarker("Simulate hair draping under gravity during ragdoll. Takes effect on next ragdoll activation.");

                if (config.RagdollHairPhysics)
                {
                    var hairGravity = config.RagdollHairGravityStrength;
                    if (ImGui.SliderFloat("Hair Gravity##ragdoll", ref hairGravity, 0.0f, 1.0f, "%.2f"))
                    {
                        config.RagdollHairGravityStrength = hairGravity;
                        config.Save();
                    }

                    var hairDamping = config.RagdollHairDamping;
                    if (ImGui.SliderFloat("Hair Damping##ragdoll", ref hairDamping, 0.80f, 0.99f, "%.3f"))
                    {
                        config.RagdollHairDamping = hairDamping;
                        config.Save();
                    }

                    var hairStiffness = config.RagdollHairStiffness;
                    if (ImGui.SliderFloat("Hair Stiffness##ragdoll", ref hairStiffness, 0.0f, 0.5f, "%.2f"))
                    {
                        config.RagdollHairStiffness = hairStiffness;
                        config.Save();
                    }
                    HelpMarker("Resistance to gravity. Higher = stiffer hair that holds its shape.");
                }

                ImGui.Unindent();
            }
        }
    }

    private void DrawNpcCollisionSection()
    {
        if (ImGui.CollapsingHeader("NPC Collision"))
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

                ImGui.Unindent();
            }
        }
    }

    private void DrawNpcSettleCollisionSection()
    {
        if (ImGui.CollapsingHeader("NPC Collision (Settle)"))
        {
            var settleCol = config.RagdollNpcSettleCollision;
            if (ImGui.Checkbox("Enable Settle Collision##settle", ref settleCol))
            {
                config.RagdollNpcSettleCollision = settleCol;
                config.Save();
            }
            HelpMarker("Prevent ragdoll bodies from sleeping so they always react to NPC bones. Takes effect on next ragdoll activation.");
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

            var showGrabToolbar = config.ShowGrabToolbar;
            if (ImGui.Checkbox("Show Grab Toolbar##dev", ref showGrabToolbar))
            {
                config.ShowGrabToolbar = showGrabToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar to toggle grab and tweak force/speed on the current victory cinema stage.");

            var ragdollLog = config.RagdollVerboseLog;
            if (ImGui.Checkbox("Ragdoll Verbose Log##dev", ref ragdollLog))
            {
                config.RagdollVerboseLog = ragdollLog;
                config.Save();
            }

            var followPos = config.RagdollFollowPosition;
            if (ImGui.Checkbox("Ragdoll Follow Position##dev", ref followPos))
            {
                config.RagdollFollowPosition = followPos;
                config.Save();
            }
            HelpMarker("Update GameObject.Position to follow the ragdoll root bone each frame. Prevents character model from unloading on long falls (e.g., off cliffs). May have side effects.");

            var npcScale = config.DevNpcScale;
            if (ImGui.SliderFloat("NPC Scale##dev", ref npcScale, 0.1f, 3.0f, "%.2f"))
            {
                config.DevNpcScale = npcScale;
                config.Save();
            }
            HelpMarker("Scale all active target NPCs. Applied live each frame.");

            var occlusionHide = config.DevNpcOcclusionHide;
            if (ImGui.Checkbox("Hide Blocking NPCs##dev", ref occlusionHide))
            {
                config.DevNpcOcclusionHide = occlusionHide;
                config.Save();
            }
            HelpMarker("Hide active target NPCs that block the camera's view of the player. Only works with Active Camera enabled. Never hides the cinematic (grabbing) NPC.");
            if (config.DevNpcOcclusionHide)
            {
                ImGui.Indent();
                var occRadius = config.DevNpcOcclusionRadius;
                if (ImGui.SliderFloat("Occlusion Radius##dev", ref occRadius, 0.2f, 3.0f, "%.1f"))
                {
                    config.DevNpcOcclusionRadius = occRadius;
                    config.Save();
                }
                HelpMarker("How close an NPC must be to the camera-to-player line to be considered blocking.");
                ImGui.Unindent();
            }

            victorySequenceGui.Draw();

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
                if (hookSafetyChecker.HasConflicts)
                {
                    showConflictConfirmPopup = true;
                }
                else
                {
                    combatEngine.StartSimulation();
                    chatGui.Print("[CombatSim] Combat simulation started.");
                }
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

        // Conflict popup must be drawn at this window scope
        DrawConflictConfirmPopup();

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

        // Bone selector
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
        ImGui.Text("H");
        ImGui.SameLine();
        var height = config.ActiveCameraHeightOffset;
        ImGui.SetNextItemWidth(60);
        if (ImGui.DragFloat("##acH", ref height, 0.01f, -5f, 10f, "%.2f"))
        {
            config.ActiveCameraHeightOffset = height;
            config.Save();
        }

        // Side offset
        ImGui.SameLine();
        ImGui.Text("S");
        ImGui.SameLine();
        var side = config.ActiveCameraSideOffset;
        ImGui.SetNextItemWidth(60);
        if (ImGui.DragFloat("##acS", ref side, 0.01f, -5f, 5f, "%.2f"))
        {
            config.ActiveCameraSideOffset = side;
            config.Save();
        }

        // Min zoom distance
        ImGui.SameLine();
        ImGui.Text("Min");
        ImGui.SameLine();
        var minDist = config.ActiveCameraMinZoomDistance;
        ImGui.SetNextItemWidth(60);
        if (ImGui.DragFloat("##acMin", ref minDist, 0.01f, 0.0f, 2.0f, "%.2f"))
        {
            config.ActiveCameraMinZoomDistance = minDist;
            config.Save();
        }

        ImGui.End();
    }

    public void DrawGrabToolbar(Dev.VictorySequenceController victorySequenceController)
    {
        if (!ImGui.Begin("Grab", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var stages = config.VictorySequenceStages;
        var idx = victorySequenceController.CurrentStageIndex;

        if (!victorySequenceController.IsActive || idx < 0 || idx >= stages.Count)
        {
            ImGui.TextDisabled("No active stage");
            ImGui.End();
            return;
        }

        var s = stages[idx];
        ImGui.Text($"Stage {idx}");

        ImGui.SameLine();
        var grab = s.GrabEnabled;
        if (ImGui.Checkbox("Grab##tb", ref grab))
        { s.GrabEnabled = grab; config.Save(); }

        if (s.GrabEnabled)
        {
            ImGui.SameLine();
            ImGui.Text("F");
            ImGui.SameLine();
            var gf = s.GrabForce;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGF", ref gf, 10f, 10, 5000, "%.0f"))
            { s.GrabForce = gf; config.Save(); }

            ImGui.SameLine();
            ImGui.Text("S");
            ImGui.SameLine();
            var gs = s.GrabSpeed;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGS", ref gs, 1f, 1, 200, "%.0f"))
            { s.GrabSpeed = gs; config.Save(); }

            ImGui.SameLine();
            ImGui.Text("Hz");
            ImGui.SameLine();
            var gsf = s.GrabSpringFreq;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGHz", ref gsf, 5f, 10, 500, "%.0f"))
            { s.GrabSpringFreq = gsf; config.Save(); }
        }

        ImGui.End();
    }

    public void Dispose()
    {
    }
}
