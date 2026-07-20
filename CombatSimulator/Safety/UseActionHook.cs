using System;
using CombatSimulator.Fighting;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatSimulator.Safety;

public unsafe class UseActionHook : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly NpcSpawner npcSpawner;
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly PlayerTargetController targetController;
    private readonly MapEnemyController mapEnemyController;
    private readonly CombatSimulator.ActionCombat.IPlayerActionSink actionSink;
    private readonly IFightingModeInputSink fightingModeSink;

    // Client-only simulated actors are assigned entity ids at/above this floor. Such an
    // id exists only inside our local simulation — the server never issued it — so it must
    // never be handed to the real ActionManager (see CallOriginalGuarded).
    private const ulong SimulatedEntityIdFloor = 0xF0000000;

    private delegate bool UseActionDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    private Hook<UseActionDelegate>? useActionHook;
    private bool hookEnabled;

    /// <summary>
    /// True if the hook was created successfully and is currently enabled.
    /// When false during active simulation, the sim must be emergency-stopped
    /// to prevent unintercepted actions from reaching the server.
    /// </summary>
    public bool IsHealthy => useActionHook != null && hookEnabled && !useActionHook.IsDisposed;

    public UseActionHook(
        IGameInteropProvider gameInterop,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        NpcSpawner npcSpawner,
        Configuration config,
        IClientState clientState,
        IPluginLog log,
        PlayerTargetController targetController,
        MapEnemyController mapEnemyController,
        CombatSimulator.ActionCombat.IPlayerActionSink actionSink,
        IFightingModeInputSink fightingModeSink)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.npcSpawner = npcSpawner;
        this.config = config;
        this.clientState = clientState;
        this.log = log;
        this.targetController = targetController;
        this.mapEnemyController = mapEnemyController;
        this.actionSink = actionSink;
        this.fightingModeSink = fightingModeSink;

        try
        {
            useActionHook = gameInterop.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                UseActionDetour);

            log.Info("UseAction hook created successfully.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create UseAction hook. Combat simulation will not intercept actions.");
        }
    }

    public void Enable()
    {
        if (useActionHook != null && !hookEnabled)
        {
            useActionHook.Enable();
            hookEnabled = true;
            log.Info("UseAction hook enabled.");
        }
    }

    public void Disable()
    {
        if (useActionHook != null && hookEnabled)
        {
            useActionHook.Disable();
            hookEnabled = false;
            log.Info("UseAction hook disabled.");
        }
    }

    private bool UseActionDetour(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        try
        {
            log.Debug($"UseAction called: type={actionType}, actionId={actionId}, targetId=0x{targetId:X}, active={combatEngine.IsActive}");

            if (!combatEngine.IsActive)
                return CallOriginalGuarded(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            if (actionType != ActionType.Action)
                return CallOriginalGuarded(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            if (config.FightingMode)
            {
                log.Debug($"Fighting Mode input: actionId={actionId}, targetId=0x{targetId:X}");
                if (fightingModeSink.OnPlayerAction((uint)actionType, actionId, targetId, extraParam))
                    return true;
            }

            // Action Mode: hotbar presses are remapped to action-combat inputs
            // (light attack / guard / skill) and never reach the server. Bypasses all
            // sim-mode targeting/spawn routing below.
            if (config.ActionMode)
            {
                // Diagnostic: confirms out-of-range/no-target presses still reach us
                // (the game calls UseAction for them) — they should always whiff-swing.
                log.Debug($"Action Mode input: actionId={actionId}, targetId=0x{targetId:X}");
                actionSink.OnAction((uint)actionType, actionId, targetId, extraParam);
                return true;
            }

            // Custom targeting: our PlayerTargetController owns the player's target.
            // Route every combat action to the locked target; with no lock, swallow
            // the action (no server packet, no simulation) so "attack only when a
            // target is locked" holds.
            if (config.EnableCustomTargeting)
            {
                var lockedId = targetController.LockedTargetEntityId;
                if (lockedId == 0)
                {
                    lockedId = targetController.TryAcquireTargetForAction();
                    if (lockedId != 0)
                    {
                        log.Info($"INTERCEPTED (custom targeting acquired): actionId={actionId} -> locked 0x{lockedId:X}");
                        combatEngine.EnqueuePlayerAction((uint)actionType, actionId, lockedId, extraParam);
                        return true;
                    }

                    var selected = targetId is not 0 and not 0xE0000000
                        ? npcSelector.GetSelectedNpc((uint)targetId)
                        : null;
                    selected ??= mapEnemyController.TryRegisterByEntityId(targetId);
                    if (selected != null)
                    {
                        targetController.LockTarget(selected);
                        log.Info($"INTERCEPTED (map enemy join): actionId={actionId} -> 0x{selected.SimulatedEntityId:X}");
                        combatEngine.EnqueuePlayerAction((uint)actionType, actionId, selected.SimulatedEntityId, extraParam);
                        return true;
                    }

                    log.Debug("Custom targeting: no locked target; action ignored.");
                    return true;
                }

                log.Info($"INTERCEPTED (custom targeting): actionId={actionId} -> locked 0x{lockedId:X}");
                combatEngine.EnqueuePlayerAction((uint)actionType, actionId, lockedId, extraParam);
                return true;
            }

            // Spawn mode: when active, route all combat actions to the last alive
            // spawned NPC. No game target needed — bypasses TargetSystem entirely.
            if (npcSpawner.SpawnModeActive && (targetId == 0 || targetId == 0xE0000000))
            {
                var spawnTarget = npcSpawner.GetLastAliveSpawnedNpc();
                if (spawnTarget != null)
                {
                    targetId = spawnTarget.SimulatedEntityId;
                    log.Debug($"Spawn mode: routing action to '{spawnTarget.Name}' (0x{targetId:X}).");
                }
            }

            var isSelected = IsSelectedTarget(targetId);

            // Auto-select: if simulation is active and target is not yet registered, register it
            if (!isSelected)
                isSelected = TryAutoSelectTarget(targetId);

            log.Info($"UseAction check: actionId={actionId}, targetId=0x{targetId:X}, isSelected={isSelected}");

            if (!isSelected)
                return CallOriginalGuarded(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            // Intercept — route to simulation, don't send to server
            log.Info($"INTERCEPTED UseAction: actionId={actionId}, targetId=0x{targetId:X}");
            combatEngine.EnqueuePlayerAction((uint)actionType, actionId, targetId, extraParam);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in UseAction hook, passing through to original.");
            return CallOriginalGuarded(actionManager, actionType, actionId,
                targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
    }

    /// <summary>
    /// Invoke the real UseAction — unless the target id belongs to a client-only simulated
    /// actor (>= <see cref="SimulatedEntityIdFloor"/>), in which case swallow the call
    /// instead of passing it through. Every pass-through path (sim inactive, non-Action
    /// type, unselected target, and the exception fallback) routes through here so that a
    /// fake target id — e.g. one Spawn mode rewrote onto <c>targetId</c> before an
    /// exception — can never leave the client as a real action packet.
    /// </summary>
    private bool CallOriginalGuarded(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        if (targetId >= SimulatedEntityIdFloor)
        {
            log.Warning($"Suppressed UseAction pass-through: actionId={actionId} targets simulated 0x{targetId:X} " +
                        "(would leak an action packet to the server).");
            return true;
        }

        return useActionHook!.Original(actionManager, actionType, actionId,
            targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    /// <summary>
    /// Attempt to auto-select the target as a combat NPC when simulation is active.
    /// Returns true if the target was successfully registered.
    /// </summary>
    private bool TryAutoSelectTarget(ulong targetId)
    {
        try
        {
            // Don't auto-select self
            var localPlayer = CombatSimulator.Core.Services.ObjectTable.LocalPlayer;
            if (localPlayer == null || targetId == localPlayer.EntityId)
                return false;

            // Don't auto-select invalid targets
            if (targetId == 0 || targetId == 0xE0000000)
                return false;

            // Don't auto-select other players
            foreach (var obj in Core.Services.ObjectTable)
            {
                if (obj.EntityId == (uint)targetId)
                {
                    if (obj is IPlayerCharacter)
                    {
                        log.Debug($"Auto-select skipped: target 0x{targetId:X} is a player.");
                        return false;
                    }
                    break;
                }
            }

            // Use NpcSelector to register the target (it reads from targetManager.Target)
            var (npc, error) = npcSelector.SelectCurrentTarget(
                config.DefaultNpcLevel,
                config.DefaultNpcHpMultiplier);

            if (npc != null)
            {
                // Verify the selected target matches what we expected
                if (npc.SimulatedEntityId == (uint)targetId)
                {
                    combatEngine.RegisterNpcEntity(npc);
                    log.Info($"Auto-selected '{npc.Name}' (0x{targetId:X}) as combat target (Lv.{config.DefaultNpcLevel}).");
                    return true;
                }
                else
                {
                    // Selected wrong target — shouldn't happen but deselect to be safe
                    log.Warning($"Auto-select mismatch: expected 0x{targetId:X}, got 0x{npc.SimulatedEntityId:X}. Deselecting.");
                    npcSelector.DeselectNpc(npc);
                    return false;
                }
            }
            else
            {
                log.Debug($"Auto-select failed for 0x{targetId:X}: {error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Auto-select error for target 0x{targetId:X}.");
            return false;
        }
    }

    private bool IsSelectedTarget(ulong targetId)
    {
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.SimulatedEntityId == (uint)targetId)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        Disable();
        useActionHook?.Dispose();
    }
}
