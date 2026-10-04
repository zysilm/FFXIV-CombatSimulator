// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using System.Text;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    /// <summary>
    /// On-demand inventory/shape inspection for controller.Describe(), under its existing runtime gate.
    /// Never called during Advance/AppendGeometry and never skins or queries native terrain.
    /// SI model values are formatted as ml, mm and mm² for reading near-face film scales.
    /// </summary>
    public string InventoryDiagnostics
    {
        get
        {
            double filmVolume = 0, beadVolume = 0, threadVolume = 0, dropVolume = 0, groundVolume = 0;
            double peakThickness = 0, wetArea = 0, sourceThickness = 0, sourceArea = 0, breakVolume = 0, terminalVolume = 0;
            var filmCount = 0; var beadCount = 0; var threadCount = 0; var dropCount = 0; var groundCount = 0; var pendingCount = 0;
            var sourceTriangle = -1; var sourceNormalY = 0f;
            var hasSource = surface.TryGetMouthAnchor(out var source);
            foreach (var film in films)
            {
                filmVolume += film.Volume;
                if (film.Volume > 0) filmCount++;
                var inspection = film.Inspect();
                peakThickness = Math.Max(peakThickness, inspection.PeakThickness);
                wetArea += inspection.WetArea;
                if (hasSource && film.TryGetCell(source, out var sourceCell))
                {
                    var cell = film.GetCell(sourceCell);
                    sourceThickness = cell.Thickness; sourceArea = cell.Geometry.Area;
                    sourceTriangle = source.Triangle; sourceNormalY = cell.Geometry.Normal.Y;
                }
            }
            foreach (var bead in beads) { beadVolume += bead.Volume; if (bead.Volume > 0) beadCount++; }
            foreach (var drop in drops) { dropVolume += drop.Volume; if (drop.Volume > 0) dropCount++; }
            groundVolume = GroundVolume; groundCount = GroundCellCount;
            var details = new StringBuilder(768);
            var runoffCount = 0; var blockedCaps = 0;
            for (var i = 0; i < beads.Length; i++)
            {
                var bead = beads[i];
                if (bead.Volume <= 0) continue;
                if (bead.RunoffDistance > 0) runoffCount++;
                if (bead.WalkBlocked) blockedCaps++;
                var normalY = surface.TryEvaluate(bead.Anchor, out var pose) ? pose.Normal.Y : float.NaN;
                details.Append($" cap[{i}]:source={i == sourceBead},V={bead.Volume * 1e6:F5}ml,tri={bead.Anchor.Triangle},normalY={normalY:F3}," +
                    $"runoff={bead.RunoffDistance * 1e3:F3}mm,walkBlocked={bead.WalkBlocked},threadReserved={BeadSlotReserved(i)};");
            }
            for (var i = 0; i < threads.Length; i++)
            {
                var thread = threads[i];
                threadVolume += thread.Model.TotalVolume;
                if (thread.Model.TotalVolume <= 0) continue;
                threadCount++;
                if (thread.PendingContact) pendingCount++;
                terminalVolume += thread.Model.TerminalVolume;
                double peakRadius = 0, proposalDistance = 0, length = 0, minimumLength = double.PositiveInfinity;
                float maximumSpeed = 0, worldResolution = 0;
                for (var segment = 0; segment < thread.Model.SegmentCount; segment++)
                {
                    var sample = thread.Model.GetSegment(segment);
                    breakVolume += sample.PendingBreakVolume;
                    peakRadius = Math.Max(peakRadius, sample.Radius);
                    if (sample.Volume > 0)
                    {
                        var segmentLength = Vector3.Distance(sample.A, sample.B);
                        length += segmentLength; minimumLength = Math.Min(minimumLength, segmentLength);
                    }
                }
                for (var node = 0; node < thread.Model.NodeCount; node++)
                {
                    maximumSpeed = Math.Max(maximumSpeed, thread.Model.GetNodeVelocity(node).Length());
                    var p = thread.Model.GetNodePosition(node);
                    worldResolution = Math.Max(worldResolution, Math.Max(MathF.Abs(MathF.BitIncrement(p.X) - p.X),
                        Math.Max(MathF.Abs(MathF.BitIncrement(p.Y) - p.Y), MathF.Abs(MathF.BitIncrement(p.Z) - p.Z))));
                }
                if (thread.PendingContact)
                    for (var node = 0; node < thread.Model.NodeCount; node++)
                        proposalDistance = Math.Max(proposalDistance, Vector3.Distance(thread.Previous[node], thread.Model.GetNodePosition(node)));
                details.Append($" thread[{i}]:V={thread.Model.TotalVolume * 1e6:F5}ml,pending={thread.PendingContact},segment={thread.ContactSegment}/{thread.Model.SegmentCount}," +
                    $"storedV={thread.Model.TotalVolume:E16}m3,nodes={thread.Model.NodeCount},coarsened={thread.Model.CoarsenedSegments},coarsenedV={thread.Model.CoarsenedVolume * 1e6:F6}ml,remeshMomentumError={thread.Model.LastRemeshMomentumError:E3}kgm/s," +
                    $"anchor={thread.Attached},tip={thread.TipAttached},sourceConnected={thread.Model.TerminalConnectedToSource},rMax={peakRadius * 1e3:F3}mm,L={length * 1e3:F3}mm," +
                    $"terminalV={thread.Model.TerminalVolume * 1e6:F5}ml,terminalR={Radius(thread.Model.TerminalVolume) * 1e3:F3}mm,proposalMove={proposalDistance * 1e3:F3}mm,solverDeferred={thread.Deferred:F5}s," +
                    $"lastRequested={thread.Model.LastRequestedSeconds:F5}s,lastSimulated={thread.Model.LastSimulatedSeconds:F5}s,substeps={thread.Model.LastSubsteps}," +
                    $"requestedAge={thread.RequestedSeconds:F4}s,simulatedAge={thread.SimulatedSeconds:F4}s,acceptedProposals={thread.AcceptedProposals},pendingSteps={thread.PendingSteps}," +
                    $"solverNoProgress={thread.SolverNoProgress},solverStop='{thread.Model.LastStopReason}'," +
                    $"strainRejects={thread.Model.LastStrainRejections},rejectedSegment={thread.Model.LastRejectedSegment}," +
                    $"rejectedBefore={thread.Model.LastRejectedLengthBefore * 1e3:F6}mm,rejectedAfter={thread.Model.LastRejectedLengthAfter * 1e3:F6}mm," +
                    $"rejectedDt={thread.Model.LastRejectedTrialSeconds:E6}s,rejectedReason='{thread.Model.LastStrainRejectionReason}'," +
                    $"minSegment={(double.IsFinite(minimumLength) ? minimumLength * 1e3 : 0):F6}mm,maxSpeed={maximumSpeed:F5}m/s,worldUlp={worldResolution * 1e3:F6}mm;");
            }
            var averageThickness = wetArea > 0 ? filmVolume / wetArea : 0;
            var capVolume = sourceBead >= 0 ? beads[sourceBead].Volume : 0;
            BeadDimensions(capVolume, out _, out var capHeight, out var capFootprint);
            var capLoad = Density * capVolume * Math.Max(0, -9.81 * sourceNormalY);
            var capRetention = SurfaceTension * Math.PI * capFootprint * 2 * 0.15;
            var tangentLoad = Density * capVolume * 9.81 * Math.Sqrt(Math.Max(0, 1 - sourceNormalY * sourceNormalY));
            var tangentRetention = SurfaceTension * capFootprint * 2 * 0.025;
            details.Insert(0,
                $"inventory:reservoir={reservoir * 1e6:F5}ml,film={filmVolume * 1e6:F5}ml/{filmCount},bead={beadVolume * 1e6:F5}ml/{beadCount}," +
                $"thread={threadVolume * 1e6:F5}ml/{threadCount},terminalSubset={terminalVolume * 1e6:F5}ml,breakPending={breakVolume * 1e6:F5}ml,drop={dropVolume * 1e6:F5}ml/{dropCount},ground={groundVolume * 1e6:F5}ml/{groundCount}; " +
                $"sourceCap:V={capVolume * 1e6:F5}ml,h={capHeight * 1e3:F3}mm,footprintR={capFootprint * 1e3:F3}mm,outward={capLoad * 1e6:F2}uN,retention={capRetention * 1e6:F2}uN,acceptedSourceAge={acceptedSourceSeconds:F4}s; " +
                $"runoff:sourceTangent={tangentLoad * 1e6:F2}uN,tangentRetention={tangentRetention * 1e6:F2}uN,caps={runoffCount},blocked={blockedCaps},transitions={runoffTransitions},coalescedTransfer={coalescedVolume * 1e6:F5}ml; " +
                $"coat:thickness=.025mm,actualCoatVolume={actualCoatVolume * 1e6:F5}ml,wettingTrailTransfer={wettingTrailTransfer * 1e6:F5}ml; " +
                $"film:sourceTri={sourceTriangle},sourceH={sourceThickness * 1e3:F4}mm,sourceA={sourceArea * 1e6:F3}mm²,normalY={sourceNormalY:F3}," +
                $"peakH={peakThickness * 1e3:F4}mm,wetA={wetArea * 1e6:F3}mm²,meanH={averageThickness * 1e3:F4}mm,secondaryPoolThreshold=.800mm; " +
                $"contacts:threadsPending={pendingCount},surfaceCallsLeft={surfaceBudget}/40,terrainCallsLeft={terrainBudget}/32," +
                $"triangleTests={surface.TriangleTestsThisFrame}/4096,budgetHit={surface.BudgetExhausted};");
            for (int i = 0; i < grounds.Length; i++)
                if (grounds[i] != null) details.Append($" ground[{i}]:{grounds[i]!.Inspect()};");
            return details.ToString();
        }
    }
}
