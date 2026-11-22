// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Gui;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using CombatSimulator.Spectators;
using CombatSimulator.UpdateLog;
using CombatSimulator.Voice;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// What the player sees and hears besides the game: the windows, the HP bars and damage numbers, the
/// overlays, the enemies' omen VFX, the enemies' voices, the update-log popup; and the per-frame drawing
/// of all of them (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// </summary>
internal sealed class PresentationModule
{
    private readonly PluginModules m;

    private MainWindow mainWindow = null!;
    private HpBarOverlay hpBarOverlay = null!;
    private DamageNumberOverlay damageNumbers = null!;
    private CombatLogWindow combatLogWindow = null!;
    private RagdollDebugOverlay ragdollDebugOverlay = null!;
    private CombatLinkOverlay combatLinkOverlay = null!;
    private TelegraphOverlay telegraphOverlay = null!;
    private EnemyAreaOverlay enemyAreaOverlay = null!;
    private CombatSimulator.ActionCombat.EnemyOmenVfx enemyOmenVfx = null!;
    private OsuParryOverlay osuParryOverlay = null!;
    private ReticleOverlay reticleOverlay = null!;
    private UpdateLogPopupController updateLogPopupController = null!;
    private VoiceDirector voiceDirector = null!;

    public PresentationModule(PluginModules modules) => m = modules;

    public MainWindow MainWindow => mainWindow;
    public HpBarOverlay HpBars => hpBarOverlay;
    public DamageNumberOverlay DamageNumbers => damageNumbers;
    public CombatLogWindow CombatLog => combatLogWindow;
    public CombatSimulator.ActionCombat.EnemyOmenVfx EnemyOmens => enemyOmenVfx;
    public VoiceDirector Voices => voiceDirector;

    private IPresentationModuleSettings config => m.Config;
    private IPluginLog log => m.Log;
    private NpcSelector npcSelector => m.Actors.Selector;
    private NpcSpawner npcSpawner => m.Actors.Spawner;
    private CombatCompanionManager companionManager => m.Actors.Companions;
    private MapEnemyController mapEnemyController => m.Actors.MapEnemies;
    private SpectatorController spectatorController => m.Actors.Spectators;
    private ActionDataProvider actionDataProvider => m.Actors.ActionData;
    private CombatEngine combatEngine => m.Combat.Engine;
    private UseActionHook useActionHook => m.Combat.UseAction;
    private CombatSimulator.ActionCombat.TelegraphSystem telegraphSystem => m.Combat.Telegraphs;
    private CombatSimulator.ActionCombat.EnemyAreaMarkers enemyAreaMarkers => m.Combat.AreaMarkers;
    private CombatSimulator.ActionCombat.PlayerGuardController playerGuardController => m.Combat.Guard;
    private CombatSimulator.ActionCombat.PlayerHitboxResolver playerHitboxResolver => m.Combat.PlayerHitbox;
    private GlamourerIpc glamourerIpc => m.World.Glamourer;
    private VNavmeshIpc vnavmeshIpc => m.World.VNavmesh;
    private AnimationController animationController => m.World.Animation;
    private BoneTransformService boneTransformService => m.World.BoneTransforms;
    private RagdollController ragdollController => m.Corpse.PlayerRagdoll;
    private DismembermentController dismembermentController => m.Corpse.Dismemberment;
    private Effects.ArrowImpactController arrowImpactController => m.Corpse.ArrowImpacts;
    private Dev.KoStripController armorDetachmentController => m.Corpse.ArmorDetachment;
    private ActiveCameraController activeCameraController => m.Camera.Active;
    private DynamicCameraController dynamicCameraController => m.Camera.Dynamic;
    private DynamicCameraDebugOverlay dynamicCameraDebugOverlay = null!;
    private EnemyControlController enemyControlController => m.Actors.EnemyControl;
    private HookSafetyChecker hookSafetyChecker => m.SafetyDev.HookSafety;
    private Dev.IDevExperimental devExperimental => m.SafetyDev.DevExperimental;
    private Gui.WarBanner warBanner => m.Battle.Banner;
    private void DrawWar() => m.Battle.DrawWar();

    public void CreateMainWindow(IClientState clientState, IDataManager dataManager, IChatGui chatGui)
    {
        mainWindow = new MainWindow(m.Config, npcSelector, npcSpawner, companionManager, combatEngine, mapEnemyController, glamourerIpc, vnavmeshIpc, animationController, ragdollController, dismembermentController, activeCameraController, dynamicCameraController, hookSafetyChecker, useActionHook, spectatorController, devExperimental, clientState, dataManager, chatGui, log);
        mainWindow.Barriers = m.Combat.Barriers;
        mainWindow.ArrowImpacts = arrowImpactController;
        mainWindow.HideResultBanner = warBanner.Hide;
        mainWindow.ArmorDetachment = armorDetachmentController;
        mainWindow.CutsceneShots = m.Camera.CutsceneShots;
        mainWindow.DuelCameraStatus = () => m.Camera.Duel?.Status ?? "off";
        mainWindow.ForgetDuelDeathShotAngle = () => m.Camera.DeathShots.ForgetAngle();
        enemyControlController.GrabStarted += mainWindow.HideEnemyControlAnchorMarkers;
    }

    /// <summary>The enemies' voices. (What it listens to and when it keeps quiet are wired by the
    /// plugin's constructor right after.)</summary>
    public void CreateVoice(IDataManager dataManager)
    {
        MigrateVoiceDefaults();
        voiceDirector = new VoiceDirector(m.Config, npcSelector, combatEngine,
            () => companionManager.HasLivingCompanions, dataManager, log);
        voiceDirector.DeadCompanionCount = () =>
            config.EnableCombatCompanions ? companionManager.Companions.Count(c => c.IsSpawned && !c.State.IsAlive) : 0;
    }

    /// <summary>
    /// Voices were experimental and off by default, so every existing config has "off" saved
    /// explicitly; the new on-by-default would only reach fresh installs. Switch them on once.
    /// </summary>
    private void MigrateVoiceDefaults()
    {
        if (m.Config.EnemyVoiceDefaultsMigrated20261024)
            return;
        m.Config.EnableEnemyVoices = true;
        m.Config.EnemyVoiceBubbles = true;
        m.Config.EnemyVoiceDefaultsMigrated20261024 = true;
        m.Config.Save();
    }

    public void TickVoice(float deltaTime) => voiceDirector.Tick(deltaTime);

    /// <summary>The HP bars and the damage numbers. (What they listen to is wired by the plugin's
    /// constructor right after.)</summary>
    public void CreateHpBarsAndDamageNumbers(IGameGui gameGui, IClientState clientState)
    {
        hpBarOverlay = new HpBarOverlay(npcSelector, companionManager, combatEngine, boneTransformService, gameGui, clientState, config);
        damageNumbers = new Gui.DamageNumberOverlay(gameGui, config, combatEngine.ResolveEntityAddress);
    }

    /// <summary>The combat log window, the overlays, the omen VFX and the update-log popup.</summary>
    public void CreateOverlays(IGameGui gameGui, IClientState clientState, IDataManager dataManager, bool hadExistingConfig)
    {
        combatLogWindow = new CombatLogWindow(combatEngine);
        ragdollDebugOverlay = new RagdollDebugOverlay(ragdollController, dismembermentController, mainWindow, config, gameGui, clientState);
        combatLinkOverlay = new CombatLinkOverlay(npcSelector, combatEngine, boneTransformService, gameGui, config);
        telegraphOverlay = new TelegraphOverlay(telegraphSystem, gameGui, config);
        hpBarOverlay.ExternalCast = npc =>
            telegraphSystem.TryGetCast(npc.SimulatedEntityId, out var castActionId, out var castProgress)
                ? (actionDataProvider.GetActionData(castActionId)?.Name ?? "Casting", castProgress)
                : null;
        enemyAreaOverlay = new EnemyAreaOverlay(enemyAreaMarkers, gameGui, config);
        enemyOmenVfx = new CombatSimulator.ActionCombat.EnemyOmenVfx(
            enemyAreaMarkers, animationController, actionDataProvider.GetActionData, dataManager, config, log);
        enemyAreaOverlay.HasNativeOmen = enemyOmenVfx.HasOmen;
        osuParryOverlay = new OsuParryOverlay(telegraphSystem, gameGui, config, playerGuardController);
        reticleOverlay = new ReticleOverlay(playerHitboxResolver, combatEngine, gameGui);
        dynamicCameraDebugOverlay = new DynamicCameraDebugOverlay(dynamicCameraController, gameGui, config);
        updateLogPopupController = new UpdateLogPopupController(m.Config, log, hadExistingConfig);
    }

    public void OnDraw(IClientState clientState)
    {
        // No plugin UI off-world (title / character select) — prevents acting on
        // a preview character (e.g. clicking Reset there crashes the game).
        if (!clientState.IsLoggedIn)
            return;

        // Raw mouse wheel for the dynamic camera's death shot: its zoom is pinned shut at
        // the game camera, so the wheel's INTENT has to be read here — the camera struct
        // has no pending-zoom field to intercept. Skipped while ImGui wants the mouse
        // (scrolling one of our own windows must not retune the shot).
        // (The duel camera and the automatic death shot of Duel mode take the wheel the same way.)
        var imguiIo = Dalamud.Bindings.ImGui.ImGui.GetIO();
        if (!imguiIo.WantCaptureMouse && imguiIo.MouseWheel != 0f)
        {
            dynamicCameraController.NotifyMouseWheel(imguiIo.MouseWheel);
            m.Camera.Duel.NotifyMouseWheel(imguiIo.MouseWheel);
            m.Camera.DeathShots.NotifyMouseWheel(imguiIo.MouseWheel);
        }

        if (config.ShowShortcuts)
            mainWindow.DrawShortcutsBar();

        if (config.ShowFastCombatToolbar)
            mainWindow.DrawFastCombatToolbar();

        if (config.ShowDynamicCamToolbar)
            mainWindow.DrawDynamicCamToolbar();

        if (config.ShowActiveCamToolbar)
            mainWindow.DrawActiveCamToolbar();

        if (config.ShowEnemyControlGui)
            mainWindow.DrawEnemyControlWindow(enemyControlController);

        devExperimental.DrawToolbars(mainWindow);

        if (config.ShowMainWindow)
            mainWindow.Draw();

        if (config.ShowProfessionalWindow)
            mainWindow.DrawProfessional();

        mainWindow.DrawDefeatRevivePopup();
        updateLogPopupController.Draw();

        if (combatEngine.IsActive)
        {
            if (config.ShowEnemyHpBar || config.ShowPlayerHpBar || config.ShowHudPlayerHpBar)
                hpBarOverlay.Draw();
            damageNumbers?.Draw();
            DrawWar();

            if (config.ShowCombatLog)
                combatLogWindow.Draw();

            if (config.ShowCombatLinkArcs)
                combatLinkOverlay.Draw();

            if (config.ShowTelegraphs)
                telegraphOverlay.Draw();
            // Real-shape enemy areas (any mode); empty unless an enemy-skill option registered one.
            enemyAreaOverlay.Draw();

            osuParryOverlay.Draw();
            reticleOverlay.Draw();
        }

        ragdollDebugOverlay.Draw();
        dynamicCameraDebugOverlay.Draw();

        if (!combatEngine.IsActive)
        {
            // Restore native HP bar when sim is not active
            hpBarOverlay.RestoreNativeHpBar();
        }
    }
}
