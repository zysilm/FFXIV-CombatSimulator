// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Recipes;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Spectators;
using CombatSimulator.Targeting;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Gui;

public partial class MainWindow : IDisposable
{
    private static readonly (string Label, string BoneName)[] FightingModeTranslateBones =
    {
        ("Hips (j_kosi)", "j_kosi"),
        ("Lower Spine (j_sebo_a)", "j_sebo_a"),
        ("Mid Spine (j_sebo_b)", "j_sebo_b"),
        ("Upper Spine (j_sebo_c)", "j_sebo_c"),
        ("Neck (j_kubi)", "j_kubi"),
        ("Head (j_kao)", "j_kao"),
    };

    private static readonly string[] NpcCollisionModeLabels = { "Fast", "Accurate" };

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
    private readonly DismembermentController dismembermentController;
    private readonly ActiveCameraController activeCameraController;
    private readonly DynamicCameraController dynamicCameraController;
    private readonly HookSafetyChecker hookSafetyChecker;
    private readonly UseActionHook useActionHook;
    private readonly PlayerTargetController playerTargetController;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly CombatRecipeBook recipeBook;
    private readonly IPluginLog log;

    // Conflict confirmation popup
    private bool showConflictConfirmPopup;

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();
    private int glamourerSelectedIndex = -1;
    private int glamourerResetSelectedIndex = -1;
    private bool showControlsPopup;

    // Local player's skeleton bone names (Virtual Enemies look-at bone fallback list).
    private string[] skeletonBoneNames = Array.Empty<string>();
    private string virtualEnemyLookAtBoneFilter = "";
    private IReadOnlyList<string> virtualEnemyLookAtBoneNames = Array.Empty<string>();
    public Func<IReadOnlyList<string>>? VirtualEnemyLookAtBonesProvider { get; set; }
    private List<(string Label, string Path)>? actionVfxCache;
    private string guardSuccessVfxFilter = "";
    private string enemyWarningVfxFilter = "";
    private string hitVfxFilter = "";

    private static readonly string[] ActionGuardKeyLabels = { "Shift", "Ctrl", "Alt", "None" };
    private static readonly int[] ActionGuardKeyValues = { 16, 17, 18, 0 };
    private static readonly string[] ActionGamepadButtonLabels = { "R1", "L1", "R2", "L2", "Circle / East", "Cross / South", "Square / West", "Triangle / North", "None" };
    private static readonly GamepadButtons[] ActionGamepadButtonValues =
    {
        GamepadButtons.R1,
        GamepadButtons.L1,
        GamepadButtons.R2,
        GamepadButtons.L2,
        GamepadButtons.East,
        GamepadButtons.South,
        GamepadButtons.West,
        GamepadButtons.North,
        GamepadButtons.None,
    };
    private static readonly Regex AvfxPathRegex = new(@"\u0000([a-zA-Z0-9\/_]*?)\.avfx", RegexOptions.Compiled);


    // Optional experimental dev hooks — implemented only when the private module is compiled in;
    // elided to no-ops otherwise (so the public build shows no dev section / entry / PC dismember).
    partial void InitDevExperimental(CombatSimulator.Dev.IDevExperimental devExperimental);
    partial void DrawDevSidebarEntry(ref int selectedTab);
    partial void DrawDevTabContent(int selectedTab);
    partial void DrawDevSection();
    partial void DrawExperimentalEffects();
    partial void DrawPcDismemberSection();
    partial void GetDevExperimentalUnlocked(ref bool unlocked);
    partial void DrawPrivateEffectsSection();

    public bool DevExperimentalUnlocked
    {
        get
        {
            var unlocked = false;
            GetDevExperimentalUnlocked(ref unlocked);
            return unlocked;
        }
    }

    // Virtual Enemies section state
    private NpcCatalog? npcCatalog;
    private string spawnSearchFilter = "";
    private int spawnCategoryIndex = 0; // 0=Popular, 1=Recent, 2=Human, 3=DemiHuman, 4=Monster, 5=All
    private NpcCatalogEntry? selectedCatalogEntry;
    private int selectedRecipeIndex = 0;

    private static readonly string[] SpawnCategoryNames =
        { "Popular", "Recent", "Human", "DemiHuman", "Monster", "All" };

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
        DismembermentController dismembermentController,
        ActiveCameraController activeCameraController,
        DynamicCameraController dynamicCameraController,
        HookSafetyChecker hookSafetyChecker,
        UseActionHook useActionHook,
        PlayerTargetController playerTargetController,
        SpectatorController spectatorController,
        CombatSimulator.Dev.IDevExperimental devExperimental,
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
        this.dismembermentController = dismembermentController;
        this.activeCameraController = activeCameraController;
        this.dynamicCameraController = dynamicCameraController;
        this.hookSafetyChecker = hookSafetyChecker;
        this.useActionHook = useActionHook;
        this.playerTargetController = playerTargetController;
        this.spectatorController = spectatorController;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.log = log;
        this.recipeBook = new CombatRecipeBook(log);
        InitDevExperimental(devExperimental);
    }

    private int selectedTab = 0;

    private static readonly string[] TabNames = new[]
    {
        "Combat",
        "Targets",
        "Party",
        "Effects",
        "Camera",
        "Ragdoll",
        "Virtual Enemies",
        "Spectators (Experimental)",
        "Settings",
        "Diagnose",
    };

    /// <summary>Sidebar index of a tab by name. Jump targets resolve through this rather than
    /// hardcoding an index — inserting a tab used to silently repoint every jump below it.</summary>
    private static int TabIndex(string name) => Array.IndexOf(TabNames, name);

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
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 240), new Vector2(float.MaxValue));
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

        // Restored/docked windows can briefly have less space than the sidebar
        // and content minimums. Never pass an inverted interval to Math.Clamp.
        if (totalWidth < 230f || contentHeight <= 0f)
        {
            ImGui.TextWrapped("Expand the window to show the controls.");
            ImGui.End();
            return;
        }

        // Clamp sidebar width
        sidebarWidth = Math.Clamp(sidebarWidth, 80f, totalWidth - 150f);

        // Left sidebar
        ImGui.BeginChild("##sidebar", new Vector2(sidebarWidth, contentHeight), true);
        for (int i = 0; i < TabNames.Length; i++)
        {
            if (ImGui.Selectable(TabNames[i], selectedTab == i))
                selectedTab = i;
        }
        DrawDevSidebarEntry(ref selectedTab);
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
                // Hidden pending a security review (see update log 2.6.0.1); only
                // reachable via the Dev Experimental unlock.
                if (DevExperimentalUnlocked)
                    DrawFightingModeSection();
                DrawActionModeSection();
                break;
            case 1: // Targets
                DrawNpcAttackRangeSection();
                DrawEnemyTacticsSection();
                DrawMapEnemiesSection();
                break;
            case 2: // Party
                DrawPartyTab();
                break;
            case 3: // Effects
                DrawHitVfxSection();
                DrawArrowImpactSection();
                DrawPrivateEffectsSection();
                DrawExperimentalEffects();
                DrawArmorDetachmentEntrySection();
                DrawEnemyControlEntrySection();
                DrawRagdollFollowEntrySection();
                DrawGlamourerHeaderSection();
                break;
            case 4: // Camera
                DrawActiveCamSection();
                DrawDynamicCamSection();
                break;
            case 5: // Ragdoll
                DrawRagdollSection();
                DrawGuidedCollapseSection();
                DrawNpcCollisionSection();
                ImGui.Separator();
                if (ImGui.Button("Reset All to Defaults##ragdollpage"))
                    config.ResetRagdollPageDefaults();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Restore every option on this page to its default.\n" +
                                     "Leaves Enable Ragdoll alone: resetting it would switch the feature off and take the page with it.");
                break;
            case 6: // Virtual Enemies
                DrawVirtualEnemiesTab();
                break;
            case 7: // Spectators (Experimental)
                DrawSpectatorsTab();
                break;
            case 8: // Settings
                DrawGuiSettingsSection();
                break;
            case 9: // Diagnose
                DrawDiagnoseSection();
                break;
            default:
                DrawDevTabContent(selectedTab);
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
                selectedTab = TabIndex("Diagnose");
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
        SyncSelectedRecipeFromConfig(recipes);

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

            var actionMode = config.ActionMode;
            if (ImGui.Checkbox("Action Mode", ref actionMode))
            {
                config.ActionMode = actionMode;
                if (actionMode)
                    config.FightingMode = false;
                config.Save();
            }
            HelpMarker(BuildActionModeQuickHelp());

            // Hidden pending a security review of its direct position/rotation writes
            // (see update log 2.6.0.1) — only reachable via the Dev Experimental unlock.
            if (DevExperimentalUnlocked)
            {
                var fightingMode = config.FightingMode;
                if (ImGui.Checkbox("Fighting Mode [Experimental]", ref fightingMode))
                {
                    config.FightingMode = fightingMode;
                    if (fightingMode)
                        config.ActionMode = false;
                    config.Save();
                }
                HelpMarker("Experimental 1v1 side-view mode. Press an attack on an enemy to lock the pair into a 2D fighting lane and side camera. Ignores profiles while active.");
            }
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
            HelpMarker("Hide character names and death labels in HP overlays.");
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

    private string BuildActionModeQuickHelp()
    {
        var guardKeyboard = FormatActionKey(config.ActionGuardKey);
        var guardPad = FormatGamepadButton(config.ActionGuardGamepadButton);
        var basicKeyboard = FormatActionKey(config.ActionBasicAttackKey);
        var basicPad = FormatGamepadButton(config.ActionBasicAttackGamepadButton);

        return "Action Mode turns combat into real-time soft-target action combat.\n\n" +
               "Hotbar actions use their real range/shape and auto-pick targets in front of you.\n" +
               "Enemy attacks show parry rings; press guard as the outer ring meets the inner ring.\n\n" +
               $"Guard: keyboard {guardKeyboard}, gamepad {guardPad}\n" +
               $"Basic attack: keyboard {basicKeyboard}, gamepad {basicPad}\n" +
               "Skills: your normal hotbar buttons.";
    }

    private static string FormatActionKey(int key)
        => key switch
        {
            0 => "None",
            16 => "Shift",
            17 => "Ctrl",
            18 => "Alt",
            0x51 => "Q",
            >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            _ => $"VK {key}",
        };

    private static string FormatGamepadButton(GamepadButtons button)
        => button switch
        {
            GamepadButtons.R1 => "R1",
            GamepadButtons.L1 => "L1",
            GamepadButtons.R2 => "R2",
            GamepadButtons.L2 => "L2",
            GamepadButtons.East => "Circle / East",
            GamepadButtons.South => "Cross / South",
            GamepadButtons.West => "Square / West",
            GamepadButtons.North => "Triangle / North",
            GamepadButtons.None => "None",
            _ => button.ToString(),
        };

    private void DrawRecipeCombo(string id, float width)
    {
        var recipes = recipeBook.Recipes;
        SyncSelectedRecipeFromConfig(recipes);
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
            {
                selectedRecipeIndex = i;
                config.FastCombatRecipeName = recipes[i].Name;
                config.Save();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void SyncSelectedRecipeFromConfig(IReadOnlyList<CombatRecipe> recipes)
    {
        if (recipes.Count == 0)
        {
            selectedRecipeIndex = 0;
            return;
        }

        if (selectedRecipeIndex >= 0 &&
            selectedRecipeIndex < recipes.Count &&
            recipes[selectedRecipeIndex].Name == config.FastCombatRecipeName)
            return;

        if (!string.IsNullOrWhiteSpace(config.FastCombatRecipeName))
        {
            for (var i = 0; i < recipes.Count; i++)
            {
                if (string.Equals(recipes[i].Name, config.FastCombatRecipeName, StringComparison.Ordinal))
                {
                    selectedRecipeIndex = i;
                    return;
                }
            }
        }

        if (selectedRecipeIndex < 0 || selectedRecipeIndex >= recipes.Count)
            selectedRecipeIndex = 0;
    }

    public void DrawFastCombatToolbar()
    {
        using var toolbarStyle = PushCompactToolbarStyle();
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
        RestoreToolbarFontScale();

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

        if (config.FightingMode)
        {
            config.ActionMode = false;
            config.EnableCombatCompanions = false;
            config.SensePartyMembers = false;
            config.EnableMapPlayerEnemySensing = false;
            npcSpawner.SpawnModeActive = false;
            mapEnemyController.ClearRecipeSettings();
            combatEngine.StartSimulation();
            chatGui.Print("[CombatSim] Fighting Mode started. Attack one enemy to begin a 1v1.");
            return;
        }

        // Recipe-scoped NPC collision. Null leaves the user's own setting in force; the
        // StopFastCombat above has already cleared any previous recipe's override.
        config.RecipeNpcCollisionOverride = recipe.NpcCollision;

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
            if (!entry.IsSpawnable)
            {
                chatGui.PrintError(
                    $"[CombatSim] Recipe enemy '{entry.Name}' has no complete spawn source data.");
                continue;
            }

            var count = Math.Max(0, group.Count);
            for (var i = 0; i < count; i++)
            {
                npcSpawner.QueueSpawn(new NpcSpawnRequest
                {
                    DisplayName = entry.Name,
                    BNpcBaseId = entry.Source == NpcCatalogSource.BNpcBase ? entry.Id : 0,
                    BNpcNameId = entry.BNpcNameId,
                    ENpcBaseId = entry.Source == NpcCatalogSource.ENpcBase ? entry.Id : 0,
                    ModelCharaId = entry.Source == NpcCatalogSource.ModelChara ? entry.ModelCharaId : 0,
                    Level = Math.Clamp(config.FastCombatLevel, 1, 300),
                    HpMultiplier = Math.Max(0.0001f, group.HpMultiplier),
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
            SyncSelectedRecipeFromConfig(recipes);
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
        config.RecipeNpcCollisionOverride = null;

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

        var maxSkills = config.EnemyMaxRealSkills;
        if (ImGui.SliderInt("Humanoid skills", ref maxSkills, 0, 8))
        {
            config.EnemyMaxRealSkills = Math.Clamp(maxSkills, 0, 8);
            config.Save();
        }
        HelpMarker("Humanoid enemies cast their real job's strongest N damage skills (by weapon → job). " +
                   "Monsters and NPC-only weapons auto-attack only. 0 = everyone auto-attacks.");
        DrawEnemySkillsSection();

        ImGui.Separator();

        var autoCounter = config.EnableAutoCounter;
        if (ImGui.Checkbox("Auto-counter", ref autoCounter))
        {
            config.EnableAutoCounter = autoCounter;
            config.Save();
        }
        HelpMarker("When you have no locked target, being hit by an enemy auto-locks that attacker. Pressing cancel stops auto-counter (no auto-lock even when hit) until you press confirm to lock a target again. Requires custom targeting.");
    }

    private void DrawNpcAttackRangeSection()
    {
        if (!ImGui.CollapsingHeader("Enemy Attack Range", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var scale = Math.Clamp(config.NpcAttackRangeScale, 0.1f, 100f);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat("Attack range scale", ref scale, 0.1f, 100f, "%.2fx",
                ImGuiSliderFlags.Logarithmic))
        {
            config.NpcAttackRangeScale = Math.Clamp(scale, 0.1f, 100f);
            config.Save();
        }
        HelpMarker("Multiplies enemy attack/skill reach and the matching chase/formation stopping distance. " +
                   "Default is 1.00x. Increasing it lets enemies stop farther away and attack from that distance; " +
                   "very small NPC Scale values may need a large multiplier to restore usable melee reach.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##npcAttackRangeScale"))
        {
            config.NpcAttackRangeScale = 1f;
            config.Save();
        }
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
            3 => npcCatalog.Search(spawnSearchFilter, NpcCatalogType.DemiHuman),
            4 => npcCatalog.Search(spawnSearchFilter, NpcCatalogType.Monster),
            5 => npcCatalog.Search(spawnSearchFilter),
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
                ImGuiClip.ClippedDraw(entries, entry =>
                {
                    bool isSelected = selectedCatalogEntry != null &&
                                      selectedCatalogEntry.Id == entry.Id &&
                                      selectedCatalogEntry.Source == entry.Source &&
                                      selectedCatalogEntry.BNpcNameId == entry.BNpcNameId;
                    var visibleName = entry.HasDuplicateName
                        ? $"{entry.Name}  [Base #{entry.Id}, Model #{entry.ModelCharaId}]"
                        : entry.Name;
                    if (ImGui.Selectable(
                            $"{visibleName}##cat{entry.Source}{entry.Id}_{entry.BNpcNameId}",
                            isSelected))
                    {
                        selectedCatalogEntry = entry;
                    }
                }, 1, ImGui.GetTextLineHeightWithSpacing());
            }
            ImGui.EndListBox();
        }

        if (selectedCatalogEntry != null)
        {
            ImGui.TextDisabled(
                $"{selectedCatalogEntry.Type} | {selectedCatalogEntry.Source} #{selectedCatalogEntry.Id} | " +
                $"ModelChara #{selectedCatalogEntry.ModelCharaId}");
            if (!selectedCatalogEntry.IsSpawnable)
                ImGui.TextColored(
                    new Vector4(1f, 0.65f, 0.2f, 1f),
                    "This ModelChara-only DemiHuman lacks the required customize/equipment source data.");
        }

        // Spawn settings
        ImGui.Spacing();
        var inFront = config.SpawnInFront;
        if (ImGui.Checkbox("Front##spawninfront", ref inFront))
        {
            config.SpawnInFront = inFront;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Spawn virtual enemies directly in front of your character, using its real\n" +
                             "in-game facing (not any ragdoll/visual facing). Also applies when combat\n" +
                             "reset regenerates/refreshes enemies. Off = a loose, layered ring around you.");
        ImGui.SameLine();

        var dist = config.SpawnDistance;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("Distance", ref dist, 0.0f, 15.0f, "%.1f yalms"))
        {
            config.SpawnDistance = dist;
            config.Save();
        }
        HelpMarker(config.SpawnInFront
            ? "Enemies spawn in front of your character (real in-game facing)."
            : "Enemies surround your character in loose layers; the nearest stands at this distance.");

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
        bool canSpawn = selectedCatalogEntry is { IsSpawnable: true } &&
                        npcSpawner.TotalCount < npcSpawner.MaxNpcs;
        if (!canSpawn) ImGui.BeginDisabled();
        if (ImGui.Button("Spawn", new Vector2(80, 0)))
        {
            var entry = selectedCatalogEntry!;

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
                DisplayName = entry.Name,
                BNpcBaseId = entry.Source == NpcCatalogSource.BNpcBase ? entry.Id : 0,
                BNpcNameId = entry.BNpcNameId,
                ENpcBaseId = entry.Source == NpcCatalogSource.ENpcBase ? entry.Id : 0,
                ModelCharaId = entry.Source == NpcCatalogSource.ModelChara ? entry.ModelCharaId : 0,
                Level = config.DefaultNpcLevel,
                HpMultiplier = config.DefaultNpcHpMultiplier,
                // No Position: the spawner places it. This used to pass a point computed here by a
                // second copy of the placement logic, which meant the spawner's own version was
                // never reached from this button — so the fan that keeps a group from stacking
                // silently did nothing. One owner for where an enemy goes.
            });

            // Track in recent list (avoid duplicates, keep last 20)
            config.RecentNpcEntries.RemoveAll(r => r.BNpcBaseId == entry.Id && r.Source == entry.Source);
            config.RecentNpcEntries.Insert(0, new RecentNpcEntry
            {
                BNpcBaseId = entry.Id,
                BNpcNameId = entry.BNpcNameId,
                ModelCharaId = entry.ModelCharaId,
                Type = entry.Type,
                Source = entry.Source,
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

        DrawVirtualEnemyLookAtSection();
    }

    private void DrawVirtualEnemyLookAtSection()
    {
        ImGui.Separator();
        var lookAt = config.VirtualEnemyLookAtTarget;
        if (ImGui.Checkbox("Look at target##veLookAt", ref lookAt))
        {
            config.VirtualEnemyLookAtTarget = lookAt;
            config.Save();
        }
        HelpMarker("Virtual enemies look at their combat target while you are alive. After your defeat, living enemies keep watching you, including during victory sequences, grabs and other control modes. Turns at most 70° sideways and 35° up/down. Off by default.");
        if (!lookAt) return;
        ImGui.Indent();
        var bone = config.VirtualEnemyLookAtBone ?? "j_kao";
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo("Bone##veLookAt", VirtualEnemyBoneLabel(bone)))
        {
            if (ImGui.IsWindowAppearing())
            {
                RefreshSkeletonBones();
                virtualEnemyLookAtBoneNames = VirtualEnemyLookAtBonesProvider?.Invoke() ?? skeletonBoneNames;
                virtualEnemyLookAtBoneFilter = "";
                ImGui.SetKeyboardFocusHere();
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##veLookAtBoneSearch", "Search bones...", ref virtualEnemyLookAtBoneFilter, 128);
            var available = virtualEnemyLookAtBoneNames.Count > 0 ? virtualEnemyLookAtBoneNames : new[] { "j_kao", "j_kubi", "j_sebo_c" };
            ImGui.BeginChild("##veLookAtBoneList", new Vector2(0, 230), false);
            foreach (var name in available.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var label = VirtualEnemyBoneLabel(name);
                if (!label.Contains(virtualEnemyLookAtBoneFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ImGui.Selectable(label, name == bone)) continue;
                config.VirtualEnemyLookAtBone = name;
                config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndChild();
            ImGui.EndCombo();
        }
        HelpMarker("Which bone turns toward the target. j_kao = head (default), j_kubi = neck (head follows), j_sebo_c = upper chest (neck and head follow). Enemies without this bone are left alone.");
        ImGui.Unindent();
    }

    private static string VirtualEnemyBoneLabel(string name) => name switch
    {
        "j_kao" => "Head (j_kao)",
        "j_kubi" => "Neck (j_kubi)",
        "j_sebo_c" => "Upper chest (j_sebo_c)",
        _ => name
    };

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
                config.Save();
            }
            HelpMarker("Global damage multiplier applied to all attacks.");

            var crit = config.EnableCriticalHits;
            if (ImGui.Checkbox("Critical Hits", ref crit))
            {
                config.EnableCriticalHits = crit;
                config.Save();
            }
            HelpMarker("Enable critical hit chance on attacks.");

            ImGui.SameLine();
            var dh = config.EnableDirectHits;
            if (ImGui.Checkbox("Direct Hits", ref dh))
            {
                config.EnableDirectHits = dh;
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

    private void DrawFightingModeSection()
    {
        if (!ImGui.CollapsingHeader("Fighting Mode [Experimental]"))
            return;

        var fightingMode = config.FightingMode;
        if (ImGui.Checkbox("Enable Fighting Mode", ref fightingMode))
        {
            config.FightingMode = fightingMode;
            if (fightingMode)
                config.ActionMode = false;
            config.Save();
        }
        HelpMarker("1v1 side-view combat mode. The first player attack against an enemy locks the pair into a fighting-game lane and camera: 2D movement (forward/back + jump), weapon-contact hit detection, timed guard against telegraphs, a dedicated fighting AI, and a KO camera.");

        if (!config.FightingMode)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Reset defaults##fightingmode"))
        {
            config.ResetFightingModeDefaults();
            config.Save();
        }
        HelpMarker("Reset every Fighting Mode setting (movement, combat, AI, cameras) to defaults. Keeps the mode enabled.");

        ImGui.Separator();
        ImGui.Text("Lane");

        var minSep = config.FightingModeMinSeparation;
        if (ImGui.SliderFloat("Min separation##fightingmode", ref minSep, 0.1f, 1.5f, "%.2f"))
        {
            config.FightingModeMinSeparation = minSep;
            config.Save();
        }
        HelpMarker("Closest the pair can stand: whoever moves is clamped this far from the other along the lane.");

        ImGui.Separator();
        ImGui.Text("Input");
        HelpMarker("Fighting Mode uses no keys of its own. Movement, run and jump are the game's own bindings (projected onto the 2D lane); a second jump press mid-air vaults over the enemy. The basic attack and guard reuse the Action Mode binds — configure them under Action Mode.");

        var vaultDur = config.FightingModeVaultDuration;
        if (ImGui.SliderFloat("Vault duration##fightingmode", ref vaultDur, 0.2f, 1.2f, "%.2f s"))
        {
            config.FightingModeVaultDuration = vaultDur;
            config.Save();
        }
        HelpMarker("How long the mid-air vault arc over the enemy lasts. Triggered by a second press of your game jump bind while airborne.");

        ImGui.Separator();
        ImGui.Text("Weapon Combat");

        var debugDraw = config.FightingModeDebugDraw;
        if (ImGui.Checkbox("Debug draw##fightingmode", ref debugDraw))
        {
            config.FightingModeDebugDraw = debugDraw;
            config.Save();
        }
        HelpMarker("Draws the live weapon segment (yellow; red while the hit window is open) and the enemy hurtbox capsule. Essential for tuning the values below.");

        var weaponLen = config.FightingModeWeaponLength;
        if (ImGui.SliderFloat("Weapon length##fightingmode", ref weaponLen, 0.2f, 3.0f, "%.2f"))
        {
            config.FightingModeWeaponLength = weaponLen;
            config.Save();
        }
        HelpMarker("Used for single-bone weapons and the unarmed/hand-bone fallback. Multi-bone weapons derive length from their own skeleton.");

        var weaponScale = config.FightingModeWeaponLengthScale;
        if (ImGui.SliderFloat("Weapon length scale##fightingmode", ref weaponScale, 0.5f, 2.0f, "%.2f"))
        {
            config.FightingModeWeaponLengthScale = weaponScale;
            config.Save();
        }

        var weaponRadius = config.FightingModeWeaponRadius;
        if (ImGui.SliderFloat("Weapon radius##fightingmode", ref weaponRadius, 0.02f, 0.6f, "%.2f"))
        {
            config.FightingModeWeaponRadius = weaponRadius;
            config.Save();
        }

        var weaponAxis = config.FightingModeWeaponAxis;
        if (ImGui.Combo("Weapon axis##fightingmode", ref weaponAxis, "X\0Y\0Z\0"))
        {
            config.FightingModeWeaponAxis = Math.Clamp(weaponAxis, 0, 2);
            config.Save();
        }
        HelpMarker("Blade direction in the weapon's local space, for single-bone weapons only.");

        var hurtHeight = config.FightingModeHurtboxHeight;
        if (ImGui.SliderFloat("Hurtbox height##fightingmode", ref hurtHeight, 0.5f, 5.0f, "%.2f"))
        {
            config.FightingModeHurtboxHeight = hurtHeight;
            config.Save();
        }

        var hurtScale = config.FightingModeHurtboxRadiusScale;
        if (ImGui.SliderFloat("Hurtbox radius scale##fightingmode", ref hurtScale, 0.3f, 3.0f, "%.2f"))
        {
            config.FightingModeHurtboxRadiusScale = hurtScale;
            config.Save();
        }

        var activeStart = config.FightingModeAttackActiveStartPct;
        if (ImGui.SliderFloat("Hit window start##fightingmode", ref activeStart, 0f, 0.9f, "%.2f"))
        {
            config.FightingModeAttackActiveStartPct = activeStart;
            config.Save();
        }
        HelpMarker("Active hit window as a fraction of the attack animation: contact only counts between start and end.");

        var activeEnd = config.FightingModeAttackActiveEndPct;
        if (ImGui.SliderFloat("Hit window end##fightingmode", ref activeEnd, 0.1f, 1f, "%.2f"))
        {
            config.FightingModeAttackActiveEndPct = MathF.Max(activeEnd, config.FightingModeAttackActiveStartPct + 0.05f);
            config.Save();
        }

        ImGui.Separator();
        ImGui.Text("Fighting AI");

        var aiSpeed = config.FightingAiMoveSpeed;
        if (ImGui.SliderFloat("AI move speed##fightingai", ref aiSpeed, 0.5f, 8.0f, "%.1f"))
        {
            config.FightingAiMoveSpeed = aiSpeed;
            config.Save();
        }

        var aiRetreatScale = config.FightingAiRetreatSpeedScale;
        if (ImGui.SliderFloat("Retreat speed scale##fightingai", ref aiRetreatScale, 0.1f, 1.0f, "%.2f"))
        {
            config.FightingAiRetreatSpeedScale = aiRetreatScale;
            config.Save();
        }
        HelpMarker("Backpedal speed as a fraction of move speed — keep it below your run speed or the enemy is uncatchable.");

        var aiRangeMin = config.FightingAiRangeMin;
        if (ImGui.SliderFloat("Preferred range min##fightingai", ref aiRangeMin, 0.5f, 6.0f, "%.1f"))
        {
            config.FightingAiRangeMin = aiRangeMin;
            config.Save();
        }

        var aiRangeMax = config.FightingAiRangeMax;
        if (ImGui.SliderFloat("Preferred range max##fightingai", ref aiRangeMax, 1.0f, 10.0f, "%.1f"))
        {
            config.FightingAiRangeMax = MathF.Max(aiRangeMax, config.FightingAiRangeMin + 0.2f);
            config.Save();
        }
        HelpMarker("The enemy holds this spacing band: approaches when farther, backs off when closer, attacks from inside it.");

        var aiCooldown = config.FightingAiAttackCooldown;
        if (ImGui.SliderFloat("Attack cooldown##fightingai", ref aiCooldown, 0.3f, 8.0f, "%.1f s"))
        {
            config.FightingAiAttackCooldown = aiCooldown;
            config.Save();
        }

        var aiJitter = config.FightingAiAttackCooldownJitter;
        if (ImGui.SliderFloat("Cooldown jitter##fightingai", ref aiJitter, 0f, 4.0f, "%.1f s"))
        {
            config.FightingAiAttackCooldownJitter = aiJitter;
            config.Save();
        }

        var aiCombo = config.FightingAiComboChance;
        if (ImGui.SliderFloat("Combo chance##fightingai", ref aiCombo, 0f, 1f, "%.2f"))
        {
            config.FightingAiComboChance = aiCombo;
            config.Save();
        }
        HelpMarker("Probability an attack opens a combo string (up to Max combo hits, chained with a short gap).");

        var aiComboMax = config.FightingAiMaxComboHits;
        if (ImGui.SliderInt("Max combo hits##fightingai", ref aiComboMax, 2, 5))
        {
            config.FightingAiMaxComboHits = aiComboMax;
            config.Save();
        }

        var aiComboGap = config.FightingAiComboGap;
        if (ImGui.SliderFloat("Combo gap##fightingai", ref aiComboGap, 0.05f, 0.8f, "%.2f s"))
        {
            config.FightingAiComboGap = aiComboGap;
            config.Save();
        }

        var aiDash = config.FightingAiDashChance;
        if (ImGui.SliderFloat("Dash-in chance##fightingai", ref aiDash, 0f, 1f, "%.2f"))
        {
            config.FightingAiDashChance = aiDash;
            config.Save();
        }
        HelpMarker("When out of range, chance to burst-dash in (at the speed scale below) and attack immediately.");

        var aiDashScale = config.FightingAiDashSpeedScale;
        if (ImGui.SliderFloat("Dash speed scale##fightingai", ref aiDashScale, 1.2f, 4.0f, "%.1f"))
        {
            config.FightingAiDashSpeedScale = aiDashScale;
            config.Save();
        }

        var aiJumpOver = config.FightingAiJumpOverChance;
        if (ImGui.SliderFloat("Jump-over chance##fightingai", ref aiJumpOver, 0f, 1f, "%.2f"))
        {
            config.FightingAiJumpOverChance = aiJumpOver;
            config.Save();
        }
        HelpMarker("Chance the enemy hops over you to the other side instead of attacking.");

        var aiJumpDur = config.FightingAiJumpDuration;
        if (ImGui.SliderFloat("Jump duration##fightingai", ref aiJumpDur, 0.3f, 1.2f, "%.2f s"))
        {
            config.FightingAiJumpDuration = aiJumpDur;
            config.Save();
        }

        var aiJumpHeight = config.FightingAiJumpHeight;
        if (ImGui.SliderFloat("Jump height##fightingai", ref aiJumpHeight, 0.5f, 4.0f, "%.1f"))
        {
            config.FightingAiJumpHeight = aiJumpHeight;
            config.Save();
        }

        var aiJumpIn = config.FightingAiJumpInAttackChance;
        if (ImGui.SliderFloat("Jump-in attack chance##fightingai", ref aiJumpIn, 0f, 1f, "%.2f"))
        {
            config.FightingAiJumpInAttackChance = aiJumpIn;
            config.Save();
        }
        HelpMarker("Chance to attack immediately on landing a jump-over.");

        var aiRetreat = config.FightingAiRetreatChance;
        if (ImGui.SliderFloat("Retreat chance##fightingai", ref aiRetreat, 0f, 1f, "%.2f"))
        {
            config.FightingAiRetreatChance = aiRetreat;
            config.Save();
        }
        HelpMarker("Probability of backing off after each attack instead of staying in.");

        var aiRetreatDur = config.FightingAiRetreatDuration;
        if (ImGui.SliderFloat("Retreat duration##fightingai", ref aiRetreatDur, 0.2f, 3.0f, "%.1f s"))
        {
            config.FightingAiRetreatDuration = aiRetreatDur;
            config.Save();
        }

        var aiRecover = config.FightingAiRecoverTime;
        if (ImGui.SliderFloat("Recover time##fightingai", ref aiRecover, 0.1f, 3.0f, "%.1f s"))
        {
            config.FightingAiRecoverTime = aiRecover;
            config.Save();
        }

        var aiHitstun = config.FightingAiHitstunDuration;
        if (ImGui.SliderFloat("Hitstun duration##fightingai", ref aiHitstun, 0.05f, 1.5f, "%.2f s"))
        {
            config.FightingAiHitstunDuration = aiHitstun;
            config.Save();
        }

        var aiPushback = config.FightingAiHitstunPushback;
        if (ImGui.SliderFloat("Hitstun pushback##fightingai", ref aiPushback, 0f, 5.0f, "%.1f"))
        {
            config.FightingAiHitstunPushback = aiPushback;
            config.Save();
        }
        HelpMarker("Distance the enemy is shoved back along the lane by a weapon-contact hit (super-armored during its own windup).");

        ImGui.Separator();
        ImGui.Text("Camera");

        var camSide = config.FightingModeCameraSide;
        if (ImGui.Combo("Camera side##fightingmode", ref camSide, "Right\0Left\0"))
        {
            config.FightingModeCameraSide = Math.Clamp(camSide, 0, 1);
            config.Save();
        }
        HelpMarker("Which side of the fighting lane the 2D camera views from. Also applies to the KO camera's side-view lock.");

        var margin = config.FightingModeCameraMargin;
        if (ImGui.SliderFloat("Frame margin##fightingmode", ref margin, 0.4f, 1.8f, "%.2f"))
        {
            config.FightingModeCameraMargin = margin;
            config.Save();
        }

        var minDist = config.FightingModeCameraMinDistance;
        if (ImGui.SliderFloat("Min camera distance##fightingmode", ref minDist, 1.5f, 10.0f, "%.2f"))
        {
            config.FightingModeCameraMinDistance = minDist;
            config.Save();
        }

        var maxDist = config.FightingModeCameraMaxDistance;
        if (ImGui.SliderFloat("Max camera distance##fightingmode", ref maxDist, 3.0f, 25.0f, "%.2f"))
        {
            config.FightingModeCameraMaxDistance = MathF.Max(maxDist, config.FightingModeCameraMinDistance + 0.1f);
            config.Save();
        }

        var smooth = config.FightingModeCameraSmoothing;
        if (ImGui.SliderFloat("Camera smoothing##fightingmode", ref smooth, 1.0f, 30.0f, "%.1f"))
        {
            config.FightingModeCameraSmoothing = smooth;
            config.Save();
        }

        var height = config.FightingModeCameraHeight;
        if (ImGui.SliderFloat("Camera center height##fightingmode", ref height, 0.0f, 3.0f, "%.2f"))
        {
            config.FightingModeCameraHeight = height;
            config.Save();
        }

        var vertical = config.FightingModeCameraVerticalAngle;
        if (ImGui.SliderFloat("Vertical angle##fightingmode", ref vertical, -0.8f, 0.5f, "%.2f rad"))
        {
            config.FightingModeCameraVerticalAngle = vertical;
            config.Save();
        }

        ImGui.Separator();
        ImGui.Text("KO Camera");
        HelpMarker("After defeat, while the controlled monster's camera-follow is on, the camera frames the midpoint of your corpse and the monster and zooms out with their separation (fighting-game KO shot). Otherwise the Translate Cam below applies.");

        var koMargin = config.FightingModeKoCameraMargin;
        if (ImGui.SliderFloat("KO distance margin##fightingmode", ref koMargin, 0.2f, 2.0f, "%.2f"))
        {
            config.FightingModeKoCameraMargin = koMargin;
            config.Save();
        }

        var koBase = config.FightingModeKoCameraBase;
        if (ImGui.SliderFloat("KO base distance##fightingmode", ref koBase, 1.0f, 10.0f, "%.1f"))
        {
            config.FightingModeKoCameraBase = koBase;
            config.Save();
        }

        var koMin = config.FightingModeKoCameraMinDistance;
        if (ImGui.SliderFloat("KO min distance##fightingmode", ref koMin, 1.0f, 10.0f, "%.1f"))
        {
            config.FightingModeKoCameraMinDistance = koMin;
            config.Save();
        }

        var koMax = config.FightingModeKoCameraMaxDistance;
        if (ImGui.SliderFloat("KO max distance##fightingmode", ref koMax, 4.0f, 30.0f, "%.1f"))
        {
            config.FightingModeKoCameraMaxDistance = MathF.Max(koMax, config.FightingModeKoCameraMinDistance + 0.5f);
            config.Save();
        }

        var koHeight = config.FightingModeKoCameraHeight;
        if (ImGui.SliderFloat("KO center height##fightingmode", ref koHeight, -1.0f, 3.0f, "%.2f"))
        {
            config.FightingModeKoCameraHeight = koHeight;
            config.Save();
        }

        var koLock = config.FightingModeKoLockAngle;
        if (ImGui.Checkbox("KO side-view lock##fightingmode", ref koLock))
        {
            config.FightingModeKoLockAngle = koLock;
            config.Save();
        }
        HelpMarker("Keeps the camera perpendicular to the corpse-monster axis; off = you keep manual rotation.");

        ImGui.Separator();
        ImGui.Text("Translate Cam");

        var translate = config.FightingModeTranslateCam;
        if (ImGui.Checkbox("Translate cam on defeat##fightingmode", ref translate))
        {
            config.FightingModeTranslateCam = translate;
            config.Save();
        }
        HelpMarker("When the player is defeated, smoothly transition the Fighting Mode camera to a player-bone follow camera. If enabled after defeat, it performs the translate once on the next frame.");

        var duration = config.FightingModeTranslateDuration;
        if (ImGui.SliderFloat("Translate duration##fightingmode", ref duration, 0.1f, 10.0f, "%.2f s"))
        {
            config.FightingModeTranslateDuration = duration;
            config.Save();
        }

        var translateDistance = config.FightingModeTranslateDistance;
        if (ImGui.SliderFloat("Translate zoom distance##fightingmode", ref translateDistance, 1.0f, 20.0f, "%.2f"))
        {
            config.FightingModeTranslateDistance = translateDistance;
            config.Save();
        }

        var bones = FightingModeTranslateBones;
        var boneNames = new string[bones.Length];
        var boneIdx = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            boneNames[i] = bones[i].Label;
            if (bones[i].BoneName == config.FightingModeTranslateBoneName)
                boneIdx = i;
        }

        if (config.FightingModeTranslateBoneName == "n_hara")
        {
            config.FightingModeTranslateBoneName = "j_kosi";
            config.Save();
        }

        if (ImGui.Combo("Track bone##fightingmode", ref boneIdx, boneNames, boneNames.Length))
        {
            config.FightingModeTranslateBoneName = bones[boneIdx].BoneName;
            config.Save();
        }

        var translateHeight = config.FightingModeTranslateHeightOffset;
        if (ImGui.SliderFloat("Track height offset##fightingmode", ref translateHeight, -3.0f, 5.0f, "%.2f"))
        {
            config.FightingModeTranslateHeightOffset = translateHeight;
            config.Save();
        }

        var translateSide = config.FightingModeTranslateSideOffset;
        if (ImGui.SliderFloat("Track side offset##fightingmode", ref translateSide, -5.0f, 5.0f, "%.2f"))
        {
            config.FightingModeTranslateSideOffset = translateSide;
            config.Save();
        }

        var lockH = config.FightingModeTranslateLockHorizontal;
        if (ImGui.Checkbox("Lock horizontal rotation##fightingmode", ref lockH))
        {
            config.FightingModeTranslateLockHorizontal = lockH;
            config.Save();
        }

        if (config.FightingModeTranslateLockHorizontal)
        {
            var h = config.FightingModeTranslateHorizontalAngle;
            if (ImGui.SliderFloat("Horizontal angle##fightingmode", ref h, -MathF.PI, MathF.PI, "%.2f rad"))
            {
                config.FightingModeTranslateHorizontalAngle = h;
                config.Save();
            }
        }

        var lockV = config.FightingModeTranslateLockVertical;
        if (ImGui.Checkbox("Lock vertical rotation##fightingmode", ref lockV))
        {
            config.FightingModeTranslateLockVertical = lockV;
            config.Save();
        }

        if (config.FightingModeTranslateLockVertical)
        {
            var v = config.FightingModeTranslateVerticalAngle;
            if (ImGui.SliderFloat("Vertical angle##fightingmode", ref v, -0.8f, 0.5f, "%.2f rad"))
            {
                config.FightingModeTranslateVerticalAngle = v;
                config.Save();
            }
        }
    }

    private void DrawActionModeSection()
    {
        if (!ImGui.CollapsingHeader("Action Mode [Experimental]"))
            return;

        var actionMode = config.ActionMode;
        if (ImGui.Checkbox("Enable Action Mode", ref actionMode))
        {
            config.ActionMode = actionMode;
            if (actionMode)
                config.FightingMode = false;
            config.Save();
        }
        HelpMarker("Real-time action combat: button-driven light attacks and timed guard, with enemy attacks shown as telegraphs before hitbox resolution. Off = the normal tab-target simulation.");

        if (ImGui.Button("Reset Action Mode Defaults"))
        {
            config.ResetActionModeDefaults();
            config.Save();
            actionMode = config.ActionMode;
        }
        HelpMarker("Restore all Action Mode tuning, input bindings, VFX choices, telegraph, guard, player, and parry-circle settings to built-in defaults. Keeps the current Enable Action Mode state.");

        if (ImGui.Button("Recover Player Animation"))
        {
            ragdollController.Deactivate();
            animationController.RecoverPlayerAnimationState();
        }
        HelpMarker("Force-clear CombatSimulator-owned player animation state if the local player gets stuck in a death, guard, or combat idle pose.");

        if (!actionMode)
            return;

        ImGui.Separator();
        ImGui.Text("Input");
        ImGui.TextWrapped("Light attacks use any intercepted hotbar action. Guard uses an independent key, so it works on every job without action ids.");

        ImGui.Text("Guard key");
        var guardKeyIndex = IndexOf(ActionGuardKeyValues, config.ActionGuardKey);
        if (ImGui.Combo("Keyboard##actionguard", ref guardKeyIndex, ActionGuardKeyLabels, ActionGuardKeyLabels.Length))
        {
            config.ActionGuardKey = ActionGuardKeyValues[guardKeyIndex];
            config.Save();
        }
        HelpMarker("Keyboard key used for Action Mode guard. Default is Ctrl so jump remains untouched.");

        var guardPadIndex = IndexOf(ActionGamepadButtonValues, config.ActionGuardGamepadButton);
        if (ImGui.Combo("Gamepad##actionguard", ref guardPadIndex, ActionGamepadButtonLabels, ActionGamepadButtonLabels.Length))
        {
            config.ActionGuardGamepadButton = ActionGamepadButtonValues[guardPadIndex];
            config.Save();
        }
        HelpMarker("Gamepad button used for Action Mode guard. Default is R2 on a PlayStation-style layout.");

        ImGui.Text("Basic attack");
        var basicPadIndex = IndexOf(ActionGamepadButtonValues, config.ActionBasicAttackGamepadButton);
        if (ImGui.Combo("Gamepad##actionbasicattack", ref basicPadIndex, ActionGamepadButtonLabels, ActionGamepadButtonLabels.Length))
        {
            config.ActionBasicAttackGamepadButton = ActionGamepadButtonValues[basicPadIndex];
            config.Save();
        }
        HelpMarker("Gamepad button used for the no-hotbar basic attack. Default is Cross / South on a PlayStation-style layout.");
        ImGui.TextDisabled("Keyboard: Q");

        ImGui.Separator();
        ImGui.Text("Player");
        SliderFloatSaved("Swing interval", () => config.LightSwingInterval, v => config.LightSwingInterval = v, 0.15f, 1.0f, "Min seconds between light swings.");
        SliderFloatSaved("Combo window", () => config.LightComboWindow, v => config.LightComboWindow = v, 0.2f, 1.5f, "Time to chain the next combo step.");
        SliderFloatSaved("Hitbox range", () => config.PlayerHitboxRange, v => config.PlayerHitboxRange = v, 1f, 10f, "Frontal cone reach (yalms), measured to the target's SURFACE.");
        SliderFloatSaved("Hitbox angle", () => config.PlayerHitboxAngleDeg, v => config.PlayerHitboxAngleDeg = v, 30f, 180f, "Frontal cone full angle (degrees).");
        SliderFloatSaved("Large-enemy reach", () => config.MeleeTargetHitboxReach, v => config.MeleeTargetHitboxReach = v, 0f, 2f, "How much of the target's hitbox radius is added to melee reach, so big enemies are hittable from their surface instead of their centre (small enemies are unchanged). 0 = centre-to-centre, 1 = exact surface (like FFXIV), >1 = lenient.");

        var potency = config.LightAttackPotency;
        if (ImGui.InputInt("Light attack potency", ref potency))
        {
            config.LightAttackPotency = System.Math.Max(1, potency);
            config.Save();
        }

        ImGui.Separator();
        ImGui.Text("Guard");
        SliderFloatSaved("Active window", () => config.GuardActiveWindow, v => config.GuardActiveWindow = v, 0.05f, 0.6f, "Early tolerance: how long a guard press stays active (press up to this long BEFORE the strike).");
        SliderFloatSaved("Late tolerance", () => config.GuardLateTolerance, v => config.GuardLateTolerance = v, 0f, 0.5f, "Grace AFTER the strike closes during which a guard still counts (double-sided). 0 = strict.");
        SliderFloatSaved("Chain window", () => config.ChainGuardWindow, v => config.ChainGuardWindow = v, 0f, 1.0f, "After a block, keep guard open this long to absorb the next attack with the SAME press (chain guard). 0 = single block per press.");
        {
            var maxChain = config.GuardMaxChain;
            if (ImGui.SliderInt("Max chain blocks", ref maxChain, 0, 20))
            {
                config.GuardMaxChain = maxChain;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Max attacks ONE guard press can chain-block before the chain ends and you must re-guard. Stops a single press from blocking dozens of hits from a continuous swarm. 0 = unlimited (legacy).");
        }
        SliderFloatSaved("Recovery", () => config.GuardRecovery, v => config.GuardRecovery = v, 0.05f, 1.0f, "Lockout after a guard CHAIN ends.");
        SliderFloatSaved("Cooldown", () => config.GuardCooldown, v => config.GuardCooldown = v, 0f, 1.0f, "Minimum time between guard attempts.");

        DrawVfxPicker("Guard success VFX", () => config.GuardSuccessVfxPath, v => config.GuardSuccessVfxPath = v, ref guardSuccessVfxFilter);
        DrawVfxPicker("Enemy warning VFX", () => config.EnemyTelegraphVfxPath, v => config.EnemyTelegraphVfxPath = v, ref enemyWarningVfxFilter);

        ImGui.Separator();
        ImGui.Text("Enemy / companion pacing");
        SliderFloatSaved("Attack pace", () => config.ActionEnemyAttackSpeed, v => config.ActionEnemyAttackSpeed = v, 0.5f, 3.0f,
            "Enemies and companions initiate attacks this many times faster (auto delay + skill cooldowns). 1.0 = unchanged.");

        ImGui.Separator();
        ImGui.Text("Enemy telegraph");
        SliderFloatSaved("Windup", () => config.ActionWindupSeconds, v => config.ActionWindupSeconds = v, 0.15f, 1.5f,
            "Time from the telegraph appearing to the strike landing — your reaction time, and the approach-circle duration.");
        var windupSwing = config.ActionEnemyWindupSwing;
        if (ImGui.Checkbox("Wind-up swing", ref windupSwing))
        {
            config.ActionEnemyWindupSwing = windupSwing;
            config.Save();
        }
        HelpMarker("Enemy swings when the telegraph starts (body language during the windup). NOTE: the impact sound/hit-reaction is engine-tied to a swing, so the strike still swings — this means a second swing. Off = idle windup, one clean swing at the strike.");
        var showTele = config.ShowTelegraphs;
        if (ImGui.Checkbox("Show telegraph circles", ref showTele))
        {
            config.ShowTelegraphs = showTele;
            config.Save();
        }
        SliderFloatSaved("Telegraph opacity", () => config.TelegraphAlpha, v => config.TelegraphAlpha = v, 0.05f, 1.0f, "Ground danger-zone opacity.");

        ImGui.Separator();
        ImGui.Text("Parry circle (osu-style)");
        var osu = config.OsuCircleEnabled;
        if (ImGui.Checkbox("Show parry circle", ref osu))
        {
            config.OsuCircleEnabled = osu;
            config.Save();
        }
        HelpMarker("Approach circle on the player that shrinks onto a target ring; aligned = the guard window.");
        SliderFloatSaved("Anchor height", () => config.OsuAnchorHeight, v => config.OsuAnchorHeight = v, 0f, 2.2f, "Height on the player to draw the circle (yalms above feet; chest≈1.1, head≈1.6).");
        SliderFloatSaved("Inner ring size", () => config.OsuInnerRadius, v => config.OsuInnerRadius = v, 0.08f, 0.5f, "Target ring radius in world units (~1.5× head ≈ 0.18).");
        SliderFloatSaved("Outer start scale", () => config.OsuOuterStartScale, v => config.OsuOuterStartScale = v, 1.5f, 6.0f, "How wide the approach ring starts, relative to the inner ring.");
    }

    private void SliderFloatSaved(string label, Func<float> get, System.Action<float> set, float min, float max, string help)
    {
        var v = get();
        if (ImGui.SliderFloat(label, ref v, min, max, "%.2f"))
        {
            set(v);
            config.Save();
        }
        HelpMarker(help);
    }

    private void DrawVfxPicker(string label, Func<string> get, System.Action<string> set, ref string filter)
    {
        EnsureActionVfxCache();
        var list = actionVfxCache ?? new List<(string Label, string Path)> { ("None", "") };
        var current = get();
        var preview = FindVfxLabel(current);

        if (!ImGui.BeginCombo(label, preview))
            return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText($"Filter##{label}", ref filter, 128);

        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!MatchesFilter(item, filter))
                continue;

            var selected = item.Path == current;
            if (ImGui.Selectable($"{item.Label}##{label}{i}", selected))
            {
                set(item.Path);
                config.Save();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void EnsureActionVfxCache()
    {
        if (actionVfxCache != null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Label, string Path)> { ("None", "") };

        void Add(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!seen.Add(path))
                return;

            list.Add(($"{source}: {path}", path));
        }

        Add("vfx/common/eff/dk05th_stdn0t.avfx", "Common");
        Add("vfx/common/eff/cmhit_fire1t.avfx", "Common");

        try
        {
            var sheet = dataManager.GetExcelSheet<LuminaAction>();
            if (sheet != null)
            {
                foreach (var action in sheet)
                {
                    var actionName = action.Name.ToString();
                    if (string.IsNullOrWhiteSpace(actionName))
                        actionName = $"Action {action.RowId}";

                    var castLoc = action.VFX.ValueNullable?.VFX.ValueNullable?.Location.ExtractText();
                    if (!string.IsNullOrWhiteSpace(castLoc))
                        Add($"vfx/common/eff/{castLoc}.avfx", actionName);

                    var startLoc = action.AnimationStart.ValueNullable?.VFX.ValueNullable?.Location.ExtractText();
                    if (!string.IsNullOrWhiteSpace(startLoc))
                        Add($"vfx/common/eff/{startLoc}.avfx", actionName);

                    var endKey = action.AnimationEnd.ValueNullable?.Key.ExtractText();
                    if (!string.IsNullOrWhiteSpace(endKey) && !endKey.Contains("[SKL_ID]"))
                        AddVfxFromTmb($"chara/action/{endKey}.tmb", actionName, Add);

                    var hitKey = action.ActionTimelineHit.ValueNullable?.Key.ExtractText();
                    if (!string.IsNullOrWhiteSpace(hitKey) && !hitKey.Contains("[SKL_ID]") && !hitKey.Contains("normal_hit"))
                        AddVfxFromTmb($"chara/action/{hitKey}.tmb", actionName, Add);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to build action VFX picker cache.");
        }

        list.Sort(1, Math.Max(0, list.Count - 1), Comparer<(string Label, string Path)>.Create(
            (a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase)));
        actionVfxCache = list;
    }

    private void AddVfxFromTmb(string tmbPath, string source, System.Action<string, string> add)
    {
        try
        {
            if (!dataManager.FileExists(tmbPath))
                return;

            var file = dataManager.GetFile(tmbPath);
            if (file?.Data == null)
                return;

            var text = Encoding.UTF8.GetString(file.Data);
            foreach (Match match in AvfxPathRegex.Matches(text))
                add(match.Value.Trim('\0'), source);
        }
        catch
        {
        }
    }

    private string FindVfxLabel(string path)
    {
        EnsureActionVfxCache();
        if (actionVfxCache != null)
        {
            foreach (var item in actionVfxCache)
                if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                    return item.Label;
        }

        return string.IsNullOrWhiteSpace(path) ? "None" : path;
    }

    private static bool MatchesFilter((string Label, string Path) item, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOf<T>(IReadOnlyList<T> values, T current)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < values.Count; i++)
        {
            if (comparer.Equals(values[i], current))
                return i;
        }

        return Math.Max(0, values.Count - 1);
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

            ImGui.Separator();
            ImGui.TextDisabled("Enemy action density");

            var skillRate = config.NpcSkillRateLimit;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Enemy skills/sec##npcdensity", ref skillRate, 0f, 20f, skillRate <= 0f ? "uncapped" : "%.1f"))
            {
                config.NpcSkillRateLimit = skillRate;
                config.Save();
            }
            HelpMarker("Cap on how many skill casts ALL hostile NPCs may start per second between them. Each enemy " +
                       "paces itself off its own cooldowns, but every cast costs VFX on the caster and each target, " +
                       "an animation restart and flytext — all on one screen — so the visible density climbs with " +
                       "the enemy count and nothing was holding the total down.\n\n" +
                       "An enemy that can't get a slot doesn't idle: its cooldown is unspent so it goes the moment " +
                       "one frees, and it auto-attacks in the meantime. Player and companions are never throttled. " +
                       "0 = uncapped.");

            var vfxCull = config.NpcSkillVfxMaxDistance;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Enemy VFX range##npcdensity", ref vfxCull, 0f, 100f, vfxCull <= 0f ? "unlimited" : "%.0f y"))
            {
                config.NpcSkillVfxMaxDistance = vfxCull;
                config.Save();
            }
            HelpMarker("Skip action VFX for hostile casters further than this from the camera. Their damage, flytext " +
                       "and animation still play — only the effect is dropped, at a distance where it is a few pixels " +
                       "of glare anyway. Measured from the camera rather than from you, so a death cam sitting far " +
                       "from your body still shows what is actually on screen. 0 = never skip.");

            if (!config.EnableTargetVfx)
                ImGui.BeginDisabled();

            var enableVfx = config.EnableHitVfx;
            if (ImGui.Checkbox("Use Fallback Hit VFX", ref enableVfx))
            {
                config.EnableHitVfx = enableVfx;
                config.Save();
            }
            HelpMarker("When target-side action VFX are unavailable, spawn this configured hit effect on the damaged target.");

            DrawVfxPicker("Fallback Hit VFX", () => config.HitVfxPath, v => config.HitVfxPath = v, ref hitVfxFilter);
            HelpMarker("Game VFX path to spawn on hit. This list is built from action VFX data and timeline TMB files.");

            if (!config.EnableTargetVfx)
                ImGui.EndDisabled();

            if (!animationController.HitVfxAvailable)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "ActorVfxCreate not found — hit VFX unavailable.");
            }
        }

        if (ImGui.CollapsingHeader("Hit feedback"))
        {
            HelpMarker("Impact feel for player attacks (Action Mode): hitstop + camera punch + spark, delayed to the weapon's contact frame.");
            var hitFeedback = config.EnableHitFeedback;
            if (ImGui.Checkbox("Enable hit feedback", ref hitFeedback))
            {
                config.EnableHitFeedback = hitFeedback;
                config.Save();
            }
            using (ImRaii.Disabled(!config.EnableHitFeedback))
            {
                SliderFloatSaved("Feedback delay (s)", () => config.HitFeedbackDelay, v => config.HitFeedbackDelay = v, 0f, 1.0f,
                    "Wait this long after the attack input before the feedback fires, so hitstop/camera/spark land when the weapon visually CONNECTS instead of at swing start. ~0.45 matches a normal swing's contact frame. 0 = instant.");
                SliderFloatSaved("Hitstop (ms)", () => config.HitstopMs, v => config.HitstopMs = v, 0f, 150f,
                    "Freeze the struck target's animation this long on impact -- the biggest single source of weight/impact feel. Keep short (40-90 ms) or it reads as lag. 0 = off. Only the target is frozen, never the player.");
                SliderFloatSaved("Camera punch", () => config.HitCameraShake, v => config.HitCameraShake = v, 0f, 0.3f,
                    "Brief screen-shake magnitude on impact (camera offset in yalms). Small = weighty, large = nauseating. Layered over the final camera pose. 0 = off.");
                SliderFloatSaved("Punch duration", () => config.HitCameraShakeDuration, v => config.HitCameraShakeDuration = v, 0.05f, 0.5f,
                    "Seconds the camera punch decays over.");
                var spark = config.EnableHitSparkVfx;
                if (ImGui.Checkbox("Hit spark VFX", ref spark))
                {
                    config.EnableHitSparkVfx = spark;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Spawn a spark on the struck target (uses the Hit VFX path). Spawning actor VFX on modified/spawned enemies can be fragile -- disable if you see issues.");
            }
        }

        DrawVictoryCelebrationSection();

        DrawNpcScaleSection();

        DrawEnemyVoiceSection();

        DrawPcDismemberSection();

        if (ImGui.CollapsingHeader("Enemy Dismemberment"))
        {
            if (ImGui.Button("Reset Defaults##enemy_dismemberment_defaults"))
            {
                var defaults = new Configuration();
                config.EnableEnemyDismemberment = defaults.EnableEnemyDismemberment;
                config.EnemyHumanoidDismembermentCount = defaults.EnemyHumanoidDismembermentCount;
                config.EnemyMonsterDismembermentBonePercent = defaults.EnemyMonsterDismembermentBonePercent;
                config.Save();
            }
            HelpMarker("Restore enemy settings to defaults: enabled, human limb count 0, monster bone percent 100%.");

            var enemyDismember = config.EnableEnemyDismemberment;
            if (ImGui.Checkbox("Enable for enemies##enemy_dismemberment_enable", ref enemyDismember))
            {
                config.EnableEnemyDismemberment = enemyDismember;
                config.Save();
            }
            HelpMarker("Randomly separates visible pieces when enemy death ragdoll starts. Human enemies use a fixed count; non-human enemies use a percentage of eligible skeleton branches.");

            if (config.EnableEnemyDismemberment)
            {
                var humanCount = Math.Clamp(config.EnemyHumanoidDismembermentCount, 0, DismemberParts.Length);
                if (ImGui.SliderInt("Human limb count##enemy_dismemberment_human_count", ref humanCount, 0, DismemberParts.Length))
                {
                    config.EnemyHumanoidDismembermentCount = humanCount;
                    config.Save();
                }

                var monsterPercent = Math.Clamp(config.EnemyMonsterDismembermentBonePercent, 0f, 100f);
                if (ImGui.DragFloat("Monster bone percent##enemy_dismemberment_monster_percent", ref monsterPercent, 0.01f, 0f, 100f, "%.2f%%"))
                {
                    config.EnemyMonsterDismembermentBonePercent = monsterPercent;
                    config.Save();
                }
            }
        }
        DrawPlayerWeaponVisibilitySection();
    }

    private void DrawPlayerWeaponVisibilitySection()
    {
        if (!ImGui.CollapsingHeader("Hide weapons")) return;
        var left = config.HidePlayerLeftWeapon;
        var right = config.HidePlayerRightWeapon;
        if (ImGui.Checkbox("Left hand (off hand)##hide_player_weapon", ref left))
        {
            config.HidePlayerLeftWeapon = left;
            config.Save();
        }
        if (ImGui.Checkbox("Right hand (main hand)##hide_player_weapon", ref right))
        {
            config.HidePlayerRightWeapon = right;
            config.Save();
        }
        HelpMarker("Player only. Hides the selected weapon slot, including dropped weapons, without changing equipment or attacks. Off hand includes shields and job accessories; two-handed weapons belong to the main-hand slot.");
    }

    private static readonly (string Label, string Bone)[] DismemberParts =
    {
        ("Head", "j_kao"),
        ("L Upper arm", "j_ude_a_l"), ("R Upper arm", "j_ude_a_r"),
        ("L Forearm", "j_ude_b_l"),   ("R Forearm", "j_ude_b_r"),
        ("L Thigh", "j_asi_a_l"),     ("R Thigh", "j_asi_a_r"),
        ("L Shin", "j_asi_b_l"),      ("R Shin", "j_asi_b_r"),
    };

    private void DrawRagdollFollowEntrySection()
    {
        if (!ImGui.CollapsingHeader("Ragdoll Follow"))
            return;

        var follow = config.RagdollFollowPosition;
        if (ImGui.Checkbox("Follow flung corpses##ragdollfollow", ref follow))
        {
            config.RagdollFollowPosition = follow;
            config.Save();
        }
        HelpMarker("Keeps a corpse's actual game-object position moving with its ragdoll body while it falls or slides, " +
                   "so a corpse thrown far away doesn't get culled or unloaded. On by default.");
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
                // Was hardcoded to 8, which had been the Settings tab — the button never went
                // where it said it did.
                selectedTab = TabIndex("Diagnose");
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

    private unsafe void RefreshSkeletonBones()
    {
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
        var names = new List<string>();
        for (int i = 0; i < havokBones.Length; i++)
        {
            var name = havokBones[i].Name.String;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        skeletonBoneNames = names.ToArray();
    }

    private void DrawDiagnoseSection()
    {
        DrawSafetyHookStatus();
        ImGui.Spacing();

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

    // Health of our OWN safety hooks — the barriers that keep simulated combat from ever
    // reaching the server. Unlike the conflict table below (other plugins hooking us),
    // this shows whether our interceptors are actually installed right now. A red row
    // means simulated actions/targets could leak to the server.
    private void DrawSafetyHookStatus()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Client-Side Safety");
        ImGui.Separator();
        ImGui.Spacing();

        var actionOk = useActionHook.IsHealthy;
        var targetGuardOk = playerTargetController.IsHardTargetGuardHealthy;
        var allOk = actionOk && targetGuardOk;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                allOk ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                (allOk ? FontAwesomeIcon.ShieldAlt : FontAwesomeIcon.ExclamationTriangle).ToIconString());
        }
        ImGui.SameLine();
        if (allOk)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "All client-side safety barriers active");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "A safety barrier is DOWN — simulated combat may reach the server");

        ImGui.Spacing();

        if (ImGui.BeginTable("##SafetyHookTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Barrier", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Protects against", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawSafetyHookRow("UseAction interceptor", actionOk,
                "Simulated actions being sent to the server as real action packets.");
            DrawSafetyHookRow("Hard-target guard", targetGuardOk,
                "Clicking a simulated actor syncing a fake target id to the server.");

            ImGui.EndTable();
        }

        if (!allOk)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "If a barrier is down, restart the game or reload Combat Simulator to reinstall its hooks. " +
                "The simulation also auto-stops if the UseAction interceptor fails while combat is active.");
        }
    }

    private static void DrawSafetyHookRow(string name, bool healthy, string protects)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.Text(name);

        ImGui.TableNextColumn();
        if (healthy)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Active");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "DOWN");

        ImGui.TableNextColumn();
        ImGui.TextWrapped(protects);
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

            var showDynToolbar = config.ShowDynamicCamToolbar;
            if (ImGui.Checkbox("Show Dynamic Cam Toolbar", ref showDynToolbar))
            {
                config.ShowDynamicCamToolbar = showDynToolbar;
                config.Save();
            }
            HelpMarker("Show a floating toolbar to tune dynamic camera framing while playing.");

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

                var showDeathNameAlias = config.ShowDeathNameAlias;
                if (ImGui.Checkbox("Show Death Name Alias", ref showDeathNameAlias))
                {
                    config.ShowDeathNameAlias = showDeathNameAlias;
                    config.Save();
                }
                HelpMarker("Replace the player's displayed name on the simulated HP bars while defeated. The normal name returns on revive.");
                if (showDeathNameAlias)
                {
                    var deathNameAlias = config.DeathNameAlias;
                    if (ImGui.InputText("Death Name Alias", ref deathNameAlias, 64))
                    {
                        config.DeathNameAlias = deathNameAlias;
                        config.Save();
                    }
                    HelpMarker("Virtual name shown only while defeated. Leave empty to keep the normal display name.");
                }

                ImGui.TreePop();
            }
        }
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

    private void DrawGuidedCollapseSection()
    {
        if (!ImGui.CollapsingHeader("Guided Collapse##guidedcollapse"))
            return;

        var guided = config.GuidedCollapse;
        var enabled = guided.Enabled;
        if (ImGui.Checkbox("Knee buckle on death##guidedcollapse", ref enabled))
        {
            guided.Enabled = enabled;
            config.Save();
        }
        HelpMarker("On death the knees give way first and the body folds forward over them, instead of dropping straight into a limp ragdoll. Requires Enable Ragdoll.");

        if (guided.Enabled && !config.EnableRagdoll)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "Enable Ragdoll above; the knee buckle only arms during ragdoll activation.");

        if (guided.Enabled && config.RagdollActivationDelay > 0.001f)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "Activation Delay is not zero; the buckle starts from the delayed death-animation pose.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Set 0##guidedcollapseDelay"))
            {
                config.RagdollActivationDelay = 0f;
                config.Save();
            }
        }
    }

    private void DrawRagdollSection()
    {
        if (ImGui.CollapsingHeader("Ragdoll Trigger"))
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

                ImGui.Unindent();
            }
        }

        // The rest of the ragdoll settings live in sibling headers — one giant "Ragdoll"
        // header had accreted five unrelated groups and become unscannable.
        if (config.EnableRagdoll && ImGui.CollapsingHeader("Ground Detection"))
        {
            {
                ImGui.Indent();

                var extendTerrain = config.ExtendTerrainDetection;
                if (ImGui.Checkbox("Extend Terrain Detection##ragdoll", ref extendTerrain))
                {
                    config.ExtendTerrainDetection = extendTerrain;
                    config.Save();
                }
                HelpMarker("Also build ground collision under nearby enemies, not just the death spot. " +
                           "Lets a victory-sequence grab drop the body onto an enemy without falling through " +
                           "the floor. Costs extra raycasts at activation — may cause a brief hitch. Default off.");

                if (config.ExtendTerrainDetection)
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), "Warning: may cause severe stuttering.");

                ImGui.Unindent();
            }
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Weapon Drop"))
        {
            ImGui.Indent();
            var weaponCollision = config.WeaponDropMeshCollision ? 2 : config.WeaponBodyMeshContacts ? 1 : 0;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Weapon collision##weapondrop", ref weaponCollision, WeaponCollisionLabels, WeaponCollisionLabels.Length))
            {
                config.WeaponDropMeshCollision = weaponCollision == 2;
                config.WeaponBodyMeshContacts = weaponCollision >= 1;
                config.Save();
            }
            HelpMarker("On death the weapon detaches from the hand and falls with its own physics.\n" +
                       "Simple: the weapon is a box and lands on approximate body capsules.\n" +
                       "Body mesh (default): it lands on a coarse copy of the rendered body surface. The local player's surface is cached in advance; other characters are captured when their weapon drops (may hitch briefly).\n" +
                       "Full mesh: the weapon's own shape is also fitted from its mesh (up to 24 parts). May cause stuttering at death.\n" +
                       "Applies to new weapon drops.");
            if (weaponCollision >= 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh Mesh##weapondropMeshRefresh")) config.RefreshRagdollMeshCache();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Recapture cached body surfaces. Use after changing Glamourer, race, model, outfit or scale.");
            }
            if (weaponCollision == 2)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), "Warning: may cause severe stuttering.");
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Hair Physics"))
        {
            ImGui.Indent();

            var hairPhysics = config.RagdollHairPhysics;
            if (ImGui.Checkbox("Enable Hair Physics##ragdoll", ref hairPhysics))
            {
                config.RagdollHairPhysics = hairPhysics;
                config.Save();
            }
            HelpMarker("Simulate flexible hair guides with persistent hairstyle curvature, " +
                       "with head-driven inertia/whip and ground contact. Uses loaded mesh weights to fit long hair guides. " +
                       "Runs immediately while enabled, including normal animation.");

            if (config.RagdollHairPhysics)
            {
                ImGui.Indent();
                var hairBodyMesh = config.RagdollHairBodyMesh;
                if (ImGui.Checkbox("Collide with body mesh##hairrig", ref hairBodyMesh))
                {
                    config.RagdollHairBodyMesh = hairBodyMesh;
                    config.Save();
                }
                HelpMarker("Hair rests on a smoothed copy of the rendered body (shoulders, back, chest) instead of approximate body capsules. Off by default (capsules).");
                ImGui.Unindent();
            }

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Soft Tissue"))
        {
            ImGui.Indent();

            var softTissue = config.RagdollSoftTissueModBones ? 1 + Math.Clamp(config.RagdollSoftTissueScope, 0, 2) : 0;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Soft tissue##softtissue", ref softTissue, SoftTissueLabels, SoftTissueLabels.Length))
            {
                config.RagdollSoftTissueModBones = softTissue > 0;
                if (softTissue > 0) config.RagdollSoftTissueScope = softTissue - 1;
                config.Save();
            }
            HelpMarker("Extra bones simulated as jiggle bodies during ragdoll.\n" +
                       "Mod bones (default): extra bones from body-mod skeletons (Rue/YAS/IVCS lineage), excluding fingers and toes. Does nothing on vanilla skeletons.\n" +
                       "All bones: every skeleton bone that is not already a ragdoll body, vanilla bones included (experimental).\n" +
                       "All bones except digits: the same, minus fingers and toes.\n" +
                       "Discovered bones are listed in the plugin log on ragdoll activation. Takes effect on next ragdoll activation.");

            var softCollision = config.RagdollSoftTissueCollision;
            if (ImGui.Checkbox("Soft tissue collision (experimental)##softtissue", ref softCollision))
            {
                config.RagdollSoftTissueCollision = softCollision;
                config.Save();
            }
            HelpMarker("Soft bones also collide with the GROUND, so flesh pressed against the floor " +
                       "reacts instead of sinking in. Body contact stays off regardless. Costs extra contact pairs. " +
                       "Takes effect on next ragdoll activation.");

            var squash = config.RagdollSquashStretch ? SquashLevelIndex(config.RagdollSquashIntensity) : 0;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Squash & stretch (experimental)##softtissue", ref squash, SquashLabels, SquashLabels.Length))
            {
                config.RagdollSquashStretch = squash > 0;
                if (squash > 0) config.RagdollSquashIntensity = SquashIntensities[squash];
                config.Save();
            }
            HelpMarker("Soft bones visibly compress on hard impacts and flatten against the ground once " +
                       "the corpse settles, with a springy rebound. Writes per-bone scale into the skeleton pose.");

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Combat NPC Ragdoll"))
        {
            ImGui.Indent();

            var npcRagdoll = config.EnableNpcDeathRagdoll;
            if (ImGui.Checkbox("Ragdoll enemies on death##npcragdoll", ref npcRagdoll))
            {
                config.EnableNpcDeathRagdoll = npcRagdoll;
                config.Save();
            }
            HelpMarker("Apply ragdoll physics to enemy NPCs when they die in combat, including party mode enemies.");

            var companionRagdoll = config.PartyCompanionDeathRagdoll;
            if (ImGui.Checkbox("Ragdoll companions on death##companionragdoll", ref companionRagdoll))
            {
                config.PartyCompanionDeathRagdoll = companionRagdoll;
                config.Save();
            }
            HelpMarker("Apply ragdoll physics to party companion clones when they die.");

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Mesh Collision [Experimental]##ragdollMeshCollisionSection"))
        {
            ImGui.Indent();
            var meshVolumes = config.RagdollMeshVolumes;
            if (ImGui.Checkbox("Rendered Mesh Body Collision##ragdollMeshVolumes", ref meshVolumes))
            {
                config.RagdollMeshVolumes = meshVolumes;
                config.RefreshRagdollMeshCache();
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Captures visible body/equipment surfaces once into a session cache. Body parts keep fixed collision shapes during ragdoll; no live skin or mesh refit. Unsupported regions use fallback volumes. Off by default.\nRefresh after Glamourer, race, model, outfit or scale changes. Fixed joint surfaces may differ from animated skin.\n\n" +
                                 ragdollController.MeshGeometryStatus);
            if (meshVolumes)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh Mesh##ragdollMeshRefresh")) config.RefreshRagdollMeshCache();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Replace the cached mesh with the current model and pose. Use after changing Glamourer, race, model, outfit or scale. An active ragdoll will restart with the new cache.");

                var performance = MeshCollisionPerformanceIndex();
                ImGui.SetNextItemWidth(200f);
                if (ImGui.Combo("Mesh collision performance##ragdollMeshPerformance", ref performance,
                        MeshCollisionPerformanceLabels, MeshCollisionPerformanceLabels.Length))
                {
                    config.RagdollMeshCollisionLod = performance == 2;
                    if (performance == 2) config.RagdollMeshLodToleranceMm = 5.5f;
                    config.RagdollMeshContactQuality = performance == 0 ? 100 : 50;
                    config.Save();
                }
                HelpMarker("Quality: full collision surfaces and the most thorough body-to-body contact search.\n" +
                           "Balanced (default): full surfaces, contact search reduced by half.\n" +
                           "Performance: also simplifies the collision surfaces (5.5 mm target error), built in the background. " +
                           "Can visibly change ground contact and body contours.\n" +
                           "Surface simplification takes effect on the next ragdoll activation; contact search applies immediately.");
            }
            ImGui.Unindent();
        }
    }

    private static readonly string[] WeaponCollisionLabels = { "Simple", "Body mesh", "Full mesh" };
    private static readonly string[] SoftTissueLabels = { "Off", "Mod bones", "All bones", "All bones except digits" };
    private static readonly string[] SquashLabels = { "Off", "Subtle", "Normal", "Strong" };
    private static readonly float[] SquashIntensities = { 0f, 0.25f, 0.5f, 1f };
    private static readonly string[] MeshCollisionPerformanceLabels = { "Quality", "Balanced", "Performance" };

    private static int SquashLevelIndex(float intensity)
    {
        var best = 1;
        for (var i = 2; i < SquashIntensities.Length; i++)
            if (MathF.Abs(SquashIntensities[i] - intensity) < MathF.Abs(SquashIntensities[best] - intensity))
                best = i;
        return best;
    }

    private int MeshCollisionPerformanceIndex()
        => config.RagdollMeshCollisionLod ? 2 : config.RagdollMeshContactQuality >= 100 ? 0 : 1;

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
            HelpMarker("Active combat targets have per-bone collision volumes for ragdoll interaction. Client-controlled enemies and companions can step onto low corpse surfaces instead of pushing through them.");

            if (config.RagdollNpcCollision)
            {
                ImGui.Indent();
                var collisionMode = config.RagdollNpcCollisionMode == RagdollNpcCollisionMode.Mesh ? 1 : 0;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.Combo("Collision shape##npccolmode", ref collisionMode, NpcCollisionModeLabels, NpcCollisionModeLabels.Length))
                {
                    config.RagdollNpcCollisionMode = collisionMode == 1 ? RagdollNpcCollisionMode.Mesh : RagdollNpcCollisionMode.BoneCapsule;
                    config.Save();
                }
                HelpMarker("Fast (default): the per-bone capsule proxies; nothing is built at activation.\nAccurate: snapshots the rendered model mesh with the current pose and tracks the root transform. Built at activation, may hitch. Takes effect on next ragdoll activation.");
                ImGui.Unindent();
            }
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
        using var toolbarStyle = PushCompactToolbarStyle();
        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Combat Shortcuts", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }
        RestoreToolbarFontScale();

        var isActive = combatEngine.IsActive;
        var btnSize = new Vector2(95, 0);

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

    public void DrawActiveCamToolbar()
    {
        using var toolbarStyle = PushCompactToolbarStyle();
        if (!ImGui.Begin("Active Cam", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }
        RestoreToolbarFontScale();

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




    public void Dispose()
    {
    }
}
