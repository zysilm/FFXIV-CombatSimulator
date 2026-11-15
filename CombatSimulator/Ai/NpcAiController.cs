// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Combat;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Ai;

/// <summary>
/// The enemies' controller: the one combatant controller (<see cref="CombatantController{TSelf}"/>) for
/// the enemy side, ticked after the crowd step and only in combat. What stays here is the enemies' own
/// (docs/PLUGIN_ARCHITECTURE.md §2.2, §3.3): engaging, sentries and patrols, call for help, tactics, casts
/// and their telegraph snapshots, the passive pack, their movement pass and their death state.
/// </summary>
public unsafe partial class NpcAiController : CombatantController<SimulatedNpc>
{
    public const float PlayerTriggeredEngageDelay = 0.5f;

    private readonly Func<nint, bool> isExternallyControlled;
    // Enemies the crowd has set running, with the look to clear when they stop.
    private readonly Dictionary<nint, ActorVisualState> movingNpcs = new();
    private readonly HashSet<nint> passiveEnemyAddresses = new();
    // Caster/magic enemies auto-attack at melee reach (their spells are ranged, the basic swing is not):
    // ReachPolicy.Enemy.MagicAutoReach (3.5, scaled as melee).
    // Enemy MP economy: enemies have half the player's max MP (set at spawn) but recover at double
    // rate, and only from natural regen + auto-attacks — never from guarding (they don't guard). This
    // makes them lead with skills while they have MP, then fall back to auto-attacks to recover.
    // The numbers live in CombatBudgetModel (the rate: CombatantController.BeginTurn), which also
    // predicts what a kit sustains from them.

    // Auto-engage countdown: when >= 0, every Tick decrements; on reaching
    // 0 we call EngageNpc on each selected NPC. Negative = inactive.
    private float pendingAutoEngageDelay = -1f;
    // Enemies that were held (EnemyLayer.Controlled) when the auto-engage fired: engaged once they are let go.
    private readonly HashSet<uint> missedAutoEngage = new();

    /// <summary>The controller over its seams (the plugin's constructor is in NpcAiController.Game.cs).</summary>
    public NpcAiController(
        ICombatLoopEngine combatEngine,
        ICombatWorld world,
        IAttackSink attackSink,
        ICombatAiSettings config,
        IPluginLog log,
        Action<List<Vector3>>? collectPartyPositions,
        Func<nint, bool>? isExternallyControlled = null,
        Func<uint, bool>? isTelegraphBusy = null)
        : base(combatEngine, world, attackSink, config, log, isTelegraphBusy)
    {
        this.collectPartyPositions = collectPartyPositions;
        this.isExternallyControlled = isExternallyControlled ?? (_ => false);
        stepSink = new EnemyStepSink(this);

        combatEngine.OnSimulationStarted += ScheduleAutoEngage;
        combatEngine.OnSimulationReset += OnSimulationResetOrStop;
    }

    private readonly List<SimulatedNpc> crowdDriven = new();

    /// <summary>
    /// Fills a list with where the living party stands (player and companions); sentries watch for them. Given by
    /// constructor (the allies' controller's <c>CollectPartyPositions</c>); null: the player alone.
    /// </summary>
    private readonly Action<List<Vector3>>? collectPartyPositions;

    /// <summary>Where telegraphed casts register their snapshotted area (null = none drawn).</summary>
    public ICastAreaMarkers? AreaMarkers { private get; set; }

    // What the enemies did, counted per frame and written to the log every few seconds of a fight
    // (the mirror of the allies' line in CombatCompanionManager.Crowd.cs). Counting reads what the
    // movement pass has just decided; it decides nothing.
    private const int EnemyInReach = 0, EnemyWalkingIn = 1, EnemyWaiting = 2, EnemyGoingForBody = 3, EnemyFleeing = 4,
        EnemyCasting = 5, EnemyPatrolling = 6, EnemyAtPost = 7, EnemyIdle = 8, EnemyOffMesh = 9, EnemyControlled = 10;
    private static readonly string[] EnemyActivityNames =
    {
        "in reach", "walking in", "waiting a turn", "going for the body", "fleeing", "casting",
        "patrolling", "at its post", "idle", "off the mesh", "under control",
    };
    private readonly CombatActivityTally enemyActivity = new(EnemyActivityNames, listUnseen: true);

    private void NoteEnemyActivity(int activity) => enemyActivity.Note(activity);

    /// <summary>
    /// What an enemy on the mesh did this frame, read from what TryCrowdMove left behind, in the order
    /// it decides: a caster stands; going for the body (or fighting its guard); an unaware sentry (on
    /// patrol, or at its post); no living target; running from the fight; waiting for a melee place; in
    /// reach or walking in.
    /// </summary>
    private int EnemyActivityOf(SimulatedNpc npc, bool fightingLiveTarget)
    {
        if (npc.State.IsCasting)
            return EnemyCasting;
        if (npc.SeizeGoal != null)
            return npc.InReach ? EnemyInReach : EnemyGoingForBody;
        if (IsUnawareSentry(npc))
            return npc.Patrols ? EnemyPatrolling : EnemyAtPost;
        if (!fightingLiveTarget)
            return EnemyIdle;
        if (npc.AiState == CombatantAiState.Combat && IsTacticsFleeing(npc))
            return EnemyFleeing;
        if (config.EnhancedEnemyTactics && IsMeleeFighter(npc) &&
            tacticsStates.TryGetValue(npc.SimulatedEntityId, out var tactics) && !tactics.Admitted)
            return EnemyWaiting;
        return npc.InReach ? EnemyInReach : EnemyWalkingIn;
    }

    private void ReportEnemyActivity()
    {
        if (enemyActivity.TakeShares() is not { } shares)
            return;
        var allies = 0;
        foreach (var entity in combatEngine.State.Entities.Values)
            if (entity.IsCompanion && entity.IsAlive) allies++;
        log.Info($"Enemies (share of enemy-frames over 5 s, {allies} allies alive): {shares}.");
    }

    private void ScheduleAutoEngage()
    {
        // (A fight starts or is reset: the enemies' summary starts a new 5 s window with it.)
        enemyActivity.Restart();
        missedAutoEngage.Clear();

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
        missedAutoEngage.Clear();

        // OnSimulationReset fires from both StopSimulation and ResetState.
        // StopSimulation flips IsActive false before invoking — skip auto-engage
        // there since there are no selected NPCs left to engage.
        if (!combatEngine.State.IsActive) { pendingAutoEngageDelay = -1f; return; }
        ScheduleAutoEngage();
    }

    public override void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> npcs)
    {
        var player = world.LocalPlayer;
        if (player == null)
            return;

        tickNpcs = npcs;

        // The shared skill-start budget of the enemy side (config.NpcSkillRateLimit): this controller
        // only ever ticks enemies, so it throttles the hostile side alone.
        RefillSkillTokens(deltaTime);

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
                {
                    if (npc.IsSentry)
                        continue;
                    var layer = LayerOf(npc);
                    if (EnemyLayers.CanBeWoken(layer))
                        EngageNpc(npc);
                    // Held by someone else at the moment of the auto-engage: it joins once it is let go.
                    else if (layer == EnemyLayer.Controlled)
                        missedAutoEngage.Add(npc.SimulatedEntityId);
                }
            }
        }
        if (missedAutoEngage.Count > 0)
        {
            foreach (var npc in npcs)
            {
                if (!missedAutoEngage.Contains(npc.SimulatedEntityId))
                    continue;
                var layer = LayerOf(npc);
                if (layer == EnemyLayer.Controlled)
                    continue;
                missedAutoEngage.Remove(npc.SimulatedEntityId);
                if (EnemyLayers.CanBeWoken(layer))
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
                if (!npc.IsSentry && EnemyLayers.CanBeWoken(LayerOf(npc)))
                    EngageNpc(npc);
        }

        var playerPos = player.Position;
        var playerEntityId = player.EntityId;

        // Read the player's actual GameObjectId from the game object (includes Type byte)
        var playerGameObjectId = world.GameObjectIdAt(player.Address);
        var targetByNpcId = new Dictionary<uint, (SimulatedEntityState State, Vector3 Position)>();

        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned)
                continue;
            if (isExternallyControlled(npc.Address))
                continue;

            if (config.NpcCollisionActive)
                world.TrackFootIk(npc.Address, deltaTime);

            // Keep active targets in battle stance (drawn weapon / combat idle)
            if (npc.BattleChara != null && npc.AiState != CombatantAiState.Dead)
                world.SetBattleStance(npc);

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
                bool shouldTarget = npc.AiState != CombatantAiState.Dead
                                 && npc.AiState != CombatantAiState.Resetting;
                if (shouldTarget)
                    world.SetTargetId(npc, playerGameObjectId);
                else if (world.TargetObjectIdOf(npc) == playerEntityId)
                    world.SetTargetId(npc, default);
            }
            else if (config.EnableNpcTargetPlayer && npc.BattleChara != null)
            {
                if (world.TargetObjectIdOf(npc) == playerEntityId)
                    world.SetTargetId(npc, default);
            }

            TickNpc(npc, deltaTime, targetPos, targetEntityId);
        }

        TickCallForHelp(npcs, targetByNpcId);
        TickSentries(npcs, targetByNpcId);
        UpdateMeleeAdmission(npcs, targetByNpcId);

        // Movement: every enemy is moved by the crowd on the plugin's navigation mesh
        // (docs/AI_MOVEMENT_REWRITE.md); there is no other mover. Without a mesh under it (the first
        // moment of a battle, before the mesh is built) an enemy stands where it is.
        crowdDriven.Clear();
        var crowd = Crowd;
        var crowdReady = crowd is { Ready: true };
        // The player is in the crowd as something to walk round.
        if (crowdReady && combatEngine.State.PlayerState.IsAlive)
            crowd!.SetObstacle(player.Address, playerPos, world.HitboxRadiusAt(player.Address));
        foreach (var npc in npcs)
        {
            npc.CrowdDriven = false;
            if (!npc.IsSpawned || npc.BattleChara == null)
                continue;
            if (isExternallyControlled(npc.Address))
            {
                NoteEnemyActivity(EnemyControlled);
                continue;
            }
            // Freshly spawned clones are pinned to their spawn placement by NpcSpawner for a moment.
            if (npc.SpawnPlacementFrames > 0 || npc.AiState == CombatantAiState.Dead)
                continue;
            // Real NPCs of the map: keep the server from moving them under us.
            if (!npc.IsClientControlled)
                world.AddApproachNpc(npc.Address);
            if (npc.AiState == CombatantAiState.Resetting)
                npc.AiState = npc.State.IsAlive ? CombatantAiState.Idle : CombatantAiState.Dead;

            if (crowdReady && TryCrowdMove(npc, deltaTime))
            {
                crowdDriven.Add(npc);
                continue;
            }
            NoteEnemyActivity(EnemyOffMesh);
            StopApproachMoveAnim(npc);
        }
        ApplyCrowdMotion(deltaTime);
        ReportEnemyActivity();
    }

    /// <summary>
    /// Clear approach state. Called on simulation stop.
    /// </summary>
    public void ClearApproachState()
    {
        world.ClearApproachNpcs();
        StopAllMoveAnims();
        passiveEnemyAddresses.Clear();
    }

    private void TickPassiveEnemyPack(IReadOnlyList<SimulatedNpc> npcs)
    {
        pendingAutoEngageDelay = -1f;
        world.ClearApproachNpcsExcept(isExternallyControlled);
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
                npc.AiState = CombatantAiState.Dead;
                if (npc.BattleChara != null)
                    world.ApplyDead(npc);
                continue;
            }

            npc.AiState = CombatantAiState.Idle;
            liveAddresses.Add(npc.Address);
            if (npc.BattleChara != null)
            {
                world.SetTargetId(npc, default);
                if (passiveEnemyAddresses.Add(npc.Address))
                    world.ClearBattleStance(npc);
            }
        }

        passiveEnemyAddresses.RemoveWhere(address => !liveAddresses.Contains(address));
    }

    private static bool IsUnawareSentry(SimulatedNpc npc)
        => npc.IsSentry && npc.AiState == CombatantAiState.Idle;

    /// <summary>The layer that drives this enemy this frame (<see cref="EnemyLayer"/>).</summary>
    private EnemyLayer LayerOf(SimulatedNpc npc)
        => EnemyLayers.Of(isExternallyControlled(npc.Address), npc.State.IsAlive, npc.SeizeGoal != null,
            npc.AiState, npc.IsSentry, npc.Patrols);

    /// <summary>Whether this enemy wakes the unaware sentries around it (<see cref="EnemyLayers.RaisesAlarm"/>).</summary>
    private bool RaisesAlarm(SimulatedNpc npc,
        Dictionary<uint, (SimulatedEntityState State, Vector3 Position)> targetByNpcId)
        => npc.IsSpawned &&
           EnemyLayers.RaisesAlarm(LayerOf(npc),
               targetByNpcId.TryGetValue(npc.SimulatedEntityId, out var target) && target.State.IsAlive);

    // A sentry notices the party within sight, or a neighbour that is fighting someone alive.
    private const float SentrySightRange = 14f;
    private const float SentryAlertRadius = 10f;
    private const float SentrySightHeight = 8f;
    private readonly List<Vector3> partyPositions = new();

    private void TickSentries(IReadOnlyList<SimulatedNpc> npcs,
        Dictionary<uint, (SimulatedEntityState State, Vector3 Position)> targetByNpcId)
    {
        var gathered = false;
        foreach (var npc in npcs)
        {
            if (!npc.IsSpawned || !EnemyLayers.IsUnaware(LayerOf(npc)))
                continue;

            if (!gathered)
            {
                partyPositions.Clear();
                if (collectPartyPositions != null)
                    collectPartyPositions(partyPositions);
                else if (world.LocalPlayer is { } player && combatEngine.State.PlayerState.IsAlive)
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
                    if (other == npc || !RaisesAlarm(other, targetByNpcId))
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

    // A patrol: walk to a point near the post, stand a while, walk to another. Every point is on the
    // ground the party can reach (the crowd's reachable piece of mesh); a point behind a wall or off a
    // ledge is not taken (another is drawn). While the crowd cannot tell what is reachable (the player was
    // not on the mesh when it was built), any walkable floor near the point is taken. A sentry that does
    // not patrol walks back to its post when it stands further than PostReturnDistance from it (after a
    // fight). A point counts as reached within PatrolReach plus the walker's own radius (and, for a post
    // others may stand around, PostCrowdSlack more); a walk that does not get there in PatrolLegSeconds stops.
    private const float PatrolWalkSpeed = 2.0f;
    private const float PatrolNearest = 4f;
    private const float PatrolFurthest = 9f;
    private const float PatrolReach = 0.6f;
    private const float PatrolLegSeconds = 25f;
    private const int PatrolPointTries = 6;
    private const float PatrolPointSnap = 2f;
    private const float PostReturnDistance = 3f;
    private const float PostCrowdSlack = 0.5f;
    private const float PostRestSeconds = 5f;
    // A post itself is moved onto reachable ground once, searched this far out from where it was put; posts
    // of different sentries are kept at least PostSpacing apart (else they crowd one point for good).
    private const float PostSearchRadius = 96f;
    private const float PostRetrySeconds = 1f;
    private const float PostSpacing = 1.5f;

    /// <summary>
    /// Where an unaware sentry is walking now, or null while it stands. Patrolling: legs between reachable
    /// points about its post. Holding its post: back to the post when it is away from it.
    /// </summary>
    private Vector3? SentryGoal(SimulatedNpc npc, ICrowdAgents crowd, Vector3 npcPos, float deltaTime)
    {
        EnsureReachablePost(npc, crowd, deltaTime);
        var radius = world.HitboxRadiusOf(npc);
        npc.PatrolTimer -= deltaTime;

        if (!npc.Patrols)
        {
            var away = FlatDistance(npcPos, npc.SpawnPosition);
            if (npc.HasPatrolGoal && (away <= PatrolReach + radius + PostCrowdSlack || npc.PatrolTimer <= 0f))
            {
                npc.HasPatrolGoal = false;
                npc.PatrolTimer = PostRestSeconds;
            }
            else if (!npc.HasPatrolGoal && away > PostReturnDistance && npc.PatrolTimer <= 0f)
            {
                npc.HasPatrolGoal = true;
                npc.PatrolTimer = PatrolLegSeconds;
            }
            npc.PatrolGoal = npc.SpawnPosition;
            return npc.HasPatrolGoal ? npc.PatrolGoal : null;
        }

        if (npc.HasPatrolGoal && (FlatDistance(npcPos, npc.PatrolGoal) <= PatrolReach + radius || npc.PatrolTimer <= 0f))
        {
            npc.HasPatrolGoal = false;
            npc.PatrolTimer = 2f + 4f * RandomSource.NextSingle();
        }
        if (!npc.HasPatrolGoal && npc.PatrolTimer <= 0f)
        {
            var judged = crowd.CanJudgeReach;
            for (var attempt = 0; attempt < PatrolPointTries && !npc.HasPatrolGoal; attempt++)
            {
                var angle = RandomSource.NextSingle() * MathF.Tau;
                var reach = PatrolNearest + (PatrolFurthest - PatrolNearest) * RandomSource.NextSingle();
                var wanted = npc.SpawnPosition + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * reach;
                Vector3 point;
                if (judged ? !crowd.TryReachableSpot(wanted, PatrolPointSnap, out point) : !crowd.TrySnapToFloor(wanted, out point))
                    continue;
                npc.PatrolGoal = point;
                npc.HasPatrolGoal = true;
            }
            // Nowhere to go about this post this time: stand a while and draw again.
            npc.PatrolTimer = npc.HasPatrolGoal ? PatrolLegSeconds : 2f + 4f * RandomSource.NextSingle();
        }
        return npc.HasPatrolGoal ? npc.PatrolGoal : null;
    }

    /// <summary>
    /// Once the crowd can tell what is reachable, a sentry's post (<see cref="Combatant.SpawnPosition"/>, where
    /// it patrols about and returns to) is put on the ground the party can reach: the nearest such spot to where
    /// it was put, stepped aside from posts other sentries already hold. A post behind a wall, under a cliff or
    /// on a ledge would leave it stuck there for the whole fight.
    /// </summary>
    private void EnsureReachablePost(SimulatedNpc npc, ICrowdAgents crowd, float deltaTime)
    {
        if (npc.PostOnReachableGround)
            return;
        npc.PostCheckTimer -= deltaTime;
        if (npc.PostCheckTimer > 0f)
            return;
        // Not found (no mesh under it yet, out of the mesh's area, or the crowd cannot tell): asked again in a while.
        if (!crowd.CanJudgeReach || !crowd.TryReachableSpot(npc.SpawnPosition, PostSearchRadius, out var post))
        {
            npc.PostCheckTimer = PostRetrySeconds;
            return;
        }
        post = SpacedPost(npc, crowd, post);
        npc.PostOnReachableGround = true;
        var moved = Vector3.Distance(post, npc.SpawnPosition);
        if (moved > 1f)
            log.Info($"Patrol: {npc.Name}'s post moved {moved:F1} m onto ground the party can reach.");
        npc.SpawnPosition = post;
        npc.HasPatrolGoal = false;
        npc.PatrolTimer = 0f;
    }

    /// <summary>
    /// The post itself, or, when another sentry's post is within <see cref="PostSpacing"/> of it (several posts
    /// behind one wall all come out at the same spot), the nearest reachable spot on two rings around it that is
    /// clear of every other post; the post itself when there is none.
    /// </summary>
    private Vector3 SpacedPost(SimulatedNpc npc, ICrowdAgents crowd, Vector3 post)
    {
        bool Clear(Vector3 p)
        {
            foreach (var other in tickNpcs)
                if (other != npc && other.IsSentry && other.PostOnReachableGround && FlatDistance(other.SpawnPosition, p) < PostSpacing)
                    return false;
            return true;
        }
        if (Clear(post))
            return post;
        for (var ring = 1; ring <= 2; ring++)
            for (var k = 0; k < 8 * ring; k++)
            {
                var angle = k * MathF.Tau / (8 * ring);
                var candidate = post + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * (PostSpacing * ring);
                if (crowd.TryReachableSpot(candidate, PostSpacing * 0.5f, out var spot) && Clear(spot))
                    return spot;
            }
        return post;
    }

    // --- Movement on the navigation mesh (phase B of docs/AI_MOVEMENT_REWRITE.md) --------------------

    // Close in to a comfortable gap for melee; ranged stops just inside its reach. Moving resumes only
    // once the target is out of reach, so a small step back does not set it walking (CombatantMath,
    // the same for allies).

    /// <summary>
    /// The space between the two bodies, horizontally: centre distance less both hitbox radii. Ranges
    /// in the game are measured this way, so a skill's authored range is used as it is.
    /// </summary>
    private float CrowdGap(SimulatedNpc npc, SimulatedEntityState target, Vector3 npcPos, Vector3 targetPos)
        => CombatantMath.BodyGap(npcPos, targetPos, world.HitboxRadiusOf(npc), EntityHitboxRadius(target));

    /// <summary>
    /// How far this enemy can attack from right now: its auto-attack (a caster's is a melee swing), or
    /// the longest skill it could use this moment (off cooldown, unlocked by HP, affordable).
    /// </summary>
    private float ReachNow(SimulatedNpc npc) => ReachNowOf(npc);

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
        var speed = MoveSpeedOf(npc);
        Vector3? goal = null;
        npc.WalkPace = false;

        var target = combatEngine.GetNpcTarget(npc);
        var fighting = npc.AiState is CombatantAiState.Engaging or CombatantAiState.Combat or CombatantAiState.Chasing;
        if (npc.SeizeGoal is { } seize)
        {
            // Going for the lord's body: while a retainer holds it, a guard within reach is fought where
            // it stands; with nobody holding it, straight on to the body whoever is in reach.
            npc.InReach = npc.SeizeHeld && target is { IsAlive: true } guard && !npc.State.IsCasting &&
                          CrowdGap(npc, guard, npcPos, combatEngine.GetSimulatedEntityPosition(guard)) <= ReachNow(npc);
            if (!npc.InReach && !npc.State.IsCasting)
                goal = seize;
        }
        else if (IsUnawareSentry(npc))
        {
            npc.InReach = false;
            if (SentryGoal(npc, crowd, npcPos, deltaTime) is { } patrol)
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
            var stand = CombatantMath.StandGap(reach);

            npc.InReach = CombatantMath.NextInReach(npc.InReach, gap, reach, stand);

            var bodies = world.HitboxRadiusOf(npc) + EntityHitboxRadius(target);
            if (TacticsGoal(npc, npcPos, targetPos, stand, bodies, deltaTime, out var tacticsSpeed) is { } tactic)
            {
                goal = tactic;
                speed *= tacticsSpeed;
            }
            else if (!npc.InReach)
            {
                goal = CombatantMath.StandPoint(npcPos, targetPos, stand + bodies);
                speed *= ChargeScale(npc, gap);
            }
        }
        else
            npc.InReach = false;

        if (!crowd.Drive(npc.Address, npcPos, world.HitboxRadiusOf(npc), speed, goal, newToBattle: !npc.JoinedCrowd))
            return false;
        npc.JoinedCrowd = true;
        NoteEnemyActivity(EnemyActivityOf(npc, fighting && target is { IsAlive: true }));
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

            // Standing on a corpse: the foot solver asks the body up by what both legs agree on.
            var lift = config.NpcCollisionActive ? MathF.Max(0f, world.FootIkPelvisOffset(npc.Address)) : 0f;
            if (config.NpcCollisionActive)
                world.ReportFootIkRootLift(npc.Address, lift);
            position.Y += lift + config.DefaultNpcHeightOffset;
            world.SetApproachPosition(npc, position.X, position.Y, position.Z);

            if (IsWalking(velocity, out var flat))
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
        if (npc.AiState == CombatantAiState.Dead || npc.AiState == CombatantAiState.Resetting)
            return;

        if (npc.AiState == CombatantAiState.Idle)
        {
            npc.AiState = CombatantAiState.Engaging;
            StaggerTimers(npc, RandomSource);
            ApplyEngageJitter(npc);
            log.Verbose($"NPC '{npc.Name}' engaging (auto-attack in {npc.AutoAttackTimer:F1}s).");
        }
    }

    /// <summary>
    /// Randomize initial auto-attack and skill timers so NPCs don't all attack on the same frame.
    /// Call this whenever an NPC first enters combat.
    /// </summary>
    public static void StaggerTimers(SimulatedNpc npc) => StaggerTimers(npc, Random.Shared);

    /// <summary>The same, drawing from the given generator (the controller passes its own <see cref="RandomSource"/>).</summary>
    public static void StaggerTimers(SimulatedNpc npc, Random random)
    {
        npc.AutoAttackTimer = random.NextSingle() * npc.Behavior.AutoAttackDelay;
        foreach (var skill in npc.Behavior.Skills)
            skill.CooldownRemaining = random.NextSingle() * skill.Cooldown;
    }

    private void TickNpc(SimulatedNpc npc, float deltaTime, Vector3 playerPos, uint playerEntityId)
    {
        // Lock and cooldowns run down; dead: OnDeadTurn; alive: natural MP regen (double the player's rate).
        if (!BeginTurn(npc, deltaTime))
            return;

        DiscardInvalidNpcActionState(npc);

        var npcPos = GetNpcPosition(npc);
        float distToPlayer = Vector3.Distance(npcPos, playerPos);

        switch (npc.AiState)
        {
            case CombatantAiState.Idle:
                // (A sentry walking its patrol or back to its post, or an enemy going for the lord's
                // body, keeps its stride: the approach pass owns its look.)
                if (npc.BattleChara != null && !(npc.IsSentry && npc.HasPatrolGoal) && npc.SeizeGoal == null)
                    world.ApplyCombatIdle(npc);
                break;

            case CombatantAiState.Engaging:
                if (!npc.CrowdDriven)
                    RotateTowardPlayer(npc, playerPos, deltaTime);
                if (npc.EngageDelayTimer > 0)
                {
                    npc.EngageDelayTimer = Math.Max(0, npc.EngageDelayTimer - deltaTime);
                    break;
                }
                npc.AiState = CombatantAiState.Combat;
                break;

            case CombatantAiState.Combat:
                TickCombat(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                break;

            case CombatantAiState.Chasing:
                npc.AiState = CombatantAiState.Combat;
                TickCombat(npc, deltaTime, playerPos, playerEntityId, npcPos, distToPlayer);
                break;

            case CombatantAiState.Dead:
                if (npc.BattleChara != null)
                    world.ApplyDead(npc);
                npc.DeadTimer += deltaTime;
                break;

            case CombatantAiState.Resetting:
                npc.State.CurrentHp = npc.State.MaxHp;
                npc.AiState = CombatantAiState.Idle;
                break;
        }
    }

    /// <summary>
    /// A dead enemy's turn: it drops what it was doing and lies dead. Its death animation and ragdoll
    /// come from the engine's death queue (0.5 s after the blow), not from here.
    /// </summary>
    protected override void OnDeadTurn(SimulatedNpc npc)
    {
        ClearNpcActionState(npc);
        if (npc.AiState != CombatantAiState.Dead)
        {
            npc.AiState = CombatantAiState.Dead;
            npc.DeadTimer = 0;
            if (npc.BattleChara != null)
                world.ApplyDead(npc);
        }
    }

    private void TickCombat(
        SimulatedNpc npc, float deltaTime,
        Vector3 playerPos, uint playerEntityId,
        Vector3 npcPos, float distToPlayer)
    {
        var simulatedTarget = combatEngine.GetNpcTarget(npc);
        if (simulatedTarget == null || !simulatedTarget.IsAlive)
        {
            ClearNpcActionState(npc);
            npc.AiState = CombatantAiState.Idle;
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
                world.ApplyCombatIdle(npc);
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
                    world.ApplyCombatIdle(npc);
                return;
            }

            if (npc.BattleChara != null)
                world.ApplyActionLocked(npc);
            npc.State.CastTimeElapsed += deltaTime;
            if (npc.State.CastTimeElapsed >= npc.State.CastTimeTotal)
            {
                npc.State.IsCasting = false;
                if (npc.CurrentCastSkill != null)
                {
                    var ok = attackSink.ExecuteEnemyAttack(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                        npc.CurrentCastSkill.ActionId, npc.State.CastTargetId, npc.CurrentCastSkill.Potency,
                        npc.CurrentCastSkill.AttackStyle, npc.CurrentCastSkill.Radius, npc.CurrentCastSkill.CastTime));
                    if (ok)
                        npc.CurrentCastSkill.CooldownRemaining = npc.CurrentCastSkill.Cooldown / ActionPace();
                    npc.CurrentCastSkill = null;
                }
            }
            return;
        }

        // Lock, telegraph still running, skills (a random ready one after a token), auto-attack:
        // the step both sides share (Combat/CombatStep.cs).
        Act(npc, simulatedTarget, distToTarget, deltaTime, stepSink);
    }

    private readonly EnemyStepSink stepSink;

    /// <summary>
    /// The enemies' half of the shared step: an attack goes to the attack router (an instant one, or
    /// a telegraph); with the engine's cast bar (only when ActionMode is off, which no mode does any more) a skill with a
    /// cast time starts a cast instead, paid for at once, its area snapshotted.
    /// </summary>
    private sealed class EnemyStepSink(NpcAiController owner) : ICombatStepSink<SimulatedNpc, SimulatedEntityState>
    {
        public void ShowLocked(SimulatedNpc npc)
        {
            if (npc.BattleChara != null)
                owner.world.ApplyActionLocked(npc);
        }

        public void ShowReady(SimulatedNpc npc)
        {
            if (npc.BattleChara != null)
                owner.world.ApplyCombatIdle(npc);
        }

        public AttackCommit CommitSkill(SimulatedNpc npc, SimulatedEntityState simulatedTarget, NpcSkill skill, int mpCost)
        {
            var targetEntityId = simulatedTarget.EntityId;
            var castTime = owner.EffectiveCastTime(skill);

            // Action Mode skips the engine cast bar — the telegraph executor owns the
            // windup uniformly (cast time feeds the telegraph duration).
            if (!owner.config.ActionMode && castTime > 0)
            {
                npc.State.IsCasting = true;
                npc.State.CastActionId = skill.ActionId;
                npc.State.CastTimeTotal = castTime;
                npc.State.CastTimeElapsed = 0;
                npc.State.CastTargetId = targetEntityId;
                npc.State.CastInterruptible = skill.Interruptible;
                npc.CurrentCastSkill = skill;
                npc.State.CurrentMp = Math.Max(0, npc.State.CurrentMp - mpCost);
                owner.BeginSnapshotCast(npc, skill, simulatedTarget, castTime);

                owner.combatEngine.AddLogEntry(
                    $"{npc.Name} begins casting {skill.Name}...",
                    CombatLogType.Info);
                return AttackCommit.CastStarted;
            }

            var ok = owner.attackSink.ExecuteEnemyAttack(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                skill.ActionId, targetEntityId, skill.Potency, skill.AttackStyle, skill.Radius, castTime));
            return ok ? AttackCommit.Landed : AttackCommit.Failed;
        }

        public bool CommitAutoAttack(SimulatedNpc npc, SimulatedEntityState simulatedTarget)
            => owner.attackSink.ExecuteEnemyAttack(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
                npc.Behavior.AutoAttackActionId, simulatedTarget.EntityId, npc.Behavior.AutoAttackPotency,
                npc.Behavior.AutoAttackStyle, 0f, 0f, IsAutoAttack: true));
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
        var shape = attackSink.SnapshotShape(npc, req, target);
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
            world.ApplyActionLocked(npc);
        npc.State.CastTimeElapsed += deltaTime;
        if (npc.State.CastTimeElapsed < npc.State.CastTimeTotal)
            return true;

        var area = npc.CastArea;
        npc.State.IsCasting = false;
        npc.CastArea = null;
        npc.CurrentCastSkill = null;
        // CastTime 0: the wait already happened here; a telegraphed executor
        // must not add the cast again as lead-in.
        var ok = attackSink.ExecuteEnemyAttack(npc, new CombatSimulator.ActionCombat.NpcAttackRequest(
            skill.ActionId, npc.State.CastTargetId, skill.Potency, skill.AttackStyle, skill.Radius, 0f, Area: area));
        if (ok)
            skill.CooldownRemaining = skill.Cooldown / ActionPace();
        npc.State.AnimationLock = MathF.Max(npc.State.AnimationLock, 0.6f);
        return true;
    }

    private static void ClearNpcActionState(SimulatedNpc npc)
    {
        ClearActionState(npc);
        npc.CastArea = null; // its marker sees the cast end and leaves on the next tick
    }

    private void DiscardInvalidNpcActionState(SimulatedNpc npc)
    {
        if (!npc.State.IsCasting && npc.CurrentCastSkill == null)
            return;

        var target = combatEngine.State.GetEntity(npc.State.CastTargetId);
        var selectedTarget = combatEngine.GetNpcTarget(npc);
        var stateAllowsCast = npc.AiState is CombatantAiState.Combat or CombatantAiState.Chasing;
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
        if (npc.BattleChara != null && npc.AiState != CombatantAiState.Dead)
            world.ApplyCombatIdle(npc);
    }

    private void RotateTowardPlayer(SimulatedNpc npc, Vector3 playerPos, float deltaTime)
    {
        if (npc.BattleChara == null || !npc.IsClientControlled)
            return;

        var npcPos = (System.Numerics.Vector3)world.PositionOf(npc);
        var dir = new System.Numerics.Vector3(playerPos.X - npcPos.X, playerPos.Y - npcPos.Y, playerPos.Z - npcPos.Z);
        var targetRot = MathF.Atan2(dir.X, dir.Z);

        var currentRot = world.RotationOf(npc);
        var diff = targetRot - currentRot;

        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotSpeed = 5.0f * deltaTime;
        if (MathF.Abs(diff) < rotSpeed)
            world.SetRotation(npc, targetRot);
        else
            world.SetRotation(npc, currentRot + MathF.Sign(diff) * rotSpeed);
    }

    private Vector3 GetNpcPosition(SimulatedNpc npc)
    {
        // For real NPCs, read position from managed reference (safer)
        if (!npc.IsClientControlled && npc.GameObjectRef != null)
        {
            try { return world.ReferencePositionOf(npc); }
            catch { /* fall through */ }
        }

        if (npc.BattleChara != null)
        {
            return world.PositionOf(npc);
        }
        return npc.SpawnPosition;
    }

    private float EntityHitboxRadius(SimulatedEntityState entity)
    {
        nint address = 0;
        if (entity.IsPlayer)
            address = world.LocalPlayer?.Address ?? 0;
        else
            address = combatEngine.ResolveExternalEntityAddress?.Invoke(entity.EntityId) ?? 0;
        if (address == 0)
            return EnemyMovementMath.BaselineHitboxRadius;
        var r = world.HitboxRadiusAt(address);
        return r > 0.01f ? r : EnemyMovementMath.BaselineHitboxRadius;
    }


    private void StartApproachMoveAnim(SimulatedNpc npc, float deltaTime, ushort timelineId = 0)
    {
        if (npc.BattleChara == null)
            return;
        movingNpcs[npc.Address] = npc.VisualState;
        if (timelineId != 0)
            world.ApplyMoving(npc, deltaTime, timelineId);
        else
            world.ApplyMoving(npc, deltaTime);
    }

    private void StopApproachMoveAnim(SimulatedNpc npc)
    {
        if (!movingNpcs.Remove(npc.Address, out var visual))
            return;
        try { world.ClearMovement(npc.Address, visual); }
        catch { }
    }

    private void StopAllMoveAnims()
    {
        foreach (var (address, visual) in movingNpcs)
        {
            try { world.ClearMovement(address, visual); }
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
        TurnToward(npc, targetPos, deltaTime);
    }

    public override void Dispose()
    {
        combatEngine.OnSimulationStarted -= ScheduleAutoEngage;
        combatEngine.OnSimulationReset -= OnSimulationResetOrStop;
        world.ClearApproachNpcs();
        StopAllMoveAnims();
    }
}
