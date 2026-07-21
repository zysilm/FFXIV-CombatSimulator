using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Simulation;

public enum PartyEngagePlanKind
{
    None,
    ReturnToCommand,
    EngagePoint,
    PlayerPursuit,
}

public sealed class PartyEngagePlan
{
    public uint ActorId { get; init; }
    public uint TargetId { get; init; }
    public PartyEngagePlanKind Kind { get; init; }
    public Vector3 Goal { get; init; }
    public Vector3 FaceTarget { get; init; }
    public bool HasFaceTarget { get; init; }
}

public sealed unsafe class PartyEngagePlanner
{
    private const float RepathDistance = 1.5f;
    private const float RepathInterval = 0.75f;
    private const float PathTolerance = 0.75f;
    private const float ChainJoinRecomputeDistance = 1.25f;
    private const float PlayerPursuitGoalUpdateDistance = 2.0f;
    private const float PlayerPursuitGoalUpdateInterval = 0.65f;
    private const float MeleeSlotSpacing = 1.8f;
    private const float RangedBacklineSpacing = 2.2f;
    private const float AllyDensityRadius = 4.0f;
    private const float EnemyDensityRadius = 3.0f;
    private const float AllyDensityWeight = 22.0f;
    private const float EnemyDensityWeight = 10.0f;
    private const float OverextensionWeight = 18.0f;
    private const float CommandRangeWeight = 35.0f;
    private const float RangedBacklineWeight = 9.0f;
    private const float MeleeFrontWeight = 4.0f;
    private const float IsolationWeight = 8.0f;
    private const float SlotStickyBonus = 55.0f;

    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly Dictionary<uint, PartyEngagePlan> plans = new();
    private readonly Dictionary<string, PlannerPathState> pathStates = new();
    private readonly Dictionary<uint, TacticalSlot> assignedSlots = new();
    private readonly Dictionary<string, uint> slotReservations = new();
    private readonly Dictionary<uint, string> reservedSlotIds = new();

    public PartyEngagePlanner(VNavmeshIpc vnavmeshIpc)
    {
        this.vnavmeshIpc = vnavmeshIpc;
    }

    public IReadOnlyDictionary<uint, PartyEngagePlan> Plans => plans;

    public bool TryGetPlan(uint actorId, out PartyEngagePlan plan)
        => plans.TryGetValue(actorId, out plan!);

    public void Clear()
    {
        plans.Clear();
        pathStates.Clear();
    }

    public void ClearSlotReservation(uint actorId)
    {
        assignedSlots.Remove(actorId);
        reservedSlotIds.Remove(actorId);
    }

    public void Build(
        float deltaTime,
        Vector3 playerPosition,
        float playerRotation,
        Vector3 commandAnchorPosition,
        float commandAnchorRotation,
        IReadOnlyList<CombatCompanion> companions,
        IReadOnlyList<SimulatedNpc> enemies,
        IReadOnlyDictionary<uint, uint> companionTargets,
        IReadOnlyDictionary<uint, uint> enemyTargets,
        uint playerEntityId,
        float commandRange,
        float commandRandomness,
        float partyMeleeAttackRange,
        float partyRangedAttackRange)
    {
        plans.Clear();
        TickPathStateTimers(deltaTime);

        var nodes = BuildNodes(
            commandAnchorPosition,
            companions,
            enemies,
            companionTargets,
            enemyTargets,
            partyMeleeAttackRange,
            partyRangedAttackRange);

        BuildSlotAssignments(nodes, commandRange, commandRandomness);
        var livePathKeys = new HashSet<string>();
        var playerForward = FlatNormalize(new Vector3(MathF.Sin(playerRotation), 0, MathF.Cos(playerRotation)), Vector3.UnitZ);

        foreach (var node in nodes.Values)
        {
            if (node.TargetId == 0 || node.TargetId == playerEntityId || !nodes.ContainsKey(node.TargetId))
                continue;

            var cycle = FindCycle(node.Id, nodes);
            if (cycle.Count < 2 || !cycle.Contains(node.Id))
                continue;

            if (cycle.Count == 2)
                ApplyMutualPair(cycle, nodes, commandRange, commandRandomness, livePathKeys);
            else
                ApplyCycle(cycle, nodes, commandRange, commandRandomness, livePathKeys);
        }

        foreach (var node in nodes.Values)
        {
            if (plans.ContainsKey(node.Id) || node.TargetId == 0)
                continue;

            if (node.TargetId == playerEntityId)
            {
                plans[node.Id] = BuildPlayerPursuitPlan(node, playerPosition, playerForward, commandRange, commandRandomness, livePathKeys);
                continue;
            }

            if (plans.ContainsKey(node.TargetId) && nodes.TryGetValue(node.TargetId, out var plannedTargetNode))
            {
                plans[node.Id] = BuildChainPlan(node, plannedTargetNode, commandRange, commandRandomness, livePathKeys);
                continue;
            }

            if (nodes.TryGetValue(node.TargetId, out var targetNode))
            {
                plans[node.Id] = BuildPathEngagePlan(
                    $"open:{node.Id}:{targetNode.Id}",
                    node,
                    targetNode,
                    commandRange,
                    commandRandomness,
                    livePathKeys,
                    PartyEngagePlanKind.EngagePoint);
            }
        }

        foreach (var key in pathStates.Keys.ToList())
        {
            if (!livePathKeys.Contains(key))
                pathStates.Remove(key);
        }
    }

    public PartyEngagePlan BuildReturnPlan(
        uint actorId,
        Vector3 actorPosition,
        Vector3 commandAnchorPosition,
        float commandAnchorRotation,
        int companionIndex,
        int companionCount,
        float commandRange,
        float commandRandomness)
    {
        var radius = GetCommandReturnDistance(actorId, commandRange, commandRandomness);
        var angle = commandAnchorRotation + MathF.PI + SpreadAngle(companionIndex, companionCount);
        var goal = commandAnchorPosition + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * radius;
        goal.Y = commandAnchorPosition.Y;

        return new PartyEngagePlan
        {
            ActorId = actorId,
            Kind = PartyEngagePlanKind.ReturnToCommand,
            Goal = goal,
            FaceTarget = actorPosition,
            HasFaceTarget = false,
        };
    }

    public float GetCommandLimit(uint actorId, float commandRange, float commandRandomness)
    {
        var jitter = StableUnit(actorId, 0xC001u);
        var factor = 1.0f - Math.Clamp(commandRandomness, 0, 0.8f) * jitter;
        return MathF.Max(1.0f, commandRange * factor);
    }

    public (bool Reachable, float Overflow) EvaluateCompanionReachability(
        uint actorId,
        Vector3 actorPosition,
        Vector3 targetPosition,
        IReadOnlyList<CombatCompanion> companions,
        IReadOnlyList<SimulatedNpc> enemies,
        IReadOnlyDictionary<uint, uint> companionTargets,
        IReadOnlyDictionary<uint, uint> enemyTargets,
        Vector3 commandAnchorPosition,
        NpcAttackStyle attackStyle,
        float commandRange,
        float commandRandomness,
        float partyMeleeAttackRange,
        float partyRangedAttackRange,
        float recoveryDistance)
    {
        var nodes = BuildNodes(
            commandAnchorPosition,
            companions,
            enemies,
            companionTargets,
            enemyTargets,
            partyMeleeAttackRange,
            partyRangedAttackRange);
        var node = new PartyNode(
            actorId,
            PartyNodeSide.Friendly,
            actorPosition,
            commandAnchorPosition,
            GetPreferredEngageRange(attackStyle, partyMeleeAttackRange, partyRangedAttackRange),
            0,
            IsRangedStyle(attackStyle));
        nodes[actorId] = node;
        var target = new PartyNode(
            SyntheticTargetId(actorId, nodes),
            PartyNodeSide.Enemy,
            targetPosition,
            targetPosition,
            GetPreferredEngageRange(NpcAttackStyle.Melee, partyMeleeAttackRange, partyRangedAttackRange),
            0,
            false);
        foreach (var existing in nodes.Values)
        {
            if (existing.Side == PartyNodeSide.Enemy && FlatDistance(existing.Position, targetPosition) < 0.001f)
            {
                target = existing;
                break;
            }
        }

        if (!nodes.ContainsKey(target.Id))
            nodes[target.Id] = target;

        var limit = GetCommandLimit(actorId, commandRange, commandRandomness);
        var bestOverflow = float.PositiveInfinity;

        foreach (var slot in TacticalSlotCandidates(node, target, nodes, commandRange, commandRandomness))
        {
            var overflow = FlatDistance(slot.Position, commandAnchorPosition) - limit;
            bestOverflow = MathF.Min(bestOverflow, overflow);
            if (overflow <= recoveryDistance)
                return (true, MathF.Max(0, overflow));
        }

        return (false, MathF.Max(0, bestOverflow));
    }

    private Dictionary<uint, PartyNode> BuildNodes(
        Vector3 commandAnchorPosition,
        IReadOnlyList<CombatCompanion> companions,
        IReadOnlyList<SimulatedNpc> enemies,
        IReadOnlyDictionary<uint, uint> companionTargets,
        IReadOnlyDictionary<uint, uint> enemyTargets,
        float partyMeleeAttackRange,
        float partyRangedAttackRange)
    {
        var nodes = new Dictionary<uint, PartyNode>();

        foreach (var companion in companions)
        {
            if (!companion.IsSpawned || !companion.State.IsAlive || companion.BattleChara == null)
                continue;

            var position = (Vector3)((GameObject*)companion.BattleChara)->Position;
            nodes[companion.SimulatedEntityId] = new PartyNode(
                companion.SimulatedEntityId,
                PartyNodeSide.Friendly,
                position,
                commandAnchorPosition,
                GetPreferredEngageRange(companion.Behavior.AutoAttackStyle, partyMeleeAttackRange, partyRangedAttackRange),
                companionTargets.GetValueOrDefault(companion.SimulatedEntityId),
                IsRangedStyle(companion.Behavior.AutoAttackStyle));
        }

        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;

            var position = (Vector3)((GameObject*)enemy.BattleChara)->Position;
            // Action-Mode dynamic positioning: place the enemy by the action it intends to use next
            // (caster casting → backline; meleeing → front), falling back to its fixed weapon style
            // until NpcAiController has computed an intent this combat.
            var enemyRange = enemy.DesiredEngageRange > 0f
                ? enemy.DesiredEngageRange
                : GetPreferredEngageRange(enemy.Behavior.AutoAttackStyle, partyMeleeAttackRange, partyRangedAttackRange);
            var enemyRanged = enemy.DesiredEngageRange > 0f
                ? enemy.IsRangedIntent
                : IsRangedStyle(enemy.Behavior.AutoAttackStyle);
            nodes[enemy.SimulatedEntityId] = new PartyNode(
                enemy.SimulatedEntityId,
                PartyNodeSide.Enemy,
                position,
                enemy.SpawnPosition,
                enemyRange,
                enemyTargets.GetValueOrDefault(enemy.SimulatedEntityId),
                enemyRanged);
        }

        return nodes;
    }

    private void ApplyMutualPair(
        IReadOnlyList<uint> cycle,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys)
    {
        var a = nodes[cycle[0]];
        var b = nodes[cycle[1]];
        var key = $"pair:{Math.Min(a.Id, b.Id)}:{Math.Max(a.Id, b.Id)}";
        var path = GetSharedPath(key, a.Position, b.Position, livePathKeys);
        var pathLength = PathLength(path);
        var engageDistance = MathF.Min(pathLength * 0.5f, GetEngageDistance(a.Id ^ b.Id, commandRange, commandRandomness));
        var sharedPoint = PointAlongPath(path, engageDistance);

        plans[a.Id] = BuildExplicitGoalPlan(a, b.Id, b.Position, sharedPoint, commandRange, commandRandomness, PartyEngagePlanKind.EngagePoint);
        plans[b.Id] = BuildExplicitGoalPlan(b, a.Id, a.Position, sharedPoint, commandRange, commandRandomness, PartyEngagePlanKind.EngagePoint);
    }

    private void ApplyCycle(
        IReadOnlyList<uint> cycle,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys)
    {
        var centroid = Vector3.Zero;
        foreach (var id in cycle)
            centroid += nodes[id].Position;
        centroid /= cycle.Count;
        centroid = SnapToNavmesh(centroid) ?? centroid;

        foreach (var id in cycle)
        {
            var node = nodes[id];
            plans[id] = BuildPathEngagePlan(
                $"cycle:{StableCycleKey(cycle)}:{id}",
                node,
                new PartyNode(
                    node.TargetId,
                    node.Side == PartyNodeSide.Friendly ? PartyNodeSide.Enemy : PartyNodeSide.Friendly,
                    centroid,
                    node.CommandAnchor,
                    node.PreferredEngageRange,
                    0,
                    false),
                commandRange,
                commandRandomness,
                livePathKeys,
                PartyEngagePlanKind.EngagePoint);
        }
    }

    private PartyEngagePlan BuildChainPlan(
        PartyNode node,
        PartyNode targetNode,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys)
    {
        var key = $"chain:{node.Id}:{targetNode.Id}";
        return BuildPathEngagePlan(
            key,
            node,
            targetNode,
            commandRange,
            commandRandomness,
            livePathKeys,
            PartyEngagePlanKind.EngagePoint);
    }

    private PartyEngagePlan BuildPlayerPursuitPlan(
        PartyNode actor,
        Vector3 playerPosition,
        Vector3 playerForward,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys)
    {
        var key = $"player:{actor.Id}";
        livePathKeys.Add(key);
        // Every other plan builder keeps a stand-off from its target (BuildPathPointPlan's
        // holdDistance); the pursuit goal alone was the player's raw position, so it had no notion
        // of personal space and walked actors onto the player. Melee never notices — it stops on
        // TickPartyApproach's hold band long before reaching the goal — but a ranged actor, which
        // deliberately skips that hold band when it is too close so it can back off, was "backing
        // off" straight into the player. Apply the same stand-off the other builders use, so the
        // goal a ranged actor backs off to is a real spot instead of the player's feet.
        var delayedGoal = GetDelayedPlayerGoal(key, playerPosition);
        delayedGoal = PushOutToStandoff(delayedGoal, playerPosition, actor.Position, StandoffFor(actor));
        var clamped = ClampToCommandAnchor(actor, delayedGoal, commandRange, commandRandomness, out var leashed);
        if (leashed)
        {
            return new PartyEngagePlan
            {
                ActorId = actor.Id,
                TargetId = 0,
                Kind = PartyEngagePlanKind.ReturnToCommand,
                Goal = clamped,
                FaceTarget = playerPosition,
                HasFaceTarget = false,
            };
        }

        var goal = AddStableJitter(actor.Id, clamped, commandRange * 0.035f);
        return new PartyEngagePlan
        {
            ActorId = actor.Id,
            TargetId = actor.TargetId,
            Kind = PartyEngagePlanKind.PlayerPursuit,
            Goal = goal,
            FaceTarget = playerPosition,
            HasFaceTarget = true,
        };
    }

    private PartyEngagePlan BuildPathEngagePlan(
        string key,
        PartyNode actor,
        PartyNode target,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys,
        PartyEngagePlanKind kind)
    {
        var goal = assignedSlots.TryGetValue(actor.Id, out var slot)
            ? slot.Position
            : FallbackTacticalSlot(actor, target, commandRange, commandRandomness).Position;
        var path = GetSharedPath(key, actor.Position, goal, livePathKeys);
        return BuildPathPointPlan(actor, target.Id, target.Position, path, true, commandRange, commandRandomness, kind);
    }

    private PartyEngagePlan BuildPathPointPlan(
        PartyNode actor,
        uint targetId,
        Vector3 faceTarget,
        IReadOnlyList<Vector3> path,
        bool fromStart,
        float commandRange,
        float commandRandomness,
        PartyEngagePlanKind kind)
    {
        var pathLength = PathLength(path);
        var holdDistance = MathF.Max(0.25f, actor.PreferredEngageRange * 0.5f);
        var distance = fromStart
            ? MathF.Max(0, pathLength - holdDistance)
            : MathF.Min(pathLength, holdDistance);
        var goal = PointAlongPath(path, fromStart ? distance : pathLength - distance);
        goal = AddStableJitter(actor.Id, goal, MathF.Min(commandRange * 0.04f, actor.PreferredEngageRange * 0.15f));
        goal = ClampToCommandAnchor(actor, goal, commandRange, commandRandomness, out var leashed);

        return new PartyEngagePlan
        {
            ActorId = actor.Id,
            TargetId = leashed ? 0 : targetId,
            Kind = leashed ? PartyEngagePlanKind.ReturnToCommand : kind,
            Goal = goal,
            FaceTarget = faceTarget,
            HasFaceTarget = !leashed,
        };
    }

    private PartyEngagePlan BuildExplicitGoalPlan(
        PartyNode actor,
        uint targetId,
        Vector3 faceTarget,
        Vector3 sharedGoal,
        float commandRange,
        float commandRandomness,
        PartyEngagePlanKind kind)
    {
        var goal = AddStableJitter(actor.Id, sharedGoal, commandRange * 0.035f);
        goal = ClampToCommandAnchor(actor, goal, commandRange, commandRandomness, out var leashed);

        return new PartyEngagePlan
        {
            ActorId = actor.Id,
            TargetId = leashed ? 0 : targetId,
            Kind = leashed ? PartyEngagePlanKind.ReturnToCommand : kind,
            Goal = goal,
            FaceTarget = faceTarget,
            HasFaceTarget = !leashed,
        };
    }

    private void BuildSlotAssignments(
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness)
    {
        assignedSlots.Clear();
        slotReservations.Clear();
        var liveActorIds = nodes.Keys.ToHashSet();
        foreach (var actorId in reservedSlotIds.Keys.ToList())
        {
            if (!liveActorIds.Contains(actorId))
                reservedSlotIds.Remove(actorId);
        }

        var actorIds = new List<uint>();
        var actorIndex = new Dictionary<uint, int>();
        var slotIndex = new Dictionary<string, int>();
        var slots = new List<TacticalSlot>();
        var actorSlotEdges = new List<SlotEdge>();

        foreach (var node in nodes.Values)
        {
            if (node.TargetId == 0 || !nodes.TryGetValue(node.TargetId, out var targetNode))
                continue;

            if (!actorIndex.ContainsKey(node.Id))
            {
                actorIndex[node.Id] = actorIds.Count;
                actorIds.Add(node.Id);
            }

            var candidates = TacticalSlotCandidates(node, targetNode, nodes, commandRange, commandRandomness).ToList();
            foreach (var candidate in candidates)
            {
                if (!slotIndex.ContainsKey(candidate.SlotId))
                {
                    slotIndex[candidate.SlotId] = slots.Count;
                    slots.Add(candidate);
                }

                actorSlotEdges.Add(new SlotEdge(
                    node.Id,
                    candidate.SlotId,
                    candidate,
                    ScoreSlotCandidate(node, targetNode, candidate, candidates.Count, nodes, commandRange, commandRandomness)));
            }
        }

        if (actorIds.Count == 0 || slots.Count == 0)
            return;

        var source = 0;
        var actorBase = 1;
        var slotBase = actorBase + actorIds.Count;
        var sink = slotBase + slots.Count;
        var edges = new List<FlowInputEdge>();

        for (var i = 0; i < actorIds.Count; i++)
            edges.Add(new FlowInputEdge(source, actorBase + i, 1, 0, null));
        for (var i = 0; i < slots.Count; i++)
            edges.Add(new FlowInputEdge(slotBase + i, sink, 1, 0, null));

        foreach (var edge in actorSlotEdges)
        {
            edges.Add(new FlowInputEdge(
                actorBase + actorIndex[edge.ActorId],
                slotBase + slotIndex[edge.SlotId],
                1,
                edge.Cost,
                edge));
        }

        foreach (var assignment in MinCostMaxFlow(sink + 1, source, sink, edges))
        {
            if (assignedSlots.ContainsKey(assignment.ActorId) || slotReservations.ContainsKey(assignment.Slot.SlotId))
                continue;

            assignedSlots[assignment.ActorId] = assignment.Slot;
            slotReservations[assignment.Slot.SlotId] = assignment.ActorId;
            reservedSlotIds[assignment.ActorId] = assignment.Slot.SlotId;
        }
    }

    private IEnumerable<TacticalSlot> TacticalSlotCandidates(
        PartyNode actor,
        PartyNode target,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness)
    {
        var axis = BattleAxis(nodes);
        var actorForward = actor.Side == PartyNodeSide.Friendly ? axis : -axis;
        var right = new Vector3(actorForward.Z, 0, -actorForward.X);
        var range = MathF.Max(0.5f, actor.PreferredEngageRange);

        if (actor.IsRanged)
        {
            var back = -actorForward;
            var rowDistance = MathF.Max(range * 0.8f, 5.0f);
            for (var i = 0; i < 5; i++)
            {
                var slot = i - 2;
                yield return new TacticalSlot(
                    $"{actor.Side}:{target.Id}:ranged:{i}",
                    i,
                    target.Position + back * rowDistance + right * slot * RangedBacklineSpacing);
            }

            yield break;
        }

        var angles = new[] { 0f, -32f, 32f, -64f, 64f, -105f, 105f, 180f };
        var baseAngle = MathF.Atan2(-actorForward.X, -actorForward.Z);
        for (var i = 0; i < angles.Length; i++)
        {
            var angle = baseAngle + angles[i] * MathF.PI / 180f;
            yield return new TacticalSlot(
                $"{actor.Side}:{target.Id}:melee:{i}",
                i,
                target.Position + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * range);
        }
    }

    private TacticalSlot FallbackTacticalSlot(
        PartyNode actor,
        PartyNode target,
        float commandRange,
        float commandRandomness)
    {
        var nodes = new Dictionary<uint, PartyNode>
        {
            [actor.Id] = actor,
            [target.Id] = target,
        };
        var candidates = TacticalSlotCandidates(actor, target, nodes, commandRange, commandRandomness).ToList();
        if (candidates.Count == 0)
            return new TacticalSlot($"{actor.Side}:{target.Id}:fallback:0", 0, target.Position);

        var best = candidates[0];
        var bestScore = float.MaxValue;
        foreach (var candidate in candidates)
        {
            var score = ScoreSlotCandidate(actor, target, candidate, candidates.Count, nodes, commandRange, commandRandomness);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private float ScoreSlotCandidate(
        PartyNode actor,
        PartyNode target,
        TacticalSlot candidate,
        int candidateCount,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness)
    {
        var preferredSlot = (int)MathF.Floor(StableUnit(actor.Id, target.Id ^ 0x5107u) * candidateCount);
        var stickyBonus = reservedSlotIds.TryGetValue(actor.Id, out var currentSlotId) && currentSlotId == candidate.SlotId
            ? SlotStickyBonus
            : 0f;
        return FlatDistance(actor.Position, candidate.Position)
               + MathF.Abs(candidate.SlotIndex - preferredSlot) * 0.35f
               + SlotCrowdingPenalty(actor.Id, candidate.Position, nodes)
               + InfluenceCost(actor, target, candidate.Position, nodes, commandRange, commandRandomness)
               - stickyBonus;
    }

    private float SlotCrowdingPenalty(uint actorId, Vector3 candidate, IReadOnlyDictionary<uint, PartyNode> nodes)
    {
        var penalty = 0f;
        foreach (var node in nodes.Values)
        {
            if (node.Id == actorId)
                continue;

            var distance = FlatDistance(node.Position, candidate);
            if (distance < MeleeSlotSpacing)
                penalty += (MeleeSlotSpacing - distance) * 35f;
        }

        return penalty;
    }

    private float InfluenceCost(
        PartyNode actor,
        PartyNode target,
        Vector3 candidate,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        float commandRange,
        float commandRandomness)
    {
        var axis = BattleAxis(nodes);
        var sideForward = actor.Side == PartyNodeSide.Friendly ? axis : -axis;
        var allyDensity = DensityAround(candidate, nodes.Values.Where(n => n.Id != actor.Id && n.Side == actor.Side), AllyDensityRadius);
        var enemyDensity = DensityAround(candidate, nodes.Values.Where(n => n.Side != actor.Side), EnemyDensityRadius);

        var cost = allyDensity * AllyDensityWeight + enemyDensity * EnemyDensityWeight;

        var actorProgress = SignedProgress(actor.Position, target.Position, sideForward);
        var slotProgress = SignedProgress(candidate, target.Position, sideForward);
        var overextend = MathF.Max(0, slotProgress - MathF.Max(actorProgress + 2.5f, actor.PreferredEngageRange * 0.4f));
        cost += overextend * OverextensionWeight;

        if (actor.Side == PartyNodeSide.Friendly)
        {
            var limit = GetCommandLimit(actor.Id, commandRange, commandRandomness);
            var distFromCommand = FlatDistance(candidate, actor.CommandAnchor);
            cost += MathF.Max(0, distFromCommand - limit * 0.85f) * CommandRangeWeight;
        }

        var targetToSlot = FlatNormalize(candidate - target.Position, -sideForward);
        var dot = Vector3.Dot(targetToSlot, sideForward);
        var behindTarget = MathF.Max(0, -dot);
        var frontTarget = MathF.Max(0, dot);
        cost += actor.IsRanged
            ? -behindTarget * RangedBacklineWeight
            : -frontTarget * MeleeFrontWeight;

        var friendlyCenter = Average(nodes.Values.Where(n => n.Side == actor.Side).Select(n => n.Position).ToList(), actor.Position);
        cost += MathF.Max(0, FlatDistance(candidate, friendlyCenter) - commandRange * 0.8f) * IsolationWeight;
        return cost;
    }

    private static float DensityAround(Vector3 point, IEnumerable<PartyNode> nodes, float radius)
    {
        var density = 0f;
        foreach (var node in nodes)
        {
            var distance = FlatDistance(point, node.Position);
            if (distance >= radius)
                continue;

            var t = 1f - distance / radius;
            density += t * t;
        }

        return density;
    }

    private static float SignedProgress(Vector3 point, Vector3 origin, Vector3 axis)
        => (point.X - origin.X) * axis.X + (point.Z - origin.Z) * axis.Z;

    private static Vector3 BattleAxis(IReadOnlyDictionary<uint, PartyNode> nodes)
    {
        var friendlies = nodes.Values.Where(n => n.Side == PartyNodeSide.Friendly).Select(n => n.Position).ToList();
        var enemies = nodes.Values.Where(n => n.Side == PartyNodeSide.Enemy).Select(n => n.Position).ToList();
        var friendlyCenter = Average(friendlies, Vector3.Zero);
        var enemyCenter = Average(enemies, friendlyCenter + Vector3.UnitZ);
        return FlatNormalize(enemyCenter - friendlyCenter, Vector3.UnitZ);
    }

    private static List<SlotEdge> MinCostMaxFlow(
        int nodeCount,
        int source,
        int sink,
        IReadOnlyList<FlowInputEdge> edges)
    {
        var graph = Enumerable.Range(0, nodeCount).Select(_ => new List<FlowEdge>()).ToList();

        void AddEdge(FlowInputEdge input)
        {
            var forward = new FlowEdge(input.To, graph[input.To].Count, input.Capacity, input.Cost, input.Meta);
            var reverse = new FlowEdge(input.From, graph[input.From].Count, 0, -input.Cost, null);
            graph[input.From].Add(forward);
            graph[input.To].Add(reverse);
        }

        foreach (var edge in edges)
            AddEdge(edge);

        var flowEdges = new List<SlotEdge>();
        while (true)
        {
            var dist = Enumerable.Repeat(float.PositiveInfinity, nodeCount).ToArray();
            var inQueue = new bool[nodeCount];
            var prevNode = Enumerable.Repeat(-1, nodeCount).ToArray();
            var prevEdge = Enumerable.Repeat(-1, nodeCount).ToArray();
            var queue = new Queue<int>();

            dist[source] = 0;
            inQueue[source] = true;
            queue.Enqueue(source);

            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                inQueue[v] = false;
                for (var i = 0; i < graph[v].Count; i++)
                {
                    var edge = graph[v][i];
                    if (edge.Capacity <= 0)
                        continue;

                    var nextDist = dist[v] + edge.Cost;
                    if (nextDist >= dist[edge.To])
                        continue;

                    dist[edge.To] = nextDist;
                    prevNode[edge.To] = v;
                    prevEdge[edge.To] = i;
                    if (!inQueue[edge.To])
                    {
                        inQueue[edge.To] = true;
                        queue.Enqueue(edge.To);
                    }
                }
            }

            if (float.IsPositiveInfinity(dist[sink]))
                break;

            var aug = int.MaxValue;
            for (var v = sink; v != source; v = prevNode[v])
            {
                if (v < 0)
                {
                    aug = 0;
                    break;
                }

                aug = Math.Min(aug, graph[prevNode[v]][prevEdge[v]].Capacity);
            }

            if (aug <= 0)
                break;

            for (var v = sink; v != source; v = prevNode[v])
            {
                var edge = graph[prevNode[v]][prevEdge[v]];
                edge.Capacity -= aug;
                graph[edge.To][edge.Reverse].Capacity += aug;
                if (edge.Meta.HasValue)
                    flowEdges.Add(edge.Meta.Value);
            }
        }

        return flowEdges;
    }

    private IReadOnlyList<Vector3> GetSharedPath(string key, Vector3 from, Vector3 to, HashSet<string> livePathKeys)
    {
        livePathKeys.Add(key);
        if (!pathStates.TryGetValue(key, out var state))
        {
            state = new PlannerPathState();
            pathStates[key] = state;
        }

        if (state.PendingPath is { IsCompleted: true })
        {
            try
            {
                var path = state.PendingPath.GetAwaiter().GetResult();
                if (path.Count >= 2)
                    state.Waypoints = path;
                state.RequestedFrom = state.PendingFrom;
                state.RequestedTo = state.PendingTo;
            }
            catch
            {
                state.Waypoints.Clear();
            }
            state.PendingPath = null;
        }

        var shouldRepath = state.PendingPath == null &&
                           state.RepathTimer <= 0 &&
                           (state.Waypoints.Count < 2 ||
                            FlatDistance(state.RequestedFrom, from) >= RepathDistance ||
                            FlatDistance(state.RequestedTo, to) >= RepathDistance);

        if (shouldRepath && vnavmeshIpc.CanPathfind)
        {
            try
            {
                var snapFrom = SnapToNavmesh(from) ?? from;
                var snapTo = SnapToNavmesh(to) ?? to;
                state.PendingFrom = snapFrom;
                state.PendingTo = snapTo;
                state.PendingPath = vnavmeshIpc.Pathfind(snapFrom, snapTo, PathTolerance);
                state.RepathTimer = RepathInterval;
            }
            catch
            {
                state.PendingPath = null;
            }
        }

        return state.Waypoints.Count >= 2
            ? state.Waypoints
            : new[] { from, to };
    }

    private Vector3 GetStableChainGoal(string key, Vector3 targetGoal)
    {
        if (!pathStates.TryGetValue(key, out var state))
        {
            state = new PlannerPathState { StableChainGoal = targetGoal, HasStableChainGoal = true };
            pathStates[key] = state;
            return targetGoal;
        }

        if (!state.HasStableChainGoal || FlatDistance(state.StableChainGoal, targetGoal) >= ChainJoinRecomputeDistance)
        {
            state.StableChainGoal = targetGoal;
            state.HasStableChainGoal = true;
        }

        return state.StableChainGoal;
    }

    private Vector3 GetDelayedPlayerGoal(string key, Vector3 playerPosition)
    {
        if (!pathStates.TryGetValue(key, out var state))
        {
            state = new PlannerPathState
            {
                DelayedPlayerGoal = playerPosition,
                HasDelayedPlayerGoal = true,
                PlayerGoalUpdateTimer = PlayerPursuitGoalUpdateInterval,
            };
            pathStates[key] = state;
            return playerPosition;
        }

        if (!state.HasDelayedPlayerGoal)
        {
            state.DelayedPlayerGoal = playerPosition;
            state.HasDelayedPlayerGoal = true;
            state.PlayerGoalUpdateTimer = PlayerPursuitGoalUpdateInterval;
            return playerPosition;
        }

        if (state.PlayerGoalUpdateTimer <= 0 &&
            FlatDistance(state.DelayedPlayerGoal, playerPosition) >= PlayerPursuitGoalUpdateDistance)
        {
            state.DelayedPlayerGoal = playerPosition;
            state.PlayerGoalUpdateTimer = PlayerPursuitGoalUpdateInterval;
        }

        return state.DelayedPlayerGoal;
    }

    private Vector3 ClampToCommandAnchor(
        PartyNode actor,
        Vector3 goal,
        float commandRange,
        float commandRandomness,
        out bool leashed)
    {
        if (actor.Side != PartyNodeSide.Friendly)
        {
            leashed = false;
            return goal;
        }

        var limit = GetCommandLimit(actor.Id, commandRange, commandRandomness);
        var offset = goal - actor.CommandAnchor;
        offset.Y = 0;
        var distance = offset.Length();
        leashed = distance > limit;
        if (!leashed)
            return goal;

        var dir = distance > 0.001f ? offset / distance : Vector3.UnitZ;
        var clamped = actor.CommandAnchor + dir * MathF.Max(0.5f, limit * 0.85f);
        clamped.Y = actor.CommandAnchor.Y;
        return clamped;
    }

    private Vector3? SnapToNavmesh(Vector3 point)
    {
        if (!vnavmeshIpc.CanPathfind)
            return null;

        try
        {
            return vnavmeshIpc.NearestPointReachable(point) ?? vnavmeshIpc.PointOnFloor(point);
        }
        catch
        {
            return null;
        }
    }

    private void TickPathStateTimers(float deltaTime)
    {
        foreach (var state in pathStates.Values)
        {
            state.RepathTimer = Math.Max(0, state.RepathTimer - deltaTime);
            state.PlayerGoalUpdateTimer = Math.Max(0, state.PlayerGoalUpdateTimer - deltaTime);
        }
    }

    private static List<uint> FindCycle(uint startId, IReadOnlyDictionary<uint, PartyNode> nodes)
    {
        var visitedAt = new Dictionary<uint, int>();
        var order = new List<uint>();
        var currentId = startId;

        while (nodes.TryGetValue(currentId, out var current) &&
               current.TargetId != 0 &&
               nodes.ContainsKey(current.TargetId))
        {
            if (visitedAt.TryGetValue(currentId, out var index))
                return order.Skip(index).ToList();

            visitedAt[currentId] = order.Count;
            order.Add(currentId);
            currentId = current.TargetId;
        }

        return new List<uint>();
    }

    private float GetEngageDistance(uint actorId, float commandRange, float commandRandomness)
    {
        var jitter = (StableUnit(actorId, 0xA11CEu) - 0.5f) * Math.Clamp(commandRandomness, 0, 0.8f);
        return MathF.Max(0.5f, commandRange * Math.Clamp(0.75f + jitter, 0.35f, 0.95f));
    }

    private float GetCommandReturnDistance(uint actorId, float commandRange, float commandRandomness)
    {
        var jitter = StableUnit(actorId, 0xBEEFu);
        var factor = 0.35f + 0.35f * jitter * (1.0f + Math.Clamp(commandRandomness, 0, 0.8f));
        return MathF.Max(1.5f, commandRange * Math.Clamp(factor, 0.25f, 0.85f));
    }

    /// <summary>
    /// The stand-off a plan goal keeps from its target — the same rule BuildPathPointPlan uses for
    /// its holdDistance, so pursuit goals line up with every other plan kind.
    /// </summary>
    private static float StandoffFor(PartyNode actor)
        => MathF.Max(0.25f, actor.PreferredEngageRange * 0.5f);

    /// <summary>
    /// Raise a goal to at least <paramref name="standoff"/> from the target, keeping its direction.
    /// A MINIMUM only — a goal already further out is returned untouched, so this never turns into
    /// "walk out to X". Falls back to the actor's own bearing when the goal sits on the target.
    /// </summary>
    private static Vector3 PushOutToStandoff(Vector3 goal, Vector3 targetPos, Vector3 actorPos, float standoff)
    {
        var away = goal - targetPos;
        away.Y = 0;
        var dist = away.Length();
        if (dist >= standoff)
            return goal;

        if (dist < 0.01f)
        {
            away = actorPos - targetPos;
            away.Y = 0;
            dist = away.Length();
            if (dist < 0.01f)
                return goal; // actor and target fully coincident — nothing meaningful to push along
        }

        var dir = away / dist;
        return new Vector3(targetPos.X + dir.X * standoff, goal.Y, targetPos.Z + dir.Z * standoff);
    }

    private static float GetPreferredEngageRange(NpcAttackStyle style, float meleeRange, float rangedRange)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic
            ? MathF.Max(1.0f, rangedRange)
            : MathF.Max(0.5f, meleeRange);

    private static bool IsRangedStyle(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic;

    private static float PathLength(IReadOnlyList<Vector3> path)
    {
        var length = 0f;
        for (var i = 1; i < path.Count; i++)
            length += FlatDistance(path[i - 1], path[i]);
        return length;
    }

    private static Vector3 PointAlongPath(IReadOnlyList<Vector3> path, float distance)
    {
        if (path.Count == 0)
            return Vector3.Zero;
        if (path.Count == 1 || distance <= 0)
            return path[0];

        var remaining = distance;
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var segment = FlatDistance(a, b);
            if (segment <= 0.001f)
                continue;

            if (remaining <= segment)
            {
                var t = remaining / segment;
                return Vector3.Lerp(a, b, t);
            }

            remaining -= segment;
        }

        return path[^1];
    }

    private static float SpreadAngle(int index, int count)
    {
        if (count <= 1)
            return 0;
        var centered = index - (count - 1) * 0.5f;
        return centered * 0.45f;
    }

    private static Vector3 AddStableJitter(uint actorId, Vector3 point, float radius)
    {
        if (radius <= 0)
            return point;

        var angle = StableUnit(actorId, 0x5151u) * MathF.Tau;
        var distance = StableUnit(actorId, 0x7171u) * radius;
        point.X += MathF.Sin(angle) * distance;
        point.Z += MathF.Cos(angle) * distance;
        return point;
    }

    private static uint StableCycleKey(IReadOnlyList<uint> cycle)
    {
        var key = 2166136261u;
        foreach (var id in cycle.OrderBy(id => id))
        {
            key ^= id;
            key *= 16777619u;
        }
        return key;
    }

    private static float StableUnit(uint actorId, uint salt)
    {
        var x = actorId ^ salt;
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777215.0f;
    }

    private static uint SyntheticTargetId(uint actorId, IReadOnlyDictionary<uint, PartyNode> nodes)
    {
        var id = actorId ^ 0x80000000u;
        while (id == 0 || nodes.ContainsKey(id))
            id++;
        return id;
    }

    private static Vector3 FlatNormalize(Vector3 value, Vector3 fallback)
    {
        value.Y = 0;
        if (value.LengthSquared() < 0.0001f)
            return fallback;
        return Vector3.Normalize(value);
    }

    private static Vector3 Average(IReadOnlyList<Vector3> positions, Vector3 fallback)
    {
        if (positions.Count == 0)
            return fallback;

        var sum = Vector3.Zero;
        foreach (var position in positions)
            sum += position;
        return sum / positions.Count;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    private sealed class PlannerPathState
    {
        public List<Vector3> Waypoints { get; set; } = new();
        public Vector3 RequestedFrom { get; set; }
        public Vector3 RequestedTo { get; set; }
        public Vector3 PendingFrom { get; set; }
        public Vector3 PendingTo { get; set; }
        public Task<List<Vector3>>? PendingPath { get; set; }
        public float RepathTimer { get; set; }
        public Vector3 StableChainGoal { get; set; }
        public bool HasStableChainGoal { get; set; }
        public Vector3 DelayedPlayerGoal { get; set; }
        public bool HasDelayedPlayerGoal { get; set; }
        public float PlayerGoalUpdateTimer { get; set; }
    }

    private readonly record struct TacticalSlot(string SlotId, int SlotIndex, Vector3 Position);

    private readonly record struct SlotEdge(uint ActorId, string SlotId, TacticalSlot Slot, float Cost);

    private readonly record struct FlowInputEdge(int From, int To, int Capacity, float Cost, SlotEdge? Meta);

    private sealed class FlowEdge
    {
        public FlowEdge(int to, int reverse, int capacity, float cost, SlotEdge? meta)
        {
            To = to;
            Reverse = reverse;
            Capacity = capacity;
            Cost = cost;
            Meta = meta;
        }

        public int To { get; }
        public int Reverse { get; }
        public int Capacity { get; set; }
        public float Cost { get; }
        public SlotEdge? Meta { get; }
    }

    private readonly record struct PartyNode(
        uint Id,
        PartyNodeSide Side,
        Vector3 Position,
        Vector3 CommandAnchor,
        float PreferredEngageRange,
        uint TargetId,
        bool IsRanged);

    private enum PartyNodeSide
    {
        Friendly,
        Enemy,
    }
}
