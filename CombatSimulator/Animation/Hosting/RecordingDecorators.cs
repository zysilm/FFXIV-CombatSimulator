// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CombatSimulator.Animation.Recording;

namespace CombatSimulator.Animation.Hosting;

// The recorder (stage A0) as decorators around the host interfaces it can observe from outside
// (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §8): each passes the call through and, while the controller is
// recording, writes the same record the controller used to write beside the call. The record layouts are
// unchanged (format version 2). The skeleton captures, the frames, the steps and the calls stay where they
// are in the controller: they are observation points of the simulation itself, not host answers.

/// <summary>Records world queries: Raycast, GroundTriangles, WaterSurface.</summary>
internal sealed class RecordingWorldQuery : IWorldQuery
{
    private readonly IWorldQuery inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingWorldQuery(IWorldQuery inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public bool Raycast(RaycastSite site, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        var result = inner.Raycast(site, origin, direction, maxDistance, out hit);
        var w = writer();
        // The live hair's samples are not part of a ragdoll activation.
        if (w == null || site == RaycastSite.AnimatedHair) return result;
        using (w.Begin(RecordType.Raycast))
        {
            w.I32((int)site);
            w.V3(origin);
            w.V3(direction);
            w.F32(maxDistance);
            w.Bool(result);
            w.V3(hit.Point);
            w.V3(hit.Normal);
            w.F32(hit.Distance);
            w.U64(hit.Material);
            w.V3(hit.V1);
            w.V3(hit.V2);
            w.V3(hit.V3);
        }
        return result;
    }

    public bool TryCollectGroundTriangles(Vector3 min, Vector3 max, List<Vector3> triangles, int maxTriangles)
    {
        var result = inner.TryCollectGroundTriangles(min, max, triangles, maxTriangles);
        var w = writer();
        if (w == null) return result;
        using (w.Begin(RecordType.GroundTriangles))
        {
            w.V3(min);
            w.V3(max);
            w.I32(maxTriangles);
            w.Bool(result);
            w.V3s(CollectionsMarshal.AsSpan(triangles));
        }
        return result;
    }

    public bool TryFindWaterSurface(Vector3 position, float searchAbove, float searchBelow, out float surfaceY)
    {
        var result = inner.TryFindWaterSurface(position, searchAbove, searchBelow, out surfaceY);
        var w = writer();
        if (w == null) return result;
        using (w.Begin(RecordType.WaterSurface))
        {
            w.V3(position);
            w.F32(searchAbove);
            w.F32(searchBelow);
            w.Bool(result);
            w.F32(surfaceY);
        }
        return result;
    }
}

/// <summary>Records every number drawn from the unseeded random source (Random records, by site name).</summary>
internal sealed class RecordingRandom : IRagdollRandom
{
    private readonly IRagdollRandom inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingRandom(IRagdollRandom inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public double NextDouble(RandomSite site)
    {
        var value = inner.NextDouble(site);
        Write(site, value);
        return value;
    }

    public int Next(RandomSite site, int maxExclusive)
    {
        var value = inner.Next(site, maxExclusive);
        Write(site, value);
        return value;
    }

    private void Write(RandomSite site, double value)
    {
        var w = writer();
        if (w == null) return;
        using (w.Begin(RecordType.Random))
        {
            w.Str(site.ToString());
            w.F64(value);
        }
    }
}

/// <summary>Records the wall-clock stamps a replay needs (Clock records). The frame's dt is in the
/// RenderFrame record, and the mesh-frame early out is a Decision record, so only the walking-support
/// query stamp is written here.</summary>
internal sealed class RecordingClock : IRagdollClock
{
    private readonly IRagdollClock inner;
    private readonly Func<RagdollRecordingWriter?> writer;

    public RecordingClock(IRagdollClock inner, Func<RagdollRecordingWriter?> writer)
    {
        this.inner = inner;
        this.writer = writer;
    }

    public long Frequency => inner.Frequency;

    public long GetTimestamp(ClockSite site)
    {
        var ticks = inner.GetTimestamp(site);
        if (site != ClockSite.NpcTraversalQuery) return ticks;
        var w = writer();
        if (w == null) return ticks;
        using (w.Begin(RecordType.Clock))
        {
            w.Str(site.ToString());
            w.I64(ticks);
        }
        return ticks;
    }

    public TimeSpan GetElapsedTime(ClockSite site, long since) => inner.GetElapsedTime(site, since);
}
