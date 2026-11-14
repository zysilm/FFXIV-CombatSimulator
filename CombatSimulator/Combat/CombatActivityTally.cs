// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Combat;

/// <summary>
/// What a side did, counted per frame and written to the log every 5 s (the <c>Enemies (…)</c> and
/// <c>Allies (…)</c> lines). Counting decides nothing.
/// </summary>
public sealed class CombatActivityTally
{
    private readonly string[] names;
    private readonly long[] frames;
    private readonly bool listUnseen;
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <param name="names">The activities, in the order they are written.</param>
    /// <param name="listUnseen">Write activities no frame had (as 0 %), or leave them out.</param>
    public CombatActivityTally(string[] names, bool listUnseen)
    {
        this.names = names;
        frames = new long[names.Length];
        this.listUnseen = listUnseen;
    }

    public void Note(int activity) => frames[activity]++;

    /// <summary>Starts a new window (a fight starts or is reset).</summary>
    public void Restart()
    {
        Array.Clear(frames);
        clock.Restart();
    }

    /// <summary>
    /// Every 5 s: the shares of the window ("in reach 40%, walking in 60%"), the counts cleared; null when
    /// the window is not over, or no frame was counted (the counts then carry on).
    /// </summary>
    public string? TakeShares()
    {
        if (clock.Elapsed.TotalSeconds < 5)
            return null;
        clock.Restart();
        long total = 0;
        foreach (var n in frames) total += n;
        if (total == 0)
            return null;
        var parts = new List<string>();
        for (var i = 0; i < frames.Length; i++)
            if (listUnseen || frames[i] > 0)
                parts.Add($"{names[i]} {100.0 * frames[i] / total:0}%");
        Array.Clear(frames);
        return string.Join(", ", parts);
    }
}
