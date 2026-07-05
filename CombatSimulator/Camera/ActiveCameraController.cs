using System;
using System.Collections.Generic;
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
    private readonly ISigScanner sigScanner;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // getCameraPosition hook (resolved by sig-anchoring against setCameraLookAt) — replaces orbit center
    private delegate void GetCameraPositionDelegate(nint camera, nint target, Vector3* position, nint swapPerson);
    private Hook<GetCameraPositionDelegate>? getCameraPosHook;
    // Hypostasis-known signature for Camera::SetCameraLookAt — getCameraPosition lives at vtable[setLookAtIdx + 1]
    private const string SetCameraLookAtSig = "40 53 48 83 EC 30 44 8B 89 ?? ?? ?? ?? 48 8B DA";

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
    private readonly Dictionary<(nint Address, string BoneName), int> boneIndexCache = new();

    private bool userActive;
    private bool modeActive;
    private bool effectiveActive;

    public bool IsActive => effectiveActive;
    public bool IsUserActive => userActive;

    /// <summary>Resolved orbit center from the CameraModeCoordinator (highest-priority
    /// live request that supplied one). When it has a value it replaces the orbit
    /// center wholesale; when null the user's configured bone follow applies.</summary>
    public Func<Vector3?>? CoordinatorOrbitCenter;

    /// <summary>Current camera write authority, from the CameraModeCoordinator. The
    /// min-distance/vertical-lock overrides only apply when an active-camera
    /// personality (user bone follow or monster follow) is in charge — not when the
    /// fighting cameras drive angles and zoom themselves.</summary>
    public Func<CameraOwner>? GetCurrentOwner;

    // Death transition (fighting → dead character's bone follow)
    public ActiveCameraController(IGameInteropProvider gameInterop, IClientState clientState,
        ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.clientState = clientState;
        this.sigScanner = sigScanner;
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
        userActive = on;
        UpdateEffectiveActive();
    }

    public void SetModeActive(bool on)
    {
        modeActive = on;
        UpdateEffectiveActive();
    }

    private void UpdateEffectiveActive()
    {
        var on = userActive || modeActive;
        if (effectiveActive == on)
            return;

        effectiveActive = on;
        if (!effectiveActive)
        {
            DisableCollisionPatch();
            RestoreMinDistance();
        }
        log.Info($"ActiveCamera: {(effectiveActive ? "ON" : "OFF")} (user={userActive}, mode={modeActive})");
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
    /// Resolution is sig-anchored, not by hardcoded vtable index — SE shifts the
    /// Camera vtable on game patches (most recently 7.5 inserted a new virtual
    /// before the orbit fns, pushing getCameraPosition from vtable[15] to [16]).
    /// We sig-scan setCameraLookAt's body to locate it in the vtable, then take
    /// the next slot.
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

            var setLookAtAddr = sigScanner.ScanText(SetCameraLookAtSig);
            if (setLookAtAddr == nint.Zero)
            {
                log.Error("ActiveCamera: setCameraLookAt sig not found — cannot resolve getCameraPosition.");
                return;
            }

            var camera = (nint)camMgr->Camera;
            var vtable = *(nint**)camera;

            int setLookAtIdx = -1;
            for (int i = 0; i < 64; i++)
            {
                if (vtable[i] == setLookAtAddr)
                {
                    setLookAtIdx = i;
                    break;
                }
            }
            if (setLookAtIdx < 0)
            {
                log.Error($"ActiveCamera: setCameraLookAt (0x{setLookAtAddr:X}) not found in camera vtable.");
                return;
            }

            var getCamPosAddr = vtable[setLookAtIdx + 1];

            getCameraPosHook = gameInterop.HookFromAddress<GetCameraPositionDelegate>(getCamPosAddr, GetCameraPositionDetour);
            getCameraPosHook.Enable();

            log.Info($"ActiveCamera: setCameraLookAt resolved at vtable[{setLookAtIdx}] → getCameraPosition hook at 0x{getCamPosAddr:X}");
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
            // A mode-supplied center (fighting cameras, monster follow) replaces the
            // orbit center wholesale — priority was already resolved by the coordinator.
            var modeCenter = CoordinatorOrbitCenter?.Invoke();
            if (modeCenter.HasValue)
            {
                *position = modeCenter.Value;
                return;
            }

            // Default: the user's configured bone on the local player.
            var bonePos = GetBoneWorldPosition(config.ActiveCameraBoneName);
            if (bonePos == null) return;

            var pos = bonePos.Value;

            // Height offset
            pos.Y += config.ActiveCameraHeightOffset;
            pos = ApplyActiveCameraSideOffset(pos);

            *position = pos;
        }
        catch { }
    }

    private Vector3 ApplyActiveCameraSideOffset(Vector3 pos)
    {
        // Side offset is view-relative, so apply it in the camera hook rather than
        // baking it into smoothed fighting-camera centers computed in Tick.
        if (config.ActiveCameraSideOffset == 0)
            return pos;

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return pos;

        float a = camMgr->Camera->DirH - MathF.PI / 2f;
        pos.X += -config.ActiveCameraSideOffset * MathF.Sin(a);
        pos.Z += -config.ActiveCameraSideOffset * MathF.Cos(a);
        return pos;
    }

    /// <summary>
    /// Called each frame. Manages collision patch and vertical angle lock.
    /// </summary>
    public void Tick(float deltaTime)
    {
        EnsureHook();
        // The fighting cameras write angles/zoom themselves; don't stack the active-cam
        // min-distance/vertical-lock overrides on top of them.
        var owner = GetCurrentOwner?.Invoke() ?? CameraOwner.None;
        var fightingOwnsCamera = owner is CameraOwner.Fighting2D or CameraOwner.FightingKO;

        // Collision patch
        bool wantCollision = IsActive && config.ActiveCameraDisableCollision;
        if (wantCollision && !collisionPatchActive)
            EnableCollisionPatch();
        else if (!wantCollision && collisionPatchActive)
            DisableCollisionPatch();

        // ShouldDrawGameObject hook: enable when prevent fade is wanted
        bool wantPreventFade = IsActive && config.ActiveCameraPreventFade;
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
        if (IsActive && !fightingOwnsCamera)
        {
            try
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr != null && camMgr->Camera != null)
                {
                    var gameCam = camMgr->Camera;

                    // Apply configured min zoom distance
                    if (!distanceOverridden)
                    {
                        savedMinDistance = gameCam->MinDistance;
                        distanceOverridden = true;
                    }
                    gameCam->MinDistance = config.ActiveCameraMinZoomDistance;

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
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return null;
        return GetBoneWorldPosition(boneName, player.Address);
    }

    private Vector3? GetBoneWorldPosition(string boneName, nint characterAddress)
    {
        try
        {
            if (characterAddress == nint.Zero) return null;

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)characterAddress;
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

            var cacheKey = (characterAddress, boneName);
            if (!boneIndexCache.TryGetValue(cacheKey, out var boneIndex) ||
                boneIndex < 0 ||
                boneIndex >= havokSkeleton->Bones.Length ||
                havokSkeleton->Bones[boneIndex].Name.String != boneName)
            {
                boneIndex = -1;
                for (int i = 0; i < havokSkeleton->Bones.Length; i++)
                {
                    if (havokSkeleton->Bones[i].Name.String == boneName)
                    {
                        boneIndex = i;
                        boneIndexCache[cacheKey] = i;
                        break;
                    }
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
        userActive = false;
        modeActive = false;
        UpdateEffectiveActive();
        RestoreMinDistance();
        shouldDrawHook?.Dispose();
        getCameraPosHook?.Dispose();
    }
}
