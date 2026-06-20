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
    private readonly PartyEngagePlanner partyEngagePlanner;
    private readonly TerrainHeightService terrainHeightService;
    private readonly IPluginLog log;
    private readonly CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter;
    private readonly Func<nint, bool> isExternallyControlled;
    private readonly Dictionary<nint, ApproachPathState> approachPaths = new();
    private readonly Dictionary<uint, float> partyApproachDebugNextLogAt = new();
    private readonly Dictionary<uint, string> lastApproachDebugRoute = new();
    // Per-enemy locked standing position (world-space). While far, an enemy walks
    // straight in toward the player; once it reaches the keep-distance ring it locks
    // a fixed world spot (de-stacked) and holds it — it does NOT glide along with
    // the player. The lock only releases when the player wanders far enough away
    // that the enemy must walk in again.
    private readonly Dictionary<nint, Vector3> approachLockedGoals = new();
    private const float VNavmeshRepathDistance = 1.5f;
    private const float VNavmeshRepathInterval = 1.0f;
    private const float VNavmeshPathTolerance = 0.5f;
    private const float VNavmeshFloorResnapInterval = 0.25f;
    private const float VNavmeshWaypointReachDistance = 0.45f;
    private const float VNavmeshLookaheadDistance = 1.25f;
    private const float PartyMeleeAttackRangeBuffer = 0.25f;
    private const float PartyAttackRangeHysteresis = 0.4f;
    // Caster/magic enemies auto-attack at melee reach (their spells are ranged, the basic swing is not).
    private const float MagicAutoAttackMeleeRange = 3.5f;
    // Dynamic positioning: dwell damps intent flicker (front/back thrash).
    private const float IntentMinDwell = 0.6f;
    // Below this fraction of the desired ranged hold, a ranged-intent enemy drifts back out.
    private const float RangedBackoffFraction = 0.6f;
    private const float PartyApproachMovementEpsilon = 0.02f;
    private const float PartyApproachDebugInterval = 0.5f;
    // Low-pass time constant (seconds) for the steered enemy approach heading.
    // The per-frame steered direction can flip when enemies crowd a shared
    // target; blending toward it filters that jitter while still turning within
    // a few frames. Smaller = snappier/jitterier, larger = smoother/laggier.
    private const float ApproachHeadingSmoothingTau = 0.16f;
    // Within (approach distance + lock buffer) an enemy locks its angle and stops;
    // it only unlocks to re-approach if pushed beyond (approach distance + unlock).
    private const float ApproachLockBuffer = 1.5f;
    private const float ApproachUnlockBuffer = 1.5f;

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
        public ActorVisualState VisualState { get; set; } = new();
        public float StableRootTerrainClearance { get; set; }
        public bool HasStableRootTerrainClearance { get; set; }
        public float LastMoveRootY { get; set; }
        public bool HasLastMoveRootY { get; set; }
        public uint TargetEntityId { get; set; }
        public Vector3 SmoothedMoveDir { get; set; }
        public bool HasSmoothedMoveDir { get; set; }
        public bool WasHoldingRange { get; set; }
    }

    public NpcAiController(
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        VNavmeshIpc vnavmeshIpc,
        IClientState clientState,
        Configuration config,
        PartyEngagePlanner partyEngagePlanner,
        TerrainHeightService terrainHeightService,
        IPluginLog log,
        CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter,
        Func<nint, bool>? isExternallyControlled = null)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.vnavmeshIpc = vnavmeshIpc;
        this.clientState = clientState;
        this.config = config;
        this.partyEngagePlanner = partyEngagePlanner;
        this.terrainHeightService = terrainHeightService;
        this.log = log;
        this.combatModeRouter = combatModeRouter;
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
        approachLockedGoals.Clear();
        partyApproachDebugNextLogAt.Clear();
        lastApproachDebugRoute.Clear();

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

        var partyMode = config.EnableCombatCompanions;
        var hasLivingCompanions = combatEngine.HasLivingCompanions?.Invoke() == true;

        // Party mode is controlled by the companion feature toggle. When it is
        // active, all enemies join combat so party formation/targeting can run
        // even before companion clones are spawned.
        if (partyMode)
        {
            foreach (var npc in npcs)
                EngageNpc(npc);
        }

        var playerPos = player.Position;
        var playerEntityId = player.EntityId;

        // Read the player's actual GameObjectId from the game object (includes Type byte)
        var playerGameObj = (GameObject*)player.Address;
        var playerGameObjectId = playerGameObj->GetGameObjectId();
        var targetByNpcId = new Dictionary<uint, (SimulatedEntityState State, Vector3 Position)>();

        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned)
                continue;

            // Keep active targets in battle stance (drawn weapon / combat idle)
            if (npc.BattleChara != null && npc.AiState != NpcAiState.Dead)
                animationController.SetBattleStance(npc);

            var targetState = combatEngine.GetNpcTarget(npc);
            var targetPos = combatEngine.GetSimulatedEntityPosition(targetState);
            var targetEntityId = targetState.EntityId;
            targetByNpcId[npc.SimulatedEntityId] = (targetState, targetPos);

            // Expose "is this enemy attacking the player" for the combat-link overlay.
            npc.IsAttackingPlayer = npc.IsEngaged && npc.State.IsAlive && targetState.IsPlayer;

            // Set NPC's target to the player (client-side only).
            // NPCs target the player during combat AND when player is dead (standing over corpse).
            // Only clear when NPC itself is dead or resetting.
            if (config.EnableNpcTargetPlayer && npc.BattleChara != null && targetState.IsPlayer)
            {
                var character = (Character*)npc.BattleChara;
                bool shouldTarget = npc.AiState != NpcAiState.Dead
                                 && npc.AiState != NpcAiState.Resetting;
                if (shouldTarget)
                    character->TargetId = playerGameObjectId;
                else if (character->TargetId.ObjectId == playerEntityId)
                    character->TargetId = default;
            }
            else if (config.EnableNpcTargetPlayer && npc.BattleChara != null)
            {
                var character = (Character*)npc.BattleChara;
                if (character->TargetId.ObjectId == playerEntityId)
                    character->TargetId = default;
            }

            TickNpc(npc, deltaTime, targetPos, targetEntityId);
        }

        // In solo play this is controlled by EnableTargetApproach. In party
        // mode, enemy movement is always needed because real NPCs have no
        // natural client-side chase path in our simulation; TickApproach uses
        // the resolved combat target instead of the solo player ring.
        if (config.EnableTargetApproach || partyMode)
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
            foreach (var address in approachLockedGoals.Keys.ToList())
            {
                if (!liveAddresses.Contains(address))
                    approachLockedGoals.Remove(address);
            }

            var terrainCache = BuildTerrainHeightCache(playerPos, approachNpcs);

            for (int i = 0; i < approachNpcs.Count; i++)
            {
                var npc = approachNpcs[i];

                // Register NPC in the SetPosition block list so server can't override us
                if (!npc.IsClientControlled)
                    movementBlockHook.AddApproachNpc(npc.Address);

                // Prevent Resetting state when approach is active
                if (npc.AiState == NpcAiState.Resetting)
                    npc.AiState = npc.State.IsAlive ? NpcAiState.Idle : NpcAiState.Dead;

                if (!partyMode || (!hasLivingCompanions && config.UseSoloTargetFormationWhenNoCompanions))
                {
                    LogApproachRouteDebug(npc, "solo", partyMode, hasLivingCompanions);
                    TickSoloApproach(npc, deltaTime, playerPos, terrainCache);
                    continue;
                }

                if (!targetByNpcId.TryGetValue(npc.SimulatedEntityId, out var target))
                {
                    var state = combatEngine.GetNpcTarget(npc);
                    target = (state, combatEngine.GetSimulatedEntityPosition(state));
                }

                LogApproachRouteDebug(npc, "party", partyMode, hasLivingCompanions);
                TickPartyApproach(npc, deltaTime, playerPos, target.State, target.Position, terrainCache);
            }
        }
        else
        {
            // Clear all approach blocks when feature is disabled
            movementBlockHook.ClearApproachNpcs();
            StopAllApproachMoveAnims();
            approachPaths.Clear();
            approachLockedGoals.Clear();
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
        approachLockedGoals.Clear();
    }

    private void ClearPartyApproachState(SimulatedNpc npc)
    {
        StopApproachMoveAnim(npc);
        approachPaths.Remove(npc.Address);
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

        if (!npc.State.IsAlive)
        {
            ClearNpcActionState(npc);
            if (npc.AiState != NpcAiState.Dead)
            {
                npc.AiState = NpcAiState.Dead;
                npc.DeadTimer = 0;
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyDead((Character*)npc.BattleChara, npc.VisualState);
            }
            return;
        }

        if (npc.State.AnimationLock > 0)
            npc.State.AnimationLock = Math.Max(0, npc.State.AnimationLock - deltaTime);

        DiscardInvalidNpcActionState(npc);

        var npcPos = GetNpcPosition(npc);
        float distToPlayer = Vector3.Distance(npcPos, playerPos);

        switch (npc.AiState)
        {
            case NpcAiState.Idle:
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
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
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyDead((Character*)npc.BattleChara, npc.VisualState);
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

    // Action Mode initiates attacks faster (auto delay + skill CDs divided by this)
    // so enemies pressure the player at action-game pace instead of the ~3s tab-target cadence.
    private float ActionPace() => config.ActionMode ? MathF.Max(0.1f, config.ActionEnemyAttackSpeed) : 1f;

    private void TickCombat(
        SimulatedNpc npc, float deltaTime,
        Vector3 playerPos, uint playerEntityId,
        Vector3 npcPos, float distToPlayer)
    {
        var simulatedTarget = combatEngine.GetNpcTarget(npc);
        if (simulatedTarget == null || !simulatedTarget.IsAlive)
        {
            ClearNpcActionState(npc);
            npc.AiState = NpcAiState.Idle;
            return;
        }

        UpdateDesiredEngageRange(npc, deltaTime);

        var targetPos = combatEngine.GetSimulatedEntityPosition(simulatedTarget);
        var distToTarget = UsesPartyConfiguredRange(npc, simulatedTarget)
            ? FlatDistance(npcPos, targetPos)
            : Vector3.Distance(npcPos, targetPos);
        var targetEntityId = simulatedTarget.EntityId;

        if (npc.State.IsCasting && npc.State.CastTargetId != targetEntityId)
        {
            ClearNpcActionState(npc);
            if (npc.BattleChara != null)
                ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
        }

        // Only rotate client-controlled NPCs
        if (npc.IsClientControlled)
            ForceRotateToward(npc, targetPos, deltaTime);

        // Skip leash check for real NPCs (they don't move) and when formation approach is active.
        if (npc.IsClientControlled && !config.EnableTargetApproach && !config.EnableCombatCompanions)
        {
            float distFromSpawn = Vector3.Distance(npcPos, npc.SpawnPosition);
            if (distFromSpawn > npc.Behavior.LeashDistance)
            {
                npc.AiState = NpcAiState.Resetting;
                combatEngine.AddLogEntry($"{npc.Name} is resetting.", CombatLogType.Info);
                return;
            }
        }

        float effectiveRange = GetEffectiveNpcAttackRange(npc, simulatedTarget);

        if (distToTarget > effectiveRange)
        {
            if (npc.IsClientControlled && !UsesPartyConfiguredRange(npc, simulatedTarget))
                npc.AiState = NpcAiState.Chasing;
            // Real NPCs just wait
            return;
        }

        if (UsesPartyConfiguredRange(npc, simulatedTarget))
            ForceRotateToward(npc, targetPos, deltaTime);

        // Handle casting
        if (npc.State.IsCasting)
        {
            var castTarget = combatEngine.State.GetEntity(npc.State.CastTargetId);
            if (castTarget == null || !castTarget.IsAlive)
            {
                ClearNpcActionState(npc);
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
                return;
            }

            if (npc.BattleChara != null)
                ActorVisualStateController.ApplyActionLocked((Character*)npc.BattleChara, npc.VisualState);
            npc.State.CastTimeElapsed += deltaTime;
            if (npc.State.CastTimeElapsed >= npc.State.CastTimeTotal)
            {
                npc.State.IsCasting = false;
                if (npc.CurrentCastSkill != null)
                {
                    var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                        npc.CurrentCastSkill.ActionId, npc.State.CastTargetId, npc.CurrentCastSkill.Potency,
                        npc.CurrentCastSkill.AttackStyle, npc.CurrentCastSkill.Radius, npc.CurrentCastSkill.CastTime));
                    if (ok)
                        npc.CurrentCastSkill.CooldownRemaining = npc.CurrentCastSkill.Cooldown / ActionPace();
                    npc.CurrentCastSkill = null;
                }
            }
            return;
        }

        // Check animation lock
        if (npc.State.AnimationLock > 0)
        {
            if (npc.BattleChara != null)
                ActorVisualStateController.ApplyActionLocked((Character*)npc.BattleChara, npc.VisualState);
            return;
        }

        if (npc.BattleChara != null)
            ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);

        // Try skills
        float hpPercent = (float)npc.State.CurrentHp / npc.State.MaxHp;
        foreach (var skill in npc.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (skill.CooldownRemaining > 0)
                continue;
            var skillRange = GetEffectiveNpcSkillRange(npc, simulatedTarget, skill.Range);
            if ((npc.IsClientControlled || UsesPartyConfiguredRange(npc, simulatedTarget)) && distToTarget > skillRange)
                continue;
            if (hpPercent > skill.HpThreshold)
                continue;

            // Action Mode skips the engine cast bar — the telegraph executor owns the
            // windup uniformly (cast time feeds the telegraph duration).
            if (!config.ActionMode && skill.CastTime > 0)
            {
                npc.State.IsCasting = true;
                npc.State.CastActionId = skill.ActionId;
                npc.State.CastTimeTotal = skill.CastTime;
                npc.State.CastTimeElapsed = 0;
                npc.State.CastTargetId = targetEntityId;
                npc.CurrentCastSkill = skill;

                combatEngine.AddLogEntry(
                    $"{npc.Name} begins casting {skill.Name}...",
                    CombatLogType.Info);
            }
            else
            {
                var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                    skill.ActionId, targetEntityId, skill.Potency, skill.AttackStyle, skill.Radius, skill.CastTime));
                if (ok)
                {
                    skill.CooldownRemaining = skill.Cooldown / ActionPace();
                    npc.State.AnimationLock = MathF.Max(npc.State.AnimationLock, 0.6f);
                }
            }
            return;
        }

        // Auto-attack
        npc.AutoAttackTimer -= deltaTime;
        if (npc.AutoAttackTimer <= 0)
        {
            // FFXIV: caster/magic auto-attacks are MELEE (only physical ranged — bow/gun — auto at
            // range). Their spells stay ranged via the skill loop above; the basic swing only lands
            // up close. Hold the swing (don't reset the timer) until the target is in melee.
            if (npc.Behavior.AutoAttackStyle == NpcAttackStyle.Magic && distToTarget > MagicAutoAttackMeleeRange)
                return;

            npc.AutoAttackTimer = npc.Behavior.AutoAttackDelay / ActionPace();
            var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                npc.Behavior.AutoAttackActionId, targetEntityId, npc.Behavior.AutoAttackPotency,
                npc.Behavior.AutoAttackStyle, 0f, 0f, IsAutoAttack: true));
            if (ok)
                npc.State.AnimationLock = MathF.Max(npc.State.AnimationLock, 0.6f);
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
        if (npc.BattleChara != null)
            ActorVisualStateController.ApplyMoving((Character*)npc.BattleChara, npc.VisualState, deltaTime);
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

    private static void ClearNpcActionState(SimulatedNpc npc)
    {
        npc.State.IsCasting = false;
        npc.State.CastActionId = 0;
        npc.State.CastTimeElapsed = 0;
        npc.State.CastTimeTotal = 0;
        npc.State.CastTargetId = 0;
        npc.State.AnimationLock = 0;
        npc.CurrentCastSkill = null;
    }

    private void DiscardInvalidNpcActionState(SimulatedNpc npc)
    {
        if (!npc.State.IsCasting && npc.CurrentCastSkill == null)
            return;

        var target = combatEngine.State.GetEntity(npc.State.CastTargetId);
        var selectedTarget = combatEngine.GetNpcTarget(npc);
        var stateAllowsCast = npc.AiState is NpcAiState.Combat or NpcAiState.Chasing;
        var invalid = npc.CurrentCastSkill == null ||
                      npc.State.CastTargetId == 0 ||
                      target == null ||
                      !target.IsAlive ||
                      selectedTarget == null ||
                      !selectedTarget.IsAlive ||
                      selectedTarget.EntityId != npc.State.CastTargetId ||
                      !stateAllowsCast;

        if (!invalid)
            return;

        ClearNpcActionState(npc);
        if (npc.BattleChara != null && npc.AiState != NpcAiState.Dead)
            ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
    }

    private void TickResetting(SimulatedNpc npc, float deltaTime, Vector3 npcPos)
    {
        var direction = Vector3.Normalize(npc.SpawnPosition - npcPos);
        var newPos = npcPos + direction * npc.Behavior.MoveSpeed * 1.5f * deltaTime;
        if (npc.BattleChara != null)
            ActorVisualStateController.ApplyMoving((Character*)npc.BattleChara, npc.VisualState, deltaTime);
        SetNpcPosition(npc, newPos);

        npc.State.CurrentHp = npc.State.MaxHp;

        if (Vector3.Distance(newPos, npc.SpawnPosition) < 0.5f)
        {
            SetNpcPosition(npc, npc.SpawnPosition);
            npc.AiState = NpcAiState.Idle;

            if (npc.BattleChara != null)
            {
                var character = (Character*)npc.BattleChara;
                ActorVisualStateController.ApplyCombatIdle(character, npc.VisualState);
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
    /// Returns the standing goal for an approaching enemy. While farther than the
    /// keep-distance ring it heads straight in along its current direction (the
    /// goal is dynamic). Once it reaches the ring its angle is locked and de-stacked
    /// from neighbours so it settles and stops; the lock only releases if it gets
    /// pushed well outside again. Independent of the player's facing.
    /// </summary>
    private Vector3 ComputeApproachGoal(SimulatedNpc npc, Vector3 npcPos, Vector3 playerPos)
    {
        var targetDist = MathF.Max(0.5f, config.TargetApproachDistance);

        // Hold a locked world spot — the enemy stands its ground and does NOT glide
        // with the player. Only re-approach once the player has wandered away from
        // that spot by more than the unlock buffer.
        if (approachLockedGoals.TryGetValue(npc.Address, out var lockedGoal))
        {
            var ldx = lockedGoal.X - playerPos.X;
            var ldz = lockedGoal.Z - playerPos.Z;
            var unlock = targetDist + ApproachUnlockBuffer;
            if (ldx * ldx + ldz * ldz <= unlock * unlock)
                return lockedGoal;
            approachLockedGoals.Remove(npc.Address);
        }

        // Reserve a de-stacked world spot before arrival so pathing is planned
        // toward different ring positions instead of funneling every enemy into
        // the same point and separating only at the end.
        var flat = npcPos - playerPos;
        flat.Y = 0;
        var distToPlayer = flat.Length();
        var angle = distToPlayer < 0.1f
            ? Core.Services.ObjectTable.LocalPlayer?.Rotation ?? 0f
            : MathF.Atan2(flat.X, flat.Z);

        var goal = ResolveFreeApproachGoal(npc.Address, playerPos, angle, targetDist);
        approachLockedGoals[npc.Address] = goal;
        return goal;
    }

    /// <summary>
    /// Finds a standing spot near the enemy's natural approach angle that clears
    /// every other enemy's locked spot by a minimum distance, so a newly-arriving
    /// enemy fans out instead of stacking. Searched once, at lock time, so locked
    /// spots never shuffle frame to frame.
    /// </summary>
    private Vector3 ResolveFreeApproachGoal(nint self, Vector3 playerPos, float desiredAngle, float targetDist)
    {
        const float minSpacing = 2.0f;
        var minGap = Math.Clamp(minSpacing / targetDist, 0.2f, MathF.PI / 2f);

        Vector3 At(float a) => playerPos + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * targetDist;

        bool Clear(Vector3 p)
        {
            foreach (var (addr, g) in approachLockedGoals)
            {
                if (addr == self) continue;
                var dx = p.X - g.X;
                var dz = p.Z - g.Z;
                if (dx * dx + dz * dz < minSpacing * minSpacing)
                    return false;
            }
            return true;
        }

        var at = At(desiredAngle);
        if (Clear(at)) return at;

        for (int i = 1; i <= 18; i++)
        {
            var plus = At(desiredAngle + i * minGap);
            if (Clear(plus)) return plus;
            var minus = At(desiredAngle - i * minGap);
            if (Clear(minus)) return minus;
        }

        return at; // fully crowded -> accept overlap rather than loop forever
    }

    private void TickSoloApproach(
        SimulatedNpc npc,
        float deltaTime,
        Vector3 playerPos,
        TerrainHeightCache? terrainCache)
    {
        if (npc.BattleChara == null) return;
        if (npc.AiState == NpcAiState.Dead)
        {
            StopApproachMoveAnim(npc);
            return;
        }

        if (!combatEngine.State.PlayerState.IsAlive)
        {
            StopApproachMoveAnim(npc);
            return;
        }

        var gameObj = (GameObject*)npc.BattleChara;
        var npcPos = (Vector3)gameObj->Position;

        var targetPos = ComputeApproachGoal(npc, npcPos, playerPos);
        targetPos.Y = playerPos.Y + config.DefaultNpcHeightOffset;

        var moveTarget = targetPos;
        var hasVnavmeshTarget = TryUpdateVNavmeshPath(npc, deltaTime, npcPos, targetPos, out var pathTarget, useLookahead: false);
        if (hasVnavmeshTarget)
            moveTarget = pathTarget;

        if (config.UseVNavmeshTargetApproach && !hasVnavmeshTarget)
        {
            if (terrainCache != null && approachPaths.TryGetValue(npc.Address, out var noPathState))
                CorrectStableRootHeight(gameObj, npcPos, terrainCache, noPathState, deltaTime, preserveInitialClearance: true);

            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, playerPos, deltaTime);
            return;
        }

        if (Vector3.Distance(npcPos, moveTarget) <= 0.3f)
        {
            if (hasVnavmeshTarget && terrainCache != null &&
                approachPaths.TryGetValue(npc.Address, out var arrivedPathState))
                CorrectStableRootHeight(gameObj, npcPos, terrainCache, arrivedPathState, deltaTime, preserveInitialClearance: true);

            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, playerPos, deltaTime);
            return;
        }

        float speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        float remainingDist = Vector3.Distance(npcPos, moveTarget);
        float moveDist = speed * deltaTime;

        Vector3 newPos = remainingDist <= moveDist
            ? moveTarget
            : npcPos + Vector3.Normalize(moveTarget - npcPos) * moveDist;

        if (hasVnavmeshTarget && terrainCache != null &&
            approachPaths.TryGetValue(npc.Address, out var pathState))
        {
            newPos = CorrectMovingRootHeight(newPos, terrainCache, pathState, deltaTime, preserveInitialClearance: true);
        }

        StartApproachMoveAnim(npc, deltaTime);
        movementBlockHook.SetApproachPosition(gameObj, newPos.X, newPos.Y, newPos.Z);
        ForceRotateToward(npc, playerPos, deltaTime);
    }

    /// <summary>
    /// Party-mode target-aware enemy movement. Solo/no-party movement is kept in
    /// TickSoloApproach as the pre-party baseline.
    /// </summary>
    private void TickPartyApproach(
        SimulatedNpc npc,
        float deltaTime,
        Vector3 playerPos,
        SimulatedEntityState npcTarget,
        Vector3 npcTargetPos,
        TerrainHeightCache? terrainCache)
    {
        if (npc.BattleChara == null) return;
        if (npc.AiState == NpcAiState.Dead)
        {
            ClearPartyApproachState(npc);
            return;
        }

        // Party mode continues after player death while companions are alive.
        if (!npcTarget.IsAlive)
        {
            ClearPartyApproachState(npc);
            return;
        }

        var gameObj = (GameObject*)npc.BattleChara;
        var npcPos = (Vector3)gameObj->Position;

        if (approachPaths.TryGetValue(npc.Address, out var existingPathState) &&
            existingPathState.TargetEntityId != 0 &&
            existingPathState.TargetEntityId != npcTarget.EntityId)
        {
            existingPathState.Waypoints.Clear();
            existingPathState.WaypointIndex = 0;
            existingPathState.PendingPath = null;
        }

        if (!partyEngagePlanner.TryGetPlan(npc.SimulatedEntityId, out var partyPlan))
        {
            if (terrainCache != null && approachPaths.TryGetValue(npc.Address, out var holdPathState))
                CorrectStableRootHeight(gameObj, npcPos, terrainCache, holdPathState, deltaTime, preserveInitialClearance: false);

            LogPartyApproachDebug(npc, "no-plan", npcPos, npcTargetPos, null, npcPos, false, distToTarget: FlatDistance(npcPos, npcTargetPos), attackRange: GetPartyAttackRange(npc.Behavior.AutoAttackStyle) + PartyMeleeAttackRangeBuffer);
            StopApproachMoveAnim(npc);
            return;
        }

        var targetPos = partyPlan.Goal;
        var facePos = partyPlan.HasFaceTarget ? partyPlan.FaceTarget : npcPos;
        var distToTarget = FlatDistance(npcPos, npcTargetPos);
        var partyAttackRange = DynamicHoldRange(npc) + PartyMeleeAttackRangeBuffer;
        var rangeStateAvailable = TryGetOrCreateApproachPathState(npc, out var rangeState);
        var outerEdge = rangeStateAvailable && rangeState.WasHoldingRange
            ? partyAttackRange + PartyAttackRangeHysteresis
            : partyAttackRange;
        // Ranged-intent enemies that drifted too close (e.g. after a melee excursion) back off toward
        // the planner's backline goal instead of holding. Gentle: only fires when well inside.
        var tooCloseForRanged = npc.IsRangedIntent && distToTarget < partyAttackRange * RangedBackoffFraction;
        var shouldHoldRange = distToTarget <= outerEdge && !tooCloseForRanged;
        if (shouldHoldRange)
        {
            if (rangeStateAvailable)
            {
                rangeState.WasHoldingRange = true;
                if (terrainCache != null)
                    CorrectStableRootHeight(gameObj, npcPos, terrainCache, rangeState, deltaTime, preserveInitialClearance: false);
            }

            LogPartyApproachDebug(npc, "in-range", npcPos, npcTargetPos, partyPlan, npcPos, false, distToTarget, partyAttackRange);
            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, npcTargetPos, deltaTime);
            return;
        }
        if (rangeStateAvailable)
            rangeState.WasHoldingRange = false;

        var moveTarget = targetPos;
        var hasVnavmeshTarget = TryUpdateVNavmeshPath(
            npc,
            deltaTime,
            npcPos,
            targetPos,
            out var pathTarget,
            discardStalePendingPath: true);
        if (approachPaths.TryGetValue(npc.Address, out var pathStateForTarget))
            pathStateForTarget.TargetEntityId = npcTarget.EntityId;
        if (hasVnavmeshTarget)
            moveTarget = pathTarget;

        // vnavmesh requested but no usable path (plugin unavailable, navmesh not
        // built for this zone, or the async path is still pending). Fall back to
        // direct movement toward the player instead of freezing in place — Y just
        // tracks the player's height, same as the pre-vnavmesh approximation.
        if (config.UseVNavmeshTargetApproach && !hasVnavmeshTarget)
            moveTarget = targetPos;

        if (FlatDistance(npcPos, moveTarget) <= 0.3f)
        {
            if (partyPlan.Kind == PartyEngagePlanKind.EngagePoint &&
                FlatDistance(npcPos, npcTargetPos) > 0.3f)
            {
                moveTarget = npcTargetPos;
                hasVnavmeshTarget = false;
            }
            else
            {
                if (terrainCache != null &&
                    TryGetOrCreateApproachPathState(npc, out var arrivedPathState))
                    CorrectStableRootHeight(gameObj, npcPos, terrainCache, arrivedPathState, deltaTime, preserveInitialClearance: false);

                LogPartyApproachDebug(npc, "arrived-move-target", npcPos, npcTargetPos, partyPlan, moveTarget, hasVnavmeshTarget, distToTarget, partyAttackRange);
                StopApproachMoveAnim(npc);
                ForceRotateToward(npc, facePos, deltaTime);
                return;
            }
        }

        // Smooth movement toward the target position
        float speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        var flatMove = moveTarget - npcPos;
        flatMove.Y = 0;
        float remainingDist = flatMove.Length();
        float moveDist = speed * deltaTime;

        Vector3 newPos;
        Vector3 moveDir;
        if (remainingDist <= moveDist)
        {
            newPos = npcPos with { X = moveTarget.X, Z = moveTarget.Z };
            moveDir = flatMove.LengthSquared() > 0.000001f ? Vector3.Normalize(flatMove) : Vector3.Zero;
        }
        else
        {
            var rawDir = GetSteeredMoveDirection(
                npc.SimulatedEntityId,
                LocalSteeringFaction.Enemy,
                npcPos,
                flatMove,
                npcTarget.EntityId);
            if (rawDir == Vector3.Zero)
            {
                LogPartyApproachDebug(npc, "steering-blocked", npcPos, npcTargetPos, partyPlan, moveTarget, hasVnavmeshTarget, distToTarget, partyAttackRange);
                StopApproachMoveAnim(npc);
                ForceRotateToward(npc, facePos, deltaTime);
                return;
            }
            moveDir = SmoothApproachHeading(npc, rawDir, deltaTime);
            newPos = npcPos + moveDir * moveDist;
        }

        if (terrainCache != null &&
            TryGetOrCreateApproachPathState(npc, out var pathState))
        {
            newPos = CorrectMovingRootHeight(newPos, terrainCache, pathState, deltaTime, preserveInitialClearance: false);
        }
        else if (vnavmeshIpc.CanPathfind)
        {
            var floor = vnavmeshIpc.PointOnFloor(newPos, false, 3f);
            if (floor.HasValue)
                newPos.Y = floor.Value.Y + config.DefaultNpcHeightOffset;
        }
        else
        {
            newPos.Y = targetPos.Y;
        }

        if (FlatDistance(npcPos, newPos) <= PartyApproachMovementEpsilon)
        {
            LogPartyApproachDebug(npc, "settled-no-move", npcPos, npcTargetPos, partyPlan, moveTarget, hasVnavmeshTarget, distToTarget, partyAttackRange, moveDir, newPos);
            StopApproachMoveAnim(npc);
            ForceRotateToward(npc, facePos, deltaTime);
            return;
        }

        // Call the real SetPosition via the hook bypass — this updates both
        // the struct field AND the DrawObject (3D model position).
        movementBlockHook.AddApproachNpc(npc.Address);
        StartApproachMoveAnim(npc, deltaTime);
        movementBlockHook.SetApproachPosition(gameObj, newPos.X, newPos.Y, newPos.Z);

        LogPartyApproachDebug(npc, "move", npcPos, npcTargetPos, partyPlan, moveTarget, hasVnavmeshTarget, distToTarget, partyAttackRange, moveDir, newPos);
        ForceRotateToward(npc, moveTarget, deltaTime);
    }

    private void LogPartyApproachDebug(
        SimulatedNpc npc,
        string state,
        Vector3 npcPos,
        Vector3 targetPos,
        PartyEngagePlan? plan,
        Vector3 moveTarget,
        bool hasVnavmeshTarget,
        float distToTarget,
        float attackRange,
        Vector3? moveDir = null,
        Vector3? nextPos = null)
    {
        if (!config.DevPartyApproachDebugLog)
            return;

        var now = combatEngine.State.SimulationTime;
        if (partyApproachDebugNextLogAt.TryGetValue(npc.SimulatedEntityId, out var nextLogAt) && now < nextLogAt)
            return;

        partyApproachDebugNextLogAt[npc.SimulatedEntityId] = now + PartyApproachDebugInterval;
        var planText = plan == null
            ? "none"
            : $"{plan.Kind}/target=0x{plan.TargetId:X}/goal={FormatFlat(plan.Goal)}/face={FormatFlat(plan.FaceTarget)}";
        var dirText = moveDir.HasValue ? FormatFlat(moveDir.Value) : "-";
        var nextText = nextPos.HasValue ? FormatFlat(nextPos.Value) : "-";
        var goalToMove = plan == null ? 0f : FlatDistance(plan.Goal, moveTarget);

        log.Info(
            $"[PartyApproachDbg] {npc.Name} 0x{npc.SimulatedEntityId:X} {state} " +
            $"dist={distToTarget:F2}/range={attackRange:F2} npc={FormatFlat(npcPos)} target={FormatFlat(targetPos)} " +
            $"plan={planText} moveTarget={FormatFlat(moveTarget)} goalToMove={goalToMove:F2} " +
            $"vnav={hasVnavmeshTarget} dir={dirText} next={nextText}");
    }

    private static string FormatFlat(Vector3 value)
        => $"({value.X:F2},{value.Z:F2})";

    private void LogApproachRouteDebug(SimulatedNpc npc, string route, bool partyMode, bool hasLivingCompanions)
    {
        if (!config.DevPartyApproachDebugLog)
            return;

        var routeKey = $"{route}:{partyMode}:{hasLivingCompanions}:{config.UseSoloTargetFormationWhenNoCompanions}";
        if (lastApproachDebugRoute.TryGetValue(npc.SimulatedEntityId, out var lastRoute) && lastRoute == routeKey)
            return;

        lastApproachDebugRoute[npc.SimulatedEntityId] = routeKey;
        log.Info(
            $"[ApproachRouteDbg] {npc.Name} 0x{npc.SimulatedEntityId:X} route={route} " +
            $"partyMode={partyMode} hasCompanions={hasLivingCompanions} " +
            $"soloWhenNoCompanions={config.UseSoloTargetFormationWhenNoCompanions}");
    }

    private Vector3 GetSteeredMoveDirection(
        uint actorId,
        LocalSteeringFaction faction,
        Vector3 actorPosition,
        Vector3 goalFlatDirection,
        uint ignoredObstacleId = 0)
    {
        var actor = new LocalSteeringActor(actorId, faction, actorPosition, IsPc: false);
        return LocalSteering.SteerFlatDirection(actor, goalFlatDirection, BuildLocalSteeringActors(actor), ignoredObstacleId);
    }

    /// <summary>
    /// Exponential low-pass on a steered approach heading. Filters the
    /// frame-to-frame direction flips that crowding produces (which show up as
    /// in-place jitter) while still turning toward a genuinely new heading within
    /// a few frames. State is reset when the NPC stops moving.
    /// </summary>
    private Vector3 SmoothApproachHeading(SimulatedNpc npc, Vector3 rawDir, float deltaTime)
    {
        if (!TryGetOrCreateApproachPathState(npc, out var state))
            return rawDir;

        if (!state.HasSmoothedMoveDir)
        {
            state.SmoothedMoveDir = rawDir;
            state.HasSmoothedMoveDir = true;
            return rawDir;
        }

        var alpha = 1f - MathF.Exp(-deltaTime / MathF.Max(0.0001f, ApproachHeadingSmoothingTau));
        var blended = Vector3.Lerp(state.SmoothedMoveDir, rawDir, alpha);
        blended.Y = 0;
        if (blended.LengthSquared() < 0.000001f)
            blended = rawDir;

        var smoothed = Vector3.Normalize(blended);
        state.SmoothedMoveDir = smoothed;
        return smoothed;
    }

    private List<LocalSteeringActor> BuildLocalSteeringActors(LocalSteeringActor currentActor)
    {
        var actors = new List<LocalSteeringActor>();
        var player = Core.Services.ObjectTable.LocalPlayer;
        var playerState = combatEngine.State.PlayerState;
        if (player != null && playerState.IsAlive)
        {
            actors.Add(new LocalSteeringActor(
                playerState.EntityId,
                LocalSteeringFaction.Party,
                player.Position,
                IsPc: true));
        }

        actors.Add(currentActor);

        foreach (var entity in combatEngine.State.Entities.Values)
        {
            if (!entity.IsAlive || entity.EntityId == currentActor.Id)
                continue;

            var position = combatEngine.GetSimulatedEntityPosition(entity);
            if (position == Vector3.Zero)
                continue;

            actors.Add(new LocalSteeringActor(
                entity.EntityId,
                entity.IsCompanion ? LocalSteeringFaction.Party : LocalSteeringFaction.Enemy,
                position,
                IsPc: false));
        }

        return actors;
    }

    private bool TryGetOrCreateApproachPathState(SimulatedNpc npc, out ApproachPathState state)
    {
        if (approachPaths.TryGetValue(npc.Address, out state!))
            return true;

        state = new ApproachPathState
        {
            ConfiguredHeightOffset = config.DefaultNpcHeightOffset,
            VisualState = npc.VisualState,
        };
        approachPaths[npc.Address] = state;
        return true;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    private float GetEffectiveNpcAttackRange(SimulatedNpc npc, SimulatedEntityState target)
    {
        if (UsesPartyConfiguredRange(npc, target))
            return DynamicHoldRange(npc) + PartyMeleeAttackRangeBuffer + PartyAttackRangeHysteresis;

        // For real NPCs: use a generous range since we can't move them.
        return npc.IsClientControlled
            ? npc.Behavior.AutoAttackRange + 1.0f
            : npc.Behavior.AutoAttackRange + 30.0f;
    }

    // The enemy's current desired engage distance (driven by the action it intends to use next),
    // falling back to the fixed weapon-style range until it has been computed this combat.
    private float DynamicHoldRange(SimulatedNpc npc)
        => npc.DesiredEngageRange > 0f
            ? npc.DesiredEngageRange
            : GetPartyAttackRange(npc.Behavior.AutoAttackStyle);

    // Prefer-ranged with lookahead + dwell: a caster stays at spell range while it has any ranged
    // skill ready or coming soon, and only falls to its (melee) auto when it has no ranged option
    // for a while. Updated every combat frame; the approach + planner read the result.
    private void UpdateDesiredEngageRange(SimulatedNpc npc, float deltaTime)
    {
        if (npc.IntentDwellTimer > 0f)
            npc.IntentDwellTimer = MathF.Max(0f, npc.IntentDwellTimer - deltaTime);

        var meleeHold = MathF.Max(0.5f, config.PartyMeleeAttackRange);
        var rangedHold = MathF.Max(1.0f, config.PartyRangedAttackRange);
        var hpPercent = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 1f;

        // Physical ranged (bow/gun) auto-attacks at range. A caster fights at range whenever it OWNS
        // a usable ranged/magic skill — it WAITS at range for the cooldown instead of walking in
        // (cooldown-driven walk-in left it in melee most of the time, standing too close). Only a
        // pure-melee enemy with no ranged option engages in melee.
        var wantRanged = npc.Behavior.AutoAttackStyle == NpcAttackStyle.Ranged;
        var maxRangedSkillRange = 0f;
        foreach (var skill in npc.Behavior.Skills)
        {
            if (skill.AttackStyle is not (NpcAttackStyle.Ranged or NpcAttackStyle.Magic))
                continue;
            if (hpPercent > skill.HpThreshold)
                continue; // HP-gated — not part of its usable kit yet
            wantRanged = true;
            maxRangedSkillRange = MathF.Max(maxRangedSkillRange, skill.Range);
        }
        // Stand off at the configured ranged distance, capped by the best spell's reach.
        var rangedTarget = maxRangedSkillRange > 0f
            ? MathF.Min(rangedHold, MathF.Max(meleeHold, maxRangedSkillRange))
            : rangedHold;

        // Only flip intent once the dwell has elapsed, to avoid front/back thrash.
        if (wantRanged != npc.IsRangedIntent && npc.IntentDwellTimer <= 0f)
        {
            npc.IsRangedIntent = wantRanged;
            npc.IntentDwellTimer = IntentMinDwell;
        }

        npc.DesiredEngageRange = npc.IsRangedIntent ? rangedTarget : meleeHold;
    }

    private float GetEffectiveNpcSkillRange(SimulatedNpc npc, SimulatedEntityState target, float skillRange)
    {
        if (UsesPartyConfiguredRange(npc, target))
        {
            // Casters stand in melee (their auto is a melee swing) but their SPELLS keep full
            // range — don't clamp spell range down to the melee auto range.
            if (npc.Behavior.AutoAttackStyle == NpcAttackStyle.Magic)
                return skillRange;
            return MathF.Min(skillRange, GetPartyAttackRange(npc.Behavior.AutoAttackStyle) + PartyMeleeAttackRangeBuffer);
        }

        return skillRange;
    }

    private bool UsesPartyConfiguredRange(SimulatedNpc npc, SimulatedEntityState target)
        => config.EnableCombatCompanions &&
           (combatEngine.HasLivingCompanions?.Invoke() == true ||
            !config.UseSoloTargetFormationWhenNoCompanions);

    // Only physical ranged (bow/gun) holds at range. Casters/magic hold at MELEE — their basic
    // auto-attack is a melee swing in FFXIV; their spells (full range) still fire from up close.
    private float GetPartyAttackRange(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged
            ? MathF.Max(1.0f, config.PartyRangedAttackRange)
            : MathF.Max(0.5f, config.PartyMeleeAttackRange);

    private bool TryUpdateVNavmeshPath(
        SimulatedNpc npc,
        float deltaTime,
        Vector3 npcPos,
        Vector3 targetPos,
        out Vector3 moveTarget,
        bool useLookahead = true,
        bool discardStalePendingPath = false)
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

        if (discardStalePendingPath &&
            state.PendingPath != null &&
            !state.PendingPath.IsCompleted &&
            FlatDistance(state.PendingTarget, targetPos) >= VNavmeshRepathDistance)
        {
            state.PendingPath = null;
            state.RepathTimer = 0;
        }

        if (state.PendingPath != null && state.PendingPath.IsCompleted)
        {
            try
            {
                var completedTarget = state.PendingTarget;
                var completedPath = state.PendingPath.GetAwaiter().GetResult();
                if (discardStalePendingPath &&
                    FlatDistance(completedTarget, targetPos) >= VNavmeshRepathDistance)
                {
                    state.Waypoints.Clear();
                    state.WaypointIndex = 0;
                    state.RepathTimer = 0;
                    return false;
                }

                state.Waypoints = completedPath;
                state.RequestedTarget = completedTarget;
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
                state.WaypointIndex = SelectInitialWaypoint(state, npcPos);
                state.LastCorrectedWaypointIndex = -1;
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
               Vector3.Distance(npcPos, ApplyFloorYOffset(state, state.Waypoints[state.WaypointIndex])) <= VNavmeshWaypointReachDistance)
        {
            state.WaypointIndex++;
        }

        if (state.WaypointIndex >= state.Waypoints.Count)
            return false;

        if (useLookahead)
        {
            var lookaheadIndex = SelectLookaheadWaypoint(state, npcPos);
            state.WaypointIndex = lookaheadIndex;
        }

        moveTarget = ApplyFloorYOffset(state, state.Waypoints[state.WaypointIndex]);
        moveTarget = CorrectMoveTargetFloor(state, moveTarget, deltaTime);
        return true;
    }

    private static int SelectInitialWaypoint(ApproachPathState state, Vector3 current)
    {
        if (state.Waypoints.Count == 0)
            return 0;

        if (state.Waypoints.Count == 1)
            return FlatDistanceSquared(current, ApplyFloorYOffset(state, state.Waypoints[0])) <=
                   VNavmeshWaypointReachDistance * VNavmeshWaypointReachDistance
                ? 1
                : 0;

        var bestSegmentEnd = 1;
        var bestDistSq = float.MaxValue;
        var foundSegment = false;

        for (var i = 0; i < state.Waypoints.Count - 1; i++)
        {
            var a = ApplyFloorYOffset(state, state.Waypoints[i]);
            var b = ApplyFloorYOffset(state, state.Waypoints[i + 1]);
            a.Y = 0;
            b.Y = 0;
            var c = current;
            c.Y = 0;

            var ab = b - a;
            var lenSq = ab.LengthSquared();
            if (lenSq <= 0.0001f)
                continue;

            var t = Math.Clamp(Vector3.Dot(c - a, ab) / lenSq, 0f, 1f);
            var closest = a + ab * t;
            var distSq = Vector3.DistanceSquared(c, closest);
            if (distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            bestSegmentEnd = i + 1;
            foundSegment = true;
        }

        if (!foundSegment)
            return 0;

        while (bestSegmentEnd < state.Waypoints.Count &&
               FlatDistanceSquared(current, ApplyFloorYOffset(state, state.Waypoints[bestSegmentEnd])) <=
               VNavmeshWaypointReachDistance * VNavmeshWaypointReachDistance)
        {
            bestSegmentEnd++;
        }

        return bestSegmentEnd;
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.LengthSquared();
    }

    private static int SelectLookaheadWaypoint(ApproachPathState state, Vector3 current)
    {
        var selected = state.WaypointIndex;
        var accumulated = 0f;
        var previous = current;

        for (var i = state.WaypointIndex; i < state.Waypoints.Count; i++)
        {
            var point = ApplyFloorYOffset(state, state.Waypoints[i]);
            accumulated += Vector3.Distance(previous, point);
            selected = i;
            if (accumulated >= VNavmeshLookaheadDistance)
                break;
            previous = point;
        }

        return selected;
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

    private TerrainHeightCache? BuildTerrainHeightCache(Vector3 playerPos, IReadOnlyList<SimulatedNpc> npcs)
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

        return terrainHeightService.EnsureCoverage(
            new TerrainHeightBounds(minX, maxX, minZ, maxZ, maxY),
            combatEngine.State.SimulationTime);
    }

    private Vector3 CorrectMovingRootHeight(
        Vector3 rootPosition,
        TerrainHeightCache terrainCache,
        ApproachPathState state,
        float deltaTime,
        bool preserveInitialClearance)
    {
        if (!terrainCache.TrySample(rootPosition.X, rootPosition.Z, out var terrainY))
            return rootPosition;

        if (!state.HasStableRootTerrainClearance)
        {
            state.StableRootTerrainClearance = preserveInitialClearance
                ? rootPosition.Y - terrainY - config.DefaultNpcHeightOffset
                : 0f;
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
        TerrainHeightCache terrainCache,
        ApproachPathState state,
        float deltaTime,
        bool preserveInitialClearance)
    {
        var corrected = CorrectMovingRootHeight(rootPosition, terrainCache, state, deltaTime, preserveInitialClearance);
        if (MathF.Abs(corrected.Y - rootPosition.Y) < 0.001f)
            return;

        movementBlockHook.SetApproachPosition(gameObj, corrected.X, corrected.Y, corrected.Z);
    }

    private void StartApproachMoveAnim(SimulatedNpc npc, float deltaTime)
    {
        if (npc.BattleChara == null)
            return;

        if (!approachPaths.TryGetValue(npc.Address, out var state))
        {
            state = new ApproachPathState { VisualState = npc.VisualState };
            approachPaths[npc.Address] = state;
        }
        state.VisualState = npc.VisualState;

        var character = (Character*)npc.BattleChara;
        ActorVisualStateController.ApplyMoving(character, npc.VisualState, deltaTime);
    }

    private void StopApproachMoveAnim(SimulatedNpc npc)
    {
        if (!approachPaths.TryGetValue(npc.Address, out var state))
            return;

        StopApproachMoveAnim(npc.Address, state);
    }

    private void StopApproachMoveAnim(nint address, ApproachPathState state)
    {
        state.HasSmoothedMoveDir = false;
        try
        {
            var character = (Character*)address;
            ActorVisualStateController.ClearMovement(character, state.VisualState);
        }
        catch { }
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
        approachLockedGoals.Clear();
    }
}
