using System;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatSimulator.Safety;

public unsafe class UseActionHook : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
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
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
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
