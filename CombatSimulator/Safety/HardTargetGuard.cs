// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Safety;

/// <summary>
/// The hard-target barrier. The game's hard target is synced to the server, so a client-only simulated actor
/// (entity id at/above <see cref="SimulatedEntityIdFloor"/>) must never become it: <c>TargetSystem.SetHardTarget</c>
/// is hooked and a request for one of ours is refused (no native target, no packet). The simulation does not
/// take its target from the game's target (action combat aims with the reticle and the facing cone), so the
/// click is simply dropped. Anything that puts one of ours in a target slot by another route is cleared by
/// <see cref="FakeActorGuard"/>.
/// </summary>
public sealed unsafe class HardTargetGuard : IDisposable
{
    private readonly IPluginLog log;

    // The two flag params are declared as byte (not bool) to sidestep 1-byte-vs-4-byte
    // bool marshalling ambiguity; we never read them, only pass them straight through.
    private delegate bool SetHardTargetDelegate(TargetSystem* thisPtr, GameObject* obj, byte ignoreTargetModes, byte a4, int a5);
    private Hook<SetHardTargetDelegate>? hardTargetHook;

    // Client-only simulated actors (spawned NPCs, companions, sensed map enemies we
    // re-id) are assigned entity ids at/above this floor. Mirror of NpcSelector.SimulatedEntityIdFloor.
    private const uint SimulatedEntityIdFloor = 0xF0000000;

    public HardTargetGuard(IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.log = log;

        try
        {
            hardTargetHook = gameInterop.HookFromAddress<SetHardTargetDelegate>(
                (nint)TargetSystem.MemberFunctionPointers.SetHardTarget,
                SetHardTargetDetour);
            hardTargetHook.Enable();
            log.Info("SetHardTarget guard hook created and enabled.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to hook SetHardTarget — simulated actors could be hard-targeted and leak to the server.");
        }
    }

    /// <summary>
    /// True when the SetHardTarget guard hook is installed. When false, a mouse click on a
    /// simulated actor could set it as the native hard target and sync a fake entity id to
    /// the server — surfaced in the Diagnose tab.
    /// </summary>
    public bool IsHealthy => hardTargetHook is { IsDisposed: false };

    private bool SetHardTargetDetour(TargetSystem* thisPtr, GameObject* obj, byte ignoreTargetModes, byte a4, int a5)
    {
        try
        {
            if (obj != null && obj->EntityId >= SimulatedEntityIdFloor)
            {
                log.Debug($"Suppressed native hard-target of simulated actor 0x{obj->EntityId:X} (no server sync).");
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in SetHardTarget detour.");
        }

        return hardTargetHook!.Original(thisPtr, obj, ignoreTargetModes, a4, a5);
    }

    public void Dispose()
    {
        try
        {
            hardTargetHook?.Disable();
            hardTargetHook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error disposing SetHardTarget guard hook.");
        }
        hardTargetHook = null;
    }
}
