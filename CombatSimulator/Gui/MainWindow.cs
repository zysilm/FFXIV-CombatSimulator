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
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow : IDisposable
{
    /// <summary>Takes the battle result banner off the screen (its switch was turned off).</summary>
    public Action? HideResultBanner { get; set; }

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
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly CombatRecipeBook recipeBook;
    private readonly IPluginLog log;

    // Start confirmation popup, shown when a client-side safety barrier is down: a plain start may go ahead on
    // confirmation (barrierPendingStart); a start that spawns simulated actors is refused (barrierRefusedSpawn).
    private bool showBarrierConfirmPopup;
    private Action? barrierPendingStart;
    private bool barrierRefusedSpawn;

    private static readonly Vector4 ConflictYellow = new(1f, 0.8f, 0f, 1f);
    private static readonly Vector4 BarrierRed = new(1f, 0.4f, 0.4f, 1f);

    /// <summary>The client-side safety barriers (set by the presentation module right after construction).</summary>
    public SafetyBarriers Barriers { get; set; } = null!;

    /// <summary>True when every interceptor that keeps simulated combat off the server is installed.</summary>
    private bool SafetyBarriersHealthy => Barriers.AllHealthy;

    /// <summary>
    /// The one barrier check before anything that starts the simulation or spawns its actors. Barriers up: true,
    /// go ahead. A barrier down: false, and the "Safety Barrier Down" popup opens: a plain start can still be
    /// confirmed there (<paramref name="start"/> runs on "Start Anyway"); one that spawns simulated actors is
    /// refused outright.
    /// </summary>
    private bool PassSafetyBarrier(Action? start, bool spawnsActors)
    {
        if (SafetyBarriersHealthy)
            return true;

        barrierRefusedSpawn = spawnsActors;
        // "Start Anyway" only when the hard-target guard is the one down: with an action hook down the
        // simulation's per-frame safety check stops it again on the next frame, so starting is pointless.
        var onlyTargetGuardDown = Barriers.UseActionHealthy && Barriers.UseActionLocationHealthy;
        barrierPendingStart = spawnsActors || !onlyTargetGuardDown ? null : start;
        showBarrierConfirmPopup = true;
        if (spawnsActors)
            chatGui.PrintError($"[CombatSim] Not started: a client-side safety barrier is down ({Barriers.FirstDown}), " +
                               "so no simulated actors can be spawned. See the Diagnose tab.");
        return false;
    }

    // Glamourer design list cache for combo box
    private List<KeyValuePair<Guid, string>> glamourerDesigns = new();

    // Local player's skeleton bone names (Virtual Enemies look-at bone fallback list).

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

    // War Generator state
    private int nextSquadId = 1;
    private string warProfileName = string.Empty;
    private string warProfileNote = string.Empty;
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
        this.spectatorController = spectatorController;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.log = log;
        this.recipeBook = new CombatRecipeBook(log, BattleProfileDirectory());
        InitDevExperimental(devExperimental);
    }

    private int selectedTab = 0;

    private static readonly string[] TabNames = new[]
    {
        "Combat",
        "Effects",
        "Camera",
        "Ragdoll",
        "War Generator",
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

        if (ImGui.Button("War Generator"))
        {
            config.ShowProfessionalWindow = true;
            selectedTab = TabIndex("War Generator");
            config.Save();
        }
        HelpMarker("Open the War Generator: build a battle, choose its rules, and save it as a profile.");

        ImGui.Separator();
        vnavmeshIpc.RefreshStatus();
        if (vnavmeshIpc.IsAvailable)
            ImGui.TextColored(new Vector4(0f, 0.85f, 0f, 1f), "vnavmesh: active");
        else
            ImGui.TextDisabled("vnavmesh: not installed");
        ImGui.SameLine();
        HelpMarker("vnavmesh is recommended for better pathfinding. The plugin works without it, but enemy movement will be limited.");

        // Barrier confirmation popup (the fast-combat panel above can raise it)
        DrawBarrierConfirmPopup();

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
        var diagnoseIndex = TabIndex("Diagnose");
        for (int i = 0; i < TabNames.Length; i++)
        {
            if (i == diagnoseIndex)
                DrawDiagnoseSidebarEntry(i);
            else if (ImGui.Selectable(TabNames[i], selectedTab == i))
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
                DrawActionModeSection();
                DrawEnemyTacticsSection();
                DrawMapEnemiesSection();
                if (ImGui.CollapsingHeader("Party"))
                {
                    ImGui.Indent();
                    DrawPartyTab();
                    ImGui.Unindent();
                }
                break;
            case 1: // Effects
                DrawHitVfxSection();
                DrawArrowImpactSection();
                DrawPrivateEffectsSection();
                DrawExperimentalEffects();
                DrawEnemyControlEntrySection();
                break;
            case 2: // Camera
                DrawActiveCamSection();
                DrawDynamicCamSection();
                DrawDuelCamSection();
                DrawCutsceneShotsSection();
                break;
            case 3: // Ragdoll
                DrawRagdollSection();
                DrawGuidedCollapseSection();
                DrawNpcCollisionSection();
                DrawArmorDetachmentEntrySection();
                DrawArmorDetachmentOthersSection();
                ImGui.Separator();
                if (ImGui.Button("Reset All to Defaults##ragdollpage"))
                    config.ResetRagdollPageDefaults();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Restore every option on this page to its default.\n" +
                                     "Leaves Enable Ragdoll and Detach armor alone: resetting them would switch the feature off.");
                break;
            case 4: // War Generator
                DrawVirtualEnemiesTab();
                break;
            case 5: // Spectators (Experimental)
                DrawSpectatorsTab();
                break;
            case 6: // Settings
                DrawGuiSettingsSection();
                break;
            case 7: // Diagnose
                DrawDiagnoseSection();
                break;
            default:
                DrawDevTabContent(selectedTab);
                break;
        }
        ImGui.EndChild();

        // Barrier confirmation popup (must be at window scope, not inside a child)
        DrawBarrierConfirmPopup();

        ImGui.End();
    }

    /// <summary>
    /// The Diagnose sidebar entry: red with a warning icon when a safety barrier is down,
    /// yellow with the same icon when other plugins hook functions we call, plain otherwise.
    /// The sidebar is a list of Selectables chosen by index, and the entry keeps one fixed ID
    /// ("##sidebar_diagnose") whatever it looks like. A label renders in one font only, so in the
    /// warning states the Selectable has no visible label and the icon (icon font) and the name
    /// are drawn over it as plain text, which takes no clicks.
    /// </summary>
    private void DrawDiagnoseSidebarEntry(int index)
    {
        Vector4? color = !SafetyBarriersHealthy ? BarrierRed
            : hookSafetyChecker.HasConflicts ? ConflictYellow
            : null;

        if (color == null)
        {
            if (ImGui.Selectable($"{TabNames[index]}##sidebar_diagnose", selectedTab == index))
                selectedTab = index;
            return;
        }

        var startX = ImGui.GetCursorPosX();
        if (ImGui.Selectable("##sidebar_diagnose", selectedTab == index))
            selectedTab = index;
        ImGui.SameLine(startX);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color.Value, FontAwesomeIcon.ExclamationTriangle.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(color.Value, TabNames[index]);
    }

    /// <summary>Start from a button: straight away when the barriers are up, otherwise ask first.</summary>
    private void RequestStartSimulation()
    {
        if (PassSafetyBarrier(StartSimulationNow, spawnsActors: false))
            StartSimulationNow();
    }

    private void StartSimulationNow()
    {
        combatEngine.StartSimulation();
        chatGui.Print("[CombatSim] Combat simulation started.");
    }

    /// <summary>Reboot from a button: stop, then start again behind the same barrier check as Start.</summary>
    private void RequestRebootSimulation()
    {
        if (PassSafetyBarrier(RebootSimulationNow, spawnsActors: false))
            RebootSimulationNow();
    }

    private void RebootSimulationNow()
    {
        combatEngine.StopSimulation();
        combatEngine.StartSimulation();
        chatGui.Print("[CombatSim] Combat simulation rebooted.");
    }

    private void DrawStatusSection()
    {
        var simActive = combatEngine.IsActive;

        ImGui.Text("Status:");
        ImGui.SameLine();
        if (simActive)
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Active");
        else
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Inactive");

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
            ImGui.TextUnformatted("Battle Profile");
            HelpMarker("A battle profile sets up both sides in one click. Put your own together and save them in the War Generator tab of the settings window.");
        }

        DrawRecipeCombo("##CombatRecipe", compact ? 220f : -1f);
        if (!compact && ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose the battle profile used by Start and Reset.");

        var recipe = recipes.Count > 0 ? recipes[selectedRecipeIndex] : null;

        if (!compact)
        {
            ImGui.TextDisabled("Action combat");
            HelpMarker(BuildActionModeQuickHelp());
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
            HelpMarker("Hide every name and number on the HP bars, your own included.");
        }
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

    private const string EnemyStrengthHelp =
        "How much HP an enemy has: how long you need to kill it. Its hits are the same as yours: a skill does the same damage whoever uses it, and costs the same share of MP.\n" +
        "Auto: read from the enemy itself (ordinary enemies Normal, notorious ones Strong or Elite, bosses Boss).\n" +
        "Weak: about a third of your HP.  Normal: about your HP, an even duel.  Strong: twice.  Elite: three times.  Boss: eight times.";

    private const string EnemyLevelHelp =
        "Only the gap to your own level matters; your gear does not.\n" +
        "The gap counts as it does in the game: an enemy's HP and damage grow with what its level is worth against yours.\n" +
        "Ten levels above you is roughly twice to three times as strong; a level 100 enemy against a level 15 character has hundreds of times the HP and kills in one hit.\n" +
        "Leave it on \"My level\" for an even fight.";

    // One strength dropdown and one level slider; returns true when either changed.
    private static bool DrawEnemyStrengthAndLevel(string id, string strengthLabel, string levelLabel, ref int strength, ref int level,
        string strengthHelp = EnemyStrengthHelp, string levelHelp = EnemyLevelHelp)
    {
        var changed = false;
        strength = Math.Clamp(strength, 0, CombatBudgetModel.StrengthChoices.Length - 1);
        ImGui.SetNextItemWidth(200);
        changed |= ImGui.Combo($"{strengthLabel}##{id}", ref strength, CombatBudgetModel.StrengthChoices, CombatBudgetModel.StrengthChoices.Length);
        HelpMarker(strengthHelp);

        level = Math.Clamp(level, 0, 100);
        ImGui.SetNextItemWidth(200);
        changed |= ImGui.SliderInt($"{levelLabel}##{id}", ref level, 0, 100, level == 0 ? "My level" : "%d");
        HelpMarker(levelHelp);
        return changed;
    }

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
        DrawBarrierConfirmPopup();
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
        // Spawns simulated actors: refused while a safety barrier is down.
        if (!PassSafetyBarrier(null, spawnsActors: true))
            return;

        StopFastCombat(print: false);

        // Recipe-scoped NPC collision. Null leaves the user's own setting in force; the
        // StopFastCombat above has already cleared any previous recipe's override.
        config.RecipeNpcCollisionOverride = recipe.NpcCollision;

        // The profile's battle rules become the War Generator's (they show there, and stay).
        if (recipe.FallenRule is { } rule)
            config.WarFallenRule = (int)rule;
        if (recipe.Hostages is { } hostages)
            config.WarCaptives = hostages;
        if (recipe.CorpseSeizure is { } seizure)
            config.WarRetreat = seizure;
        if (recipe.FallenRule != null || recipe.Hostages != null || recipe.CorpseSeizure != null)
            config.Save();

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

        npcCatalog ??= new NpcCatalog(dataManager, log);
        foreach (var group in recipe.Allies)
            queuedCompanions += QueueRecipeGroup(group, ally: true);

        var queuedEnemies = 0;
        foreach (var group in recipe.Enemies)
            queuedEnemies += QueueRecipeGroup(group, ally: false);

        var mapEnemyText = mapEnemySettings != null ? $", up to {mapEnemySettings.MaxCount} map enemy/enemies" : "";
        chatGui.Print($"[CombatSim] Started battle profile '{recipe.Name}' ({queuedCompanions} companion(s), {queuedEnemies} enemy/enemies queued{mapEnemyText}).");
        log.Info($"Battle profile started: '{recipe.Name}', rule {(WarFallenRule)config.WarFallenRule}, hostages {config.WarCaptives}, corpse seizure {config.WarRetreat}; {queuedCompanions} allies, {queuedEnemies} enemies queued.");
    }

    /// <summary>Queue one group of a profile; returns how many were queued.</summary>
    private int QueueRecipeGroup(CombatRecipeEnemyGroup group, bool ally)
    {
        var entry = ResolveRecipeEnemy(group);
        if (entry == null)
        {
            chatGui.PrintError($"[CombatSim] Battle profile: '{group.Name}' not found ({group.Type}:{group.Id}).");
            return 0;
        }
        if (!entry.IsSpawnable)
        {
            chatGui.PrintError($"[CombatSim] Battle profile: '{entry.Name}' has no complete spawn source data.");
            return 0;
        }

        var placement = group.Placement ?? CurrentSpawnPlacement();
        // A profile made where the map could be used falls back to a ring where it cannot.
        if (placement == SpawnPlacement.Map && !npcSpawner.CanPlaceOnMap)
            placement = SpawnPlacement.Surround;
        // One group on the map is one squad at one post.
        var squad = !ally && placement == SpawnPlacement.Map ? nextSquadId++ : 0;
        var tier = group.Strength ?? CombatBudgetModel.TierForChoice(ally ? config.AllyStrength : config.EnemyStrength);
        var level = group.Level ?? (ally ? config.AllyLevel : config.EnemyLevel);

        var count = Math.Max(0, group.Count);
        for (var i = 0; i < count; i++)
            npcSpawner.QueueSpawn(BuildSpawnRequest(entry, ally, placement, !ally && group.Patrol, squad, tier, level));
        return count;
    }

    private static NpcSpawnRequest BuildSpawnRequest(
        NpcCatalogEntry entry, bool ally, SpawnPlacement placement, bool patrol, int squad, EnemyTier? tier, int level)
        => new()
        {
            DisplayName = entry.Name,
            BNpcBaseId = entry.Source == NpcCatalogSource.BNpcBase ? entry.Id : 0,
            BNpcNameId = entry.BNpcNameId,
            ENpcBaseId = entry.Source == NpcCatalogSource.ENpcBase ? entry.Id : 0,
            ModelCharaId = entry.Source == NpcCatalogSource.ModelChara ? entry.ModelCharaId : 0,
            Tier = tier,
            Level = level,
            Ally = ally,
            Placement = placement,
            Patrol = patrol,
            Squad = squad,
            // No Position: the spawner places it (one owner for where a creature goes).
        };

    /// <summary>Where opponents go, as chosen in the War Generator; the map only where it can be used.</summary>
    private SpawnPlacement CurrentSpawnPlacement()
    {
        var mode = config.SpawnPlacementMode ?? (config.SpawnInFront ? 1 : 0);
        var placement = (SpawnPlacement)Math.Clamp(mode, 0, 2);
        return placement == SpawnPlacement.Map && !npcSpawner.CanPlaceOnMap ? SpawnPlacement.Surround : placement;
    }

    private static string? BattleProfileDirectory()
    {
        try
        {
            var dir = Core.Services.PluginInterface?.GetPluginConfigDirectory();
            return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, "BattleProfiles");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The battle on the field as a profile: both sides as they stand, and the map settings.</summary>
    private CombatRecipe BuildProfileFromField(string name)
    {
        var recipe = new CombatRecipe
        {
            Name = name,
            Description = "Saved from the War Generator.",
            FallenRule = (CombatSimulator.Simulation.WarFallenRule)config.WarFallenRule,
            Hostages = config.WarCaptives,
            CorpseSeizure = config.WarRetreat,
        };

        static NpcCatalogSource SourceOf(NpcSpawnRequest r)
            => r.BNpcBaseId != 0 ? NpcCatalogSource.BNpcBase
                : r.ENpcBaseId != 0 ? NpcCatalogSource.ENpcBase
                : NpcCatalogSource.ModelChara;
        static uint IdOf(NpcSpawnRequest r)
            => r.BNpcBaseId != 0 ? r.BNpcBaseId : r.ENpcBaseId != 0 ? r.ENpcBaseId : r.ModelCharaId;

        // Like creatures in a row make one group; a squad on the map stays its own group.
        void Add(List<CombatRecipeEnemyGroup> groups, NpcSpawnRequest r, ref int lastSquad)
        {
            var last = groups.Count > 0 ? groups[^1] : null;
            if (last != null && last.Source == SourceOf(r) && last.Id == IdOf(r) && last.NameId == r.BNpcNameId &&
                last.Strength == r.Tier && last.Level == r.Level &&
                last.Placement == (r.Ally ? null : r.Placement) && last.Patrol == r.Patrol &&
                (r.Placement != SpawnPlacement.Map || lastSquad == r.Squad))
            {
                last.Count++;
            }
            else
            {
                groups.Add(new CombatRecipeEnemyGroup
                {
                    Type = SourceOf(r) == NpcCatalogSource.ENpcBase ? NpcCatalogType.ENpc : NpcCatalogType.BNpc,
                    Source = SourceOf(r),
                    Id = IdOf(r),
                    NameId = r.BNpcNameId,
                    Name = r.DisplayName,
                    Count = 1,
                    Strength = r.Tier,
                    Level = r.Level,
                    Placement = r.Ally ? null : r.Placement,
                    Patrol = !r.Ally && r.Patrol,
                });
            }
            lastSquad = r.Squad;
        }

        var selfClones = 0;
        var selfRandomized = 0;
        var otherClones = 0;
        var allySquad = 0;
        var localId = Core.Services.ObjectTable.LocalPlayer?.EntityId ?? 0;
        foreach (var companion in companionManager.Companions)
        {
            if (companion.IsCatalogNpc)
            {
                if (companion.CatalogRequest != null)
                    Add(recipe.Allies, companion.CatalogRequest, ref allySquad);
            }
            else if (companion.SourceEntityId == localId)
            {
                if (companion.RandomizedLook) selfRandomized++;
                else selfClones++;
            }
            else
                otherClones++;
        }
        if (selfClones > 0)
            recipe.Companions.Add(new CombatRecipeCompanionGroup { Type = CompanionRecipeType.Self, Count = selfClones });
        if (selfRandomized > 0)
            recipe.Companions.Add(new CombatRecipeCompanionGroup { Type = CompanionRecipeType.SelfRandomized, Count = selfRandomized });
        if (config.SensePartyMembers || otherClones > 0)
            recipe.Companions.Add(new CombatRecipeCompanionGroup
            {
                Type = CompanionRecipeType.VisiblePlayers,
                Count = Math.Max(otherClones, config.SensePartyMembers ? Math.Max(1, config.CombatCompanionMaxCount) : 0),
            });

        var enemySquad = 0;
        foreach (var npc in npcSpawner.SpawnedNpcs)
        {
            if (npcSpawner.RequestOf(npc) is { } request)
            {
                request.Patrol = npc.Patrols;
                Add(recipe.Enemies, request, ref enemySquad);
            }
        }

        if (config.EnableMapEnemySensing || config.EnableMapPlayerEnemySensing)
            recipe.MapEnemies.Add(new CombatRecipeMapEnemyGroup
            {
                Enabled = true,
                IncludeBattleNpcs = config.EnableMapEnemySensing,
                IncludePlayers = config.EnableMapPlayerEnemySensing,
                MaxCount = Math.Max(1, config.MapEnemyMaxCount),
                SenseRange = config.MapEnemySenseRange,
            });

        return recipe;
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
                Tier = CombatBudgetModel.TierForChoice(config.EnemyStrength),
                Level = config.EnemyLevel,
            };
        }

        return null;
    }

    private NpcCatalogEntry? ResolveRecipeEnemy(CombatRecipeEnemyGroup group)
    {
        npcCatalog ??= new NpcCatalog(dataManager, log);
        if (group.Source is { } source && group.Id != 0 &&
            npcCatalog.FindBySourceAndId(source, group.Id, group.NameId) is { } exact)
            return exact;

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
        foreach (var group in recipe.Allies)
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

        combatEngine.ResetState();
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

        ImGui.TextDisabled("Copy players in sight to your side: War Generator tab, Map players.");

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

        ImGui.TextDisabled("Who joins from the map, and enemy level and HP: War Generator tab.");

        ImGui.Separator();

        var maxSkills = config.EnemyMaxRealSkills;
        if (ImGui.SliderInt("Humanoid skills", ref maxSkills, 0, 8))
        {
            config.EnemyMaxRealSkills = Math.Clamp(maxSkills, 0, 8);
            config.Save();
        }
        HelpMarker("Humanoid enemies cast their real job's strongest N damage skills (by weapon → job). " +
                   "Monsters and NPC-only weapons auto-attack only. 0 = everyone auto-attacks.");
        DrawEnemySkillsSection();
    }


    private void DrawNpcAttackRangeSlider()
    {
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
    }

    // ---------------------------------------------------------------------------------------------
    // War Generator tab. Six blocks, each under its own header: the battle profile, spawning (the
    // creature picker beside its spawn settings), what is on the field, who joins from the map, the
    // battle rules, and how opponents behave. Inside a block, related controls sit on a card.
    // ---------------------------------------------------------------------------------------------

    private const float WarCardPad = 8f;
    private const float WarTwoColumnMinWidth = 640f;
    private static readonly Vector4 WarCardTitleColor = new(0.85f, 0.75f, 0.45f, 1f);

    /// <summary>
    /// A titled card: the body drawn inside a rounded border, padded from it. Widgets inside that
    /// fill the width should use <see cref="WarFill"/> so they stop short of the border.
    /// </summary>
    private static void WarCard(string title, Action body)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;

        ImGui.Dummy(new Vector2(0, WarCardPad * 0.5f));
        ImGui.Indent(WarCardPad);
        if (title.Length > 0)
        {
            ImGui.TextColored(WarCardTitleColor, title);
            ImGui.Spacing();
        }
        body();
        ImGui.Unindent(WarCardPad);
        ImGui.Dummy(new Vector2(0, WarCardPad * 0.5f));

        var end = new Vector2(right, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y * 0.5f);
        drawList.AddRect(start, end, ImGui.GetColorU32(ImGuiCol.Border), 5f);
        ImGui.Spacing();
    }

    /// <summary>Item width that fills a card up to its padding.</summary>
    private static float WarFill => -WarCardPad;

    /// <summary>A label on its own line above the control it names (keeps narrow columns tidy).</summary>
    private static void WarLabel(string text) => ImGui.TextUnformatted(text);

    private void DrawVirtualEnemiesTab()
    {
        // Lazy-load catalog on first open
        npcCatalog ??= new NpcCatalog(dataManager, log);

        if (ImGui.CollapsingHeader("Battle Profile##warProfileHeader", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawWarProfileSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Spawn##warSpawnHeader", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawWarSpawnSection();
            ImGui.Unindent();
        }

        var allyCount = companionManager.Companions.Count;
        var opponentCount = npcSpawner.SpawnedNpcs.Count;
        if (ImGui.CollapsingHeader($"On the Field ({allyCount} allies, {opponentCount} opponents)###warFieldHeader",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawWarFieldSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Joining from the Map##warMapHeader"))
        {
            ImGui.Indent();
            DrawWarMapSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Battle Rules##warRules"))
        {
            ImGui.Indent();
            DrawWarRules();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Opponent Behaviour##warBehaviourHeader"))
        {
            ImGui.Indent();
            DrawVirtualEnemyLookAtSection();
            ImGui.Unindent();
        }
    }

    /// <summary>The battle profile in use: choose, start and stop one, and save the battle on the field as one.</summary>
    private void DrawWarProfileSection()
    {
        var recipes = recipeBook.Recipes;
        SyncSelectedRecipeFromConfig(recipes);
        var recipe = recipes.Count > 0 ? recipes[selectedRecipeIndex] : null;

        WarCard("Run a profile", () =>
        {
            DrawRecipeCombo("##warProfile", MathF.Max(160f, ImGui.GetContentRegionAvail().X - WarCardPad - 30f));
            HelpMarker("A battle profile is a whole battle: both sides, where the opponents stand, and who joins from the map.\n" +
                       "Start runs the chosen one. Your own profiles are JSON files in the plugin's configuration folder " +
                       "(BattleProfiles) and can be edited or shared.");

            if (!string.IsNullOrEmpty(warProfileNote))
                ImGui.TextDisabled(warProfileNote);

            ImGui.Spacing();
            using (ImRaii.Disabled(recipe == null))
            {
                if (ImGui.Button("Start##warProfile", new Vector2(90, 0)) && recipe != null)
                    StartRecipe(recipe);
            }
            ImGui.SameLine();
            if (ImGui.Button("Stop##warProfile", new Vector2(90, 0)))
                StopFastCombat();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop the battle and remove everyone spawned, on both sides.");

            if (recipe is { IsUser: true })
            {
                ImGui.SameLine();
                if (ImGui.Button("Delete##warProfile", new Vector2(90, 0)))
                {
                    if (recipeBook.Delete(recipe))
                    {
                        warProfileNote = $"Deleted '{recipe.Name}'.";
                        selectedRecipeIndex = -1;
                        config.FastCombatRecipeName = string.Empty;
                        config.Save();
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Delete this profile of yours.");
            }
        });

        WarCard("Save the battle on the field", () =>
        {
            var onField = npcSpawner.SpawnedNpcs.Count + companionManager.Companions.Count > 0;
            ImGui.SetNextItemWidth(MathF.Max(120f, ImGui.GetContentRegionAvail().X - WarCardPad - 98f));
            ImGui.InputTextWithHint("##warProfileName", "Profile name", ref warProfileName, 48);
            ImGui.SameLine();
            using (ImRaii.Disabled(!recipeBook.CanSave || !onField || string.IsNullOrWhiteSpace(warProfileName)))
            {
                if (ImGui.Button("Save##warProfile", new Vector2(90, 0)))
                {
                    var saved = BuildProfileFromField(warProfileName.Trim());
                    if (recipeBook.Save(saved, out var error))
                    {
                        config.FastCombatRecipeName = saved.Name;
                        config.Save();
                        selectedRecipeIndex = -1;
                        warProfileNote = $"Saved '{saved.Name}'.";
                        warProfileName = string.Empty;
                    }
                    else
                        warProfileNote = error;
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Save the battle now on the field (both sides as they stand, and who joins from the map) " +
                                 "as a profile of your own.");
            if (!onField)
                ImGui.TextDisabled("Spawn someone first.");
        });
    }

    private void DrawWarSpawnSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f), "Spawning certain creatures may crash the game. Use with caution.");
        ImGui.TextDisabled("Spawning starts the battle at once.");
        ImGui.Spacing();

        if (ImGui.GetContentRegionAvail().X >= WarTwoColumnMinWidth &&
            ImGui.BeginTable("##warSpawnColumns", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Choose", ImGuiTableColumnFlags.WidthStretch, 1.25f);
            ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextColumn();
            WarCard("1. Choose a creature", DrawWarPicker);
            ImGui.TableNextColumn();
            WarCard("2. Spawn it", DrawWarSpawnSettings);
            ImGui.EndTable();
        }
        else
        {
            WarCard("1. Choose a creature", DrawWarPicker);
            WarCard("2. Spawn it", DrawWarSpawnSettings);
        }
    }

    private void DrawWarPicker()
    {
        // Category tabs: Popular / Recent / Human / DemiHuman / Monster / All
        for (int c = 0; c < SpawnCategoryNames.Length; c++)
        {
            if (c > 0)
            {
                // Wrap the row when the next button would run past the card.
                var next = ImGui.CalcTextSize(SpawnCategoryNames[c]).X + ImGui.GetStyle().FramePadding.X * 2;
                if (ImGui.GetContentRegionAvail().X - ImGui.GetItemRectMax().X + ImGui.GetCursorScreenPos().X > next + WarCardPad + 8f)
                    ImGui.SameLine();
            }
            bool selected = spawnCategoryIndex == c;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.SmallButton(SpawnCategoryNames[c]))
            {
                spawnCategoryIndex = c;
                selectedCatalogEntry = null;
            }
            if (selected) ImGui.PopStyleColor();
        }

        // Search filter (Human / DemiHuman / Monster / All)
        if (spawnCategoryIndex >= 2)
        {
            ImGui.SetNextItemWidth(WarFill);
            ImGui.InputTextWithHint("##npcSearch", "Search by name...", ref spawnSearchFilter, 256);
        }

        IReadOnlyList<NpcCatalogEntry> entries = spawnCategoryIndex switch
        {
            0 => npcCatalog!.GetPopularEntries(),
            1 => npcCatalog!.GetRecentEntries(config.RecentNpcEntries),
            2 => npcCatalog!.Search(spawnSearchFilter, NpcCatalogType.Human),
            3 => npcCatalog!.Search(spawnSearchFilter, NpcCatalogType.DemiHuman),
            4 => npcCatalog!.Search(spawnSearchFilter, NpcCatalogType.Monster),
            5 => npcCatalog!.Search(spawnSearchFilter),
            _ => Array.Empty<NpcCatalogEntry>(),
        };

        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 15;
        if (ImGui.BeginListBox("##npcList", new Vector2(WarFill, listHeight)))
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
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - WarCardPad);
                ImGui.TextColored(
                    new Vector4(1f, 0.65f, 0.2f, 1f),
                    "This ModelChara-only DemiHuman lacks the required customize/equipment source data.");
                ImGui.PopTextWrapPos();
            }
        }
        else
            ImGui.TextDisabled("Nothing chosen.");
    }

    private void DrawWarSpawnSettings()
    {
        // Which side and how many.
        var asAlly = config.SpawnAsAlly;
        var side = asAlly ? 1 : 0;
        WarLabel("Side");
        HelpMarker("Opponent: fights your side.\nAlly: fights at your side, with its own attacks. Any creature can be one.");
        ImGui.SetNextItemWidth(WarFill);
        if (ImGui.Combo("##warSide", ref side, WarSideNames, WarSideNames.Length))
        {
            config.SpawnAsAlly = asAlly = side == 1;
            config.Save();
        }

        WarLabel("How many");
        var count = Math.Clamp(config.SpawnCount, 1, WarMaxSpawnCount);
        ImGui.SetNextItemWidth(WarFill);
        if (ImGui.SliderInt("##warCount", ref count, 1, WarMaxSpawnCount))
        {
            config.SpawnCount = count;
            config.Save();
        }

        // Where (opponents only), and whether they patrol.
        var placement = CurrentSpawnPlacement();
        var patrol = !asAlly && config.SpawnPatrol;
        if (!asAlly)
        {
            ImGui.Spacing();
            WarLabel("Placement");
            HelpMarker("Around me: a loose, layered ring around you.\n" +
                       "In front of me: fanned out ahead of your character.\n" +
                       "Across the map: each Spawn puts one squad at a random post away from you. " +
                       "It holds the post until your side comes within sight, or a neighbour is attacked. Needs vnavmesh.");
            // The map is offered only where there is a navigation mesh to find ground on.
            var mapOffered = npcSpawner.CanPlaceOnMap;
            var placementIndex = (int)placement;
            ImGui.SetNextItemWidth(WarFill);
            if (ImGui.Combo("##warPlacement", ref placementIndex, WarPlacementNames, mapOffered ? 3 : 2))
            {
                placement = (SpawnPlacement)placementIndex;
                config.SpawnPlacementMode = placementIndex;
                config.SpawnInFront = placement == SpawnPlacement.Front;
                config.Save();
            }

            ImGui.SetNextItemWidth(WarFill);
            if (placement == SpawnPlacement.Map)
            {
                var radius = Math.Clamp(config.MapSpawnRadius, 20f, 200f);
                if (ImGui.SliderFloat("##warMapRadius", ref radius, 20f, 200f, "within %.0f yalms"))
                {
                    config.MapSpawnRadius = radius;
                    config.Save();
                }
            }
            else
            {
                var dist = config.SpawnDistance;
                if (ImGui.SliderFloat("##warDistance", ref dist, 0.0f, 15.0f, "%.1f yalms away"))
                {
                    config.SpawnDistance = dist;
                    config.Save();
                }
            }

            if (ImGui.Checkbox("Patrol##warPatrol", ref patrol))
            {
                config.SpawnPatrol = patrol;
                config.Save();
            }
            HelpMarker("Decided when it is spawned: it walks about where it was put instead of standing, " +
                       "and joins the fight only when your side comes within sight or a neighbour is attacked.");
        }

        // Strength + level. The opponents' apply to every enemy: spawned, map-sensed, auto-selected
        // and a profile's. Allies from the catalog have their own.
        ImGui.Spacing();
        if (asAlly)
        {
            var allyStrength = config.AllyStrength;
            var allyLevel = config.AllyLevel;
            if (DrawEnemyStrengthAndLevel("warAllies", "Strength", "Level", ref allyStrength, ref allyLevel,
                    AllyStrengthHelp, AllyLevelHelp))
            {
                config.AllyStrength = allyStrength;
                config.AllyLevel = allyLevel;
                config.Save();
            }
        }
        else
        {
            var enemyStrength = config.EnemyStrength;
            var enemyLevel = config.EnemyLevel;
            if (DrawEnemyStrengthAndLevel("virtualEnemies", "Strength", "Level", ref enemyStrength, ref enemyLevel))
            {
                config.EnemyStrength = enemyStrength;
                config.EnemyLevel = enemyLevel;
                config.Save();
            }
        }

        // The Spawn button, as wide as the card.
        ImGui.Spacing();
        var room = asAlly
            ? CombatCompanionManager.MaxCompanionCap - companionManager.Companions.Count - companionManager.PendingCount - PendingAllies()
            : npcSpawner.MaxNpcs - npcSpawner.TotalCount;
        bool canSpawn = selectedCatalogEntry is { IsSpawnable: true } && room > 0;
        var spawnLabel = selectedCatalogEntry == null
            ? "Spawn"
            : count > 1 ? $"Spawn {count} x {selectedCatalogEntry.Name}" : $"Spawn {selectedCatalogEntry.Name}";
        using (ImRaii.Disabled(!canSpawn))
        {
            if (ImGui.Button($"{spawnLabel}###warSpawn", new Vector2(MathF.Max(80f, ImGui.GetContentRegionAvail().X - WarCardPad), 0)))
                SpawnChosen(asAlly, count, room, placement, patrol);
        }
        if (selectedCatalogEntry == null)
            ImGui.TextDisabled("Choose a creature first.");
        else if (room <= 0)
            ImGui.TextDisabled(asAlly ? "The party is full." : "No room for more opponents.");

        if (npcSpawner.PendingCount > 0)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"{npcSpawner.PendingCount} on the way...");
    }

    private void SpawnChosen(bool asAlly, int count, int room, SpawnPlacement placement, bool patrol)
    {
        // Spawns simulated actors: refused while a safety barrier is down.
        if (!PassSafetyBarrier(null, spawnsActors: true))
            return;

        var entry = selectedCatalogEntry!;

        // Always available: activate spawn mode and start the simulation on demand so the user
        // can just search and spawn.
        npcSpawner.SpawnModeActive = true;
        if (!combatEngine.IsActive)
        {
            combatEngine.StartSimulation();
            chatGui.Print("[CombatSim] War Generator: combat simulation started.");
        }
        if (asAlly && !config.EnableCombatCompanions)
        {
            // An ally is a companion: the party has to be on for it to fight.
            config.EnableCombatCompanions = true;
        }

        // One Spawn on the map is one squad at one post.
        var squad = !asAlly && placement == SpawnPlacement.Map ? nextSquadId++ : 0;
        var tier = CombatBudgetModel.TierForChoice(asAlly ? config.AllyStrength : config.EnemyStrength);
        var level = asAlly ? config.AllyLevel : config.EnemyLevel;
        var queued = Math.Min(count, room);
        for (var i = 0; i < queued; i++)
            npcSpawner.QueueSpawn(BuildSpawnRequest(entry, asAlly, placement, patrol, squad, tier, level));

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

        chatGui.Print(queued > 1
            ? $"[CombatSim] Spawning {queued} x {entry.Name}..."
            : $"[CombatSim] Spawning {entry.Name}...");
    }

    /// <summary>Both sides as they stand: allies and opponents, each on its own card.</summary>
    private void DrawWarFieldSection()
    {
        var allies = companionManager.Companions.Count;
        var opponents = npcSpawner.SpawnedNpcs.Count;
        if (allies == 0 && opponents == 0)
        {
            ImGui.TextDisabled("Nobody spawned yet.");
            return;
        }

        if (ImGui.GetContentRegionAvail().X >= WarTwoColumnMinWidth &&
            ImGui.BeginTable("##warFieldColumns", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            WarCard($"Allies  {allies}/{CombatCompanionManager.MaxCompanionCap}", DrawWarAllies);
            ImGui.TableNextColumn();
            WarCard($"Opponents  {opponents}/{npcSpawner.MaxNpcs}", DrawWarOpponents);
            ImGui.EndTable();
        }
        else
        {
            WarCard($"Allies  {allies}/{CombatCompanionManager.MaxCompanionCap}", DrawWarAllies);
            WarCard($"Opponents  {opponents}/{npcSpawner.MaxNpcs}", DrawWarOpponents);
        }
    }

    // The field lists show this many rows; more scroll.
    private const int WarFieldVisibleRows = 10;

    /// <summary>Start the scrolling list of one side's HP bars; end it with ImGui.EndChild.</summary>
    private static void BeginWarFieldList(string id, int rows)
    {
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var height = Math.Min(rows, WarFieldVisibleRows) * rowHeight + ImGui.GetStyle().WindowPadding.Y;
        ImGui.BeginChild(id, new Vector2(0, height), false);
    }

    /// <summary>One row of the field lists: an HP bar with the name on it, then the row's buttons.</summary>
    private static void DrawWarFieldHpBar(SimulatedEntityState state, string name, Vector4 color, float buttonsWidth)
    {
        var hp = state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 0f;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, state.IsAlive ? color : new Vector4(0.4f, 0.4f, 0.4f, 1));
        var width = MathF.Max(60f, ImGui.GetContentRegionAvail().X - WarCardPad - buttonsWidth);
        ImGui.ProgressBar(hp, new Vector2(width, 0), state.IsAlive ? $"{name}  {state.CurrentHp:N0}/{state.MaxHp:N0}" : $"{name}  (down)");
        ImGui.PopStyleColor();
    }

    /// <summary>The allies on the field, with HP bars and a way to send each away.</summary>
    private void DrawWarAllies()
    {
        var allies = companionManager.Companions;
        if (allies.Count == 0)
        {
            ImGui.TextDisabled("None.");
            return;
        }

        if (allies.Count > 1 && ImGui.SmallButton("Despawn all allies##warAllies"))
        {
            companionManager.DespawnAll();
            return;
        }

        var despawnWidth = ImGui.CalcTextSize("Despawn").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;
        CombatCompanion? dismissed = null;
        BeginWarFieldList("##warAllyList", allies.Count);
        for (int i = allies.Count - 1; i >= 0; i--)
        {
            var ally = allies[i];
            ImGui.PushID($"ally_{i}");
            DrawWarFieldHpBar(ally.State, ally.Name, new Vector4(0.25f, 0.55f, 0.95f, 1), despawnWidth);
            ImGui.SameLine();
            if (ImGui.SmallButton("Despawn"))
                dismissed = ally;
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (dismissed != null)
            companionManager.DespawnCompanion(dismissed);
    }

    /// <summary>The opponents on the field: HP bars, the per-opponent ranged switch, despawn.</summary>
    private void DrawWarOpponents()
    {
        var opponents = npcSpawner.SpawnedNpcs;
        if (opponents.Count == 0)
        {
            ImGui.TextDisabled("None.");
            return;
        }

        if (opponents.Count > 1 && ImGui.SmallButton("Despawn all opponents##warOpponents"))
        {
            foreach (var npc in new List<SimulatedNpc>(npcSpawner.SpawnedNpcs))
                npcSelector.UnregisterSpawnedNpc(npc);
            npcSpawner.DespawnAll();
            return;
        }

        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.GetFrameHeight() + style.ItemSpacing.X +
                           ImGui.CalcTextSize("Despawn").X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        SimulatedNpc? sentAway = null;
        BeginWarFieldList("##warOpponentList", opponents.Count);
        for (int i = opponents.Count - 1; i >= 0; i--)
        {
            var npc = opponents[i];
            ImGui.PushID($"spawned_{i}");

            var hp = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 0f;
            var hpColor = hp > 0.5f ? new Vector4(0, 0.7f, 0.1f, 1)
                : hp > 0.25f ? new Vector4(0.8f, 0.7f, 0, 1)
                : new Vector4(0.8f, 0.1f, 0.1f, 1);
            DrawWarFieldHpBar(npc.State, npc.Name, hpColor, buttonsWidth);

            // Ranged toggle: this opponent's actions play the ranged attack motion (bow/gun draw +
            // projectile) instead of melee.
            ImGui.SameLine();
            bool ranged = npc.IsRanged;
            if (ImGui.Checkbox("##ranged", ref ranged))
                npc.IsRanged = ranged;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ranged: force ranged attack animation for this NPC (use for archers / casters whose default action animation is melee).");

            ImGui.SameLine();
            if (ImGui.SmallButton("Despawn"))
                sentAway = npc;

            ImGui.PopID();
        }
        ImGui.EndChild();

        // (After the list: despawning while walking it would change it under the loop.)
        if (sentAway != null)
        {
            npcSelector.UnregisterSpawnedNpc(sentAway);
            npcSpawner.DespawnNpc(sentAway);
        }
    }

    /// <summary>Who of the map joins a battle put together here: creatures, and other players.</summary>
    private void DrawWarMapSection()
    {
        ImGui.TextDisabled("For a battle put together here; a profile brings its own.");
        WarCard("Creatures of the map", () =>
        {
            var creatures = config.EnableMapEnemySensing ? 1 : 0;
            ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad - 30f));
            if (ImGui.Combo("##warMapCreatures", ref creatures, WarMapCreatureNames, WarMapCreatureNames.Length))
            {
                config.EnableMapEnemySensing = creatures == 1;
                config.Save();
            }
            HelpMarker("Real creatures of the map near your side join the opponents.");
        });

        WarCard("Other players in sight", () =>
        {
            var players = config.EnableMapPlayerEnemySensing
                ? (config.SensePartyMembers ? 3 : 2)
                : (config.SensePartyMembers ? 1 : 0);
            ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad - 30f));
            if (ImGui.Combo("##warMapPlayers", ref players, WarMapPlayerNames, WarMapPlayerNames.Length))
            {
                config.SensePartyMembers = players is 1 or 3;
                config.EnableMapPlayerEnemySensing = players is 2 or 3;
                if (config.SensePartyMembers)
                    config.EnableCombatCompanions = true;
                config.Save();
            }
            HelpMarker("Other players in sight are copied into the battle (only you see it): at your side, against you, or split between both.");
        });

        if (config.EnableMapEnemySensing || config.EnableMapPlayerEnemySensing)
        {
            WarCard("Who joins the opponents", () =>
            {
                var range = Math.Clamp(config.MapEnemySenseRange, 1.0f, 80.0f);
                ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad - 120f));
                if (ImGui.SliderFloat("Joins within##warMapRange", ref range, 1.0f, 80.0f, "%.0f yalms"))
                {
                    config.MapEnemySenseRange = range;
                    config.Save();
                }
                HelpMarker("A creature or player of the map joins the opponents when it is this close to anyone on your side.");

                var maxMapEnemies = Math.Clamp(config.MapEnemyMaxCount, 0, 100);
                ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad - 120f));
                if (ImGui.SliderInt("At most##warMapMax", ref maxMapEnemies, 0, 100))
                {
                    config.MapEnemyMaxCount = maxMapEnemies;
                    config.Save();
                }
            });
        }
    }

    private static readonly string[] WarSideNames = { "Opponent", "Ally" };
    private static readonly string[] WarPlacementNames = { "Around me", "In front of me", "Across the map" };
    private const int WarMaxSpawnCount = 20;

    private const string AllyStrengthHelp =
        "How much HP an ally has. Its hits are the same as yours: a skill does the same damage whoever uses it.\n" +
        "Normal: about your HP.  Weak: 0.4 times.  Strong: 2 times.  Elite: 3.5 times.  Boss: 9 times.\n" +
        "Auto: read from the creature itself (ordinary ones Normal, notorious ones Strong or Elite, bosses Boss).";

    private const string AllyLevelHelp =
        "Only the gap to your own level matters, as it does for enemies: an ally above your level is tougher and hits harder by what the gap is worth.\n" +
        "Leave it on \"My level\" for an ally that hits exactly as hard as you.";

    /// <summary>Allies asked for that the spawner has not handed to the party yet.</summary>
    private int PendingAllies()
        => npcSpawner.PendingAllyCount;

    private static readonly string[] WarFallenRuleNames = { "Stay down", "Spring and Autumn", "Warring States" };
    private string warCaptiveEmoteFilter = string.Empty;

    /// <summary>
    /// What becomes of your side's fallen. One card picks the rule; under Warring States a second
    /// card holds its own options, split by when they apply: while you stand, and when you fall.
    /// </summary>
    private void DrawWarRules()
    {
        var rule = Math.Clamp(config.WarFallenRule, 0, WarFallenRuleNames.Length - 1);
        WarCard("The fallen", () =>
        {
            ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad - 30f));
            if (ImGui.Combo("##warFallen", ref rule, WarFallenRuleNames, WarFallenRuleNames.Length))
            {
                config.WarFallenRule = rule;
                config.Save();
            }
            HelpMarker("Stay down: the fallen of your side stay down.\n\n" +
                       "Spring and Autumn: an ally runs to a fallen friend (you first) and kneels there " +
                       $"for {CombatCompanionManager.RaiseSeconds:0} seconds to raise them; while at it, that ally takes " +
                       $"{CombatCompanionManager.RaiserDamageScale:0} times the damage. Killing it first stops the raise.\n\n" +
                       "Warring States: you are the lord, and nobody is raised, you included.");

            // The rule in one line, so the choice reads without opening the help.
            WarNote(rule switch
            {
                (int)WarFallenRule.SpringAndAutumn => "Allies raise the fallen.",
                (int)WarFallenRule.WarringStates => "You are the lord: nobody is raised, you included.",
                _ => "The fallen stay down.",
            });
        });

        if (rule != (int)WarFallenRule.WarringStates)
            return;

        WarCard("Warring States", () =>
        {
            ImGui.TextUnformatted("While you stand");
            ImGui.Indent();
            var captives = config.WarCaptives;
            if (ImGui.Checkbox("Hostages##warCaptives", ref captives))
            {
                config.WarCaptives = captives;
                config.Save();
            }
            HelpMarker("A blow that would fell you or an ally leaves them at 1 HP instead, taken captive. " +
                       "An enemy with nobody else left to fight walks over, stands over the captive a moment and executes them: " +
                       "the body drops at once. Winning the battle frees every captive.");
            WarNote(captives
                ? "A felling blow takes the victim captive instead."
                : "A felling blow kills.");
            ImGui.Unindent();

            ImGui.Spacing();
            ImGui.TextUnformatted("When you fall");
            ImGui.Indent();
            var retreat = config.WarRetreat;
            if (ImGui.Checkbox("Retreat##warRetreat", ref retreat))
            {
                config.WarRetreat = retreat;
                config.Save();
            }
            HelpMarker("One ally goes to stand before your body and prays: it cannot raise you or fight, " +
                       "and takes 4 times the damage. If it falls, the next ally takes its place. The others guard it.\n" +
                       "Drive the enemy off to the last and the body is recovered. If an enemy reaches the body, " +
                       "it carries it off (Enemy Control's grab, with its current settings) and the battle is lost.");
            WarNote(retreat
                ? "One ally prays over your body, the rest guard it. If an enemy reaches it, the battle is lost and your allies are taken captive."
                : "Your allies are taken captive, to be executed.");
            ImGui.Unindent();

            ImGui.Spacing();
            ImGui.TextUnformatted("Captives");
            ImGui.Indent();
            WarLabel("Emote they hold");
            DrawWarCaptiveEmotePicker();
            ImGui.Unindent();
        });
    }

    /// <summary>A muted line of explanation that wraps inside the card.</summary>
    private static void WarNote(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - WarCardPad);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawWarCaptiveEmotePicker()
    {
        EnsureEnemyControlGrabEmoteCache();
        var ids = EnemyControlGrabEmoteIds!;
        var names = EnemyControlGrabEmoteNames!;
        var idx = Array.IndexOf(ids, config.WarCaptiveEmoteId);
        var preview = idx > 0 ? names[idx] : "Kneel";

        ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X - WarCardPad));
        if (ImGui.BeginCombo("##warCaptiveEmote", preview))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##warCaptiveEmoteFilter", "search", ref warCaptiveEmoteFilter, 32);
            // (Entry 0 of the shared list is Enemy Control's own default pose: not an emote.)
            for (int i = 1; i < names.Length; i++)
            {
                if (warCaptiveEmoteFilter.Length > 0 &&
                    names[i].IndexOf(warCaptiveEmoteFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (ImGui.Selectable($"{names[i]}##warEmote{ids[i]}", i == idx))
                {
                    config.WarCaptiveEmoteId = ids[i];
                    config.Save();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The emote a captive holds (humanoids only; a creature of another build just stands).");
    }

    private static readonly string[] WarMapCreatureNames = { "Stay out", "Join the opponents" };
    private static readonly string[] WarMapPlayerNames = { "Stay out", "Join my side", "Join the opponents", "Split between sides" };

    private static readonly string[] VirtualEnemyLookAtBones = { "j_kao", "j_kubi", "j_sebo_c" };

    private void DrawVirtualEnemyLookAtSection()
    {
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
        var bones = VirtualEnemyLookAtBones.Contains(bone) ? VirtualEnemyLookAtBones : VirtualEnemyLookAtBones.Append(bone).ToArray();
        var boneIndex = Array.IndexOf(bones, bone);
        var boneLabels = bones.Select(VirtualEnemyBoneLabel).ToArray();
        ImGui.SetNextItemWidth(240);
        if (ImGui.Combo("Turn##veLookAt", ref boneIndex, boneLabels, boneLabels.Length))
        {
            config.VirtualEnemyLookAtBone = bones[boneIndex];
            config.Save();
        }
        HelpMarker("What turns toward the target. Neck: the head follows. Upper chest: the neck and head follow. Enemies without this bone are left alone.");
        ImGui.Unindent();
    }

    private static string VirtualEnemyBoneLabel(string name) => name switch
    {
        "j_kao" => "Head",
        "j_kubi" => "Neck",
        "j_sebo_c" => "Upper chest",
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
                    RequestStartSimulation();
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

            var randomHits = config.EnableCriticalHits || config.EnableDirectHits;
            if (ImGui.Checkbox("Random crits / direct hits", ref randomHits))
            {
                config.EnableCriticalHits = config.EnableDirectHits = randomHits;
                config.Save();
            }
            HelpMarker("Attacks can randomly land as critical and/or direct hits. Off = every hit deals its base damage.");

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

    private static readonly string[] ActionDifficultyLabels = { "Easy", "Normal", "Hard" };

    private void DrawActionModeSection()
    {
        if (!ImGui.CollapsingHeader("Action Combat"))
            return;

        var difficulty = Math.Clamp(config.ActionDifficulty, 0, ActionDifficultyLabels.Length - 1);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Difficulty##actionmode", ref difficulty, ActionDifficultyLabels, ActionDifficultyLabels.Length))
        {
            config.ActionDifficulty = difficulty;
            config.Save();
        }
        HelpMarker("Sets enemy wind-up time, enemy attack pace and how forgiving guard timing is.\n" +
                   "Easy: slower enemies, wider guard windows. Hard: faster enemies, tighter guard windows.");

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

        var showTele = config.ShowTelegraphs;
        if (ImGui.Checkbox("Show telegraph circles", ref showTele))
        {
            config.ShowTelegraphs = showTele;
            config.Save();
        }
        HelpMarker("Draw ground danger zones under enemy attacks.");

        var osu = config.OsuCircleEnabled;
        if (ImGui.Checkbox("Show parry circle", ref osu))
        {
            config.OsuCircleEnabled = osu;
            config.Save();
        }
        HelpMarker("Approach circle on the player that shrinks onto a target ring; aligned = the guard window.");

        if (ImGui.Button("Reset Action Mode Defaults"))
        {
            config.ResetActionModeDefaults();
            config.Save();
        }
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

    /// <summary>Dropdown over named presets. Shows "Custom" when the saved values match none of them.</summary>
    private static bool PresetCombo(string label, string[] names, int current, out int selected)
    {
        selected = current;
        if (!ImGui.BeginCombo(label, current >= 0 && current < names.Length ? names[current] : "Custom"))
            return false;
        var changed = false;
        for (var i = 0; i < names.Length; i++)
        {
            if (ImGui.Selectable(names[i], i == current))
            {
                selected = i;
                changed = true;
            }
        }
        ImGui.EndCombo();
        return changed;
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

    private static readonly string[] ImpactStrengthLabels = { "Light", "Normal", "Strong" };
    private static readonly (float HitstopMs, float Shake, float ShakeDuration)[] ImpactStrengthPresets =
    {
        (40f, 0.05f, 0.15f),
        (81.53f, 0.1f, 0.2f),
        (120f, 0.18f, 0.3f),
    };

    private static readonly string[] OthersSkillEffectLabels = { "All", "Limited", "None" };

    private void DrawHitVfxSection()
    {
        if (ImGui.CollapsingHeader("VFX"))
        {
            var actionVfx = config.EnableCharacterVfx || config.EnableTargetVfx;
            if (ImGui.Checkbox("Action VFX", ref actionVfx))
            {
                config.EnableCharacterVfx = config.EnableTargetVfx = actionVfx;
                config.Save();
            }
            HelpMarker("Spawn action effects: cast circles and caster effects, and impact effects on the target.");

            if (actionVfx)
            {
                var others = Math.Clamp(config.OthersSkillEffects, 0, OthersSkillEffectLabels.Length - 1);
                ImGui.SetNextItemWidth(220);
                if (ImGui.Combo("Others' skill effects##vfx", ref others, OthersSkillEffectLabels, OthersSkillEffectLabels.Length))
                {
                    config.OthersSkillEffects = others;
                    config.Save();
                }
                HelpMarker("Skill effects of allies and enemies. Your own, and anything that hits you, always show.\n" +
                           "Limited: only those near the camera, and a few a second, so a big battle does not flash white.\n" +
                           "None: only your own and those that hit you.");
            }

            var spark = config.EnableHitVfx || config.EnableHitSparkVfx;
            if (ImGui.Checkbox("Impact spark", ref spark))
            {
                config.EnableHitVfx = config.EnableHitSparkVfx = spark;
                config.Save();
            }
            HelpMarker("Spawn a spark on the struck target when an action has no impact effect of its own, and on Action Mode hits. " +
                       "Spawning effects on modified or spawned enemies can be fragile -- turn off if you see issues.");

            var skillRate = config.NpcSkillRateLimit;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Enemy skills/sec##npcdensity", ref skillRate, 0f, 20f, skillRate <= 0f ? "uncapped" : "%.1f"))
            {
                config.NpcSkillRateLimit = skillRate;
                config.Save();
            }
            HelpMarker("How many skill casts all hostile enemies together may start per second. An enemy that has to wait " +
                       "auto-attacks meanwhile and casts as soon as a slot frees. Player and companions are never limited. " +
                       "0 = uncapped.");

            if (!animationController.HitVfxAvailable)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "ActorVfxCreate not found — hit VFX unavailable.");
            }
        }

        if (ImGui.CollapsingHeader("Hit feedback"))
        {
            var hitFeedback = config.EnableHitFeedback;
            if (ImGui.Checkbox("Enable hit feedback", ref hitFeedback))
            {
                config.EnableHitFeedback = hitFeedback;
                config.Save();
            }
            HelpMarker("Impact feel for player attacks (Action Mode): hitstop and a camera punch, timed to the weapon's contact frame.");
            using (ImRaii.Disabled(!config.EnableHitFeedback))
            {
                var strength = Array.FindIndex(ImpactStrengthPresets, p =>
                    MathF.Abs(p.HitstopMs - config.HitstopMs) < 0.01f &&
                    MathF.Abs(p.Shake - config.HitCameraShake) < 0.0001f &&
                    MathF.Abs(p.ShakeDuration - config.HitCameraShakeDuration) < 0.0001f);
                ImGui.SetNextItemWidth(160);
                if (PresetCombo("Impact strength##hitfeedback", ImpactStrengthLabels, strength, out var picked))
                {
                    (config.HitstopMs, config.HitCameraShake, config.HitCameraShakeDuration) = ImpactStrengthPresets[picked];
                    config.Save();
                }
                HelpMarker("How long the struck target freezes and how hard the camera punches on impact.");
            }
        }

        DrawVictoryCelebrationSection();

        DrawNpcScaleSection();

        DrawEnemyVoiceSection();

        DrawPcDismemberSection();
        // The private PC Dismemberment section ends above; its "Timing" is drawn here, as its last row, while
        // that header is open (the setting is shared with Enemy Dismemberment).
        var devUnlocked = false;
        GetDevExperimentalUnlocked(ref devUnlocked);
        if (devUnlocked && ImGui.GetStateStorage().GetInt(ImGui.GetID("PC Dismemberment"), 0) != 0)
            DrawSeparationTimingCombo("pc");

        if (ImGui.CollapsingHeader("Enemy Dismemberment"))
        {
            DrawSeparationTimingCombo("enemy");
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
            HelpMarker("Randomly separates visible pieces when an enemy dies (see Timing). Human enemies use a fixed count; non-human enemies use a percentage of eligible skeleton branches.");

            if (config.EnableEnemyDismemberment)
            {
                var humanCount = Math.Clamp(config.EnemyHumanoidDismembermentCount, 0, DismemberParts.Length);
                if (ImGui.SliderInt("Human limb count##enemy_dismemberment_human_count", ref humanCount, 0, DismemberParts.Length))
                {
                    config.EnemyHumanoidDismembermentCount = humanCount;
                    config.Save();
                }

                var monsterPercent = Array.FindIndex(MonsterBonePercents, p => MathF.Abs(p - config.EnemyMonsterDismembermentBonePercent) < 0.01f);
                if (PresetCombo("Monster bone percent##enemy_dismemberment_monster_percent", MonsterBonePercentLabels, monsterPercent, out var pickedPercent))
                {
                    config.EnemyMonsterDismembermentBonePercent = MonsterBonePercents[pickedPercent];
                    config.Save();
                }
            }
        }
        DrawPlayerWeaponVisibilitySection();
    }

    private static readonly string[] SeparationTimingLabels = { "On death", "On ragdoll activated" };

    private void DrawSeparationTimingCombo(string idSuffix)
    {
        var timing = Math.Clamp((int)config.DismemberTiming, 0, SeparationTimingLabels.Length - 1);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo($"Timing##separation_timing_{idSuffix}", ref timing, SeparationTimingLabels, SeparationTimingLabels.Length))
        {
            config.DismemberTiming = (SeparationTiming)timing;
            config.Save();
        }
        HelpMarker("When the parts come off, for you (PC Dismemberment) and enemies (Enemy Dismemberment) alike.\n" +
                   "On death: at the moment of death, while the death animation still plays.\n" +
                   "On ragdoll activated: when the body goes limp (Ragdoll tab > Ragdoll Trigger > Ragdoll starts).");
    }

    private static readonly string[] MonsterBonePercentLabels = { "25%", "50%", "75%", "100%" };
    private static readonly float[] MonsterBonePercents = { 25f, 50f, 75f, 100f };
    private static readonly string[] HideWeaponLabels = { "None", "Main hand", "Off hand", "Both" };

    private void DrawPlayerWeaponVisibilitySection()
    {
        if (!ImGui.CollapsingHeader("Hide weapons")) return;
        var hide = (config.HidePlayerRightWeapon ? 1 : 0) | (config.HidePlayerLeftWeapon ? 2 : 0);
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo("Hide weapon##hide_player_weapon", ref hide, HideWeaponLabels, HideWeaponLabels.Length))
        {
            config.HidePlayerRightWeapon = (hide & 1) != 0;
            config.HidePlayerLeftWeapon = (hide & 2) != 0;
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

    private void DrawBarrierConfirmPopup()
    {
        if (showBarrierConfirmPopup)
        {
            ImGui.OpenPopup("##BarrierConfirm");
            showBarrierConfirmPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0));
        var popupOpen = true;
        if (ImGui.BeginPopupModal("##BarrierConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(BarrierRed, FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            ImGui.SameLine();
            ImGui.TextColored(BarrierRed, "Safety Barrier Down");

            ImGui.Spacing();
            ImGui.TextWrapped(
                $"A client-side safety barrier is not installed ({Barriers.FirstDown ?? "now back up"}), so simulated " +
                "actions or targets might reach the server as if they were real.\n\n" +
                (barrierRefusedSpawn
                    ? "Battles that spawn enemies or allies cannot start until it is back.\n\n"
                    : barrierPendingStart == null
                        ? "With an action interceptor down the simulation cannot run: it would stop itself at once.\n\n"
                        : "") +
                "Restart the game or reload Combat Simulator to reinstall it. " +
                "The Diagnose tab shows which barrier is down.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Only a plain start may go ahead on confirmation; a start that spawns simulated actors may not.
            if (!barrierRefusedSpawn && barrierPendingStart != null)
            {
                if (ImGui.Button("Start Anyway", new Vector2(150, 0)))
                {
                    var start = barrierPendingStart;
                    barrierPendingStart = null;
                    start();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
            }

            if (ImGui.Button("Go to Diagnose", new Vector2(120, 0)))
            {
                // Was hardcoded to 8, which had been the Settings tab — the button never went
                // where it said it did.
                selectedTab = TabIndex("Diagnose");
                config.ShowProfessionalWindow = true;
                config.Save();
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

    private void DrawDiagnoseSection()
    {
        DrawSafetyHookStatus();
        ImGui.Spacing();

        if (ImGui.Button("Recover Player Animation"))
        {
            if (AnimationController.PlayerBusyReason() is { } busy)
            {
                chatGui.PrintError($"[CombatSim] Not recovered: you are {busy}. Try again when you are not.");
            }
            else
            {
                ragdollController.Deactivate();
                animationController.RecoverPlayerAnimationState();
            }
        }
        HelpMarker("Force-clear CombatSimulator-owned player animation state if the local player gets stuck in a death, guard, or combat idle pose.");
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

        // Functions we call: another plugin's hook on one of these runs inside our call.
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Functions we call");
        ImGui.Spacing();

        if (ImGui.BeginTable("##HookTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Function", ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Hooked By", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var func in hookSafetyChecker.Functions)
            {
                if (func.Address == 0)
                    continue; // virtual function not resolved yet (no local player at scan time)

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(func.Name);

                ImGui.TableNextColumn();
                if (func.IsHooked && func.RaisesConflict)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "HOOKED");
                }
                else if (func.IsHooked)
                {
                    // Informational: the game calls this for every actor; see HookSafetyChecker.RaisesConflict.
                    ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 1f), "Hooked");
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
                if (func.IsHooked && func.RaisesConflict)
                {
                    ImGui.TextWrapped(func.DetourInfo);
                }
                else
                {
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextDisabled(func.Description);
                    ImGui.PopTextWrapPos();
                }
            }

            ImGui.EndTable();
        }

        if (hookSafetyChecker.Functions.Count == 0)
        {
            ImGui.TextDisabled("No native functions registered for checking.");
        }

        ImGui.Spacing();

        // Functions we hook: other plugins on the same address. Dalamud chains the hooks, so this is
        // informational and never raises the conflict warning.
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Functions we hook");
        HelpMarker("Other plugins that hook the same game functions as Combat Simulator. " +
                   "Dalamud runs both hooks one after the other, so this is not a conflict by itself; " +
                   "it tells you where to look if something misbehaves only with that plugin enabled.");
        ImGui.Spacing();

        if (ImGui.BeginTable("##OwnHookTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Our hook", ImGuiTableColumnFlags.WidthFixed, 280);
            ImGui.TableSetupColumn("Also hooked by", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var own in hookSafetyChecker.OwnHooks)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(own.Name);
                ImGui.TableNextColumn();
                if (own.AlsoHookedBy.Length > 0)
                    ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 1f), own.AlsoHookedBy);
                else
                    ImGui.TextDisabled("-");
            }

            ImGui.EndTable();
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

        var actionOk = Barriers.UseActionHealthy;
        var actionLocationOk = Barriers.UseActionLocationHealthy;
        var targetGuardOk = Barriers.HardTargetGuardHealthy;
        var allOk = SafetyBarriersHealthy;

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
            ImGui.TableSetupColumn("Barrier", ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Protects against", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawSafetyHookRow("UseAction interceptor", actionOk,
                "Simulated actions being sent to the server as real action packets.");
            DrawSafetyHookRow("UseActionLocation interceptor", actionLocationOk,
                "Ground-target confirms, or other plugins calling the game directly, sending a real action during the simulation.");
            DrawSafetyHookRow("Hard-target guard", targetGuardOk,
                "Clicking a simulated actor syncing a fake target id to the server.");

            ImGui.EndTable();
        }

        if (!allOk)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "If a barrier is down, restart the game or reload Combat Simulator to reinstall its hooks. " +
                "Battles that spawn enemies or allies will not start, simulated actors are not targetable, and " +
                "the simulation auto-stops if an action interceptor fails while combat is active.");
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

            var showAllLabels = config.ShowAllBarLabels;
            if (ImGui.Checkbox("Show All Names and Numbers", ref showAllLabels))
            {
                config.ShowAllBarLabels = showAllLabels;
                config.Save();
            }
            HelpMarker("Every bar at full size with names and HP numbers, enemies and allies too.\nOff: only your own bar is full; the others are small and show only while they matter to you\n(your target, whoever attacks you, whoever was just hit, anyone near, enemies casting nearby, allies praying, raising or held captive).\nAnonymous Mode hides every name and number regardless.");

            var resultBanner = config.ShowResultBanner;
            if (ImGui.Checkbox("Show Battle Result Banner", ref resultBanner))
            {
                config.ShowResultBanner = resultBanner;
                if (!resultBanner)
                    HideResultBanner?.Invoke();
                config.Save();
            }
            HelpMarker("When a battle is won or lost, its result and the reason stay across the screen until the battle is reset.");

            var damageNumbers = config.ShowDamageNumbers;
            if (ImGui.Checkbox("Show Damage Numbers", ref damageNumbers))
            {
                config.ShowDamageNumbers = damageNumbers;
                config.Save();
            }
            HelpMarker("Every hit shows its damage above whoever took it: white on enemies, red on you and your allies, larger and gold for a critical hit. A trailing ! marks a direct hit.");

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

                ImGui.TextDisabled("Name labels show what each of your side is doing: Fighting, Raising, Guarding, Praying, Captive, Fallen.");

                var deathNameAlias = config.DeathNameAlias;
                if (ImGui.InputTextWithHint("Death Name Alias", "empty = keep your name", ref deathNameAlias, 64))
                {
                    config.DeathNameAlias = deathNameAlias;
                    config.Save();
                }
                HelpMarker("Name shown on the simulated HP bars only while you are defeated. The normal name returns on revive.");

                ImGui.TreePop();
            }
        }
    }

    private static readonly string[] CameraModeLabels = { "Game", "Follow bone", "Directed", "Duel" };
    private static readonly string[] ActiveCamVerticalLabels = { "Free", "Locked: level", "Locked: slight tilt", "Locked: strong tilt" };
    private static readonly float[] ActiveCamVerticalAngles = { 0f, 0f, 0.4f, 0.8f };

    /// <summary>Width of the Camera page's controls: most of the page, never narrower than 200 px.</summary>
    private static float CameraItemWidth() => MathF.Max(200f, ImGui.GetContentRegionAvail().X * 0.6f);

    private void DrawActiveCamSection()
    {
        // One mode instead of two Enable switches: Active Camera always took priority over the
        // directed camera when both were on, so "Follow bone" here means exactly that.
        var mode = config.EnableActiveCamera ? 1 : config.EnableDynamicCamera ? 2 : config.EnableDuelCamera ? 3 : 0;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (ImGui.Combo("Camera mode##camera", ref mode, CameraModeLabels, CameraModeLabels.Length))
        {
            config.EnableActiveCamera = mode == 1;
            config.EnableDynamicCamera = mode == 2;
            config.EnableDuelCamera = mode == 3;
            config.Save();
            activeCameraController.SetActive(mode == 1);
        }
        HelpMarker("Game: the game's own camera.\n" +
                   "Follow bone: the camera tracks a bone of your character; you rotate and zoom freely.\n" +
                   "Directed: the camera frames you over the shoulder in combat and plays an automatic death shot when you go down.\n" +
                   "Duel: the camera frames you and one opponent together; made for one-on-one fights.");

        var directedOnBattle = config.DirectedCameraOnBattle;
        if (ImGui.Checkbox("Switch to Directed when a battle starts##camera", ref directedOnBattle))
        {
            config.DirectedCameraOnBattle = directedOnBattle;
            config.Save();
        }
        HelpMarker("Start or Reset puts the camera in Directed mode, unless it is in Duel mode.");

        if (!config.EnableActiveCamera)
            return;
        if (!ImGui.CollapsingHeader("Follow-bone Camera", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();

        var bones = ActiveCameraController.CenterBones;
        var boneNames = new string[bones.Length];
        for (int b = 0; b < bones.Length; b++)
            boneNames[b] = bones[b].Label;

        int boneIdx = 0;
        for (int b = 0; b < bones.Length; b++)
            if (bones[b].BoneName == config.ActiveCameraBoneName) { boneIdx = b; break; }

        ImGui.SetNextItemWidth(CameraItemWidth());
        if (ImGui.Combo("Center Bone##activecam", ref boneIdx, boneNames, boneNames.Length))
        {
            config.ActiveCameraBoneName = bones[boneIdx].BoneName;
            config.Save();
        }

        var height = config.ActiveCameraHeightOffset;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (ImGui.SliderFloat("Height Offset##activecam", ref height, -1f, 3f, "%.2f"))
        {
            config.ActiveCameraHeightOffset = height;
            config.Save();
        }

        var vertical = !config.ActiveCameraLockVertical ? 0
            : config.ActiveCameraVerticalAngle == 0f ? 1
            : Array.FindIndex(ActiveCamVerticalAngles, a => a > 0f && MathF.Abs(a - config.ActiveCameraVerticalAngle) < 0.005f);
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (PresetCombo("Vertical angle##activecam", ActiveCamVerticalLabels, vertical, out var pickedVertical))
        {
            config.ActiveCameraLockVertical = pickedVertical > 0;
            if (pickedVertical > 0) config.ActiveCameraVerticalAngle = ActiveCamVerticalAngles[pickedVertical];
            config.Save();
        }
        HelpMarker("Free: you control the vertical angle. Locked: mouse/keyboard vertical input is ignored.");

        var closeUp = config.ActiveCameraDisableCollision || config.ActiveCameraPreventFade;
        if (ImGui.Checkbox("Close-up mode##activecam", ref closeUp))
        {
            config.ActiveCameraDisableCollision = config.ActiveCameraPreventFade = closeUp;
            config.Save();
        }
        HelpMarker("The camera passes through walls and objects, and characters no longer fade out when it comes very close.");

        var minDist = config.ActiveCameraMinZoomDistance;
        ImGui.SetNextItemWidth(CameraItemWidth());
        if (ImGui.SliderFloat("Min Zoom Distance##activecam", ref minDist, 0.0f, 2.0f, "%.2f"))
        {
            config.ActiveCameraMinZoomDistance = minDist;
            config.Save();
        }
        HelpMarker("Minimum camera distance. Lower = closer zoom.");

        var toolbar = config.ShowActiveCamToolbar;
        if (ImGui.Checkbox("Show toolbar##activecam", ref toolbar))
        {
            config.ShowActiveCamToolbar = toolbar;
            config.Save();
        }
        HelpMarker("A small floating bar to adjust the camera offsets without this window open.");

        ImGui.Unindent();
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

        if (guided.Enabled && (config.RagdollActivationDelay > 0.001f || config.RagdollTriggerMode != (int)RagdollTriggerMode.FixedDelay))
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "The ragdoll does not start at once; the buckle starts from the death-animation pose it starts in.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Start at once##guidedcollapseDelay"))
            {
                config.RagdollTriggerMode = (int)RagdollTriggerMode.FixedDelay;
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

                var triggerMode = (int)RagdollTrigger.ClampMode(config.RagdollTriggerMode);
                ImGui.SetNextItemWidth(360);
                if (ImGui.Combo("Ragdoll starts##ragdoll", ref triggerMode, RagdollTrigger.Labels, RagdollTrigger.Labels.Length))
                {
                    config.RagdollTriggerMode = triggerMode;
                    config.Save();
                }
                HelpMarker("When physics takes the body over from the death animation.\n" +
                           "After a fixed delay: the number of seconds below.\n" +
                           "One frame before the death animation ends: the animation plays out, and physics catches its last frame.\n" +
                           "At 90% of the death animation: a little earlier, while the body still has some motion.\n" +
                           "When the body falls fastest: as soon as the body's middle drops fast, or at the fastest point of a slower fall.\n" +
                           "When the body has landed: once it has come down and stopped falling.\n" +
                           "When the body has stopped moving: once the death animation has moved the body and it then lies still, for deaths that end in seconds of stillness.\n" +
                           "The last five also start when the death animation runs out, and never later than 30 seconds.");

                if (triggerMode == (int)RagdollTriggerMode.FixedDelay)
                {
                    var delay = config.RagdollActivationDelay;
                    ImGui.SetNextItemWidth(360);
                    if (ImGui.SliderFloat("Activation Delay (s)##ragdoll", ref delay, 0.0f, RagdollTrigger.MaximumWait, "%.1f"))
                    {
                        config.RagdollActivationDelay = delay;
                        config.Save();
                    }
                    HelpMarker("Seconds after death before ragdoll physics take over.");
                }

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

                var exactGround = config.RagdollExactGroundCollision;
                if (ImGui.Checkbox("Exact ground collision##ragdoll", ref exactGround))
                {
                    config.RagdollExactGroundCollision = exactGround;
                    config.Save();
                }
                HelpMarker("Off: the ground is a grid of heights sampled every metre, which smooths over steps, edges and props.\n" +
                           "On: the ground under your own body is the game's own collision mesh, triangle for triangle.\n" +
                           "Enemies and companions keep the sampled grid, so many of them dying at once costs nothing extra.\n" +
                           "Takes effect at the next ragdoll. Not used together with Rendered Mesh Body Collision. Default on.");

                // One dropdown: off, or which liquid the water behaves as.
                var liquid = config.RagdollWaterBuoyancy ? Math.Clamp(config.RagdollWaterLiquid, 0, AdvancedRagdoll.WaterBuoyancy.LiquidLabels.Length - 1) + 1 : 0;
                ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X * 0.6f, 220f, 380f));
                if (ImGui.BeginCombo("Float in water##ragdoll", liquid == 0 ? "Off" : AdvancedRagdoll.WaterBuoyancy.LiquidLabels[liquid - 1]))
                {
                    if (ImGui.Selectable("Off", liquid == 0))
                    {
                        config.RagdollWaterBuoyancy = false;
                        config.Save();
                    }
                    for (var i = 0; i < AdvancedRagdoll.WaterBuoyancy.LiquidLabels.Length; i++)
                    {
                        if (!ImGui.Selectable(AdvancedRagdoll.WaterBuoyancy.LiquidLabels[i], liquid == i + 1)) continue;
                        config.RagdollWaterBuoyancy = true;
                        config.RagdollWaterLiquid = i;
                        config.Save();
                    }
                    ImGui.EndCombo();
                }
                HelpMarker("A body in water is lifted and slowed by it: it rises to the surface, floats chest up and\n" +
                           "rides the swell. Works where the game itself has a water surface (anywhere you can fish or swim).\n" +
                           "Water: floats as a real body does, with only a little showing above the surface.\n" +
                           "Dense liquid: the water is twice as heavy and carries about half of the body above the surface.");

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

            var fineDetail = config.NpcRagdollFineDetail;
            if (ImGui.Checkbox("Fine physics for everyone##npcragdollfine", ref fineDetail))
            {
                config.NpcRagdollFineDetail = fineDetail;
                config.Save();
            }
            HelpMarker("Off (default): enemies' and allies' bodies use lighter physics than yours, and their dropped weapons " +
                       "land on simple body shapes instead of the body mesh. A battle with many bodies on the ground stays smooth.\n" +
                       "On: everyone's body and weapon get the same detail as yours. Costlier with many bodies.\n" +
                       "Applies to bodies that fall after the change.");

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
                RequestStartSimulation();
        }
        else
        {
            if (ImGui.Button("Stop", btnSize))
            {
                combatEngine.StopSimulation();
                chatGui.Print("[CombatSim] Combat simulation stopped.");
            }
        }

        // Barrier popup must be drawn at this window scope
        DrawBarrierConfirmPopup();

        ImGui.SameLine();
        if (ImGui.Button("Reset All", btnSize))
        {
            combatEngine.ResetState();
            chatGui.Print("[CombatSim] Combat state reset.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reboot", btnSize))
            RequestRebootSimulation();

        ImGui.End();
    }

    public void DrawActiveCamToolbar()
    {
        using var toolbarStyle = PushCompactToolbarStyle();
        // Closable, so the toolbar can be dismissed while its camera mode is not selected.
        var open = config.ShowActiveCamToolbar;
        var visible = ImGui.Begin("Active Cam", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize);
        if (!open)
        {
            config.ShowActiveCamToolbar = false;
            config.Save();
        }
        if (!visible)
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
