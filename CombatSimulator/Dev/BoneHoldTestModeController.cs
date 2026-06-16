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
/// Hidden dev mode: ragdoll held upright via physics constraints.
/// Supports NPC approach navigation, attack animation, arm binding,
/// NPC bone grab, shake, directional impulse and one-shot fling.
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

    // ── Core state ──────────────────────────────────────────────────────────
    private bool isActive;
    private SimulatedNpc? primaryNpc;

    // Position/facing captured at TryStart (player's death position).
    private Vector3 playerDeathPos;
    private Vector3 playerDeathForward; // unit vector, XZ only

    // ── Attack ──────────────────────────────────────────────────────────────
    private bool attackEnabled;
    private bool attackAllNpcs;
    private float attackTimer;
    // 0/0 = melee anim; non-zero = play looped emote on each tick
    private ushort attackEmoteLoopId;
    private ushort attackEmoteIntroId;

    // ── Shake ───────────────────────────────────────────────────────────────
    private bool shakeEnabled;
    private float shakeIntensity;
    private float shakeTimer;
    private const float ShakeInterval = 0.15f;

    // ── Arm bind ────────────────────────────────────────────────────────────
    private bool bindArmsEnabled;

    // ── NPC Grab ────────────────────────────────────────────────────────────
    private bool grabEnabled;
    private string grabNpcBone  = "j_te_r";
    private string grabPlayerBone = "j_kubi";

    // ── Approach navigation ─────────────────────────────────────────────────
    private float approachDistance;
    private readonly List<NpcApproachState> approachStates = new();

    private const float ApproachSpeed         = 3.5f;
    private const float ApproachStopDistance  = 0.25f;
    private const float ApproachRepathInterval = 0.75f;
    private const float ApproachRepathDistance = 1.0f;
    private const float ApproachWaypointReach  = 0.5f;
    private const float FloorRayStart         = 6.0f;
    private const float FloorRayDist          = 24.0f;
    private const float NavmeshYTolerance     = 2.5f;

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
        this.boneService         = boneService;
        this.emotePlayer         = emotePlayer;
        this.ragdollController   = ragdollController;
        this.animationController = animationController;
        this.movementBlockHook   = movementBlockHook;
        this.vnavmeshIpc         = vnavmeshIpc;
        this.log                 = log;
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────

    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, string anchorBone, float standingHeight,
        bool enableAttack, bool allNpcs, float approachDist,
        bool enableShake, float shakeStr,
        bool bindArms, float armSpread, float armHeight,
        bool enableGrab, string npcBone, string playerBone, float grabForce, float grabFreq)
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
            if (npc.State.IsAlive && npc.BattleChara != null) { candidate = npc; break; }
        }
        if (candidate == null)
        {
            log.Warning("BoneHoldTestMode: no alive NPC found");
            return false;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) { log.Warning("BoneHoldTestMode: no local player"); return false; }

        var go = (GameObject*)player.Address;
        playerDeathPos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        playerDeathForward = new Vector3(MathF.Sin(go->Rotation), 0f, MathF.Cos(go->Rotation));

        var anchorTarget = new Vector3(playerDeathPos.X, playerDeathPos.Y + standingHeight, playerDeathPos.Z);
        if (!ragdollController.CreateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone))
        {
            log.Warning("BoneHoldTestMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc        = candidate;
        attackEnabled     = enableAttack;
        attackAllNpcs     = allNpcs;
        approachDistance  = Math.Clamp(approachDist, 0.1f, 3.0f);
        shakeEnabled      = enableShake;
        shakeIntensity    = shakeStr;
        shakeTimer        = 0f;
        attackTimer       = 0f;
        bindArmsEnabled   = bindArms;
        grabEnabled       = enableGrab;
        grabNpcBone       = npcBone;
        grabPlayerBone    = playerBone;

        // Attack: take over NPC movement/animation only when attack is on.
        if (attackEnabled)
        {
            foreach (var npc in npcs)
            {
                if (!npc.State.IsAlive || npc.BattleChara == null) continue;
                movementBlockHook.AddApproachNpc(npc.Address);
                approachStates.Add(new NpcApproachState { Npc = npc });
            }
            animationController.SetBattleStance(primaryNpc);
        }

        // Arm bind.
        if (bindArmsEnabled)
            ApplyArmBind(armSpread, armHeight);

        // NPC Grab.
        if (grabEnabled)
            ApplyGrab(grabForce, grabFreq);

        isActive = true;
        log.Info($"BoneHoldTestMode: started — anchor={anchorBone} h={standingHeight:F2} atk={attackEnabled} shake={shakeEnabled} bind={bindArmsEnabled} grab={grabEnabled}");
        return true;
    }

    // ── Live adjustments (callable while active) ──────────────────────────────

    public void UpdateHold(string anchorBone, float standingHeight)
    {
        if (!isActive) return;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;
        var go = (GameObject*)player.Address;
        var target = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);
        ragdollController.UpdateStandingSupport(target, Quaternion.Identity, anchorBone);
    }

    public void UpdateArmBind(bool enabled, float spread, float height)
    {
        if (!isActive) return;
        bindArmsEnabled = enabled;
        if (!enabled) { ragdollController.RemoveWristConstraints(); return; }
        ApplyArmBind(spread, height);
    }

    public void SetShake(bool enabled, float intensity)
    {
        shakeEnabled   = enabled;
        shakeIntensity = intensity;
        if (!enabled) shakeTimer = 0f;
    }

    public void SetAttack(bool enabled, bool allNpcs, IReadOnlyList<SimulatedNpc> npcs)
    {
        if (!isActive) return;
        attackAllNpcs = allNpcs;
        if (enabled == attackEnabled) return;

        if (enabled)
        {
            foreach (var npc in npcs)
            {
                if (!npc.State.IsAlive || npc.BattleChara == null) continue;
                movementBlockHook.AddApproachNpc(npc.Address);
                approachStates.Add(new NpcApproachState { Npc = npc });
            }
            if (primaryNpc != null) animationController.SetBattleStance(primaryNpc);
            attackTimer   = 0f;
            attackEnabled = true;
        }
        else
        {
            foreach (var state in approachStates)
            {
                movementBlockHook.RemoveApproachNpc(state.Npc.Address);
                if (state.Npc.BattleChara == null) continue;
                var ch = (Character*)state.Npc.BattleChara;
                ActorVisualStateController.ClearMovement(ch, state.Visual);
                animationController.ClearBattleStance(state.Npc);
            }
            approachStates.Clear();
            attackEnabled = false;
        }
    }

    public void SetAttackAll(bool allNpcs)
    {
        attackAllNpcs = allNpcs;
    }

    /// <summary>Set which emote NPCs play on each attack tick. Both 0 = melee anim mode.</summary>
    public void SetAttackEmote(ushort loopId, ushort introId)
    {
        attackEmoteLoopId  = loopId;
        attackEmoteIntroId = introId;
    }

    public void SetGrab(bool enabled, string npcBone, string playerBone, float force, float freq)
    {
        if (!isActive) return;
        if (enabled == grabEnabled) return;

        grabNpcBone    = npcBone;
        grabPlayerBone = playerBone;

        if (enabled)
        {
            grabEnabled = true;
            ApplyGrab(force, freq);
        }
        else
        {
            grabEnabled = false;
            ragdollController.RemoveGrabConstraint();
        }
    }

    // ── One-shot actions ──────────────────────────────────────────────────────

    /// <summary>
    /// Raycast forward from the player's death position and pin the anchor bone
    /// to the wall hit point. Falls back to height-based pin if no wall is hit.
    /// </summary>
    public bool PinToWall(string bone, float rayHeight, float maxDist = 2.5f)
    {
        if (!isActive) return false;
        var origin = playerDeathPos + Vector3.UnitY * rayHeight;
        if (BGCollisionModule.RaycastMaterialFilter(origin, playerDeathForward, out var hit, maxDist))
        {
            // Pull back slightly from the surface so the body doesn't clip into the wall.
            var pinPoint = hit.Point - playerDeathForward * 0.15f;
            ragdollController.UpdateStandingSupport(pinPoint, Quaternion.Identity, bone);
            log.Info($"BoneHoldTestMode: wall hit at {hit.Point:F2}, pinned at {pinPoint:F2}");
            return true;
        }
        ragdollController.UpdateStandingSupport(
            new Vector3(playerDeathPos.X, playerDeathPos.Y + rayHeight, playerDeathPos.Z),
            Quaternion.Identity, bone);
        log.Info("BoneHoldTestMode: wall raycast no hit, using height fallback");
        return false;
    }

    /// <summary>Apply a lateral push impulse relative to player's death facing direction.</summary>
    public void Push(float forwardBias, float rightBias, float upBias, float speed = 8f)
    {
        if (!isActive) return;
        var right = new Vector3(playerDeathForward.Z, 0f, -playerDeathForward.X);
        var dir = playerDeathForward * forwardBias + right * rightBias + Vector3.UnitY * upBias;
        if (dir.LengthSquared() > 0.001f)
            dir = Vector3.Normalize(dir);
        ragdollController.ApplyImpulse("j_kosi", dir * speed);
    }

    /// <summary>Fling: big upward + forward impulse, then release hold.</summary>
    public void Fling()
    {
        if (!isActive) return;
        ragdollController.ApplyImpulse("j_kosi", playerDeathForward * 6f + Vector3.UnitY * 12f);
        Stop(Array.Empty<SimulatedNpc>());
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;

        if (!ragdollController.IsActive) { StopInternal(restoreNpc: true, allNpcs); return; }
        if (primaryNpc?.BattleChara == null) { StopInternal(restoreNpc: false, allNpcs); return; }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var playerGo  = (GameObject*)player.Address;
        var playerPos = new Vector3(playerGo->Position.X, playerGo->Position.Y, playerGo->Position.Z);

        // NPC approach navigation.
        foreach (var state in approachStates)
        {
            if (!state.Npc.State.IsAlive || state.Npc.BattleChara == null) continue;
            TickNpcApproach(state, playerPos, deltaTime);
        }

        // Shake.
        if (shakeEnabled)
        {
            shakeTimer -= deltaTime;
            if (shakeTimer <= 0f)
            {
                ragdollController.ApplyShake(shakeIntensity);
                shakeTimer = ShakeInterval;
            }
        }

        // NPC grab: update servo target every tick from NPC hand bone.
        if (grabEnabled && primaryNpc.BattleChara != null)
        {
            var handPos = boneService.GetBoneWorldPos(primaryNpc.Address, grabNpcBone);
            if (handPos.HasValue)
                ragdollController.UpdateGrabTarget(handPos.Value);
        }

        // Attack.
        if (!attackEnabled) return;
        attackTimer -= deltaTime;
        if (attackTimer > 0f) return;
        attackTimer = AttackInterval;

        if (!attackAllNpcs)
        {
            PerformAttackAnimation(primaryNpc);
            return;
        }
        foreach (var npc in allNpcs)
        {
            if (!npc.State.IsAlive || npc.BattleChara == null) continue;
            PerformAttackAnimation(npc);
        }
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    public void Stop() => Stop(Array.Empty<SimulatedNpc>());

    public void Stop(IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;
        StopInternal(restoreNpc: true, allNpcs);
    }

    private void StopInternal(bool restoreNpc, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        ragdollController.RemoveStandingSupport();
        ragdollController.RemoveWristConstraints();
        if (grabEnabled) ragdollController.RemoveGrabConstraint();

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
        shakeEnabled  = false;
        bindArmsEnabled = false;
        grabEnabled   = false;
        shakeTimer    = 0f;
        attackTimer   = 0f;
        isActive      = false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void PerformAttackAnimation(SimulatedNpc npc)
    {
        if (attackEmoteLoopId == 0)
            animationController.PlayNpcMeleeAnimationOnly(npc);
        else
            animationController.PlayNpcEmote(npc, attackEmoteLoopId, attackEmoteIntroId);
    }

    private void ApplyArmBind(float spread, float height)
    {
        var right = new Vector3(playerDeathForward.Z, 0f, -playerDeathForward.X);
        var leftTarget  = playerDeathPos + right * -(spread / 2f) + Vector3.UnitY * height;
        var rightTarget = playerDeathPos + right * +(spread / 2f) + Vector3.UnitY * height;
        ragdollController.CreateWristConstraints(leftTarget, rightTarget);
    }

    private void ApplyGrab(float force, float freq)
    {
        var initialPos = boneService.GetBoneWorldPos(primaryNpc!.Address, grabNpcBone)
                         ?? playerDeathPos + playerDeathForward * 0.5f + Vector3.UnitY * 1.4f;
        ragdollController.CreateGrabConstraint(grabPlayerBone, initialPos,
            primaryNpc.Address, force, 50f, freq);
    }

    // ─── Approach navigation ──────────────────────────────────────────────────

    private void TickNpcApproach(NpcApproachState state, Vector3 playerPos, float dt)
    {
        var go      = (GameObject*)state.Npc.BattleChara;
        var current = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);

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
            catch { path.Waypoints.Clear(); }
            path.PendingPath = null;
        }

        var exhausted   = path.WaypointIndex >= path.Waypoints.Count;
        var shouldRepath = path.PendingPath == null && path.RepathTimer <= 0f &&
                           (path.Waypoints.Count == 0 ||
                            Vector3.Distance(path.RequestedTarget, target) > ApproachRepathDistance ||
                            (exhausted && FlatDist(current, target) > 2f));
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
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true, Array.Empty<SimulatedNpc>());
    }
}
