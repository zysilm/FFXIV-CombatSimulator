using System;
using System.Numerics;
using Dalamud.Plugin.Services;
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
    private readonly Configuration config;
    private readonly IPluginLog log;

    private DeathCamState state = DeathCamState.Inactive;
    private float interpElapsed;

    // Interpolation start values (captured at activation)
    private float startDirH;
    private float startDirV;
    private float startDistance;
    private Vector3 startLookAt;

    public DeathCamState State => state;

    public DeathCamController(IClientState clientState, Configuration config, IPluginLog log)
    {
        this.clientState = clientState;
        this.config = config;
        this.log = log;
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
    /// Called every framework update. Writes camera overrides while active.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (state == DeathCamState.Inactive)
            return;

        try
        {
            var bonePos = GetBoneWorldPosition(config.DeathCamBoneIndex);
            if (bonePos == null)
            {
                // Fallback: use player root position
                var player = clientState.LocalPlayer;
                if (player == null)
                {
                    Deactivate();
                    return;
                }
                bonePos = player.Position;
            }

            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
            {
                Deactivate();
                return;
            }

            var gameCam = camMgr->Camera;
            var sceneCam = &gameCam->CameraBase.SceneCamera;
            var facing = GetCharacterFacing();

            // Apply height and side offsets to bone position
            // Side offset is perpendicular to character facing (right = positive)
            float rightX = MathF.Cos(facing);
            float rightZ = -MathF.Sin(facing);
            var targetLookAt = bonePos.Value
                + new Vector3(0, config.DeathCamHeightOffset, 0)
                + new Vector3(rightX * config.DeathCamSideOffset, 0, rightZ * config.DeathCamSideOffset);

            // Compute target values
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
                var lookAt = Vector3.Lerp(startLookAt, targetLookAt, smooth);

                WriteCameraState(gameCam, sceneCam, dirH, dirV, distance, lookAt);

                if (t >= 1f)
                {
                    state = DeathCamState.Following;
                    log.Info("DeathCam: Interpolation complete — now following bone.");
                }
            }
            else if (state == DeathCamState.Following)
            {
                WriteCameraState(gameCam, sceneCam, targetDirH, targetDirV, targetDistance, targetLookAt);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "DeathCam: Error during tick, deactivating.");
            Deactivate();
        }
    }

    private static void WriteCameraState(
        FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera* sceneCam,
        float dirH, float dirV, float distance, Vector3 lookAt)
    {
        // Write Game Camera orbital parameters
        gameCam->DirH = dirH;
        gameCam->DirV = dirV;
        gameCam->Distance = distance;
        gameCam->InterpDistance = distance;

        // Clear user input to prevent fighting
        gameCam->InputDeltaH = 0;
        gameCam->InputDeltaV = 0;
        gameCam->InputDeltaHAdjusted = 0;
        gameCam->InputDeltaVAdjusted = 0;

        // Override orbit center to be the bone position
        sceneCam->LookAtVector = lookAt;
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
        Deactivate();
    }
}
