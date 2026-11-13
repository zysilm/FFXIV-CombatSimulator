// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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

public unsafe partial class NpcAiController : IDisposable
{
    public const float PlayerTriggeredEngageDelay = 0.5f;

    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter;
    private readonly NpcCorpseFootIkSolver footIkSolver;
    private readonly Func<nint, bool> isExternallyControlled;
    private readonly Func<uint, bool> isTelegraphBusy;
    // Enemies the crowd has set running, with the look to clear when they stop.
    private readonly Dictionary<nint, ActorVisualState> movingNpcs = new();
    private readonly HashSet<nint> passiveEnemyAddresses = new();
    // Caster/magic enemies auto-attack at melee reach (their spells are ranged, the basic swing is not).
    private const float MagicAutoAttackMeleeRange = 3.5f;
    // Enemy MP economy: enemies have half the player's max MP (set at spawn) but recover at double
    // rate, and only from natural regen + auto-attacks — never from guarding (they don't guard). This
    // makes them lead with skills while they have MP, then fall back to auto-attacks to recover.
    // The numbers live in CombatBudgetModel, which also predicts what a kit sustains from them.
    private const int EnemyMpRegenPerSecond = CombatBudgetModel.EnemyMpRegenPerSecond;       // 2× the player's ~67/s natural regen
    private const int EnemyAutoAttackMpRestore = CombatBudgetModel.EnemyAutoAttackMpRestore; // 2× the player's 300 basic-attack restore

    // Auto-engage countdown: when >= 0, every Tick decrements; on reaching
    // 0 we call EngageNpc on each selected NPC. Negative = inactive.
    private float pendingAutoEngageDelay = -1f;

    public NpcAiController(
        CombatEngine combatEngine,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        Configuration config,
        IPluginLog log,
        CombatSimulator.ActionCombat.CombatModeRouter combatModeRouter,
        NpcCorpseFootIkSolver footIkSolver,
        Func<nint, bool>? isExternallyControlled = null,
        Func<uint, bool>? isTelegraphBusy = null)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.config = config;
        this.log = log;
        this.combatModeRouter = combatModeRouter;
        this.footIkSolver = footIkSolver;
        this.isExternallyControlled = isExternallyControlled ?? (_ => false);
        this.isTelegraphBusy = isTelegraphBusy ?? (_ => false);

        combatEngine.OnSimulationStarted += ScheduleAutoEngage;
        combatEngine.OnSimulationReset += OnSimulationResetOrStop;
    }

    /// <summary>The crowd on the plugin's navigation mesh: the only thing that moves enemies. Null or not ready: they stand.</summary>
    public CombatSimulator.Navigation.CrowdMover? Crowd { private get; set; }
    private readonly List<SimulatedNpc> crowdDriven = new();

    /// <summary>Fills a list with where the living party stands (player and companions); sentries watch for them.</summary>
    public Action<List<Vector3>>? CollectPartyPositions { private get; set; }

    /// <summary>Where telegraphed casts register their snapshotted area (null = none drawn).</summary>
    public CombatSimulator.ActionCombat.EnemyAreaMarkers? AreaMarkers { private get; set; }

    private void ScheduleAutoEngage()
    {
        if (!config.EnableNpcAutoEngage || combatEngine.IsEnemyInitiationSuppressed)
        {
            pendingAutoEngageDelay = -1f;
            return;
        }
        pendingAutoEngageDelay = Math.Clamp(config.NpcAutoEngageDelay, 0f, 20f);
    }

    private void OnSimulationResetOrStop()
    {
        StopAllMoveAnims();
        tacticsStates.Clear();

        // OnSimulationReset fires from both StopSimulation and ResetState.
        // StopSimulation flips IsActive false before invoking — skip auto-engage
        // there since there are no selected NPCs left to engage.
        if (!combatEngine.State.IsActive) { pendingAutoEngageDelay = -1f; return; }
        ScheduleAutoEngage();
    }

    /// <summary>
    /// Shared skill-start budget for every hostile NPC (see config.NpcSkillRateLimit). This
    /// controller only ever ticks enemies — companions are driven by CombatCompanionManager — so
    /// gating here throttles the hostile side alone, which is the intent.
    /// </summary>
    private float skillTokens;
    /// <summary>How many starts may go back-to-back after a lull, so a fight still opens with a
    /// volley instead of a metronome.</summary>
    private const float SkillTokenBurst = 3f;

    /// <summary>Take a skill-start slot if one is free. Only the COMMIT points call this: a cast
    /// that already paid on the way in must not pay again when it lands.</summary>
    private bool TryTakeSkillToken()
    {
        if (config.NpcSkillRateLimit <= 0f) return true; // uncapped
        if (skillTokens < 1f) return false;
        skillTokens -= 1f;
        return true;
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> npcs)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        tickNpcs = npcs;

        skillTokens = MathF.Min(SkillTokenBurst, skillTokens + MathF.Max(0f, config.NpcSkillRateLimit) * deltaTime);

        if (combatEngine.IsEnemyInitiationSuppressed)
        {
            TickPassiveEnemyPack(npcs);
            return;
        }
        passiveEnemyAddresses.Clear();

        // Auto-engage countdown — when it expires, force every selected NPC
        // from Idle into Engaging so attacks start without player aggro.
        if (pendingAutoEngageDelay >= 0f)
        {
            pendingAutoEngageDelay -= deltaTime;
            if (pendingAutoEngageDelay <= 0f)
            {
                pendingAutoEngageDelay = -1f;
                foreach (var npc in npcs)
                    if (!npc.IsSentry)
                        EngageNpc(npc);
            }
        }

        var partyMode = config.EnableCombatCompanions;
        var hasLivingCompanions = combatEngine.HasLivingCompanions?.Invoke() == true;

        // Party mode is controlled by the companion feature toggle. When it is
        // active, all enemies join combat so party formation/targeting can run
        // even before companion clones are spawned. Enhanced tactics opts out: forcing it every frame
        // made auto-engage and its delay dead settings and left enemies no idle phase.
        if (partyMode && !config.TacticsRespectEngage)
        {
            foreach (var npc in npcs)
                if (!npc.IsSentry)
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
            if (isExternallyControlled(npc.Address))
                continue;

            if (config.NpcCollisionActive)
                footIkSolver.Track(npc.Address, deltaTime);

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

        TickCallForHelp(npcs);
        TickSentries(npcs);
        UpdateMeleeAdmission(npcs, targetByNpcId);

        // Movement: every enemy is moved by the crowd on the plugin's navigation mesh
        // (docs/AI_MOVEMENT_REWRITE.md); there is no other mover. Without a mesh under it (the first
        // moment of a battle, before the mesh is built) an enemy stands where it is.
        crowdDriven.Clear();
        var crowd = Crowd;
        var crowdReady = crowd is { Ready: true };
        // The player is in the crowd as something to walk round.
        if (crowdReady && combatEngine.State.PlayerState.IsAlive)
            crowd!.SetObstacle(player.Address, playerPos, ((GameObject*)player.Address)->HitboxRadius);
        foreach (var npc in npcs)
        {
            npc.CrowdDriven = false;
            if (!npc.IsSpawned || npc.BattleChara == null || isExternallyControlled(npc.Address))
                continue;
            // Freshly spawned clones are pinned to their spawn placement by NpcSpawner for a moment.
            if (npc.SpawnPlacementFrames > 0 || npc.AiState == NpcAiState.Dead)
                continue;
            // Real NPCs of the map: keep the server from moving them under us.
            if (!npc.IsClientControlled)
                movementBlockHook.AddApproachNpc(npc.Address);
            if (npc.AiState == NpcAiState.Resetting)
                npc.AiState = npc.State.IsAlive ? NpcAiState.Idle : NpcAiState.Dead;

            if (crowdReady && TryCrowdMove(npc, deltaTime))
            {
                crowdDriven.Add(npc);
                continue;
            }
            StopApproachMoveAnim(npc);
        }
        ApplyCrowdMotion(deltaTime);
    }

    /// <summary>
    /// Clear approach state. Called on simulation stop.
    /// </summary>
    public void ClearApproachState()
    {
        movementBlockHook.ClearApproachNpcs();
        StopAllMoveAnims();
        passiveEnemyAddresses.Clear();
    }

    private void TickPassiveEnemyPack(IReadOnlyList<SimulatedNpc> npcs)
    {
        pendingAutoEngageDelay = -1f;
        movementBlockHook.ClearApproachNpcsExcept(isExternallyControlled);
        StopAllMoveAnims();

        var liveAddresses = new HashSet<nint>();
        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned || isExternallyControlled(npc.Address))
                continue;

            npc.IsAttackingPlayer = false;
            ClearNpcActionState(npc);
            npc.EngageDelayTimer = 0f;

            if (!npc.State.IsAlive)
            {
                npc.AiState = NpcAiState.Dead;
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyDead((Character*)npc.BattleChara, npc.VisualState);
                continue;
            }

            npc.AiState = NpcAiState.Idle;
            liveAddresses.Add(npc.Address);
            if (npc.BattleChara != null)
            {
                ((Character*)npc.BattleChara)->TargetId = default;
                if (passiveEnemyAddresses.Add(npc.Address))
                    animationController.ClearBattleStance(npc);
            }
        }

        passiveEnemyAddresses.RemoveWhere(address => !liveAddresses.Contains(address));
    }

    private static bool IsUnawareSentry(SimulatedNpc npc)
        => npc.IsSentry && npc.AiState == NpcAiState.Idle;

    // A sentry notices the party within sight, or a neighbour that is already fighting.
    private const float SentrySightRange = 14f;
    private const float SentryAlertRadius = 10f;
    private const float SentrySightHeight = 8f;
    private readonly List<Vector3> partyPositions = new();

    private void TickSentries(IReadOnlyList<SimulatedNpc> npcs)
    {
        var gathered = false;
        foreach (var npc in npcs)
        {
            if (!IsUnawareSentry(npc) || !npc.IsSpawned || !npc.State.IsAlive || isExternallyControlled(npc.Address))
                continue;

            if (!gathered)
            {
                partyPositions.Clear();
                if (CollectPartyPositions != null)
                    CollectPartyPositions(partyPositions);
                else if (Core.Services.ObjectTable.LocalPlayer is { } player && combatEngine.State.PlayerState.IsAlive)
                    partyPositions.Add(player.Position);
                gathered = true;
            }

            var pos = GetNpcPosition(npc);
            var sight = npc.Behavior.AggroRange > 0f ? MathF.Max(8f, npc.Behavior.AggroRange) : SentrySightRange;
            var noticed = false;
            foreach (var friend in partyPositions)
            {
                if (MathF.Abs(friend.Y - pos.Y) <= SentrySightHeight && FlatDistance(friend, pos) <= sight)
                {
                    noticed = true;
                    break;
                }
            }

            if (!noticed)
            {
                foreach (var other in npcs)
                {
                    if (other == npc || !other.IsSpawned || !other.State.IsAlive || !other.IsEngaged)
                        continue;
                    if (FlatDistance(GetNpcPosition(other), pos) <= SentryAlertRadius)
                    {
                        noticed = true;
                        break;
                    }
                }
            }

            if (!noticed)
                continue;

            npc.HasPatrolGoal = false;
            npc.PatrolTimer = 0f;
            EngageNpc(npc);
        }
    }

    // A patrol: walk to a point near the post, stand a while, walk to another.
    private const float PatrolWalkSpeed = 2.0f;
    private const float PatrolNearest = 4f;
    private const float PatrolFurthest = 9f;
    private const float PatrolReach = 0.6f;
    private const float PatrolLegSeconds = 25f;

    /// <summary>Where a patrolling sentry is walking now, or null while it stands between legs.</summary>
    private Vector3? PatrolGoal(SimulatedNpc npc, Vector3 npcPos, float deltaTime)
    {
        npc.PatrolTimer -= deltaTime;
        if (npc.HasPatrolGoal && (FlatDistance(npcPos, npc.PatrolGoal) <= PatrolReach || npc.PatrolTimer <= 0f))
        {
            npc.HasPatrolGoal = false;
            npc.PatrolTimer = 2f + 4f * Random.Shared.NextSingle();
        }
        if (!npc.HasPatrolGoal && npc.PatrolTimer <= 0f)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var reach = PatrolNearest + (PatrolFurthest - PatrolNearest) * Random.Shared.NextSingle();
            npc.PatrolGoal = npc.SpawnPosition + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * reach;
            npc.HasPatrolGoal = true;
            npc.PatrolTimer = PatrolLegSeconds;
        }
        return npc.HasPatrolGoal ? npc.PatrolGoal : null;
    }

    // --- Movement on the navigation mesh (phase B of docs/AI_MOVEMENT_REWRITE.md) --------------------

    // Close in to a comfortable gap for melee; ranged stops just inside its reach. Moving resumes only
    // once the target is out of reach, so a small step back does not set it walking.
    private const float MeleeStandGap = 1.0f;
    private const float RangedStandMargin = 1.0f;
    private const float StopTolerance = 0.25f;
    private const float MeleeReachLimit = 5f;

    /// <summary>
    /// The space between the two bodies, horizontally: centre distance less both hitbox radii. Ranges
    /// in the game are measured this way, so a skill's authored range is used as it is.
    /// </summary>
    private float CrowdGap(SimulatedNpc npc, SimulatedEntityState target, Vector3 npcPos, Vector3 targetPos)
        => MathF.Max(0f, FlatDistance(npcPos, targetPos) - npc.HitboxRadius - EntityHitboxRadius(target));

    /// <summary>
    /// How far this enemy can attack from right now: its auto-attack (a caster's is a melee swing), or
    /// the longest skill it could use this moment (off cooldown, unlocked by HP, affordable).
    /// </summary>
    private float ReachNow(SimulatedNpc npc)
    {
        var reach = npc.Behavior.AutoAttackStyle switch
        {
            NpcAttackStyle.Ranged => ScaleNpcNonMeleeRange(npc.Behavior.AutoAttackRange),
            NpcAttackStyle.Magic => ScaleNpcMeleeRange(MagicAutoAttackMeleeRange),
            _ => ScaleNpcMeleeRange(npc.Behavior.AutoAttackRange),
        };
        var hpShare = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 1f;
        foreach (var skill in npc.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0 || hpShare > skill.HpThreshold)
                continue;
            if (npc.State.CurrentMp < SkillMpCost(npc, skill.ActionId))
                continue;
            reach = MathF.Max(reach, SkillReach(npc, skill));
        }
        return reach;
    }

    /// <summary>
    /// Decide where this enemy goes and hand it to the crowd. Going for the lord's body: there. A sentry
    /// that has not noticed the party: its patrol, at a walk, or nowhere. Fighting: stand when the target
    /// is within reach, otherwise the nearest point at its standing gap. Casting: stand (its area was
    /// fixed when the cast began). False when the enemy is not on the mesh.
    /// </summary>
    private bool TryCrowdMove(SimulatedNpc npc, float deltaTime)
    {
        var crowd = Crowd!;
        var npcPos = GetNpcPosition(npc);
        var speed = npc.Behavior.MoveSpeed > 0 ? npc.Behavior.MoveSpeed * 1.5f : 8.0f;
        Vector3? goal = null;
        npc.WalkPace = false;

        var target = combatEngine.GetNpcTarget(npc);
        var fighting = npc.AiState is NpcAiState.Engaging or NpcAiState.Combat or NpcAiState.Chasing;
        if (npc.SeizeGoal is { } seize)
        {
            goal = seize;
            npc.InReach = false;
        }
        else if (IsUnawareSentry(npc))
        {
            npc.InReach = false;
            if (npc.Patrols && PatrolGoal(npc, npcPos, deltaTime) is { } patrol)
            {
                goal = patrol;
                speed = PatrolWalkSpeed;
                npc.WalkPace = true;
            }
        }
        else if (fighting && target is { IsAlive: true } && !npc.State.IsCasting)
        {
            var targetPos = combatEngine.GetSimulatedEntityPosition(target);
            var reach = ReachNow(npc);
            var gap = CrowdGap(npc, target, npcPos, targetPos);
            var stand = reach <= MeleeReachLimit
                ? MathF.Min(MeleeStandGap, reach * 0.5f)
                : MathF.Max(MeleeStandGap, reach - RangedStandMargin);

            if (npc.InReach && gap > reach)
                npc.InReach = false;
            else if (!npc.InReach && gap <= stand + StopTolerance)
                npc.InReach = true;

            var bodies = npc.HitboxRadius + EntityHitboxRadius(target);
            if (TacticsGoal(npc, npcPos, targetPos, stand, bodies, deltaTime, out var tacticsSpeed) is { } tactic)
            {
                goal = tactic;
                speed *= tacticsSpeed;
            }
            else if (!npc.InReach)
            {
                var away = new Vector3(npcPos.X - targetPos.X, 0f, npcPos.Z - targetPos.Z);
                away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitZ;
                goal = targetPos + away * (stand + bodies);
                speed *= ChargeScale(npc, gap);
            }
        }
        else
            npc.InReach = false;

        if (!crowd.Drive(npc.Address, npcPos, npc.HitboxRadius, speed, goal))
            return false;
        npc.CrowdDriven = true;
        return true;
    }

    /// <summary>
    /// Put every enemy the crowd moves where it is (the crowd is stepped once a frame by the plugin,
    /// after both sides have set their goals), facing where it goes or its target.
    /// </summary>
    private void ApplyCrowdMotion(float deltaTime)
    {
        var crowd = Crowd;
        if (crowd == null)
            return;

        foreach (var npc in crowdDriven)
        {
            if (npc.BattleChara == null || !crowd.TryGetMotion(npc.Address, out var position, out var velocity))
                continue;

            var gameObj = (GameObject*)npc.BattleChara;
            // Standing on a corpse: the foot solver asks the body up by what both legs agree on.
            var lift = config.NpcCollisionActive ? MathF.Max(0f, footIkSolver.GetPelvisOffset(npc.Address)) : 0f;
            if (config.NpcCollisionActive)
                footIkSolver.ReportRootLift(npc.Address, lift);
            position.Y += lift + config.DefaultNpcHeightOffset;
            movementBlockHook.SetApproachPosition(gameObj, position.X, position.Y, position.Z);

            var flat = new Vector3(velocity.X, 0f, velocity.Z);
            if (flat.LengthSquared() > 0.3f * 0.3f)
            {
                StartApproachMoveAnim(npc, deltaTime, npc.WalkPace ? ActorVisualStateController.NormalWalkTimelineId : (ushort)0);
                ForceRotateToward(npc, position + flat, deltaTime);
            }
            else
            {
                StopApproachMoveAnim(npc);
                if (combatEngine.GetNpcTarget(npc) is { IsAlive: true } target)
                    ForceRotateToward(npc, combatEngine.GetSimulatedEntityPosition(target), deltaTime);
            }
        }
    }

    public void EngageNpc(SimulatedNpc npc)
    {
        if (npc.AiState == NpcAiState.Dead || npc.AiState == NpcAiState.Resetting)
            return;

        if (npc.AiState == NpcAiState.Idle)
        {
            npc.AiState = NpcAiState.Engaging;
            StaggerTimers(npc);
            ApplyEngageJitter(npc);
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

        // Natural MP regen (double the player's rate).
        npc.State.RegenerateMp(EnemyMpRegenPerSecond, deltaTime);

        DiscardInvalidNpcActionState(npc);

        var npcPos = GetNpcPosition(npc);
        float distToPlayer = Vector3.Distance(npcPos, playerPos);

        switch (npc.AiState)
        {
            case NpcAiState.Idle:
                // (A sentry walking its patrol, or an enemy going for the lord's body, keeps its
                // stride: the approach pass owns its look.)
                if (npc.BattleChara != null && !(npc.IsSentry && npc.Patrols && npc.HasPatrolGoal) && npc.SeizeGoal == null)
                    ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
                break;

            case NpcAiState.Engaging:
                if (!npc.CrowdDriven)
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
                npc.AiState = NpcAiState.Combat;
                TickCombat(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                break;

            case NpcAiState.Dead:
                if (npc.BattleChara != null)
                    ActorVisualStateController.ApplyDead((Character*)npc.BattleChara, npc.VisualState);
                npc.DeadTimer += deltaTime;
                break;

            case NpcAiState.Resetting:
                npc.State.CurrentHp = npc.State.MaxHp;
                npc.AiState = NpcAiState.Idle;
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

        // Enhanced tactics: a fleeing enemy holds its attacks until it has regrouped.
        if (IsTacticsFleeing(npc))
            return;

        var targetPos = combatEngine.GetSimulatedEntityPosition(simulatedTarget);
        var distToTarget = CrowdGap(npc, simulatedTarget, npcPos, targetPos);
        var targetEntityId = simulatedTarget.EntityId;

        if (npc.State.IsCasting && npc.State.CastTargetId != targetEntityId)
        {
            ClearNpcActionState(npc);
            if (npc.BattleChara != null)
                ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);
        }

        // The crowd pass faces the enemies it moves; one standing off the mesh faces its target here.
        if (!npc.CrowdDriven)
            ForceRotateToward(npc, targetPos, deltaTime);

        // A telegraphed cast runs to completion wherever its target goes: the area is fixed, so
        // walking out of it is the dodge. Letting the range gate below pause it would let the
        // player freeze a cast just by stepping back.
        if (npc.CastArea != null && TickSnapshotCast(npc, deltaTime))
            return;

        // Out of reach: the crowd is walking it in.
        if (distToTarget > ReachNow(npc))
            return;

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

        // Already mid-attack (telegraph windup / late-guard grace / recovery) — do not commit a new
        // attack, or the next swing overlaps the current one. No-op outside telegraphed modes.
        if (isTelegraphBusy(npc.SimulatedEntityId))
        {
            if (npc.BattleChara != null)
                ActorVisualStateController.ApplyActionLocked((Character*)npc.BattleChara, npc.VisualState);
            return;
        }

        if (npc.BattleChara != null)
            ActorVisualStateController.ApplyCombatIdle((Character*)npc.BattleChara, npc.VisualState);

        // Try skills — collect everything ready (off cooldown, in range, hp-gated) and fire one at
        // RANDOM, so a humanoid enemy varies its real rotation instead of always leading with the
        // same top skill. Cooldowns still gate reuse. (Allocates only when a skill is actually ready.)
        float hpPercent = (float)npc.State.CurrentHp / npc.State.MaxHp;
        List<NpcSkill>? readySkills = null;
        foreach (var skill in npc.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0)
                continue;
            if (distToTarget > SkillReach(npc, skill))
                continue;
            if (hpPercent > skill.HpThreshold)
                continue;
            // Only consider skills the enemy can currently afford — otherwise it auto-attacks to
            // recover MP. Enemies prioritise skills whenever they have the MP for one.
            if (npc.State.CurrentMp < SkillMpCost(npc, skill.ActionId))
                continue;

            (readySkills ??= new List<NpcSkill>()).Add(skill);
        }
        // A ready skill still needs a slot from the shared budget. Falling through instead of
        // returning is deliberate: the enemy auto-attacks rather than standing still, and the
        // skill's cooldown is left unspent so it goes as soon as the budget allows.
        if (readySkills != null && TryTakeSkillToken())
        {
            var skill = readySkills[Random.Shared.Next(readySkills.Count)];
            var mpCost = SkillMpCost(npc, skill.ActionId);
            var castTime = EffectiveCastTime(skill);

            // Action Mode skips the engine cast bar — the telegraph executor owns the
            // windup uniformly (cast time feeds the telegraph duration).
            if (!config.ActionMode && castTime > 0)
            {
                npc.State.IsCasting = true;
                npc.State.CastActionId = skill.ActionId;
                npc.State.CastTimeTotal = castTime;
                npc.State.CastTimeElapsed = 0;
                npc.State.CastTargetId = targetEntityId;
                npc.State.CastInterruptible = skill.Interruptible;
                npc.CurrentCastSkill = skill;
                npc.State.CurrentMp = Math.Max(0, npc.State.CurrentMp - mpCost);
                BeginSnapshotCast(npc, skill, simulatedTarget, castTime);

                combatEngine.AddLogEntry(
                    $"{npc.Name} begins casting {skill.Name}...",
                    CombatLogType.Info);
            }
            else
            {
                var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                    skill.ActionId, targetEntityId, skill.Potency, skill.AttackStyle, skill.Radius, castTime));
                if (ok)
                {
                    npc.State.CurrentMp = Math.Max(0, npc.State.CurrentMp - mpCost);
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
            if (npc.Behavior.AutoAttackStyle == NpcAttackStyle.Magic &&
                distToTarget > ScaleNpcMeleeRange(MagicAutoAttackMeleeRange))
                return;

            npc.AutoAttackTimer = npc.Behavior.AutoAttackDelay / ActionPace();
            var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                npc.Behavior.AutoAttackActionId, targetEntityId, npc.Behavior.AutoAttackPotency,
                npc.Behavior.AutoAttackStyle, 0f, 0f, IsAutoAttack: true));
            if (ok)
            {
                // Auto-attacks recover MP (double the player's basic-attack restore) so a depleted
                // enemy earns its way back into skill range.
                npc.State.CurrentMp = Math.Min(npc.State.MaxMp, npc.State.CurrentMp + EnemyAutoAttackMpRestore);
                npc.State.AnimationLock = MathF.Max(npc.State.AnimationLock, 0.6f);
            }
        }
    }

    /// <summary>
    /// What an enemy pays for a skill, as a fraction of its OWN pool: the same fraction the player pays
    /// of theirs (prices are authored for the player's 10000 pool; a cast-time spell is 5096–8500).
    /// Charging the raw price against an enemy's half-size pool made caster spells unaffordable; the
    /// same share keeps them affordable and as dear as the player's: a big nuke, then auto-attacks to
    /// earn MP back.
    /// </summary>
    private int SkillMpCost(SimulatedNpc npc, uint actionId)
    {
        return CombatBudgetModel.EnemySkillMpCost(combatEngine.GetActionData(actionId)?.MpCost ?? 0, npc.State.MaxMp);
    }

    /// <summary>
    /// Cast time actually used. Generated kits keep <see cref="NpcSkill.CastTime"/> at 0 (a cast
    /// with nothing on screen was 3 silent seconds); with real-shape telegraphs on, a skill that has a
    /// drawable area gets its real cast back as a dodgeable windup. Skills with no area stay instant.
    /// </summary>
    // Real casts longer than this are shortened to it.
    private const float EnemyMaxCastSeconds = 5f;

    private float EffectiveCastTime(NpcSkill skill)
    {
        if (skill.CastTime > 0f || skill.NativeCastTime <= 0f ||
            !config.EnemyTelegraphRealShapes)
            return skill.CastTime;

        var data = combatEngine.GetActionData(skill.ActionId);
        var hasArea = skill.Radius > 0f ||
                      (data != null && data.Shape != AoeShape.Single &&
                       (data.Radius > 0f || CastTypeMapping.IsCharge(data.CastType)));
        return hasArea
            ? MathF.Min(skill.NativeCastTime, EnemyMaxCastSeconds)
            : skill.CastTime;
    }

    /// <summary>
    /// Real-shape option: freeze the cast's area now, the way the game places an omen when the
    /// cast bar starts, and draw it for the length of the cast.
    /// </summary>
    private void BeginSnapshotCast(SimulatedNpc npc, NpcSkill skill, SimulatedEntityState target, float castTime)
    {
        npc.CastArea = null;
        if (!config.EnemyTelegraphRealShapes)
            return;

        var req = new CombatSimulator.ActionCombat.NpcAttackRequest(
            skill.ActionId, target.EntityId, skill.Potency, skill.AttackStyle, skill.Radius, castTime);
        var shape = CombatSimulator.ActionCombat.TelegraphSystem.SnapshotShape(combatEngine, npc, req, target);
        if (!shape.IsArea)
            return;

        npc.CastArea = shape;
        var actionId = skill.ActionId;
        AreaMarkers?.Add(npc.SimulatedEntityId, actionId, shape, castTime,
            () => npc.State.IsCasting && npc.State.CastActionId == actionId && npc.CastArea != null);
    }

    /// <summary>
    /// Advance a snapshotted cast. Returns true while it owns this frame. A cast the engine
    /// stopped (interrupt/stun) spends the skill, so it is not immediately recast.
    /// </summary>
    private bool TickSnapshotCast(SimulatedNpc npc, float deltaTime)
    {
        var skill = npc.CurrentCastSkill;
        if (skill == null)
        {
            npc.CastArea = null;
            return false;
        }

        if (!npc.State.IsCasting)
        {
            skill.CooldownRemaining = skill.Cooldown / ActionPace();
            npc.CurrentCastSkill = null;
            npc.CastArea = null;
            combatEngine.AddLogEntry($"{npc.Name}'s {skill.Name} is interrupted.", CombatLogType.Info);
            return false;
        }

        if (npc.BattleChara != null)
            ActorVisualStateController.ApplyActionLocked((Character*)npc.BattleChara, npc.VisualState);
        npc.State.CastTimeElapsed += deltaTime;
        if (npc.State.CastTimeElapsed < npc.State.CastTimeTotal)
            return true;

        var area = npc.CastArea;
        npc.State.IsCasting = false;
        npc.CastArea = null;
        npc.CurrentCastSkill = null;
        // CastTime 0: the wait already happened here; a telegraphed executor (Fighting Mode)
        // must not add the cast again as lead-in.
        var ok = combatModeRouter.AttackExecutor.Execute(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
            skill.ActionId, npc.State.CastTargetId, skill.Potency, skill.AttackStyle, skill.Radius, 0f, Area: area));
        if (ok)
            skill.CooldownRemaining = skill.Cooldown / ActionPace();
        npc.State.AnimationLock = MathF.Max(npc.State.AnimationLock, 0.6f);
        return true;
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
        npc.CastArea = null; // its marker sees the cast end and leaves on the next tick
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

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    /// <summary>
    /// The NPC Scale effect is written directly to DrawObject, so the game object's authored combat
    /// radius does not follow it. Keep every AI-authored melee distance proportional to the visual
    /// model, then apply the explicit gameplay range multiplier to both hit eligibility and movement
    /// planning so a longer-range enemy actually stops chasing at that longer range.
    /// </summary>
    private float NpcMeleeRangeScale()
    {
        var visualScale = config.EnableNpcScale
            ? Math.Clamp(config.NpcScale, 0.01f, 3.0f)
            : 1.0f;
        return visualScale * NpcAttackRangeScale();
    }

    private float NpcAttackRangeScale()
        => Math.Clamp(config.NpcAttackRangeScale, 0.1f, 100f);

    private float ScaleNpcNonMeleeRange(float range)
        => MathF.Max(0.001f, MathF.Max(0f, range) * NpcAttackRangeScale());

    private float ScaleNpcMeleeRange(float range)
        => MathF.Max(0.001f, MathF.Max(0f, range) * NpcMeleeRangeScale());

    /// <summary>A skill's authored range (measured between the bodies), times the range scale.</summary>
    private float SkillReach(SimulatedNpc npc, NpcSkill skill)
    {
        var style = skill.AttackStyle == NpcAttackStyle.Auto
            ? npc.Behavior.AutoAttackStyle
            : skill.AttackStyle;
        return style == NpcAttackStyle.Melee
            ? ScaleNpcMeleeRange(skill.Range)
            : ScaleNpcNonMeleeRange(skill.Range);
    }

    private float EntityHitboxRadius(SimulatedEntityState entity)
    {
        nint address = 0;
        if (entity.IsPlayer)
            address = Core.Services.ObjectTable.LocalPlayer?.Address ?? 0;
        else
            address = combatEngine.ResolveExternalEntityAddress?.Invoke(entity.EntityId) ?? 0;
        if (address == 0)
            return EnemyMovementMath.BaselineHitboxRadius;
        var r = ((GameObject*)address)->HitboxRadius;
        return r > 0.01f ? r : EnemyMovementMath.BaselineHitboxRadius;
    }


    private void StartApproachMoveAnim(SimulatedNpc npc, float deltaTime, ushort timelineId = 0)
    {
        if (npc.BattleChara == null)
            return;
        movingNpcs[npc.Address] = npc.VisualState;
        var character = (Character*)npc.BattleChara;
        if (timelineId != 0)
            ActorVisualStateController.ApplyMoving(character, npc.VisualState, deltaTime, timelineId);
        else
            ActorVisualStateController.ApplyMoving(character, npc.VisualState, deltaTime);
    }

    private void StopApproachMoveAnim(SimulatedNpc npc)
    {
        if (!movingNpcs.Remove(npc.Address, out var visual))
            return;
        try { ActorVisualStateController.ClearMovement((Character*)npc.Address, visual); }
        catch { }
    }

    private void StopAllMoveAnims()
    {
        foreach (var (address, visual) in movingNpcs)
        {
            try { ActorVisualStateController.ClearMovement((Character*)address, visual); }
            catch { }
        }
        movingNpcs.Clear();
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

        var rotSpeed = NpcTurnRate(npc) * deltaTime;
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
        StopAllMoveAnims();
    }
}
