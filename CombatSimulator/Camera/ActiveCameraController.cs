using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// Active Camera: hooks getCameraPosition (vtable 15) to replace the camera's
/// orbit center with a bone world position. The user retains full camera rotation
/// and zoom control. Completely independent of DeathCamController.
/// </summary>
public unsafe class ActiveCameraController : IDisposable
{
    // Same bone list as DeathCamController for consistency
    public static readonly (string Label, string BoneName)[] CenterBones = DeathCamController.CenterBones;

    private readonly IClientState clientState;
    private readonly IGameInteropProvider gameInterop;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // getCameraPosition hook (vtable index 15) — replaces orbit center
    private delegate void GetCameraPositionDelegate(nint camera, nint target, Vector3* position, nint swapPerson);
    private Hook<GetCameraPositionDelegate>? getCameraPosHook;

    // Camera collision patch (same mechanism as DeathCamController, independent instance)
    private nint collisionPatchAddress;
    private byte[]? collisionOriginalBytes;
    private static readonly byte[] CollisionPatchBytes = { 0x30, 0xC0, 0x90, 0x90, 0x90 };
    private bool collisionPatchActive;

    // ShouldDrawGameObject hook — prevents model fade at close zoom
    private delegate bool ShouldDrawGameObjectDelegate(
        FFXIVClientStructs.FFXIV.Client.Game.CameraBase* thisPtr,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* gameObject,
        Vector3* sceneCameraPos, Vector3* lookAtVector);
    private Hook<ShouldDrawGameObjectDelegate>? shouldDrawHook;
    private bool shouldDrawHookActive;

    // Camera distance override — allow closer zoom when active
    private float savedMinDistance;
    private bool distanceOverridden;

    public bool IsActive { get; private set; }

    public ActiveCameraController(IGameInteropProvider gameInterop, IClientState clientState,
        ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.clientState = clientState;
        this.config = config;
        this.log = log;

        InitCollisionPatch(sigScanner);
        InitShouldDrawHook(gameInterop);
    }

    private void InitShouldDrawHook(IGameInteropProvider gameInterop)
    {
        try
        {
            var addr = (nint)FFXIVClientStructs.FFXIV.Client.Game.CameraBase.MemberFunctionPointers.ShouldDrawGameObject;
            if (addr != nint.Zero)
            {
                shouldDrawHook = gameInterop.HookFromAddress<ShouldDrawGameObjectDelegate>(
                    addr, ShouldDrawGameObjectDetour);
                log.Info($"ActiveCamera: ShouldDrawGameObject hook ready at 0x{addr:X}");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "ActiveCamera: Failed to create ShouldDrawGameObject hook");
        }
    }

    private bool ShouldDrawGameObjectDetour(
        FFXIVClientStructs.FFXIV.Client.Game.CameraBase* thisPtr,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* gameObject,
        Vector3* sceneCameraPos, Vector3* lookAtVector)
    {
        // Always return true — prevents character/NPC models from disappearing at close zoom
        return true;
    }

    private void InitCollisionPatch(ISigScanner sigScanner)
    {
        try
        {
            collisionPatchAddress = sigScanner.ScanModule("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01");
            if (collisionPatchAddress != nint.Zero)
            {
                collisionOriginalBytes = new byte[CollisionPatchBytes.Length];
                Marshal.Copy(collisionPatchAddress, collisionOriginalBytes, 0, collisionOriginalBytes.Length);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "ActiveCamera: Failed to find collision patch address.");
        }
    }

    public void SetActive(bool on)
    {
        IsActive = on;
        if (!on)
        {
            DisableCollisionPatch();
            RestoreMinDistance();
        }
        log.Info($"ActiveCamera: {(on ? "ON" : "OFF")}");
    }

    private void RestoreMinDistance()
    {
        if (!distanceOverridden) return;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
                camMgr->Camera->MinDistance = savedMinDistance;
        }
        catch { }
        distanceOverridden = false;
    }

    /// <summary>
    /// Lazily create the getCameraPosition hook once the camera is available.
    /// </summary>
    public void EnsureHook()
    {
        if (getCameraPosHook != null)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var camera = (nint)camMgr->Camera;
            var vtable = *(nint**)camera;
            var getCamPosAddr = vtable[15];

            getCameraPosHook = gameInterop.HookFromAddress<GetCameraPositionDelegate>(getCamPosAddr, GetCameraPositionDetour);
            getCameraPosHook.Enable();

            log.Info($"ActiveCamera: getCameraPosition hook at 0x{getCamPosAddr:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "ActiveCamera: Failed to create hook.");
        }
    }

    /// <summary>
    /// getCameraPosition detour: replaces the orbit center with the bone position + offsets.
    /// The game applies rotation/zoom on top, so the user retains full camera control.
    /// </summary>
    private void GetCameraPositionDetour(nint camera, nint target, Vector3* position, nint swapPerson)
    {
        getCameraPosHook!.Original(camera, target, position, swapPerson);

        if (!IsActive)
            return;

        try
        {
            var bonePos = GetBoneWorldPosition(config.ActiveCameraBoneName);
            if (bonePos == null) return;

            var pos = bonePos.Value;

            // Height offset
            pos.Y += config.ActiveCameraHeightOffset;

            // Side offset (perpendicular to camera horizontal direction)
            if (config.ActiveCameraSideOffset != 0)
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr != null && camMgr->Camera != null)
                {
                    float a = camMgr->Camera->DirH - MathF.PI / 2f;
                    pos.X += -config.ActiveCameraSideOffset * MathF.Sin(a);
                    pos.Z += -config.ActiveCameraSideOffset * MathF.Cos(a);
                }
            }

            *position = pos;
        }
        catch { }
    }

    /// <summary>
    /// Called each frame. Manages collision patch and vertical angle lock.
    /// </summary>
    public void Tick(float deltaTime)
    {
        EnsureHook();

        // Collision patch
        bool wantCollision = IsActive && config.ActiveCameraDisableCollision;
        if (wantCollision && !collisionPatchActive)
            EnableCollisionPatch();
        else if (!wantCollision && collisionPatchActive)
            DisableCollisionPatch();

        // ShouldDrawGameObject hook: enable when prevent fade is wanted
        bool wantPreventFade = IsActive && config.ActiveCameraPreventFade && config.ActiveCameraCloseZoom;
        if (wantPreventFade && !shouldDrawHookActive && shouldDrawHook != null)
        {
            shouldDrawHook.Enable();
            shouldDrawHookActive = true;
            log.Info("ActiveCamera: ShouldDrawGameObject hook enabled");
        }
        else if (!wantPreventFade && shouldDrawHookActive && shouldDrawHook != null)
        {
            shouldDrawHook.Disable();
            shouldDrawHookActive = false;
            log.Info("ActiveCamera: ShouldDrawGameObject hook disabled");
        }

        // Camera distance + vertical angle overrides
        if (IsActive)
        {
            try
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr != null && camMgr->Camera != null)
                {
                    var gameCam = camMgr->Camera;

                    // Allow closer zoom if enabled
                    if (config.ActiveCameraCloseZoom)
                    {
                        if (!distanceOverridden)
                        {
                            savedMinDistance = gameCam->MinDistance;
                            distanceOverridden = true;
                        }
                        gameCam->MinDistance = config.ActiveCameraMinZoomDistance;
                    }
                    else if (distanceOverridden)
                    {
                        gameCam->MinDistance = savedMinDistance;
                        distanceOverridden = false;
                    }

                    // Lock vertical angle if configured
                    if (config.ActiveCameraLockVertical)
                    {
                        gameCam->DirV = config.ActiveCameraVerticalAngle;
                        gameCam->InputDeltaV = 0;
                        gameCam->InputDeltaVAdjusted = 0;
                    }
                }
            }
            catch { }
        }
        else if (distanceOverridden)
        {
            // Restore original min distance
            try
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr != null && camMgr->Camera != null)
                    camMgr->Camera->MinDistance = savedMinDistance;
            }
            catch { }
            distanceOverridden = false;
        }
    }

    private Vector3? GetBoneWorldPosition(string boneName)
    {
        try
        {
            var player = clientState.LocalPlayer;
            if (player == null) return null;

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (gameObj == null) return null;

            var drawObject = gameObj->DrawObject;
            if (drawObject == null) return null;

            var characterBase = (CharacterBase*)drawObject;
            var skeleton = characterBase->Skeleton;
            if (skeleton == null || skeleton->PartialSkeletonCount == 0) return null;

            var partialSkeleton = &skeleton->PartialSkeletons[0];
            var pose = partialSkeleton->GetHavokPose(0);
            if (pose == null) return null;

            var havokSkeleton = pose->Skeleton;
            if (havokSkeleton == null) return null;

            int boneIndex = -1;
            for (int i = 0; i < havokSkeleton->Bones.Length; i++)
            {
                if (havokSkeleton->Bones[i].Name.String == boneName)
                {
                    boneIndex = i;
                    break;
                }
            }
            if (boneIndex < 0) return null;

            var modelPose = pose->GetSyncedPoseModelSpace();
            if (modelPose == null || modelPose->Length <= boneIndex) return null;

            var boneTransform = modelPose->Data[boneIndex];
            var boneModelPos = new Vector3(
                boneTransform.Translation.X,
                boneTransform.Translation.Y,
                boneTransform.Translation.Z);

            var skeletonPos = new Vector3(
                skeleton->Transform.Position.X,
                skeleton->Transform.Position.Y,
                skeleton->Transform.Position.Z);
            var skeletonRot = new Quaternion(
                skeleton->Transform.Rotation.X,
                skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z,
                skeleton->Transform.Rotation.W);

            return skeletonPos + Vector3.Transform(boneModelPos, skeletonRot);
        }
        catch
        {
            return null;
        }
    }

    private void EnableCollisionPatch()
    {
        if (collisionPatchActive || collisionPatchAddress == nint.Zero) return;
        WriteMemory(collisionPatchAddress, CollisionPatchBytes);
        collisionPatchActive = true;
    }

    private void DisableCollisionPatch()
    {
        if (!collisionPatchActive || collisionOriginalBytes == null) return;
        WriteMemory(collisionPatchAddress, collisionOriginalBytes);
        collisionPatchActive = false;
    }

    private static void WriteMemory(nint address, byte[] bytes)
    {
        VirtualProtect(address, (nuint)bytes.Length, 0x40, out uint oldProtect);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
    }

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    public void Dispose()
    {
        SetActive(false);
        RestoreMinDistance();
        shouldDrawHook?.Dispose();
        getCameraPosHook?.Dispose();
    }
}
