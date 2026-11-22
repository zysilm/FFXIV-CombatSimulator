using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;

namespace DuelCamera;

/// <summary>An upright wall: a segment in plan (xz), from the ground up to a height.</summary>
public readonly record struct Wall(Vector2 A, Vector2 B, float Top);

/// <summary>The test world: a ground height function and upright walls.</summary>
public sealed class TestWorld : IDeathShotWorld
{
    public Func<float, float, float> Ground = (_, _) => 0f;
    public float? Water;
    public readonly List<Wall> Walls = new();
    public int Rays;

    public float? GroundBelow(Vector3 from)
    {
        Rays++;
        var g = Ground(from.X, from.Z);
        return from.Y >= g ? g : null;
    }

    public float? WaterBelow(Vector3 from)
    {
        Rays++;
        return Water;
    }

    public bool Blocked(Vector3 from, Vector3 to, out float distance)
    {
        Rays++;
        distance = 0f;
        var along = to - from;
        var length = along.Length();
        if (length < 1e-4f) return false;
        var best = float.PositiveInfinity;
        foreach (var wall in Walls)
        {
            // Segment against segment in plan, then the height at the crossing.
            var p = new Vector2(from.X, from.Z);
            var r = new Vector2(along.X, along.Z);
            var s = wall.B - wall.A;
            var cross = r.X * s.Y - r.Y * s.X;
            if (MathF.Abs(cross) < 1e-8f) continue;
            var qp = wall.A - p;
            var t = (qp.X * s.Y - qp.Y * s.X) / cross;
            var u = (qp.X * r.Y - qp.Y * r.X) / cross;
            if (t < 0f || t > 1f || u < 0f || u > 1f) continue;
            var y = from.Y + along.Y * t;
            if (y > wall.Top) continue;
            best = MathF.Min(best, t * length);
        }
        // The ground, by marching.
        var steps = Math.Max(2, (int)(length / 0.1f));
        for (var i = 1; i <= steps; i++)
        {
            var q = from + along * (i / (float)steps);
            if (q.Y < Ground(q.X, q.Z) - 0.01f)
            {
                best = MathF.Min(best, length * (i - 1) / steps);
                break;
            }
        }
        if (float.IsFinite(best))
        {
            distance = best;
            return true;
        }
        return false;
    }

    /// <summary>Whether a point is on the far side of a wall from another (the segment between them crosses one).</summary>
    public bool WallBetween(Vector3 a, Vector3 b)
    {
        var ground = Ground;
        Ground = (_, _) => float.NegativeInfinity;
        var hit = Blocked(a, b, out _);
        Ground = ground;
        return hit;
    }
}

/// <summary>One enemy of a scripted frame.</summary>
public readonly record struct Enemy(uint Id, DuelSubject Body, bool Alive = true, bool Attacking = true, bool Striking = false, bool Targeted = false);

/// <summary>One scripted frame: the fighters, the player's hand on the camera, the key moments.</summary>
public sealed class Moment
{
    public DuelSubject Player;
    public readonly List<Enemy> Enemies = new();
    public float YawInput, PitchInput, WheelInput;
    public readonly List<DuelBeat> Beats = new();
}

/// <summary>What a scripted duel may read back: the yaw the camera rendered last (the player's movement is relative to it).</summary>
public sealed class Feedback
{
    public float Yaw;
    public float Dt;
}

public sealed class Scenario
{
    public required string Name;
    public required float Duration;
    public Func<TestWorld> World = () => new TestWorld();
    /// <summary>Builds the frame at time t (called once per frame, in order).</summary>
    public required Func<Func<float, Feedback, Moment>> Script;
    /// <summary>Times at which a dash through the other fighter ends (recovery is measured from there).</summary>
    public float[] DashEnds = [];
    /// <summary>Crossings of the line between the fighters this duel may show (the player dragging the camera across,
    /// a wall).</summary>
    public int CrossingsAllowed;
    /// <summary>Counted into the framing gates (the camera's ordinary work, not a stress case).</summary>
    public bool Ordinary = true;
    public int Fps = 60;
    /// <summary>The camera the shot starts from sits this far round the player from "behind them, facing the opponent".</summary>
    public float StartAzimuth;
}

public static class Bodies
{
    /// <summary>A humanoid standing at <paramref name="feet"/>, with a little animation in the head.</summary>
    public static DuelSubject Stand(Vector3 feet, float t, float height = 1.5f, float radius = 0.4f, float phase = 0f)
    {
        var sway = new Vector3(MathF.Sin(t * 7f + phase) * 0.02f, MathF.Sin(t * 11f + phase) * 0.015f, MathF.Cos(t * 5f + phase) * 0.02f);
        return new DuelSubject(feet, feet + new Vector3(0f, height, 0f) + sway, radius);
    }

    /// <summary>A humanoid going down over <paramref name="fall"/> seconds from <paramref name="since"/>, ending on the ground.</summary>
    public static DuelSubject Fall(Vector3 feet, float t, float since, Vector3 toward, float height = 1.5f, float fall = 0.8f)
    {
        var k = Math.Clamp((t - since) / fall, 0f, 1f);
        k = k * k * (3f - 2f * k);
        var up = new Vector3(0f, height, 0f);
        var down = Vector3.Normalize(new Vector3(toward.X, 0f, toward.Z)) * height * 0.9f + new Vector3(0f, 0.2f, 0f);
        return new DuelSubject(feet, feet + Vector3.Lerp(up, down, k), 0.4f);
    }
}
