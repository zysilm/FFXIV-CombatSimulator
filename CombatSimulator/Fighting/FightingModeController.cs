using System;
using System.Numerics;
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

public unsafe sealed class FightingModeController : IFightingModeInputSink
{
    private readonly Configuration config;
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly MapEnemyController mapEnemyController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly ActiveCameraController activeCameraController;
    private readonly IPluginLog log;

    private SimulatedNpc? target;
    private nint playerAddress;
    private nint targetAddress;
    private Vector3 laneAxis = Vector3.UnitZ;
    private Vector3 smoothedCameraCenter;
    private float smoothedCameraDistance;
    private float savedMaxDistance;
    private bool maxDistanceOverridden;
    private bool hadActiveCameraBeforeEngage;
    private bool engaged;

    public bool IsEngaged => engaged;
    public Vector3? CameraCenterOverride => engaged ? smoothedCameraCenter : null;

    public FightingModeController(
        Configuration config,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        MapEnemyController mapEnemyController,
        MovementBlockHook movementBlockHook,
        ActiveCameraController activeCameraController,
        IPluginLog log)
    {
        this.config = config;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.mapEnemyController = mapEnemyController;
        this.movementBlockHook = movementBlockHook;
        this.activeCameraController = activeCameraController;
        this.log = log;
    }

    public bool OnPlayerAction(uint actionType, uint actionId, ulong targetId, uint extraParam)
    {
        if (!config.FightingMode || !combatEngine.IsActive)
            return false;

        config.ActionMode = false;
        config.EnableCustomTargeting = false;

        var npc = ResolveTarget(targetId);
        if (npc == null)
        {
            log.Debug($"FightingMode: no valid target for actionId={actionId}, targetId=0x{targetId:X}");
            return true;
        }

        Engage(npc);
        combatEngine.EnqueuePlayerAction(actionType, actionId, npc.SimulatedEntityId, extraParam);
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (!config.FightingMode || !combatEngine.IsActive || !combatEngine.State.PlayerState.IsAlive)
        {
            Disengage();
            return;
        }

        if (target == null || !target.IsAlive || target.Address == nint.Zero)
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

        movementBlockHook.AddApproachNpc(playerAddress);
        movementBlockHook.AddApproachNpc(targetAddress);

        ApplyLane(playerObj, targetObj);
        UpdateCamera(playerObj, targetObj, deltaTime);
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
        hadActiveCameraBeforeEngage = config.EnableActiveCamera || activeCameraController.IsActive;
        config.EnableActiveCamera = true;
        activeCameraController.SetActive(true);
        log.Info($"FightingMode: engaged 1v1 with '{npc.Name}' (0x{npc.SimulatedEntityId:X}).");
    }

    private void Disengage()
    {
        if (playerAddress != nint.Zero)
            movementBlockHook.RemoveApproachNpc(playerAddress);
        if (targetAddress != nint.Zero)
            movementBlockHook.RemoveApproachNpc(targetAddress);

        if (!hadActiveCameraBeforeEngage && engaged)
        {
            config.EnableActiveCamera = false;
            activeCameraController.SetActive(false);
        }

        RestoreCameraMaxDistance();
        target = null;
        playerAddress = nint.Zero;
        targetAddress = nint.Zero;
        engaged = false;
    }

    private void ApplyLane(GameObject* playerObj, GameObject* targetObj)
    {
        var p = ToVector3(playerObj->Position);
        var e = ToVector3(targetObj->Position);
        var flat = e - p;
        flat.Y = 0f;
        if (flat.LengthSquared() > 0.0001f)
            laneAxis = Vector3.Normalize(flat);

        var center = (p + e) * 0.5f;
        var pAlong = Vector3.Dot(p - center, laneAxis);
        var eAlong = Vector3.Dot(e - center, laneAxis);
        var separation = MathF.Abs(eAlong - pAlong);
        var minSep = MathF.Max(0.1f, config.FightingModeMinSeparation);
        var maxSep = MathF.Max(minSep + 0.1f, config.FightingModeMaxSeparation);
        separation = Math.Clamp(separation, minSep, maxSep);

        var playerSign = pAlong <= eAlong ? -1f : 1f;
        var enemySign = -playerSign;
        var targetP = center + laneAxis * (playerSign * separation * 0.5f);
        var targetE = center + laneAxis * (enemySign * separation * 0.5f);
        targetP.Y = p.Y;
        targetE.Y = e.Y;

        movementBlockHook.SetApproachPosition(playerObj, targetP.X, targetP.Y, targetP.Z);
        movementBlockHook.SetApproachPosition(targetObj, targetE.X, targetE.Y, targetE.Z);
        movementBlockHook.SetApproachRotation(playerObj, YawTo(targetE - targetP));
        movementBlockHook.SetApproachRotation(targetObj, YawTo(targetP - targetE));
    }

    private void UpdateCamera(GameObject* playerObj, GameObject* targetObj, float dt)
    {
        var p = ToVector3(playerObj->Position);
        var e = ToVector3(targetObj->Position);
        var center = (p + e) * 0.5f;
        center.Y = MathF.Min(p.Y, e.Y) + config.FightingModeCameraHeight;

        var sep = Vector3.Distance(new Vector3(p.X, 0f, p.Z), new Vector3(e.X, 0f, e.Z));
        var desiredDistance = Math.Clamp(
            sep * MathF.Max(1.0f, config.FightingModeCameraMargin) + 3.5f,
            MathF.Max(1f, config.FightingModeCameraMinDistance),
            MathF.Max(config.FightingModeCameraMinDistance + 0.1f, config.FightingModeCameraMaxDistance));

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

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return;

        var gameCam = camMgr->Camera;
        if (!maxDistanceOverridden)
        {
            savedMaxDistance = gameCam->MaxDistance;
            maxDistanceOverridden = true;
        }

        gameCam->MaxDistance = MathF.Max(savedMaxDistance, config.FightingModeCameraMaxDistance + 2f);
        gameCam->Distance = smoothedCameraDistance;
        gameCam->InterpDistance = smoothedCameraDistance;
        gameCam->DirH = MathF.Atan2(side.X, side.Z);
        gameCam->DirV = config.FightingModeCameraVerticalAngle;
        gameCam->InputDeltaH = 0;
        gameCam->InputDeltaV = 0;
        gameCam->InputDeltaHAdjusted = 0;
        gameCam->InputDeltaVAdjusted = 0;
    }

    private void RestoreCameraMaxDistance()
    {
        if (!maxDistanceOverridden)
            return;

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
                camMgr->Camera->MaxDistance = savedMaxDistance;
        }
        catch { }

        maxDistanceOverridden = false;
        smoothedCameraDistance = 0f;
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
