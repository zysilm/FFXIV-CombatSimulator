using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

public enum DeathCamState
{
    Inactive,
    Interpolating,
    Following,
}

public unsafe class DeathCamController : IDisposable
{
    public static readonly (string Name, int Index)[] CenterBones =
    {
        ("n_hara (Waist)", 1),
        ("j_kosi (Hips)", 2),
        ("j_sebo_a (Lower Spine)", 3),
        ("j_sebo_b (Mid Spine)", 4),
        ("j_sebo_c (Upper Spine)", 5),
        ("j_kubi (Neck)", 8),
        ("j_kao (Head)", 9),
        ("j_mune_l (Left Chest)", 6),
        ("j_mune_r (Right Chest)", 7),
    };

    private readonly IClientState clientState;
    private readonly IGameInteropProvider gameInterop;
    private readonly ISigScanner sigScanner;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private DeathCamState state = DeathCamState.Inactive;
    private float interpElapsed;

    // Interpolation start values (captured at activation)
    private float startDirH;
    private float startDirV;
    private float startDistance;
    private float startFoV;
    private Vector3 startLookAt;

    // Saved original camera limits — restored on deactivation
    private float savedMinDistance;
    private float savedMaxDistance;
    private float savedDirVMin;
    private float savedDirVMax;
    private float savedMinFoV;
    private float savedMaxFoV;
    private bool limitsOverridden;

    // Camera collision assembly patch (same approach as Cammy)
    // Patches the CALL to collision raycast with XOR AL,AL; NOP; NOP; NOP
    // This makes the game think no collision occurred, skipping distance adjustment entirely
    private nint collisionPatchAddress;
    private byte[]? collisionOriginalBytes;
    private static readonly byte[] CollisionPatchBytes = { 0x30, 0xC0, 0x90, 0x90, 0x90 }; // xor al, al; nop; nop; nop
    private bool collisionPatchActive;

    // Camera update hook — intercepts camera computation to apply offsets
    private delegate void CameraUpdateDelegate(CameraBase* thisPtr);
    private Hook<CameraUpdateDelegate>? cameraUpdateHook;

    public DeathCamState State => state;
    public bool IsPreviewActive { get; private set; }
    public bool IsActiveCamMode { get; private set; }
    public bool CollisionPatchAvailable => collisionPatchAddress != nint.Zero;

    public DeathCamController(IGameInteropProvider gameInterop, IClientState clientState, ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.clientState = clientState;
        this.sigScanner = sigScanner;
        this.config = config;
        this.log = log;

        InitCollisionPatch();
    }

    /// <summary>
    /// Scan for the camera collision raycast call and save original bytes for patching.
    /// Signature from Cammy: matches the CALL + TEST AL,AL + JZ pattern in the camera collision check.
    /// </summary>
    private void InitCollisionPatch()
    {
        try
        {
            collisionPatchAddress = sigScanner.ScanModule("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01");
            if (collisionPatchAddress != nint.Zero)
            {
                collisionOriginalBytes = new byte[CollisionPatchBytes.Length];
                System.Runtime.InteropServices.Marshal.Copy(collisionPatchAddress, collisionOriginalBytes, 0, collisionOriginalBytes.Length);
                log.Info($"DeathCam: Camera collision patch address found at 0x{collisionPatchAddress:X}.");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Could not find camera collision signature — collision disable will not work.");
            collisionPatchAddress = nint.Zero;
        }
    }

    /// <summary>
    /// Enable the camera collision disable patch (replaces CALL with XOR AL,AL + NOPs).
    /// </summary>
    private void EnableCollisionPatch()
    {
        if (collisionPatchActive || collisionPatchAddress == nint.Zero)
            return;

        WriteMemory(collisionPatchAddress, CollisionPatchBytes);
        collisionPatchActive = true;
        log.Info("DeathCam: Camera collision patch enabled.");
    }

    /// <summary>
    /// Disable the camera collision patch (restore original bytes).
    /// </summary>
    private void DisableCollisionPatch()
    {
        if (!collisionPatchActive || collisionPatchAddress == nint.Zero || collisionOriginalBytes == null)
            return;

        WriteMemory(collisionPatchAddress, collisionOriginalBytes);
        collisionPatchActive = false;
        log.Info("DeathCam: Camera collision patch disabled.");
    }

    /// <summary>
    /// Write bytes to a code address, temporarily making the page writable.
    /// </summary>
    private static void WriteMemory(nint address, byte[] bytes)
    {
        VirtualProtect(address, (nuint)bytes.Length, 0x40 /* PAGE_EXECUTE_READWRITE */, out uint oldProtect);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
    }

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    /// <summary>
    /// Create and enable the camera update hook. Call after the game camera is available.
    /// Safe to call multiple times — only hooks once.
    /// </summary>
    public void EnsureHook()
    {
        if (cameraUpdateHook != null)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var camera = (CameraBase*)camMgr->Camera;

            // Read vtable pointer (first 8 bytes of the struct)
            var vtable = *(nint**)camera;
            // CameraBase.Update() is VirtualFunction(3) — vtable index 3
            // Note: vtable is nint*, so indexing already scales by sizeof(nint)
            var updateAddr = vtable[3];

            cameraUpdateHook = gameInterop.HookFromAddress<CameraUpdateDelegate>(updateAddr, CameraUpdateDetour);
            cameraUpdateHook.Enable();

            log.Info($"DeathCam: Camera update hook created at 0x{updateAddr:X}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "DeathCam: Failed to create camera update hook.");
        }
    }

    /// <summary>
    /// Hook detour: called when the game updates the camera.
    /// Calls the original (game computes camera position), then applies our offsets.
    /// </summary>
    private void CameraUpdateDetour(CameraBase* thisPtr)
    {
        // Check if this is the main game camera
        bool isGameCamera = false;
        try
        {
            var camMgr = GameCameraManager.Instance();
            isGameCamera = camMgr != null && (nint)thisPtr == (nint)camMgr->Camera;
        }
        catch { }

        // Let the game compute the camera position normally
        cameraUpdateHook!.Original(thisPtr);

        if (!isGameCamera)
            return;

        if (state == DeathCamState.Inactive && !IsPreviewActive && !IsActiveCamMode)
            return;

        try
        {
            var gameCam = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)thisPtr;
            var sceneCam = &thisPtr->SceneCamera;

            float camH = gameCam->DirH;
            var offset = ComputeOffsetFromCameraAngle(camH);

            if (state != DeathCamState.Inactive || IsActiveCamMode)
            {
                // Death cam / Active cam: override look-at to follow bone
                var bonePos = GetBoneWorldPosition(config.DeathCamBoneIndex);
                if (bonePos == null)
                {
                    var player = clientState.LocalPlayer;
                    if (player != null)
                        bonePos = player.Position;
                }

                if (bonePos != null)
                {
                    if (state == DeathCamState.Interpolating)
                    {
                        float t = Math.Clamp(interpElapsed / config.DeathCamTransitionDuration, 0f, 1f);
                        float smooth = t * t * (3f - 2f * t);
                        var targetLookAt = bonePos.Value + offset;
                        Vector3 lookAt = Vector3.Lerp(startLookAt, targetLookAt, smooth);
                        sceneCam->LookAtVector = lookAt;
                    }
                    else
                    {
                        sceneCam->LookAtVector = bonePos.Value + offset;
                    }
                }
            }
            else
            {
                // Preview mode: shift the look-at by the same offset so view direction is preserved
                Vector3 lookAt = sceneCam->LookAtVector;
                sceneCam->LookAtVector = lookAt + offset;
            }

            // Apply position offset to the computed camera eye position
            Vector3 pos = sceneCam->Position;
            sceneCam->Position = pos + offset;

            if (sceneCam->RenderCamera != null)
            {
                Vector3 origin = sceneCam->RenderCamera->Origin;
                sceneCam->RenderCamera->Origin = origin + offset;

                ApplyOffsetToViewMatrix(ref sceneCam->RenderCamera->ViewMatrix, offset);
            }

            ApplyOffsetToViewMatrix(ref sceneCam->ViewMatrix, offset);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Error in camera update detour.");
        }
    }

    /// <summary>
    /// Apply a world-space position offset to an existing view matrix.
    /// ViewMatrix translates world→view, so shifting camera position
    /// modifies the translation component: T -= R * offset
    /// </summary>
    private static void ApplyOffsetToViewMatrix(ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 viewMatrix, Vector3 offset)
    {
        viewMatrix.M41 -= viewMatrix.M11 * offset.X + viewMatrix.M12 * offset.Y + viewMatrix.M13 * offset.Z;
        viewMatrix.M42 -= viewMatrix.M21 * offset.X + viewMatrix.M22 * offset.Y + viewMatrix.M23 * offset.Z;
        viewMatrix.M43 -= viewMatrix.M31 * offset.X + viewMatrix.M32 * offset.Y + viewMatrix.M33 * offset.Z;
    }


    /// <summary>
    /// Get the world position of a bone on the local player character.
    /// Returns null if the bone cannot be read.
    /// </summary>
    public Vector3? GetBoneWorldPosition(int boneIndex)
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
            if (skeleton == null) return null;

            if (skeleton->PartialSkeletonCount == 0) return null;

            var partialSkeleton = &skeleton->PartialSkeletons[0];
            var pose = partialSkeleton->GetHavokPose(0);
            if (pose == null) return null;

            var modelPose = pose->GetSyncedPoseModelSpace();
            if (modelPose == null || modelPose->Length <= boneIndex || boneIndex < 0) return null;

            // Read bone position in model space
            var boneTransform = modelPose->Data[boneIndex];
            var boneModelPos = new Vector3(
                boneTransform.Translation.X,
                boneTransform.Translation.Y,
                boneTransform.Translation.Z);

            // Transform to world space using skeleton transform
            var skeletonPos = new Vector3(
                skeleton->Transform.Position.X,
                skeleton->Transform.Position.Y,
                skeleton->Transform.Position.Z);
            var skeletonRot = new Quaternion(
                skeleton->Transform.Rotation.X,
                skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z,
                skeleton->Transform.Rotation.W);

            var worldPos = skeletonPos + Vector3.Transform(boneModelPos, skeletonRot);
            return worldPos;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to read bone world position.");
            return null;
        }
    }

    /// <summary>
    /// Get the character's facing angle from the skeleton rotation (Y-axis rotation).
    /// </summary>
    private float GetCharacterFacing()
    {
        try
        {
            var player = clientState.LocalPlayer;
            if (player == null) return 0;

            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            return gameObj->Rotation; // Y-axis rotation in radians
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Capture the current camera state as the death cam anchor.
    /// Call this when the user clicks "Set Anchor" with the camera positioned how they want it.
    /// </summary>
    public bool SetAnchor()
    {
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
            {
                log.Warning("DeathCam: Cannot set anchor — camera not available.");
                return false;
            }

            var bonePos = GetBoneWorldPosition(config.DeathCamBoneIndex);
            if (bonePos == null)
            {
                log.Warning("DeathCam: Cannot set anchor — bone position not readable.");
                return false;
            }

            var gameCam = camMgr->Camera;
            var facing = GetCharacterFacing();

            // Store camera angles relative to character facing direction
            config.DeathCamAnchorDirH = gameCam->DirH - facing;
            config.DeathCamAnchorDirV = gameCam->DirV;
            config.DeathCamAnchorDistance = gameCam->Distance;
            config.DeathCamFoV = gameCam->FoV;
            config.DeathCamAnchorSet = true;
            config.Save();

            log.Info($"DeathCam: Anchor set — DirH={config.DeathCamAnchorDirH:F3} DirV={config.DeathCamAnchorDirV:F3} Dist={config.DeathCamAnchorDistance:F2} FoV={config.DeathCamFoV:F3}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "DeathCam: Failed to set anchor.");
            return false;
        }
    }

    /// <summary>
    /// Activate the death cam. Called when the player dies in simulation.
    /// </summary>
    public void Activate()
    {
        if (!config.EnableDeathCam || !config.DeathCamAnchorSet)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var gameCam = camMgr->Camera;

            // Save current camera state as interpolation start
            startDirH = gameCam->DirH;
            startDirV = gameCam->DirV;
            startDistance = gameCam->Distance;
            startFoV = gameCam->FoV;
            startLookAt = gameCam->CameraBase.SceneCamera.LookAtVector;

            state = DeathCamState.Interpolating;
            interpElapsed = 0;

            log.Info("DeathCam: Activated — starting interpolation.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "DeathCam: Failed to activate.");
        }
    }

    /// <summary>
    /// Restore the game camera's original limits that we overrode.
    /// </summary>
    private void RestoreCameraLimits()
    {
        if (!limitsOverridden)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var gameCam = camMgr->Camera;
            gameCam->MinDistance = savedMinDistance;
            gameCam->MaxDistance = savedMaxDistance;
            gameCam->DirVMin = savedDirVMin;
            gameCam->DirVMax = savedDirVMax;
            gameCam->MinFoV = savedMinFoV;
            gameCam->MaxFoV = savedMaxFoV;

            // Reset tilt to 0
            *(float*)((byte*)gameCam + 0x170) = 0f;

            limitsOverridden = false;
            log.Info("DeathCam: Camera limits restored.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Failed to restore camera limits.");
        }
    }

    /// <summary>
    /// Deactivate the death cam. The game's camera controller resumes naturally.
    /// </summary>
    public void Deactivate()
    {
        if (state == DeathCamState.Inactive && !IsActiveCamMode)
            return;

        state = DeathCamState.Inactive;
        IsActiveCamMode = false;
        DisableCollisionPatch();
        RestoreCameraLimits();
        log.Info("DeathCam: Deactivated.");
    }

    /// <summary>
    /// Toggle active cam mode: same camera behavior as death cam but independent of death state.
    /// When death cam activates (character dies), it takes priority. Active cam resumes after reset.
    /// </summary>
    public void SetActiveCam(bool on)
    {
        IsActiveCamMode = on;
        if (on)
        {
            log.Info("DeathCam: Active cam ON.");
        }
        else
        {
            if (state == DeathCamState.Inactive && !IsPreviewActive)
            {
                DisableCollisionPatch();
                RestoreCameraLimits();
            }
            log.Info("DeathCam: Active cam OFF.");
        }
    }

    /// <summary>
    /// Toggle preview mode: applies height/side offsets and orbital params to the live camera
    /// so the user can see the result before dying.
    /// </summary>
    public void SetPreview(bool on)
    {
        IsPreviewActive = on;
        if (on)
        {
            log.Info("DeathCam: Preview ON.");
        }
        else
        {
            RestoreCameraLimits();
            log.Info("DeathCam: Preview OFF.");
        }
    }

    /// <summary>
    /// Helper to uncap camera limits and write orbital params + FoV directly to the game camera.
    /// </summary>
    private void WriteCameraParams(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        float dirH, float dirV, float distance, float fov)
    {
        // Save original limits before overriding (only once)
        if (!limitsOverridden)
        {
            savedMinDistance = gameCam->MinDistance;
            savedMaxDistance = gameCam->MaxDistance;
            savedDirVMin = gameCam->DirVMin;
            savedDirVMax = gameCam->DirVMax;
            savedMinFoV = gameCam->MinFoV;
            savedMaxFoV = gameCam->MaxFoV;
            limitsOverridden = true;
        }

        // Temporarily widen limits so our values aren't clamped
        gameCam->MinDistance = 0f;
        gameCam->MaxDistance = savedMaxDistance; // never exceed original max
        gameCam->DirVMin = -MathF.PI / 2f;
        gameCam->DirVMax = MathF.PI / 2f;
        gameCam->MinFoV = 0.01f;
        gameCam->MaxFoV = 3.0f;

        // Write orbital parameters
        gameCam->DirH = dirH;
        gameCam->DirV = dirV;
        gameCam->Distance = distance;
        gameCam->InterpDistance = distance;
        gameCam->FoV = fov;

        // Write tilt (roll) — native game camera field at offset 0x170 (same as Cammy)
        // The game engine reads this and applies the roll rotation when building the view matrix
        *(float*)((byte*)gameCam + 0x170) = config.DeathCamTilt;

        // Zero input deltas so user input doesn't fight us
        gameCam->InputDeltaH = 0;
        gameCam->InputDeltaV = 0;
        gameCam->InputDeltaHAdjusted = 0;
        gameCam->InputDeltaVAdjusted = 0;
    }

    /// <summary>
    /// Called in Framework.Update. Advances interpolation timers and writes
    /// Game Camera orbital fields (DirH, DirV, Distance, FoV) which feed into the
    /// game's camera pipeline. Also lazily creates the camera update hook.
    /// </summary>
    public void Tick(float deltaTime)
    {
        // Lazily create the camera hook once the camera is available
        EnsureHook();

        // Manage collision patch: enable when death cam active + config on, disable otherwise
        bool wantCollisionDisabled = config.DeathCamDisableCollision && (state != DeathCamState.Inactive || IsActiveCamMode);
        if (wantCollisionDisabled && !collisionPatchActive)
            EnableCollisionPatch();
        else if (!wantCollisionDisabled && collisionPatchActive)
            DisableCollisionPatch();

        // Preview mode: lock camera to anchor values
        if (IsPreviewActive && state == DeathCamState.Inactive && config.DeathCamAnchorSet)
        {
            try
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr == null || camMgr->Camera == null)
                    return;

                var gameCam = camMgr->Camera;
                var facing = GetCharacterFacing();

                WriteCameraParams(gameCam,
                    config.DeathCamAnchorDirH + facing,
                    config.DeathCamAnchorDirV,
                    config.DeathCamAnchorDistance,
                    config.DeathCamFoV);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "DeathCam: Error during preview tick.");
            }
            return;
        }

        // Active cam: bone tracking is handled by the camera hook (look-at override).
        // We do NOT call WriteCameraParams here — the user controls orbital angles
        // freely via mouse/keyboard/controller, just like normal gameplay.
        if (IsActiveCamMode && state == DeathCamState.Inactive)
            return;

        if (state == DeathCamState.Inactive)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
            {
                Deactivate();
                return;
            }

            var gameCam = camMgr->Camera;
            var facing = GetCharacterFacing();

            float anchorDirH = config.DeathCamAnchorDirH + facing;
            float anchorDirV = config.DeathCamAnchorDirV;
            float anchorDistance = config.DeathCamAnchorDistance;
            float anchorFoV = config.DeathCamFoV;

            if (state == DeathCamState.Interpolating)
            {
                interpElapsed += deltaTime;
                float t = Math.Clamp(interpElapsed / config.DeathCamTransitionDuration, 0f, 1f);
                float smooth = t * t * (3f - 2f * t); // smoothstep

                WriteCameraParams(gameCam,
                    AngleLerp(startDirH, anchorDirH, smooth),
                    Lerp(startDirV, anchorDirV, smooth),
                    Lerp(startDistance, anchorDistance, smooth),
                    Lerp(startFoV, anchorFoV, smooth));

                if (t >= 1f)
                {
                    state = DeathCamState.Following;
                    log.Info("DeathCam: Interpolation complete — now following bone.");
                }
            }
            else if (state == DeathCamState.Following)
            {
                WriteCameraParams(gameCam, anchorDirH, anchorDirV, anchorDistance, anchorFoV);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "DeathCam: Error during tick, deactivating.");
            Deactivate();
        }
    }

    /// <summary>
    /// Compute the height + side offset vector using the camera's horizontal angle.
    /// Side offset is perpendicular to camera direction (matching Cammy's approach).
    /// </summary>
    private Vector3 ComputeOffsetFromCameraAngle(float cameraHRotation)
    {
        var offset = new Vector3(0, config.DeathCamHeightOffset, 0);

        if (config.DeathCamSideOffset != 0)
        {
            // Cammy: a = currentHRotation - PI/2 (perpendicular to camera direction)
            float a = cameraHRotation - MathF.PI / 2f;
            offset.X += -config.DeathCamSideOffset * MathF.Sin(a);
            offset.Z += -config.DeathCamSideOffset * MathF.Cos(a);
        }

        return offset;
    }

    /// <summary>
    /// Lerp between two angles using shortest-path wrapping.
    /// </summary>
    private static float AngleLerp(float from, float to, float t)
    {
        float diff = to - from;
        // Normalize to [-PI, PI]
        while (diff > MathF.PI) diff -= 2f * MathF.PI;
        while (diff < -MathF.PI) diff += 2f * MathF.PI;
        return from + diff * t;
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    public void Dispose()
    {
        IsPreviewActive = false;
        Deactivate();
        DisableCollisionPatch();
        RestoreCameraLimits();
        cameraUpdateHook?.Dispose();
    }
}
