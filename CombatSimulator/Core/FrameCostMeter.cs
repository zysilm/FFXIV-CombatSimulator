// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CombatSimulator.Core;

/// <summary>
/// Time spent per frame by each of the plugin's per-frame handlers (render hooks, framework
/// update), summed by handler name and read out for the log summary. Main thread only.
/// </summary>
public static class FrameCostMeter
{
    private static readonly Dictionary<string, long> ticksByName = new();
    private static readonly Dictionary<Delegate, string> names = new();

    /// <summary>Run each handler of a per-frame event in turn, timing each one.</summary>
    public static void InvokeTimed(Action? handlers)
    {
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                ((Action)handler)();
            }
            finally
            {
                Add(NameOf(handler), Stopwatch.GetTimestamp() - start);
            }
        }
    }

    public static void Add(string name, long ticks)
        => ticksByName[name] = ticksByName.GetValueOrDefault(name) + ticks;

    private static string NameOf(Delegate handler)
    {
        if (names.TryGetValue(handler, out var name))
            return name;
        name = $"{handler.Method.DeclaringType?.Name ?? "?"}.{handler.Method.Name}";
        if (names.Count < 512)
            names[handler] = name;
        return name;
    }

    /// <summary>The costliest handlers since the last call, as milliseconds per game frame; then reset.</summary>
    public static string TakeTop(int count, int frames)
    {
        frames = Math.Max(1, frames);
        var text = string.Join(", ", ticksByName
            .OrderByDescending(kv => kv.Value)
            .Take(count)
            .Select(kv => $"{kv.Key} {kv.Value * 1000.0 / Stopwatch.Frequency / frames:F2}"));
        ticksByName.Clear();
        names.Clear();
        return text;
    }
}
