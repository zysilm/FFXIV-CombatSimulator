// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Simulation;

namespace CombatSimulator.ActionCombat;

/// <summary>One drawn enemy attack area (see <see cref="EnemyAreaMarkers"/>).</summary>
public sealed class EnemyAreaMarker
{
    public int Id;
    public uint SourceSimId;
    public uint ActionId;
    public TelegraphShape Shape;
    public float Elapsed;
    public float Duration;
    /// <summary>Optional liveness check (e.g. "still casting this action"); false removes the marker.</summary>
    public Func<bool>? StillValid;

    public float Progress => Duration <= 0f ? 1f : Math.Clamp(Elapsed / Duration, 0f, 1f);
}

/// <summary>
/// The enemy attack areas currently on the ground, independent of which path committed them:
/// Action-Mode telegraphs and ordinary cast bars both register here, and the overlay draws from
/// this one list. Every marker has a hard lifetime (duration plus a grace) so a missed removal
/// can never leave a danger zone on the floor.
/// </summary>
public sealed class EnemyAreaMarkers
{
    private const float ExpiryGrace = 1.0f;

    private readonly List<EnemyAreaMarker> markers = new();
    private int nextId = 1;

    public IReadOnlyList<EnemyAreaMarker> Active => markers;

    /// <summary>Raised when a marker leaves the list (resolve, cancel, expiry or clear).</summary>
    public event Action<EnemyAreaMarker>? Removing;

    public int Add(uint sourceSimId, uint actionId, in TelegraphShape shape, float duration, Func<bool>? stillValid = null)
    {
        var marker = new EnemyAreaMarker
        {
            Id = nextId++,
            SourceSimId = sourceSimId,
            ActionId = actionId,
            Shape = shape,
            Duration = MathF.Max(0.01f, duration),
            StillValid = stillValid,
        };
        markers.Add(marker);
        Added?.Invoke(marker);
        return marker.Id;
    }

    /// <summary>Raised after a marker is added.</summary>
    public event Action<EnemyAreaMarker>? Added;

    public void Remove(int id)
    {
        if (id == 0)
            return;
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            if (markers[i].Id != id)
                continue;
            RemoveAt(i);
            return;
        }
    }

    public void RemoveForSource(uint sourceSimId)
    {
        for (var i = markers.Count - 1; i >= 0; i--)
            if (markers[i].SourceSimId == sourceSimId)
                RemoveAt(i);
    }

    public void Tick(float dt)
    {
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var m = markers[i];
            m.Elapsed += dt;
            var invalid = false;
            try { invalid = m.StillValid != null && !m.StillValid(); }
            catch { invalid = true; }
            if (invalid || m.Elapsed > m.Duration + ExpiryGrace)
                RemoveAt(i);
        }
    }

    public void Clear()
    {
        for (var i = markers.Count - 1; i >= 0; i--)
            RemoveAt(i);
    }

    private void RemoveAt(int index)
    {
        var m = markers[index];
        markers.RemoveAt(index);
        Removing?.Invoke(m);
    }
}
