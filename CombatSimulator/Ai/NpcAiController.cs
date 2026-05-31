using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Ai;

public unsafe class NpcAiController : IDisposable
{
    public const float PlayerTriggeredEngageDelay = 0.5f;

    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly IClientState clientState;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly Func<nint, bool> isExternallyControlled;
    private readonly Dictionary<nint, ApproachPathState> approachPaths = new();
    private const float VNavmeshRepathDistance = 1.5f;
    private const float VNavmeshRepathInterval = 1.0f;
    private const float VNavmeshPathTolerance = 0.5f;
    private const float VNavmeshFloorResnapInterval = 0.25f;
    private const float TerrainGridStep = 0.5f;
    private const int TerrainGridMaxSize = 33;
    private const ushort NormalRunTimelineId = 22;

    // Auto-engage countdown: when >= 0, every Tick decrements; on reaching
    // 0 we call EngageNpc on each selected NPC. Negative = inactive.
    private float pendingAutoEngageDelay = -1f;

    private class ApproachPathState
    {
        public List<Vector3> Waypoints { get; set; } = new();
        public int WaypointIndex { get; set; }
        public Vector3 RequestedTarget { get; set; }
        public float RepathTimer { get; set; }
        public Task<List<Vector3>>? PendingPath { get; set; }
        public Vector3 PendingTarget { get; set; }
        public string LastError { get; set; } = "";
        public float FloorYOffset { get; set; }
        public float ConfiguredHeightOffset { get; set; }
        public bool HasFloorYOffset { get; set; }
        public float FloorResnapTimer { get; set; }
        public float LastFloorY { get; set; }
        public bool HasLastFloorY { get; set; }
        public int LastCorrectedWaypointIndex { get; set; } = -1;
        public bool MoveAnimActive { get; set; }
        public float StableRootTerrainClearance { get; set; }
        public bool HasStableRootTerrainClearance { get; set; }
        public float LastMoveRootY { get; set; }
        public bool HasLastMoveRootY { get; set; }
    }

    private sealed class ApproachTerrainCache
    {
        public float OriginX { get; init; }
        public float OriginZ { get; init; }
        public float Step { get; init; }
        public int Width { get; init; }
        public int Depth { get; init; }
        public float[,] Heights { get; init; } = new float[0, 0];
        public bool[,] Valid { get; init; } = new bool[0, 0];

        public bool TrySample(float x, float z, out float y)
        {
            y = 0;
            if (Width <= 0 || Depth <= 0 || Step <= 0)
                return false;

            var gx = (x - OriginX) / Step;
            var gz = (z - OriginZ) / Step;
            var ix = (int)MathF.Floor(gx);
            var iz = (int)MathF.Floor(gz);

            if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth)
                return false;

            if (ix < Width - 1 && iz < Depth - 1)
            {
                var tx = gx - ix;
                var tz = gz - iz;
                if (Valid[ix, iz] && Valid[ix + 1, iz] && Valid[ix, iz + 1] && Valid[ix + 1, iz + 1])
                {
                    var y0 = Lerp(Heights[ix, iz], Heights[ix + 1, iz], tx);
                    var y1 = Lerp(Heights[ix, iz + 1], Heights[ix + 1, iz + 1], tx);
                    y = Lerp(y0, y1, tz);
                    return true;
                }
            }

            var bestDistSq = float.MaxValue;
            var bestY = 0f;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var sx = ix + dx;
                var sz = iz + dz;
                if (sx < 0 || sz < 0 || sx >= Width || sz >= Depth || !Valid[sx, sz])
                    continue;

                var wx = OriginX + sx * Step;
                var wz = OriginZ + sz * Step;
                var distSq = (wx - x) * (wx - x) + (wz - z) * (wz - z);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestY = Heights[sx, sz];
                }
            }

            if (bestDistSq == float.MaxValue)
                return false;

            y = bestY;
            return true;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    public NpcAiController(
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        VNavmeshIpc vnavmeshIpc,
        IClientState clientState,
        Configuration config,
        IPluginLog log,
        Func<nint, bool>? isExternallyControlled = null)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.vnavmeshIpc = vnavmeshIpc;
        this.clientState = clientState;
        this.config = config;
        this.log = log;
        this.isExternallyControlled = isExternallyControlled ?? (_ => false);

        combatEngine.OnSimulationStarted += ScheduleAutoEngage;
        combatEngine.OnSimulationReset += OnSimulationResetOrStop;
    }

    private void ScheduleAutoEngage()
    {
        if (!config.EnableNpcAutoEngage) { pendingAutoEngageDelay = -1f; return; }
        pendingAutoEngageDelay = Math.Clamp(config.NpcAutoEngageDelay, 0f, 20f);
    }

    private void OnSimulationResetOrStop()
    {
        StopAllApproachMoveAnims();
        approachPaths.Clear();

        // OnSimulationReset fires from both StopSimulation and ResetState.
        // StopSimulation flips IsActive false before invoking — skip auto-engage
        // there since there are no selected NPCs left to engage.
        if (!combatEngine.State.IsActive) { pendingAutoEngageDelay = -1f; return; }
        ScheduleAutoEngage();
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> npcs)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        // Auto-engage countdown — when it expires, force every selected NPC
        // from Idle into Engaging so attacks start without player aggro.
        if (pendingAutoEngageDelay >= 0f)
        {
            pendingAutoEngageDelay -= deltaTime;
            if (pendingAutoEngageDelay <= 0f)
            {
                pendingAutoEngageDelay = -1f;
                foreach (var npc in npcs)
                    EngageNpc(npc);
            }
        }

        var playerPos = player.Position;
        var playerEntityId = player.EntityId;

        // Read the player's actual GameObjectId from the game object (includes Type byte)
        var playerGameObj = (GameObject*)player.Address;
        var playerGameObjectId = playerGameObj->GetGameObjectId();

        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned)
                continue;

            // Keep active targets in battle stance (drawn weapon / combat idle)
            if (npc.BattleChara != null && npc.AiState != NpcAiState.Dead)
                animationController.SetBattleStance(npc);

            // Set NPC's target to the player (client-side only).
            // NPCs target the player during combat AND when player is dead (standing over corpse).
            // Only clear when NPC itself is dead or resetting.
            if (config.EnableNpcTargetPlayer && npc.BattleChara != null)
            {
                var character = (Character*)npc.BattleChara;
                bool shouldTarget = npc.AiState != NpcAiState.Dead
                                 && npc.AiState != NpcAiState.Resetting;
                if (shouldTarget)
                    character->TargetId = playerGameObjectId;
                else if (character->TargetId.ObjectId == playerEntityId)
                    character->TargetId = default;
            }

            TickNpc(npc, deltaTime, playerPos, playerEntityId);
        }

        // Target approach: move active targets near the player
        if (config.EnableTargetApproach)
        {
            if (config.UseVNavmeshTargetApproach)
                vnavmeshIpc.RefreshStatus();

            // Count live approach-eligible NPCs for spread calculation
            var approachNpcs = new List<SimulatedNpc>();
            foreach (var npc in npcs)
            {
                if (!npc.IsSpawned || npc.BattleChara == null)
                    continue;
                if (isExternallyControlled(npc.Address))
                    continue;
                if (npc.AiState != NpcAiState.Dead)
                    approachNpcs.Add(npc);
            }

            var liveAddresses = new HashSet<nint>(approachNpcs.Select(n => n.Address));
            foreach (var address in approachPaths.Keys.ToList())
            {
                if (!liveAddresses.Contains(address))
                {
                    StopApproachMoveAnim(address, approachPaths[address]);
                    approachPaths.Remove(address);
                }
            }

            var terrainCache = config.UseVNavmeshTargetApproach
                ? BuildApproachTerrainCache(playerPos, approachNpcs)
                : null;

            for (int i = 0; i < approachNpcs.Count; i++)
            {
                var npc = approachNpcs[i];

                // Register NPC in the SetPosition block list so server can't override us
                if (!npc.IsClientControlled)
                    movementBlockHook.AddApproachNpc(npc.Address);

                // Prevent Resetting state when approach is active
                if (npc.AiState == NpcAiState.Resetting)
                    npc.AiState = npc.State.IsAlive ? NpcAiState.Idle : NpcAiState.Dead;

                TickApproach(npc, deltaTime, playerPos, i, approachNpcs.Count, terrainCache);
            }
        }
        else
        {
            // Clear all approach blocks when feature is disabled
            movementBlockHook.ClearApproachNpcs();
            StopAllApproachMoveAnims();
            approachPaths.Clear();
        }
    }

    /// <summary>
    /// Clear approach state. Called on simulation stop.
    /// </summary>
    public void ClearApproachState()
    {
        movementBlockHook.ClearApproachNpcs();
        StopAllApproachMoveAnims();
        approachPaths.Clear();
    }

    public void EngageNpc(SimulatedNpc npc)
    {
        if (npc.AiState == NpcAiState.Dead || npc.AiState == NpcAiState.Resetting)
            return;

        if (npc.AiState == NpcAiState.Idle)
        {
            npc.AiState = NpcAiState.Engaging;
            StaggerTimers(npc);
            log.Verbose($"NPC '{npc.Name}' engaging (auto-attack in {npc.AutoAttackTimer:F1}s).");
        }
    }

    /// <summary>
    /// Randomize initial auto-attack and skill timers so NPCs don't all attack on the same frame.
    /// Call this whenever an NPC first enters combat.
    /// </summary>
    public static void StaggerTimers(SimulatedNpc npc)
    {
        npc.AutoAttackTimer = Random.Shared.NextSingle() * npc.Behavior.AutoAttackDelay;
        foreach (var skill in npc.Behavior.Skills)
            skill.CooldownRemaining = Random.Shared.NextSingle() * skill.Cooldown;
    }

    private void TickNpc(SimulatedNpc npc, float deltaTime, Vector3 playerPos, uint playerEntityId)
    {
        // Tick skill cooldowns
        foreach (var skill in npc.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0)
                skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - deltaTime);
        }

        // Check if NPC should be dead
        if (!npc.State.IsAlive && npc.AiState != NpcAiState.Dead)
        {
            npc.AiState = NpcAiState.Dead;
            npc.DeadTimer = 0;
            return;
        }

        var npcPos = GetNpcPosition(npc);
        float distToPlayer = Vector3.Distance(npcPos, playerPos);

        switch (npc.AiState)
        {
            case NpcAiState.Idle:
                break;

            case NpcAiState.Engaging:
                if (npc.IsClientControlled)
                    RotateTowardPlayer(npc, playerPos, deltaTime);
                if (npc.EngageDelayTimer > 0)
                {
                    npc.EngageDelayTimer = Math.Max(0, npc.EngageDelayTimer - deltaTime);
                    break;
                }
                npc.AiState = NpcAiState.Combat;
                break;

            case NpcAiState.Combat:
                TickCombat(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                break;

            case NpcAiState.Chasing:
                if (npc.IsClientControlled)
                    TickChasing(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                else
                    // Real NPCs don't chase — just keep trying combat from current pos
                    TickCombat(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                break;

            case NpcAiState.Dead:
                npc.DeadTimer += deltaTime;
                break;

            case NpcAiState.Resetting:
                if (npc.IsClientControlled)
                    TickResetting(npc, deltaTime, npcPos);
                else
                {
                    // Real NPCs don't reset — just go idle and restore HP
                    npc.State.CurrentHp = npc.State.MaxHp;
                    npc.AiState = NpcAiState.Idle;
                }
                break;
        }
    }

    private void TickCombat(
        SimulatedNpc npc, float deltaTime,
        Vector3 playerPos, uint playerEntityId,
        Vector3 npcPos, float distToPlayer)
    {
        // Only rotate client-controlled NPCs
        if (npc.IsClientControlled)
            RotateTowardPlayer(npc, playerPos, deltaTime);

        // Skip leash check for real NPCs (they don't move) and when approach is active
        if (npc.IsClientControlled && !config.EnableTargetApproach)
        {
            float distFromSpawn = Vector3.Distance(npcPos, npc.SpawnPosition);
            if (distFromSpawn > npc.Behavior.LeashDistance)
            {
                npc.AiState = NpcAiState.Resetting;
                combatEngine.AddLogEntry($"{npc.Name} is resetting.", CombatLogType.Info);
                return;
            }
        }

        // For real NPCs: use a generous range since we can't move them
        float effectiveRange = npc.IsClientControlled
            ? npc.Behavior.AutoAttackRange + 1.0f
            : npc.Behavior.AutoAttackRange + 30.0f; // Large range for real NPCs

        if (distToPlayer > effectiveRange)
        {
            if (npc.IsClientControlled)
                npc.AiState = NpcAiState.Chasing;
            // Real NPCs just wait
            return;
        }

        // Check if player is alive — NPCs stop attacking when player is dead
        if (!combatEngine.State.PlayerState.IsAlive)
        {
            npc.AiState = NpcAiState.Idle;
            return;
        }

        // Handle casting
        if (npc.State.IsCasting)
        {
            npc.State.CastTimeElapsed += deltaTime;
            if (npc.State.CastTimeElapsed >= npc.State.CastTimeTotal)
            {
                npc.State.IsCasting = false;
                if (npc.CurrentCastSkill != null)
                {
                    combatEngine.ProcessNpcAction(npc, npc.CurrentCastSkill.ActionId,
                        playerEntityId, npc.CurrentCastSkill.Potency, npc.CurrentCastSkill.AttackStyle);
                    npc.CurrentCastSkill.CooldownRemaining = npc.CurrentCastSkill.Cooldown;
                    npc.CurrentCastSkill = null;
                }
            }
            return;
        }

        // Check animation lock
        if (npc.State.AnimationLock > 0)
        {
            npc.State.AnimationLock -= deltaTime;
            return;
        }

        // Try skills
        float hpPercent = (float)npc.State.CurrentHp / npc.State.MaxHp;
        foreach (var skill in npc.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (skill.CooldownRemaining > 0)
                continue;
            // For real NPCs, skip range check (generous range already checked above)
            if (npc.IsClientControlled && distToPlayer > skill.Range)
                continue;
            if (hpPercent > skill.HpThreshold)
                continue;

            if (skill.CastTime > 0)
            {
                npc.State.IsCasting = true;
                npc.State.CastActionId = skill.ActionId;
                npc.State.CastTimeTotal = skill.CastTime;
                npc.State.CastTimeElapsed = 0;
                npc.State.CastTargetId = playerEntityId;
                npc.CurrentCastSkill = skill;

                combatEngine.AddLogEntry(
                    $"{npc.Name} begins casting {skill.Name}...",
                    CombatLogType.Info);
            }
            else
            {
                combatEngine.ProcessNpcAction(npc, skill.ActionId, playerEntityId, skill.Potency, skill.AttackStyle);
                skill.CooldownRemaining = skill.Cooldown;
                npc.State.AnimationLock = 0.6f;
            }
            return;
        }

        // Auto-attack
        npc.AutoAttackTimer -= deltaTime;
        if (npc.AutoAttackTimer <= 0)
        {
            npc.AutoAttackTimer = npc.Behavior.AutoAttackDelay;
            combatEngine.ProcessNpcAction(npc, npc.Behavior.AutoAttackActionId,
                playerEntityId, npc.Behavior.AutoAttackPotency, npc.Behavior.AutoAttackStyle);
            npc.State.AnimationLock = 0.6f;
        }
    }

    private void TickChasing(
        SimulatedNpc npc, float deltaTime,
        Vector3 playerPos, uint playerEntityId,
        Vector3 npcPos, float distToPlayer)
    {
        RotateTowardPlayer(npc, playerPos, deltaTime);

        var direction = Vector3.Normalize(playerPos - npcPos);
        var newPos = npcPos + direction * npc.Behavior.MoveSpeed * deltaTime;
        SetNpcPosition(npc, newPos);

        float newDist = Vector3.Distance(newPos, playerPos);
        if (newDist <= npc.Behavior.AutoAttackRange)
            npc.AiState = NpcAiState.Combat;

        float distFromSpawn = Vector3.Distance(newPos, npc.SpawnPosition);
        if (distFromSpawn > npc.Behavior.LeashDistance)
        {
            npc.AiState = NpcAiState.Resetting;
            combatEngine.AddLogEntry($"{npc.Name} is resetting.", CombatLogType.Info);
        }
    }

    private void TickResetting(SimulatedNpc npc, float deltaTime, Vector3 npcPos)
    {
        var direction = Vector3.Normalize(npc.SpawnPosition - npcPos);
        var newPos = npcPos + direction * npc.Behavior.MoveSpeed * 1.5f * deltaTime;
        SetNpcPosition(npc, newPos);

        npc.State.CurrentHp = npc.State.MaxHp;

        if (Vector3.Distance(newPos, npc.SpawnPosition) < 0.5f)
        {
            SetNpcPosition(npc, npc.SpawnPosition);
            npc.AiState = NpcAiState.Idle;

            if (npc.BattleChara != null)
            {
                var character = (Character*)npc.BattleChara;
                character->Mode = CharacterModes.Normal;
            }

            combatEngine.AddLogEntry($"{npc.Name} has reset.", CombatLogType.Info);
        }
    }

    private void RotateTowardPlayer(SimulatedNpc npc, Vector3 playerPos, float deltaTime)
    {
        if (npc.BattleChara == null || !npc.IsClientControlled)
            return;

        var gameObj = (GameObject*)npc.BattleChara;
        var npcPos = (System.Numerics.Vector3)gameObj->Position;
        var dir = new System.Numerics.Vector3(playerPos.X - npcPos.X, playerPos.Y - npcPos.Y, playerPos.Z - npcPos.Z);
        var targetRot = MathF.Atan2(dir.X, dir.Z);

        var currentRot = gameObj->Rotation;
        var diff = targetRot - currentRot;

        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotSpeed = 5.0f * deltaTime;
        if (MathF.Abs(diff) < rotSpeed)
            gameObj->Rotation = targetRot;
        else
            gameObj->Rotation = currentRot + MathF.Sign(diff) * rotSpeed;
    }

    private Vector3 GetNpcPosition(SimulatedNpc npc)
    {
        // For real NPCs, read position from managed reference (safer)
        if (!npc.IsClientControlled && npc.GameObjectRef != null)
        {
            try { return npc.GameObjectRef.Position; }
            catch { /* fall through */ }
        }

        if (npc.BattleChara != null)
        {
            var gameObj = (GameObject*)npc.BattleChara;
            return gameObj->Position;
        }
        return npc.SpawnPosition;
    }

    private void SetNpcPosition(SimulatedNpc npc, Vector3 position)
    {
        if (npc.BattleChara == null || !npc.IsClientControlled)
            return;

        var gameObj = (GameObject*)npc.BattleChara;
        gameObj->Position = position;
    }

    /// <summary>
    /// Move an active target NPC toward the player, stopping at the configured distance.
    /// Each NPC gets a unique angular slot around the player so they spread out naturally.
    /// Works for both real and client-controlled NPCs.
    /// </summary>
    private void TickApproach(
        SimulatedNpc npc,
        float deltaTime,
        Vector3 playerPos,
        int npcIndex,
        int totalNpcs,
        ApproachTerrainCache? terrainCache)
    {
        if (npc.BattleChara == null) return;
        if (npc.AiState == NpcAiState.Dead)
        {
            StopApproachMoveAnim(npc);
            return;
        }

        // Don't move NPCs when the player is dead — they stay in place
        if (!combatEngine.State.PlayerState.IsAlive)
        {
            StopApproachMoveAnim(npc);
            return;
        }

        var gameObj = (GameObject*)npc.BattleChara;
        var npcPos = (Vector3)gameObj->Position;

        float distToPlayer = Vector3.Distance(npcPos, playerPos);
        float targetDist = config.TargetApproachDistance;

        // Calculate the angular slot for this NPC so they spread around the player
        Vector3 targetPos;
        if (totalNpcs <= 1)
        {
            // Single NPC: approach from its current direction
            if (distToPlayer < 0.1f)
            {
                var player = Core.Services.ObjectTable.LocalPlayer;
                float playerRot = player?.Rotation ?? 0;
                var forward = new Vector3(-MathF.Sin(playerRot), 0, -MathF.Cos(playerRot));
                targetPos = playerPos + forward * targetDist;
            }
            else
            {
                var dirFromPlayer = (npcPos - playerPos) / distToPlayer;
                targetPos = playerPos + dirFromPlayer * targetDist;
            }
        }
        else
        {
            // Multiple NPCs: distribute evenly in front of the player
            // In FFXIV, playerRot points forward; NPC at angle playerRot is in front of the player
            var player = Core.Services.ObjectTable.LocalPlayer;
            float playerRot = player?.Rotation ?? 0;

            // The center of the arc is the player's forward direction
            // Index 0 is always dead center (directly facing the player)
            float arcSpan = MathF.PI * 4f / 3f; // 240 degrees
            float angleStep = totalNpcs > 1 ? arcSpan / (totalNpcs - 1) : 0;
            float startAngle = playerRot - arcSpan / 2f;
            float angle = startAngle + angleStep * npcIndex;

            var dir = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
            targetPos = playerPos + dir * targetDist;
        }

        // Approximate terrain following: use player Y, with the configured
        // height offset stacked on top. The offset lives on the approach
        // logic because approach is the only flow that writes Y every frame —
        // raw direct-Y writes outside this flow fight the game's own position
        // updates and break the rest of approach movement.
        targetPos.Y = playerPos.Y + config.DefaultNpcHeightOffset;

        // Already close enough — just face the player
        var moveTarget = targetPos;
        var hasVnavmeshTarget = TryUpdateVNavmeshPath(npc, deltaTime, npcPos, targetPos, out var pathTarget);
        if (hasVnavmeshTarget)
            moveTarget = pathTarget;

        if (config.UseVNavmeshTargetApproach && !hasVnavmeshTarget)
        {
            if (terrainCache != null && approachPaths.TryGetValue(npc.Address, out var noPathState))
                CorrectStableRootHeight(gameObj, npcPos, terrainCache, noPathState, deltaTime);

            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, playerPos, deltaTime);
            return;
        }

        if (Vector3.Distance(npcPos, moveTarget) <= 0.3f)
        {
            if (hasVnavmeshTarget && terrainCache != null &&
                approachPaths.TryGetValue(npc.Address, out var arrivedPathState))
                CorrectStableRootHeight(gameObj, npcPos, terrainCache, arrivedPathState, deltaTime);

            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, playerPos, deltaTime);
            return;
        }

        // Smooth movement toward the target position
        float speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        float remainingDist = Vector3.Distance(npcPos, moveTarget);
        float moveDist = speed * deltaTime;

        Vector3 newPos;
        if (remainingDist <= moveDist)
        {
            newPos = moveTarget;
        }
        else
        {
            var moveDir = Vector3.Normalize(moveTarget - npcPos);
            newPos = npcPos + moveDir * moveDist;
        }

        if (hasVnavmeshTarget && terrainCache != null &&
            approachPaths.TryGetValue(npc.Address, out var pathState))
        {
            newPos = CorrectMovingRootHeight(newPos, terrainCache, pathState, deltaTime);
        }

        // Call the real SetPosition via the hook bypass — this updates both
        // the struct field AND the DrawObject (3D model position).
        StartApproachMoveAnim(npc);
        movementBlockHook.SetApproachPosition(gameObj, newPos.X, newPos.Y, newPos.Z);

        // Face the player
        ForceRotateToward(npc, playerPos, deltaTime);
    }

    private bool TryUpdateVNavmeshPath(
        SimulatedNpc npc,
        float deltaTime,
        Vector3 npcPos,
        Vector3 targetPos,
        out Vector3 moveTarget)
    {
        moveTarget = targetPos;

        if (!config.UseVNavmeshTargetApproach)
        {
            approachPaths.Remove(npc.Address);
            return false;
        }

        if (!vnavmeshIpc.CanPathfind)
            return false;

        if (!approachPaths.TryGetValue(npc.Address, out var state))
        {
            state = new ApproachPathState();
            approachPaths[npc.Address] = state;
        }
        state.ConfiguredHeightOffset = config.DefaultNpcHeightOffset;

        state.RepathTimer = Math.Max(0, state.RepathTimer - deltaTime);

        if (state.PendingPath != null && state.PendingPath.IsCompleted)
        {
            try
            {
                state.Waypoints = state.PendingPath.GetAwaiter().GetResult();
                state.WaypointIndex = 0;
                state.RequestedTarget = state.PendingTarget;
                if (!state.HasFloorYOffset)
                {
                    var floor = SnapToNavmesh(npcPos);
                    state.FloorYOffset = floor.HasValue ? npcPos.Y - floor.Value.Y : 0f;
                    state.HasFloorYOffset = true;
                    state.HasLastFloorY = false;
                    state.LastCorrectedWaypointIndex = -1;
                    state.FloorResnapTimer = 0;
                }
                state.ConfiguredHeightOffset = config.DefaultNpcHeightOffset;
                state.LastError = "";
                state.HasLastMoveRootY = false;
            }
            catch (Exception ex)
            {
                log.Verbose($"vnavmesh path request failed for '{npc.Name}': {ex.Message}");
                state.Waypoints.Clear();
                state.WaypointIndex = 0;
                state.LastError = ex.Message;
            }
            finally
            {
                state.PendingPath = null;
            }
        }

        var targetMoved = state.Waypoints.Count == 0 ||
                          Vector3.Distance(state.RequestedTarget, targetPos) >= VNavmeshRepathDistance;

        if (targetMoved && state.PendingPath == null && state.RepathTimer <= 0)
        {
            try
            {
                var from = SnapToNavmesh(npcPos) ?? npcPos;
                var to = SnapToNavmesh(targetPos) ?? targetPos;

                state.PendingTarget = to;
                state.PendingPath = vnavmeshIpc.Pathfind(from, to, VNavmeshPathTolerance);
                state.RepathTimer = VNavmeshRepathInterval;
            }
            catch (Exception ex)
            {
                log.Verbose($"vnavmesh unavailable while requesting path for '{npc.Name}': {ex.Message}");
                state.PendingPath = null;
                state.LastError = ex.Message;
                return false;
            }
        }

        while (state.WaypointIndex < state.Waypoints.Count &&
               Vector3.Distance(npcPos, ApplyFloorYOffset(state, state.Waypoints[state.WaypointIndex])) <= 0.45f)
        {
            state.WaypointIndex++;
        }

        if (state.WaypointIndex >= state.Waypoints.Count)
            return false;

        moveTarget = ApplyFloorYOffset(state, state.Waypoints[state.WaypointIndex]);
        moveTarget = CorrectMoveTargetFloor(state, moveTarget, deltaTime);
        return true;
    }

    private static Vector3 ApplyFloorYOffset(ApproachPathState state, Vector3 point)
    {
        return state.HasFloorYOffset
            ? point with { Y = point.Y + state.FloorYOffset + state.ConfiguredHeightOffset }
            : point;
    }

    private Vector3 CorrectMoveTargetFloor(ApproachPathState state, Vector3 moveTarget, float deltaTime)
    {
        if (!state.HasFloorYOffset)
            return moveTarget;

        state.FloorResnapTimer = Math.Max(0, state.FloorResnapTimer - deltaTime);
        var waypointChanged = state.LastCorrectedWaypointIndex != state.WaypointIndex;

        if (waypointChanged || !state.HasLastFloorY || state.FloorResnapTimer <= 0)
        {
            var floor = SnapToNavmesh(moveTarget);
            if (floor.HasValue)
            {
                state.LastFloorY = floor.Value.Y;
                state.HasLastFloorY = true;
                state.LastCorrectedWaypointIndex = state.WaypointIndex;
                state.FloorResnapTimer = VNavmeshFloorResnapInterval;
            }
        }

        return state.HasLastFloorY
            ? moveTarget with { Y = state.LastFloorY + state.FloorYOffset + state.ConfiguredHeightOffset }
            : moveTarget;
    }

    private Vector3? SnapToNavmesh(Vector3 point)
    {
        try
        {
            return vnavmeshIpc.NearestPointReachable(point)
                   ?? vnavmeshIpc.PointOnFloor(point + new Vector3(0, 10f, 0));
        }
        catch (Exception ex)
        {
            log.Verbose($"vnavmesh snap failed: {ex.Message}");
            return null;
        }
    }

    private ApproachTerrainCache? BuildApproachTerrainCache(Vector3 playerPos, IReadOnlyList<SimulatedNpc> npcs)
    {
        if (npcs.Count == 0)
            return null;

        var minX = playerPos.X - config.TargetApproachDistance - 2f;
        var maxX = playerPos.X + config.TargetApproachDistance + 2f;
        var minZ = playerPos.Z - config.TargetApproachDistance - 2f;
        var maxZ = playerPos.Z + config.TargetApproachDistance + 2f;
        var maxY = playerPos.Y;

        foreach (var npc in npcs)
        {
            if (npc.BattleChara == null)
                continue;

            var pos = (Vector3)((GameObject*)npc.BattleChara)->Position;
            minX = MathF.Min(minX, pos.X - 2f);
            maxX = MathF.Max(maxX, pos.X + 2f);
            minZ = MathF.Min(minZ, pos.Z - 2f);
            maxZ = MathF.Max(maxZ, pos.Z + 2f);
            maxY = MathF.Max(maxY, pos.Y);
        }

        var widthWorld = MathF.Max(maxX - minX, TerrainGridStep);
        var depthWorld = MathF.Max(maxZ - minZ, TerrainGridStep);
        var step = MathF.Max(TerrainGridStep,
            MathF.Max(widthWorld, depthWorld) / MathF.Max(1, TerrainGridMaxSize - 1));
        var width = Math.Clamp((int)MathF.Ceiling(widthWorld / step) + 1, 2, TerrainGridMaxSize);
        var depth = Math.Clamp((int)MathF.Ceiling(depthWorld / step) + 1, 2, TerrainGridMaxSize);

        var heights = new float[width, depth];
        var valid = new bool[width, depth];
        var originY = maxY + 10f;
        const float rayDistance = 80f;

        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            var wx = minX + x * step;
            var wz = minZ + z * step;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(wx, originY, wz),
                    new Vector3(0, -1, 0),
                    out var hit,
                    rayDistance))
            {
                heights[x, z] = hit.Point.Y;
                valid[x, z] = true;
            }
        }

        return new ApproachTerrainCache
        {
            OriginX = minX,
            OriginZ = minZ,
            Step = step,
            Width = width,
            Depth = depth,
            Heights = heights,
            Valid = valid,
        };
    }

    private Vector3 CorrectMovingRootHeight(
        Vector3 rootPosition,
        ApproachTerrainCache terrainCache,
        ApproachPathState state,
        float deltaTime)
    {
        if (!terrainCache.TrySample(rootPosition.X, rootPosition.Z, out var terrainY))
            return rootPosition;

        if (!state.HasStableRootTerrainClearance)
        {
            state.StableRootTerrainClearance = rootPosition.Y - terrainY - config.DefaultNpcHeightOffset;
            state.HasStableRootTerrainClearance = true;
        }

        var desiredY = terrainY + state.StableRootTerrainClearance + config.DefaultNpcHeightOffset;
        var fromY = state.HasLastMoveRootY ? state.LastMoveRootY : rootPosition.Y;
        var maxStep = MathF.Max(0.03f, 6.0f * deltaTime);
        var deltaY = Math.Clamp(desiredY - fromY, -maxStep, maxStep);
        var y = fromY + deltaY;

        state.LastMoveRootY = y;
        state.HasLastMoveRootY = true;
        return rootPosition with { Y = y };
    }

    private void CorrectStableRootHeight(
        GameObject* gameObj,
        Vector3 rootPosition,
        ApproachTerrainCache terrainCache,
        ApproachPathState state,
        float deltaTime)
    {
        var corrected = CorrectMovingRootHeight(rootPosition, terrainCache, state, deltaTime);
        if (MathF.Abs(corrected.Y - rootPosition.Y) < 0.001f)
            return;

        movementBlockHook.SetApproachPosition(gameObj, corrected.X, corrected.Y, corrected.Z);
    }

    private void StartApproachMoveAnim(SimulatedNpc npc)
    {
        if (npc.BattleChara == null)
            return;

        if (!approachPaths.TryGetValue(npc.Address, out var state))
        {
            state = new ApproachPathState();
            approachPaths[npc.Address] = state;
        }

        if (state.MoveAnimActive)
            return;

        var character = (Character*)npc.BattleChara;
        character->Timeline.BaseOverride = NormalRunTimelineId;
        if (character->Timeline.TimelineSequencer.Parent != null)
            character->Timeline.PlayActionTimeline(NormalRunTimelineId);
        state.MoveAnimActive = true;
    }

    private void StopApproachMoveAnim(SimulatedNpc npc)
    {
        if (!approachPaths.TryGetValue(npc.Address, out var state))
            return;

        StopApproachMoveAnim(npc.Address, state);
    }

    private void StopApproachMoveAnim(nint address, ApproachPathState state)
    {
        if (!state.MoveAnimActive)
            return;

        try
        {
            var character = (Character*)address;
            character->Timeline.BaseOverride = 0;
        }
        catch { }

        state.MoveAnimActive = false;
    }

    private void StopAllApproachMoveAnims()
    {
        foreach (var pair in approachPaths)
            StopApproachMoveAnim(pair.Key, pair.Value);
    }

    /// <summary>
    /// Rotate any NPC to face a target position, regardless of IsClientControlled.
    /// Uses SetApproachRotation to properly update the DrawObject.
    /// </summary>
    private void ForceRotateToward(SimulatedNpc npc, Vector3 targetPos, float deltaTime)
    {
        if (npc.BattleChara == null) return;

        var gameObj = (GameObject*)npc.BattleChara;
        var npcPos = (Vector3)gameObj->Position;
        var dir = targetPos - npcPos;

        if (dir.LengthSquared() < 0.001f) return;

        var targetRot = MathF.Atan2(dir.X, dir.Z);
        var currentRot = gameObj->Rotation;
        var diff = targetRot - currentRot;

        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotSpeed = 5.0f * deltaTime;
        float newRot;
        if (MathF.Abs(diff) < rotSpeed)
            newRot = targetRot;
        else
            newRot = currentRot + MathF.Sign(diff) * rotSpeed;

        movementBlockHook.SetApproachRotation(gameObj, newRot);
    }

    public void Dispose()
    {
        combatEngine.OnSimulationStarted -= ScheduleAutoEngage;
        combatEngine.OnSimulationReset -= OnSimulationResetOrStop;
        movementBlockHook.ClearApproachNpcs();
        StopAllApproachMoveAnims();
        approachPaths.Clear();
    }
}
