using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Safety;

/// <summary>
/// Hooks GameObject.SetPosition and SetRotation to freeze the local player's
/// position when they are dead in the combat simulator.
/// Unlike input-based blocking, this prevents the position from ever changing,
/// so there is no shaking or fighting between the game engine and the plugin.
/// </summary>
public unsafe class MovementBlockHook : IDisposable
{
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private delegate void SetPositionDelegate(GameObject* thisPtr, float x, float y, float z);
    private delegate void SetRotationDelegate(GameObject* thisPtr, float value);

    private Hook<SetPositionDelegate>? setPositionHook;
    private Hook<SetRotationDelegate>? setRotationHook;

    /// <summary>
    /// When true, the local player's position and rotation are frozen.
    /// </summary>
    public bool IsBlocking { get; set; }

    /// <summary>
    /// Addresses of NPCs whose server-driven position updates should be blocked
    /// (we control their position directly for the target approach feature).
    /// </summary>
    private readonly HashSet<nint> approachBlockedAddresses = new();

    public void AddApproachNpc(nint address) => approachBlockedAddresses.Add(address);
    public void RemoveApproachNpc(nint address) => approachBlockedAddresses.Remove(address);
    public void ClearApproachNpcs() => approachBlockedAddresses.Clear();

    public MovementBlockHook(IGameInteropProvider gameInterop, IClientState clientState, IPluginLog log)
    {
        this.clientState = clientState;
        this.log = log;

        try
        {
            setPositionHook = gameInterop.HookFromAddress<SetPositionDelegate>(
                (nint)GameObject.MemberFunctionPointers.SetPosition,
                SetPositionDetour);

            setRotationHook = gameInterop.HookFromAddress<SetRotationDelegate>(
                (nint)GameObject.MemberFunctionPointers.SetRotation,
                SetRotationDetour);

            log.Info("MovementBlockHook: Position/rotation hooks created.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "MovementBlockHook: Failed to create hooks.");
        }
    }

    public void Enable()
    {
        setPositionHook?.Enable();
        setRotationHook?.Enable();
        log.Info("MovementBlockHook: Enabled.");
    }

    public void Disable()
    {
        setPositionHook?.Disable();
        setRotationHook?.Disable();
    }

    private void SetPositionDetour(GameObject* thisPtr, float x, float y, float z)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr))
            return; // Skip — player position stays frozen

        if (approachBlockedAddresses.Contains((nint)thisPtr))
            return; // Skip — we control this NPC's position for target approach

        setPositionHook!.Original(thisPtr, x, y, z);
    }

    private void SetRotationDetour(GameObject* thisPtr, float value)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr))
            return; // Skip — player rotation stays frozen

        if (approachBlockedAddresses.Contains((nint)thisPtr))
            return; // Skip — we control this NPC's rotation for target approach

        setRotationHook!.Original(thisPtr, value);
    }

    private bool IsLocalPlayer(GameObject* obj)
    {
        var player = clientState.LocalPlayer;
        return player != null && (nint)obj == player.Address;
    }

    public void Dispose()
    {
        IsBlocking = false;
        approachBlockedAddresses.Clear();
        setPositionHook?.Dispose();
        setRotationHook?.Dispose();
    }
}
