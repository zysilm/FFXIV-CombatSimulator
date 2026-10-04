// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private const int CapChartWorkBudget = 8192;
    private readonly CurvedCapGeometryCache?[] curvedCapGeometry = new CurvedCapGeometryCache?[BeadCapacity];
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
        float scale = LargeVisibilityPreview ? 1 : config.BodyFluidThicknessScale;
        int filmStart = builder.Count;
        // Detached material belongs to world space, not the lip's current topology.
        if (generation == surface.Generation)
            foreach (var film in films) film.AppendGeometry(builder);
        builder.ExaggerateForVisibilityPreview(scale, 0, scale, filmStart);
        foreach (var thread in threads)
        {
            if (thread.Model.TotalVolume <= 0) continue;
            if (thread.Model.TerminalVolume > 0 && thread.Model.GetSegment(thread.Model.SegmentCount - 1).Volume <= 0)
            {
                // True reservoir radius; the neck's end cap slightly overlaps the
                // sphere pole. This is a reduced junction, not extra visual inventory.
                var center = thread.PendingContact ? thread.PreviousTerminalCenter : thread.Model.TerminalCenter;
                AppendFreeDrop(builder, center, thread.Model.TerminalVelocity, thread.Model.TerminalVolume);
            }
            for (var i = 0; i < thread.Model.SegmentCount; i++)
            {
                var neck = thread.Model.GetSegment(i);
                if (neck.PendingBreakVolume <= 0) continue;
                var center = thread.PendingContact ? (thread.Previous[i] + thread.Previous[i + 1]) * 0.5f : (neck.A + neck.B) * 0.5f;
                builder.AddEllipsoid(center, new Vector3((float)Radius(neck.PendingBreakVolume) * scale), 12, 6);
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
                if (segment == thread.Model.SegmentCount && thread.Model.TerminalVolume > 0)
                {
                    // Reconstruct neck and loaded pendant as one smooth free surface.
                    // It is the same owned terminal volume, not an overlapping sphere.
                    var direction = thread.RenderPositions[count - 1] - thread.RenderPositions[count - 2];
                    direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : -Vector3.UnitY;
                    var center = thread.PendingContact ? thread.PreviousTerminalCenter : thread.Model.TerminalCenter;
                    var radius = (float)Radius(thread.Model.TerminalVolume);
                    thread.RenderPositions[count] = center - direction * (radius * .3f);
                    thread.RenderRadii[count++] = radius * .9f;
                    thread.RenderPositions[count] = center + direction * (radius * .3f);
                    thread.RenderRadii[count++] = radius * .9f;
                    thread.RenderPositions[count] = center + direction * (radius * .85f);
                    thread.RenderRadii[count++] = radius * .35f;
                    inventory += thread.Model.TerminalVolume;
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
                for (var node = 0; node < count; node++) thread.RenderRadii[node] *= (float)correction * scale;
                builder.AddThread(thread.RenderPositions.AsSpan(0, count), thread.RenderRadii.AsSpan(0, count), 16, 2);
            }
        }
        int capWork = CapChartWorkBudget;
        RivuletsDrawn = 0;
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
            int capStart = builder.Count;
            if (AppendRivulet(index, builder))
            {
                builder.ExaggerateForVisibilityPreview(scale, 0, scale, capStart);
                RivuletsDrawn++; continue;
            }
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
                builder.ExaggerateForVisibilityPreview(scale, 0, scale, capStart);
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
            AppendFreeDrop(builder, drop.Position, drop.Velocity, drop.Volume);
        }
        int groundStart = builder.Count;
        ground.AppendGeometry(builder);
        builder.ExaggerateForVisibilityPreview(scale, 0, scale, groundStart);
    }

    private void AppendFreeDrop(FluidGeometryBuilder builder, Vector3 center, Vector3 velocity, double volume)
    {
        float radius = (float)Radius(volume) * (LargeVisibilityPreview ? 1 : config.BodyFluidThicknessScale);
        float speed = velocity.Length();
        var direction = speed > .01f ? velocity / speed : -Vector3.UnitY;
        float stretch = 1 + Math.Clamp(speed * .18f, 0, .4f);
        float radial = radius / MathF.Sqrt(stretch);
        float dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, direction), -1, 1);
        var axis = Vector3.Cross(Vector3.UnitY, direction);
        var rotation = dot < -.9999f ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) :
            axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
        builder.AddEllipsoid(center, new Vector3(radial, radius * stretch, radial), 24, 12, rotation);
    }

}
