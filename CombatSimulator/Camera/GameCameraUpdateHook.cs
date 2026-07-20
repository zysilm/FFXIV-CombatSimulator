using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// Hosts camera-wide layers that must run inside CameraBase.Update. This is deliberately
/// independent of every camera mode: Dynamic Camera uses the pre-update callback to pin
/// its death composition, while hit feedback is applied after the game's final camera pose.
/// </summary>
public unsafe sealed class GameCameraUpdateHook : IDisposable
{
    private delegate void CameraUpdateDelegate(CameraBase* thisPtr);

    public unsafe delegate void PreCameraUpdateDelegate(
        FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCamera);

    private readonly IGameInteropProvider gameInterop;
    private readonly IPluginLog log;
    private Hook<CameraUpdateDelegate>? updateHook;

    public PreCameraUpdateDelegate? PreCameraUpdate { get; set; }
    public Func<Vector3>? HitShakeProvider { get; set; }

    public GameCameraUpdateHook(IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.log = log;
    }

    /// <summary>Lazily installs the vtable hook once the main game camera exists.</summary>
    public void Tick()
    {
        if (updateHook != null)
            return;

        try
        {
            var cameraManager = GameCameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera == null)
                return;

            var camera = (CameraBase*)cameraManager->Camera;
            var vtable = *(nint**)camera;
            var updateAddress = vtable[3];

            updateHook = gameInterop.HookFromAddress<CameraUpdateDelegate>(updateAddress, CameraUpdateDetour);
            updateHook.Enable();
            log.Info($"Camera update hook created at 0x{updateAddress:X}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create camera update hook.");
        }
    }

    private void CameraUpdateDetour(CameraBase* thisPtr)
    {
        var isMainCamera = false;
        try
        {
            var cameraManager = GameCameraManager.Instance();
            isMainCamera = cameraManager != null && (nint)thisPtr == (nint)cameraManager->Camera;
        }
        catch
        {
            // A transient camera-manager read must never prevent the original update.
        }

        if (isMainCamera && PreCameraUpdate != null)
        {
            try
            {
                PreCameraUpdate((FFXIVClientStructs.FFXIV.Client.Game.Camera*)thisPtr);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Pre-camera update callback failed.");
            }
        }

        updateHook!.Original(thisPtr);

        if (isMainCamera)
            ApplyHitShake(thisPtr);
    }

    private void ApplyHitShake(CameraBase* camera)
    {
        var offset = HitShakeProvider?.Invoke() ?? Vector3.Zero;
        if (offset.LengthSquared() < 0.0000001f)
            return;

        try
        {
            var sceneCamera = &camera->SceneCamera;
            var position = sceneCamera->Position;
            sceneCamera->Position = new Vector3(
                position.X + offset.X,
                position.Y + offset.Y,
                position.Z + offset.Z);

            if (sceneCamera->RenderCamera != null)
            {
                var origin = sceneCamera->RenderCamera->Origin;
                sceneCamera->RenderCamera->Origin = new Vector3(
                    origin.X + offset.X,
                    origin.Y + offset.Y,
                    origin.Z + offset.Z);
                ApplyOffsetToViewMatrix(ref sceneCamera->RenderCamera->ViewMatrix, offset);
            }

            ApplyOffsetToViewMatrix(ref sceneCamera->ViewMatrix, offset);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error applying camera hit shake.");
        }
    }

    private static void ApplyOffsetToViewMatrix(
        ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 viewMatrix,
        Vector3 offset)
    {
        viewMatrix.M41 -= viewMatrix.M11 * offset.X + viewMatrix.M12 * offset.Y + viewMatrix.M13 * offset.Z;
        viewMatrix.M42 -= viewMatrix.M21 * offset.X + viewMatrix.M22 * offset.Y + viewMatrix.M23 * offset.Z;
        viewMatrix.M43 -= viewMatrix.M31 * offset.X + viewMatrix.M32 * offset.Y + viewMatrix.M33 * offset.Z;
    }

    public void Dispose() => updateHook?.Dispose();
}
