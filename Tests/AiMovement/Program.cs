using CombatSimulator.Simulation;

int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
bool Near(float a, float b, float eps = 1e-4f) => MathF.Abs(a - b) <= eps;

// --- Terrain follow ---------------------------------------------------------------------------
// Rising always snaps.
Check(Near(EnemyMovementMath.FollowTerrainY(0f, 2f, 1f / 60f, 0f), 2f), "rise does not snap");
// Standing still settles slowly and independently of frame rate.
float Settle(int fps)
{
    var y = 1f;
    for (var i = 0; i < fps / 2; i++) y = EnemyMovementMath.FollowTerrainY(y, 0f, 1f / fps, 0f);
    return y;
}
Check(Near(Settle(30), Settle(144), 0.01f), $"settle depends on frame rate: {Settle(30)} vs {Settle(144)}");
Check(Near(Settle(60), 0.25f, 0.01f), "settle is not 1.5 y/s");
// Running 9 y/s down a 30 degree slope stays on the ground at any frame rate.
foreach (var fps in new[] { 30, 60, 144, 240 })
{
    float dt = 1f / fps, step = 9f * dt, ground = 0f, y = 0f, worst = 0f;
    for (var i = 0; i < fps * 2; i++)
    {
        ground -= step * MathF.Tan(30f * MathF.PI / 180f);
        y = EnemyMovementMath.FollowTerrainY(y, ground, dt, step);
        worst = MathF.Max(worst, y - ground);
    }
    Check(worst < 0.01f, $"floats {worst:F3}y downhill at {fps} fps");
}
// Never overshoots below the desired height.
Check(EnemyMovementMath.FollowTerrainY(1f, 0.9f, 1f, 50f) >= 0.9f - 1e-5f, "falls below the ground");

Console.WriteLine($"AiMovement: {checks} checks passed.");
