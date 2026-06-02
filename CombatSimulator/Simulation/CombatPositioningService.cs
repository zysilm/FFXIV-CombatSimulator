using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Simulation;

public enum CombatSide
{
    Friendly,
    Enemy,
}

public sealed unsafe class CombatPositioningService
{
    private const float DefaultMeleeRange = 1.0f;
    private const float MinSlotSpacing = 1.8f;
    private const float PlayerPersonalSpace = 1.4f;
    private const float OccupiedSlotPenalty = 80.0f;
    private const float PlayerSpacePenalty = 350.0f;

    private readonly Dictionary<uint, ReservedSlot> reservations = new();
    private Vector3 playerPosition;
    private Vector3 playerForward = Vector3.UnitZ;
    private Vector3 friendlyCenter;
    private Vector3 enemyCenter;
    private Vector3 battleAxis = Vector3.UnitZ;
    private uint frame;

    public void BeginFrame(
        Vector3 playerPosition,
        float playerRotation,
        IReadOnlyList<CombatCompanion> companions,
        IReadOnlyList<SimulatedNpc> enemies)
    {
        frame++;
        this.playerPosition = playerPosition;
        playerForward = FlatNormalize(new Vector3(MathF.Sin(playerRotation), 0, MathF.Cos(playerRotation)), Vector3.UnitZ);

        var friendlyPositions = new List<Vector3> { playerPosition };
        foreach (var companion in companions)
        {
            if (!companion.IsSpawned || !companion.State.IsAlive || companion.BattleChara == null)
                continue;

            friendlyPositions.Add(((GameObject*)companion.BattleChara)->Position);
        }

        var enemyPositions = new List<Vector3>();
        foreach (var enemy in enemies)
        {
            if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null)
                continue;

            enemyPositions.Add(((GameObject*)enemy.BattleChara)->Position);
        }

        friendlyCenter = Average(friendlyPositions, playerPosition);
        enemyCenter = Average(enemyPositions, playerPosition + playerForward);
        battleAxis = FlatNormalize(enemyCenter - friendlyCenter, playerForward);

        var liveActorIds = new HashSet<uint>(companions
            .Where(c => c.IsSpawned && c.State.IsAlive)
            .Select(c => c.SimulatedEntityId));
        foreach (var enemy in enemies)
        {
            if (enemy.IsSpawned && enemy.State.IsAlive)
                liveActorIds.Add(enemy.SimulatedEntityId);
        }

        foreach (var key in reservations.Keys.ToList())
        {
            if (!liveActorIds.Contains(key))
                reservations.Remove(key);
        }
    }

    public bool TryGetCompanionCombatPosition(
        CombatCompanion companion,
        SimulatedNpc target,
        out Vector3 desiredPosition)
    {
        desiredPosition = default;
        if (!companion.IsSpawned || !companion.State.IsAlive ||
            companion.BattleChara == null || target.BattleChara == null)
        {
            Release(companion.SimulatedEntityId);
            return false;
        }

        var actorPos = (Vector3)((GameObject*)companion.BattleChara)->Position;
        var targetPos = (Vector3)((GameObject*)target.BattleChara)->Position;
        var direction = FlatNormalize(friendlyCenter - enemyCenter, -battleAxis);
        var range = MathF.Max(DefaultMeleeRange, companion.Behavior.AutoAttackRange);

        desiredPosition = ReserveBestSlot(
            companion.SimulatedEntityId,
            target.SimulatedEntityId,
            CombatSide.Friendly,
            actorPos,
            targetPos,
            direction,
            range,
            protectPlayerSpace: true);
        return true;
    }

    public bool TryGetEnemyCombatPosition(
        SimulatedNpc enemy,
        SimulatedEntityState target,
        Vector3 targetPosition,
        out Vector3 desiredPosition)
    {
        desiredPosition = default;
        if (!enemy.IsSpawned || !enemy.State.IsAlive || enemy.BattleChara == null || !target.IsAlive)
        {
            Release(enemy.SimulatedEntityId);
            return false;
        }

        var actorPos = (Vector3)((GameObject*)enemy.BattleChara)->Position;
        var direction = FlatNormalize(actorPos - targetPosition, enemyCenter - friendlyCenter);
        var range = GetEnemyCombatRange(enemy);

        desiredPosition = ReserveBestSlot(
            enemy.SimulatedEntityId,
            target.EntityId,
            CombatSide.Enemy,
            actorPos,
            targetPosition,
            direction,
            range,
            protectPlayerSpace: !target.IsPlayer);
        return true;
    }

    public void Release(uint actorId)
        => reservations.Remove(actorId);

    private static float GetEnemyCombatRange(SimulatedNpc enemy)
        => enemy.Behavior.AutoAttackStyle is NpcAttackStyle.Ranged or NpcAttackStyle.Magic
            ? MathF.Max(DefaultMeleeRange, enemy.Behavior.AutoAttackRange)
            : DefaultMeleeRange;

    private Vector3 ReserveBestSlot(
        uint actorId,
        uint targetId,
        CombatSide side,
        Vector3 actorPosition,
        Vector3 targetPosition,
        Vector3 preferredDirection,
        float desiredRange,
        bool protectPlayerSpace)
    {
        var candidates = GenerateArcSlots(targetPosition, preferredDirection, desiredRange);
        if (candidates.Count == 0)
            return targetPosition;

        reservations.TryGetValue(actorId, out var current);
        if (current != null && current.TargetId == targetId && current.Side == side)
        {
            var held = targetPosition + current.Offset;
            reservations[actorId] = new ReservedSlot
            {
                ActorId = current.ActorId,
                TargetId = current.TargetId,
                Side = current.Side,
                Position = held,
                Offset = current.Offset,
                LastSeenFrame = frame,
            };
            return held;
        }

        var best = candidates[0];
        var bestScore = float.MaxValue;
        foreach (var candidate in candidates)
        {
            var score = ScoreSlot(actorId, targetId, side, actorPosition, candidate, current, protectPlayerSpace);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        reservations[actorId] = new ReservedSlot
        {
            ActorId = actorId,
            TargetId = targetId,
            Side = side,
            Position = best,
            Offset = best - targetPosition,
            LastSeenFrame = frame,
        };
        return best;
    }

    private float ScoreSlot(
        uint actorId,
        uint targetId,
        CombatSide side,
        Vector3 actorPosition,
        Vector3 candidate,
        ReservedSlot? current,
        bool protectPlayerSpace)
    {
        var score = FlatDistance(actorPosition, candidate);

        foreach (var reservation in reservations.Values)
        {
            if (reservation.ActorId == actorId)
                continue;

            var distance = FlatDistance(reservation.Position, candidate);
            if (distance < MinSlotSpacing)
                score += OccupiedSlotPenalty * (1f - distance / MinSlotSpacing);
        }

        if (protectPlayerSpace)
        {
            var playerDistance = FlatDistance(playerPosition, candidate);
            if (playerDistance < PlayerPersonalSpace)
                score += PlayerSpacePenalty * (1f - playerDistance / PlayerPersonalSpace);
        }

        return score;
    }

    private static List<Vector3> GenerateArcSlots(Vector3 center, Vector3 direction, float radius)
    {
        var slots = new List<Vector3>();
        var angles = new[] { 0f, -25f, 25f, -50f, 50f, -75f, 75f, -110f, 110f, 180f };
        var baseAngle = MathF.Atan2(direction.X, direction.Z);
        foreach (var degrees in angles)
        {
            var angle = baseAngle + degrees * MathF.PI / 180f;
            slots.Add(center + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * radius);
        }

        return slots;
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

    private static Vector3 FlatNormalize(Vector3 vector, Vector3 fallback)
    {
        vector.Y = 0;
        if (vector.LengthSquared() < 0.0001f)
            return FlatNormalize(fallback, Vector3.UnitZ);
        return Vector3.Normalize(vector);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private sealed class ReservedSlot
    {
        public uint ActorId { get; init; }
        public uint TargetId { get; init; }
        public CombatSide Side { get; init; }
        public Vector3 Position { get; init; }
        public Vector3 Offset { get; init; }
        public uint LastSeenFrame { get; init; }
    }
}
