// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Pure movement math shared by the enemy approach, the companion mover and the engage planner.
/// No game state, so Tests/AiMovement can exercise it offline.
/// </summary>
public static class EnemyMovementMath
{
    /// <summary>Descent rate (yalms/s) a standing actor settles at. Slow on purpose: stepping off a
    /// corpse should ease down rather than snap.</summary>
    public const float SettleFallSpeed = 1.5f;

    /// <summary>Steepest descent a moving actor follows, as drop per yalm walked (~60°). Ground falls
    /// away under a running actor this fast at most on real terrain, so it stays glued downhill.</summary>
    public const float MaxDescentPerYalm = 1.75f;

    /// <summary>
    /// Next root height when following terrain. Rising snaps (never sink into a slope); falling is
    /// capped. The cap used to be <c>max(0.02, 1.5·dt)</c>: a per-frame floor (so the rate depended on
    /// frame rate) and 1.5 y/s regardless of run speed, so anything running downhill floated. The cap
    /// now scales with the horizontal distance covered this step and keeps the slow settle only for
    /// an actor that is (nearly) standing still.
    /// </summary>
    public static float FollowTerrainY(float fromY, float desiredY, float deltaTime, float horizontalStep)
    {
        if (desiredY >= fromY)
            return desiredY;

        var maxFall = MathF.Max(
            SettleFallSpeed * MathF.Max(0f, deltaTime),
            MathF.Max(0f, horizontalStep) * MaxDescentPerYalm);
        return fromY - MathF.Min(fromY - desiredY, maxFall);
    }
}
