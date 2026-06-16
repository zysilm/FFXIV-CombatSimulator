using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Dev;

/// <summary>
/// Hidden mode: ragdoll stays active but is held upright via spine/pelvis physics
/// constraints while NPCs navigate to the configured approach distance and perform
/// periodic melee attacks.
/// </summary>
public unsafe class BoneHoldTestModeController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly RagdollController ragdollController;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly IPluginLog log;

    private bool isActive;
    private bool attackEnabled;
    private bool attackAllNpcs;
    private float approachDistance;
    private SimulatedNpc? primaryNpc;
    private float attackTimer;

    private readonly List<NpcApproachState> approachStates = new();

    private const float ApproachSpeed           = 3.5f;
    private const float ApproachStopDistance    = 0.25f;
    private const float ApproachRepathInterval  = 0.75f;
    private const float ApproachRepathDistance  = 1.0f;
    private const float ApproachWaypointReach   = 0.5f;
    private const float FloorRayStart           = 6.0f;
    private const float FloorRayDist            = 24.0f;
    private const float NavmeshYTolerance       = 2.5f;

    public float AttackInterval { get; set; } = 2.5f;
    public bool IsActive => isActive;

    private class NpcApproachState
    {
        public required SimulatedNpc Npc;
        public readonly ApproachPathState Path  = new();
        public readonly ActorVisualState Visual = new();
    }

    private class ApproachPathState
    {
        public List<Vector3> Waypoints  = new();
        public int WaypointIndex;
        public Vector3 RequestedTarget;
        public Vector3 PendingTarget;
        public Task<List<Vector3>>? PendingPath;
        public float RepathTimer;
    }

    public BoneHoldTestModeController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        RagdollController ragdollController,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        VNavmeshIpc vnavmeshIpc,
        IPluginLog log)
    {
        this.boneService          = boneService;
        this.emotePlayer          = emotePlayer;
        this.ragdollController    = ragdollController;
        this.animationController  = animationController;
        this.movementBlockHook    = movementBlockHook;
        this.vnavmeshIpc          = vnavmeshIpc;
        this.log                  = log;
    }

    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, string anchorBone = "j_kosi",
        float standingHeight = 0.92f, bool enableAttack = true,
        bool allNpcs = false, float approachDist = 1.0f)
    {
        if (isActive) return false;
        if (!ragdollController.IsActive)
        {
            log.Warning("BoneHoldTestMode: ragdoll not active");
            return false;
        }

        SimulatedNpc? candidate = null;
        foreach (var npc in npcs)
        {
            if (npc.State.IsAlive && npc.BattleChara != null)
            {
                candidate = npc;
                break;
            }
        }
        if (candidate == null)
        {
            log.Warning("BoneHoldTestMode: no alive NPC found");
            return false;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            log.Warning("BoneHoldTestMode: no local player");
            return false;
        }

        var go = (GameObject*)player.Address;
        var anchorTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);

        if (!ragdollController.CreateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone))
        {
            log.Warning("BoneHoldTestMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc       = candidate;
        attackEnabled    = enableAttack;
        attackAllNpcs    = allNpcs;
        approachDistance = Math.Clamp(approachDist, 0.1f, 3.0f);

        // Only take over NPC movement/animation when attack is enabled.
        // Without attack, VictorySequence keeps full control of NPCs.
        if (attackEnabled)
        {
            foreach (var npc in npcs)
            {
                if (!npc.State.IsAlive || npc.BattleChara == null) continue;
                movementBlockHook.AddApproachNpc(npc.Address);
                approachStates.Add(new NpcApproachState { Npc = npc });
            }
            animationController.SetBattleStance(primaryNpc);
            attackTimer = 0f;
        }

        isActive = true;
        log.Info($"BoneHoldTestMode: started — NPC '{primaryNpc.Name}', anchor={anchorBone}, height={standingHeight:F2}, approach={approachDistance:F2}m");
        return true;
    }

    /// <summary>Adjust anchor bone and height while the mode is already running.</summary>
    public void UpdateHold(string anchorBone, float standingHeight)
    {
        if (!isActive) return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var go = (GameObject*)player.Address;
        var anchorTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);
        ragdollController.UpdateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone);
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;

        if (!ragdollController.IsActive)
        {
            StopInternal(restoreNpc: true, allNpcs);
            return;
        }

        if (primaryNpc?.BattleChara == null)
        {
            StopInternal(restoreNpc: false, allNpcs);
            return;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var playerGo  = (GameObject*)player.Address;
        var playerPos = new Vector3(playerGo->Position.X, playerGo->Position.Y, playerGo->Position.Z);

        // Drive all registered NPCs toward the approach distance.
        foreach (var state in approachStates)
        {
            if (!state.Npc.State.IsAlive || state.Npc.BattleChara == null) continue;
            TickNpcApproach(state, playerPos, deltaTime);
        }

        if (!attackEnabled) return;

        attackTimer -= deltaTime;
        if (attackTimer > 0f) return;
        attackTimer = AttackInterval;

        if (!attackAllNpcs)
        {
            animationController.PlayNpcMeleeAnimationOnly(primaryNpc);
            return;
        }

        foreach (var npc in allNpcs)
        {
            if (!npc.State.IsAlive || npc.BattleChara == null) continue;
            animationController.PlayNpcMeleeAnimationOnly(npc);
        }
    }

    private void TickNpcApproach(NpcApproachState state, Vector3 playerPos, float dt)
    {
        var go      = (GameObject*)state.Npc.BattleChara;
        var current = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);

        // Target = playerPos offset toward the NPC at approachDistance.
        var dir2d = current - playerPos;
        dir2d.Y = 0f;
        var target = dir2d.LengthSquared() > 0.01f
            ? playerPos + Vector3.Normalize(dir2d) * approachDistance
            : playerPos + new Vector3(0, 0, approachDistance);
        target = SnapToFloor(target, playerPos.Y);

        if (FlatDist(current, target) <= ApproachStopDistance)
        {
            movementBlockHook.SetApproachPosition(go, target.X, target.Y, target.Z);
            FaceToward(go, playerPos);
            ActorVisualStateController.ClearMovement((Character*)go, state.Visual);
            return;
        }

        var moveTarget = GetNavTarget(state.Path, current, target, playerPos.Y, dt);
        var moveDir    = moveTarget - current;
        moveDir.Y = 0f;
        if (moveDir.LengthSquared() < 0.0001f) return;

        moveDir = Vector3.Normalize(moveDir);
        var remaining = FlatDist(current, moveTarget);
        var moveDist  = ApproachSpeed * dt;
        var next      = remaining <= moveDist ? moveTarget : current + moveDir * moveDist;
        next = SnapToFloor(next, current.Y);

        movementBlockHook.SetApproachPosition(go, next.X, next.Y, next.Z);
        movementBlockHook.SetApproachRotation(go, MathF.Atan2(moveDir.X, moveDir.Z));
        ActorVisualStateController.ApplyMoving((Character*)go, state.Visual, dt);
    }

    private Vector3 GetNavTarget(ApproachPathState path, Vector3 current, Vector3 target, float refY, float dt)
    {
        vnavmeshIpc.RefreshStatus();
        if (!vnavmeshIpc.CanPathfind) return target;

        path.RepathTimer = MathF.Max(0f, path.RepathTimer - dt);

        if (path.PendingPath is { IsCompleted: true })
        {
            try
            {
                path.Waypoints       = path.PendingPath.GetAwaiter().GetResult();
                path.RequestedTarget = path.PendingTarget;
                path.WaypointIndex   = 0;
            }
            catch
            {
                path.Waypoints.Clear();
            }
            path.PendingPath = null;
        }

        var pathExhausted = path.WaypointIndex >= path.Waypoints.Count;
        var shouldRepath  = path.PendingPath == null &&
                            path.RepathTimer <= 0f &&
                            (path.Waypoints.Count == 0 ||
                             Vector3.Distance(path.RequestedTarget, target) > ApproachRepathDistance ||
                             (pathExhausted && FlatDist(current, target) > 2f));

        if (shouldRepath)
        {
            path.RepathTimer   = ApproachRepathInterval;
            var from           = SnapNavmesh(current, current.Y) ?? current;
            var to             = SnapNavmesh(target, refY) ?? target;
            path.PendingTarget = to;
            try { path.PendingPath = vnavmeshIpc.Pathfind(from, to, 0.75f); }
            catch { path.PendingPath = null; }
        }

        while (path.WaypointIndex < path.Waypoints.Count &&
               FlatDist(current, path.Waypoints[path.WaypointIndex]) < ApproachWaypointReach)
            path.WaypointIndex++;

        return path.WaypointIndex < path.Waypoints.Count
            ? SnapToFloor(path.Waypoints[path.WaypointIndex], current.Y)
            : target;
    }

    private Vector3 SnapToFloor(Vector3 pos, float refY)
    {
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(pos.X, refY + FloorRayStart, pos.Z),
                new Vector3(0, -1, 0), out var hit, FloorRayDist))
            return new Vector3(pos.X, hit.Point.Y, pos.Z);

        var snapped = SnapNavmesh(pos, refY);
        return snapped.HasValue ? new Vector3(pos.X, snapped.Value.Y, pos.Z) : pos;
    }

    private Vector3? SnapNavmesh(Vector3 pos, float refY)
    {
        try
        {
            vnavmeshIpc.RefreshStatus();
            if (!vnavmeshIpc.CanPathfind) return null;
            var snapped = vnavmeshIpc.NearestPointReachable(pos)
                          ?? vnavmeshIpc.PointOnFloor(pos + new Vector3(0, 10f, 0));
            if (snapped.HasValue && MathF.Abs(snapped.Value.Y - refY) <= NavmeshYTolerance)
                return snapped;
        }
        catch { }
        return null;
    }

    private void FaceToward(GameObject* go, Vector3 target)
    {
        var dir = target - new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.001f) return;
        movementBlockHook.SetApproachRotation(go, MathF.Atan2(dir.X, dir.Z));
    }

    private static float FlatDist(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Stop() => Stop(Array.Empty<SimulatedNpc>());

    public void Stop(IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;
        StopInternal(restoreNpc: true, allNpcs);
    }

    private void StopInternal(bool restoreNpc, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        ragdollController.RemoveStandingSupport();

        foreach (var state in approachStates)
        {
            movementBlockHook.RemoveApproachNpc(state.Npc.Address);
            if (!restoreNpc || state.Npc.BattleChara == null) continue;
            var character = (Character*)state.Npc.BattleChara;
            ActorVisualStateController.ClearMovement(character, state.Visual);
            emotePlayer.ResetEmote(character);
            if (attackEnabled) animationController.ClearBattleStance(state.Npc);
            character->SetMode(CharacterModes.Normal, 0);
        }
        approachStates.Clear();

        log.Info($"BoneHoldTestMode: stopped (primary='{primaryNpc?.Name ?? "none"}')");
        primaryNpc    = null;
        attackEnabled = false;
        attackTimer   = 0f;
        isActive      = false;
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true, Array.Empty<SimulatedNpc>());
    }
}
