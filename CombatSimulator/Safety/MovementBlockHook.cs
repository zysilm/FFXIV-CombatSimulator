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
    /// (we control their position via SetApproachPosition/Rotation instead).
    /// </summary>
    private readonly HashSet<nint> approachBlockedAddresses = new();
    private readonly Dictionary<nint, HashSet<object>> approachBlockOwners = new();

    /// <summary>
    /// When true, the next SetPosition/SetRotation call is from our approach
    /// logic and should be allowed through (not blocked by the approach filter).
    /// </summary>
    private bool allowApproachUpdate;

    public void AddApproachNpc(nint address) => approachBlockedAddresses.Add(address);
    public void RemoveApproachNpc(nint address) => approachBlockedAddresses.Remove(address);
    public void AddApproachNpc(nint address, object owner)
    {
        if (!approachBlockOwners.TryGetValue(address, out var owners))
        {
            owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
            approachBlockOwners[address] = owners;
        }
        owners.Add(owner);
    }
    public void RemoveApproachNpc(nint address, object owner)
    {
        if (!approachBlockOwners.TryGetValue(address, out var owners))
            return;
        owners.Remove(owner);
        if (owners.Count == 0)
            approachBlockOwners.Remove(address);
    }
    public void ClearApproachNpcs()
    {
        approachBlockedAddresses.Clear();
        // Owner-scoped registrations have independent lifetimes (for example spectators
        // survive a combat stop). Their owners remove them explicitly on actor destruction.
    }
    public void ClearApproachNpcsExcept(Func<nint, bool> keep)
    {
        approachBlockedAddresses.RemoveWhere(address => !keep(address));
        // Owner-scoped registrations are removed by their owner. This method exists for the
        // legacy AI set and must not erase independent controllers that share the hook.
    }

    /// <summary>
    /// Move an approach-controlled NPC by calling the real SetPosition,
    /// bypassing our own block. This properly updates the DrawObject (3D model).
    /// </summary>
    public void SetApproachPosition(GameObject* obj, float x, float y, float z)
    {
        if (setPositionHook == null) return;
        allowApproachUpdate = true;
        try
        {
            setPositionHook.Original(obj, x, y, z);
        }
        finally
        {
            allowApproachUpdate = false;
        }
    }

    /// <summary>
    /// Rotate an approach-controlled NPC by calling the real SetRotation,
    /// bypassing our own block.
    /// </summary>
    public void SetApproachRotation(GameObject* obj, float value)
    {
        if (setRotationHook == null) return;
        allowApproachUpdate = true;
        try
        {
            setRotationHook.Original(obj, value);
        }
        finally
        {
            allowApproachUpdate = false;
        }
    }

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

        if (!allowApproachUpdate && IsApproachBlocked((nint)thisPtr))
            return; // Skip — server update blocked; we move this NPC via SetApproachPosition

        setPositionHook!.Original(thisPtr, x, y, z);
    }

    private void SetRotationDetour(GameObject* thisPtr, float value)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr))
            return; // Skip — player rotation stays frozen

        if (!allowApproachUpdate && IsApproachBlocked((nint)thisPtr))
            return; // Skip — server update blocked; we rotate this NPC via SetApproachRotation

        setRotationHook!.Original(thisPtr, value);
    }

    private bool IsLocalPlayer(GameObject* obj)
    {
        var player = CombatSimulator.Core.Services.ObjectTable.LocalPlayer;
        return player != null && (nint)obj == player.Address;
    }

    private bool IsApproachBlocked(nint address)
        => approachBlockedAddresses.Contains(address) || approachBlockOwners.ContainsKey(address);

    public void Dispose()
    {
        IsBlocking = false;
        approachBlockedAddresses.Clear();
        approachBlockOwners.Clear();
        setPositionHook?.Dispose();
        setRotationHook?.Dispose();
    }
}
