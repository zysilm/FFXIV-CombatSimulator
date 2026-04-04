using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Safety;

/// <summary>
/// Hooks DrawObject.GetAttachBoneWorldTransform (vf17) to override weapon bone
/// transforms during ragdoll weapon drop. The Attach deformer calls this to position
/// weapon DrawObjects — without this hook, it reads from the frozen death animation
/// instead of our physics-driven bone positions.
///
/// Hook creation is deferred to first use (EnsureHook) because the player's DrawObject
/// is needed to resolve the virtual function address, and it's not available at plugin
/// construction time (not on main thread).
/// </summary>
public unsafe class WeaponAttachHook : IDisposable
{
    private readonly IGameInteropProvider gameInterop;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private delegate Matrix4x4* GetAttachBoneWorldTransformDelegate(
        DrawObject* thisPtr, Matrix4x4* outTransform, int attachBoneIndex);
    private Hook<GetAttachBoneWorldTransformDelegate>? hook;
    private bool hookAttempted;

    /// <summary>Address of the hooked function (for diagnostics). 0 until hook is created.</summary>
    public nint HookedAddress { get; private set; }

    /// <summary>
    /// When set, the hook overrides weapon bone transforms for this DrawObject.
    /// </summary>
    public nint BlockedDrawObject { get; set; }
    private readonly Dictionary<int, Matrix4x4> overrideTransforms = new();

    public WeaponAttachHook(IGameInteropProvider gameInterop, IClientState clientState, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.clientState = clientState;
        this.log = log;
    }

    /// <summary>
    /// Lazily create the hook on first call. Must be called from the main thread
    /// (e.g., from framework update or ragdoll activation).
    /// </summary>
    public void EnsureHook()
    {
        if (hook != null || hookAttempted) return;
        hookAttempted = true;

        try
        {
            var player = clientState.LocalPlayer;
            if (player == null) return;

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (gameObj->DrawObject == null) return;

            var vtablePtr = *(nint*)gameObj->DrawObject;
            var vf17Addr = *(nint*)(vtablePtr + 17 * 8);
            HookedAddress = vf17Addr;

            hook = gameInterop.HookFromAddress<GetAttachBoneWorldTransformDelegate>(
                vf17Addr, GetAttachBoneWorldTransformDetour);
            hook.Enable();

            log.Info($"WeaponAttachHook: Hooked GetAttachBoneWorldTransform (vf17) at 0x{vf17Addr:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "WeaponAttachHook: Failed to create hook.");
        }
    }

    /// <summary>
    /// Set override transform for a weapon attachment bone index.
    /// </summary>
    public void SetOverride(int attachBoneIndex, Vector3 worldPos, Quaternion worldRot)
    {
        EnsureHook();
        var mat = Matrix4x4.CreateFromQuaternion(worldRot);
        mat.M41 = worldPos.X;
        mat.M42 = worldPos.Y;
        mat.M43 = worldPos.Z;
        overrideTransforms[attachBoneIndex] = mat;
    }

    /// <summary>Clear all overrides (called on ragdoll deactivate).</summary>
    public void ClearOverrides()
    {
        BlockedDrawObject = nint.Zero;
        overrideTransforms.Clear();
    }

    private Matrix4x4* GetAttachBoneWorldTransformDetour(
        DrawObject* thisPtr, Matrix4x4* outTransform, int attachBoneIndex)
    {
        if (BlockedDrawObject != nint.Zero &&
            (nint)thisPtr == BlockedDrawObject &&
            overrideTransforms.TryGetValue(attachBoneIndex, out var overrideMat))
        {
            *outTransform = overrideMat;
            return outTransform;
        }

        return hook!.Original(thisPtr, outTransform, attachBoneIndex);
    }

    public void Dispose()
    {
        ClearOverrides();
        hook?.Dispose();
    }
}
