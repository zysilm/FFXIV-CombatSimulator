using System;
using System.Numerics;
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
    };

    private readonly IClientState clientState;
    private readonly IGameInteropProvider gameInterop;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private DeathCamState state = DeathCamState.Inactive;
    private float interpElapsed;

    // Interpolation start values (captured at activation)
    private float startDirH;
    private float startDirV;
    private float startDistance;
    private Vector3 startLookAt;

    // Camera update hook — intercepts camera computation to apply offsets
    private delegate void CameraUpdateDelegate(CameraBase* thisPtr);
    private Hook<CameraUpdateDelegate>? cameraUpdateHook;

    public DeathCamState State => state;
    public bool IsPreviewActive { get; private set; }

    public DeathCamController(IGameInteropProvider gameInterop, IClientState clientState, Configuration config, IPluginLog log)
    {
        this.gameInterop = gameInterop;
        this.clientState = clientState;
        this.config = config;
        this.log = log;
    }

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
        // Let the game compute the camera position normally
        cameraUpdateHook!.Original(thisPtr);

        // Only modify the main game camera, not lobby/spectator/etc.
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || (nint)thisPtr != (nint)camMgr->Camera)
                return;
        }
        catch
        {
            return;
        }

        if (state == DeathCamState.Inactive && !IsPreviewActive)
            return;

        try
        {
            var gameCam = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)thisPtr;
            var sceneCam = &thisPtr->SceneCamera;

            float camH = gameCam->DirH;
            var offset = ComputeOffsetFromCameraAngle(camH);

            if (state != DeathCamState.Inactive)
            {
                // Death cam: override look-at to follow bone
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

                // Also update the render camera view matrix translation for the offset
                ApplyOffsetToViewMatrix(ref sceneCam->RenderCamera->ViewMatrix, offset);
            }

            // Update scene camera view matrix as well
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
            config.DeathCamAnchorSet = true;
            config.Save();

            log.Info($"DeathCam: Anchor set — DirH={config.DeathCamAnchorDirH:F3} DirV={config.DeathCamAnchorDirV:F3} Dist={config.DeathCamAnchorDistance:F2}");
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
    /// Deactivate the death cam. The game's camera controller resumes naturally.
    /// </summary>
    public void Deactivate()
    {
        if (state == DeathCamState.Inactive)
            return;

        state = DeathCamState.Inactive;
        log.Info("DeathCam: Deactivated.");
    }

    /// <summary>
    /// Toggle preview mode: applies height/side offsets to the live camera so
    /// the user can see the result before dying.
    /// </summary>
    public void SetPreview(bool on)
    {
        IsPreviewActive = on;
        if (on)
            log.Info("DeathCam: Preview ON.");
        else
            log.Info("DeathCam: Preview OFF.");
    }

    /// <summary>
    /// Called in Framework.Update. Advances interpolation timers and writes
    /// Game Camera orbital fields (DirH, DirV, Distance) which feed into the
    /// game's camera pipeline. Also lazily creates the camera update hook.
    /// </summary>
    public void Tick(float deltaTime)
    {
        // Lazily create the camera hook once the camera is available
        EnsureHook();

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

            float targetDirH = config.DeathCamAnchorDirH + facing;
            float targetDirV = config.DeathCamAnchorDirV;
            float targetDistance = config.DeathCamAnchorDistance;

            if (state == DeathCamState.Interpolating)
            {
                interpElapsed += deltaTime;
                float t = Math.Clamp(interpElapsed / config.DeathCamTransitionDuration, 0f, 1f);
                float smooth = t * t * (3f - 2f * t); // smoothstep

                float dirH = AngleLerp(startDirH, targetDirH, smooth);
                float dirV = Lerp(startDirV, targetDirV, smooth);
                float distance = Lerp(startDistance, targetDistance, smooth);

                // Write orbital parameters — the game pipeline uses these to compute positions
                gameCam->DirH = dirH;
                gameCam->DirV = dirV;
                gameCam->Distance = distance;
                gameCam->InterpDistance = distance;
                gameCam->InputDeltaH = 0;
                gameCam->InputDeltaV = 0;
                gameCam->InputDeltaHAdjusted = 0;
                gameCam->InputDeltaVAdjusted = 0;

                if (t >= 1f)
                {
                    state = DeathCamState.Following;
                    log.Info("DeathCam: Interpolation complete — now following bone.");
                }
            }
            else if (state == DeathCamState.Following)
            {
                gameCam->DirH = targetDirH;
                gameCam->DirV = targetDirV;
                gameCam->Distance = targetDistance;
                gameCam->InterpDistance = targetDistance;
                gameCam->InputDeltaH = 0;
                gameCam->InputDeltaV = 0;
                gameCam->InputDeltaHAdjusted = 0;
                gameCam->InputDeltaVAdjusted = 0;
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
        cameraUpdateHook?.Dispose();
    }
}
