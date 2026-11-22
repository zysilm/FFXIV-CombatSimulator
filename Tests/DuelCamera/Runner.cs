using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;

namespace DuelCamera;

public sealed class Frame
{
    public float T, Dt;
    public DuelSubject Player;
    public DuelSubject? Opponent;
    public uint OpponentId;
    public DuelOutput Out;
    /// <summary>The yaw the movement used on this frame when the game reads it one frame late: the movement yaw
    /// given on the frame before.</summary>
    public float MoveYaw;
    public string? Picked;
}

public enum Camera
{
    Duel,
    /// <summary>The game's orbit camera pulled back: 6 m behind the player, the yaw never touched.</summary>
    GameFar,
    /// <summary>The same, close: 2.5 m.</summary>
    GameClose,
}

public static class Runner
{
    public const float Aspect = 16f / 9f;
    public const float StartDistance = 5f, StartElevation = 15f * DeathShotMath.Deg, StartFov = 0.78f;

    public static List<Frame> Run(Scenario scenario, DuelFraming framing, Camera camera, out TestWorld world, float? moveLean = null)
    {
        world = scenario.World();
        var script = scenario.Script();
        var solver = new DuelCameraSolver(world);
        if (moveLean is { } lean) solver.MoveLean = lean;
        var picker = new DuelOpponentPicker();
        var feedback = new Feedback();
        var frames = new List<Frame>();
        var candidates = new List<DuelCandidate>();
        var dt = 1f / scenario.Fps;
        var count = (int)MathF.Round(scenario.Duration * scenario.Fps);
        var startAz = 0f;
        var lastYaw = 0f;
        for (var i = 0; i < count; i++)
        {
            var t = i * dt;
            feedback.Dt = dt;
            var m = script(t, feedback);
            candidates.Clear();
            foreach (var e in m.Enemies)
                candidates.Add(new DuelCandidate(e.Id, e.Body.Feet, e.Alive, e.Attacking, e.Striking, e.Targeted));
            var id = picker.Pick(candidates, m.Player.Feet, dt, out var why);
            DuelSubject? opponent = null;
            foreach (var e in m.Enemies)
                if (e.Id == id) opponent = e.Body;

            var chest = m.Player.At(0.74f);
            if (i == 0)
            {
                var away = m.Enemies.Count > 0 ? m.Player.Feet - m.Enemies[0].Body.Feet : new Vector3(0f, 0f, -1f);
                startAz = DeathShotMath.Azimuth(away) + scenario.StartAzimuth;
                solver.Begin(chest + DeathShotMath.OrbitDirection(startAz, StartElevation) * StartDistance, chest, StartFov);
                lastYaw = startAz;
            }

            DuelOutput o;
            if (camera == Camera.Duel)
            {
                foreach (var beat in m.Beats) solver.Beat(beat);
                o = solver.Step(new DuelInput
                {
                    Dt = dt,
                    Player = m.Player,
                    Opponent = opponent,
                    OpponentId = id,
                    Aspect = Aspect,
                    Framing = framing,
                    YawInput = m.YawInput,
                    PitchInput = m.PitchInput,
                    WheelInput = m.WheelInput,
                });
            }
            else
            {
                // The game's own orbit camera with nobody touching it: a fixed yaw (plus what the script's player
                // drags), a fixed pitch and distance, looking at the player.
                startAz += m.YawInput;
                var distance = camera == Camera.GameFar ? 6f : 2.5f;
                var eye = chest + DeathShotMath.OrbitDirection(startAz, StartElevation) * distance;
                if (world.GroundBelow(eye + new Vector3(0f, 1.5f, 0f)) is { } g && eye.Y < g + 0.25f) eye.Y = g + 0.25f;
                o = new DuelOutput
                {
                    Eye = eye,
                    LookAt = chest,
                    Fov = StartFov,
                    Yaw = DeathShotMath.Azimuth(eye - chest),
                    MoveYaw = DeathShotMath.Azimuth(eye - chest),
                    TwoShot = opponent != null,
                    Cut = i == 0,
                    Pull = 1f,
                    Reason = "",
                };
            }

            frames.Add(new Frame { T = t, Dt = dt, Player = m.Player, Opponent = opponent, OpponentId = id, Out = o, MoveYaw = lastYaw, Picked = why });
            lastYaw = o.MoveYaw;
            feedback.Yaw = o.MoveYaw;
        }
        return frames;
    }
}
