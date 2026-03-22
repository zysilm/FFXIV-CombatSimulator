using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Ai;

public unsafe class NpcAiController : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly IClientState clientState;
    private readonly Configuration config;
    private readonly IPluginLog log;

    public NpcAiController(
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        IClientState clientState,
        Configuration config,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.clientState = clientState;
        this.config = config;
        this.log = log;
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> npcs)
    {
        var player = clientState.LocalPlayer;
        if (player == null)
            return;

        var playerPos = player.Position;
        var playerEntityId = player.EntityId;

        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned)
                continue;

            // Keep active targets in battle stance (drawn weapon / combat idle)
            if (npc.BattleChara != null && (config.EnableBrutal || npc.AiState != NpcAiState.Dead))
                animationController.SetBattleStance(npc);

            TickNpc(npc, deltaTime, playerPos, playerEntityId);
        }

        // Target approach: move active targets near the player
        if (config.EnableTargetApproach)
        {
            // Count live approach-eligible NPCs for spread calculation
            var approachNpcs = new List<SimulatedNpc>();
            foreach (var npc in npcs)
            {
                if (!npc.IsSpawned || npc.BattleChara == null)
                    continue;
                if (config.EnableBrutal || npc.AiState != NpcAiState.Dead)
                    approachNpcs.Add(npc);
            }

            for (int i = 0; i < approachNpcs.Count; i++)
            {
                var npc = approachNpcs[i];

                // Register NPC in the SetPosition block list so server can't override us
                if (!npc.IsClientControlled)
                    movementBlockHook.AddApproachNpc(npc.Address);

                // Prevent Resetting state when approach is active
                if (npc.AiState == NpcAiState.Resetting)
                    npc.AiState = npc.State.IsAlive ? NpcAiState.Idle : NpcAiState.Dead;

                TickApproach(npc, deltaTime, playerPos, i, approachNpcs.Count);
            }
        }
        else
        {
            // Clear all approach blocks when feature is disabled
            movementBlockHook.ClearApproachNpcs();
        }
    }

    /// <summary>
    /// Clear approach state. Called on simulation stop.
    /// </summary>
    public void ClearApproachState()
    {
        movementBlockHook.ClearApproachNpcs();
    }

    public void EngageNpc(SimulatedNpc npc)
    {
        if (npc.AiState == NpcAiState.Dead || npc.AiState == NpcAiState.Resetting)
            return;

        if (npc.AiState == NpcAiState.Idle)
        {
            npc.AiState = NpcAiState.Engaging;
            log.Verbose($"NPC '{npc.Name}' engaging.");
        }
    }

    private void TickNpc(SimulatedNpc npc, float deltaTime, Vector3 playerPos, uint playerEntityId)
    {
        // Tick skill cooldowns
        foreach (var skill in npc.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0)
                skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - deltaTime);
        }

        // Check if NPC should be dead (skip in brutal mode — dead NPCs keep fighting)
        if (!config.EnableBrutal && !npc.State.IsAlive && npc.AiState != NpcAiState.Dead)
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

        // Check if player is alive (skip in brutal mode)
        if (!config.EnableBrutal && !combatEngine.State.PlayerState.IsAlive)
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
                        playerEntityId, npc.CurrentCastSkill.Potency);
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
                combatEngine.ProcessNpcAction(npc, skill.ActionId, playerEntityId, skill.Potency);
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
                playerEntityId, npc.Behavior.AutoAttackPotency);
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
    private void TickApproach(SimulatedNpc npc, float deltaTime, Vector3 playerPos, int npcIndex, int totalNpcs)
    {
        if (npc.BattleChara == null) return;
        if (!config.EnableBrutal && npc.AiState == NpcAiState.Dead) return;

        // Don't move NPCs when the player is dead — they stay in place (skip in brutal)
        if (!config.EnableBrutal && !combatEngine.State.PlayerState.IsAlive) return;

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
                var player = clientState.LocalPlayer;
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
            var player = clientState.LocalPlayer;
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

        // Approximate terrain following: use player Y
        targetPos.Y = playerPos.Y;

        // Already close enough — just face the player
        if (Vector3.Distance(npcPos, targetPos) <= 0.3f)
        {
            ForceRotateToward(npc, playerPos, deltaTime);
            return;
        }

        // Smooth movement toward the target position
        float speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        float remainingDist = Vector3.Distance(npcPos, targetPos);
        float moveDist = speed * deltaTime;

        Vector3 newPos;
        if (remainingDist <= moveDist)
        {
            newPos = targetPos;
        }
        else
        {
            var moveDir = Vector3.Normalize(targetPos - npcPos);
            newPos = npcPos + moveDir * moveDist;
        }

        // Call the real SetPosition via the hook bypass — this updates both
        // the struct field AND the DrawObject (3D model position).
        movementBlockHook.SetApproachPosition(gameObj, newPos.X, newPos.Y, newPos.Z);

        // Face the player
        ForceRotateToward(npc, playerPos, deltaTime);
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
        movementBlockHook.ClearApproachNpcs();
    }
}
