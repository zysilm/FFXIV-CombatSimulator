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

    public bool IsActive { get; private set; }

    // --- Fighting Camera (1v1) ---
    private enum FightingCamState { Off, Fighting, Transitioning, Following }
    private FightingCamState fightingState = FightingCamState.Off;

    // Provides the locked 1v1 target's character address (set by Plugin); null when none.
    public Func<nint?>? GetFightingTargetAddress;

    // Smoothed orbit center + auto-zoom distance, computed in Tick, consumed by the hook.
    private Vector3 fightingCenter;
    private float fightingDistance;
    private bool fightingHasState;
    private nint framedTargetAddress;

    // Death transition (fighting → dead character's bone follow)
    private float transitionElapsed;
    private Vector3 transitionStartCenter;
    private float transitionStartDistance;
    private nint deadCharacterAddress;

    // MaxDistance limit override so auto-zoom isn't clamped by the game (save/restore)
    private float savedMaxDistanceFighting;
    private bool fightingMaxDistOverridden;

    /// <summary>True while the fighting camera owns the camera (any non-Off state).</summary>
    public bool IsFightingEngaged => fightingState != FightingCamState.Off;

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
        IsActive = on;
        if (!on)
        {
            EndFighting();
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
            // Fighting camera owns the orbit center while engaged — Tick computes the
            // fully-offset, smoothed center (midpoint while fighting, dead bone afterward).
            if (IsFightingEngaged && fightingHasState)
            {
                *position = ApplyActiveCameraSideOffset(fightingCenter);
                return;
            }

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

        // Fighting camera state machine (computes center + auto-zoom; writes Distance)
        UpdateFightingCamera(deltaTime);

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
        if (IsActive)
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

    /// <summary>
    /// Fighting camera state machine. Run each frame from Tick. Computes the smoothed orbit
    /// center (cached for the getCameraPosition hook) and writes the auto-zoom Distance.
    /// </summary>
    private void UpdateFightingCamera(float dt)
    {
        // Disengage if active cam off or fighting mode disabled
        if (!IsActive || !config.ActiveCameraFightingMode)
        {
            if (fightingState != FightingCamState.Off) EndFighting();
            return;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null) return;
        var gameCam = camMgr->Camera;

        var playerAddr = LocalPlayerAddress();
        var targetAddr = GetFightingTargetAddress?.Invoke() ?? nint.Zero;

        switch (fightingState)
        {
            case FightingCamState.Off:
            {
                // Engage only with a valid, readable, live 1v1 pair
                if (playerAddr == nint.Zero || targetAddr == nint.Zero) break;
                var a = GetBoneWorldPosition(config.ActiveCameraFightingBoneName, playerAddr);
                var b = GetBoneWorldPosition(config.ActiveCameraFightingBoneName, targetAddr);
                if (a == null || b == null) break;
                framedTargetAddress = targetAddr;
                ComputeFraming(a.Value, b.Value, gameCam, out var c0, out var d0);
                fightingCenter = c0;
                fightingDistance = d0;
                fightingHasState = true;
                fightingState = FightingCamState.Fighting;
                ApplyFightingDistance(gameCam, fightingDistance);
                log.Info("FightingCam: engaged (1v1 framing).");
                break;
            }

            case FightingCamState.Fighting:
            {
                if (playerAddr == nint.Zero || targetAddr == nint.Zero) { EndFighting(); break; }
                framedTargetAddress = targetAddr;
                var a = GetBoneWorldPosition(config.ActiveCameraFightingBoneName, playerAddr);
                var b = GetBoneWorldPosition(config.ActiveCameraFightingBoneName, targetAddr);
                if (a == null || b == null) break;
                ComputeFraming(a.Value, b.Value, gameCam, out var ct, out var dt2);
                SmoothToward(ref fightingCenter, ct, dt);
                fightingDistance = SmoothScalar(fightingDistance, dt2, dt);
                ApplyFightingDistance(gameCam, fightingDistance);
                break;
            }

            case FightingCamState.Transitioning:
            {
                transitionElapsed += dt;
                float dur = MathF.Max(config.ActiveCameraFightingTransitionDuration, 0.01f);
                float t = Math.Clamp(transitionElapsed / dur, 0f, 1f);
                float s = SmoothStep(t);

                var deadBone = GetBoneWorldPosition(config.ActiveCameraBoneName, deadCharacterAddress);
                var targetCenter = deadBone ?? transitionStartCenter;
                targetCenter.Y += config.ActiveCameraHeightOffset;
                // Zoom all the way in to the active-cam min distance so we get the close-up on the
                // corpse — that's the whole point of active cam. The user can zoom out afterward.
                float targetDist = config.ActiveCameraMinZoomDistance;

                fightingCenter = Vector3.Lerp(transitionStartCenter, targetCenter, s);
                fightingDistance = Lerp(transitionStartDistance, targetDist, s);
                ApplyFightingDistance(gameCam, fightingDistance);

                if (t >= 1f)
                {
                    fightingState = FightingCamState.Following;
                    log.Info("FightingCam: transition complete — following corpse bone.");
                }
                break;
            }

            case FightingCamState.Following:
            {
                var deadBone = GetBoneWorldPosition(config.ActiveCameraBoneName, deadCharacterAddress);
                if (deadBone == null) { EndFighting(); break; } // corpse despawned (combat reset)
                var c = deadBone.Value;
                c.Y += config.ActiveCameraHeightOffset;
                SmoothToward(ref fightingCenter, c, dt);
                // Hand zoom back to the user once settled on the corpse.
                RestoreFightingMaxDistance(gameCam);
                break;
            }
        }
    }

    /// <summary>Compute midpoint center + auto-zoom distance so both subjects stay framed.</summary>
    private void ComputeFraming(Vector3 a, Vector3 b, FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        out Vector3 center, out float distance)
    {
        center = (a + b) * 0.5f;
        center.Y += config.ActiveCameraFightingHeightOffset;

        float sep = (a - b).Length();
        float vFov = gameCam->FoV;
        if (vFov < 0.01f) vFov = 1.0f;
        float aspect = GetViewportAspect();
        float hHalf = MathF.Atan(aspect * MathF.Tan(vFov * 0.5f));
        float margin = MathF.Max(1.0f, config.ActiveCameraFightingZoomMargin);

        // Worst-case horizontal fit (pair spread across the wider screen axis)
        float dH = hHalf > 0.001f ? (sep * 0.5f) / MathF.Tan(hHalf) * margin : config.ActiveCameraFightingMinDistance;
        // Vertical fit for height differences
        float vSep = MathF.Abs(a.Y - b.Y);
        float vHalf = vFov * 0.5f;
        float dV = vHalf > 0.001f ? (vSep * 0.5f) / MathF.Tan(vHalf) * margin : 0f;

        distance = Math.Clamp(MathF.Max(dH, dV),
            config.ActiveCameraFightingMinDistance, config.ActiveCameraFightingMaxDistance);
    }

    private static float GetViewportAspect()
    {
        try
        {
            var dev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (dev != null && dev->Height > 0)
                return (float)dev->Width / dev->Height;
        }
        catch { }
        return 16f / 9f;
    }

    private void ApplyFightingDistance(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float d)
    {
        if (!fightingMaxDistOverridden)
        {
            savedMaxDistanceFighting = gameCam->MaxDistance;
            fightingMaxDistOverridden = true;
        }
        gameCam->MaxDistance = MathF.Max(savedMaxDistanceFighting, config.ActiveCameraFightingMaxDistance + 1f);
        gameCam->Distance = d;
        gameCam->InterpDistance = d;
    }

    private void RestoreFightingMaxDistance(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam)
    {
        if (!fightingMaxDistOverridden) return;
        gameCam->MaxDistance = savedMaxDistanceFighting;
        fightingMaxDistOverridden = false;
    }

    private void SmoothToward(ref Vector3 cur, Vector3 target, float dt)
    {
        float k = 1f - MathF.Exp(-MathF.Max(0.1f, config.ActiveCameraFightingSmoothing) * dt);
        cur = Vector3.Lerp(cur, target, k);
    }

    private float SmoothScalar(float cur, float target, float dt)
    {
        float k = 1f - MathF.Exp(-MathF.Max(0.1f, config.ActiveCameraFightingSmoothing) * dt);
        return cur + (target - cur) * k;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    private static nint LocalPlayerAddress()
    {
        var p = Core.Services.ObjectTable.LocalPlayer;
        return p?.Address ?? nint.Zero;
    }

    /// <summary>
    /// Notify that a combatant died. If the fighting camera is framing this pair, begin the
    /// smooth transition to the dead character's active-cam bone. Called from Plugin death hooks.
    /// </summary>
    public void NotifyCombatantDeath(nint address, bool isPlayer)
    {
        if (fightingState != FightingCamState.Fighting) return;
        var playerAddr = LocalPlayerAddress();
        bool relevant = (isPlayer && address == playerAddr) || (!isPlayer && address == framedTargetAddress);
        if (!relevant) return;

        transitionStartCenter = fightingCenter;
        transitionStartDistance = fightingDistance;
        deadCharacterAddress = address;
        transitionElapsed = 0f;
        fightingState = FightingCamState.Transitioning;
        log.Info($"FightingCam: combatant died (isPlayer={isPlayer}) — transitioning to corpse bone.");
    }

    /// <summary>
    /// Force the fighting camera back to a clean Off state. Call on simulation reset/stop so a
    /// post-death Following state (which otherwise lingers until the corpse bone becomes
    /// unreadable — never, if the dead character revives at the same address) is cleared and the
    /// next 1v1 can re-engage.
    /// </summary>
    public void ResetFightingCamera() => EndFighting();

    private void EndFighting()
    {
        if (fightingState == FightingCamState.Off && !fightingHasState) return;
        fightingState = FightingCamState.Off;
        fightingHasState = false;
        deadCharacterAddress = nint.Zero;
        framedTargetAddress = nint.Zero;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
                RestoreFightingMaxDistance(camMgr->Camera);
        }
        catch { }
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
        EndFighting();
        SetActive(false);
        RestoreMinDistance();
        shouldDrawHook?.Dispose();
        getCameraPosHook?.Dispose();
    }
}
