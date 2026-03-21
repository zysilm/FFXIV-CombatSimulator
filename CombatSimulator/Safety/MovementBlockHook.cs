using System;
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
            return; // Skip — position stays frozen

        setPositionHook!.Original(thisPtr, x, y, z);
    }

    private void SetRotationDetour(GameObject* thisPtr, float value)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr))
            return; // Skip — rotation stays frozen

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
        setPositionHook?.Dispose();
        setRotationHook?.Dispose();
    }
}
