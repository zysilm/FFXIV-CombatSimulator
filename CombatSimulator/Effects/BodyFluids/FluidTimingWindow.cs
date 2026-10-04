// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>Bounded timing samples; sorting/formatting happens only on explicit status requests.</summary>
internal sealed class FluidTimingWindow
{
    private readonly double[] samples = new double[256];
    private int next, count;
    private double maximum;

    internal void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        samples[next] = milliseconds;
        next = (next + 1) % samples.Length;
        count = Math.Min(count + 1, samples.Length);
        maximum = Math.Max(maximum, milliseconds);
    }

    internal void Clear()
    {
        next = count = 0;
        maximum = 0;
        Array.Clear(samples);
    }

    internal string Describe()
    {
        if (count == 0) return "no samples";
        var ordered = samples.AsSpan(0, count).ToArray();
        Array.Sort(ordered);
        return $"n={count},p50={ordered[(count - 1) / 2]:F3}," +
            $"p95={ordered[(int)Math.Ceiling(count * .95) - 1]:F3}," +
            $"p99={ordered[(int)Math.Ceiling(count * .99) - 1]:F3},max={maximum:F3} ms";
    }
}
