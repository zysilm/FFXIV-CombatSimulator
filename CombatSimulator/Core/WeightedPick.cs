// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Core;

/// <summary>
/// Roulette selection shared by victory emotes, story profile pools and line variants. The roll
/// is passed in so the selection is deterministic under test; callers supply Random.Shared.
/// </summary>
public static class WeightedPick
{
    /// <summary>Index of the chosen weight, or -1 when nothing has a positive finite weight.
    /// <paramref name="avoid"/> is skipped whenever another candidate is available, so a pool of
    /// two or more never repeats the previous pick back to back.</summary>
    public static int Pick(IReadOnlyList<float> weights, double roll, int avoid = -1)
    {
        var total = 0d;
        var usable = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (!Usable(weights[i])) continue;
            usable++;
            if (i != avoid) total += weights[i];
        }
        if (usable == 0) return -1;
        // A single usable candidate is returned even when it is the one to avoid: repeating is
        // better than picking nothing.
        if (total <= 0d) return avoid;

        var target = Math.Clamp(double.IsFinite(roll) ? roll : 0d, 0d, 0.999999999d) * total;
        var last = -1;
        for (var i = 0; i < weights.Count; i++)
        {
            if (!Usable(weights[i]) || i == avoid) continue;
            last = i;
            target -= weights[i];
            if (target < 0d) return i;
        }
        return last;
    }

    public static int Pick<T>(IReadOnlyList<T> items, Func<T, float> weight, double roll, int avoid = -1)
    {
        var weights = new float[items.Count];
        for (var i = 0; i < items.Count; i++) weights[i] = weight(items[i]);
        return Pick(weights, roll, avoid);
    }

    private static bool Usable(float weight) => float.IsFinite(weight) && weight > 0f;
}
