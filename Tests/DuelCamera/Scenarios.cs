using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;

namespace DuelCamera;

/// <summary>The scripted duels. Metres, seconds; the ground is y = 0 unless the duel says otherwise.</summary>
public static class Scenarios
{
    private static Vector3 V(float x, float z) => new(x, 0f, z);

    private static float Ease(float t, float from, float length)
    {
        var k = Math.Clamp((t - from) / length, 0f, 1f);
        return k * k * (3f - 2f * k);
    }

    /// <summary>Whether the frame at t is the one nearest to a scripted time.</summary>
    private static bool Now(float t, Feedback f, float at) => MathF.Abs(t - at) < f.Dt * 0.5f;

    /// <summary>Into the screen and to its right for a camera yaw (the game moves the player by these).</summary>
    public static (Vector3 Forward, Vector3 Right) MoveAxes(float yaw)
    {
        var forward = new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        return (forward, new Vector3(-forward.Z, 0f, forward.X));
    }

    public static List<Scenario> All() => new()
    {
        new Scenario
        {
            Name = "approach", Duration = 8f,
            Script = () => (t, f) =>
            {
                var z = MathF.Min(-2f, -12f + 4f * t);
                var m = new Moment { Player = Bodies.Stand(V(0f, z), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                return m;
            },
        },
        new Scenario
        {
            Name = "circling", Duration = 12f,
            Script = () =>
            {
                var angle = MathF.PI;
                return (t, f) =>
                {
                    angle += (t < 6f ? 1f : -1f) * (5f / 3f) * f.Dt;
                    var m = new Moment { Player = Bodies.Stand(V(MathF.Sin(angle) * 3f, MathF.Cos(angle) * 3f), t) };
                    m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "melee dance", Duration = 15f,
            Script = () => (t, f) =>
            {
                var enemy = V(MathF.Sin(t * 0.9f) * 1.5f, MathF.Sin(t * 0.6f + 1f) * 1.5f);
                var radius = 3.2f + MathF.Sin(t * 0.7f) * 0.9f;
                var angle = MathF.PI + MathF.Sin(t * 0.5f) * 2.2f;
                var m = new Moment { Player = Bodies.Stand(enemy + V(MathF.Sin(angle) * radius, MathF.Cos(angle) * radius), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(enemy, t, phase: 1f)));
                return m;
            },
        },
        DashThrough("dash through", 60),
        DashThrough("dash through, 20 fps", 20),
        new Scenario
        {
            Name = "knock-back", Duration = 11f,
            Script = () => (t, f) =>
            {
                // The opponent is thrown 8 m at 2 s and walks back; the player is thrown 6 m at 7 s and walks back.
                var enemyZ = 2.5f + 8f * Ease(t, 2f, 0.35f) - 8f * Ease(t, 3.5f, 2.5f);
                var playerZ = -6f * Ease(t, 7f, 0.3f) + 6f * Ease(t, 8.2f, 2f);
                var m = new Moment { Player = Bodies.Stand(V(0f, playerZ), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, enemyZ), t, phase: 1f)));
                if (Now(t, f, 2f)) m.Beats.Add(DuelBeat.HitGiven);
                if (Now(t, f, 7f)) m.Beats.Add(DuelBeat.HitTaken);
                return m;
            },
        },
        new Scenario
        {
            Name = "one down, next, nobody", Duration = 13f,
            Script = () => (t, f) =>
            {
                // The first opponent goes down at 4 s; the second stands behind the player and walks in; it goes
                // down at 10 s and nobody is left.
                var m = new Moment { Player = Bodies.Stand(V(0f, 0f), t) };
                var first = V(0f, 3f);
                m.Enemies.Add(t < 4f
                    ? new Enemy(1, Bodies.Stand(first, t, phase: 1f))
                    : new Enemy(1, Bodies.Fall(first, t, 4f, V(0.3f, 1f)), Alive: false, Attacking: false));
                var second = Vector3.Lerp(V(-6f, -7f), V(-1.5f, -2.5f), Ease(t, 5.5f, 2.5f));
                if (t < 11.6f)
                    m.Enemies.Add(t < 10f
                        ? new Enemy(2, Bodies.Stand(second, t, phase: 2f), Attacking: t > 5f)
                        : new Enemy(2, Bodies.Fall(second, t, 10f, V(-1f, -0.2f)), Alive: false, Attacking: false));
                if (Now(t, f, 4f) || Now(t, f, 10f)) m.Beats.Add(DuelBeat.Finish);
                return m;
            },
        },
        new Scenario
        {
            Name = "three enemies", Duration = 16f,
            Script = () => (t, f) =>
            {
                var m = new Moment { Player = Bodies.Stand(V(MathF.Sin(t * 0.4f) * 1.5f, MathF.Cos(t * 0.3f) * 1.5f), t) };
                var who = (int)(t / 4f) % 3;
                for (var i = 0; i < 3; i++)
                {
                    var a = i * 2.1f + 0.4f + MathF.Sin(t * 0.25f + i) * 0.4f;
                    var r = 4f + i * 1.3f + MathF.Sin(t * 0.5f + i * 2f) * 0.8f;
                    var striking = who == i && t % 4f > 1.5f && t % 4f < 2.6f;
                    m.Enemies.Add(new Enemy((uint)(i + 1), Bodies.Stand(V(MathF.Sin(a) * r, MathF.Cos(a) * r), t, phase: i), Attacking: who == i || i == 0, Striking: striking));
                }
                return m;
            },
        },
        new Scenario
        {
            Name = "wall", Duration = 20f, CrossingsAllowed = 2, Ordinary = false, StartAzimuth = -40f * DeathShotMath.Deg,
            World = () =>
            {
                var w = new TestWorld();
                w.Walls.Add(new Wall(new Vector2(1.5f, -40f), new Vector2(1.5f, 40f), 6f));
                return w;
            },
            Script = () => (t, f) =>
            {
                // 0–8 s: the fight runs along a wall 0.9 m away. Then the opponent steps away from it and the player
                // circles them once, passing along the wall.
                Vector3 player, enemy;
                if (t < 8f)
                {
                    enemy = V(0.6f, 2f);
                    player = V(0.6f, -1f + MathF.Sin(t * 1.3f) * 0.6f);
                }
                else
                {
                    enemy = Vector3.Lerp(V(0.6f, 2f), V(-1.5f, 2f), Ease(t, 8f, 2f));
                    var start = V(0.6f, -1f + MathF.Sin(8f * 1.3f) * 0.6f);
                    var angle = MathF.PI + (t - 11f) * (3f / 2.8f);
                    var onCircle = enemy + V(MathF.Sin(MathF.Max(MathF.PI, angle)) * 2.8f, MathF.Cos(MathF.Max(MathF.PI, angle)) * 2.8f);
                    player = t < 11f ? Vector3.Lerp(start, onCircle, Ease(t, 8f, 3f)) : onCircle;
                }
                var m = new Moment { Player = Bodies.Stand(player, t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(enemy, t, phase: 1f)));
                return m;
            },
        },
        new Scenario
        {
            Name = "key moments", Duration = 12f,
            Script = () => (t, f) =>
            {
                var m = new Moment { Player = Bodies.Stand(V(MathF.Sin(t) * 0.3f, -3f), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, MathF.Sin(t * 0.8f) * 0.3f), t, phase: 1f)));
                foreach (var (at, beat) in BeatTimes)
                    if (Now(t, f, at)) m.Beats.Add(beat);
                return m;
            },
        },
        new Scenario
        {
            Name = "player's hand", Duration = 13f, CrossingsAllowed = 1,
            Script = () => (t, f) =>
            {
                var m = new Moment { Player = Bodies.Stand(V(0f, -3.5f), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                // 60° one way and let go; then 120° the other way (across the line); wheel in; pitch up.
                if (t >= 2f && t < 2.5f) m.YawInput = 60f * DeathShotMath.Deg * f.Dt / 0.5f;
                if (t >= 6f && t < 6.5f) m.YawInput = -120f * DeathShotMath.Deg * f.Dt / 0.5f;
                if (Now(t, f, 9f)) m.WheelInput = 3f;
                if (t >= 10f && t < 10.5f) m.PitchInput = 20f * DeathShotMath.Deg * f.Dt / 0.5f;
                return m;
            },
        },
        new Scenario
        {
            Name = "hold W", Duration = 8f,
            Script = () =>
            {
                var player = V(0f, -12f);
                return (t, f) =>
                {
                    if (t > 0f && Vector3.Distance(player, Vector3.Zero) > 2.2f)
                        player += MoveAxes(f.Yaw).Forward * 5f * f.Dt;
                    var m = new Moment { Player = Bodies.Stand(player, t) };
                    m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "hold D", Duration = 10f,
            Script = () =>
            {
                var player = V(0f, -3f);
                return (t, f) =>
                {
                    if (t > 0f) player += MoveAxes(f.Yaw).Right * 5f * f.Dt;
                    var m = new Moment { Player = Bodies.Stand(player, t) };
                    m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "large monster", Duration = 10f,
            Script = () =>
            {
                var angle = MathF.PI;
                return (t, f) =>
                {
                    var radius = 6f - 2.5f * Ease(t, 6f, 2f);
                    angle += 5f / radius * f.Dt;
                    var m = new Moment { Player = Bodies.Stand(V(MathF.Sin(angle) * radius, MathF.Cos(angle) * radius), t) };
                    m.Enemies.Add(new Enemy(1, new DuelSubject(V(0f, 0f), new Vector3(MathF.Sin(t) * 0.2f, 5f, 0f), 2.5f)));
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "bank", Duration = 12f,
            World = () => new TestWorld { Ground = Bank },
            Script = () =>
            {
                var angle = MathF.PI;
                return (t, f) =>
                {
                    angle += 3f / 2.5f * f.Dt;
                    var enemy = new Vector3(-0.5f, Bank(-0.5f, 0f), 0f);
                    var x = enemy.X + MathF.Sin(angle) * 2.5f;
                    var z = MathF.Cos(angle) * 2.5f;
                    var m = new Moment { Player = Bodies.Stand(new Vector3(x, Bank(x, z), z), t) };
                    m.Enemies.Add(new Enemy(1, Bodies.Stand(enemy, t, phase: 1f)));
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "nobody", Duration = 8f,
            Script = () =>
            {
                var player = V(0f, 0f);
                return (t, f) =>
                {
                    var leg = (int)(t / 2f) % 4;
                    player += (leg switch { 0 => V(1f, 0f), 1 => V(0f, 1f), 2 => V(-1f, 0f), _ => V(0f, -1f) }) * 6f * f.Dt;
                    var m = new Moment { Player = Bodies.Stand(player, t) };
                    if (t >= 3f && t < 3.5f) m.YawInput = 90f * DeathShotMath.Deg * f.Dt / 0.5f;
                    return m;
                };
            },
        },
        new Scenario
        {
            Name = "entry from the side", Duration = 6f, StartAzimuth = 90f * DeathShotMath.Deg, Ordinary = false,
            Script = () => (t, f) =>
            {
                var m = new Moment { Player = Bodies.Stand(V(0f, -3f), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                return m;
            },
        },
        new Scenario
        {
            Name = "entry from the front", Duration = 6f, StartAzimuth = 180f * DeathShotMath.Deg, Ordinary = false,
            Script = () => (t, f) =>
            {
                var m = new Moment { Player = Bodies.Stand(V(0f, -3f), t) };
                m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, 0f), t, phase: 1f)));
                return m;
            },
        },
    };

    public static readonly (float At, DuelBeat Beat)[] BeatTimes =
    {
        (2f, DuelBeat.HitGiven), (4f, DuelBeat.Guard), (6f, DuelBeat.Threat), (6.4f, DuelBeat.HitTaken),
        (8f, DuelBeat.HitGiven), (8.3f, DuelBeat.HitGiven), (10f, DuelBeat.Finish),
    };

    /// <summary>A bank: the ground rises 1.5 m over a metre for x below −2.5.</summary>
    public static float Bank(float x, float z)
    {
        var k = Math.Clamp((-2.5f - x) / 1f, 0f, 1f);
        return 1.5f * k * k * (3f - 2f * k);
    }

    private static Scenario DashThrough(string name, int fps) => new()
    {
        Name = name, Duration = 12f, Fps = fps, DashEnds = [3.3f, 6.3f, 9.3f], Ordinary = false,
        Script = () => (t, f) =>
        {
            // The player dashes through the opponent at 3 s and back at 6 s (6 m in 0.3 s, passing 0.4 m beside
            // them); the opponent dashes through the player at 9 s.
            var playerZ = -3f + 6f * Ease(t, 3f, 0.3f) - 6f * Ease(t, 6f, 0.3f);
            var enemyZ = -6f * Ease(t, 9f, 0.3f);
            var m = new Moment { Player = Bodies.Stand(V(0.4f, playerZ), t) };
            m.Enemies.Add(new Enemy(1, Bodies.Stand(V(0f, enemyZ), t, phase: 1f)));
            return m;
        },
    };
}
