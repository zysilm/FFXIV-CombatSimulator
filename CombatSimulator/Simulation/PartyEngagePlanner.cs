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
    private const float MeleeFormationRadius = 1.85f;
    private const float RangedFormationRadius = 7.5f;
    private const float FormationRingStep = 0.85f;
    private const int FormationSlotsPerRing = 10;

    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly Dictionary<uint, PartyEngagePlan> plans = new();
    private readonly Dictionary<string, PlannerPathState> pathStates = new();
    private readonly Dictionary<uint, FormationSlotAssignment> formationSlots = new();

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
        float commandRandomness)
    {
        plans.Clear();
        TickPathStateTimers(deltaTime);

        var nodes = BuildNodes(
            commandAnchorPosition,
            companions,
            enemies,
            companionTargets,
            enemyTargets);

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

            if (plans.TryGetValue(node.TargetId, out var targetPlan))
            {
                plans[node.Id] = BuildChainPlan(node, targetPlan, commandRange, commandRandomness, livePathKeys);
                continue;
            }

            if (nodes.TryGetValue(node.TargetId, out var targetNode))
            {
                plans[node.Id] = BuildPathEngagePlan(
                    $"open:{node.Id}:{targetNode.Id}",
                    node,
                    targetNode.Position,
                    targetNode.Id,
                    commandRange,
                    commandRandomness,
                    livePathKeys,
                    PartyEngagePlanKind.EngagePoint);
            }
        }

        ApplyFormationSlots(nodes, playerPosition, playerForward, commandRange, commandRandomness);

        foreach (var key in pathStates.Keys.ToList())
        {
            if (!livePathKeys.Contains(key))
                pathStates.Remove(key);
        }

        var liveActorIds = nodes.Keys.ToHashSet();
        foreach (var actorId in formationSlots.Keys.ToList())
        {
            if (!liveActorIds.Contains(actorId))
                formationSlots.Remove(actorId);
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

    private Dictionary<uint, PartyNode> BuildNodes(
        Vector3 commandAnchorPosition,
        IReadOnlyList<CombatCompanion> companions,
        IReadOnlyList<SimulatedNpc> enemies,
        IReadOnlyDictionary<uint, uint> companionTargets,
        IReadOnlyDictionary<uint, uint> enemyTargets)
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
                companionTargets.GetValueOrDefault(companion.SimulatedEntityId),
                GetFormationRadius(companion.Behavior));
        }

        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;

            var position = (Vector3)((GameObject*)enemy.BattleChara)->Position;
            nodes[enemy.SimulatedEntityId] = new PartyNode(
                enemy.SimulatedEntityId,
                PartyNodeSide.Enemy,
                position,
                enemy.SpawnPosition,
                enemyTargets.GetValueOrDefault(enemy.SimulatedEntityId),
                GetFormationRadius(enemy.Behavior));
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
                centroid,
                node.TargetId,
                commandRange,
                commandRandomness,
                livePathKeys,
                PartyEngagePlanKind.EngagePoint);
        }
    }

    private PartyEngagePlan BuildChainPlan(
        PartyNode node,
        PartyEngagePlan targetPlan,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys)
    {
        var key = $"chain:{node.Id}:{targetPlan.ActorId}";
        livePathKeys.Add(key);
        var stableGoal = GetStableChainGoal(key, targetPlan.Goal);
        return BuildExplicitGoalPlan(
            node,
            node.TargetId,
            targetPlan.Goal,
            stableGoal,
            commandRange,
            commandRandomness,
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
        var delayedGoal = GetDelayedPlayerGoal(key, playerPosition);
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
        Vector3 targetPosition,
        uint targetId,
        float commandRange,
        float commandRandomness,
        HashSet<string> livePathKeys,
        PartyEngagePlanKind kind)
    {
        var path = GetSharedPath(key, actor.Position, targetPosition, livePathKeys);
        return BuildPathPointPlan(actor, targetId, targetPosition, path, fromStart: true, commandRange, commandRandomness, kind);
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
        var distance = MathF.Min(pathLength, GetEngageDistance(actor.Id, commandRange, commandRandomness));
        var goal = PointAlongPath(path, fromStart ? distance : pathLength - distance);
        goal = AddStableJitter(actor.Id, goal, commandRange * 0.04f);
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

    private void ApplyFormationSlots(
        IReadOnlyDictionary<uint, PartyNode> nodes,
        Vector3 playerPosition,
        Vector3 playerForward,
        float commandRange,
        float commandRandomness)
    {
        if (plans.Count <= 1)
            return;

        var updatedPlans = new Dictionary<uint, PartyEngagePlan>();
        var groups = plans.Values
            .Where(p => p.Kind != PartyEngagePlanKind.ReturnToCommand && p.TargetId != 0 && nodes.ContainsKey(p.ActorId))
            .GroupBy(p => p.TargetId);

        foreach (var group in groups)
        {
            var groupPlans = group
                .OrderBy(p => StableUnit(p.ActorId, 0x5A11u))
                .ToList();
            if (groupPlans.Count == 0)
                continue;

            var center = GetFormationCenter(groupPlans, nodes, playerPosition);
            var baseDirection = GetFormationDirection(groupPlans, nodes, center, playerForward);
            var assignedSlots = AssignFormationSlots(group.Key, groupPlans);

            foreach (var plan in groupPlans)
            {
                var node = nodes[plan.ActorId];
                var slotIndex = assignedSlots[plan.ActorId];
                var goal = BuildFormationGoal(center, baseDirection, node.FormationRadius, slotIndex);
                goal = ClampToCommandAnchor(node, goal, commandRange, commandRandomness, out var leashed);
                if (leashed)
                    goal = plan.Goal;

                formationSlots[plan.ActorId] = new FormationSlotAssignment(plan.TargetId, slotIndex);
                updatedPlans[plan.ActorId] = WithGoal(plan, goal);
            }
        }

        foreach (var (actorId, plan) in updatedPlans)
            plans[actorId] = plan;
    }

    private Dictionary<uint, int> AssignFormationSlots(uint targetId, IReadOnlyList<PartyEngagePlan> groupPlans)
    {
        var assigned = new Dictionary<uint, int>();
        var used = new HashSet<int>();

        foreach (var plan in groupPlans)
        {
            if (!formationSlots.TryGetValue(plan.ActorId, out var slot) || slot.TargetId != targetId)
                continue;

            assigned[plan.ActorId] = slot.SlotIndex;
            used.Add(slot.SlotIndex);
        }

        var nextSlot = 0;
        foreach (var plan in groupPlans)
        {
            if (assigned.ContainsKey(plan.ActorId))
                continue;

            while (used.Contains(nextSlot))
                nextSlot++;
            assigned[plan.ActorId] = nextSlot;
            used.Add(nextSlot);
        }

        return assigned;
    }

    private static Vector3 GetFormationCenter(
        IReadOnlyList<PartyEngagePlan> groupPlans,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        Vector3 playerPosition)
    {
        if (groupPlans[0].TargetId != 0 && groupPlans[0].HasFaceTarget)
            return groupPlans[0].FaceTarget;

        var sum = Vector3.Zero;
        var count = 0;
        foreach (var plan in groupPlans)
        {
            if (!nodes.TryGetValue(plan.ActorId, out var node))
                continue;
            sum += node.Position;
            count++;
        }

        return count > 0 ? sum / count : playerPosition;
    }

    private static Vector3 GetFormationDirection(
        IReadOnlyList<PartyEngagePlan> groupPlans,
        IReadOnlyDictionary<uint, PartyNode> nodes,
        Vector3 center,
        Vector3 fallback)
    {
        var sum = Vector3.Zero;
        foreach (var plan in groupPlans)
        {
            if (nodes.TryGetValue(plan.ActorId, out var node))
                sum += node.Position - center;
        }

        return FlatNormalize(sum, fallback);
    }

    private static Vector3 BuildFormationGoal(Vector3 center, Vector3 baseDirection, float radius, int slotIndex)
    {
        var ring = slotIndex / FormationSlotsPerRing;
        var slot = slotIndex % FormationSlotsPerRing;
        var angleOffsets = new[] { 0f, -35f, 35f, -70f, 70f, -110f, 110f, 145f, -145f, 180f };
        var baseAngle = MathF.Atan2(baseDirection.X, baseDirection.Z);
        var angle = baseAngle + angleOffsets[slot] * MathF.PI / 180f;
        var slotRadius = radius + ring * FormationRingStep;
        var goal = center + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * slotRadius;
        goal.Y = center.Y;
        return goal;
    }

    private static PartyEngagePlan WithGoal(PartyEngagePlan plan, Vector3 goal)
    {
        return new PartyEngagePlan
        {
            ActorId = plan.ActorId,
            TargetId = plan.TargetId,
            Kind = plan.Kind,
            Goal = goal,
            FaceTarget = plan.FaceTarget,
            HasFaceTarget = plan.HasFaceTarget,
        };
    }

    private static float GetFormationRadius(NpcBehavior behavior)
    {
        return behavior.AutoAttackStyle is NpcAttackStyle.Ranged or NpcAttackStyle.Magic
            ? Math.Clamp(behavior.AutoAttackRange, RangedFormationRadius, 12.0f)
            : MeleeFormationRadius;
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

    private static Vector3 FlatNormalize(Vector3 value, Vector3 fallback)
    {
        value.Y = 0;
        if (value.LengthSquared() < 0.0001f)
            return fallback;
        return Vector3.Normalize(value);
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

    private readonly record struct PartyNode(
        uint Id,
        PartyNodeSide Side,
        Vector3 Position,
        Vector3 CommandAnchor,
        uint TargetId,
        float FormationRadius);

    private readonly record struct FormationSlotAssignment(uint TargetId, int SlotIndex);

    private enum PartyNodeSide
    {
        Friendly,
        Enemy,
    }
}
