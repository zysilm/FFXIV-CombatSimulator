using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Core;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Fighting;

public interface IFightingModeInputSink
{
    bool OnPlayerAction(uint actionType, uint actionId, ulong targetId, uint extraParam);
}

public interface IFightingModeLaneConstraint
{
    bool IsLaneActive { get; }
    Vector3 ConstrainToLane(Vector3 position);
    Vector3 LaneAxis { get; }
}

public unsafe sealed class FightingModeController : IFightingModeInputSink, IFightingModeLaneConstraint
{
    private readonly Configuration config;
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly MapEnemyController mapEnemyController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly CameraModeCoordinator cameraCoordinator;
    private readonly BoneTransformService boneService;
    private readonly FightingPlayerMotor playerMotor;
    private readonly Func<nint, bool> isExternallyControlled;
    private readonly IPluginLog log;
    private readonly Func<float, Vector3> alongToWorld;

    private SimulatedNpc? target;
    private nint playerAddress;
    private nint targetAddress;
    private Vector3 laneAxis = Vector3.UnitZ;
    private Vector3 laneOrigin;
    private bool laneInitialized;
    private Vector3 smoothedCameraCenter;
    private float smoothedCameraDistance;
    private bool suppressDeathCameraThisDeath;
    private bool engaged;
    private bool postDeathEngaged;
    private bool activeCameraWasSuppressing;
    private CameraPhase cameraPhase = CameraPhase.Fighting;
    private float translateElapsed;
    private Vector3 translateStartCenter;
    private float translateStartDistance;
    private float translateStartDirH;
    private float translateStartDirV;
    private Vector3 lastSuppressedLookAt;
    private float lastSuppressedDistance;
    private float lastSuppressedDirH;
    private float lastSuppressedDirV;
    private bool hasLastSuppressedCamera;

    private enum CameraPhase
    {
        Fighting,
        Translating,
        PlayerFollow,
    }

    public bool IsEngaged => engaged;
    public bool IsLaneActive => (engaged || postDeathEngaged) && laneInitialized;
    public Vector3 LaneAxis => laneAxis;
    public bool ShouldSuppressDeathCamera => engaged || postDeathEngaged || suppressDeathCameraThisDeath;
    public SimulatedNpc? CurrentTarget => target;

    /// <summary>Weapon-contact attack path (FightingCombatController.OnAttackInput). Set by the
    /// plugin after construction; when unset, attacks fall back to the classic queued resolve.</summary>
    public Func<uint, SimulatedNpc, bool>? AttackSink { get; set; }

    /// <summary>1v1 fighting AI for the engaged enemy. Set by the plugin after construction;
    /// while wired, NpcAiController skips the fighter (see ControlsEnemy).</summary>
    public FightingAiController? FightingAi { get; set; }

    /// <summary>True while this controller (via FightingAi) owns the engaged enemy — used by
    /// NpcAiController's externally-controlled skip.</summary>
    public bool ControlsEnemy(nint address)
        => engaged && targetAddress == address && FightingAi != null;

    public FightingModeController(
        Configuration config,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        MapEnemyController mapEnemyController,
        MovementBlockHook movementBlockHook,
        CameraModeCoordinator cameraCoordinator,
        BoneTransformService boneService,
        FightingPlayerMotor playerMotor,
        Func<nint, bool> isExternallyControlled,
        IPluginLog log)
    {
        this.config = config;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.mapEnemyController = mapEnemyController;
        this.movementBlockHook = movementBlockHook;
        this.cameraCoordinator = cameraCoordinator;
        this.boneService = boneService;
        this.playerMotor = playerMotor;
        this.isExternallyControlled = isExternallyControlled;
        this.log = log;
        alongToWorld = along => laneOrigin + laneAxis * along;
    }

    public bool OnPlayerAction(uint actionType, uint actionId, ulong targetId, uint extraParam)
    {
        if (!config.FightingMode || !combatEngine.IsActive)
            return false;

        var npc = ResolveTarget(targetId);
        if (npc == null)
        {
            log.Debug($"FightingMode: no valid target for actionId={actionId}, targetId=0x{targetId:X}");
            return true;
        }

        Engage(npc);
        if (AttackSink != null)
            AttackSink(actionId, npc);
        else
            combatEngine.EnqueuePlayerAction(actionType, actionId, npc.SimulatedEntityId, extraParam);
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (!config.FightingMode)
        {
            Disengage();
            return;
        }

        if (!combatEngine.IsActive && !postDeathEngaged)
        {
            if (combatEngine.State.PlayerState.IsAlive)
                suppressDeathCameraThisDeath = false;
            Disengage();
            return;
        }

        if (target == null || target.Address == nint.Zero)
        {
            Disengage();
            return;
        }

        var player = Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
        {
            Disengage();
            return;
        }

        playerAddress = player.Address;
        targetAddress = target.Address;

        var playerObj = (GameObject*)playerAddress;
        var targetObj = (GameObject*)targetAddress;

        var targetControlled = isExternallyControlled(targetAddress);
        if (!targetControlled)
            movementBlockHook.AddApproachNpc(targetAddress);

        TickPlayerMotor(deltaTime, playerObj, targetObj);
        TickFightingAi(deltaTime, playerObj, targetObj, targetControlled);
        ApplyLane(playerObj, targetObj, targetControlled);
        UpdateCamera(playerObj, targetObj, deltaTime);
    }

    private void TickFightingAi(float dt, GameObject* playerObj, GameObject* targetObj, bool targetControlled)
    {
        if (FightingAi == null || targetControlled || !laneInitialized)
            return;

        var playerAlive = !postDeathEngaged && combatEngine.State.PlayerState.IsAlive;
        if (!playerAlive)
            return;

        var playerAlong = playerMotor.IsActive ? playerMotor.AlongPos : AlongOf(ToVector3(playerObj->Position));
        FightingAi.Tick(dt, AlongOf(ToVector3(targetObj->Position)), playerAlong);
    }

    private void TickPlayerMotor(float dt, GameObject* playerObj, GameObject* targetObj)
    {
        var playerAlive = !postDeathEngaged && combatEngine.State.PlayerState.IsAlive;
        if (!playerAlive)
        {
            playerMotor.End();
            return;
        }

        if (!laneInitialized)
            return;

        if (!playerMotor.IsActive)
        {
            var p = ToVector3(playerObj->Position);
            playerMotor.Begin(playerAddress, AlongOf(p), p.Y);
        }

        var e = ToVector3(targetObj->Position);
        playerMotor.Tick(dt, AlongOf(e), alongToWorld);
    }

    private float AlongOf(Vector3 pos)
        => Vector3.Dot(new Vector3(pos.X, 0f, pos.Z) - laneOrigin, laneAxis);

    /// <summary>
    /// Re-apply only the lane constraint after NPC AI has moved actors this frame.
    /// Camera smoothing and translate timers already advanced in Tick — running the
    /// full Tick twice per frame made them progress at double speed.
    /// </summary>
    public void ReapplyLane()
    {
        if (!engaged || !laneInitialized)
            return;
        if (playerAddress == nint.Zero || targetAddress == nint.Zero)
            return;

        ApplyLane((GameObject*)playerAddress, (GameObject*)targetAddress, isExternallyControlled(targetAddress));
    }

    public void Reset() => Disengage();

    private SimulatedNpc? ResolveTarget(ulong targetId)
    {
        SimulatedNpc? npc = null;
        if (targetId is not 0 and not 0xE0000000)
        {
            npc = npcSelector.GetSelectedNpc((uint)targetId);
            npc ??= mapEnemyController.TryRegisterByEntityId(targetId, force: true);
        }

        if (npc is { IsAlive: true, Address: not 0 })
            return npc;

        var player = Services.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        SimulatedNpc? best = null;
        var bestDistSq = float.MaxValue;
        foreach (var candidate in npcSelector.SelectedNpcs)
        {
            if (!candidate.IsAlive || candidate.Address == nint.Zero)
                continue;
            var d = candidate.GameObjectRef != null
                ? Vector3.DistanceSquared(candidate.GameObjectRef.Position, player.Position)
                : DistanceSquared(candidate.Address, player.Address);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = candidate;
            }
        }

        return best;
    }

    private static float DistanceSquared(nint a, nint b)
    {
        var ga = (GameObject*)a;
        var gb = (GameObject*)b;
        var pa = new Vector3(ga->Position.X, ga->Position.Y, ga->Position.Z);
        var pb = new Vector3(gb->Position.X, gb->Position.Y, gb->Position.Z);
        return Vector3.DistanceSquared(pa, pb);
    }

    private void Engage(SimulatedNpc npc)
    {
        if (target == npc && engaged)
            return;

        Disengage();
        target = npc;
        engaged = true;
        cameraPhase = CameraPhase.Fighting;
        CaptureLane();
        if (!isExternallyControlled(npc.Address))
            FightingAi?.Begin(npc);
        log.Info($"FightingMode: engaged 1v1 with '{npc.Name}' (0x{npc.SimulatedEntityId:X}).");
    }

    public void HandlePlayerDeath()
    {
        suppressDeathCameraThisDeath = engaged;
        playerMotor.End();
        FightingAi?.End();
        if (!engaged)
            return;

        postDeathEngaged = true;
        if (config.FightingModeTranslateCam)
            BeginTranslateCam();
    }

    private void Disengage()
    {
        playerMotor.End();
        FightingAi?.End();
        if (playerAddress != nint.Zero)
            movementBlockHook.RemoveApproachNpc(playerAddress);
        if (targetAddress != nint.Zero)
            movementBlockHook.RemoveApproachNpc(targetAddress);

        cameraCoordinator.Release(CameraOwner.Fighting2D);
        cameraCoordinator.Release(CameraOwner.FightingKO);
        smoothedCameraDistance = 0f;
        target = null;
        playerAddress = nint.Zero;
        targetAddress = nint.Zero;
        laneInitialized = false;
        postDeathEngaged = false;
        cameraPhase = CameraPhase.Fighting;
        engaged = false;
        // Camera handoff snapshots must not leak into the next session.
        activeCameraWasSuppressing = false;
        hasLastSuppressedCamera = false;
        suppressDeathCameraThisDeath = false;
        translateElapsed = 0f;
    }

    private void CaptureLane()
    {
        var player = Services.ObjectTable.LocalPlayer;
        if (player == null || target == null || target.Address == nint.Zero)
            return;

        var playerObj = (GameObject*)player.Address;
        var targetObj = (GameObject*)target.Address;
        var p = ToVector3(playerObj->Position);
        var e = ToVector3(targetObj->Position);
        var axis = e - p;
        axis.Y = 0f;
        laneAxis = axis.LengthSquared() > 0.0001f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        laneOrigin = (p + e) * 0.5f;
        laneOrigin.Y = 0f;
        laneInitialized = true;
    }

    public Vector3 ConstrainToLane(Vector3 position)
    {
        if (!IsLaneActive)
            return position;

        var along = Vector3.Dot(new Vector3(position.X, 0f, position.Z) - laneOrigin, laneAxis);
        var constrained = laneOrigin + laneAxis * along;
        constrained.Y = position.Y;
        return constrained;
    }

    private void ApplyLane(GameObject* playerObj, GameObject* targetObj, bool targetControlled)
    {
        var p = ToVector3(playerObj->Position);
        var e = ToVector3(targetObj->Position);

        if (!laneInitialized)
            CaptureLane();

        var playerAlive = !postDeathEngaged && combatEngine.State.PlayerState.IsAlive;

        // Player: motor-driven along the lane while alive. A dead body belongs to the
        // ragdoll, not the lane.
        if (playerAlive && playerMotor.IsActive)
        {
            var targetP = laneOrigin + laneAxis * playerMotor.AlongPos;
            targetP.Y = playerMotor.PosY;
            movementBlockHook.SetApproachPosition(playerObj, targetP.X, targetP.Y, targetP.Z);
            movementBlockHook.SetApproachRotation(playerObj, YawTo(e - targetP));
            p = targetP;
        }

        // Enemy: when MonsterMode owns it, it moves itself (lane-projected through the
        // IFightingModeLaneConstraint seam) — hands off. Otherwise project it onto the
        // lane at its own coordinate. Never recenter the pair: moving one actor must
        // not translate the other.
        if (targetControlled)
            return;

        var pAlong = AlongOf(p);
        var eAlong = FightingAi is { IsActive: true } && playerAlive
            ? FightingAi.DesiredAlong
            : AlongOf(e);

        if (playerAlive)
        {
            // Min separation only, clamping the enemy (its AI is the mover here).
            var minSep = Math.Clamp(config.FightingModeMinSeparation, 0.1f, 0.65f);
            float side = MathF.Sign(eAlong - pAlong);
            if (side == 0f)
                side = 1f;
            if (MathF.Abs(eAlong - pAlong) < minSep)
                eAlong = pAlong + side * minSep;
        }

        var targetE = laneOrigin + laneAxis * eAlong;
        targetE.Y = e.Y;
        movementBlockHook.SetApproachPosition(targetObj, targetE.X, targetE.Y, targetE.Z);
        movementBlockHook.SetApproachRotation(targetObj, YawTo(p - targetE));
    }

    private void UpdateCamera(GameObject* playerObj, GameObject* targetObj, float dt)
    {
        var playerDefeated = postDeathEngaged || !combatEngine.State.PlayerState.IsAlive;

        // User-enabled Active Cam has explicit priority over Fighting Mode camera. Fighting Mode still
        // owns combat/lane state, but it must not write camera angles, zoom, or mode-center override.
        if (config.EnableActiveCamera)
        {
            CaptureSuppressedCameraState();
            activeCameraWasSuppressing = true;
            return;
        }

        if (activeCameraWasSuppressing)
        {
            activeCameraWasSuppressing = false;
            if (playerDefeated && config.FightingModeTranslateCam)
                BeginTranslateCam(fromActiveCamera: true);
        }

        if (playerDefeated && config.FightingModeTranslateCam)
        {
            if (cameraPhase == CameraPhase.Fighting)
                BeginTranslateCam(fromActiveCamera: false);
            UpdateTranslateCamera(playerObj, dt);
            return;
        }

        if (!playerDefeated && cameraPhase != CameraPhase.Fighting)
            cameraPhase = CameraPhase.Fighting;

        var p = ToVector3(playerObj->Position);
        var e = ToVector3(targetObj->Position);
        var center = (p + e) * 0.5f;
        center.Y = MathF.Min(p.Y, e.Y) + config.FightingModeCameraHeight;

        var sep = Vector3.Distance(new Vector3(p.X, 0f, p.Z), new Vector3(e.X, 0f, e.Z));
        var minDistance = MathF.Min(MathF.Max(1f, config.FightingModeCameraMinDistance), 3.5f);
        var maxDistance = MathF.Min(MathF.Max(minDistance + 0.1f, config.FightingModeCameraMaxDistance), 12f);
        var desiredDistance = Math.Clamp(
            sep * MathF.Max(0.4f, config.FightingModeCameraMargin) + 1.6f,
            minDistance,
            maxDistance);

        var k = 1f - MathF.Exp(-MathF.Max(0.1f, config.FightingModeCameraSmoothing) * dt);
        if (smoothedCameraDistance <= 0.01f)
        {
            smoothedCameraCenter = center;
            smoothedCameraDistance = desiredDistance;
        }
        else
        {
            smoothedCameraCenter = Vector3.Lerp(smoothedCameraCenter, center, k);
            smoothedCameraDistance += (desiredDistance - smoothedCameraDistance) * k;
        }

        var side = new Vector3(-laneAxis.Z, 0f, laneAxis.X);
        if (side.LengthSquared() < 0.0001f)
            side = Vector3.UnitX;
        side = Vector3.Normalize(side);

        cameraCoordinator.Submit(CameraOwner.Fighting2D, new CameraRequest
        {
            OrbitCenter = smoothedCameraCenter,
            DirH = MathF.Atan2(side.X, side.Z),
            DirV = config.FightingModeCameraVerticalAngle,
            Distance = smoothedCameraDistance,
            MaxDistanceAtLeast = config.FightingModeCameraMaxDistance + 2f,
            ClearInputH = true,
            ClearInputV = true,
        });
    }

    private void BeginTranslateCam(bool fromActiveCamera = false)
    {
        if (!fromActiveCamera && cameraPhase is CameraPhase.Translating or CameraPhase.PlayerFollow)
            return;

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return;

        var gameCam = camMgr->Camera;
        var currentLookAt = gameCam->CameraBase.SceneCamera.LookAtVector;
        translateStartCenter = fromActiveCamera
            ? (hasLastSuppressedCamera
                ? lastSuppressedLookAt
                : new Vector3(currentLookAt.X, currentLookAt.Y, currentLookAt.Z))
            : smoothedCameraCenter;
        translateStartDistance = fromActiveCamera && hasLastSuppressedCamera
            ? lastSuppressedDistance
            : (smoothedCameraDistance > 0.01f ? smoothedCameraDistance : gameCam->Distance);
        translateStartDirH = fromActiveCamera && hasLastSuppressedCamera ? lastSuppressedDirH : gameCam->DirH;
        translateStartDirV = fromActiveCamera && hasLastSuppressedCamera ? lastSuppressedDirV : gameCam->DirV;
        smoothedCameraCenter = translateStartCenter;
        smoothedCameraDistance = translateStartDistance;
        translateElapsed = 0f;
        cameraPhase = CameraPhase.Translating;
    }

    private void CaptureSuppressedCameraState()
    {
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var gameCam = camMgr->Camera;
            var lookAt = gameCam->CameraBase.SceneCamera.LookAtVector;
            lastSuppressedLookAt = new Vector3(lookAt.X, lookAt.Y, lookAt.Z);
            lastSuppressedDistance = gameCam->Distance;
            lastSuppressedDirH = gameCam->DirH;
            lastSuppressedDirV = gameCam->DirV;
            hasLastSuppressedCamera = true;
        }
        catch { }
    }

    private void UpdateTranslateCamera(GameObject* playerObj, float dt)
    {
        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return;

        var gameCam = camMgr->Camera;
        var targetCenter = GetTranslateTargetCenter(playerObj, gameCam->DirH);
        var targetDistance = Math.Clamp(config.FightingModeTranslateDistance, 1.0f, 20.0f);
        var targetDirH = config.FightingModeTranslateLockHorizontal
            ? config.FightingModeTranslateHorizontalAngle
            : gameCam->DirH;
        var targetDirV = config.FightingModeTranslateLockVertical
            ? config.FightingModeTranslateVerticalAngle
            : gameCam->DirV;

        float? dirH = null;
        float? dirV = null;
        if (cameraPhase == CameraPhase.Translating)
        {
            translateElapsed += dt;
            var duration = MathF.Max(0.01f, config.FightingModeTranslateDuration);
            var t = Math.Clamp(translateElapsed / duration, 0f, 1f);
            var s = t * t * (3f - 2f * t);
            smoothedCameraCenter = Vector3.Lerp(translateStartCenter, targetCenter, s);
            smoothedCameraDistance = Lerp(translateStartDistance, targetDistance, s);
            if (config.FightingModeTranslateLockHorizontal)
                dirH = AngleLerp(translateStartDirH, targetDirH, s);
            if (config.FightingModeTranslateLockVertical)
                dirV = Lerp(translateStartDirV, targetDirV, s);
            if (t >= 1f)
                cameraPhase = CameraPhase.PlayerFollow;
        }
        else
        {
            smoothedCameraCenter = targetCenter;
            smoothedCameraDistance = targetDistance;
            if (config.FightingModeTranslateLockHorizontal)
                dirH = targetDirH;
            if (config.FightingModeTranslateLockVertical)
                dirV = targetDirV;
        }

        cameraCoordinator.Submit(CameraOwner.FightingKO, new CameraRequest
        {
            OrbitCenter = smoothedCameraCenter,
            DirH = dirH,
            DirV = dirV,
            Distance = smoothedCameraDistance,
            MaxDistanceAtLeast = smoothedCameraDistance + 1f,
            ClearInputH = config.FightingModeTranslateLockHorizontal,
            ClearInputV = config.FightingModeTranslateLockVertical,
        });
    }

    private Vector3 GetTranslateTargetCenter(GameObject* playerObj, float dirH)
    {
        var boneName = config.FightingModeTranslateBoneName == "n_hara"
            ? "j_kosi"
            : config.FightingModeTranslateBoneName;
        var pos = boneService.GetBoneWorldPos(playerAddress, boneName)
                  ?? ToVector3(playerObj->Position);
        pos.Y += config.FightingModeTranslateHeightOffset;

        if (MathF.Abs(config.FightingModeTranslateSideOffset) > 0.001f)
        {
            var a = dirH - MathF.PI / 2f;
            pos.X += -config.FightingModeTranslateSideOffset * MathF.Sin(a);
            pos.Z += -config.FightingModeTranslateSideOffset * MathF.Cos(a);
        }

        return pos;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float AngleLerp(float a, float b, float t)
    {
        var diff = NormalizeAngle(b - a);
        return a + diff * t;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private static Vector3 ToVector3(FFXIVClientStructs.FFXIV.Common.Math.Vector3 v)
        => new(v.X, v.Y, v.Z);

    private static float YawTo(Vector3 dir)
    {
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            return 0f;
        dir = Vector3.Normalize(dir);
        return MathF.Atan2(dir.X, dir.Z);
    }
}
