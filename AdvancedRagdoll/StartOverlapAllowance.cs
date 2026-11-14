// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using BepuPhysics.CollisionDetection;

namespace CombatSimulator.Animation;

/// <summary>
/// Lets the bodies of a ragdoll start out overlapping without being thrown apart.
///
/// A ragdoll takes over a pose it did not make: an arm lies against the chest, the thighs touch, and
/// the volumes standing in for them overlap from the first instant. An ordinary contact treats that
/// overlap as an error to be pushed out, while the joints hold the two bodies where they are; the
/// limb is driven back and forth between the two and flung about.
///
/// Here a contact only resists going deeper than the pair already was. The depth a pair has when the
/// ragdoll starts is allowed; whenever the pair is found less deep than its allowance, the allowance
/// shrinks to that, and it never grows. So an overlap is never pushed out, only prevented from getting
/// worse, and once two bodies have come apart they collide in full, like any pair that started clear.
/// It applies to every pair alike and adds no energy: at the start every such contact carries no load.
/// </summary>
internal sealed class StartOverlapAllowance
{
    // Below this an allowance is spent.
    private const float Spent = 0.001f;

    private readonly Dictionary<(int, int), (float Allowed, int Step)> pairs = new();
    private readonly object gate = new();
    private int step;

    /// <summary>Call once before every simulation step. Depths seen during the first one become allowances.</summary>
    public void BeginStep()
    {
        lock (gate)
            step++;
    }

    /// <summary>Number of pairs still carrying an allowance.</summary>
    public int Count
    {
        get { lock (gate) return pairs.Count; }
    }

    /// <summary>Takes the pair's allowance off every contact of its manifold.</summary>
    public void Apply<TManifold>(int bodyA, int bodyB, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var count = manifold.Count;
        if (count == 0)
            return;
        var deepest = float.MinValue;
        for (var i = 0; i < count; i++)
            deepest = MathF.Max(deepest, manifold.GetDepth(i));

        var key = (Math.Min(bodyA, bodyB), Math.Max(bodyA, bodyB));
        float allowed;
        lock (gate)
        {
            if (step <= 1)
            {
                if (deepest <= Spent)
                    return;
                pairs[key] = (deepest, step);
                allowed = deepest;
            }
            else
            {
                if (!pairs.TryGetValue(key, out var entry))
                    return;
                // A pair that dropped out of contact altogether has come apart: nothing is left to allow.
                allowed = entry.Step < step - 1 ? 0f : MathF.Min(entry.Allowed, MathF.Max(deepest, 0f));
                if (allowed <= Spent)
                {
                    pairs.Remove(key);
                    return;
                }
                pairs[key] = (allowed, step);
            }
        }

        for (var i = 0; i < count; i++)
            TManifold.GetDepthReference(ref manifold, i) -= allowed;
    }
}
