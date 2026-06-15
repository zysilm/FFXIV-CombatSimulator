using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Recipes;
using CombatSimulator.Safety;
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
    private readonly NpcSpawner npcSpawner;
    private readonly CombatCompanionManager companionManager;
    private readonly CombatEngine combatEngine;
    private readonly MapEnemyController mapEnemyController;
    private readonly GlamourerIpc glamourerIpc;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly AnimationController animationController;
    private readonly RagdollController ragdollController;
    private readonly DeathCamController deathCamController;
    private readonly ActiveCameraController activeCameraController;
    private readonly HookSafetyChecker hookSafetyChecker;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly Dev.VictorySequenceGui victorySequenceGui;
    private readonly CombatRecipeBook recipeBook;
    private readonly IPluginLog log;

    // Conflict confirmation popup
    private bool showConflictConfirmPopup;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;
    private bool showControlsPopup;

    // Skeleton bone cache for ragdoll advanced UI
    private string[] skeletonBoneNames = Array.Empty<string>();
    private Dictionary<string, string?> skeletonBoneParents = new();
    private bool skeletonBonesLoaded;

    private static readonly string[] BehaviorNames = { "Training Dummy", "Basic Melee", "Basic Ranged", "Boss" };

    // Death cam preset state
    private string newPresetName = "";
    private int selectedPresetIndex = -1;
    private bool overwritePopupOpen = false;
    private string overwriteTargetName = "";

    // Ragdoll bone profile state (Advanced page — per-bone configs)
    private string newBoneProfileName = "";
    private int selectedBoneProfileIndex = -1;
    private bool boneProfileOverwritePopupOpen = false;
    private string boneProfileOverwriteTarget = "";

    // Dev easter egg state
    private int devClickCount = 0;
    private bool devUnlocked = false;

    // Virtual Enemies section state
    private NpcCatalog? npcCatalog;
    private string spawnSearchFilter = "";
    private int spawnCategoryIndex = 0; // 0=Popular, 1=Recent, 2=Human, 3=Monsters, 4=All
    private int selectedCatalogIndex = -1;
    private NpcCatalogEntry? selectedCatalogEntry;
    private int selectedRecipeIndex = 0;

    private static readonly string[] SpawnCategoryNames = { "Popular", "Recent", "Human", "Monsters", "All" };

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
        CombatCompanionManager companionManager,
        CombatEngine combatEngine,
        MapEnemyController mapEnemyController,
        GlamourerIpc glamourerIpc,
        VNavmeshIpc vnavmeshIpc,
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
        this.companionManager = companionManager;
        this.combatEngine = combatEngine;
        this.mapEnemyController = mapEnemyController;
        this.glamourerIpc = glamourerIpc;
        this.vnavmeshIpc = vnavmeshIpc;
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
        this.recipeBook = new CombatRecipeBook(log);
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
        "Party",
        "Effects",
        "Camera",
        "Ragdoll",
        "Ragdoll (Adv)",
        "Virtual Enemies",
        "Settings",
        "Diagnose",
    };

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 230), ImGuiCond.FirstUseEver);
        var showWindow = config.ShowMainWindow;
        if (!ImGui.Begin("Combat Simulator", ref showWindow))
        {
            config.ShowMainWindow = showWindow;
            ImGui.End();
            return;
        }
        config.ShowMainWindow = showWindow;

        DrawFastCombatPanel(compact: false);

        ImGui.Separator();
        if (ImGui.Button("Professional Mode"))
        {
            config.ShowProfessionalWindow = true;
            config.Save();
        }
        HelpMarker("Open the detailed configuration and advanced tools window.");

        if (ImGui.Button("Controls"))
            showControlsPopup = true;
        HelpMarker("Show the combat targeting controls used by Combat Simulator.");
        DrawControlsPopup();

        ImGui.Separator();
        vnavmeshIpc.RefreshStatus();
        if (vnavmeshIpc.IsAvailable)
            ImGui.TextColored(new Vector4(0f, 0.85f, 0f, 1f), "vnavmesh: active");
        else
            ImGui.TextDisabled("vnavmesh: not installed");
        ImGui.SameLine();
        HelpMarker("vnavmesh is recommended for better pathfinding. The plugin works without it, but enemy movement will be limited.");

        ImGui.End();
    }

    public void DrawProfessional()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 500), ImGuiCond.FirstUseEver);
        var showWindow = config.ShowProfessionalWindow;
        if (!ImGui.Begin("Combat Simulator - Professional Mode", ref showWindow))
        {
            config.ShowProfessionalWindow = showWindow;
            ImGui.End();
            return;
        }
        config.ShowProfessionalWindow = showWindow;

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
                DrawMapEnemiesSection();
                break;
            case 2: // Party
                DrawPartyTab();
                break;
            case 3: // Effects
                DrawHitVfxSection();
                DrawGlamourerHeaderSection();
                break;
            case 4: // Camera
                DrawActiveCamSection();
                DrawDeathCamSection();
                break;
            case 5: // Ragdoll
                DrawRagdollSection();
                DrawNpcCollisionSection();
                DrawNpcSettleCollisionSection();
                ImGui.Separator();
                if (ImGui.Button("Reset All to Defaults##ragdollpage"))
                {
                    // Ragdoll
                    config.RagdollActivationDelay = 1.0f;
                    config.EnableNpcDeathRagdoll = true;
                    config.NpcRagdollActivationDelay = 0.5f;
                    config.PartyCompanionDeathRagdoll = true;
                    config.PartyEnemyDeathRagdoll = true;
                    config.RagdollGravity = 9.8f;
                    config.RagdollDamping = 0.97f;
                    config.RagdollSolverIterations = 8;
                    config.RagdollSelfCollision = true;
                    config.RagdollFriction = 1.0f;
                    config.WeaponDropGravity = 9.8f;
                    config.WeaponDropDamping = 0.99f;
                    config.WeaponDropAngularDamping = 0.85f;
                    config.WeaponDropMass = 1.5f;
                    config.WeaponDropRadius = 0.025f;
                    config.WeaponDropHalfLength = 0.4f;
                    config.WeaponDropBounce = 1.5f;
                    config.WeaponDropFriction = 0.6f;
                    config.WeaponDropSolverIterations = 4;
                    config.RagdollHairPhysics = false;
                    config.RagdollHairGravityStrength = 0.5f;
                    config.RagdollHairDamping = 0.92f;
                    config.RagdollHairStiffness = 0.1f;
                    // NPC Collision
                    config.RagdollNpcCollision = true;
                    config.RagdollNpcCollisionConvexHull = false;
                    // NPC Collision (Settle)
                    config.RagdollNpcSettleCollision = true;
                    config.Save();
                }
                break;
            case 6: // Ragdoll (Advanced)
                DrawRagdollAdvancedSection();
                break;
            case 7: // Virtual Enemies
                DrawVirtualEnemiesTab();
                break;
            case 8: // Settings
                DrawGuiSettingsSection();
                DrawDevSection();
                break;
            case 9: // Diagnose
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
                selectedTab = 9; // Jump to Diagnose tab
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

    private void DrawFastCombatPanel(bool compact)
    {
        var recipes = recipeBook.Recipes;
        if (selectedRecipeIndex < 0 || selectedRecipeIndex >= recipes.Count)
            selectedRecipeIndex = 0;

        if (!compact)
        {
            ImGui.TextUnformatted("Combat Recipe");
            HelpMarker("Recipes define companion and enemy groups for one-click skirmish setup. Recipes are loaded from embedded plugin JSON resources.");
        }

        DrawRecipeCombo("##CombatRecipe", compact ? 220f : -1f);
        if (!compact && ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose the recipe used by Start and Reset.");

        var recipe = recipes.Count > 0 ? recipes[selectedRecipeIndex] : null;
        if (!compact && recipe != null && !string.IsNullOrWhiteSpace(recipe.Description))
            ImGui.TextWrapped(recipe.Description);

        if (!compact)
        {
            var level = Math.Clamp(config.FastCombatLevel, 1, 300);
            if (ImGui.SliderInt("Enemy level", ref level, 1, 300))
            {
                config.FastCombatLevel = level;
                config.Save();
            }
            HelpMarker("Enemy simulated level used when starting a recipe. Companions mirror player stats.");
        }

        if (compact)
            ImGui.SameLine();

        using (ImRaii.Disabled(recipe == null))
        {
            if (ImGui.Button("Start", compact ? new Vector2(70, 0) : new Vector2(110, 0)) && recipe != null)
                StartRecipe(recipe);
        }
        if (!compact)
            HelpMarker("Stop any existing recipe battle, spawn the selected companions and virtual enemies, then start combat.");

        ImGui.SameLine();
        if (ImGui.Button("Reset", compact ? new Vector2(70, 0) : new Vector2(110, 0)))
            ResetFastCombat();
        if (!compact)
            HelpMarker("Reset the current combat state. Existing recipe companions are revived and virtual enemies are regenerated using the current setup.");

        ImGui.SameLine();
        var canRevive = combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;
        using (ImRaii.Disabled(!canRevive))
        {
            if (ImGui.Button("Revive", compact ? new Vector2(70, 0) : new Vector2(110, 0)) && canRevive)
                combatEngine.RevivePlayerInPlace();
        }
        if (!compact)
            HelpMarker("Revive the player in place when defeated, restoring HP/MP without resetting enemies or companions. Disabled while the player is alive.");

        ImGui.SameLine();
        if (ImGui.Button("Stop", compact ? new Vector2(70, 0) : new Vector2(110, 0)))
            StopFastCombat();
        if (!compact)
            HelpMarker("Stop combat and clear all recipe companions and virtual enemies.");

        if (!compact)
        {
            var anonymousMode = config.AnonymousMode;
            if (ImGui.Checkbox("Anonymous Mode", ref anonymousMode))
            {
                config.AnonymousMode = anonymousMode;
                config.Save();
            }
            HelpMarker("Hide character names and defeated/dead labels in HP overlays.");
        }
    }

    private void DrawControlsPopup()
    {
        if (!showControlsPopup)
            return;

        ImGui.OpenPopup("Combat Controls");
        if (ImGui.BeginPopupModal("Combat Controls", ref showControlsPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Combat targeting uses your current game keybinds.");
            ImGui.Spacing();

            if (ImGui.BeginTable("##CombatControlTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Action");
                ImGui.TableSetupColumn("Keyboard / Mouse");
                ImGui.TableSetupColumn("Gamepad");
                ImGui.TableHeadersRow();

                DrawControlRow("Acquire target", "Confirm / OK binding", "Confirm / OK button");
                DrawControlRow("Cancel target", "Cancel binding", "Cancel button");
                DrawControlRow("Next target", "Target Next binding", "D-pad Right");
                DrawControlRow("Previous target", "Target Previous binding", "D-pad Left");
                DrawControlRow("Attack", "Your normal action hotkeys", "Your normal hotbar buttons");
                DrawControlRow("Auto-counter", "Hit while unlocked can lock the attacker", "Same behavior");

                ImGui.EndTable();
            }

            ImGui.Spacing();
            if (ImGui.Button("Close", new Vector2(100, 0)))
                showControlsPopup = false;

            ImGui.EndPopup();
        }
    }

    private static void DrawControlRow(string action, string keyboard, string gamepad)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(action);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(keyboard);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(gamepad);
    }

    private void DrawRecipeCombo(string id, float width)
    {
        var recipes = recipeBook.Recipes;
        var preview = recipes.Count > 0 ? recipes[selectedRecipeIndex].Name : "(No recipes)";
        if (width != 0)
            ImGui.SetNextItemWidth(width);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(0, 0),
            new Vector2(float.MaxValue, ImGui.GetTextLineHeightWithSpacing() * 9));
        if (!ImGui.BeginCombo(id, preview))
            return;

        for (var i = 0; i < recipes.Count; i++)
        {
            var selected = i == selectedRecipeIndex;
            if (ImGui.Selectable(recipes[i].Name, selected))
                selectedRecipeIndex = i;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    public void DrawFastCombatToolbar()
    {
        ImGui.SetNextWindowSize(new Vector2(620, 46), ImGuiCond.FirstUseEver);
        var showToolbar = config.ShowFastCombatToolbar;
        if (!ImGui.Begin("Combat Simulator Fast Combat", ref showToolbar,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (config.ShowFastCombatToolbar != showToolbar)
            {
                config.ShowFastCombatToolbar = showToolbar;
                config.Save();
            }
            ImGui.End();
            return;
        }

        if (config.ShowFastCombatToolbar != showToolbar)
        {
            config.ShowFastCombatToolbar = showToolbar;
            config.Save();
        }

        DrawFastCombatPanel(compact: true);
        ImGui.End();
    }

    public void DrawDefeatRevivePopup()
    {
        if (!config.ShowDefeatRevivePopup || !combatEngine.IsActive || combatEngine.State.PlayerState.IsAlive)
            return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(300, 0), new Vector2(360, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        var open = true;
        if (!ImGui.Begin("Defeated##CombatSimDefeated", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var defeatedBy = string.IsNullOrWhiteSpace(combatEngine.LastPlayerDefeatedBy)
            ? "an enemy"
            : combatEngine.LastPlayerDefeatedBy;
        ImGui.TextWrapped($"You were defeated by {defeatedBy}.");
        HelpMarker("Revive restores your simulated HP/MP in place without resetting enemies or companions.");

        if (ImGui.Button("Revive", new Vector2(110, 0)))
            combatEngine.RevivePlayerInPlace();

        ImGui.End();
    }

    private void StartRecipe(CombatRecipe recipe)
    {
        StopFastCombat(print: false);

        config.EnableCombatCompanions = true;
        config.SensePartyMembers = recipe.Companions.Exists(c => c.Type == CompanionRecipeType.VisiblePlayers);
        config.EnableMapPlayerEnemySensing = recipe.MapEnemies.Exists(g => g.Enabled && g.IncludePlayers);
        config.CombatCompanionMaxCount = Math.Min(CombatCompanionManager.MaxCompanionCap, TotalRequestedCompanions(recipe));

        npcSpawner.SpawnModeActive = true;
        combatEngine.StartSimulation();
        var mapEnemySettings = BuildRecipeMapEnemySettings(recipe);
        mapEnemyController.SetRecipeSettings(mapEnemySettings);

        var queuedCompanions = 0;
        foreach (var group in recipe.Companions)
        {
            var count = Math.Max(0, group.Count);
            queuedCompanions += group.Type switch
            {
                CompanionRecipeType.VisiblePlayers => companionManager.SpawnFromVisiblePlayers(count),
                CompanionRecipeType.Self => companionManager.SpawnSelfCharacters(count, randomizeAppearance: false, ignoreConfiguredMax: true),
                CompanionRecipeType.SelfRandomized => companionManager.SpawnSelfCharacters(count, randomizeAppearance: true, ignoreConfiguredMax: true),
                _ => 0,
            };
        }

        var queuedEnemies = 0;
        npcCatalog ??= new NpcCatalog(dataManager, log);
        foreach (var group in recipe.Enemies)
        {
            var entry = ResolveRecipeEnemy(group);
            if (entry == null)
            {
                chatGui.PrintError($"[CombatSim] Recipe enemy not found: {group.Name} ({group.Type}:{group.Id}).");
                continue;
            }

            var count = Math.Max(0, group.Count);
            for (var i = 0; i < count; i++)
            {
                npcSpawner.QueueSpawn(new NpcSpawnRequest
                {
                    BNpcBaseId = entry.Type == NpcCatalogType.BNpc ? entry.Id : 0,
                    BNpcNameId = entry.BNpcNameId,
                    ENpcBaseId = entry.Type is NpcCatalogType.ENpc or NpcCatalogType.Human ? entry.Id : 0,
                    Level = Math.Clamp(config.FastCombatLevel, 1, 300),
                    HpMultiplier = Math.Max(0.0001f, group.HpMultiplier),
                    BehaviorType = group.Behavior,
                });
                queuedEnemies++;
            }
        }

        var mapEnemyText = mapEnemySettings != null ? $", up to {mapEnemySettings.MaxCount} map enemy/enemies" : "";
        chatGui.Print($"[CombatSim] Started recipe '{recipe.Name}' ({queuedCompanions} companion(s), {queuedEnemies} enemy/enemies queued{mapEnemyText}).");
    }

    private MapEnemySettings? BuildRecipeMapEnemySettings(CombatRecipe recipe)
    {
        foreach (var group in recipe.MapEnemies)
        {
            if (!group.Enabled || group.MaxCount <= 0)
                continue;

            return new MapEnemySettings
            {
                Enabled = true,
                IncludeBattleNpcs = group.IncludeBattleNpcs,
                IncludePlayers = group.IncludePlayers,
                MaxCount = Math.Max(0, group.MaxCount),
                SenseRange = Math.Max(0.1f, group.SenseRange),
                Level = Math.Clamp(config.FastCombatLevel, 1, 300),
                HpMultiplier = Math.Max(0.0001f, group.HpMultiplier),
                BehaviorType = group.Behavior,
            };
        }

        return null;
    }

    private NpcCatalogEntry? ResolveRecipeEnemy(CombatRecipeEnemyGroup group)
    {
        npcCatalog ??= new NpcCatalog(dataManager, log);
        if (group.Id != 0)
        {
            var byId = npcCatalog.FindById(group.Type, group.Id)
                ?? (group.Type == NpcCatalogType.Human ? npcCatalog.FindById(NpcCatalogType.ENpc, group.Id) : null)
                ?? (group.Type == NpcCatalogType.ENpc ? npcCatalog.FindById(NpcCatalogType.Human, group.Id) : null);
            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(group.Name))
            return npcCatalog.FindByNameOccurrence(group.Name, group.Type, group.Occurrence)
                ?? npcCatalog.FindByNameOccurrence(group.Name, null, group.Occurrence);

        return null;
    }

    private static int TotalRequestedCompanions(CombatRecipe recipe)
    {
        var total = 0;
        foreach (var group in recipe.Companions)
            total += Math.Max(0, group.Count);
        return Math.Max(1, total);
    }

    private void ResetFastCombat()
    {
        if (!combatEngine.IsActive)
        {
            var recipes = recipeBook.Recipes;
            if (recipes.Count > 0)
                StartRecipe(recipes[selectedRecipeIndex]);
            return;
        }

        var keepCompanionsOnReset = config.KeepCompanionsOnReset;
        config.KeepCompanionsOnReset = true;
        combatEngine.ResetState();
        config.KeepCompanionsOnReset = keepCompanionsOnReset;
        chatGui.Print("[CombatSim] Fast combat reset.");
    }

    private void StopFastCombat(bool print = true)
    {
        combatEngine.StopSimulation();
        foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
            npcSelector.UnregisterSpawnedNpc(npc);
        npcSpawner.DespawnAll();
        companionManager.DespawnAll();
        npcSpawner.SpawnModeActive = false;
        mapEnemyController.ClearRecipeSettings();

        if (print)
            chatGui.Print("[CombatSim] Fast combat stopped.");
    }

    private void DrawPartyTab()
    {
        var enabled = config.EnableCombatCompanions;
        if (ImGui.Checkbox("Enable Combat Companions", ref enabled))
        {
            config.EnableCombatCompanions = enabled;
            config.Save();
        }

        var maxCount = Math.Clamp(config.CombatCompanionMaxCount, 0, CombatCompanionManager.MaxCompanionCap);
        if (ImGui.SliderInt("Max cloned players", ref maxCount, 0, CombatCompanionManager.MaxCompanionCap))
        {
            config.CombatCompanionMaxCount = maxCount;
            config.Save();
        }

        var senseParty = config.SensePartyMembers;
        if (ImGui.Checkbox("Sense party members", ref senseParty))
        {
            config.SensePartyMembers = senseParty;
            config.Save();
        }

        var keepOnReset = config.KeepCompanionsOnReset;
        if (ImGui.Checkbox("Keep companions on reset", ref keepOnReset))
        {
            config.KeepCompanionsOnReset = keepOnReset;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resetting combat revives and heals existing companions\ninstead of despawning them. Stopping always despawns.");

        ImGui.Separator();

        var commandRange = Math.Clamp(config.PartyCommandRange, 3.0f, 50.0f);
        if (ImGui.SliderFloat("Command range", ref commandRange, 3.0f, 50.0f, "%.1f yalms"))
        {
            config.PartyCommandRange = commandRange;
            config.Save();
        }

        var commandRandomness = Math.Clamp(config.PartyCommandRangeRandomness, 0.0f, 0.6f);
        if (ImGui.SliderFloat("Command range randomness", ref commandRandomness, 0.0f, 0.6f, "%.2f"))
        {
            config.PartyCommandRangeRandomness = commandRandomness;
            config.Save();
        }

        var meleeRange = Math.Clamp(config.PartyMeleeAttackRange, 0.5f, 5.0f);
        if (ImGui.SliderFloat("Melee attack range", ref meleeRange, 0.5f, 5.0f, "%.1f yalms"))
        {
            config.PartyMeleeAttackRange = meleeRange;
            config.Save();
        }

        var rangedRange = Math.Clamp(config.PartyRangedAttackRange, 5.0f, 35.0f);
        if (ImGui.SliderFloat("Ranged attack range", ref rangedRange, 5.0f, 35.0f, "%.1f yalms"))
        {
            config.PartyRangedAttackRange = rangedRange;
            config.Save();
        }

        // Companion clone gear — apply a Glamourer design's equipment to spawned
        // clones (equipment only, so customize/randomized look is preserved).
        // "(None)" keeps each clone's own inherited gear and also works when
        // Glamourer is not installed.
        ImGui.Spacing();
        ImGui.TextUnformatted("Companion clone gear (Glamourer, equipment only)");

        var glamNames = new string[glamourerDesigns.Count + 1];
        glamNames[0] = "(None - inherit own gear)";
        for (int i = 0; i < glamourerDesigns.Count; i++)
            glamNames[i + 1] = glamourerDesigns[i].Value;

        var partyGlamSel = 0;
        if (Guid.TryParse(config.PartyCompanionGlamourerDesignId, out var curPartyGlamId))
        {
            for (int i = 0; i < glamourerDesigns.Count; i++)
            {
                if (glamourerDesigns[i].Key == curPartyGlamId)
                {
                    partyGlamSel = i + 1;
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("##PartyCompanionGlam", ref partyGlamSel, glamNames, glamNames.Length))
        {
            config.PartyCompanionGlamourerDesignId = partyGlamSel <= 0
                ? string.Empty
                : glamourerDesigns[partyGlamSel - 1].Key.ToString();
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh##PartyGlam"))
        {
            glamourerDesigns = new List<KeyValuePair<Guid, string>>(glamourerIpc.GetDesignList());
            glamourerDesigns.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            chatGui.Print(glamourerDesigns.Count > 0
                ? $"[CombatSim] Found {glamourerDesigns.Count} Glamourer designs."
                : "[CombatSim] No Glamourer designs found. Is Glamourer installed?");
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!config.EnableCombatCompanions))
        {
            if (ImGui.Button("Clone visible players"))
            {
                var queued = companionManager.SpawnFromVisiblePlayers();
                chatGui.Print($"[CombatSim] Queued {queued} companion clone(s).");
            }
            ImGui.SameLine();
            if (ImGui.Button("Clone self character with randomized look"))
            {
                var queued = companionManager.SpawnSelfCharacter(randomizeAppearance: true);
                chatGui.Print(queued
                    ? "[CombatSim] Queued randomized self companion clone."
                    : "[CombatSim] Could not queue randomized self companion clone.");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear companions"))
            companionManager.DespawnAll();

        ImGui.Text($"Active: {companionManager.Companions.Count}  Pending: {companionManager.PendingCount}");

        if (companionManager.Companions.Count == 0)
        {
            ImGui.TextDisabled("No companion clones spawned.");
            return;
        }

        if (ImGui.BeginTable("##companions", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Level");
            ImGui.TableSetupColumn("HP");
            ImGui.TableSetupColumn("Recent DPS");
            ImGui.TableHeadersRow();

            foreach (var companion in companionManager.Companions)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(companion.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(companion.State.Level.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{companion.State.CurrentHp:N0} / {companion.State.MaxHp:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{companion.RecentDps:N0}");
            }

            ImGui.EndTable();
        }
    }

    private void DrawMapEnemiesSection()
    {
        if (!ImGui.CollapsingHeader("Map Enemies", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var sensing = config.EnableMapEnemySensing;
        if (ImGui.Checkbox("Sense map enemies", ref sensing))
        {
            config.EnableMapEnemySensing = sensing;
            config.Save();
        }
        HelpMarker("During normal mixed battles, nearby map BattleNpcs can join the enemy pool. Existing recipes only use this when the recipe explicitly enables Map Enemies.");

        var playerEnemySensing = config.EnableMapPlayerEnemySensing;
        if (ImGui.Checkbox("Sense map players as enemies", ref playerEnemySensing))
        {
            config.EnableMapPlayerEnemySensing = playerEnemySensing;
            config.Save();
        }
        HelpMarker("Nearby visible players can join the enemy pool instead of being cloned as companions. If companion sensing is also enabled, each visible player is randomly assigned to one side and never both.");

        if (!sensing && !playerEnemySensing)
            ImGui.BeginDisabled();

        var range = Math.Clamp(config.MapEnemySenseRange, 1.0f, 80.0f);
        if (ImGui.SliderFloat("Sense range", ref range, 1.0f, 80.0f, "%.1f yalms"))
        {
            config.MapEnemySenseRange = range;
            config.Save();
        }

        var maxMapEnemies = Math.Clamp(config.MapEnemyMaxCount, 0, 100);
        if (ImGui.SliderInt("Max map enemies", ref maxMapEnemies, 0, 100))
        {
            config.MapEnemyMaxCount = maxMapEnemies;
            config.Save();
        }

        if (!sensing && !playerEnemySensing)
            ImGui.EndDisabled();

        ImGui.Separator();

        var defaultLevel = config.DefaultNpcLevel;
        if (ImGui.SliderInt("Enemy level", ref defaultLevel, 1, 300))
        {
            config.DefaultNpcLevel = defaultLevel;
            config.Save();
        }

        var hpMult = config.DefaultNpcHpMultiplier;
        if (ImGui.InputFloat("HP multiplier", ref hpMult, 0.0001f, 0.01f, "%.4f"))
        {
            if (hpMult < 0.0001f) hpMult = 0.0001f;
            config.DefaultNpcHpMultiplier = hpMult;
            config.Save();
        }

        var behaviorType = config.DefaultNpcBehaviorType;
        if (ImGui.Combo("Behavior", ref behaviorType, BehaviorNames, BehaviorNames.Length))
        {
            config.DefaultNpcBehaviorType = behaviorType;
            config.Save();
        }

        ImGui.Separator();

        var autoCounter = config.EnableAutoCounter;
        if (ImGui.Checkbox("Auto-counter", ref autoCounter))
        {
            config.EnableAutoCounter = autoCounter;
            config.Save();
        }
        HelpMarker("When you have no locked target, being hit by an enemy auto-locks that attacker. Pressing cancel stops auto-counter (no auto-lock even when hit) until you press confirm to lock a target again. Requires custom targeting.");
    }

    private void DrawVirtualEnemiesTab()
    {
        // Lazy-load catalog on first open
        npcCatalog ??= new NpcCatalog(dataManager, log);

        ImGui.TextWrapped("Search an enemy by name, then Spawn to fight it. Spawning starts the combat simulation automatically. Use Stop in the main window to despawn everything.");
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.2f, 0.2f, 1f), "Warning: Spawning certain enemies may cause the game to crash. Use with caution.");
        ImGui.Spacing();

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

        // Search filter (Human / Monsters / All)
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
            3 => npcCatalog.Search(spawnSearchFilter, NpcCatalogType.BNpc),
            4 => npcCatalog.Search(spawnSearchFilter),
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
        var dist = config.SpawnDistance;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("Distance", ref dist, 1.0f, 15.0f, "%.1f yalms"))
        {
            config.SpawnDistance = dist;
            config.Save();
        }
        HelpMarker("Spawn direction is randomized around your character.");

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
            config.DefaultNpcLevel = Math.Clamp(defaultLevel, 1, 300);
            config.Save();
        }

        ImGui.SameLine();
        var hpMult = config.DefaultNpcHpMultiplier;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputFloat("HP Mult", ref hpMult, 0.0001f, 0.01f, "%.4f"))
        {
            if (hpMult < 0.0001f) hpMult = 0.0001f;
            config.DefaultNpcHpMultiplier = hpMult;
            config.Save();
        }

        // Spawn button + counter
        ImGui.Spacing();
        bool canSpawn = selectedCatalogEntry != null &&
                        npcSpawner.TotalCount < npcSpawner.MaxNpcs;
        if (!canSpawn) ImGui.BeginDisabled();
        if (ImGui.Button("Spawn", new Vector2(80, 0)))
        {
            var entry = selectedCatalogEntry!;
            var pos = CalculateSpawnPosition(config.SpawnDistance);

            // Virtual enemies are always available now: activate spawn mode and start
            // the simulation on demand so the user can just search and spawn.
            npcSpawner.SpawnModeActive = true;
            if (!combatEngine.IsActive)
            {
                combatEngine.StartSimulation();
                chatGui.Print("[CombatSim] Virtual Enemies: combat simulation started.");
            }

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
                    // 75% of the leftover row width — leaves a clear gap before
                    // the name / ranged checkbox / despawn cluster on the right.
                    float hpBarWidth = (ImGui.GetContentRegionAvail().X - 120) * 0.75f;
                    ImGui.ProgressBar(hp, new Vector2(hpBarWidth, 0),
                        $"{npc.State.CurrentHp:N0}/{npc.State.MaxHp:N0}");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                }

                ImGui.Text(npc.Name);

                // Ranged toggle — when on, this NPC's actions play the ranged
                // attack motion (bow/gun draw + projectile) instead of melee.
                // Label hidden via ##id; tooltip on hover explains what it does.
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 145);
                bool ranged = npc.IsRanged;
                if (ImGui.Checkbox("##ranged", ref ranged))
                    npc.IsRanged = ranged;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Ranged: force ranged attack animation for this NPC (use for archers / casters whose default action animation is melee).");

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

    private Vector3 CalculateSpawnPosition(float distance)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return Vector3.Zero;

        var playerPos = player.Position;
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var dir = new Vector3(-MathF.Sin(angle), 0, -MathF.Cos(angle));
        return playerPos + dir * distance;
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

    private void DrawHitVfxSection()
    {
        if (ImGui.CollapsingHeader("VFX"))
        {
            var characterVfx = config.EnableCharacterVfx;
            if (ImGui.Checkbox("Enable Character VFX", ref characterVfx))
            {
                config.EnableCharacterVfx = characterVfx;
                config.Save();
            }
            HelpMarker("Spawn caster/character-side action effects such as cast circles, start effects, and caster timeline VFX.");

            if (config.EnableCharacterVfx && hookSafetyChecker.HasConflicts)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "! Check Diagnose");
            }

            ImGui.Spacing();

            var targetVfx = config.EnableTargetVfx;
            if (ImGui.Checkbox("Enable Target VFX", ref targetVfx))
            {
                config.EnableTargetVfx = targetVfx;
                config.Save();
            }
            HelpMarker("Spawn target-side impact effects. If an action has no target VFX, the fallback hit VFX below can be used.");

            if (config.EnableTargetVfx && hookSafetyChecker.HasConflicts)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "! Check Diagnose");
            }

            if (!config.EnableTargetVfx)
                ImGui.BeginDisabled();

            var enableVfx = config.EnableHitVfx;
            if (ImGui.Checkbox("Use Fallback Hit VFX", ref enableVfx))
            {
                config.EnableHitVfx = enableVfx;
                config.Save();
            }
            HelpMarker("When target-side action VFX are unavailable, spawn this configured hit effect on the damaged target.");

            var vfxPath = config.HitVfxPath;
            if (ImGui.InputText("VFX Path (.avfx)", ref vfxPath, 256))
            {
                config.HitVfxPath = vfxPath;
                config.Save();
            }
            HelpMarker("Game VFX path to spawn on hit. Uses FFXIV's internal .avfx format.\nDefault: vfx/common/eff/dk05th_stdn0t.avfx");

            if (!config.EnableTargetVfx)
                ImGui.EndDisabled();

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

        DrawRagdollBoneProfilesSection();
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
        var anatomicalRoles = Enum.GetNames<RagdollController.AnatomicalRole>();
        var colliderShapes = Enum.GetNames<RagdollController.RagdollColliderShape>();
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

                    var role = bone.AnatomicalRole;
                    if (ImGui.Combo($"Anatomical Role{id}", ref role, anatomicalRoles, anatomicalRoles.Length))
                    { bone.AnatomicalRole = role; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var shape = bone.ColliderShape;
                    if (ImGui.Combo($"Collider Shape{id}", ref shape, colliderShapes, colliderShapes.Length))
                    { bone.ColliderShape = shape; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var radius = bone.CapsuleRadius;
                    if (ImGui.SliderFloat($"Capsule Radius{id}", ref radius, 0.01f, 0.3f, "%.3f"))
                    { bone.CapsuleRadius = radius; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var halfLen = bone.CapsuleHalfLength;
                    if (ImGui.SliderFloat($"Capsule Half-Length{id}", ref halfLen, 0.0f, 0.3f, "%.3f"))
                    { bone.CapsuleHalfLength = halfLen; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    if ((RagdollController.RagdollColliderShape)bone.ColliderShape == RagdollController.RagdollColliderShape.Box)
                    {
                        var boxX = bone.BoxHalfExtentX;
                        if (ImGui.SliderFloat($"Box Half X{id}", ref boxX, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentX = boxX; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var boxY = bone.BoxHalfExtentY;
                        if (ImGui.SliderFloat($"Box Half Y{id}", ref boxY, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentY = boxY; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var boxZ = bone.BoxHalfExtentZ;
                        if (ImGui.SliderFloat($"Box Half Z{id}", ref boxZ, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentZ = boxZ; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

                    var mass = bone.Mass;
                    if (ImGui.SliderFloat($"Mass{id}", ref mass, 0.1f, 15.0f, "%.1f"))
                    { bone.Mass = mass; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var swing = bone.SwingLimit;
                    if (ImGui.SliderFloat($"Swing Limit (rad){id}", ref swing, 0.0f, MathF.PI, "%.2f"))
                    { bone.SwingLimit = swing; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                    if ((RagdollController.JointType)bone.JointType == RagdollController.JointType.Hinge)
                    {
                        var swingMin = bone.SwingMinLimit ?? 0f;
                        if (ImGui.SliderFloat($"Swing Min Limit (rad){id}", ref swingMin, 0.0f, MathF.PI, "%.2f"))
                        { bone.SwingMinLimit = swingMin; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                        var restAngle = bone.HingeRestAngle ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Angle (rad){id}", ref restAngle, 0.0f, MathF.PI, "%.2f"))
                        { bone.HingeRestAngle = restAngle; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                        var restFreq = bone.HingeRestSpringFreq ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Freq (Hz){id}", ref restFreq, 0.0f, 10.0f, "%.1f"))
                        { bone.HingeRestSpringFreq = restFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var restForce = bone.HingeRestMaxForce ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Max Force{id}", ref restForce, 0.0f, 50.0f, "%.1f"))
                        { bone.HingeRestMaxForce = restForce; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

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
                            var def = CloneBoneConfig(RagdollController.AllBoneDefaults[i]);
                            bone.CapsuleRadius = def.CapsuleRadius;
                            bone.CapsuleHalfLength = def.CapsuleHalfLength;
                            bone.Mass = def.Mass;
                            bone.SwingLimit = def.SwingLimit;
                            bone.SwingMinLimit = def.SwingMinLimit;
                            bone.HingeRestAngle = def.HingeRestAngle;
                            bone.HingeRestSpringFreq = def.HingeRestSpringFreq;
                            bone.HingeRestMaxForce = def.HingeRestMaxForce;
                            bone.JointType = def.JointType;
                            bone.TwistMinAngle = def.TwistMinAngle;
                            bone.TwistMaxAngle = def.TwistMaxAngle;
                            bone.AnatomicalRole = def.AnatomicalRole;
                            bone.ColliderShape = def.ColliderShape;
                            bone.BoxHalfExtentX = def.BoxHalfExtentX;
                            bone.BoxHalfExtentY = def.BoxHalfExtentY;
                            bone.BoxHalfExtentZ = def.BoxHalfExtentZ;
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
        var player = Core.Services.ObjectTable.LocalPlayer;
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
                var unknown = new RagdollBoneConfig
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
                };
                RagdollController.FillProfileDefaults(unknown);
                config.RagdollBoneConfigs.Add(unknown);
            }
            added = true;
        }

        if (added) config.Save();
    }

    private static RagdollBoneConfig CloneBoneConfig(RagdollBoneConfig src)
    {
        var clone = new RagdollBoneConfig
        {
            Name = src.Name,
            SkeletonParent = src.SkeletonParent,
            Enabled = src.Enabled,
            CapsuleRadius = src.CapsuleRadius,
            CapsuleHalfLength = src.CapsuleHalfLength,
            Mass = src.Mass,
            SwingLimit = src.SwingLimit,
            SwingMinLimit = src.SwingMinLimit,
            HingeRestAngle = src.HingeRestAngle,
            HingeRestSpringFreq = src.HingeRestSpringFreq,
            HingeRestMaxForce = src.HingeRestMaxForce,
            JointType = src.JointType,
            TwistMinAngle = src.TwistMinAngle,
            TwistMaxAngle = src.TwistMaxAngle,
            AnatomicalRole = src.AnatomicalRole,
            ColliderShape = src.ColliderShape,
            BoxHalfExtentX = src.BoxHalfExtentX,
            BoxHalfExtentY = src.BoxHalfExtentY,
            BoxHalfExtentZ = src.BoxHalfExtentZ,
            Description = src.Description,
            SoftBody = src.SoftBody,
            SoftSpringFreq = src.SoftSpringFreq,
            SoftSpringDamp = src.SoftSpringDamp,
            SoftServoFreq = src.SoftServoFreq,
            SoftServoDamp = src.SoftServoDamp,
        };
        RagdollController.FillProfileDefaults(clone);
        return clone;
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

            var showFastCombatToolbar = config.ShowFastCombatToolbar;
            if (ImGui.Checkbox("Show Fast Combat Toolbar", ref showFastCombatToolbar))
            {
                config.ShowFastCombatToolbar = showFastCombatToolbar;
                config.Save();
            }
            HelpMarker("Show a compact recipe selector with Start, Reset, and Stop controls.");

            var showDefeatPopup = config.ShowDefeatRevivePopup;
            if (ImGui.Checkbox("Show Defeat Revive Popup", ref showDefeatPopup))
            {
                config.ShowDefeatRevivePopup = showDefeatPopup;
                config.Save();
            }
            HelpMarker("Show a small window on simulated player defeat with a Revive button. Revive restores only the player and keeps the fight running.");

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

            if (ImGui.TreeNode("Player HP Bar Labels & Offsets"))
            {
                var customName = config.CustomPlayerName;
                if (ImGui.InputText("Custom Player Name", ref customName, 64))
                {
                    config.CustomPlayerName = customName;
                    config.Save();
                }
                HelpMarker("Custom name shown on the sim HP bar. Leave empty to use your character's actual name.");

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

                ImGui.Separator();

                var showSim = config.ShowSimLabel;
                if (ImGui.Checkbox("Show Sim Label", ref showSim))
                {
                    config.ShowSimLabel = showSim;
                    config.Save();
                }
                HelpMarker("Show the [Sim] prefix on the live HP bar.");
                if (showSim)
                {
                    var simText = config.SimLabelText;
                    if (ImGui.InputText("Sim Label Text", ref simText, 32))
                    {
                        config.SimLabelText = simText;
                        config.Save();
                    }
                }

                var showDead = config.ShowDeadLabel;
                if (ImGui.Checkbox("Show Dead Label", ref showDead))
                {
                    config.ShowDeadLabel = showDead;
                    config.Save();
                }
                HelpMarker("Show the [DEAD] prefix on the HP bar when the player is defeated.");
                if (showDead)
                {
                    var deadText = config.DeadLabelText;
                    if (ImGui.InputText("Dead Label Text", ref deadText, 32))
                    {
                        config.DeadLabelText = deadText;
                        config.Save();
                    }
                }

                var showDefeated = config.ShowDefeatedText;
                if (ImGui.Checkbox("Show Defeated HP Text", ref showDefeated))
                {
                    config.ShowDefeatedText = showDefeated;
                    config.Save();
                }
                HelpMarker("Replace the HP numbers with a 'DEFEATED' label inside the bar when dead. Off = keep showing 0 / Max.");
                if (showDefeated)
                {
                    var defeatedText = config.DefeatedText;
                    if (ImGui.InputText("Defeated Text", ref defeatedText, 32))
                    {
                        config.DefeatedText = defeatedText;
                        config.Save();
                    }
                }

                ImGui.TreePop();
            }
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
            HelpMarker("On player death, smoothly transition the camera orbit center to a bone. Rotation and zoom remain under normal camera control.");

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
            HelpMarker("How long the camera takes to interpolate to the selected bone on death.");

            // --- Actions ---
            ImGui.Separator();

            if (ImGui.Button("Set Preset Orbit"))
            {
                if (deathCamController.SetAnchor())
                    chatGui.Print("[CombatSim] Death cam preset orbit set.");
                else
                    chatGui.PrintError("[CombatSim] Failed to set preset orbit. Make sure you have a character loaded.");
            }
            HelpMarker("Capture the current camera angle and distance as an optional preset starting orbit.");

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
            HelpMarker("Toggle live preview. The camera tracks the selected bone while you keep normal rotation and zoom control.");

            ImGui.SameLine();
            if (config.DeathCamAnchorSet && ImGui.SmallButton("Clear Preset Orbit"))
            {
                config.DeathCamAnchorSet = false;
                config.Save();
            }

            // Anchor status
            if (config.DeathCamAnchorSet)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Preset orbit is set.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No preset orbit set. Death cam still tracks the selected bone.");
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
        if (deathCamController.IsPreviewActive || deathCamController.State != DeathCamState.Inactive)
            deathCamController.ApplyAnchorToCamera();
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

        // --- Preset Orbit ---
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Preset Orbit");
        ImGui.Separator();

        if (ImGui.Button("Capture Current Orbit"))
        {
            if (!deathCamController.SetAnchor())
                chatGui.PrintError("[CombatSim] Failed to capture death cam orbit.");
        }
        ImGui.SameLine();
        if (!config.DeathCamAnchorSet)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply Preset Orbit"))
            deathCamController.ApplyAnchorToCamera();
        if (!config.DeathCamAnchorSet)
            ImGui.EndDisabled();

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

                ImGui.Separator();
                ImGui.TextDisabled("Fighting Camera (1v1)");

                var fighting = config.ActiveCameraFightingMode;
                if (ImGui.Checkbox("Enable Fighting Camera##activecam", ref fighting))
                {
                    config.ActiveCameraFightingMode = fighting;
                    config.Save();
                }
                HelpMarker("During a 1v1, center the camera between you and the locked target and auto-zoom so both stay in frame (you keep manual rotation). On either death, smoothly transition to the dead character's bone (above) and suppress Death Cam.");

                if (config.ActiveCameraFightingMode)
                {
                    // Framing bone (read from both combatants)
                    int fBoneIdx = 0;
                    for (int b = 0; b < bones.Length; b++)
                        if (bones[b].BoneName == config.ActiveCameraFightingBoneName) { fBoneIdx = b; break; }
                    if (ImGui.Combo("Framing Bone##fightcam", ref fBoneIdx, boneNames, boneNames.Length))
                    {
                        config.ActiveCameraFightingBoneName = bones[fBoneIdx].BoneName;
                        config.Save();
                    }
                    HelpMarker("Bone on each combatant used to compute the framing midpoint.");

                    var fTrans = config.ActiveCameraFightingTransitionDuration;
                    if (ImGui.SliderFloat("Transition Duration##fightcam", ref fTrans, 0.1f, 5f, "%.2f s"))
                    {
                        config.ActiveCameraFightingTransitionDuration = fTrans;
                        config.Save();
                    }
                    HelpMarker("How long the camera takes to move from the 1v1 framing to the dead character's bone.");

                    var fMargin = config.ActiveCameraFightingZoomMargin;
                    if (ImGui.SliderFloat("Zoom Margin##fightcam", ref fMargin, 1.0f, 2.5f, "%.2f"))
                    {
                        config.ActiveCameraFightingZoomMargin = fMargin;
                        config.Save();
                    }
                    HelpMarker("Extra zoom-out so both fighters stay comfortably in frame. Higher = more padding.");

                    var fMin = config.ActiveCameraFightingMinDistance;
                    if (ImGui.SliderFloat("Min Distance##fightcam", ref fMin, 1.0f, 10f, "%.2f"))
                    {
                        config.ActiveCameraFightingMinDistance = fMin;
                        config.Save();
                    }
                    HelpMarker("Closest the camera will auto-zoom when the fighters are near each other.");

                    var fMax = config.ActiveCameraFightingMaxDistance;
                    if (ImGui.SliderFloat("Max Distance##fightcam", ref fMax, 5f, 40f, "%.2f"))
                    {
                        config.ActiveCameraFightingMaxDistance = fMax;
                        config.Save();
                    }
                    HelpMarker("Farthest the camera will auto-zoom when the fighters are far apart.");

                    var fSmooth = config.ActiveCameraFightingSmoothing;
                    if (ImGui.SliderFloat("Smoothing##fightcam", ref fSmooth, 1f, 20f, "%.1f"))
                    {
                        config.ActiveCameraFightingSmoothing = fSmooth;
                        config.Save();
                    }
                    HelpMarker("How quickly the camera follows center/zoom changes. Higher = snappier, lower = floatier.");

                    var fHeight = config.ActiveCameraFightingHeightOffset;
                    if (ImGui.DragFloat("Center Height Offset##fightcam", ref fHeight, 0.01f, -5f, 10f, "%.2f"))
                    {
                        config.ActiveCameraFightingHeightOffset = fHeight;
                        config.Save();
                    }
                    HelpMarker("Raises/lowers the framing midpoint.");
                }
            }

            ImGui.Unindent();
        }
    }

    private void DrawRagdollBoneProfilesSection()
    {
        if (!ImGui.CollapsingHeader("Bone Profiles", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var profiles = config.RagdollBoneProfiles;
        var profileNames = new string[profiles.Count];
        for (int i = 0; i < profiles.Count; i++)
            profileNames[i] = profiles[i].Name;

        var hasSelection = selectedBoneProfileIndex >= 0 && selectedBoneProfileIndex < profiles.Count;

        if (ImGui.BeginListBox("##BoneProfileSelect",
                new Vector2(250, ImGui.GetTextLineHeightWithSpacing() * 6 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            for (int i = 0; i < profileNames.Length; i++)
            {
                bool isSelected = selectedBoneProfileIndex == i;
                if (ImGui.Selectable(profileNames[i], isSelected))
                    selectedBoneProfileIndex = i;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndListBox();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load##boneprofile") && hasSelection)
            LoadBoneProfile(profiles[selectedBoneProfileIndex]);

        ImGui.SameLine();
        if (ImGui.Button("Overwrite##boneprofile") && hasSelection)
        {
            boneProfileOverwriteTarget = profiles[selectedBoneProfileIndex].Name;
            boneProfileOverwritePopupOpen = true;
            ImGui.OpenPopup("Confirm Overwrite##BoneProfileOverwrite");
        }

        ImGui.SameLine();
        var io = ImGui.GetIO();
        bool ctrlShiftHeld = io.KeyCtrl && io.KeyShift;
        if (!ctrlShiftHeld)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Delete##boneprofile");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl+Shift to enable delete.");
        }
        else if (ImGui.Button("Delete##boneprofile") && hasSelection)
        {
            profiles.RemoveAt(selectedBoneProfileIndex);
            selectedBoneProfileIndex = Math.Min(selectedBoneProfileIndex, profiles.Count - 1);
            config.Save();
        }

        ImGui.SetNextItemWidth(250);
        ImGui.InputText("##BoneProfileName", ref newBoneProfileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save Profile##boneprofile") && newBoneProfileName.Length > 0)
        {
            var existingIdx = profiles.FindIndex(p =>
                p.Name.Equals(newBoneProfileName, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                boneProfileOverwriteTarget = newBoneProfileName;
                boneProfileOverwritePopupOpen = true;
                ImGui.OpenPopup("Confirm Overwrite##BoneProfileOverwrite");
            }
            else
            {
                SaveBoneProfile(newBoneProfileName);
                newBoneProfileName = "";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset to Defaults##boneprofile"))
            LoadBoneDefaults();
        HelpMarker("Replace the live per-bone config list with built-in defaults from RagdollController.AllBoneDefaults. Does not modify saved profiles.");

        // Overwrite confirmation popup
        if (ImGui.BeginPopupModal("Confirm Overwrite##BoneProfileOverwrite", ref boneProfileOverwritePopupOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text($"Overwrite profile \"{boneProfileOverwriteTarget}\"?");
            ImGui.Spacing();

            if (ImGui.Button("Yes", new Vector2(80, 0)))
            {
                SaveBoneProfile(boneProfileOverwriteTarget);
                newBoneProfileName = "";
                boneProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("No", new Vector2(80, 0)))
            {
                boneProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void SaveBoneProfile(string name)
    {
        // Deep-copy the live bone configs into the snapshot
        var snapshot = new RagdollBoneProfile { Name = name };
        foreach (var b in config.RagdollBoneConfigs)
            snapshot.Bones.Add(CloneBoneConfig(b));

        var existing = config.RagdollBoneProfiles.FindIndex(p => p.Name == name);
        if (existing >= 0)
            config.RagdollBoneProfiles[existing] = snapshot;
        else
            config.RagdollBoneProfiles.Add(snapshot);

        config.Save();
        chatGui.Print($"[CombatSim] Bone profile '{name}' saved ({snapshot.Bones.Count} bones).");
    }

    private void LoadBoneProfile(RagdollBoneProfile p)
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var b in p.Bones)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(b));
        config.Save();
        chatGui.Print($"[CombatSim] Bone profile '{p.Name}' loaded ({p.Bones.Count} bones). Reactivate ragdoll to apply.");
    }

    private void LoadBoneDefaults()
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var def in RagdollController.AllBoneDefaults)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(def));
        config.Save();
        chatGui.Print("[CombatSim] Bone configs reset to defaults. Reactivate ragdoll to apply.");
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
                        var player = Core.Services.ObjectTable.LocalPlayer;
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
                if (ImGui.SliderFloat("Activation Delay (s)##ragdoll", ref delay, 0.0f, 20.0f, "%.1f"))
                {
                    config.RagdollActivationDelay = delay;
                    config.Save();
                }
                HelpMarker("Seconds after death before ragdoll physics take over.");

                var extendTerrain = config.ExtendTerrainDetection;
                if (ImGui.Checkbox("Extend Terrain Detection##ragdoll", ref extendTerrain))
                {
                    config.ExtendTerrainDetection = extendTerrain;
                    config.Save();
                }
                HelpMarker("Also build ground collision under nearby enemies, not just the death spot. " +
                           "Lets a victory-sequence grab drop the body onto an enemy without falling through " +
                           "the floor. Costs extra raycasts at activation — may cause a brief hitch. Default off.");

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

                var solverSubsteps = config.RagdollSolverSubsteps;
                if (ImGui.SliderInt("Solver Substeps##ragdoll", ref solverSubsteps, 1, 8))
                {
                    config.RagdollSolverSubsteps = solverSubsteps;
                    config.Save();
                }
                HelpMarker("Velocity-solve substeps per fixed timestep. 1 = legacy. Raising this re-solves constraints at a finer sub-step — BEPU's recommended way to keep a stiff joint-limit wall (see below) stable instead of jittering. Costs ~linearly. Takes effect on next ragdoll activation.");

                var limitFreq = config.RagdollLimitSpringFrequency;
                if (ImGui.SliderFloat("Limit Wall Stiffness (Hz)##ragdoll", ref limitFreq, 30f, 180f, "%.0f"))
                {
                    config.RagdollLimitSpringFrequency = limitFreq;
                    config.Save();
                }
                HelpMarker("Spring frequency of the joint LIMIT walls (swing cones + twist ranges). Higher = firmer wall so joints don't over-rotate past their range, but too high for the substep count over-drives the solver into jitter. 60 = soft, 90 = balanced default, 120+ = firm (raise Solver Substeps to match). Takes effect on next ragdoll activation.");

                var experimentalJointFrames = config.RagdollExperimentalJointFrames;
                if (ImGui.Checkbox("Experimental Joint Frames##ragdoll", ref experimentalJointFrames))
                {
                    config.RagdollExperimentalJointFrames = experimentalJointFrames;
                    config.Save();
                }
                HelpMarker("Uses parent/child anatomical joint frames for hinge axes and ball-joint twist references. Disable this if an unusual skeleton's joints behave incorrectly. Takes effect on next ragdoll activation.");

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
                HelpMarker("On death the weapon detaches from the hand and falls with its own physics. Always active while ragdoll is enabled; tune the parameters below.");

                {
                    var wdGravity = config.WeaponDropGravity;
                    if (ImGui.SliderFloat("Gravity##weapondrop", ref wdGravity, 0.0f, 30.0f, "%.2f"))
                    {
                        config.WeaponDropGravity = wdGravity;
                        config.Save();
                    }

                    var wdDamping = config.WeaponDropDamping;
                    if (ImGui.SliderFloat("Damping##weapondrop", ref wdDamping, 0.80f, 1.00f, "%.3f"))
                    {
                        config.WeaponDropDamping = wdDamping;
                        config.Save();
                    }
                    HelpMarker("Per-frame velocity multiplier. 1.0 = no damping, lower values settle faster.");

                    var wdAngDamping = config.WeaponDropAngularDamping;
                    if (ImGui.SliderFloat("Angular Damping##weapondrop", ref wdAngDamping, 0.50f, 1.00f, "%.3f"))
                    {
                        config.WeaponDropAngularDamping = wdAngDamping;
                        config.Save();
                    }
                    HelpMarker("Spin damping per frame. Lower than Linear Damping so the capsule stops rotating on contact and does not roll away.");

                    var wdBounce = config.WeaponDropBounce;
                    if (ImGui.SliderFloat("Bounce##weapondrop", ref wdBounce, 0.0f, 5.0f, "%.2f"))
                    {
                        config.WeaponDropBounce = wdBounce;
                        config.Save();
                    }
                    HelpMarker("BepuPhysics MaximumRecoveryVelocity — higher = bouncier weapons.");

                    var wdFriction = config.WeaponDropFriction;
                    if (ImGui.SliderFloat("Friction##weapondrop", ref wdFriction, 0.0f, 2.0f, "%.2f"))
                    {
                        config.WeaponDropFriction = wdFriction;
                        config.Save();
                    }

                    var wdMass = config.WeaponDropMass;
                    if (ImGui.SliderFloat("Mass (kg)##weapondrop", ref wdMass, 0.1f, 10.0f, "%.2f"))
                    {
                        config.WeaponDropMass = wdMass;
                        config.Save();
                    }

                    var wdRadius = config.WeaponDropRadius;
                    if (ImGui.SliderFloat("Capsule Radius##weapondrop", ref wdRadius, 0.005f, 0.2f, "%.3f"))
                    {
                        config.WeaponDropRadius = wdRadius;
                        config.Save();
                    }

                    var wdHalf = config.WeaponDropHalfLength;
                    if (ImGui.SliderFloat("Capsule Half-Length##weapondrop", ref wdHalf, 0.1f, 1.5f, "%.2f"))
                    {
                        config.WeaponDropHalfLength = wdHalf;
                        config.Save();
                    }

                    var wdSolver = config.WeaponDropSolverIterations;
                    if (ImGui.SliderInt("Solver Iterations##weapondrop", ref wdSolver, 1, 64))
                    {
                        config.WeaponDropSolverIterations = wdSolver;
                        config.Save();
                    }
                    HelpMarker("Higher = more stable contact resolution, more CPU. Changing physics params clears all currently-dropped weapons.");
                }

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

                ImGui.Separator();
                ImGui.Text("Death Ragdoll");

                var npcRagdoll = config.EnableNpcDeathRagdoll;
                if (ImGui.Checkbox("Ragdoll enemies on death##npcragdoll", ref npcRagdoll))
                {
                    config.EnableNpcDeathRagdoll = npcRagdoll;
                    config.PartyEnemyDeathRagdoll = npcRagdoll;
                    config.Save();
                }
                HelpMarker("Apply ragdoll physics to enemy NPCs when they die in combat, including party mode enemies.");

                if (config.EnableNpcDeathRagdoll)
                {
                    var npcDelay = config.NpcRagdollActivationDelay;
                    if (ImGui.SliderFloat("Enemy activation delay (s)##npcragdoll", ref npcDelay, 0.0f, 5.0f, "%.1f"))
                    {
                        config.NpcRagdollActivationDelay = npcDelay;
                        config.Save();
                    }
                    HelpMarker("Seconds after enemy death before ragdoll physics take over.");
                }

                var companionRagdoll = config.PartyCompanionDeathRagdoll;
                if (ImGui.Checkbox("Ragdoll companions on death##companionragdoll", ref companionRagdoll))
                {
                    config.PartyCompanionDeathRagdoll = companionRagdoll;
                    config.Save();
                }
                HelpMarker("Apply ragdoll physics to party companion clones when they die.");

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

                var convexHull = config.RagdollNpcCollisionConvexHull;
                if (ImGui.Checkbox("Convex hull collision##npccolconvex", ref convexHull))
                {
                    config.RagdollNpcCollisionConvexHull = convexHull;
                    config.Save();
                }
                HelpMarker("Each collision target uses a single convex hull built from all bone positions. Eliminates inter-capsule gaps on mounts and monsters. Shape is a snapshot of the activation pose; root position/rotation tracks animation. Takes effect on next ragdoll activation.");

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

            var showHoldToolbar = config.ShowHoldToolbar;
            if (ImGui.Checkbox("Show Hold Toolbar##dev", ref showHoldToolbar))
            {
                config.ShowHoldToolbar = showHoldToolbar;
                config.Save();
            }
            HelpMarker("Floating toolbar to toggle standing hold on the active ragdoll while an NPC continues attacking.");

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

            var companionAppearance = config.DevCompanionAppearanceVariant;
            if (ImGui.Checkbox("Ripoff Parties##dev", ref companionAppearance))
            {
                config.DevCompanionAppearanceVariant = companionAppearance;
                config.Save();
            }
            HelpMarker("Applies an alternate visual state to party companions after they are defeated. Reset restores the original appearance.");

            var partyApproachDebug = config.DevPartyApproachDebugLog;
            if (ImGui.Checkbox("Party Approach Debug Log##dev", ref partyApproachDebug))
            {
                config.DevPartyApproachDebugLog = partyApproachDebug;
                config.Save();
            }
            HelpMarker("Logs enemy party-approach plan, waypoint, steering, and range state about twice per second per enemy. Use briefly for 1v10 debugging.");

            var autoEngage = config.EnableNpcAutoEngage;
            if (ImGui.Checkbox("NPC Auto Engage##dev", ref autoEngage))
            {
                config.EnableNpcAutoEngage = autoEngage;
                config.Save();
            }
            HelpMarker("Selected enemy NPCs start attacking the player automatically on Start / Reset / Reboot, without the player having to attack them first.");
            if (config.EnableNpcAutoEngage)
            {
                ImGui.Indent();
                var autoEngageDelay = config.NpcAutoEngageDelay;
                if (ImGui.SliderFloat("Engage Delay##dev", ref autoEngageDelay, 0f, 20f, "%.1f s"))
                {
                    config.NpcAutoEngageDelay = autoEngageDelay;
                    config.Save();
                }
                HelpMarker("Seconds to wait after Start / Reset / Reboot before NPCs start attacking.");
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

        var enabled = config.EnableActiveCamera;
        if (ImGui.Checkbox("##acEnable", ref enabled))
        {
            config.EnableActiveCamera = enabled;
            config.Save();
            activeCameraController.SetActive(enabled);
        }

        ImGui.SameLine();

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

    private static readonly (string Bone, string Label, float DefaultHeight)[] HoldAnchorBones =
    {
        ("j_kosi",   "Pelvis",       0.92f),
        ("j_sebo_a", "Lower Spine",  1.05f),
        ("j_sebo_b", "Mid Spine",    1.15f),
        ("j_sebo_c", "Chest",        1.25f),
        ("j_kubi",   "Neck",         1.45f),
    };

    public void DrawHoldToolbar(Dev.BoneHoldTestModeController executionModeController)
    {
        if (!ImGui.Begin("Hold", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var active = executionModeController.IsActive;

        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1f));

        if (ImGui.Button(active ? "Release##hold" : "Hold##hold"))
        {
            if (active)
                executionModeController.Stop();
            else
                executionModeController.TryStart(npcSelector.SelectedNpcs, config.HoldAnchorBone, config.HoldStandingHeight, config.HoldNpcAttack);
        }

        ImGui.SameLine();
        var atk = config.HoldNpcAttack;
        if (ImGui.Checkbox("Atk##hold", ref atk))
        { config.HoldNpcAttack = atk; config.Save(); }

        if (active)
            ImGui.PopStyleColor();

        if (!active)
        {
            ImGui.SameLine();

            // Bone selector dropdown.
            var currentLabel = "Pelvis";
            var currentIdx = 0;
            for (int i = 0; i < HoldAnchorBones.Length; i++)
            {
                if (HoldAnchorBones[i].Bone == config.HoldAnchorBone)
                {
                    currentLabel = HoldAnchorBones[i].Label;
                    currentIdx = i;
                    break;
                }
            }
            ImGui.SetNextItemWidth(90);
            if (ImGui.BeginCombo("##holdBone", currentLabel))
            {
                for (int i = 0; i < HoldAnchorBones.Length; i++)
                {
                    var selected = i == currentIdx;
                    if (ImGui.Selectable(HoldAnchorBones[i].Label, selected))
                    {
                        config.HoldAnchorBone = HoldAnchorBones[i].Bone;
                        config.HoldStandingHeight = HoldAnchorBones[i].DefaultHeight;
                        config.Save();
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bone to pin in place");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            var h = config.HoldStandingHeight;
            if (ImGui.DragFloat("##holdH", ref h, 0.02f, 0.2f, 2.0f, "%.2fm"))
            { config.HoldStandingHeight = h; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Height above death position");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled("active");
        }

        ImGui.End();
    }

    public void Dispose()
    {
    }
}
