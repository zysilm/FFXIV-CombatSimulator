using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Simulation;

public enum LocalSteeringFaction
{
    Party,
    Enemy,
}

public readonly record struct LocalSteeringActor(
    uint Id,
    LocalSteeringFaction Faction,
    Vector3 Position,
    bool IsPc);

public static class LocalSteering
{
    public const float SeparationRadius = 2.8f;
    public const float AllyWeight = 1.35f;
    public const float EnemyWeight = 0.75f;
    public const float PcPersonalRadius = 3.6f;
    public const float PcPersonalWeight = 1.45f;
    public const float GoalWeight = 1.0f;
    public const float MaxInfluence = 1.4f;

    public static Vector3 SteerFlatDirection(
        LocalSteeringActor actor,
        Vector3 goalFlatDirection,
        IEnumerable<LocalSteeringActor> actors)
    {
        goalFlatDirection.Y = 0;
        var goalLenSq = goalFlatDirection.LengthSquared();
        if (goalLenSq <= 0.000001f)
            return Vector3.Zero;

        var goal = goalFlatDirection / MathF.Sqrt(goalLenSq) * GoalWeight;
        var steer = SteeringVector(actor, actors);
        var move = goal + steer;
        move.Y = 0;

        var moveLenSq = move.LengthSquared();
        return moveLenSq <= 0.000001f
            ? Vector3.Zero
            : move / MathF.Sqrt(moveLenSq);
    }

    public static Vector3 SteeringVector(
        LocalSteeringActor actor,
        IEnumerable<LocalSteeringActor> actors)
    {
        if (actor.IsPc)
            return Vector3.Zero;

        var sx = 0f;
        var sz = 0f;
        LocalSteeringActor? pc = null;

        foreach (var other in actors)
        {
            if (other.Id == actor.Id)
                continue;

            if (other.IsPc)
                pc = other;

            var dx = actor.Position.X - other.Position.X;
            var dz = actor.Position.Z - other.Position.Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= 0.001f || d >= SeparationRadius)
                continue;

            var laneWeight = other.Faction == actor.Faction ? AllyWeight : EnemyWeight;
            var t = (1f - d / SeparationRadius) * laneWeight;
            sx += dx / d * t;
            sz += dz / d * t;
        }

        if (pc is { } player)
        {
            var dx = actor.Position.X - player.Position.X;
            var dz = actor.Position.Z - player.Position.Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > 0.001f && d < PcPersonalRadius)
            {
                var t = (1f - d / PcPersonalRadius) * PcPersonalWeight;
                sx += dx / d * t;
                sz += dz / d * t;
            }
        }

        var len = MathF.Sqrt(sx * sx + sz * sz);
        if (len > MaxInfluence)
        {
            sx = sx / len * MaxInfluence;
            sz = sz / len * MaxInfluence;
        }

        return new Vector3(sx, 0, sz);
    }
}
