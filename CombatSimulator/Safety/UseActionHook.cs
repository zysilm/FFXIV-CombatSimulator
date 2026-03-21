using System;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatSimulator.Safety;

public unsafe class UseActionHook : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

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

    public UseActionHook(
        IGameInteropProvider gameInterop,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        Configuration config,
        IClientState clientState,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.config = config;
        this.clientState = clientState;
        this.log = log;

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
                return useActionHook!.Original(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            if (actionType != ActionType.Action)
                return useActionHook!.Original(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            var isSelected = IsSelectedTarget(targetId);

            // Auto-select: if simulation is active and target is not yet registered, register it
            if (!isSelected)
                isSelected = TryAutoSelectTarget(targetId);

            log.Info($"UseAction check: actionId={actionId}, targetId=0x{targetId:X}, isSelected={isSelected}");

            if (!isSelected)
                return useActionHook!.Original(actionManager, actionType, actionId,
                    targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

            // Intercept — route to simulation, don't send to server
            log.Info($"INTERCEPTED UseAction: actionId={actionId}, targetId=0x{targetId:X}");
            combatEngine.EnqueuePlayerAction((uint)actionType, actionId, targetId, extraParam);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in UseAction hook, passing through to original.");
            return useActionHook!.Original(actionManager, actionType, actionId,
                targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
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
            var localPlayer = clientState.LocalPlayer;
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
                config.DefaultNpcHpMultiplier,
                (NpcBehaviorType)config.DefaultNpcBehaviorType);

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
