using CombatSimulator.Simulation;

int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
bool Near(float a, float b, float eps = 1e-4f) => MathF.Abs(a - b) <= eps;

// --- Circling goal ------------------------------------------------------------------------------
{
    var (x, z) = EnemyMovementMath.CircleGoal(0, 0, 0, 2, 90f, 1f, 3f);
    Check(Near(x, 2f) && Near(z, 0f, 1e-3f), $"90 degree circle goal wrong ({x},{z})");
    (x, z) = EnemyMovementMath.CircleGoal(0, 0, 0, 2, -45f, 1f, 3f);
    Check(x < 0 && Near(MathF.Sqrt(x * x + z * z), 2f), "negative arc does not go the other way");
    // Distance stays inside the band.
    (x, z) = EnemyMovementMath.CircleGoal(5, 5, 5, 5.2f, 40f, 1.2f, 1.8f);
    Check(Near(MathF.Sqrt((x - 5) * (x - 5) + (z - 5) * (z - 5)), 1.2f), "too-close actor not pushed to band");
    (x, z) = EnemyMovementMath.CircleGoal(0, 0, 10, 0, 40f, 1.2f, 1.8f);
    Check(Near(MathF.Sqrt(x * x + z * z), 1.8f), "too-far actor not pulled to band");
    // Degenerate: actor on the target still yields a finite goal.
    (x, z) = EnemyMovementMath.CircleGoal(0, 0, 0, 0, 30f, 1f, 2f);
    Check(float.IsFinite(x) && float.IsFinite(z) && Near(MathF.Sqrt(x * x + z * z), 1f), "degenerate circle goal");
}

// --- Turn rate ----------------------------------------------------------------------------------
Check(Near(EnemyMovementMath.TurnRate(5f, 0.5f), 5f), "humanoid turn rate changed");
Check(Near(EnemyMovementMath.TurnRate(5f, 0.2f), 5f), "small body turns faster than base");
Check(EnemyMovementMath.TurnRate(5f, 1f) < 5f && EnemyMovementMath.TurnRate(5f, 3f) < EnemyMovementMath.TurnRate(5f, 1f), "turn rate not decreasing with size");
Check(Near(EnemyMovementMath.TurnRate(5f, 50f), EnemyMovementMath.MinTurnRate), "turn rate floor missing");
Check(Near(EnemyMovementMath.TurnRate(0.5f, 50f), 0.5f), "floor raises a slow base rate");

// --- Flee goal ----------------------------------------------------------------------------------
{
    var (x, z) = EnemyMovementMath.FleeGoal(0, 0, 0, 10, 1.5f);
    Check(Near(x, 0f) && Near(z, 11.5f), "flee goal is not behind the ally");
    (x, z) = EnemyMovementMath.FleeGoal(3, 3, 3, 3, 1.5f);
    Check(Near(x, 3f) && Near(z, 3f), "degenerate flee goal");
}

// --- Threat -------------------------------------------------------------------------------------
Check(EnemyMovementMath.HitThreat(1000, true) > EnemyMovementMath.HitThreat(2500, false), "tank hit does not outweigh a bigger dps hit");
Check(Near(EnemyMovementMath.HitThreat(-5, false), 0f), "negative damage makes threat");
{
    float a = 100f, b = 100f;
    for (var i = 0; i < 60; i++) a = EnemyMovementMath.DecayThreat(a, 1f / 60f);
    for (var i = 0; i < 144; i++) b = EnemyMovementMath.DecayThreat(b, 1f / 144f);
    Check(Near(a, b, 0.01f), "threat decay depends on frame rate");
    Check(a < 100f && a > 90f, "threat decay too fast or absent");
}
Check(Near(EnemyMovementMath.ThreatShare(50, 100), 0.5f), "share wrong");
Check(Near(EnemyMovementMath.ThreatShare(5, 0), 0f), "share with empty table");

Console.WriteLine($"AiMovement: {checks} checks passed.");
