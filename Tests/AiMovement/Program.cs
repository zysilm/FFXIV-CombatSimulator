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

// --- Hitbox spacing -----------------------------------------------------------------------------
Check(Near(EnemyMovementMath.HitboxSpacingExtra(0.5f, 0.5f), 0f), "humanoid pair gets extra spacing");
Check(Near(EnemyMovementMath.HitboxSpacingExtra(0.3f, 0.4f), 0f), "small bodies pull in");
Check(Near(EnemyMovementMath.HitboxSpacingExtra(3f, 0.5f), 2.5f), "big monster extra wrong");
Check(Near(EnemyMovementMath.HitboxSpacingExtra(0.5f, 3f), 2.5f), "extra not symmetric");

// --- Melee ring ---------------------------------------------------------------------------------
foreach (var r in new[] { 0.6f, 1.5f, 1.75f, 3f, 5f, 12f })
{
    var gap = EnemyMovementMath.MinMeleeSlotGap;
    var angles = EnemyMovementMath.MeleeRingAngles(r, gap);
    Check(angles.Length >= 1 && angles.Length <= EnemyMovementMath.MaxMeleeRingSlots, $"slot count {angles.Length} at r={r}");
    Check(Near(angles[0], 0f), "first slot is not the front");
    var min = float.MaxValue;
    for (var i = 0; i < angles.Length; i++)
    for (var j = i + 1; j < angles.Length; j++)
        min = MathF.Min(min, EnemyMovementMath.ChordLength(r, angles[i], angles[j]));
    if (angles.Length > 1)
        Check(min >= gap - 1e-3f, $"slots {min:F2}y apart at r={r}, below {gap}");
    foreach (var a in angles) Check(a > -180.001f && a <= 180.001f, $"angle {a} out of range");
    if (angles.Length > 2) Check(MathF.Sign(angles[1]) == -MathF.Sign(angles[2]), "slots do not alternate sides");
}
Check(EnemyMovementMath.MeleeRingAngles(1.5f, 1.2f).Length >= 6, "a 1.5y ring should still fit six attackers");
Check(EnemyMovementMath.MeleeRingAngles(12f, 1.2f).Length == EnemyMovementMath.MaxMeleeRingSlots, "large ring not capped");

// --- Flank preference ---------------------------------------------------------------------------
Check(Near(EnemyMovementMath.Frontness(0, 2, 0, 0, 0f), 1f), "slot ahead is not front");
Check(Near(EnemyMovementMath.Frontness(0, -2, 0, 0, 0f), -1f), "slot behind is not rear");
Check(Near(EnemyMovementMath.Frontness(2, 0, 0, 0, MathF.PI / 2), 1f), "yaw ignored");
Check(Near(EnemyMovementMath.Frontness(2, 0, 0, 0, 0f), 0f), "flank is not neutral");

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
