// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private const int CapChartWorkBudget = 8192;
    private readonly CurvedCapGeometryCache?[] curvedCapGeometry = new CurvedCapGeometryCache?[16];
    private int capGeometryOrder;
    public int CapGeometryVerifiedCount { get; private set; }
    public int CapGeometryPendingCount { get; private set; }
    public int CapGeometryHiddenCount { get; private set; }
    public int CapGeometryWorkThisFrame { get; private set; }
    public int CapGeometrySupportedCount { get; private set; }
    public int CapGeometryTrianglesThisFrame { get; private set; }
    public string CapGeometryPendingReason { get; private set; } = string.Empty;
    public string CapGeometryDiagnostics => $"curvedCaps:verified={CapGeometryVerifiedCount},pending={CapGeometryPendingCount},hidden={CapGeometryHiddenCount}," +
        $"supportedClipped={CapGeometrySupportedCount},triangles={CapGeometryTrianglesThisFrame},managedChartWork={CapGeometryWorkThisFrame}/{CapChartWorkBudget},pendingReason='{CapGeometryPendingReason}'; " +
        "skinLift=.080mm; discrete prism volume normalization; supported clip is reduced pinned footprint, not exact Young-Laplace";

    /// <summary>Append continuous film, bounded thread and curved drop/puddle surfaces to the producer's builder.</summary>
    public void AppendGeometry(FluidGeometryBuilder builder)
    {
        if (generation != surface.Generation) return;
        foreach (var film in films) film.AppendGeometry(builder);
        foreach (var thread in threads)
        {
            if (thread.Model.TotalVolume <= 0) continue;
            if (thread.Model.TerminalVolume > 0)
            {
                // True reservoir radius; the neck's end cap slightly overlaps the
                // sphere pole. This is a reduced junction, not extra visual inventory.
                var center = thread.PendingContact ? thread.PreviousTerminalCenter : thread.Model.TerminalCenter;
                builder.AddEllipsoid(center, new Vector3((float)Radius(thread.Model.TerminalVolume)), 24, 12);
            }
            for (var i = 0; i < thread.Model.SegmentCount; i++)
            {
                var neck = thread.Model.GetSegment(i);
                if (neck.PendingBreakVolume <= 0) continue;
                var center = thread.PendingContact ? (thread.Previous[i] + thread.Previous[i + 1]) * 0.5f : (neck.A + neck.B) * 0.5f;
                builder.AddEllipsoid(center, new Vector3((float)Radius(neck.PendingBreakVolume)), 12, 6);
            }
            var segment = 0;
            while (segment < thread.Model.SegmentCount)
            {
                while (segment < thread.Model.SegmentCount && thread.Model.GetSegment(segment).Volume <= 0) segment++;
                if (segment == thread.Model.SegmentCount) break;
                var first = segment;
                double inventory = 0;
                while (segment < thread.Model.SegmentCount && thread.Model.GetSegment(segment).Volume > 0)
                { inventory += thread.Model.GetSegment(segment).Volume; segment++; }
                var count = segment - first + 1;
                for (var node = 0; node < count; node++)
                {
                    var index = first + node;
                    thread.RenderPositions[node] = thread.PendingContact ? thread.Previous[index] : thread.Model.GetNodePosition(index);
                    var left = thread.Model.GetSegment(Math.Max(first, index - 1)).Radius;
                    var right = thread.Model.GetSegment(Math.Min(segment - 1, index)).Radius;
                    thread.RenderRadii[node] = (float)Math.Max(1e-6, (left + right) * 0.5);
                }
                double reconstructed = 0;
                for (var node = 0; node < count - 1; node++)
                {
                    var a = thread.RenderRadii[node]; var b = thread.RenderRadii[node + 1];
                    reconstructed += Math.PI * Vector3.Distance(thread.RenderPositions[node], thread.RenderPositions[node + 1]) *
                        (a * a + a * b + b * b) / 3;
                }
                // Include the two hemispherical caps when normalizing the visual radius.
                // The centerline's Hermite interpolation remains a bounded rendering approximation.
                reconstructed += 2 * Math.PI / 3 * (Math.Pow(thread.RenderRadii[0], 3) + Math.Pow(thread.RenderRadii[count - 1], 3));
                var correction = reconstructed > 0 ? Math.Sqrt(inventory / reconstructed) : 1;
                for (var node = 0; node < count; node++) thread.RenderRadii[node] *= (float)correction;
                builder.AddThread(thread.RenderPositions.AsSpan(0, count), thread.RenderRadii.AsSpan(0, count), 12, 1);
            }
        }
        int capWork = CapChartWorkBudget;
        CapGeometryVerifiedCount = CapGeometryPendingCount = CapGeometryHiddenCount = 0;
        CapGeometrySupportedCount = CapGeometryTrianglesThisFrame = 0;
        CapGeometryPendingReason = string.Empty;
        // A cold or expanding cap cannot starve the later slots indefinitely.
        int firstCap = capGeometryOrder++ % beads.Length;
        if (capGeometryOrder == int.MaxValue) capGeometryOrder = 0;
        for (int step = 0; step < beads.Length; step++)
        {
            int index = (firstCap + step) % beads.Length;
            var bead = beads[index];
            if (bead.Volume <= 0) { curvedCapGeometry[index]?.Invalidate(); continue; }
            var cache = curvedCapGeometry[index] ??= new CurvedCapGeometryCache();
            bool ready = false;
            if (bead.Film >= 0 && bead.Film < films.Length && surface.TryEvaluate(bead.Anchor, out var sample))
            {
                BeadDimensions(bead.Volume, out var radius, out _, out _);
                ready = cache.Prepare(films[bead.Film], bead.Anchor, sample, radius, bead.Volume, ref capWork);
            }
            else cache.MarkUnavailable();
            if (ready)
            {
                cache.Append(builder); CapGeometryVerifiedCount++;
                if (cache.UsesClippedSupport) CapGeometrySupportedCount++;
                CapGeometryTrianglesThisFrame += cache.RenderedTriangles;
            }
            else CapGeometryHiddenCount++;
            if (cache.Pending || !ready)
            {
                CapGeometryPendingCount++;
                if (CapGeometryPendingReason.Length == 0)
                    CapGeometryPendingReason = $"cap[{index}]:{(cache.Pending ? cache.Status : "Current anchor/patch unavailable")}";
            }
        }
        CapGeometryWorkThisFrame = CapChartWorkBudget - capWork;
        foreach (var drop in drops)
        {
            if (drop.Volume <= 0) continue;
            builder.AddEllipsoid(drop.Position, new Vector3((float)Radius(drop.Volume)), 12, 6);
        }
        ground.AppendGeometry(builder);
    }

}
