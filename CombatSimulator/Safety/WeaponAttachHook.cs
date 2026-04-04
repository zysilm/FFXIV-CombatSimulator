using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Safety;

/// <summary>
/// Hooks DrawObject.GetAttachBoneWorldTransform (vf17) to override weapon bone
/// transforms during ragdoll weapon drop. The Attach deformer calls this to position
/// weapon DrawObjects — without this hook, it reads from the frozen death animation
/// instead of our physics-driven bone positions.
/// </summary>
public unsafe class WeaponAttachHook : IDisposable
{
    private readonly IPluginLog log;

    private delegate Matrix4x4* GetAttachBoneWorldTransformDelegate(
        DrawObject* thisPtr, Matrix4x4* outTransform, int attachBoneIndex);
    private Hook<GetAttachBoneWorldTransformDelegate>? hook;

    /// <summary>Address of the hooked function (for diagnostics).</summary>
    public nint HookedAddress { get; private set; }

    /// <summary>
    /// When set, the hook overrides weapon bone transforms for this DrawObject.
    /// Key = attachBoneIndex, Value = world-space 4x4 transform matrix.
    /// </summary>
    public nint BlockedDrawObject { get; set; }
    private readonly System.Collections.Generic.Dictionary<int, Matrix4x4> overrideTransforms = new();

    public WeaponAttachHook(IGameInteropProvider gameInterop, IClientState clientState, IPluginLog log)
    {
        this.log = log;

        try
        {
            // Resolve vf17 from a live CharacterBase instance (player's DrawObject)
            var player = clientState.LocalPlayer;
            if (player == null)
            {
                log.Warning("WeaponAttachHook: No local player — deferring hook creation.");
                return;
            }

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (gameObj->DrawObject == null)
            {
                log.Warning("WeaponAttachHook: No DrawObject — deferring hook creation.");
                return;
            }

            // Read vtable pointer from the DrawObject, then read vf17 entry
            var vtablePtr = *(nint*)gameObj->DrawObject;
            var vf17Addr = *(nint*)(vtablePtr + 17 * 8); // 8 bytes per vtable entry (x64)
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
    /// Call each frame from the ragdoll's weapon bone write-back.
    /// </summary>
    public void SetOverride(int attachBoneIndex, Vector3 worldPos, Quaternion worldRot)
    {
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
        // Only override for the target DrawObject and if we have an override for this bone
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
