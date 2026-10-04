// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private const int RetainedRivuletCapacity = 32;
    private readonly RetainedRivulet?[] retainedRivulets = new RetainedRivulet?[RetainedRivuletCapacity];
    private long retainedSequence;
    private int retainedRenderOrder;
    public int RetainedRivuletsDrawn { get; private set; }
    public long RetainedRivuletsRecycled { get; private set; }

    private sealed class RetainedRivulet
    {
        public readonly RivuletPath Path = new();
        public int Outlet, SourceIndex;
        public long SourceCreated, Created;
        public double Volume;
    }

    public double RetainedRivuletVolume
    {
        get { double total = 0; foreach (var trail in retainedRivulets) if (trail != null) total += trail.Volume; return total; }
    }

    // Residual pinned wetting is a separate inventory, not a duplicate rendering
    // of liquid that has already fallen. No lifetime timer drains these traces.
    private void PreserveRivulet(int index)
    {
        var path = rivulets[index];
        if (path == null || path.Count < 2) return;
        ref var bead = ref beads[index];
        int slot = path.RetainedSlot;
        RetainedRivulet? trail = (uint)slot < retainedRivulets.Length ? retainedRivulets[slot] : null;
        if (trail == null || trail.SourceIndex != index || trail.SourceCreated != bead.Created || trail.Volume <= 0)
        {
            if (bead.Volume <= 0) return;
            slot = -1;
            for (int i = 0; i < retainedRivulets.Length; i++)
            {
                var candidate = retainedRivulets[i];
                if (candidate == null || candidate.Volume <= 0) { slot = i; break; }
                if (slot < 0 || candidate.Created < retainedRivulets[slot]!.Created) slot = i;
            }
            trail = retainedRivulets[slot] ??= new();
            if (trail.Volume > 0) { RetiredVolume += trail.Volume; RetainedRivuletsRecycled++; }
            trail.Path.Reset();
            trail.Volume = bead.Volume * .10;
            bead.Volume -= trail.Volume;
            trail.SourceIndex = index; trail.SourceCreated = bead.Created;
            trail.Outlet = bead.Outlet; trail.Created = ++retainedSequence;
            path.RetainedSlot = slot;
        }
        CopyRivuletPath(path, trail.Path);
    }

    private static void CopyRivuletPath(RivuletPath source, RivuletPath target)
    {
        target.Count = source.Count;
        Array.Copy(source.Centers, target.Centers, source.Count);
        Array.Copy(source.Left, target.Left, source.Count);
        Array.Copy(source.Right, target.Right, source.Count);
        Array.Copy(source.Samples, target.Samples, source.Count * RivuletPath.Columns);
    }

    private void AppendRetainedRivulets(FluidGeometryBuilder builder)
    {
        RetainedRivuletsDrawn = 0;
        int first = retainedRenderOrder;
        retainedRenderOrder = (retainedRenderOrder + 1) % retainedRivulets.Length;
        for (int step = 0; step < retainedRivulets.Length; step++)
        {
            var trail = retainedRivulets[(first + step) % retainedRivulets.Length];
            if (trail == null || trail.Volume <= 0 || trail.Path.Centers[0].Generation != surface.Generation) continue;
            builder.Group = (byte)trail.Outlet;
            int start = builder.Count;
            // Temporary pose/query failure hides only this frame; the wet owner
            // and its material anchors survive and can draw again on the next.
            if (!AppendRivuletSurface(trail.Path, trail.Volume, builder, trail.Outlet)) continue;
            float scale = VisualScale(trail.Outlet);
            builder.ExaggerateForVisibilityPreview(scale, 0, scale, start);
            RetainedRivuletsDrawn++;
        }
    }

    private void ClearRetainedRivulets(bool retire)
    {
        if (retire) RetiredVolume += RetainedRivuletVolume;
        foreach (var trail in retainedRivulets)
            if (trail != null) { trail.Volume = 0; trail.Path.Reset(); }
        RetainedRivuletsDrawn = 0;
        retainedRenderOrder = 0;
    }
}
