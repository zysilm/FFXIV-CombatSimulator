// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Simulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatSimulator.Safety;

/// <summary>
/// The action barrier: nothing the player does in the simulation may leave the client as a real action packet.
/// Two hooks, one rule:
/// <list type="bullet">
/// <item><c>ActionManager.UseAction</c> — the hotbar / keybind entry. During a simulation every combat action
/// (<see cref="ActionType.Action"/>) is routed into the simulation and never reaches the original.</item>
/// <item><c>ActionManager.UseActionLocation</c> — the game's "execute now" function. UseAction calls it for
/// every action it lets through, but it is also called directly: the game's ground-target confirm, and other
/// plugins (rotation helpers such as BossMod) that execute actions themselves. Without this hook those calls
/// would bypass the UseAction barrier. (A queued action most likely re-enters through UseAction itself.)</item>
/// </list>
/// Both reject a client-only simulated target (entity id at/above <see cref="SimulatedEntityIdFloor"/>) on
/// every pass-through, in or out of a simulation.
/// </summary>
public unsafe class UseActionHook : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly IPluginLog log;
    private readonly CombatSimulator.ActionCombat.IPlayerActionSink actionSink;

    // Client-only simulated actors are assigned entity ids at/above this floor. Such an
    // id exists only inside our local simulation — the server never issued it — so it must
    // never be handed to the real ActionManager (see CallOriginalGuarded).
    private const uint SimulatedEntityIdFloor = 0xF0000000;

    // An entity id (high 32 bits zero) at/above the floor. A GameObjectId with a non-zero high half is an object
    // without an entity id (its object index in the high half), never one of ours.
    private static bool IsSimulatedTarget(ulong targetId)
        => (targetId >> 32) == 0 && (uint)targetId >= SimulatedEntityIdFloor;

    private delegate bool UseActionDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    private delegate bool UseActionLocationDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        Vector3* location,
        uint extraParam,
        byte a7);

    private Hook<UseActionDelegate>? useActionHook;
    private bool hookEnabled;
    private Hook<UseActionLocationDelegate>? useActionLocationHook;
    private bool locationHookEnabled;

    // > 0 while the real UseAction runs for a call this hook let through: the UseActionLocation call the
    // game makes inside it has already been judged by the UseAction rule (only the fake-target check is
    // repeated). Framework-thread only, like the game's action calls.
    private int passThroughDepth;
    private uint lastSuppressedLocationAction;
    private long lastSuppressedLocationLogMs;

    /// <summary>True when the UseAction hook was created and is enabled.</summary>
    public bool IsUseActionHealthy => useActionHook != null && hookEnabled && !useActionHook.IsDisposed;

    /// <summary>True when the UseActionLocation hook was created and is enabled.</summary>
    public bool IsUseActionLocationHealthy =>
        useActionLocationHook != null && locationHookEnabled && !useActionLocationHook.IsDisposed;

    /// <summary>
    /// True if both action hooks were created successfully and are enabled.
    /// When false during active simulation, the sim must be emergency-stopped
    /// to prevent unintercepted actions from reaching the server.
    /// </summary>
    public bool IsHealthy => IsUseActionHealthy && IsUseActionLocationHealthy;
    public Func<bool>? SuppressExternalInput { get; set; }

    public UseActionHook(
        IGameInteropProvider gameInterop,
        CombatEngine combatEngine,
        IPluginLog log,
        CombatSimulator.ActionCombat.IPlayerActionSink actionSink)
    {
        this.combatEngine = combatEngine;
        this.log = log;
        this.actionSink = actionSink;

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

        try
        {
            useActionLocationHook = gameInterop.HookFromAddress<UseActionLocationDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseActionLocation,
                UseActionLocationDetour);

            log.Info("UseActionLocation hook created successfully.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create UseActionLocation hook. Direct action calls (ground-target confirm, other plugins) will not be intercepted.");
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

        if (useActionLocationHook != null && !locationHookEnabled)
        {
            useActionLocationHook.Enable();
            locationHookEnabled = true;
            log.Info("UseActionLocation hook enabled.");
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

        if (useActionLocationHook != null && locationHookEnabled)
        {
            useActionLocationHook.Disable();
            locationHookEnabled = false;
            log.Info("UseActionLocation hook disabled.");
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

            if (SuppressExternalInput?.Invoke() == true) return true;

            // Action combat: every hotbar press is remapped to a light attack, guard or skill input and never
            // reaches the server.
            log.Debug($"Action input: actionId={actionId}, targetId=0x{targetId:X}");
            actionSink.OnAction((uint)actionType, actionId, targetId, extraParam);
            return true;
        }
        catch (Exception ex)
        {
            // During a simulation a combat action is the simulation's: an exception on its way in must not
            // turn it into a real one. Swallow it. Everything else (outside a simulation, or items, general
            // actions and the like, which pass through in a simulation anyway) keeps passing through.
            if (IsSimulationAction(actionType))
            {
                log.Error(ex, $"Error in UseAction hook during the simulation; swallowed actionId={actionId} (not sent to the server).");
                return true;
            }

            log.Error(ex, "Error in UseAction hook, passing through to original.");
            return CallOriginalGuarded(actionManager, actionType, actionId,
                targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
    }

    /// <summary>A combat action while a simulation runs: it belongs to the simulation, never to the server.</summary>
    private bool IsSimulationAction(ActionType actionType)
    {
        try
        {
            return actionType == ActionType.Action && combatEngine.IsActive;
        }
        catch
        {
            // Cannot tell: treat it as the simulation's (swallowing a press is harmless, sending one is not).
            return actionType == ActionType.Action;
        }
    }

    /// <summary>
    /// Invoke the real UseAction — unless the target id belongs to a client-only simulated
    /// actor (>= <see cref="SimulatedEntityIdFloor"/>), in which case swallow the call
    /// instead of passing it through. Every pass-through path (sim inactive, non-Action
    /// type, and the exception fallback) routes through here so that a fake target id
    /// (a click on a simulated actor outside a battle, another plugin's call) can never
    /// leave the client as a real action packet.
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
        if (IsSimulatedTarget(targetId))
        {
            log.Warning($"Suppressed UseAction pass-through: actionId={actionId} targets simulated 0x{targetId:X} " +
                        "(would leak an action packet to the server).");
            return true;
        }

        passThroughDepth++;
        try
        {
            return useActionHook!.Original(actionManager, actionType, actionId,
                targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
        finally
        {
            passThroughDepth--;
        }
    }

    /// <summary>
    /// The game's "execute now". Same rule as UseAction: a simulated target never passes, in or out of a
    /// simulation; during a simulation a combat action that did not come through UseAction (the ground-target
    /// confirm, another plugin such as BossMod calling the game directly) is the simulation's and is
    /// dropped. The call UseAction makes inside a press it let through passes (that press was judged already).
    /// </summary>
    private bool UseActionLocationDetour(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        Vector3* location,
        uint extraParam,
        byte a7)
    {
        try
        {
            if (IsSimulatedTarget(targetId))
            {
                log.Warning($"Suppressed UseActionLocation: actionId={actionId} targets simulated 0x{targetId:X} " +
                            "(would leak an action packet to the server).");
                return false;
            }

            if (passThroughDepth == 0 && actionType == ActionType.Action && combatEngine.IsActive)
            {
                // A caller may retry every frame: one line per action per second.
                var now = Environment.TickCount64;
                if (actionId != lastSuppressedLocationAction || now - lastSuppressedLocationLogMs > 1000)
                {
                    lastSuppressedLocationAction = actionId;
                    lastSuppressedLocationLogMs = now;
                    log.Info($"Suppressed direct UseActionLocation during the simulation: actionId={actionId}, " +
                             $"targetId=0x{targetId:X} (did not come through UseAction).");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            if (passThroughDepth == 0 && IsSimulationAction(actionType))
            {
                log.Error(ex, $"Error in UseActionLocation hook during the simulation; swallowed actionId={actionId}.");
                return false;
            }
            log.Error(ex, "Error in UseActionLocation hook, passing through to original.");
        }

        return useActionLocationHook!.Original(actionManager, actionType, actionId,
            targetId, location, extraParam, a7);
    }

    public void Dispose()
    {
        Disable();
        useActionHook?.Dispose();
        useActionLocationHook?.Dispose();
    }
}
