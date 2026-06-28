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
    public static readonly (string Label, string BoneName)[] CenterBones =
    {
        ("Waist (n_hara)", "n_hara"),
        ("Hips (j_kosi)", "j_kosi"),
        ("Lower Spine (j_sebo_a)", "j_sebo_a"),
        ("Mid Spine (j_sebo_b)", "j_sebo_b"),
        ("Upper Spine (j_sebo_c)", "j_sebo_c"),
        ("Neck (j_kubi)", "j_kubi"),
        ("Head (j_kao)", "j_kao"),
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
    /// Calls the original, then shifts the camera orbit center to the selected bone.
    /// This mirrors ActiveCameraController's behavior while keeping the existing
    /// death-cam transition and preset entry points.
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

        if (state == DeathCamState.Inactive && !IsPreviewActive)
        {
            // Normal gameplay: no death-cam framing, but layer the combat hit-shake on top of
            // whatever the final view is (Active/Fight cam already applied via Original()).
            ApplyHitShake(thisPtr);
            return;
        }

        try
        {
            var sceneCam = &thisPtr->SceneCamera;

            var targetLookAt = GetDeathCamLookAt();
            if (targetLookAt == null)
                return;

            var sceneLookAt = sceneCam->LookAtVector;
            var oldLookAt = new Vector3(sceneLookAt.X, sceneLookAt.Y, sceneLookAt.Z);
            var desiredLookAt = targetLookAt.Value;

            if (state != DeathCamState.Inactive)
            {
                if (state == DeathCamState.Interpolating)
                {
                    float duration = Math.Max(config.DeathCamTransitionDuration, 0.01f);
                    float t = Math.Clamp(interpElapsed / duration, 0f, 1f);
                    float smooth = SmoothStep(t);
                    desiredLookAt = Vector3.Lerp(startLookAt, desiredLookAt, smooth);
                }
            }

            var cameraShift = desiredLookAt - oldLookAt;
            if (cameraShift.LengthSquared() < 0.000001f)
                return;

            sceneCam->LookAtVector = desiredLookAt;
            var scenePos = sceneCam->Position;
            sceneCam->Position = new Vector3(
                scenePos.X + cameraShift.X,
                scenePos.Y + cameraShift.Y,
                scenePos.Z + cameraShift.Z);

            if (sceneCam->RenderCamera != null)
            {
                var renderOrigin = sceneCam->RenderCamera->Origin;
                sceneCam->RenderCamera->Origin = new Vector3(
                    renderOrigin.X + cameraShift.X,
                    renderOrigin.Y + cameraShift.Y,
                    renderOrigin.Z + cameraShift.Z);
                ApplyOffsetToViewMatrix(ref sceneCam->RenderCamera->ViewMatrix, cameraShift);
            }

            ApplyOffsetToViewMatrix(ref sceneCam->ViewMatrix, cameraShift);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Error in camera update detour.");
        }
    }

    private Vector3? GetDeathCamLookAt()
    {
        var bonePos = GetBoneWorldPosition(config.DeathCamBoneName);
        if (bonePos == null)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null)
                return null;

            bonePos = player.Position;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return bonePos.Value + new Vector3(0, config.DeathCamHeightOffset, 0);

        return bonePos.Value + ComputeOffsetFromCameraAngle(camMgr->Camera->DirH);
    }

    /// <summary>
    /// Apply a world-space position offset to an existing view matrix.
    /// ViewMatrix translates world→view, so shifting camera position
    /// modifies the translation component: T -= R * offset
    /// </summary>
    /// <summary>Supplies the current combat hit-shake camera offset (set by Plugin). Applied only
    /// during normal gameplay, on top of any Active/Fight cam, and never during the death cam.</summary>
    public Func<Vector3>? HitShakeProvider { get; set; }

    private void ApplyHitShake(CameraBase* thisPtr)
    {
        var provider = HitShakeProvider;
        if (provider == null)
            return;
        var offset = provider();
        if (offset.LengthSquared() < 0.0000001f)
            return;
        try
        {
            // Mirror the death-cam shift: move Position + RenderCamera Origin AND the view matrices,
            // so the offset survives any downstream rebuild of the view matrix from Position.
            var sceneCam = &thisPtr->SceneCamera;
            var scenePos = sceneCam->Position;
            sceneCam->Position = new Vector3(scenePos.X + offset.X, scenePos.Y + offset.Y, scenePos.Z + offset.Z);
            if (sceneCam->RenderCamera != null)
            {
                var origin = sceneCam->RenderCamera->Origin;
                sceneCam->RenderCamera->Origin = new Vector3(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
                ApplyOffsetToViewMatrix(ref sceneCam->RenderCamera->ViewMatrix, offset);
            }
            ApplyOffsetToViewMatrix(ref sceneCam->ViewMatrix, offset);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Error applying hit shake.");
        }
    }

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
    public Vector3? GetBoneWorldPosition(string boneName)
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
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

            // Resolve bone name to index by iterating the skeleton's bones
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
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to read bone '{boneName}' world position.");
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
            var player = Core.Services.ObjectTable.LocalPlayer;
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

            var bonePos = GetBoneWorldPosition(config.DeathCamBoneName);
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

    public bool ApplyAnchorToCamera()
    {
        if (!config.DeathCamAnchorSet)
            return false;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return false;

            var gameCam = camMgr->Camera;
            var facing = GetCharacterFacing();

            WriteCameraOrbit(gameCam,
                config.DeathCamAnchorDirH + facing,
                config.DeathCamAnchorDirV,
                config.DeathCamAnchorDistance);
            ApplyLens(gameCam, config.DeathCamFoV);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DeathCam: Failed to apply anchor to camera.");
            return false;
        }
    }

    /// <summary>
    /// Activate the death cam. Called when the player dies in simulation.
    /// </summary>
    public void Activate()
    {
        if (!config.EnableDeathCam)
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
        if (state == DeathCamState.Inactive)
            return;

        state = DeathCamState.Inactive;
        DisableCollisionPatch();
        RestoreCameraLimits();
        log.Info("DeathCam: Deactivated.");
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
            ApplyAnchorToCamera();
            log.Info("DeathCam: Preview ON.");
        }
        else
        {
            RestoreCameraLimits();
            log.Info("DeathCam: Preview OFF.");
        }
    }

    /// <summary>
    /// Helper to uncap camera limits and write orbital params directly to the game camera.
    /// </summary>
    private void WriteCameraOrbit(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        float dirH, float dirV, float distance)
    {
        EnsureCameraLimits(gameCam);

        // Temporarily widen limits so our values aren't clamped
        gameCam->MinDistance = 0f;
        gameCam->MaxDistance = savedMaxDistance; // never exceed original max
        gameCam->DirVMin = -MathF.PI / 2f;
        gameCam->DirVMax = MathF.PI / 2f;

        // Write orbital parameters
        gameCam->DirH = dirH;
        gameCam->DirV = dirV;
        gameCam->Distance = distance;
        gameCam->InterpDistance = distance;

        // Write tilt (roll) — native game camera field at offset 0x170 (same as Cammy)
        // The game engine reads this and applies the roll rotation when building the view matrix
        *(float*)((byte*)gameCam + 0x170) = config.DeathCamTilt;

        // Zero input deltas so user input doesn't fight us
        gameCam->InputDeltaH = 0;
        gameCam->InputDeltaV = 0;
        gameCam->InputDeltaHAdjusted = 0;
        gameCam->InputDeltaVAdjusted = 0;
    }

    private void ApplyLens(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float fov)
    {
        EnsureCameraLimits(gameCam);

        gameCam->MinFoV = 0.01f;
        gameCam->MaxFoV = 3.0f;
        gameCam->FoV = fov;
        *(float*)((byte*)gameCam + 0x170) = config.DeathCamTilt;
    }

    private void ClearCameraInput(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam)
    {
        gameCam->InputDeltaH = 0;
        gameCam->InputDeltaV = 0;
        gameCam->InputDeltaHAdjusted = 0;
        gameCam->InputDeltaVAdjusted = 0;
    }

    private void EnsureCameraLimits(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam)
    {
        if (limitsOverridden)
            return;

        savedMinDistance = gameCam->MinDistance;
        savedMaxDistance = gameCam->MaxDistance;
        savedDirVMin = gameCam->DirVMin;
        savedDirVMax = gameCam->DirVMax;
        savedMinFoV = gameCam->MinFoV;
        savedMaxFoV = gameCam->MaxFoV;
        limitsOverridden = true;
    }

    /// <summary>
    /// Called in Framework.Update. Advances interpolation timers, applies lens
    /// settings, and lazily creates the camera update hook.
    /// </summary>
    public void Tick(float deltaTime)
    {
        // Lazily create the camera hook once the camera is available
        EnsureHook();

        // Manage collision patch: enable when death cam/preview active + config on, disable otherwise
        bool wantCollisionDisabled = config.DeathCamDisableCollision && (state != DeathCamState.Inactive || IsPreviewActive);
        if (wantCollisionDisabled && !collisionPatchActive)
            EnableCollisionPatch();
        else if (!wantCollisionDisabled && collisionPatchActive)
            DisableCollisionPatch();

        if (IsPreviewActive && state == DeathCamState.Inactive)
        {
            try
            {
                var camMgr = GameCameraManager.Instance();
                if (camMgr == null || camMgr->Camera == null)
                    return;

                ApplyLens(camMgr->Camera, config.DeathCamFoV);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "DeathCam: Error during preview tick.");
            }
            return;
        }

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

            ApplyLens(gameCam, anchorFoV);

            if (state == DeathCamState.Interpolating)
            {
                interpElapsed += deltaTime;
                float duration = Math.Max(config.DeathCamTransitionDuration, 0.01f);
                float t = Math.Clamp(interpElapsed / duration, 0f, 1f);
                float smooth = SmoothStep(t);

                if (config.DeathCamAnchorSet)
                {
                    WriteCameraOrbit(gameCam,
                        AngleLerp(startDirH, anchorDirH, smooth),
                        Lerp(startDirV, anchorDirV, smooth),
                        Lerp(startDistance, anchorDistance, smooth));
                    ClearCameraInput(gameCam);
                }

                if (t >= 1f)
                {
                    state = DeathCamState.Following;
                    log.Info("DeathCam: Interpolation complete — now following bone.");
                }
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

    private static float SmoothStep(float t)
    {
        return t * t * (3f - 2f * t);
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
